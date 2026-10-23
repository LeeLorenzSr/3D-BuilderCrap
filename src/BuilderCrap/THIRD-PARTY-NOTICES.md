# Geometry backend

Builder's geometry worker statically links Manifold 3.5.4, copyright The Manifold Authors, under Apache License 2.0.

- Source: https://github.com/elalish/manifold
- Pinned commit: `ce50d78021d64507f89e8c9fc2c2e51018117857`
- Upstream source is unchanged; Builder's process adapter is separate original code.
- `scripts/Build-GeometryWorker.ps1` copies the upstream `LICENSE` and `AUTHORS` into the worker's `licenses/` directory. Managed executable builds preserve that directory beside the worker. Include it when redistributing the executable.
- The used configuration disables external 2D CrossSection and parallel dependencies. Manifold's bundled linear algebra header acknowledges its public-domain origin in Sterling Orsten's linalg library.

No Microsoft 3D Builder code, resources or binaries are included.

# MCP adapter

`Builder.Mcp` references the official **ModelContextProtocol.Core 2.3.0** NuGet package, copyright Model Context Protocol a Series of LF Projects, LLC. NuGet declares Apache-2.0; the upstream license also records the ongoing MIT-to-Apache transition and retains applicable MIT terms. The complete upstream license is included at `Builder.Mcp/licenses/ModelContextProtocol-LICENSE.txt` and copied beside managed outputs. Preserve it when redistributing.

- Source: https://github.com/modelcontextprotocol/csharp-sdk
- Package commit: `9a327829ef066c44a0d737c58bf3c7617d7c701c`
- Exact dependency resolution is recorded in `Builder.Mcp/packages.lock.json`. Its Microsoft.Extensions dependency packages retain their own package notices/licenses; include those with the eventual distributable. The successor's tool/desktop/pipe adapters are separate original code.
