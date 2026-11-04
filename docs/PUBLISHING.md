# Publishing TrayDrop on GitHub

[Application overview](../README.md) | [Architecture](ARCHITECTURE.md) | [Screenshots](SCREENSHOTS.md)

This is the maintainer's release guide for [istartedcoding/TrayDrop](https://github.com/istartedcoding/TrayDrop). The current downloadable milestone is [v0.1.1](https://github.com/istartedcoding/TrayDrop/releases/tag/v0.1.1). Commands below are examples for preparing and publishing a release, not instructions for downloading the app.

## 1. Decide the public project identity

Repository: **istartedcoding/TrayDrop**.

Suggested short description:

> A native Windows tray app for two-way file transfers over trusted local Wi-Fi, with QR browser pairing and a configurable Downloads destination.

Possible repository topics: `windows`, `winforms`, `dotnet`, `file-transfer`, `local-network`, `wifi`, `qr-code`, `aspnet-core`.

The project currently declares version **0.1.1**. Mark the release **pre-release** while compatibility, MSI/MSIX packaging, signing, and encrypted transport are still being developed.

Do not describe the current app as a network-wide device scanner, encrypted file-transfer service, native mobile app, or internet-accessible sharing server.

## 2. Preserve project and dependency licenses

The project owner selected [MIT](../LICENSE) for the original source and documentation. Third-party components retain their own terms, summarized in [third-party notices](../THIRD_PARTY_NOTICES.md).

Review third-party dependency obligations as part of the release:

- QRCoder is used for QR generation.
- .NET and ASP.NET Core runtime components are included in a self-contained executable.
- MSTest is a development/test dependency.

Include the original dependency license and notice files from the actual versions bundled in each portable ZIP. Do not discard notices or assume the project license overrides dependency terms.

## 3. Review the source and documentation package

Include:

- [README.md](../README.md), with the introduction, feature summary, setup, limitations, and architecture overview.
- [Architecture and API reference](ARCHITECTURE.md).
- [Screenshot gallery](SCREENSHOTS.md) and its [images](images).
- [Publishing guide](PUBLISHING.md).
- [End-user guide](HOW_TO_USE.md), [release notes](releases/v0.1.1.md), [setup script](../scripts/Install-TrayDrop.ps1), [project license](../LICENSE), and [dependency notices](../THIRD_PARTY_NOTICES.md).
- [Desktop source](../src/TrayDrop), [core source](../src/TrayDrop.Core), and [tests](../tests/TrayDrop.Tests).
- [Solution](../TrayDrop.slnx), [shared build settings](../Directory.Build.props), and [ignore rules](../.gitignore).
- The original dependency license and notice texts required for binary redistribution.

The existing ignore rules exclude build outputs, intermediate objects, test results, IDE state, and the local publish directory. Keep binary release downloads in GitHub Releases rather than committing the publish directory to source history.

Before committing:

- [ ] Confirm you own or have permission to publish all source, documentation, and assets.
- [ ] Select and add a project license.
- [ ] Review dependency licensing and redistribution obligations.
- [ ] Check for credentials, private pairing links, bearer tokens, download keys, personal paths, and unintended logs.
- [ ] Include the demo-only screenshots, not images of a real QR invitation or personal transfer queue.
- [ ] Verify the README's relative links and image paths work after cloning.
- [ ] Keep the trusted-network and unencrypted-HTTP warnings visible.
- [ ] State limitations and planned work separately from implemented features.

## 4. Build and test on Windows

Install the .NET 10 SDK, then run from the repository root:

```powershell
dotnet build .\TrayDrop.slnx --configuration Release
dotnet test .\tests\TrayDrop.Tests\TrayDrop.Tests.csproj --configuration Release
```

The documented milestone passed 54 test cases, including data-driven filename cases and 10 native Windows interaction cases. Run the suite again for the exact commit you plan to release.

The phone page is embedded during the .NET build. A Node.js installation, npm dependency installation, or frontend bundling step is not required to build or run the application.

## 5. Publish the standalone executable

Use a separate versioned output so publishing does not overwrite a running executable. Build a self-contained Windows x64 version without debugging symbols:

```powershell
dotnet publish .\src\TrayDrop\TrayDrop.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=None `
  -p:DebugSymbols=false `
  --output .\artifacts\releases\v0.1.1\TrayDrop
```

The resulting executable is `artifacts\releases\v0.1.1\TrayDrop\TrayDrop.exe`. A separate .NET installation is not required on the receiving PC for this self-contained build.

Add the end-user guide, project license, dependency notices, QRCoder license, and the resolved Microsoft runtime license/notice files to that portable folder. Copy `scripts\Install-TrayDrop.ps1` beside `TrayDrop.exe`; its packaged mode must work without the source checkout or SDK. Exclude PDB files and development/test outputs, then create `TrayDrop-0.1.1-win-x64.zip` and `SHA256SUMS.txt` alongside it.

Do not install the package, run its setup script, or launch the new executable in a session whose existing installation must remain untouched. Single-instance activation can otherwise bring the existing app forward instead of testing the new build. Use an isolated Windows user/session or another machine for manual executable checks, and record any checks not performed.

Smoke-test the exact published binary:

1. Start it as a normal user and confirm tray-only startup.
2. Open the panel and confirm lower-right placement and close-to-tray behavior.
3. Open its connection dialog and choose the appropriate local adapter.
4. Pair a phone on trusted Wi-Fi with firewall access allowed on Private networks only.
5. Drag a harmless file to the selected device and download it in the browser.
6. Upload a harmless file and verify it appears in the actual Downloads folder.
7. Choose another writable folder, restart the app, and confirm persistence.
8. Reset with **Use Downloads**, and verify new uploads use the Windows known-folder destination.
9. Remove a share and verify its future download link stops working.
10. Check the log for errors, then remove only your own smoke-test files.

These steps do not replace real-device compatibility testing or a security review.

## 6. Create the repository only when ready

For a new project, review and commit the intended files before creating and pushing a repository. The following example is not needed for the already-published TrayDrop repository:

```powershell
gh repo create YOUR_GITHUB_USERNAME/TrayDrop --public --source . --remote origin --push
```

This assumes a locally initialized Git repository, a reviewed commit, GitHub CLI authentication, and no conflicting remote. Replace the placeholder owner. Choose `--private` instead if you want to review the repository before making it public.

Do not push secrets and then rely on deleting them in a later commit. Once committed and pushed, sensitive values can remain in history.

## 7. Prepare the GitHub release

Create a reviewed version tag for the intended commit, then attach the published executable to a pre-release. For example:

```powershell
Get-FileHash -LiteralPath .\artifacts\releases\v0.1.1\TrayDrop-0.1.1-win-x64.zip -Algorithm SHA256
gh release create v0.1.1 `
  .\artifacts\releases\v0.1.1\TrayDrop-0.1.1-win-x64.zip `
  .\artifacts\releases\v0.1.1\SHA256SUMS.txt `
  --repo istartedcoding/TrayDrop `
  --verify-tag `
  --title "TrayDrop 0.1.1 (Windows preview)" `
  --prerelease `
  --notes-file .\docs\releases\v0.1.1.md
```

The example requires that the version tag has already been created and pushed, and that the portable package and its checksum file have been prepared for that exact commit. Increment the version and filenames when preparing a future release.

Suggested release-note content:

- Native Windows tray panel with file drag/drop and device selection.
- Recovery when a previous panel was disposed, including tray clicks, activation, dialogs, and notifications.
- Optional per-user setup with Start menu/desktop shortcuts and quiet Windows startup.
- QR/private-link browser pairing over local Wi-Fi.
- Two-way file transfers and per-device outgoing shares.
- Received files default to Downloads, with a remembered custom destination.
- Streaming uploads, range-capable downloads, filename checks, and no-overwrite saves.
- Exact regression-test results for the released commit.
- Supported release target: the built Windows x64 executable.
- Security notice: HTTP is unencrypted; trusted local networks only; no port forwarding.
- Known limitations: no MSI/MSIX installer/signing pipeline, native mobile apps, automatic unpaired-device discovery, folder transfer, automatic updates, or resumable uploads.
- SHA-256 checksums for the actual uploaded archive and its extracted executable.

The current unsigned executable may trigger Windows reputation or SmartScreen warnings. Do not instruct users to disable system security broadly. Signing and an MSI/MSIX installer are future packaging improvements, not current guarantees.

## 8. Final release checklist

- [ ] Public README and documentation match the released commit.
- [ ] Project license and dependency notices have been reviewed.
- [ ] Release build and tests passed for the tagged source.
- [ ] The exact self-contained binary passed the manual smoke checks.
- [ ] Release notes clearly identify the prototype/pre-release status.
- [ ] No live secrets or personal data appear in screenshots or release notes.
- [ ] The release asset and its checksum correspond.
- [ ] Supported/tested devices and browsers are stated without overstating coverage.
- [ ] Trusted-network, Private-firewall, and no-port-forwarding warnings are visible.
- [ ] Future features are not advertised as already available.
