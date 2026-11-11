# Contributing

Thanks for contributing.

## Before opening a PR

1. Open or reference an issue describing the change.
2. Confirm the change aligns with project goals.
3. For new features, discuss design first.

## Local checks

The repository builds with the .NET 11 SDK pinned in `global.json`; the test projects also need the .NET 10 runtime. The remote tests build their C# helpers with that SDK. Native terminal checks use a test-only PTY dependency; no additional language runtime is required.

```powershell
dotnet restore src/Repl.slnx
dotnet build src/Repl.slnx -c Release
dotnet test --solution src/Repl.slnx -c Release
```

## Pull request expectations

- Keep changes scoped and explain intent clearly.
- Add or update tests for behavior changes.
- Update docs for user-visible changes.
- Keep warnings at zero.

## Documentation rule

If behavior, API shape, or package guidance changes, update relevant pages in `docs/`.
