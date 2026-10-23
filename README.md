# 3D BuilderCrap

An approachable Windows mesh editor for 3D printing, inspired by the useful workflows of Microsoft 3D Builder. Written in C#/.NET with WPF, a shared editing core, and a native geometry worker. It is an independent implementation and does not require Microsoft's application, REA, or an LLM.

Current source version: **1.0.1**. The original application code, tools and artwork in this repository are available under the [MIT license](LICENSE). Dependencies retain their own [licenses and notices](THIRD-PARTY-NOTICES.md).

![3D BuilderCrap application mark](prototypes/Builder/Builder.Desktop/Assets/Icons/app-mark.svg)

## What it does

- Open and insert STL, supported 3MF, OBJ/MTL, PLY and static glTF/GLB, with explicit source units and supported opaque colors.
- Move, rotate and scale objects using handles or numeric fields; edit group members; align, distribute, center, mirror and make rectangular or radial arrays.
- Create nine primitive shapes, text, planar emboss/engraving, image relief, contours, edges and stamps.
- Split meshes, perform booleans, diagnose and conservatively repair meshes, hollow, simplify, smooth, separate disconnected pieces and extrude down.
- Paint objects and surfaces, including brush strokes; preserve supported names, groups, colors and metadata in native 3MF.
- Review previews before Apply/Cancel, undo accepted edits, and recover accepted unsaved work separately from model files.
- Use **71 local MCP tools**, including 32 preview operations, through the same editing session as the desktop.

The desktop uses familiar Insert, Object, Edit, Paint and View tabs. Failed or cancelled operations preserve the last accepted scene. Supported subsets and practical limits are described in [the user guide](docs/user-guide.md).

## Build and run

Supported desktop target: **Windows 11 x64**. Install PowerShell 7, Git, the .NET SDK selected by `global.json`, and Visual Studio 2022 C++ build tools with CMake and a Windows SDK.

From the repository root:

```powershell
pwsh -NoProfile -File scripts/Build-GeometryWorker.ps1
dotnet build prototypes/Builder/Builder.slnx
dotnet run --no-build --project prototypes/Builder/Builder.Desktop
```

The native build fetches a pinned Manifold commit. NuGet restores the managed dependencies. Neither step needs the original Microsoft app. No downloaded tools, installed binaries or research captures are included in this repository. [Detailed prerequisites and build steps](docs/build.md).

## Prepare a model

Open a model and choose its source units. Select a part, choose a tool, inspect **Preview**, then **Apply** or **Cancel**. **Undo / Redo** changes accepted edits. **Save** writes native 3MF; **Export** writes a slicer/interchange copy. **Help / F1** opens the bundled offline guide.

Generate a small original sample corpus with `python scripts/generate_fixtures.py`; then open `fixtures/generated/cube-20mm.stl` in millimeters. [Checks and fixtures](docs/verification.md).

## Installer and MCP

[Build the Windows installer](docs/windows-packaging.md) to include the private .NET runtime, geometry worker, MCP launcher, help and notices. Setup offers an optional **Create a desktop shortcut** checkbox. Generated installers stay under ignored `.local/packages/`; no release download is published by this source repository yet. Local builds are unsigned unless separately signed.

For the open desktop scene, choose **View → MCP access → Enable access → Copy config**. Access is off at launch. A separate `--headless` stdio mode is also available. [Setup and local access rules](docs/mcp-access.md).

## Repository layout

The existing `prototypes/Builder` layout is retained so the source, check runners and build scripts use the same relative paths.

| Path | Contents |
|---|---|
| `prototypes/Builder/Builder.Core` | Scene identity, selection, commands, previews, history, formats and recovery |
| `prototypes/Builder/Builder.Desktop` | WPF tabs, tools and viewport |
| `prototypes/Builder/Builder.GeometryWorker` | C++ process adapter and pinned Manifold dependency |
| `prototypes/Builder/Builder.Mcp` / `Builder.Mcp.Server` | Shared-session tools and local transport |
| `prototypes/Builder/Builder.Windows.Text` / `Builder.Windows.Images` | Windows font and raster adapters |
| `prototypes/Builder/*Checks` | Core, WPF and protocol checks |
| `scripts` | Build, fixture generation and verification helpers |
| `docs` | Public build, usage, architecture and contribution guides |

[Architecture](docs/architecture.md) · [Contributing](CONTRIBUTING.md) · [Third-party notices](THIRD-PARTY-NOTICES.md)
