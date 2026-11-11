# Running on .NET 11

Repl ships for `net10.0` and `net11.0` from 0.12.1. These notes are for developers who build or run a Repl
application with the .NET 11 SDK. Some of the tools below predate .NET 11 (`dotnet run -e`, `dnx`) or ship
on their own (dotnetup), and are here because .NET 11 developers meet them. Each point was checked with the
.NET SDK 11.0.100; the platform is named where it matters.

## C# 15 and union results

A `net11.0` project compiles C# 15 by default, so it can declare a `union` without setting `LangVersion`;
the same declaration does not compile in a `net10.0` project with its default language version. A handler
can return a union, and Repl processes its active case: see
[Union return values](commands.md#union-return-values-net-11).

## `FORCE_COLOR`

The .NET 11 console logger (`Microsoft.Extensions.Logging.Console`) colours its output when `FORCE_COLOR`
has any non-empty value, even `0`, including on a redirected stream and ahead of `NO_COLOR` (checked on
Linux). Repl applies the same rule to its own output, so one variable colours both. Forcing colour onto a
file or a pipe does not make Repl write terminal control sequences there (screen clearing,
shell-integration marks, progress); the exact rules are in [ANSI Detection](output-system.md#ansi-detection).

Colours an application sets through `Console.ForegroundColor` are not written to a redirected stream,
whatever `FORCE_COLOR` says (checked on Windows and Linux). Repl writes its colours as ANSI sequences and
is not affected.

## `dotnet watch`

`dotnet watch` starts the application with its standard input, output and error attached to the
terminal, not redirected (checked on Windows and Linux), so Repl detects a terminal and starts the
interactive loop. But `dotnet watch` keeps the keyboard for its own keys, and the application does not
receive what is typed: on Linux, none of the keys sent while the application was reading reached it, with
or without `--non-interactive`.

Use `dotnet watch` for one-shot commands, which it runs again on each change, and `dotnet run` for the
interactive loop:

```bash
dotnet watch run -- contact list --json
dotnet run
```

## `dotnet run -e`

`dotnet run -e NAME=value` passes an environment variable to the application for that run, which is a
quick way to try environment-driven behaviour (checked on Windows and Linux):

```bash
dotnet run -e FORCE_COLOR=1 -- contact list > contacts.txt
dotnet run -e NO_COLOR=1
```

## dotnetup installs and agent hosts

[dotnetup](https://learn.microsoft.com/dotnet/core/tools/dotnetup/getting-started) (in public preview)
installs .NET per user, by default in `%LOCALAPPDATA%\dotnetup\dotnet` on Windows and
`~/.local/share/dotnetup/dotnet` on Linux. Its access mode decides who finds that installation: `none`
changes no environment variable, and `shell` sets `PATH` and `DOTNET_ROOT` only in the shell profile. The
default is `everywhere` on Windows, which changes the system `PATH`, and `shell` on Linux and macOS when
dotnetup recognises the shell, `none` otherwise.

With `none` or `shell`, an agent host or IDE that does not start from that shell finds neither `dotnet` nor
`dnx`. When no other .NET installation is present, a framework-dependent executable, such as a .NET global
tool, also stops with "You must install .NET to run this application". This was checked on Linux with
dotnetup as the only installation.

For an MCP server registered in such a host, either:

- run `dnx` through the absolute path of the dotnetup `dotnet`, which finds its own runtime:

  ```json
  {
    "command": "/home/me/.local/share/dotnetup/dotnet/dotnet",
    "args": ["dnx", "-y", "MyApp", "--", "mcp", "serve"]
  }
  ```

- or keep a framework-dependent executable, such as a global tool, and point it to the installation with
  `DOTNET_ROOT`:

  ```json
  {
    "command": "/home/me/.dotnet/tools/myapp",
    "args": ["mcp", "serve"],
    "env": { "DOTNET_ROOT": "/home/me/.local/share/dotnetup/dotnet" }
  }
  ```

- or publish the application self-contained, which needs no .NET installation.

On Windows the paths are `C:\\Users\\me\\AppData\\Local\\dotnetup\\dotnet\\dotnet.exe` for `command` and
`C:\\Users\\me\\AppData\\Local\\dotnetup\\dotnet` for `DOTNET_ROOT`, with the backslashes doubled in JSON.

Setting up shell completion works with a dotnetup `dotnet`: the setup check compares executable file names,
not folders. For an application started through `dotnet` rather than its own executable, PowerShell
completion keeps the full path of the `dotnet` that ran the setup, and stops answering if `dotnet` later
resolves to another installation; run the setup again after switching.

See also: [MCP Server Reference](mcp-reference.md#publishing-and-installation) | [Output System](output-system.md) | [Shell Completion](shell-completion.md) | [For coding agents](for-coding-agents.md)
