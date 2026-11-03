# Container hosting and live administration

Embed Repl in a long-running service when operators need to inspect the running process, run a health probe or attach a console. These are advanced hosting features, useful for existing applications where Repl is one component. Remote access is explicitly enabled by the application; adding a package does not start an endpoint.

The [hosting sample](../samples/05-hosting-remote/) serves browser terminals and a local named-pipe console against the same in-memory settings and message bus. The application host owns its services and process signals. MCP is optional and is not used in this recipe.

For an application combining HTTP MCP, a persistent search index and separate administration, see [sample 10 — Documentation MCP Server](../samples/10-docs-mcp/). Its [operations guide](../samples/10-docs-mcp/docs/operations.md) covers the document and index volumes, live-process health probes and deployment boundaries. Administration is unauthenticated and must remain private.

## Start, probe and attach

Run these commands from the repository root:

```sh
docker build -f samples/05-hosting-remote/Dockerfile -t repl-hosting-remote .
docker run -d --name repl-hosting-remote -p 127.0.0.1:8080:8080 repl-hosting-remote
```

Open <http://localhost:8080> for the browser terminal. In another terminal, probe the live process without a TTY:

```sh
docker exec repl-hosting-remote dotnet /app/HostingRemoteSample.dll --remote-exec repl-hosting-sample health --liveness
```

Attach a native terminal:

```sh
docker exec -it repl-hosting-remote dotnet /app/HostingRemoteSample.dll --remote-console repl-hosting-sample
```

Run `settings set greeting hello`, then read `settings show greeting` from the browser. Both sessions use the same settings instance. Ctrl+C cancels the current command in that session. Ctrl+D detaches without stopping the server. See the [remote CLI guide](remote-cli.md) for client exit codes, interruption and connection ownership.

Console and exec modes branch before host construction. They connect to the existing server instead of initializing another application. Attach and probe commands invoke `dotnet` directly; neither requires `/bin/sh` or curl.

## Liveness and readiness

| Probe | Question | Maintenance enabled | Successful exit |
| --- | --- | --- | --- |
| `health --liveness` | Has the host started, and is it outside shutdown? | Still succeeds | `0` |
| `health` | Is the host started, outside shutdown and outside maintenance? | Fails | `0` |

An unsuccessful application probe returns `1`. A connection error or client cancellation can also fail the command; those do not establish that a handler ran. Docker's probe timeout bounds a stalled probe process.

The image uses **liveness** for its exec-form `HEALTHCHECK`. Maintenance therefore makes the sample unready while leaving the container healthy and its administration available. The sample has no external business dependencies; a real readiness check must include the dependencies needed to serve work.

Try this from another terminal while the service remains running:

```sh
docker exec repl-hosting-remote dotnet /app/HostingRemoteSample.dll --remote-exec repl-hosting-sample settings set maintenance on
docker exec repl-hosting-remote dotnet /app/HostingRemoteSample.dll --remote-exec repl-hosting-sample health
docker exec repl-hosting-remote dotnet /app/HostingRemoteSample.dll --remote-exec repl-hosting-sample health --liveness
docker exec repl-hosting-remote dotnet /app/HostingRemoteSample.dll --remote-exec repl-hosting-sample settings set maintenance off
```

The readiness call returns `notReady` and exit `1`; liveness returns `alive` and exit `0`. A subsequent readiness call returns `ready` and exit `0`. For an application rebuilding an index or warming a cache, use that application's real state instead of the sample's maintenance setting.

Inspect Docker's latest probe output and status:

```sh
docker inspect --format '{{json .State.Health}}' repl-hosting-remote
```

## Logs and session output

Operational diagnostics use the host's `ILogger` providers and appear in the container's stdout/stderr logging stream:

```sh
docker logs -f repl-hosting-remote
```

From a Repl session, run `log "operator checkpoint"`. The message appears in the host logs; the calling session receives an acknowledgement. Other sessions do not receive it. The host also logs startup, shutdown and connection failures.

Handlers send interactive output through `IReplIoContext.Output`, `.Error` or `IReplInteractionChannel`. Returned results are rendered for that invocation. Do not replace `Console.Out` globally to capture an individual session: other sessions and the application's console logger share the process. The sample's explicit client modes write their own returned stdout, stderr and JSON to the attached client process.

Settings and bus messages are shared. Input, history and command cancellation are session-local. The sample's `who` and `sessions` display tracks browser sessions; native sessions still expose their own metadata through `status`.

## Startup, callbacks and shutdown

The sample follows this ownership order:

1. Select client mode before constructing application services.
2. In server mode, register shared services and configure the Repl graph. Enable the embedded console profile so the application host owns process signals.
3. Map browser transports and create the local listener. Each connection/session receives its own I/O and cancellation context.
4. Start the web host. Its started/stopping lifetime state drives the health commands. Terminal startup callbacks and command hooks run through their ordinary Repl pipelines.
5. On the host's stopping signal, reject new sessions and cancel browser sessions. Stop the pipe listener and drain its connections and the tracked SignalR runs.
6. Stop the HTTP host, then dispose shared services after their users finish.

See [startup callbacks](advanced-startup.md), [input hooks](advanced-input.md), and [remote CLI lifetimes](remote-cli.md) for the detailed framework order. The host's signal handling remains authoritative; an attached console never installs signal handling in the server process.

The sample allows fifteen seconds for shutdown. Browser session cancellation runs command callbacks asynchronously so they cannot block the host's stopping notification. The shutdown deadline is armed before cleanup runs, including any synchronous cancellation callbacks. If cleanup fails or exceeds that budget, it logs a critical failure and terminates with exit `1` instead of disposing shared services beneath work that may still be running. This does not make uncooperative handlers safe to stop individually. The remote listener also has its own bounded disposal contract, documented in the remote CLI guide.

Give Docker twenty seconds before forced termination:

```sh
docker stop --time 20 repl-hosting-remote
docker inspect --format '{{.State.ExitCode}}' repl-hosting-remote
docker rm repl-hosting-remote
```

With the sample's cooperative commands, shutdown cancels active sessions and exits `0`. Closing a client alone does not initiate this sequence.

Docker exec processes live only while the container's PID 1 is running, so their termination status during teardown can differ from the server's. Check the container exit code and host logs to verify service shutdown. See [Docker's exec lifecycle](https://docs.docker.com/reference/cli/docker/container/exec/).

## Portainer console

In Portainer, select **Containers**, the running container, then **Console**. Enable **Use custom command** and enter:

```text
dotnet /app/HostingRemoteSample.dll --remote-console repl-hosting-sample
```

Use an interactive TTY and the image's default user. Connect and run the same settings commands as above. Leave the console with Ctrl+D; the container keeps serving other sessions. The custom executable is the remote client, not a shell. See [Portainer's console documentation](https://docs.portainer.io/user/docker/containers/console) for the UI controls supported by your version.

## Users, storage and endpoint exposure

The runtime image runs as the .NET image's non-root application user. Named-pipe clients must use the same OS user; native Windows execution additionally requires compatible elevation. Do not override the console user to root as a substitute for matching ownership.

The pipe lives inside the container. `docker exec` creates the client there, so no socket mount or additional network port is needed. A client on the host cannot use that logical name to connect into the container automatically. Native deployments use a listener and clients on the same machine.

The sample's settings and history are in memory; a restart loses them. An embedding application that persists data must provide a writable mount owned by its runtime UID/GID. Mount configuration or inputs read-only when the application does not need to modify them. Do not make the entire service run as root to fix a volume permission error.

The published browser port is bound to host loopback. Browser endpoints have no built-in authentication in this sample. Exposing them beyond that trusted boundary requires authentication, authorization and appropriate transport security supplied by the embedding host. The same-user local pipe is also an administrative surface: hidden commands are not an authorization boundary.

`Repl__PipeName` changes the server's pipe name. Pass the same name to clients and override the image's health-check command accordingly. A pipe-name override alone does not rewrite the Dockerfile's exec array. See [Docker's HEALTHCHECK reference](https://docs.docker.com/reference/dockerfile/#healthcheck).

## Verify the recipe

The repository's container smoke test builds and runs this image, checks health and maintenance, exercises simultaneous clients and logs, and stops the service with active sessions. Run the documented smoke command from the sample README. Protocol, ownership and terminal restoration remain covered by the existing remote test suite.
