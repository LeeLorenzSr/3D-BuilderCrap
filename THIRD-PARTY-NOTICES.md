# Third-party notices

The original 3D BuilderCrap source and artwork are MIT licensed. That license does not replace the terms of the following dependencies.

## Geometry

The native worker builds against **Manifold 3.5.4**, pinned to commit `ce50d78021d64507f89e8c9fc2c2e51018117857`, under Apache-2.0. It is fetched during the native build, not vendored in this repository. The build copies the upstream LICENSE and AUTHORS files beside the worker. Preserve them in executable distributions. The geometry configuration disables optional external 2D and parallel dependencies. [Detailed notice](src/BuilderCrap/THIRD-PARTY-NOTICES.md).

## MCP and managed runtime

**ModelContextProtocol.Core 2.3.0** is restored through NuGet with a committed dependency lock. Its complete [upstream license and transition notice](src/BuilderCrap/Builder.Mcp/licenses/ModelContextProtocol-LICENSE.txt) is retained. Microsoft.Extensions dependencies and .NET/Windows Desktop runtime files retain their upstream terms. The packaging script collects their license/notice files into the generated distribution.

## Installer

**Inno Setup 7.1.0** is a separately installed build tool. The packaging script requires that version and includes its complete LICENSE.TXT in generated payloads. Inno Setup binaries are not included in this source repository.

## Other development tools

Optional Python verification dependencies are listed in `tools/verification-requirements.txt` and retain their upstream licenses. No font binaries, icon libraries, reference application binaries, decompiled Microsoft implementation, research tool checkout or downloaded toolchain is included.
