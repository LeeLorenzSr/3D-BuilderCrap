# Changelog

## 1.0.4

- Put Move, Rotate and Scale beside the model with color-coded handles and direct numeric entry. Drag and release to make one undoable edit.
- Replace the adaptive line grid with a 10 mm checkerboard and mark the positive X and Y axes every 50 mm.
- Move document commands into File, keep Save and history in the workspace header, and let the Items pane collapse.
- Keep surface colors visible under a blue selection outline. Use the same neutral gray for unassigned faces, painting and default color export.
- Rename Place on bed to Settle and give it a dedicated confirmation.

## 1.0.3

- Preserve the source 3MF when opening a project that contains slicer settings, attachments or other package parts. Save the editable model to a separate file.
- Keep typed dimensions, positions and angles exact with handle snapping enabled. Place on bed keeps the handle snap setting.
- Reopen miniature models with small placement scales. Save collapsed glTF instances with their visible geometry and face colors.

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
