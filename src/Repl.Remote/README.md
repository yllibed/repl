# Repl.Remote

JSON-RPC clients, servers and local named-pipe listeners for administering an existing process. Supports structured command execution and interactive UTF-8/VT terminals.

This package has no dependency on Repl.Core, Repl.Defaults or a DI container. Implement `IRemoteReplBackend`, or use the `Repl.Remote.Defaults` adapter for `ReplApp`.

See the [remote CLI guide](https://github.com/yllibed/repl/blob/main/docs/remote-cli.md) for the experimental wire profile, ownership rules and hosting examples.
