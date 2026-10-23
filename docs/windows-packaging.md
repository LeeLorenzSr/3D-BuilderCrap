# Windows packaging

Current source version: **1.0.2**, Windows 11 x64, per-user installation. The installer contains a private .NET/Windows Desktop **10.0.12** runtime, native geometry worker, MCP launcher, offline help and third-party notices. No SDK or original Microsoft application is required on the destination machine.

## Build setup

First follow the [source build guide](build.md). Install **Inno Setup 7.1.0** separately and supply its compiler path; no compiler is downloaded or installed automatically.

```powershell
pwsh -NoProfile -File scripts/Build-WindowsPackage.ps1 `
  -Version 1.0.2 `
  -CompilerPath 'C:\path\to\Inno Setup 7\ISCC.exe'
```

The default output is ignored `.local/packages/1.0.2/`. Choose a fresh `-OutputDirectory` to rebuild; existing packages are never overwritten. Desktop and MCP are published separately as self-contained Release/win-x64 payloads and merged only when overlapping files match. The script collects runtime/dependency licenses and creates a payload inventory and installer SHA-256 file.

Installer binaries remain outside Git. [Publishing a GitHub release](releases.md) describes the draft-release workflow and manual upload path. Local packages are unsigned unless separately signed; no certificate or security exception is installed.

## Install and shortcuts

The default location is `%LOCALAPPDATA%\Programs\3D BuilderCrap`. Setup adds Start menu entries for the app, Help and recovery, and uninstall. On **Select Additional Tasks**, **Create a desktop shortcut** is optional and unchecked on a fresh install. Updates/repairs remember the choice; uninstall removes an installer-created shortcut. The shortcut launches that installation's `Builder.exe` with the installation folder as its working directory.

Silent setup can select `/TASKS="desktopicon"` or decline with `/TASKS=""`. Omit the override to retain the previous choice.

Setup requests no administrator access, does not launch the editor automatically, and does not change file associations, PATH, Windows security or global MCP client configuration.

## Update and uninstall

Save and close that installation's desktop/MCP hosts before maintenance. Running hosts make setup/uninstall refuse; the installer never kills them. The same version repairs, a newer version updates, and an older version refuses a downgrade. A foreign nonempty folder or new package filename colliding with a user-added file is refused.

The installer backs up owned files under its exact `.builder-rollback` child before updating. Normal failure/cancel paths restore prior bytes. If an interrupted journal remains, resolve the reported cause and rerun the matching/newer installer rather than deleting the backup. Real power-cut/disk-full recovery is not claimed.

Uninstall removes tracked application files, shortcuts and registration. It keeps saved models, user-added files, external MCP settings and `%LOCALAPPDATA%\3D BuilderCrap` preferences/recovery. It never recursively deletes the application folder. Keep models outside the installation directory.

## Verification

`scripts/Verify-WindowsPackage.ps1` accepts only separately identified **Packaging Test** builds and a fresh evidence directory; it refuses the normal production registration. Build the historical test pair 0.1.0 and 0.1.1 with `-TestChannel` into separate output folders. The 0.1.1 test build includes an extra test-only file for rollback/collision probes.

```powershell
pwsh -NoProfile -File scripts/Verify-WindowsPackage.ps1 `
  -BasePackage 'C:\path\test-base\3D-BuilderCrap-0.1.0-win-x64-Setup.exe' `
  -UpdatePackage 'C:\path\test-update\3D-BuilderCrap-0.1.1-win-x64-Setup.exe' `
  -EvidenceDirectory 'C:\path\fresh-package-evidence'
```

This is an optional installer-maintenance harness, not a normal application launch. Do not run it against a production installer or replace its separate AppId with the product AppId. Historical local reports/screenshots and installed binaries are not included in this source repository.
