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

Cutting a release line starts from a clean `main`, never from a feature branch or a pull
request branch. That is what this section covers. Two other `nbgv prepare-release`
invocations operate on an existing release branch instead — promoting its stability stage,
and the servicing flow — and are described under *Servicing a released version* below.

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
behaviour for a stable line, and pushing to such a branch more than once is an ordinary thing to do —
a documentation fix, a workflow tweak, a cherry-picked change. Neither publish step treats the repeat
as an error, and neither of them can republish the version either.

**A published release is left entirely alone.** The job checks whether `v<version>` has already been
published and, if it has, touches nothing: not the assets, not the tag. It does not re-upload,
because a released version is immutable on NuGet — replacing the assets would leave a direct GitHub
download and a NuGet install of one version number carrying different binaries. The tag likewise keeps pointing at the
commit that produced the published packages, the only commit it can honestly describe. Moving a
published tag is a deliberate decision, not something CI should do on its own.

**An unfinished draft stops the run instead.** `gh release create` attaches assets by creating the
release as a draft, uploading the packages, then publishing it, so a run interrupted mid-upload
leaves a draft behind — and the existence check finds it, because `gh release view` looks up drafts
as well as published releases. Taking that for a finished release would skip creation and let NuGet
publish under a version with no release anyone can see, so the job fails instead and publishes
nothing. Inspect the draft and either publish it with the packages it is missing or delete it and
re-run: both are decisions about what has already reached consumers, and CI should not guess at them.

**NuGet still runs, and that is deliberate.** `dotnet nuget push --skip-duplicate` leaves every
version it already has alone, which is the normal outcome here. But it *does* upload a package NuGet
is missing, and that is the only way a push whose upload failed part-way recovers without manual
intervention. Two things follow. A repeat run is not guaranteed to publish nothing at all — it
publishes precisely what NuGet lacks. And such a recovery uploads from the *current* commit, while
the release's assets and tag stay on the one that created them.

**So a change meant for consumers needs a new version.** Under a number already released, the release
assets will not be updated and NuGet will accept only what it does not yet have — which is never the
changed build of a package it already holds. The job says so through a workflow warning and a
job-summary note, precisely because a green build must not be read as "the change shipped". A commit
that changes nothing consumers receive can be pushed as it is.

### Servicing a released version

Nerdbank.GitVersioning's own workflow for a fix to an already-released line works **on the release
branch**, not through `nbgv prepare-release` from `main` — that command cuts a *new* line, which is
what the *Release preparation* section above covers:

```powershell
git switch release/0.11.0
# Merge or cherry-pick the fix and commit it.
nbgv get-version   # what version will this commit build as?
```

Run `nbgv get-version` before pushing, because this repository's configuration makes the answer
`0.11.0` again: `release.branchName` is `release/{version}`, so the branch carries a full three-part
version, and `prepare-release` removed the prerelease tag, leaving no `{height}` to advance. A fix
therefore needs its `version` field bumped on that branch — to `0.11.1` — before it can ship. The
branch name then no longer matches its version, which is cosmetic: `publicReleaseRefSpec` matches
`^refs/heads/release/.*$`, so packaging and release creation are unaffected.

`nbgv prepare-release` can also be run *on* a release branch to move its stability stage, for example
from a prerelease tag to stable — that is Nerdbank.GitVersioning's documented behaviour, not an
exception invented here. `AGENTS.md`'s rule that release preparation runs from `main` is about not
cutting a release from a feature or pull-request branch; a release branch is neither, and cutting a
new line still starts from `main` as the *Release preparation* section describes.

Consult Nerdbank.GitVersioning's versioning-workflow documentation before doing anything these paths
do not cover; do not improvise a version edit.

### What a release contains

The release job does not rebuild: it downloads the `packages` artifact produced by
`Build, Test, Pack`. Within that job, `Test` and `Pack` both run with `--no-build` against the
outputs of a single `Build`, so a release publishes exactly the assemblies the tests ran against.

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
