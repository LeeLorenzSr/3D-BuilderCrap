# Changelog

## 1.0.2

- Move maintained application sources to `src/BuilderCrap` and update build scripts, checks, tools and documentation.
- Rebuild the Windows installer at version 1.0.2, preserving installed names, AppId, desktop shortcut choice and model/MCP compatibility.
- Add a tag-driven GitHub Actions workflow that builds, checks and stages a draft release with the installer, checksum, payload manifest and license.

## 1.0.1

- Windows setup offers an optional desktop shortcut, remembers the selection on maintenance, and removes an installer-created shortcut on uninstall.
- Source publication includes an MIT license, preserved dependency notices, public build/usage guides and original synthetic fixtures.

## 1.0.0

- Initial local v1 mesh editor with tabbed modeling, preview/Apply/Cancel, Undo/Redo, supported mesh interchange, painting, recovery and local MCP access.
- Self-contained Windows x64 installer with the geometry worker, MCP launcher, offline help and notices.
