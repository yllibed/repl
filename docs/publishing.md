# Publishing Process

This repository publishes packages from CI.

## Release preparation

Use Nerdbank.GitVersioning to prepare releases:

```powershell
git switch main
git pull --ff-only
git status --short
nbgv prepare-release
```

For stable release preparation, run `nbgv prepare-release` from a clean `main` only. Do
not run it from a feature branch or a pull request branch.

The command uses the `release` settings in `version.json` to:

- create a `release/<version>` branch, for example `release/0.10.0`;
- remove the prerelease tag on the release branch, for example `0.10.0`;
- bump `main` to the next development version, for example `0.11.0-dev.{height}`;
- merge the release branch back into `main` so the `version.json` changes are resolved.

Push the resulting `main` and `release/<version>` branches when the release is ready.
CI is responsible for build, test, pack, symbol packages, GitHub Release creation, and
NuGet publication.

## What triggers publishing artifacts

- CI runs on:
  - `pull_request`
  - `push` on `main`
  - `push` on `release/**`
- On pushes to `main` or `release/**`, the GitHub Release job runs after build, test,
  pack, and documentation lint succeed.

## Branch strategy

- `main`: prerelease flow (`-dev.*`).
- `release/*`: release branch flow (same packaging pipeline, intended for release stabilization).
- Branch names come from `version.json`'s `release.branchName`, currently `release/{version}`, so
  they carry the full three-part version: `release/0.11.0`, not `release/0.11`.
- Release branches should be created from `main` with `nbgv prepare-release` rather than by hand.

## Versioning (`version.json`)

- Versioning is handled by Nerdbank.GitVersioning.
- `version.json` is **never updated automatically** by CI.
- The prerelease pattern lives in `version.json`'s `version` field — read it there rather than
  from this page, since `nbgv prepare-release` rewrites it at every release. On `main` it is a
  `-dev.{height}` pattern; on a release branch the prerelease tag is removed.
- `nbgv prepare-release` updates the `version` field when cutting a release. Manual edits
  should be reserved for changing the version line or policy outside the normal release flow.
- `publicReleaseRefSpec` covers `main`, `release/*` and `v<major>.<minor>` tags, so versions built
  from those refs carry no `.g<sha>` suffix. CI additionally passes `-p:PublicRelease=true` for
  pushes to `main` and `release/**` only — so a pull-request build deliberately produces
  `.g<sha>`-suffixed packages, which are diagnostic artifacts and are never published.

### When should I modify it?

- Usual flow:
  - On `main`, keep a moving prerelease pattern (for example `0.9.0-dev.{height}`).
  - When you cut `release/*`, let `nbgv prepare-release` set the release branch version
    and bump `main` to the next line.
  - Change `version.json` manually only if you want a different version stream there
    (for example `0.9.0-rc.{height}` instead of `0.9.0`).
- If you do not change `version.json` on `release/*`, that branch keeps the same version pattern inherited from `main`.

## One publish per version, and what that means on a release branch

`nbgv prepare-release` removes the prerelease tag on the release branch, so its `version` has no
`{height}`: **every commit on `release/0.11.0` computes the same `0.11.0`**. That is the intended
behaviour for a stable line, but it has a consequence worth knowing before you push twice.

The two publish steps disagree about repetition:

- `dotnet nuget push` runs with `--skip-duplicate`, so re-pushing an already-published version is
  tolerated and reported, not fatal.
- `gh release create` has no such tolerance. The second push to a release branch tries to create a
  release whose tag already exists, that step fails, and because `Publish to NuGet` is gated on
  `if: success()` the publication is skipped for that run.

So a follow-up commit on a release branch — a documentation fix, a cherry-picked hotfix — turns CI
red and publishes nothing, without anything being wrong with the code. Two ways through it:

- **Preferred:** cut a new version. Bump the release branch's `version` (for example to `0.11.1`)
  so the run produces a version that has never been released.
- **If the commit genuinely must not change the version** (say a workflow-only change on the release
  branch), expect the release job to fail on the duplicate and treat it as such; nothing was
  published, and nothing was lost.

The release job does not rebuild: it downloads the `packages` artifact produced by
`Build, Test, Pack`, so a release publishes exactly the packages CI tested.

## Do I need to create a Git tag manually?

- No for normal flow.
- On push to `main`/`release/*`, CI creates a GitHub Release with tag `v<NuGetPackageVersion>`.
- Only create tags manually for exceptional/manual workflows.

## Release notes

There is no changelog file to maintain. CI creates a GitHub Release per published version with
`gh release create "v${VERSION}" --target "${GITHUB_SHA}" --generate-notes`, so the notes are
generated from the pull requests merged since the previous tag and the tag points at the commit whose
packages are attached — an anchoring a hand-written file cannot have, since Nerdbank.GitVersioning
assigns the version at pack time.

Know what that publishes, and what it does not. `--generate-notes` emits **pull request titles**,
authors and links; it does **not** copy a PR description into the release body. So a PR title is
consumer-facing prose, and a migration step written only in a PR description is reachable through the
link but is not part of the notes.

Durable guidance therefore belongs in the topic page under `docs/` that owns the feature — a new
default, a behavioural break and its restore recipe, a constraint on upgrading packages together.
The PR description is where you explain the change to a reviewer; `docs/` is where a consumer finds
it six months later.

## NuGet publish status

- Package and symbol packages (`.snupkg`) are produced by `Build, Test, Pack` and published
  by the release job.
- `Publish to NuGet` runs after GitHub Release creation when the preceding step succeeds
  (`if: success()`).
- The `NUGET_API_KEY` repository secret must remain configured. Do not expose its value or
  attempt to validate it locally.
