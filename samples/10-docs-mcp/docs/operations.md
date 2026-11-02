# Operating the sample

[Quickstart and guided tour](../README.md) · [Inside the application](architecture.md)

Run shell commands from `samples/10-docs-mcp`. A native server and its local clients must use the same operating-system user and `DOCS_PIPE`. Multiple servers need distinct pipes, ports and index directories.

## Bring your own documents

For a first experiment, copy Markdown files into the sample's `documents` directory. The worker detects additions, edits and removals on its next scan. `index rebuild` requests a full rebuild immediately. Fetch reads the published index; changing a source file does not change fetch results until publication.

To use another folder natively, configure the server before starting it. Keep the index outside the document tree.

PowerShell:

```powershell
$env:DOCS_DOCUMENTS = 'C:/work/project-docs'
$env:DOCS_INDEX = 'C:/work/project-docs-index'
dotnet run -- serve
```

Bash/zsh:

```sh
DOCS_DOCUMENTS="$HOME/work/project-docs" \
DOCS_INDEX="$HOME/work/project-docs-index" \
dotnet run -- serve
```

These directories must exist or be creatable as appropriate: the document directory must exist, and the server creates the index directory. Markdown extensions are matched without case sensitivity. Symbolic links are skipped; use the source directory directly rather than a linked root. Each file is limited to 8 MiB.

For Docker, edit the source side of `./documents:/documents:ro` in [compose.yaml](../compose.yaml). Keep the target `/documents` and the read-only flag. Use a folder that Docker Desktop is allowed to mount. The server only reads this mount; rebuilding writes to the separate index volume.

### What persists

| Location | Native default | Container | Role |
| --- | --- | --- | --- |
| Sources | `documents` | Read-only `/documents` mount | Your Markdown corpus |
| Index | `.index` | Named volume at `/index` | SQLite database, WAL and temporary rebuild storage |
| Assets | `.assets` | `/assets`, included in image | Model, vocabulary, native extension and browser dependencies |

Use local storage. SQLite WAL is not intended here for a network filesystem or multiple processes writing the same index. A bind mount used instead of the named index volume must be writable by the container's non-root application user. Allow space for both the published index and its staging replacement.

Normal completion, cancellation and handled failures remove their temporary `build-<guid>.db` files. A forced termination or power loss can leave them behind; repeated interrupted rebuilds can consume disk space. With the server fully stopped, remove only files matching `build-*.db` from the configured index directory. For a Docker volume, stop its owning container before inspecting that volume with your volume-management tool. Preserve `documents.db`, `documents.db-wal` and `documents.db-shm`; never remove staging files while a rebuild might still be using them. Restart the server afterward; the published generation remains authoritative.

## Health and persistence

The local health client invokes the running process through its pipe. It is not merely checking whether an executable can start or a port can accept a connection.

| Check | Succeeds when | Useful for |
| --- | --- | --- |
| `health` | The host has started and is not stopping | Liveness; Docker's built-in check |
| `health --ready` | Liveness succeeds and a compatible generation is published | Readiness before sending searches |

Before the first index is published, liveness can succeed while readiness fails. A failed rebuild can remain ready on its previous index. Read `index status` to distinguish readiness from the outcome of the most recent rebuild. A successful check returns exit code zero; a failed check or connection failure returns a nonzero code.

For a native server, use `dotnet run -- health --ready` from another terminal. To observe container persistence:

```sh
docker exec docs-index /app/docs-index exec index status
docker compose restart
docker exec docs-index /app/docs-index health --ready
docker exec docs-index /app/docs-index exec index status
```

After the restart completes, the generation identifier should match if the corpus, model and schema are unchanged. A health call immediately during startup may fail; retry after startup. Detach console clients before restarting to avoid mistaking an expected disconnect for a server fault.

`docker compose down` removes the container and network but retains the named index volume. `docker compose down --volumes` also deletes that index volume; the next start must rebuild it from the source documents. The source folder is not deleted by either command.

## Configuration

[Program.cs](../Program.cs) builds a .NET `ConfigurationManager` before selecting a launcher command. It loads an optional `appsettings.json` from the current working directory, then environment variables with the `DOCS_` prefix. Environment values take priority; the provider strips that prefix, so `DOCS_MCP_PORT` supplies the configuration key `MCP_PORT`. Repl owns the command-line arguments; they are not also parsed as .NET configuration switches.

For example, create `appsettings.json` in the sample directory:

```json
{
  "DOCUMENTS": "../my-documents",
  "INDEX": ".my-index",
  "MCP_PORT": 9080,
  "ADMIN_PORT": 9081
}
```

Then run `dotnet run -- serve` as usual. Set `DOCS_MCP_PORT` to override just the public port without editing the file. The same file is read by local clients, so they can share a `PIPE` setting with the server. Inside a container, mount a file at `/app/appsettings.json` to use it there; Docker's `DOCS_*` environment values still take priority.

[Settings.cs](../Settings.cs) converts `IConfiguration` into the application's typed, immutable settings, using the defaults below for omitted keys. Paths are resolved relative to the process's current directory; absolute paths make scripted deployments less surprising. Port conversion reports malformed values before host construction. Server-side validation still checks the configured directories, addresses, origins and listener ports before opening the service.

Configuration is a startup snapshot: changing a JSON file or environment value requires restarting the affected server or client. Automatic reload and `IOptionsMonitor` are deliberately unnecessary here because the model, index and listeners are constructed from one consistent set of settings.

| Configuration key | Environment variable | Native default | Purpose |
| --- | --- | --- | --- |
| `DOCUMENTS` | `DOCS_DOCUMENTS` | `documents` | Markdown source directory |
| `INDEX` | `DOCS_INDEX` | `.index` | Writable index directory, outside the source tree |
| `ASSETS` | `DOCS_ASSETS` | `.assets` | Verified runtime assets |
| `PIPE` | `DOCS_PIPE` | `repl-docs-index` | Shared server/client pipe name |
| `MCP_ADDRESS` | `DOCS_MCP_ADDRESS` | `127.0.0.1` | MCP bind IP address |
| `MCP_PORT` | `DOCS_MCP_PORT` | `8080` | MCP HTTP listener port |
| `ADMIN_PORT` | `DOCS_ADMIN_PORT` | `8081` | Administration HTTP/WebSocket listener |
| `ADMIN_ADDRESS` | `DOCS_ADMIN_ADDRESS` | `127.0.0.1` | Admin bind IP address |
| `ADMIN_ORIGIN` | `DOCS_ADMIN_ORIGIN` | Unset | One additional exact browser origin |

The Dockerfile supplies container paths and binds both listeners to `0.0.0.0` **inside the container**. Compose publishes that port as `127.0.0.1:8081:8081` on the host. Do not change it to `8081:8081` on an untrusted host.

Browser administration accepts local HTTP origins `localhost`, `127.0.0.1` and `[::1]` on the configured admin port. For a proxy or another hostname, set `DOCS_ADMIN_ORIGIN` to one exact HTTP(S) origin without a path or trailing slash, such as `https://docs-admin.example.com`. This only adds an allowed origin; it does not configure a proxy, TLS or authentication.

Foreign and opaque (`null`) origins are rejected before admin HTTP actions or WebSocket acceptance. Native clients without an Origin header are allowed. This browser boundary is not authentication: a client can omit or forge headers.

## Assets and offline execution

[assets.json](../assets.json) is the manifest for immutable URLs and SHA256 checksums. `assets fetch` verifies cached files, downloads missing or mismatched ones, and extracts the matching native sqlite-vec extension. Server startup verifies the selected files, including the extracted extension, before using them.

The packaged native targets are Windows x64, Linux x64 and macOS Apple Silicon. Docker builds a Linux amd64 image, including when the host is Apple Silicon. macOS Intel is unsupported by the selected ONNX Runtime package. GPU execution and other native targets are outside this sample.

Asset acquisition requires network access. Serving after acquisition requires none: model inference is local, and xterm JavaScript/CSS are served from the application. Preserve `.assets` between native runs. For an offline deployment, fetch on a connected machine or build the container image first, then transfer the matching assets or image.

Each download has a ten-minute deadline covering response headers, the complete body and checksum verification. A timeout removes its partial `.download` file and preserves any existing asset. Run `assets fetch` again to retry; files that already passed verification are reused. Ctrl+C cancels acquisition rather than being reported as a download timeout.

The ONNX model and vocabulary are MIT-licensed; sqlite-vec is MIT/Apache-2.0; xterm and its fit addon are MIT-licensed. The manifest includes xterm license files. Consult the model card and the packages' licensing and notices when redistributing a deployment: [model](https://huggingface.co/intfloat/multilingual-e5-small), [sqlite-vec](https://github.com/asg017/sqlite-vec), [ONNX Runtime](https://github.com/microsoft/onnxruntime), [xterm.js](https://github.com/xtermjs/xterm.js).

## When something does not work

Start with the server's terminal or `docker compose logs`, then inspect `index status` through a connected console or one-shot client.

| Symptom | What to check |
| --- | --- |
| Missing or modified asset | Run `dotnet run -- assets fetch` with the same `DOCS_ASSETS`; server mode never repairs downloads automatically |
| Port already in use | Stop the other native/container instance, or assign distinct listener ports; Docker published ports must match your chosen deployment |
| Local client cannot connect | Server is running; both processes use the same user and pipe; a host pipe does not reach a pipe inside a container |
| Admin browser returns 403 | Use the loopback dashboard URL or configure the exact proxy origin; do not disable the boundary to make a foreign page work |
| First searches report not ready | Wait for initial publication; inspect progress and `LastError` in index status |
| Rebuild fails but searches still work | The previous generation is still serving; fix the reported source/storage failure and request another rebuild |
| Documents changed during indexing | Finish editing, then rebuild or allow the next scan; the incomplete replacement was not published |
| New file is absent from search | Verify source folder, Markdown extension and successful publication of a new generation |
| `index watch` rejected by one-shot client | Attach with `console`, or use `index status` for a finite snapshot |
| Fetch identifier no longer exists | Search again against the current generation |
| Future schema cannot be opened | No migrations are implemented; stop the server and recreate the disposable index from the source corpus |

Model identity and schema version prevent serving an incompatible generation. If the existing schema is still usable, the worker rebuilds automatically. If opening the database itself fails, the worker reports the failure and can retry; do not remove a live database beneath active readers.

A pipe connection has a five-second deadline. An absent server reports the pipe name and asks
you to start it (`dotnet run -- serve`); an explicit caller cancellation remains cancellation.
One-shot commands have a separate 30-second deadline after connection. Startup failures include
their exception diagnostics on stderr without changing the command's exit-code policy.
Malformed addresses and empty configured paths identify their configuration key instead of
silently selecting another location.

## Verify your changes

The [test project](../../../src/Repl.DocsSampleTests/) has pure chunking/ranking tests and opt-in native integration tests. After fetching assets, enable the complete sample suite:

PowerShell:

```powershell
$env:DOCS_TEST_ASSETS = (Resolve-Path .assets).Path
dotnet test --project ../../src/Repl.DocsSampleTests/Repl.DocsSampleTests.csproj
```

Bash/zsh:

```sh
DOCS_TEST_ASSETS="$PWD/.assets" \
dotnet test --project ../../src/Repl.DocsSampleTests/Repl.DocsSampleTests.csproj
```

Without that variable, native tests are skipped explicitly. With it, the suite exercises the real SQLite extension, model reference vectors, actual HTTP host and pipe, cancellation/retry, generation consistency and browser-origin boundaries. A tiny substituted embedding implementation keeps transport and worker tests independent of model behavior.

The [sample workflow](../../../.github/workflows/docs-sample.yml) fetches verified assets on each packaged native target. Its container job runs without network access, checks readiness, executes a search and verifies that a restart retains the generation. The [main CI](../../../.github/workflows/ci.yml) also checks the framework and existing samples.

## Deployment boundaries

This is a usable reference for modest corpora, with deliberate limits:

| Included | A deployment must decide or add |
| --- | --- |
| Separate public and admin listeners, restricted MCP graph | Authentication, authorization and TLS |
| Loopback-only admin publication in Compose, browser-origin checks | Network policy and access through trusted proxies |
| Bounded queries/fetches and per-file size limit | Corpus limits, concurrency limits, rate limits and quotas |
| Local CPU inference, exact cosine search | Capacity planning, larger-scale indexing/search if needed |
| Full rebuilds, atomic publication and a persistent volume | Backup/recovery policy, schema migrations and multi-writer coordination |

Search accepts up to 2,000 characters and 20 results; the model's 512-token input limit can reject a query before the character limit. Fetch returns up to 20,000 characters and reports truncation. Full rebuilds and exact vector search favor understandable code over scaling to a large collection.

Only index content you intend to make available to the MCP audience. Local inference keeps model computation on the host; it does not make a public search endpoint private. The unauthenticated administration port grants much broader access and must remain a trusted surface.

Native execution binds both listeners to `127.0.0.1` by default. Set `DOCS_MCP_ADDRESS`
explicitly to expose MCP beyond the local machine. The Dockerfile sets both listener addresses
to `0.0.0.0` inside its network namespace; published ports and firewall rules determine external access.

Before deployment, choose authentication and authorization for both audiences, protect static
administration assets as well as API routes, and configure TLS at the host or trusted proxy.
Add rate/concurrency limits for public inference, restrict document/index volume permissions,
and connect logging and metrics to your monitoring system. See the
[extension points](architecture.md#make-it-yours) for where these policies attach.
