# Optional WPF latency diagnosis

Retained diagnostic source outside Builder.slnx. This tool references a frozen Desktop/Core output directory through the `BuilderBinaryDirectory` MSBuild property. Its runners and comparisons were written for a specific historical capture schedule.

Original captures, source snapshots and frozen binaries are not part of this public repository. Build or supply your own matching assemblies and generated fixtures, review the runner paths, and use a fresh evidence directory. Old comparison hashes and timing gates in the source are historical configuration, not current verification claims.

This detached-content harness does not establish native mouse/keyboard behavior or physical display latency. It is not part of the normal editor build. See [fresh-checkout checks](../../../docs/verification.md).
