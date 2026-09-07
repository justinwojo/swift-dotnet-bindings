## Review — P8 SDK path readiness slice

Scope packet `/private/tmp/paired-p8-path-nnzclmqb/` at HEAD `82ef2b43`; three files, hashes match the working tree I read. Design/evidence read: `src/docs/audit-2026-09/execution/P8/path-design.md`, `path-validation.md`, `path-golden-r1/receipt.json` + `old.log`.

**Verdict on the core change: correct and complete for the pattern it targets.** I independently enumerated the faulty concatenation across `src/Swift.Bindings.Sdk/Sdk/` (props + targets) and confirmed exactly three sites existed (`path-golden-r1/old.targets:2835,3650,3659`) and zero remain — the only surviving `$(MSBuildProjectDirectory)` uses in the SDK are `:166` (find root), `:749`, `:2096`, `:2104` (already the same 2-arg `GetFullPath` authority) and `:4135` (README `Exists`). No fourth producer exists in generator C# (`src/Swift.Bindings/src` has zero references to `_SwiftBindingIntermediateDir`/`MSBuildProjectDirectory`), so the emitted NuGet/ProjectReference `.targets` are not a hidden instance. `Path.GetFullPath(candidate, MSBuildProjectDirectory)` preserves an absolute candidate and resolves a relative one against the producer, which is the stated contract. Per the packet, I make no claim about the P8 resource slice.

---

### P2 (Medium) — `GetSwiftFrameworkSearchPaths` still exports the *source* xcframework un-normalized, in the same target, same category

`src/Swift.Bindings.Sdk/Sdk/Sdk.targets:2834`:

```
      <_SwiftBindingFrameworkSearchPath Include="%(SwiftFramework.Identity)" />
```

Its sibling in the other fixed producer does normalize — `Sdk.targets:3643`:

```
      <_SwiftBindingNativeManifest Include="$([System.IO.Path]::GetFullPath('%(SwiftFramework.Identity)'))"
```

`path-design.md:19` asserts "Source and declared dependency entries already use absolute-path transforms and are not instances of the faulty concatenation." That is true of `SwiftFrameworkDependency` (`:2837` uses `->'%(FullPath)'`) and of the manifest's source entry, but **not** of `:2834`, which emits `Identity` verbatim across an MSBuild project boundary.

Triggering condition: a producer binding project declares a relative source, e.g. `<SwiftFramework Include="../shared/Lib.xcframework" />`, and another binding project `ProjectReference`s it. The consumer receives the raw relative string at `Sdk.targets:3054/3062` (`_ResolvedDepXCFramework`) and passes it through verbatim at `Sdk.targets:3082`:

```
      <_SwiftWrapperCmd Condition="'@(_ResolvedDepXCFramework)' != ''">$(_SwiftWrapperCmd) @(_ResolvedDepXCFramework->Distinct()->' --framework-dependency "%(Identity)"', '')</_SwiftWrapperCmd>
```

which becomes a `-F` path resolved in the *consumer's* working directory — the exact failure the fix exists to remove ("no such module '<Dep>'" at wrapper compile).

Impact/severity calibration: **pre-existing, not introduced by this change**, which is why it is Medium and not High. Reachability is real but not exercised in-repo: every in-repo `<SwiftFramework Include=...>` is absolute (build fixtures, `SdkTargetsBehaviorTests`), while auto-discovery is absolute by construction (`Sdk.targets:166`). The relative form is nonetheless the one advertised to users — `Sdk.targets:174` ("add `<SwiftFramework Include="path/to/Library.xcframework" />`") and `src/Swift.Bindings.Templates/content/swift-binding/.template.config/template.json:70`. The new test cannot catch it: `GetSwiftFrameworkSearchPaths_WrapperPathIsExactAndExists` only asserts `Assert.Contains(...WrapperPath, searchPaths)` and never inspects the source entry. I am reporting it because the design doc's inventory explicitly (and inaccurately) clears this site, so it will otherwise be recorded as "category closed."

### P2 (Medium) — evidence gap: producer #1 has no red-on-old-targets demonstration, and its only source-content pin was relaxed in the same commit

`path-golden-r1/receipt.json:16` shows the golden control's filter:

```
    "FullyQualifiedName~GetNativeManifest_SourceDroppedWithWrapperMetadataTrue_FlowsExactExistingPaths"
```

and `old.log:31`:

```
Failed!  - Failed:     2, Passed:     1, Skipped:     0, Total:     3, Duration: 1 s - Swift.Bindings.Unit.Tests.dll (net10.0)
```

So the old-vs-corrected golden covers **only** the two `GetNativeManifest` producers (wrapper + bridge). The third changed producer — `GetSwiftFrameworkSearchPaths` at `Sdk.targets:2835` — is covered by two artifacts that both moved in this same change: the new behavioral `GetSwiftFrameworkSearchPaths_WrapperPathIsExactAndExists` (`SdkTargetsBehaviorTests.cs:2836`) and the rewritten literal pin `SdkPropsTargetsTests.cs:1159`. Neither was run against the old targets, so neither is demonstrated to fail on the pre-fix source; the literal pin in particular was simply retargeted from the old string to the new one, which by construction cannot witness the fix.

Current impact: the search-path producer is *believed* fixed on the same reasoning as the other two (I read the target and agree the reasoning holds), but the regression-detector claim for it is unproven, and `path-validation.md:5` ("Three actual-target cases cover absolute, relative and artifacts") reads as if the golden covered all three producers when it covered three *modes* of one producer. Minimal reproduction shape to close it: re-run the existing `run-path-golden.py` swap with the filter widened to `GetSwiftFrameworkSearchPaths_WrapperPathIsExactAndExists` and confirm the `absolute` case goes red on `old.targets`.

### P3 (Low) — the search-path test normalizes producer output before asserting exactness, and its second assertion is tautological

`src/Swift.Bindings/tests/UnitTests/SdkTests/SdkTargetsBehaviorTests.cs:2848-2853`:

```csharp
            var searchPaths = File.ReadAllLines(fixture.SearchManifestPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .ToArray();
            Assert.Contains(Path.GetFullPath(fixture.WrapperPath), searchPaths);
            Assert.True(Directory.Exists(Path.GetFullPath(fixture.WrapperPath)));
```

`Select(Path.GetFullPath)` re-resolves any relative producer output against the *test process's* current directory before comparison, so the test does not assert that the producer emitted an absolute path — only that it emitted something that normalizes to the wrapper path from the test's CWD. The sibling manifest test deliberately does the opposite (`:2818-2821` compares `result.ManifestPaths` raw), which is the stronger and stated form ("Returned item equals the wrapper directory's normalized full path", `path-design.md:31`). Practical masking risk is small — the corrupt double-prefix `/…/GetManifest.Swift.iOS//…/absolute-obj/…` normalizes to a still-mismatching path — so this is Low, not a correctness defect. Line `:2853` asserts the existence of a directory the fixture itself created (`SdkTargetsBehaviorTests.cs:3986-3987`, `Directory.CreateDirectory(wrapperPath)`), so it constrains nothing about the target under test; the manifest test's equivalent (`:2822`, `Assert.All(result.ManifestPaths, …)`) checks the *returned* paths and is the meaningful form.

### P3 (Low) — the `relative` × ProjectReference-consumer cell is uncovered, and `Exists()` guards keep a different resolution basis than the `Include`

`GetNativeManifest_ProjectReferenceConsumerReceivesExactExistingPaths` (`SdkTargetsBehaviorTests.cs:2861-2863`) runs only `intermediateMode: "absolute"`. That is the right choice for demonstrating the bug, but it leaves the relative form unexercised across a real project boundary — and in that form the emitted `Include` and its own gate no longer share a base. `Sdk.targets:3650-3653`:

```
      <_SwiftBindingNativeManifest Include="$([System.IO.Path]::GetFullPath('$(_SwiftBindingIntermediateDir)$(_GNM_WrapperModule).xcframework', '$(MSBuildProjectDirectory)'))"
                                   Condition="'$(SwiftFrameworkType)' != 'ObjC'
                                              AND '$(_GNM_HasWrapper)' == 'True'
                                              AND Exists('$(_SwiftBindingIntermediateDir)$(_GNM_WrapperModule).xcframework')">
```

The `Include` resolves against `MSBuildProjectDirectory`; the `Exists()` resolves a relative path against the build's current directory. In practice MSBuild sets the current directory to the project being built, so these agree — and this asymmetry is unchanged by the diff (the old code had the identical split). Rated Low as unchanged latent + a coverage note, not a defect of this change.

### Info — 2-arg `Path.GetFullPath` requires .NET Core-era MSBuild

`Path.GetFullPath(string, string)` does not exist on .NET Framework, so the new expressions would fail evaluation under `MSBuild.exe`. Not actionable: the SDK is macOS/Apple-TFM-only (`SWIFTBIND010`, `Sdk.targets:144`), shells out to `find`/`xcrun`, and the identical 2-arg call already shipped at `Sdk.targets:2096` and `:2104` before this change.

---

### Candidates inspected and dismissed (preserved with reasons)

- **`GetFullPath` throwing on empty/invalid input** (would be High if real). Dismissed: every argument is a property expansion suffixed with the literal `.xcframework`, so it can never be empty/whitespace; `MSBuildProjectDirectory` is always fully qualified; and MSBuild evaluates an in-target item element's `Condition` before expanding its `Include`, so the expression is not evaluated when `_GNM_HasWrapper`/`_SwiftBindingHasWrapperXCFramework` is unset.
- **Normalization changing behavior in the relative case** (regression candidate). Dismissed: old `$(MSBuildProjectDirectory)/` + relative intermediate and new `GetFullPath` produce the same absolute path on macOS; the golden's `relative` case passing on both old and corrected targets (`old.log:31`, 1 passed) is consistent with this.
- **Other cross-boundary `Returns=` targets.** `GetSwiftTransitiveFrameworkDependencies` (`Sdk.targets:2905-2908`) uses `->'%(FullPath)'` and `_AutoFrameworkDependency` (populated by absolute `find` output); `GetSwiftObjCCompanionAssembly` (`:2927`) returns `GetTargetPath` output. Neither is an instance.
- **`GetSwiftFrameworkSearchPaths` exports no bridge xcframework while `GetNativeManifest` does.** Not a path defect; the bridge is a generated SwiftUI shim not imported by a dependency's `.swiftinterface`, so it has no role on a dependent's wrapper `-F` path. Dropped as by-design.
- **Intra-project `NativeReference` items left relative** (`Sdk.targets:3564,3569`). Explicitly scoped out by `path-design.md:19` (does not cross a project boundary) and unchanged in exposure by this diff.
- **9-element tuple return from `RunGetNativeManifestDump`** (`SdkTargetsBehaviorTests.cs:3897-3899`) and the added second `dotnet msbuild` invocation (`:3990`, `-t:TestIntermediate`). Style/cost only; the extra invocation runs a dependency-free target so no SDK pipeline is triggered. No finding.
- **`Assert.DoesNotContain(Path.GetFullPath(result.SourcePath), result.ManifestPaths)`** (`:2824`) — checked overload resolution: binds the generic `IEnumerable<T>` form (exact equality), not the substring form. Correct as written.

### Coverage

Reviewed: all three changed files in full context; `GetSwiftFrameworkSearchPaths`, `GetNativeManifest`, `_CompileSwiftWrapper` consumption, `_ResolveSwiftNativeReferences`, `_DiscoverSwiftFrameworks`, `_ImportSwiftBindingMetadata`; exhaustive SDK-wide grep for the concat pattern and for `Returns=` cross-project exports; generator C# checked for a fourth producer; design + validation docs and the golden receipt/logs.

Not executed (packet constraint, no edits/builds/runs): I did not run the unit tests, MSBuild, or the golden script, so the reported pass counts (17,998 / 79 / 888) are taken as given and not independently verified; the `old.log`/`receipt.json` filter scope above is the one evidence claim I checked directly. Not reviewed: the P8 resource/package slice (out of scope by instruction) and any workload-side `ResolveProjectReferences` behavior beyond what the fixture's direct `MSBuild` task invocation models.

Review is complete for the requested scope. The two Medium findings are the only items I would not close out silently: one is a genuine unfixed instance of the same category the design doc records as closed, the other is a gap between what the golden control proves and what `path-validation.md` reads as proving.