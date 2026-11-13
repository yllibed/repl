# Deployment checklist

## Listener addresses

Native execution binds the MCP and administration listeners to loopback by default. To expose MCP intentionally, set `DOCS_MCP_ADDRESS` before starting the server. The administration listener should remain reachable only by trusted operators.

## Browser origins

Use the administration dashboard on its configured loopback port. A reverse proxy needs an explicit `DOCS_ADMIN_ORIGIN`. Origin and hostname checks protect the browser boundary; they do not authenticate a client and cannot replace authorization.

## Offline assets

Run `assets fetch` while network access is available. The server verifies the pinned model, vocabulary, native extension and browser files on startup. Once those assets are present, inference and dashboard assets are served locally without a network connection.

## Persistent storage

Store the SQLite index outside the source directory on writable local storage. Allow room for both the published database and a staging rebuild. Do not put the WAL database on a network filesystem or share it between multiple writing server processes.

## Container ownership

The container runs as a non-root user. A bind-mounted index directory must be writable by that user. The source corpus is mounted read-only; rebuilding reads source files and writes only to the separate index volume.

## Authentication

The sample does not install authentication, authorization, TLS or rate limits. Before exposing it, protect the public audience and the broader administration surface separately. Administration static files need protection as well as HTTP endpoints and WebSocket upgrades.
