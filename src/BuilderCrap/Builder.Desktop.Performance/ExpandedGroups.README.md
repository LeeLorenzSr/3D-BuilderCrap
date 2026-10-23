# Expanded-group CPU and layout profile

Historical diagnostic protocol: original frozen binaries and reports are not included in this public source export. Supply compatible inputs/assemblies and review runner paths before using this optional harness.


This caller is separate from the prior `Program.cs` and `ScopeProgram.cs`; their reports remain historical. It references explicit frozen or candidate Release binaries without a product project reference. Run builds/checks and other timing processes separately.

```powershell
./src/BuilderCrap/Builder.Desktop.Performance/Run-ExpandedGroups.ps1 -Role baseline -BinaryDirectory .local/evidence/expanded-groups-20261019/baseline-binaries
./src/BuilderCrap/Builder.Desktop.Performance/Run-ExpandedGroups.ps1 -Role candidate -BinaryDirectory src/BuilderCrap/Builder.Desktop/bin/Release/net10.0-windows
./src/BuilderCrap/Builder.Desktop.Performance/Compare-ExpandedGroups.ps1 -EvidenceRoot .local/evidence/expanded-groups-20261019
```

Each role runs three fresh processes on the same combined 1,000-object/100,000-triangle input. The root content is explicitly detached from its never-shown Window, so manual 1280×780 DIP layout remains authoritative through dispatcher work. Empty-window controls settle before Open, matching the normal application setup and preventing initial tab selection from starting a transform tool on the imported group. Empty setup, Open and first expanded document layout have separate cold measurements.

Every gated transition measures the actual handler, forced layout, a dispatcher Background drain and final forced layout. This includes deferred scroll/focus restoration through LayoutUpdated. The group stays expanded. Logical scene nodes, actual nonzero list dimensions, all realized item containers across nested/flat controls, complete visual-node count and scroll metrics are recorded. The last member must be visibly reachable; its scene-scope row selects the whole group. Thirteen Enter/Done pairs record final-member visibility and scene-scroll restoration after settlement. Baseline behavior failures are retained; candidate behavior must pass. Ten scoped member Preview/Apply/Undo sequences verify exact accepted content, path, dirty state, group identity, scope, selection and all 999 sibling references. First interaction is separate from the remaining nine warm samples.

The existing 50 ms CPU/layout budget applies to first expanded scene layout, first/warm Done and warm member Apply/Undo. Reports retain min/median/p95/max, per-process p95 and managed allocations. Threshold misses remain in the report. Detached content does not own native keyboard focus, so focus continuation requires separate shown-window/native checks. This does not measure native input, GPU/frame presentation, raster throughput, peak/native memory or broad real-world geometry performance. Source/project, input, measured binary and report SHA-256 values are pinned by the comparison script. Fatal setup diagnostics remain under `.local/evidence/expanded-groups-20261019/diagnostic/`; they are excluded from the final six-process comparison.

Cold gates use the maximum first-use sample. Warm gates require both the pooled p95 and worst-process p95 to meet the same 50 ms threshold; percentiles use linear interpolation at `p * (n - 1)`. The runner rejects an existing `scope-01.json`, `scope-02.json` or `scope-03.json` before building. For a later measurement, pass a new `-EvidenceRoot` to both roles and the comparer. Never overwrite a completed candidate to replace a miss.

The authoritative final paired report is `.local/evidence/expanded-groups-20261019/final-measurement/expanded-groups-comparison.json`, with interpretation in `report.md`. Its original baseline reports are copied byte-for-byte rather than rerun. Final Desktop `154F83F6...4CBD94` realizes at most 7 containers/91 visual nodes and passes all 78 settled transition observations. Cold initial layout remains 50.96-60.05 ms; warm Done median/p95/max is 18.95/75.42/79.21 ms. Both corresponding 50 ms gates fail. Warm member Apply and Undo pass. The original first candidate, diagnostic-only processes and pre-tightening comparison snapshots remain separately retained. See the main [README.md](README.md) for complete metrics and limitations.
