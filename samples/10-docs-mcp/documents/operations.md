# Health checks and deployment boundaries

## Liveness and readiness

`docs-index health` calls the running process through its local pipe and returns a process exit code. It checks liveness: the host has started and is not stopping. Docker can use this executable directly without curl or a shell.

`docs-index health --ready` also requires a compatible published index. During the first build, liveness can succeed while readiness fails. A failed rebuild does not make a healthy old index unavailable.

## Administration is trusted access

The administration console has no authentication. Anyone who can reach its listener can invoke administrative commands. Keep the port on loopback or a trusted network. Production exposure requires authentication, authorization, TLS and network restrictions.

The public listener exposes only the search and fetch tools. It does not expose the dashboard or the console. The sample does not implement rate limits or quotas; add abuse protection before exposing public search on the Internet.
