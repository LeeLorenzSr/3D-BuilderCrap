# Architecture

`Builder.Core.EditorSession` is the application boundary. It owns the accepted immutable scene, stable object/group identities, selection and member scope, revisions, commands, history, preview tokens, files and recovery. A desktop control and an MCP client must reach the same behavior through this boundary.

## Modules

| Module | Responsibility |
|---|---|
| `Builder.Core` | Geometry data, semantic editing requests, diagnosis, formats, document transitions, recovery and shared presentation settings |
| `Builder.GeometryWorker` | Separate C++ process for bounded Manifold-backed geometry work; original protocol/process adapter |
| `Builder.Desktop` | WPF tabs, controls, viewport, picking input, tool drafts and document dialogs |
| `Builder.Windows.Text` | Installed-font discovery and shaping into Core outline data |
| `Builder.Windows.Images` | Raster decoding into Core pixel data |
| `Builder.Mcp` | Typed tools over an EditorSession, schema validation, connection ownership and progress/cancellation |
| `Builder.Mcp.Server` | Explicit headless stdio or desktop named-pipe attachment |
| `Shared/InstallationLease.cs` | Installed-host lease shared by Desktop/MCP and installer maintenance |

The existing namespace/project names and `Builder.exe` filename are internal identifiers; the product is 3D BuilderCrap.

The maintained source root is `src/BuilderCrap`. Version 1.0.2 relocates the source and updates build/check paths; it does not change document schemas, session semantics, installed filenames, the installer AppId or MCP tool names. Fresh native builds use `.local/geometry-build/BuilderCrap` so caches from earlier layouts cannot supply an old worker.

## Edits and previews

Accepted commands create undoable changes. Selection changes advance the session revision without inventing a mesh edit. Expensive work produces a private candidate tied to its input revision, selection/member scope and preview identity. Apply validates that ownership and commits one history step. Cancel discards the candidate. Invalid, failed, stale or cancelled work preserves accepted scene state.

Keep UI drafts local until they form a typed request. Do not mutate mesh data from the viewport or add a second document model in the MCP adapter. Long geometry operations use the Core cancellation/progress boundary and an isolated worker.

## Documents

Native Save writes supported 3MF data. Export writes a separate interchange file and retains the native destination. Open/New/Close transitions use explicit dirty-work decisions. A failed save prevents replacement; a save that committed before a later Open failure remains an honest saved document.

Recovery checkpoints live separately from native saves. They contain accepted work, not pending previews or the old Undo stack. Restore starts fresh history and marks the restored document unsaved while retaining its native destination.

## Presentation and access

Camera, display normals, wireframe, grid, colors, X-ray, shadows and reflections are presentation. They do not bake mesh edits or add document history. Presentation settings are kept outside model files.

Desktop MCP is disabled at launch. Enabling it grants one trusted same-user client access to the exact open session through a protected local pipe. Headless mode owns another session. There is no network listener or folder sandbox. [MCP rules](mcp-access.md).
