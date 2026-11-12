# Running on .NET 11

Repl ships for `net10.0` and `net11.0` from 0.12.1. These notes are for developers who build or run a Repl
application with the .NET 11 SDK. Some of the tools below predate .NET 11 (`dotnet run -e`, `dnx`) or ship
on their own (dotnetup), and are here because .NET 11 developers meet them. Each point was checked with the
.NET SDK 11.0.100; the platform is named where it matters.

## C# 15 and union results

A `net11.0` project compiles C# 15 by default, so it can declare a `union` without setting `LangVersion`;
the same declaration does not compile in a `net10.0` project with its default language version. A handler
can return a union, and Repl processes its active case: see
[Union return values](commands.md#union-return-values-c-15).

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
`~/.local/share/dotnetup/dotnet` on Linux; macOS was not checked. According to the dotnetup documentation,
its access mode decides who finds that installation: `none` changes no environment variable, and `shell`
sets `PATH` and `DOTNET_ROOT` only in the shell profile. The default is `everywhere` on Windows, which
changes the system `PATH` and sets `DOTNET_ROOT` for the user, and `shell` on Linux and macOS when dotnetup
recognises the shell, `none` otherwise. A host that was already running keeps its environment until it
restarts.

With `none` or `shell`, an agent host or IDE that does not start from that shell does not find the dotnetup
`dotnet` or `dnx`: it finds none, or another installation that comes first on its `PATH`. When no other
.NET installation is present, a framework-dependent executable, such as a .NET global tool, also stops
with "You must install .NET to run this application". This was checked on Linux with dotnetup as the only
installation.

The configurations below use the default folders above; if dotnetup installed .NET elsewhere
(`--install-path`), use that folder instead.

For an MCP server registered in such a host, use one of these entries in the host's configuration:

- run `dnx` through the absolute path of the dotnetup `dotnet`, which finds its own runtime. `dnx` runs the
  newest stable version; to stay on one version, install the tool and use the next entry.

  ```json
  {
    "mcpServers": {
      "myapp": {
        "command": "/home/me/.local/share/dotnetup/dotnet/dotnet",
        "args": ["dnx", "-y", "MyApp", "--", "mcp", "serve"]
      }
    }
  }
  ```

- or keep a framework-dependent executable, such as a global tool, and point it to the installation with
  `DOTNET_ROOT`:

  ```json
  {
    "mcpServers": {
      "myapp": {
        "command": "/home/me/.dotnet/tools/myapp",
        "args": ["mcp", "serve"],
        "env": { "DOTNET_ROOT": "/home/me/.local/share/dotnetup/dotnet" }
      }
    }
  }
  ```

- or publish the application self-contained, which needs no .NET installation
  (`dotnet publish -c Release -r linux-x64 --self-contained`, which also works for a project packed as a
  tool), and use the published executable as `command`.

On Windows:

- the `dnx` entry's `command` is `C:\Users\me\AppData\Local\dotnetup\dotnet\dotnet.exe`;
- the global tool entry's `command` is `C:\Users\me\.dotnet\tools\myapp.exe`, and its `DOTNET_ROOT` is
  `C:\Users\me\AppData\Local\dotnetup\dotnet`;
- the self-contained build uses `-r win-x64`.

JSON needs every backslash doubled (`C:\\Users\\me\\...`).

A tool built for an older major version than the one installed, such as a `net10.0` tool with only .NET 11
in the dotnetup folder, stops with "You must install or update .NET to run this application", through `dnx`
too. Install that runtime with dotnetup, target both `net10.0` and `net11.0`, or let the tool run on the
newer runtime: put `--allow-roll-forward` before the package name in the `dnx` arguments, or add
`"DOTNET_ROLL_FORWARD": "Major"` to the global tool entry's `env`. The error and both roll-forward options
were checked on Linux with only the .NET 11 runtime installed.

Shell completion does not depend on the folder .NET was installed in; set it up from the application's
own executable, as [Shell Completion](shell-completion.md#user-commands) explains.

See also: [MCP Server Reference](mcp-reference.md#publishing-and-installation) | [Output System](output-system.md) | [Shell Completion](shell-completion.md) | [For coding agents](for-coding-agents.md)
