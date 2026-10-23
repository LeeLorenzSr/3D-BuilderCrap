# Publishing GitHub releases

The repository is [LeeLorenzSr/3D-BuilderCrap](https://github.com/LeeLorenzSr/3D-BuilderCrap). A release combines a version tag, notes and downloadable files. Keep installers out of Git and attach them as release assets.

Current version: **1.0.2**. This source change has not published a tag or release. Local packages remain unsigned.

## Recommended: build a draft from a tag

1. Review and commit the source/docs/workflow changes, then push **Main** using GitHub Desktop or Git.
2. In the maintained repository, create and push the matching version tag:

   ```powershell
   git switch Main
   git tag v1.0.2
   git push origin v1.0.2
   ```

3. Open the repository's **Actions** page. **Build draft Windows release** validates the version, builds the native worker and .NET application, runs the two focused Core modes, builds the installer and prepares checksummed assets on a Windows 2022 runner.
4. When the workflow succeeds, open [Releases](https://github.com/LeeLorenzSr/3D-BuilderCrap/releases). Review the **draft** for `v1.0.2`, its notes and five assets, then click **Publish release** when ready.

The workflow leaves the release unpublished. It uses the repository's automatic `GITHUB_TOKEN` with `contents: write`; no personal access token is required. Actions must be permitted by the repository/organization policy. Checkout/setup actions are pinned by commit, the SDK comes from `global.json`, and the Inno compiler download is pinned by SHA-256. Compiler installation happens only on the disposable runner.

An existing draft or published release is left unchanged. If a failed upload left a partial draft, inspect it and upload the missing assets manually; rerunning does not clobber files. Do not move/reuse a published version tag for different code.

## Local/manual release

Build and prepare the same files locally:

```powershell
pwsh -NoProfile -File scripts/Test-ReleaseVersion.ps1 -Tag v1.0.2
pwsh -NoProfile -File scripts/Build-GeometryWorker.ps1
dotnet build src/BuilderCrap/Builder.slnx
pwsh -NoProfile -File scripts/Build-WindowsPackage.ps1 -CompilerPath 'C:\path\to\ISCC.exe'
pwsh -NoProfile -File scripts/Prepare-ReleaseAssets.ps1
```

Choose fresh output folders for rebuilds; the scripts refuse to overwrite existing packages/assets. The version defaults to `src/BuilderCrap/Directory.Build.props`. `Prepare-ReleaseAssets.ps1` refuses a mismatched version, Packaging Test installer or bad package checksum.

Upload all files from `.local/release-assets/1.0.2/`:

- `3D-BuilderCrap-1.0.2-win-x64-Setup.exe`
- `SHA256SUMS.txt`
- `release-manifest.json`
- `LICENSE.txt`
- `RELEASE-NOTES.md`

On GitHub, choose **Releases → Draft a new release**, choose/create tag `v1.0.2` targeting the reviewed **Main** commit, use the notes from `docs/releases/1.0.2.md`, and attach the files. Save a draft for review, then **Publish release**. If the tag workflow also runs, it leaves this existing release untouched. GitHub adds its own source archives; those archives do not contain the built installer.

## Next versions

Update the version in `src/BuilderCrap/Directory.Build.props`, the four-part assembly identity in `Builder.Desktop/app.manifest`, and the fallback in `Packaging/Builder.iss`. Update CHANGELOG and public version references, and add `docs/releases/<version>.md`. The release guard rejects inconsistent versions and a tag other than `v<version>`. Build/check the change before pushing its tag.

## Verification boundary

The 1.0.2 local native/.NET/package builds, release-asset preparation and focused checks are recorded in the local migration evidence. The hosted GitHub Actions workflow cannot be claimed as exercised until a maintainer pushes the tag and reviews that run. Source relocation changes no model schema, installer AppId or MCP tool contract. Accessibility/DPI testing and Windows settings changes are outside this release pass.

GitHub documents [creating and publishing releases](https://docs.github.com/en/repositories/releasing-projects-on-github/managing-releases-in-a-repository), and the CLI documents [`--draft` and `--verify-tag`](https://cli.github.com/manual/gh_release_create).
