# Printing workflows and limits

3D BuilderCrap is a mesh preparation utility, not a slicer or a parametric CAD system. The main workflow is Open → inspect → select → edit → Preview → Apply/Cancel → Undo/Redo → Save or Export.

## Tabs

| Tab | Main tasks |
|---|---|
| Insert | Cube, cylinder, pyramid, cone, sphere, hexagon, wedge, torus, tetrahedron; text, images and model insertion |
| Object | Move/rotate/scale, snapping, groups and members, duplicate/delete, alignment, distribution, center on plate, rectangular/radial arrays, mirror and measurement |
| Edit | Split, union/subtract/intersect, diagnosis/repair, hollow, simplify, smooth, separate pieces, extrude down and planar emboss |
| Paint | Object colors/materials, flat surface fill and brush painting |
| View | Camera/display controls, model information, recovery, display preferences and local MCP access |

Select objects in the viewport or Objects list. Ctrl adds/removes items; Shift selects a range in the list. Groups normally edit together. Expand a group and enter a member to edit it without ungrouping; Done returns to the group. Review previews before accepting them. Each accepted edit can be undone.

## Files and units

Scene coordinates are millimeters. Confirm the source unit for unitless formats. Use native 3MF for continued editing; use Export for a slicer/interchange copy and verify scale in the slicer.

| Format | Supported subset |
|---|---|
| 3MF | Supported mesh data, names, flat groups, opaque object/triangle colors and title/designer/description/attribution metadata |
| STL | Triangle geometry with explicit import units; export in mm. No groups or colors. Nonstandard binary attribute/color words require explicit geometry-only consent. |
| OBJ/MTL | Explicit units, named meshes/flat groups and opaque colors. Keep OBJ and companion MTL together. |
| PLY | ASCII and binary mesh import with opaque face/vertex colors; one flattened mesh on export; vertex gradients become flat triangle colors. |
| glTF/GLB | Static named meshes/flat groups, transforms and opaque base/vertex colors, with standard meter scaling. Textures, animation and skins are outside the subset. |

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

The app includes an [offline guide](../prototypes/Builder/Help/index.html). Desktop mouse controls are right-drag orbit, middle-drag pan and wheel zoom. F fits the scene; Help/F1 describes commands. Text fields retain their local Undo while document history is available outside active field/tool ownership.
