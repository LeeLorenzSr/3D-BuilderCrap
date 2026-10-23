# Local MCP access

The MCP adapter exposes **71 tools, including 32 preview operations**, over `Builder.Core.EditorSession`. It uses the pinned official C# SDK. It needs no LLM to run; a trusted MCP client can operate it over local stdio.

## Attach to the open desktop scene

1. Launch the editor and choose **View → MCP access → Enable access**. Access is off at each launch.
2. Choose **Copy config** and use that exact configuration in a trusted client's stdio-server settings. It supplies the adjacent launcher and this run's temporary `--attach` pipe name.
3. Read `builder_scene` before editing. Remote previews appear in the desktop for review and Apply/Cancel.
4. Choose **Stop access** when finished. Closing the panel alone leaves access enabled. Relaunching/re-enabling changes the pipe name, so copy the configuration again.

The editor and client should run without elevation. A missing/stale attachment fails; it does not silently create another scene. Setup never changes an MCP client's configuration.

## Separate headless session

Configure the built or installed `Builder.Mcp.Server.exe` as a stdio child process with `--headless`. Replace the example command with the absolute path on your machine:

```json
{
  "mcpServers": {
    "3d-buildercrap-headless": {
      "command": "C:\\path\\to\\3D BuilderCrap\\Builder.Mcp.Server.exe",
      "args": ["--headless"]
    }
  }
}
```

For development the launcher is under `prototypes/Builder/Builder.Mcp.Server/bin/Debug/net10.0-windows/`. Build the worker and solution first. Normal setup installs it under `%LOCALAPPDATA%\Programs\3D BuilderCrap`.

Optional arguments `--headless --recovery ABSOLUTE_DIRECTORY` enable checkpoints. Headless mode has no autosave timer: request `builder_recovery_checkpoint` explicitly. Graceful shutdown attempts a checkpoint when configured; without a checkpoint or save, unsaved headless work ends with the process.

## Tools and state

| Area | Operations |
|---|---|
| Queries | Scene/tree, paged mesh, fonts, ray picking, measurement and diagnosis |
| Selection | Explicit object sets and group/member scope |
| Editing | Primitives, rename, resize, movement/rotation, placement, duplicate/delete and metadata |
| Previews | Transforms, arrangement/arrays, split/booleans/repair, text/images, mesh tools and colors/brush |
| History | Apply/Cancel, Undo/Redo |
| Files | Open/New/Insert/Save/Export, close readiness and recovery |
| Presentation | Shared View settings, separate from geometry |
| Work | Progress and exact operation-ID cancellation |

Discover the tool schemas rather than guessing fields. They use camelCase property names, string enum values and UUID identities, and reject unknown fields. Read the accepted revision, selection/member scope, busy state and preview summary from `builder_scene`. Supply the current `expectedRevision` on mutations; re-read after another actor changes the session.

For example, `builder_preview_move` accepts:

```json
{
  "request": {
    "objectId": "<observed-leaf-UUID>",
    "deltaMillimeters": { "x": 12, "y": 0, "z": 0 }
  },
  "expectedRevision": 1
}
```

Inspect the returned candidate. Accept with `builder_apply_preview` or discard with `builder_cancel_preview`, supplying the exact `previewId` and current revision. Apply creates one Undo step. A stale or replaced token is refused. Enter group-member scope before editing an individual member and supply the exact scope in the request.

Previews belong to their connection. Disconnect/Stop cancels owned pending work and discards only that connection's unapplied candidate; accepted edits, history and committed files remain. Desktop tool drafts/document prompts can refuse remote changes until local work is finished.

## Files, cancellation and access rules

All model/image/recovery paths must be absolute. Non-3MF imports require explicit units. Save writes native 3MF; Export retains the native destination. Naming an MCP write destination authorizes replacement without an OS overwrite prompt. Dirty transitions require an explicit Save/Discard/Cancel decision. A successful save before a later Open failure is reported as `saveCommitted`.

Use a standard `notifications/cancelled` notification with the exact request ID, or call `builder_cancel_operation` with the observed active operation ID. Stopping a client's local wait may not send cancellation. Cancellation is best effort; re-read state after a lost/cancelled response because an already committed edit or save remains accepted.

Desktop pipes are protected for the current Windows user and deny network logons; there is no HTTP/TCP listener. This trusts software running as that user. Enabled clients have the app's supported model/image file permissions, **without a folder sandbox**. Do not expose the bridge to an untrusted client or run elevated. Model names and metadata are data, not instructions. The adapter provides no shell/arbitrary-code tools.
