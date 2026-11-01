# Connect to a running process

## Browser administration

Open the administration listener on port 8081. The dashboard shows the index currently serving queries, while the embedded terminal opens its own Repl session over a WebSocket.

Type `index status` to inspect the active generation. Use `search compare --query "remote console"` to compare keyword, vector and hybrid rankings in Spectre tables.

## Attach from a shell

Run `docs-index console` on the same machine and as the same operating-system user as the server. The client connects to a named pipe. It does not load another model or create another index.

Inside a container, use `docker exec -it docs-index /app/docs-index console`. In Portainer, choose a custom console command with this same executable and argument instead of `/bin/sh`.

## Leave without stopping the server

Type `exit` or press Ctrl+D to detach. Each terminal has independent history and cancellation. Closing your browser or interrupting a command does not stop the host or another session.
