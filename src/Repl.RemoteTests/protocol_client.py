"""Independent JSON-RPC client used by Given_RemoteInterop over redirected standard I/O."""

import json
import sys


def send(method, params, request_id):
    body = json.dumps({"jsonrpc": "2.0", "id": request_id, "method": method, "params": params}, ensure_ascii=False).encode("utf-8")
    sys.stdout.buffer.write(f"Content-Length: {len(body)}\r\n\r\n".encode("ascii") + body)
    sys.stdout.buffer.flush()


def receive():
    headers = {}
    while True:
        line = sys.stdin.buffer.readline()
        if not line:
            raise EOFError("Server disconnected before a complete header")
        if line == b"\r\n":
            break
        key, value = line.decode("ascii").split(":", 1)
        headers[key.lower()] = value.strip()
    length = int(headers["content-length"])
    body = bytearray()
    while len(body) < length:
        part = sys.stdin.buffer.read(length - len(body))
        if not part:
            raise EOFError("Server disconnected before a complete body")
        body.extend(part)
    return json.loads(body)


send("rpc.discover", {}, 1)
discovery = receive()
assert discovery["id"] == 1
assert discovery["result"]["x-cli"]["version"].startswith("1.")
assert {"cli.describe", "cli.execute", "terminal.open"} <= {m["name"] for m in discovery["result"]["methods"]}

send("cli.describe", {}, "graph")
assert receive()["result"]["name"] == "python-interop"

value = {"label": "été 🦆", "items": [1, True, None, {"empty": []}]}
send("cli.execute", {"command": "echo", "values": {"value": value}}, 3)
result = receive()["result"]
assert result["result"] == value
assert result["exitCode"] == 17

send("not.a.method", {}, 4)
assert receive()["error"]["code"] == -32601
send("cli.execute", {}, 5)
assert receive()["error"]["code"] == -32602

# Public CLR methods on the target must not become remote methods.
send("DisposeAsync", {}, 6)
assert receive()["error"]["code"] == -32601
print("Independent JSON-RPC discovery, invocation and validation passed.", file=sys.stderr)
