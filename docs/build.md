# Build and run

The desktop and packaged MCP host target **Windows 11 x64**. The core library targets portable .NET 10; the desktop, font/image adapters and local pipe host use Windows APIs.

## Prerequisites

- Git and PowerShell 7 (`pwsh`).
- .NET SDK **10.0.401**, or a later 10.0.4xx patch accepted by `global.json`.
- Visual Studio 2022 / Build Tools with **Desktop development with C++**, MSVC x64 tools, a Windows SDK, and the Visual C++ CMake tools. CMake 3.24 or newer is required by the native project.
- Internet access for the first pinned Manifold fetch and NuGet restore. Neither the original 3D Builder app nor REA is needed.
- Python 3 is optional for fixture generation and independent file inspection.

The scripts use paths relative to this repository, including paths containing spaces. Keep the `src/BuilderCrap` directory structure intact.

## Build

From the repository root:

```powershell
pwsh -NoProfile -File scripts/Build-GeometryWorker.ps1
dotnet build src/BuilderCrap/Builder.slnx
dotnet run --no-build --project src/BuilderCrap/Builder.Desktop
```

The native script locates `cmake` on PATH or through Visual Studio's `vswhere`. To select it explicitly:

```powershell
pwsh -NoProfile -File scripts/Build-GeometryWorker.ps1 -CMakePath 'C:\path\to\cmake.exe'
```

CMake fetches the exact Manifold commit in `Builder.GeometryWorker/CMakeLists.txt` into ignored `.local/geometry-build/BuilderCrap/`. The worker, fault probe and notices are built under ignored `bin/` directories. Managed executable builds copy the worker and its licenses beside their outputs. Build native code first; a managed build alone does not create the worker.

The solution contains Core, Desktop, Windows adapters, MCP and the three check projects. The optional benchmark/performance/trace projects are retained source tools outside the normal solution; they are not required to run the editor.

## Run and check

The Debug app is `src/BuilderCrap/Builder.Desktop/bin/Debug/net10.0-windows/Builder.exe`. The SDK includes the runtime for development. A packaged installation includes a private runtime and does not require an SDK.

```powershell
python scripts/generate_fixtures.py
python scripts/generate_failure_fixtures.py
dotnet run --no-build --project src/BuilderCrap/Builder.Checks -- . --primitives-only
```

Open `fixtures/generated/cube-20mm.stl` in millimeters for a small sample. [More checks and fixture requirements](verification.md), [Windows packaging](windows-packaging.md), [MCP setup](mcp-access.md).

## Dependencies and source publication

Do not commit the dependency checkout, compiler, runtime, native binaries or generated installers. `.gitignore` excludes them. The original app code is [MIT](../LICENSE); builds copy it as `LICENSE.txt` alongside outputs. Preserve it and the [dependency notices](../THIRD-PARTY-NOTICES.md) when distributing built files. There are no developer-specific NuGet credentials or private package feeds in this source export.
