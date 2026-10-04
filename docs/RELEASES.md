# Versions and releases

Root `VERSION` is the only product version source. It starts at `2.0.0` and every new app repository commit must increase it, including documentation-only commits, amendments, squash commits, and merge commits. Use canonical numeric `major.minor.patch` (components 0–65534). The executable, native host, installer, package manifest, release tag and asset names share that version. Package dependency versions remain in Directory.Packages.props.

Install local checks once per clone with Node 24:

```sh
node scripts/install-hooks.mjs
```

This refuses to replace an existing hook configuration or disable existing local hooks. It changes only this checkout's Git configuration and sets the tracked hook files executable. CI is authoritative because local hooks can be bypassed.

Before each commit, choose an unused greater version, inspect and stage it with the change:

```sh
node scripts/version.mjs bump 2.0.2
git add VERSION
node scripts/version.mjs staged
```

The staged file is checked even if your working file differs. Fetch before choosing a version. A merge version must exceed both parents; parallel branches cannot reuse the same version. Fast-forwarding an existing commit does not create another version. Rebases/cherry-picks creating new commits may need new versions. The migration boundary is commit `6587c041b56dd8a2a7d9edebd307543b6971b972`; historical earlier commits are unchanged. Its first versioned successor introduces `2.0.0`. CI validates every introduced commit with full history/tags, including intermediate commits in multi-commit pushes and actual PR commits rather than GitHub's synthetic merge.

Run dependency-free policy tests:

```sh
node --test scripts/version.test.mjs scripts/release.test.mjs
```

## Automatic Windows publishing

CI keeps the existing Windows/Linux/macOS build and test matrix, checks application translations, and tests the legacy extension logic. A successful push to `v2` or `main`, or a manual workflow dispatch on one of those branch heads, builds Windows packages serially and publishes the tested head as `v<version>`. Pull requests and forks never get publishing credentials. Only the release job has `contents: write`, using this repository's GITHUB_TOKEN; no personal token is required. All actions are pinned to full verified commit SHAs.

The Windows job produces:

- `Colibri-<version>-win-x64-setup.exe`
- `Colibri-<version>-win-x64-portable.zip`
- `release-manifest.json` with repository, tag, version, exact filenames, sizes and SHA256 hashes
- `SHA256SUMS`

Publication is serialized, rejects stale branch tips and older stable versions, and never moves a tag or replaces an asset. A failed upload leaves a draft; a rerun verifies existing assets and only adds missing ones. All assets and the exact checksum list must match before the draft is published. A matching complete published release is a no-op. Current unsigned packages provide hash integrity, not an independent publisher signature.

The owner can enable [release immutability](https://docs.github.com/en/code-security/how-tos/secure-your-supply-chain/establish-provenance-and-integrity/prevent-release-changes) for GitHub-enforced protection in addition to the workflow's no-overwrite policy. The workflow reports whether GitHub made the published release immutable; it does not request administrative credentials or claim that its own checks prevent an administrator from changing assets. Set branch protection to require CI/version checks to block invalid integration. These scripts do not change repository settings, visibility, or authentication. Publication is skipped while this repository remains private. The owner will make it public; after that, dispatch CI on `v2`/`main` (or push the next versioned commit) to publish the tested head. Until then unauthenticated application update checks cannot access its feed. No initial release has been published as part of this implementation.

Automatic checks and user-requested download/install behavior belong to the application updater; publishing this pipeline does not by itself install updates. Portable packages must be unpacked to a new directory after exiting; the running executable is never overwritten. See [Windows packaging](WINDOWS-PACKAGING.md) for profile preservation and installer ownership checks.
