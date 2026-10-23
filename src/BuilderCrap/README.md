# Application source

3D BuilderCrap **1.0.2**, an independent C#/.NET/WPF mesh editor for 3D printing. This is the maintained application source tree. Internal project names and the installed `Builder.exe` identifier remain stable.

From this directory, after building the native worker from the repository root:

```powershell
dotnet build Builder.slnx
dotnet run --no-build --project Builder.Desktop
```

The solution contains Core, Desktop, Windows font/image adapters, MCP and the check runners. Core owns all accepted editing and file transactions; Desktop and MCP use the same EditorSession. Failed or cancelled operations preserve accepted work.

[Build prerequisites](../../docs/build.md) · [Architecture](../../docs/architecture.md) · [Checks](../../docs/verification.md) · [MCP setup](../../docs/mcp-access.md) · [Windows packaging](../../docs/windows-packaging.md) · [Dependency notices](THIRD-PARTY-NOTICES.md)

The optional benchmark/performance/trace projects are retained outside the main solution. Their historical captures and input binaries are not included. Generated files and local reports belong in ignored directories.
