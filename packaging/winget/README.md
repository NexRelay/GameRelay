# winget manifest

These manifests let anyone install GameRelay with:

```powershell
winget install NexRelay.GameRelay
```

winget downloads the release zip, extracts it, and registers `GameRelay.exe`
as the `gamerelay` command (portable install — no admin rights).

## How updates work

Two independent mechanisms:

- **In-app:** GameRelay checks GitHub Releases on launch and shows a one-click
  "Update available" banner (see `UpdateChecker` in `GameRelay.Core`).
- **winget:** `winget upgrade NexRelay.GameRelay` once the new version's manifest
  is published.

## Publishing a new version

1. Cut the GitHub release and upload `GameRelay-Windows-x64-vX.Y.zip`.
2. Update `PackageVersion`, `InstallerUrl` and `InstallerSha256` in these files
   (get the hash with `Get-FileHash <zip> -Algorithm SHA256`).
3. Validate: `winget validate --manifest packaging/winget`.
4. To appear in the public catalog, submit the folder as a pull request to
   [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) (or use
   [wingetcreate](https://github.com/microsoft/winget-create)). Until then you
   can install straight from a local manifest:
   `winget install --manifest packaging/winget`.
