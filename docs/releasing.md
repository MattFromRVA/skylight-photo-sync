# Releasing: publish a version to NuGet

Pushing a tag such as `v0.1.0` publishes the `SkylightPhotoSync` package to
[nuget.org](https://www.nuget.org/packages/SkylightPhotoSync), which is what makes
`dotnet tool install --global SkylightPhotoSync` work. The
[publish workflow](../.github/workflows/publish.yml) does the work; this page covers the one-time
setup it needs and how to cut a release. Only the repository owner can do either.

## One-time setup: the `NUGET_API_KEY` secret

The workflow reads the nuget.org API key from a repository secret named `NUGET_API_KEY`. The key
never goes in the repo. Until the secret exists, the workflow builds and packs, then fails at the
push step with a message saying the secret is missing.

1. Sign in at [nuget.org](https://www.nuget.org) and open
   [API Keys](https://www.nuget.org/account/apikeys) from the account menu.
2. Choose **Create** and fill in:
   - **Key name:** anything, for example `skylight-photo-sync publish`.
   - **Expiration:** up to 365 days. The key must be replaced when it expires (repeat these steps).
   - **Scopes:** **Push**, then **Push new packages and package versions**. The first release
     creates the package, so "push only new package versions" is not enough until then.
   - **Glob pattern:** `SkylightPhotoSync`, so the key can push this package id and nothing else.
3. Copy the key. nuget.org shows it once.
4. Store it as the repository secret. This prompts for the value, so the key stays out of your
   shell history:

   ```bash
   gh secret set NUGET_API_KEY --repo MattFromRVA/skylight-photo-sync
   ```

   Check that it is there (the value is never shown):

   ```bash
   gh secret list --repo MattFromRVA/skylight-photo-sync
   ```

## Cut a release

The package version is the tag without its leading `v`. Tag the commit on `main` that you want to
ship, and push the tag:

```bash
git switch main && git pull
git tag v0.1.0
git push origin v0.1.0
```

Watch the run with `gh run watch` or under **Actions > Publish to NuGet**. When it is green,
nuget.org still takes a few minutes to validate and index the package before
`dotnet tool install --global SkylightPhotoSync` can find it.

The tag must be `v` followed by a [SemVer](https://semver.org) version: `v1.2.3`, or
`v1.2.3-rc.1` for a pre-release (which people install with `--prerelease` or an explicit
`--version`). Anything else, such as `v1.2`, `v1.2.3.4` or `vnext`, fails the run in its first
step and publishes nothing; `scripts/release-version.sh` is that check, and you can run it
yourself first: `scripts/release-version.sh v0.1.0` prints `0.1.0`.

What the workflow does, in order: checks the tag, builds with `-p:Version=<version>` (warnings
are errors), runs the format check and the tests, packs, and pushes with `--skip-duplicate`.
`skylight-sync --version` reports the same version as the package.

## If something goes wrong

- **The run failed before the push step** (bad tag, build, format or tests). Nothing was
  published. Fix the problem on `main`, then move the tag or use a new one. To redo the same
  tag: `git tag -d v0.1.0 && git push origin :refs/tags/v0.1.0`, then tag and push again.
- **The push step failed because the secret is missing or the key expired.** Set the secret as
  above, then re-run the failed run (`gh run rerun <run-id>`); no new tag is needed.
- **A bad version was published.** nuget.org never lets a version number be replaced or reused.
  Unlist the bad version on nuget.org and release a higher version. Re-running a workflow for a
  version that is already on nuget.org is harmless: `--skip-duplicate` makes the push a no-op.

## Not covered here

A Homebrew tap that wraps the tool is tracked in
[issue #49](https://github.com/MattFromRVA/skylight-photo-sync/issues/49).
