# Builder.Mcp

Local MCP adapter over an existing `Builder.Core.EditorSession`. The official pinned MCP SDK owns protocol transport; this library owns typed tools, connection preview ownership, progress/cancellation and opt-in Windows pipe attachment. Core remains the only scene/selection/history/file transaction owner.

`Builder.Mcp.Server` provides standard stdio in explicit `--headless` or `--attach <desktop-pipe>` mode. `Builder.Desktop` exposes View → MCP access with per-run Enable/Stop and visible remote preview Apply/Cancel. Access is disabled at launch; enabled trusted clients have current-user supported-model/image file access, without a folder sandbox. No HTTP/TCP listener or global configuration changes.

[Setup, tool contract and local access rules](../../../docs/mcp-access.md). [Architecture](../../../docs/architecture.md). The solution builds the console launcher into desktop output. Build the native worker first if it is absent. Do not add UI-only or transport-only document edit paths here.
