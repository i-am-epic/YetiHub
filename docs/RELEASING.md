# Releasing

Releases are fully automated by [.github/workflows/release.yml](../.github/workflows/release.yml), using
[Velopack](https://velopack.io) for the installer and auto-updates.

## Ship a new version

1. Add a section at the top of `CHANGELOG.md`.
2. Commit, then tag and push:

   ```bash
   git tag v0.2.0
   git push origin main --tags
   ```

3. The workflow tests, builds self-contained `win-x64`, packages it, and publishes a GitHub Release with:
   - `YetiHub-win-Setup.exe`: first-time installer (per-user, no admin needed)
   - `YetiHub-win-Portable.zip`
   - `*-full.nupkg` / `*-delta.nupkg` + `releases.win.json`: what installed copies download to update

Installed copies check on startup, show "Version x is available", and update via **Install & restart**.

## Versioning

- `vMAJOR.MINOR.PATCH`. The tag is the single source of truth; CI passes it to `dotnet publish -p:Version=`.
- A tag like `v0.3.0-beta.1` also triggers a release. The in-app updater ignores GitHub releases marked
  "pre-release" unless `UpdateService` is changed to `prerelease: true`.

## Code signing (recommended later)

Unsigned installers trigger Windows SmartScreen. When you have a certificate, add to `vpk pack`:
`--signParams "/a /f cert.pfx /p ${{ secrets.CERT_PASSWORD }} /tr http://timestamp.digicert.com /td sha256 /fd sha256"`.
