# Repl.Remote.Defaults

Connects a running `ReplApp` to the `Repl.Remote` protocol. Each invocation uses a DI scope and the ordinary command pipeline. Interactive sessions use `StreamedReplHost` with targeted command interruption.

This is an optional adapter package. Referencing it or configuring `ReplApp` does not start a remote endpoint; the host explicitly starts and owns the listener.

See the [remote CLI guide](https://github.com/yllibed/repl/blob/main/docs/remote-cli.md) and [hosting sample](https://github.com/yllibed/repl/tree/main/samples/05-hosting-remote).
