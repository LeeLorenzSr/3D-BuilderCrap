# Printing workflows and limits

3D BuilderCrap is a mesh preparation utility, not a slicer or a parametric CAD system. Use the File menu to open or export a model. Save, Undo and Redo sit at the upper right.

## Tabs

| Tab | Main tasks |
|---|---|
| Insert | Cube, cylinder, pyramid, cone, sphere, hexagon, wedge, torus, tetrahedron; text, images and model insertion |
| Object | Duplicate/delete, Settle, mirror, measurement, alignment, distribution, center on plate and rectangular/radial arrays |
| Edit | Split, union/subtract/intersect, diagnosis/repair, hollow, simplify, smooth, separate pieces, extrude down and planar emboss |
| Paint | Object colors/materials, flat surface fill and brush painting |
| View | Camera/display controls, model information, recovery, display preferences and local MCP access |

Select objects in the viewport or Items list. Ctrl adds/removes items; Shift selects a range in the list. Group and ungroup selections using the controls above the Items list. Groups normally edit together. Expand a group and enter a member to edit it without ungrouping; Done returns to the group. Mesh tools show a preview before you accept an edit.

## Files and units

Scene coordinates are millimeters. Confirm the source unit for unitless formats. Use native 3MF for continued editing; use Export for a slicer/interchange copy and verify scale in the slicer.

| Format | Supported subset |
|---|---|
| 3MF | Supported mesh data, names, flat groups, opaque object/triangle colors and title/designer/description/attribution metadata |
| STL | Triangle geometry with explicit import units; export in mm. No groups or colors. Nonstandard binary attribute/color words require explicit geometry-only consent. |
| OBJ/MTL | Explicit units, named meshes/flat groups and opaque colors. Keep OBJ and companion MTL together. |
| PLY | ASCII and binary mesh import with opaque face/vertex colors; one flattened mesh on export; vertex gradients become flat triangle colors. |
| glTF/GLB | Static named meshes/flat groups, transforms and opaque base/vertex colors, with standard meter scaling. Textures, animation and skins are outside the subset. |

When a 3MF has additional package data, such as a slicer profile or thumbnail, Open loads the supported model as a working copy. Choose a new file name to save your edits. The original keeps its attachments and settings.

## Move, rotate and scale

Select a part and choose a mode from the strip below the model. Drag a colored handle and release to accept one edit. Escape during a drag cancels it. In Scale, a corner handle changes all dimensions together. To enter an exact value, select its field in the strip, type the number and press Enter. The checkmark accepts a typed change and the cross discards it. Move shows the center position in millimeters, Rotate shows the part’s Roll, Pitch and Yaw in degrees, and Scale shows dimensions in millimeters. For example, changing Yaw from 7° to 12° turns the part another 5°. For multiple parts or a sheared, mirrored or collapsed placement, **Δ°** indicates a relative rotation. The arrow at the end of the strip opens handle snapping options.

**Object → Settle** previews moving the lowest point of the selection to Z = 0 while preserving its orientation. Choose **Settle** to accept or **Cancel** to keep the original placement. The floor has 10 mm squares and marks the positive axes every 50 mm. **View → Grid** toggles the floor and its measurements.

New/Open/Close on dirty work offers Save, Discard or Cancel. Failed/cancelled operations retain accepted work. Export does not change the native save path.

## Recovery and settings

Checkpoints, recent paths and View preferences live under `%LOCALAPPDATA%\3D BuilderCrap`, outside your models and application installation. On recovery, Restore retains the native destination and selection, starts fresh Undo history and marks the document unsaved. Not now retains the copy. Discard removes only the chosen recovery copy. Uninstall keeps model files and user data.

## Practical limits

- Current scene bounds are **1,000 objects / 100,000 triangles**, with additional operation limits.
- Hollowing has a **4,096 input-triangle cap** and produces sealed cavities; no drain-hole tool is included.
- Repair, simplify and smoothing are conservative subsets and may refuse difficult meshes. Closed topology alone does not prove printability.
- Some mesh edits refuse painted geometry when they cannot retain color ownership. Complete those edits before surface painting.
- No texture painting, curved emboss, general CAD/sketch history, automatic packing, slicing or direct printer control.
- Native ARM64/x86, every format extension/client, accessibility conformance and per-monitor DPI certification are not claimed.

The app includes an [offline guide](../src/BuilderCrap/Help/index.html). Desktop mouse controls are right-drag orbit, middle-drag pan and wheel zoom. F fits the scene; Help/F1 describes commands. Text fields retain their local Undo while document history is available outside active field/tool ownership.
