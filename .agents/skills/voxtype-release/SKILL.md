---
name: voxtype-release
description: Build and publish VoxType Windows x64 preview releases with installer, portable ZIP and checksums.
---

# VoxType release

Run from the VoxType repository root. See `docs/releases.md`; the scripts are the source of truth.

1. Inspect Git status, origin, branch, local tags and remote tags. Origin must be `hongjiapeng/VoxType`; merge intended work into `master` first.
2. Choose an unused SemVer version, starting at `0.1.0`. Keep `Directory.Build.props`, script defaults, installer default and README examples aligned; write `docs/releases/v<version>.md`.
3. Commit the intended source before building publishable artifacts. For a local preview, `./scripts/build-release.ps1 -Version 0.1.0` also accepts a dirty tree, recorded in `build-info.json`; such artifacts cannot be published by the release script.
4. Build with `./scripts/build-release.ps1 -Version 0.1.0`. The script runs all Release tests and checks native/runtime/content files. No skip-tests option is provided. Inno Setup 6 is required. Do not overwrite existing artifacts silently.
5. Verify portable launch and installation/uninstallation as appropriate. Do not count automated checks as proof of real microphone, input focus or clean-machine compatibility.
6. Only when the user requests GitHub publication, run `./scripts/release.ps1 0.1.0`. It checks provenance and hashes, pushes master plus an annotated tag, uploads four assets to a draft, verifies their sizes and then publishes a prerelease. `-DryRun` checks source preflight without creating tags or uploading.
7. Confirm the Release is public (`isDraft=false`) and all assets exist. Report a pushed tag separately from a completed Release if upload or verification fails. Retain a failed draft for diagnosis; do not describe it as published.

Artifacts and test reports live in ignored `dist/` and `artifacts/`. Models, recordings, secrets and user settings are never release assets. The initial packages are unsigned; do not install a trust certificate or change Windows security policy as part of a release.
