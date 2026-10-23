# Optional Core performance probes

Retained diagnostic source outside Builder.slnx. This console references a chosen Core DLL through the `BuilderCoreAssembly` MSBuild property, so it can compare frozen assemblies without rebuilding them.

The original measured binaries, timing reports and private research workspace are not included. Supply your own matching assembly/input tree and a fresh output directory. The program expects the synthetic performance fixture mode from `scripts/generate_fixtures.py --performance`. Inspect Program.cs and Compare.ps1 before using this historical harness.

This is not part of the normal build or verification workflow. See [supported fresh-checkout checks](../../../docs/verification.md).
