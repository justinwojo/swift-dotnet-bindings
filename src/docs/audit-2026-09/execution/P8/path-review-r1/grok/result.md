I'll review the exact P8 SDK path slice from the scope packet, then inspect the three producers, design docs, and evidence without mutating anything.Scope packet lists three files at `82ef2b43`. I'll fingerprint them, locate the P8 design docs, and inspect the diff next.Design docs live under `src/docs/audit-2026-09/execution/P8`. I'll fingerprint the three files and inspect the producers plus the plan next.SHA256 matches the packet. Next I’ll read the design, the three producers’ control flow, and the remaining path-construction sites.## Paired review — P8 SDK path slice

**Scope:** uncommitted working-tree slice vs `HEAD` `82ef2b43827e782dd2f6e010f19617452c75df5a`  
**Packet:** `/private/tmp/paired-p8-path-nnzclmqb/scope.diff` + `scope.json`  
**Snapshot:** SHA-256 of all three scoped files matches the packet (frozen).  
**Areas:** inspected directly (three-file local change; no subagents).  
**Status:** **complete**

Authoritative plan: `src/docs/audit-2026-09/execution-plan.md` revision 2 + `execution-plan-amendments.md` A12. Design/evidence: `src/docs/audit-2026-09/execution/P8/path-design.md`, `path-validation.md`, `path-golden-r1/receipt.json`. W9-H1 resource/package readiness was treated as out of scope.

---

### Findings

No confirmed Critical / High / Medium / Low defects in this slice.

The three W9-01 producers now use the same two-argument `Path.GetFullPath(candidate, MSBuildProjectDirectory)` authority already used for binding-report paths. That preserves an already-absolute `_SwiftBindingIntermediateDir` (custom absolute output and `UseArtifactsOutput`) and resolves a relative one against the **declaring** binding project.

```2835:2836:src/Swift.Bindings.Sdk/Sdk/Sdk.targets
      <_SwiftBindingFrameworkSearchPath Include="$([System.IO.Path]::GetFullPath('$(_SwiftBindingIntermediateDir)$(_SwiftBindingWrapperModuleName).xcframework', '$(MSBuildProjectDirectory)'))"
                                        Condition="'$(_SwiftBindingHasWrapperXCFramework)' == 'True'" />
```

```3650:3653:src/Swift.Bindings.Sdk/Sdk/Sdk.targets
      <_SwiftBindingNativeManifest Include="$([System.IO.Path]::GetFullPath('$(_SwiftBindingIntermediateDir)$(_GNM_WrapperModule).xcframework', '$(MSBuildProjectDirectory)'))"
                                   Condition="'$(SwiftFrameworkType)' != 'ObjC'
                                              AND '$(_GNM_HasWrapper)' == 'True'
                                              AND Exists('$(_SwiftBindingIntermediateDir)$(_GNM_WrapperModule).xcframework')">
```

`GetNativeManifest` still returns those items as `Returns="@(_SwiftBindingNativeManifest)"` for the workload `ResolveProjectReferences` query (path c). `_CompileSwiftWrapper` still consumes `GetSwiftFrameworkSearchPaths` as `TargetOutputs` → `--framework-dependency "%(Identity)"`. Evaluation is inside the targets (not an eager property), which matches the design constraint that tests may override `_SwiftBindingIntermediateDir` after SDK import.

Strengthened tests assert exact path equality + `Directory.Exists` (not a filename substring). Golden control: same compiled tests vs `HEAD` `Sdk.targets` failed absolute + artifacts and passed relative; corrected targets 3/3; `path-golden-r1/receipt.json` pins `current.targets` to the same SHA-256 as the working tree and `restored_exactly: true`.

---

### Candidate dispositions

| Candidate | Disposition | Reason |
|---|---|---|
| `GetSwiftFrameworkSearchPaths` still exports `%(SwiftFramework.Identity)` without `GetFullPath` (`Sdk.targets:2834`) | **Drop** | Pre-existing; not the W9-01 concatenation. Design excludes source/dependency entries. Auto-discover uses `find "$(MSBuildProjectDirectory)"` (absolute). This change does not newly expose it. |
| `GetNativeManifest` source/dependency items still use one-arg `GetFullPath('%(Identity)')` (`Sdk.targets:3643`, `3668`) — cwd, not `MSBuildProjectDirectory` | **Drop** | Unchanged lines. Design treats them as already-absolute transforms, not the faulty prefix. Relative `SwiftFramework` Include across a consumer cwd is a separate pre-existing shape. |
| `_ResolveSwiftNativeReferences` still includes `$(_SwiftBindingIntermediateDir)…xcframework` with no `GetFullPath` (`Sdk.targets:3514`, `3520`, `3564`, `3569`) | **Drop** | SDK-local `NativeReference` in the declaring project; design explicitly leaves these unchanged. Not a ProjectReference export. |
| Search-path theory omits `artifacts`; ProjectReference consumer only uses `absolute`; search dump runs on the producer, not a second project | **Drop** | Design matrix requires abs/rel for search and a consumer query for wrapper/bridge. Absolute is the previously-corrupt case. Producer `Returns=` identities are what the MSBuild task forwards; `Contains(Path.GetFullPath(wrapper))` fails if the identity is still relative (testhost cwd ≠ fixture). GetNativeManifest artifacts already covers SDK-derived absolute `IntermediateOutputPath` with the same `GetFullPath` + `_SwiftBindingIntermediateDir`. |
| `SdkPropsTargetsTests` pins only the search-path `GetFullPath` string, not the two `GetNativeManifest` expressions | **Drop** | Source-string test is a regression pin for producer 1. Producers 2–3 are gated by actual-target equality tests. A12 asked to strengthen the behavior test, which was done. |
| Search-path `Directory.Exists(fixture.WrapperPath)` is tautological (the fixture created that directory) | **Drop** | Load-bearing assert is `Assert.Contains(exact full wrapper path, searchPaths)`. Existence of a prefixed corrupt path would fail that Contains. |
| Missing native resource / PackageReference / Bundle.module readiness | **Drop** | Caller + design: W9-H1 is a future P8 slice. Not a missing path implementation. |
| `Exists('$(_SwiftBindingIntermediateDir)$(_GNM_WrapperModule).xcframework')` left un-normalized | **Drop** | W9-01’s correct half: Exists already succeeded for absolute intermediates; Include was the corrupt prefix. Unchanged on purpose. |

---

### Coverage

**Inspected**
- `src/Swift.Bindings.Sdk/Sdk/Sdk.targets` — `GetSwiftFrameworkSearchPaths` (`:2804–2842`), `_CompileSwiftWrapper` consumption (`:3048–3082`), `GetNativeManifest` (`:3597–3686`), `_SwiftBindingIntermediateDir` (`:48–49`), remaining `MSBuildProjectDirectory` / `GetFullPath` sites
- `SdkTargetsBehaviorTests.cs` — exact-path theory, search-path theory, ProjectReference consumer, `RunGetNativeManifestDump`
- `SdkPropsTargetsTests.cs` — search-path source pin
- Design, revision-2 P8 row, A12, W9-01 write-up, golden receipt + old/current logs

**Not re-executed** (caller: no builds/generation/shell mutation): `nuke test-r7` and the golden `dotnet test` swap. Hashes and logs were read, not replayed.

**Skipped by scope:** resource graph, pack items, `_ResolveSwiftNativeReferences`, other packages except the `_CompileSwiftWrapper` / `ResolveProjectReferences` interactions above.

No coverage gap that would turn “no confirmed findings” into an incomplete review.