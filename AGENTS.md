# Repository Guidance

- Release and publishing guidance lives in `docs/publishing.md`.
- Use Nerdbank.GitVersioning (`nbgv prepare-release`) for release preparation.
- Always run release preparation from `main`, not from a feature or pull request branch.
- CI builds and packs release artifacts; do not create local release packages unless explicitly needed for diagnostics.
