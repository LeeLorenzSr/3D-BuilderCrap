# 3D BuilderCrap development

Read README.md, docs/build.md, docs/architecture.md and docs/verification.md before extending the application.

Prioritize approachable 3D-printing workflows and useful features over numerical or performance campaigns. Practical 0.001 mm resolution is sufficient for the current product. Keep known limitations concise and honest.

Builder.Core.EditorSession owns accepted edits, selection, preview ownership, history, document/file operations and recovery. Desktop and MCP must use the same session and transaction rules. Keep geometry independent of WPF and keep camera/presentation separate from mesh edits. Failed or cancelled work must preserve the last accepted scene and last-good files. Use stable object identity and explicit units.

Build native code through scripts/Build-GeometryWorker.ps1 and installable payloads through scripts/Build-WindowsPackage.ps1. Keep pinned upstream dependencies and their notices. Never terminate running editors or recursively delete an installation during maintenance.

Use original source, synthetic fixtures and appropriately licensed dependencies. Do not add Microsoft's original binaries, resources or decompiled implementation. Keep downloaded dependencies, generated binaries, private paths, credentials and research captures out of Git. Original application code is MIT; third-party licenses remain separate.

For normal checks use the documented Core modes. The normal desktop smoke mode is --normal-workflow-only with a fresh --evidence-root; its required historical fixture tree is described in docs/verification.md. Do not substitute --v1-usability-only, which includes accessibility/scaled-render checks. Do not alter Windows settings or start accessibility/DPI testing without a new explicit request.

Report actual checks and their limits. A build does not prove a native UI workflow, and a closed mesh alone does not prove repair parity or printability.
