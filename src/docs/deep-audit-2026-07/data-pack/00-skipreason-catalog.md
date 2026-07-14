# Data Pack — SkipReason Catalog + BindingTests Corpus Metrics

**Date**: 2026-07-16  
**Mode**: Evidence extraction (no fixes)  
**Primary corpus**: `BindingTests/output/binding-report.json` + `binding-emission-report.json` (current tree artifacts)

---

## 1. BindingTests headline metrics (live artifact)

| Metric | Value |
|--------|------:|
| TotalTypes | 1361 |
| EmittedTypes | 1312 |
| SkippedTypes | 49 |
| TotalMembers | 4741 |
| EmittedMembers | 4364 |
| SkippedMembers | 263 |
| SkippedItems rows | 312 |
| **PublicSurfaceLost** (triage) | **296** |
| **ReviewCount** | **1** |
| wrapper_stripped_count (manifest) | **0** |

### SkipTriage by disposition

| Disposition | Count | Meaning |
|-------------|------:|---------|
| Recovered | 2 | CSM recovered open-generic loss |
| ExpectedNonPublic | 14 | Never public surface |
| ExpectedStructural | 107 | Correct-by-design prune |
| **KnownLimitation** | **188** | Documented gap, consumer-visible |
| **Review** | **1** | Unexplained — human look |

**Review item (only one):**  
`ListenerProxy` / `NestedProtoOuter.Listener` — `EveryProtocolConformanceSkipped` — *“no decision recorded”* (attribution gap — Review-tier, not silent).

### SkipTriage by reason (BindingTests)

| Count | Reason | Disposition (classifier) |
|------:|--------|--------------------------|
| 63 | SuppressedProxyMemberDegraded | KnownLimitation |
| 45 | SwiftUIView | ExpectedStructural |
| 42 | EveryProtocolConformanceSkipped | Review default / refined |
| 37 | UnsupportedSignature | KnownLimitation |
| 17 | UnsupportedType | KnownLimitation |
| 14 | AnyTypeFallback | KnownLimitation |
| 12 | UnsupportedExistential | KnownLimitation |
| 10 | ModuleInternal | ExpectedNonPublic |
| 10 | SynthesizedCodable | ExpectedStructural |
| 9 | UnsupportedClosure | KnownLimitation |
| 8 | GenericProtocolConstraint | KnownLimitation |
| 8 | Pattern2InternalTypeReach | ExpectedStructural |
| 7 | DuplicateSignature | KnownLimitation |
| 5 | GenericTypeCallback | KnownLimitation |
| 5 | NonBlittableCallConvSwift | KnownLimitation |
| 4 | AbsentFrameworkType | KnownLimitation |
| 4 | ParentModuleInternalNoFallback | ExpectedNonPublic |
| 3 | StaticProtocolMember | ExpectedStructural |
| 3 | SwiftUIConstraint | KnownLimitation |
| 3 | UnsatisfiedGenericConstraint | KnownLimitation |
| 1 each | ConstrainedExtensionWrapper, IndeterminateStructLayout, NetUnavailableType | KnownLimitation |

### Suppressed-proxy site tokens (from Details strings)

| Token | ~Count in Details | Product meaning |
|-------|------------------:|-----------------|
| produce-throw | 32 | Public getter/return **throws** (compile-but-dead class) |
| consume-degraded | ~28 | C#→Swift reverse set never fires for C# conformers |
| receiver-failfast | 3 | Reverse receiver fail-fast; slot kept for layout |

**G1 implication:** On BindingTests alone, **~63** rows are compile-but-degraded reverse surface — largest single KnownLimitation bucket, larger than UnsupportedSignature.

---

## 2. Complete SkipReason enum (38 active + retired retained)

Source: `src/Swift.Bindings/src/Reporting/BindingReport.cs`

| Reason | Disposition | Notes |
|--------|-------------|-------|
| UnsupportedType | KnownLimitation | |
| AnyTypeFallback | KnownLimitation | May be Recovered via CSM RecoveredBy |
| AsyncProperty | KnownLimitation | |
| SwiftUIConstraint | KnownLimitation | |
| CombineFramework | KnownLimitation | |
| GenericProtocolConstraint | KnownLimitation | |
| UnsatisfiedGenericConstraint | KnownLimitation | |
| UnsupportedSignature | KnownLimitation | |
| UnsupportedExistential | KnownLimitation | |
| UnsupportedClosure | KnownLimitation | |
| UnsupportedAsyncStream | KnownLimitation | |
| UnsupportedThrowingAsyncStream | KnownLimitation | **Retired** (bound now; enum retained) |
| DuplicateSignature | KnownLimitation | |
| MissingHandler | **Review** | |
| SwiftUIView | ExpectedStructural | Bridge path |
| StaticProtocolMember | ExpectedStructural | |
| GenericTypeCallback | KnownLimitation | |
| ActorIsolatedAsyncStream | KnownLimitation | |
| SynthesizedCodable | ExpectedStructural | |
| UnderscorePrefixInternal | ExpectedNonPublic | |
| ModuleInternal | ExpectedNonPublic | |
| ExtensionDefault | ExpectedStructural | |
| NonBlittableCallConvSwift | KnownLimitation | |
| EveryProtocolConformanceSkipped | **Review** (refined by Details) | |
| OwnedByAppleSupplement | ExpectedStructural | |
| IndeterminatePwtShape | KnownLimitation | |
| IndeterminateStructLayout | KnownLimitation | |
| AncestorSkipped | ExpectedStructural | |
| ActorIsolatedConstructor | KnownLimitation | |
| MissingWrapperSymbol | **Review** | Integrity/co-gate residual |
| ConstrainedExtensionWrapper | KnownLimitation | Planning-time honest skip |
| GenericEnumCaseConstructor | KnownLimitation | |
| SuppressedProxyMethodBody | ExpectedStructural | **Retired** |
| CovariantReturnNotRepresentable | KnownLimitation | |
| Pattern2InternalTypeReach | ExpectedStructural | |
| ParentModuleInternalNoFallback | ExpectedNonPublic | Async/closure/operator on internal parent |
| NetUnavailableType | KnownLimitation | |
| AbsentFrameworkType | KnownLimitation | |
| ObjCUnresolvableType | KnownLimitation | |
| ObjCUnsupportedConstruct | KnownLimitation | |
| ObjCDuplicateSignature | KnownLimitation | |
| ObjCVariadicFunction | KnownLimitation | |
| ObjCUnavailableApi | ExpectedStructural | |
| ObjCAccessibilityConflict | ExpectedStructural | |
| ObjCEmptyCategory | ExpectedStructural | |
| ObjCDuplicateSelector | ExpectedStructural | |
| ObjCMissingNativeSymbol | ExpectedStructural | |
| SuppressedProxyMemberDegraded | KnownLimitation | produce-throw / consume / failfast |
| Unknown | **Review** | |

Unmapped reasons default to **Review** (fail-safe) + unit test forces map completeness.

---

## 3. Emission-report skip *causes* (wrapper/planning vocabulary)

Different axis from SkipReason — `binding-emission-report.json` → `skipReasons`:

| Count | Cause key |
|------:|-----------|
| 63 | method_level_generics |
| 40 | generic_parent_type |
| 13 | closure_params |
| 10 | unsupported_generic_container |
| 9 | async |
| 9 | variadic_params |
| 6 | generic_parent_unresolved_pwt_constraint |
| 6 | generic_parent_metadata_buffer_mode |
| 6 | actor_isolated |
| 3 | inherited_generic_context |
| 3 | parent_module_internal |
| 2 | generic_parent |
| 2 | direct_closure_setter |
| 1 | module_internal / self_property / inout_abi_mismatch |

**Wrapper strategy mix (BindingTests):**  
CdeclProperty 1496 · CdeclMethod 1296 · NativeThunk 777 · CdeclConstructor 623 · None 123 · DirectCdecl 68 · CdeclSubscript 21  

**Other emission counters:** suppressedProxyClassCount **42**, silentTombstones **4**, csmConformerRejections **9**, degradedExistentials **6**, degradedReverseDispatchReceivers **3**.

### CSM rejection themes (9 rows)

- conformer fails non-selected protocol constraint (current-module ABI) ×3  
- method `init` where-clause adds `Sendable` unsatisfied ×3  
- Element : Collectionish / Element == Int / refinedLabel RefinableMark ×1 each  

---

## 4. Worker implications

1. **Largest consumer-visible loss class on test lib:** SuppressedProxyMemberDegraded (produce-throw first) — G1-003 fuel.  
2. **Review budget on BindingTests is almost clean (1)** — attribution mostly works; `ListenerProxy` “no decision recorded” is a small honesty bug class.  
3. **Two vocabularies:** SkipReason (report/triage) vs emission skipReasons (wrapper planning) — workers must not conflate.  
4. **Zero strip** on current BindingTests — post-processor clean for this corpus.  

---

## 5. PartialSuccessKitchen seed shapes (from real skip density)

Fixtures that should compile-clean with expected triage (for later workers):

| Shape | Expected reason bucket |
|-------|------------------------|
| SwiftUI View types | SwiftUIView structural |
| PAT / associated-type protocol reverse | EveryProtocolConformanceSkipped / SuppressedProxy* |
| Unsupported closure matrix remainder | UnsupportedClosure |
| Method-level generics on generic parent | method_level_generics / GenericProtocolConstraint |
| Variadic params | emission skip / UnsupportedSignature |
| Actor-isolated ctor/async stream | ActorIsolated* |
| Module-internal parent async/closure | ParentModuleInternalNoFallback |

---

*Next data-pack files: diagnostic encyclopedia, emit-then-break inventory, gates/CI.*
