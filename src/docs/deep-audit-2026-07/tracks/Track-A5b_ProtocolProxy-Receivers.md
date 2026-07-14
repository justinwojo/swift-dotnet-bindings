# Track A5b — ProtocolProxy Receivers + Keys

| Field | Value |
|-------|--------|
| **Wave** | 2 |
| **Track** | A5b |
| **Date** | 2026-07-15 |
| **Mode** | Read-only (no production edits) |
| **Risk rating** | **2 / 5** (Low) — AF05 CT axis, orphan-receiver raw-key dedup, and VtableLayout index source are implemented and fixture-gated; residual risk is fillability limits, legacy-blocking hazards, docs drift, and L4 dead/dual code |
| **Confidence** | **high** on CT / orphan-receiver / three-key-axis model; **medium** on unsampled arity>4 async reverse-dispatch and property-rename×fillability edges |
| **Lenses** | L1 (keys/slots/dispatch), L2 (fixtures), L3 (produce-throw / suppressed proxy), L4 (dead key + dual conversion families), L5 (constraints drift) |

---

## Headline

ProtocolProxy **receivers and projected keys are mature and largely closed** after AF05 Target D, the S20 orphan-receiver fix, and S13 Pillar C real-async witnesses:

| Subsystem | Verdict |
|-----------|---------|
| Projected C# key one-core (`BuildProjectedMethodKey`) | Sound; protocol path includes async CT (ruling b) |
| ProtocolHandler `emittedResolvedSignatures` CT append | Sound; same axis as projected key |
| Real-async receiver `default(CancellationToken)` | Sound; BindingTests + unit |
| **Legacy blocking** receiver CT (constraints.md “unfixtured”) | **Fixed and fixtured** — constraints / M0-A are **stale** |
| Orphan receivers for raw-key-collapsed existentials | Fixed (raw-signature `emittedRawKeys` on receivers + StaticInit) |
| Slot index source (`VtableLayout.MethodSlotIndexByKey`) | Sound; fillability ≠ layout |
| Suppressed-proxy receiver honesty | Improved (fail-fast stub + report row + CONSUME degrade) |
| Residual second-overload reverse-dispatch | **Intentional fillability null** (not layout bug) |

**No new emission-live P0/P1 compile or wrong-overload binding defect was confirmed open.** The track’s value is a **key/receiver map**, verification that prior AF05/S20 tails are closed, and residual L3/L4/L5 inventory.

---

## Key axes (do not conflate)

Reverse dispatch uses **three distinct key domains**. Mixing them is the historical crash/compile class (Bug #21 / FirebaseFirestore orphans / AF05 silent drop).

| Axis | Builder | Includes labels? | Async effect? | Used for |
|------|---------|------------------|---------------|----------|
| **Slot / layout** | `EveryProtocolEmitter.GetMethodKey` | **Yes** (`label:type`) | **Yes** (`:async`) | Vtable field index, `MethodSlotIndexByKey`, receiver `Receive_{name}_{idx}`, StaticInit assignment index |
| **Raw / fillability collapse** | `ProtocolSignatureHelper.GetMethodSignatureKey` via `ProtocolMethodDisambiguator.EffectiveRawKey` | No (except when disambiguated → slot key) | Yes (default) | Interface raw dedup, `_skippedMethodKeys`, receiver/static-init **raw-signature** collapse |
| **Projected C# overload** | `ProtocolSignatureHelper.BuildProjectedMethodKey` / `EffectiveProjectedKey` | No (name may be label-derived via disambiguator) | **CT param** when `IsAsync` | Interface projected dedup, receiver/static-init projected collapse, proxy InterfaceImpl dedup |
| **Forward SBW witness** | `WitnessDispatchEmitter.GetMethodKey` / `EffectiveWitnessSlotKey` | No (yes when disambiguated) | Yes | Forward proxy→Swift accessor index (InterfaceImpl), not reverse receivers |

**Dead fourth key (do not revive):** `ProtocolProxyEmitter.Helpers.GetMethodKey` — label-blind **and** async-blind; **zero call sites** in production or tests (see finding DA-W2-A5b-006).

Evidence:

```6393:6408:src/Swift.Bindings/src/Emitter/StringEmitter/EveryProtocolEmitter.cs
    internal static string GetMethodKey(MethodDecl method)
    {
        // ... labels + async ...
        var asyncSuffix = method.IsAsync ? ":async" : "";
        return method.Name + "(" + string.Join(",", method.CSSignature.Skip(1).Select(p =>
            (p.GetSwiftName() ?? p.Name) + ":" + (p.SwiftTypeSpec?.ToString() ?? ""))) + ")" + asyncSuffix;
    }
```

```2573:2581:src/Swift.Bindings/src/Emitter/StringEmitter/WitnessDispatchEmitter.cs
    internal static string GetMethodKey(MethodDecl method)
    {
        var asyncSuffix = method.IsAsync ? ":async" : "";
        return method.Name + "(" + string.Join(",", method.CSSignature.Skip(1).Select(p => p.SwiftTypeSpec?.ToString() ?? "")) + ")" + asyncSuffix;
    }
```

```177:180:src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.Helpers.cs
    internal static string GetMethodKey(MethodDecl method)
    {
        return method.Name + "(" + string.Join(",", method.CSSignature.Skip(1).Select(p => p.SwiftTypeSpec?.ToString() ?? "")) + ")";
    }
```

---

## Receiver emission map (`ProtocolProxyEmitter.Receivers.cs`, ~2745 LOC)

### Method receiver fan-out (`EmitMethodReceiver`)

| Shape | Gate | Emitter | Impl call CT? |
|-------|------|---------|----------------|
| Dispatchable closure-returning method | `IsDispatchableClosureReturningMethod` | dedicated | N/A |
| Dispatchable async-closure method | `IsDispatchableAsyncClosureMethod` | dedicated | N/A |
| Real-async witness (blittable return + ≤4 blittable params) | `EmitsRealAsyncWitness` | `EmitRealAsyncWitnessReceiver` | **Yes** `asyncImplArgs` + `default(CancellationToken)` |
| Legacy value / string / ObjC / collection (+ **legacy blocking async**) | fall-through | `EmitMethodReceiverBody` | **Yes** when `method.IsAsync` (`implCallArgs`) |

Real-async predicate is method-shape-only and rejects void returns, non-primitives, closures, generics, Self, inout, arity>4 (`EveryProtocolEmitter.cs:6441–6480`). Those async requirements stay on the **legacy blocking** path (Issue-1-class sync block + FailFast on escape).

### Fillability filters (methods) — leave Swift-kept slots **null**, never re-index

From `EmitReceiverMethods` (`Receivers.cs:63–132`) and mirrored StaticInit (`StaticInit.cs:273–314`):

1. **Layout**: `VtableLayoutBuilder.Build(...).MethodSlotIndexByKey` + `ProtocolVtableMembers.IncludesMethod`
2. **`_skippedMethodKeys`** via `EffectiveRawKey` (AnyType / gate-skipped / interface-dropped)
3. **Raw-signature collapse** (`emittedRawKeys`) — interface one-method-per-raw-key (existential AnyType pair)
4. **Projected-C# collapse** (`emittedCSharpKeys` via `EffectiveProjectedKey(..., propertyNames: null)`)

Comment at `Receivers.cs:69–80` documents: second collapsed overload **cannot reverse-dispatch today** — known fillability limitation, not layout corruption.

### Property / subscript receivers

- Value-shaped + dispatchable-closure-property special path
- Sibling property/method fan-out (`ProxyLifetimeTracker.ResolveImpl` / FailFast Design B2)
- Async **gated out** of method sibling fallback (`Receivers.cs:1103–1108`) — so `EmitMethodLookupHit` does **not** need `GetAwaiter().GetResult()` (sync-only). L5 hazard if async is later added without unwrap + CT.

### Suppressed-proxy degrade (L3)

| Site | Behavior | Report |
|------|----------|--------|
| Receiver body throws `SuppressedProxyReferenceException` | Checkpoint rollback → fail-fast UCO stub (keeps symbol for cctor `&Receive_*`) | `SuppressedProxyReporting.RecordReceiver` (receiver-failfast) |
| Getter/return CONSUME arm (silent drop of C# wrap) | Live body may still emit | `RecordReceiverGetterConsumeDegrade` → consume-degraded |
| Interface/proxy PRODUCE stub | `NotSupportedException` / SB0003 | produce-throw via same reporting helper |

Canonical site taxonomy: `src/Swift.Bindings/src/Reporting/SuppressedProxyReporting.cs`.

---

## AF05 ruling b — three coordinated sites (verify current code)

| # | Site | CT handling | Status |
|---|------|-------------|--------|
| 1 | `BuildProjectedMethodKey` async append | `paramTypes.Add("System.Threading.CancellationToken")` when `decl.IsAsync` (`ProtocolSignatureHelper.cs:287–295`) | **Live** |
| 2 | `ProtocolHandler.BuildEmittedSignature` | same append (`ProtocolHandler.cs:1459–1469`) | **Live** |
| 3a | Real-async receiver | `asyncImplArgs` with explicit default CT (`Receivers.cs:1550–1560`) | **Live + fixtures** |
| 3b | Legacy blocking receiver | `implCallArgs` with explicit default CT (`Receivers.cs:1343–1360`) | **Live + fixtures** (constraints.md still says incomplete — **stale**) |

BindingTests:

- `/Users/wojo/Dev/swift-bindings/BindingTests/Sources/SwiftBindingsTestLib/Protocols/KeyBuilderAsyncOverloadProtocol.swift` + `RuntimeTestsApp/Protocols/KeyBuilderAsyncOverloadProtocolTests.cs` (real-async)
- `KeyBuilderAsyncBlockingOverloadProtocol.swift` + `KeyBuilderAsyncBlockingOverloadProtocolTests.cs` (legacy blocking String return)
- Unit: `ProtocolProxyEmitterTests.EmitProxyClass_LegacyBlockingAsyncReceiver_WithSyncNamesake_BindsAsyncOverloadViaExplicitCancellationToken` (`ProtocolProxyEmitterTests.cs:2042–2098`)

---

## Findings

### Confirmed (open residual / inventory)

#### DA-W2-A5b-001: Collapsed-overload second slot is null (fillability, not layout)

- **Severity**: P2 (capability gap)  
- **Status**: confirmed (by design)  
- **Confidence**: high  
- **Lenses**: L1 (documented), L3  
- **Reachability**: emission-live (Firestore-class existential overload pairs; BindingTests `OverloadCollapseDispatch`)  
- **Claim**: When two Swift overloads collapse to one C# interface method (raw `GetMethodSignatureKey` / AnyType), layout still allocates **two** reverse slots (distinct `EveryProtocolEmitter.GetMethodKey`); fillability wires only the first receiver and leaves the second null. C# cannot reverse-dispatch the second overload.  
- **Evidence**: `Receivers.cs:69–80`, `115–126`; `StaticInit.cs:303–312`; unit `EmitProxyClass_TwoExistentialOverloadsSameRawKey_EmitsSingleReceiver` (`ProtocolProxyEmitterTests.cs:1641–1696`); Swift fixture `OverloadCollapseDispatch.swift`.  
- **Probe**: regenerate OverloadCollapse / Firestore-shaped pair; assert one `Receive_*` and two struct fields with second unassigned / null.  
- **Suggested fixture**: already present.  
- **Prior art**: BindingAudit / BSA EveryProtocol themes (partial); this is the **fillability** residual of the **fixed** orphan-receiver bug.

#### DA-W2-A5b-002: Dead `ProtocolProxyEmitter.Helpers.GetMethodKey` (async- and label-blind)

- **Severity**: P3  
- **Status**: simplification  
- **Confidence**: high  
- **Lenses**: L4, L5  
- **Reachability**: latent (unused)  
- **Claim**: A third-looking `GetMethodKey` on the proxy partial is unused and **wrong** relative to both live oracles (no labels, no `:async`). A future “cleanup” that routes receivers through it would re-open slot collapse.  
- **Evidence**: `ProtocolProxyEmitter.Helpers.cs:177–180`; repo-wide grep shows **no** call sites (all live walks use `EveryProtocolEmitter.GetMethodKey` / `WitnessDispatchEmitter.GetMethodKey`).  
- **Suggested simplification**: delete method (byte-identical); or replace body with `=> EveryProtocolEmitter.GetMethodKey(method)` if a single name is desired — **needs fixture** only if re-homed into a live walk.  
- **Do not do if**: any external tool reflection depends on the method (none found in-tree).

#### DA-W2-A5b-003: constraints.md / M0-A still claim legacy CT edge is open

- **Severity**: P3 (docs / AI hazard)  
- **Status**: confirmed (docs drift)  
- **Confidence**: high  
- **Lenses**: L5  
- **Claim**: `.claude/rules/constraints.md` overload-key trap and Wave-0 map still describe AF05 site 3 (legacy blocking receiver) as incomplete/unfixtured; production code + BindingTests closed it. Agents following constraints may re-implement or re-file a fixed bug.  
- **Evidence**:  
  - Live fix: `Receivers.cs:1343–1360`  
  - Fixture: `KeyBuilderAsyncBlockingOverloadProtocol*`  
  - Unit: `ProtocolProxyEmitterTests.cs:2042–2098`  
  - Stale map: `00-codebase-map` / prior-art AR open tails; `waves/W0-map/M0-A-generator-pipeline.md:403` “known incomplete CT edge”  
- **Probe**: re-read constraints trap paragraph vs Receivers implCallArgs.  
- **Prior art**: AR-SESS “AF05 legacy async receiver CT edge”; **re-tag as fixed**, do not re-chase as open P1.

#### DA-W2-A5b-004: Real-async excludes void / non-primitive / arity>4 → legacy blocking residual class

- **Severity**: P2 (runtime hazard when hit; not silent wrong ABI if CT fixed)  
- **Status**: confirmed (intentional narrow Phase-1 shape)  
- **Confidence**: high  
- **Lenses**: L1  
- **Reachability**: fixture-reachable / emission-live for String async reverse dispatch (`KeyBuilderAsyncBlockingOverloadProtocol`)  
- **Claim**: `EmitsRealAsyncWitness` returns false for empty-tuple returns and non-blittable returns; those reverse-async paths **block** the UCO thread (`GetAwaiter().GetResult()`), with documented deadlock risk under MainActor / pool starvation (`Receivers.cs:1329–1340`). Errors FailFast (no Swift error channel on legacy path).  
- **Evidence**: `EveryProtocolEmitter.cs:6457–6464` (void false); blocking path comments `Receivers.cs:1316–1340`; real-async path uses `AsyncClosureHelper.RunAsync*` instead.  
- **Suggested fixture**: already have String legacy pair; optional: arity-5 void async + MainActor re-entrancy stress (would document deadlock, not necessarily fix).  
- **Prior art**: S13 Pillar C design; Mono Issue 1 (blocked upstream for related shapes).

#### DA-W2-A5b-005: SB0003 / produce-throw / static stubs remain compile-but-dead public surface

- **Severity**: P2 (usability)  
- **Status**: already-known (BA/BSA EveryProtocol theme) + residual after honesty improvements  
- **Confidence**: high  
- **Lenses**: L3, L2  
- **Reachability**: emission-live on non-dispatchable / static / mixed-generic / inherited stub paths  
- **Claim**: Proxy InterfaceImpl still emits public members that always throw (`NotSupportedException`, SB0003 Obsolete) to satisfy CS0535. Suppressed-proxy **members** are now report-classified (`SuppressedProxyMemberDegraded` with produce-throw / consume-degraded / receiver-failfast), which is better honesty than silent dead surface — but SB0003 stubs remain discoverable APIs.  
- **Evidence**: `ProtocolProxyEmitter.InterfaceImpl.cs` SB0003 stubs (~953+, ~1308+, ~2811+); `SuppressedProxyReporting.cs:1–113`; BA-SUM “EveryProtocol proxy skips → compile-but-dead”.  
- **Prior art**: BA-SUM, BSA-05 — **do not re-discover as novel P0**.

#### DA-W2-A5b-006: Dual conversion visitor families (Accessor vs Receiver)

- **Severity**: P3  
- **Status**: simplification  
- **Confidence**: medium  
- **Lenses**: L4  
- **Claim**: Forward accessor conversion (`AccessorConversionVisitors.cs`) and reverse-receiver conversion (`ReceiverConversionVisitors.cs` + large private helpers in Receivers for optional/existential/containers) must stay projection-exhaustive (constraints: implement Visit on every `IProjectionVisitor`). Parallel arms already use exhaustive visitors for whole-value getter/setter/class-copy-out; existential/optional container helpers remain hand-written dual paths.  
- **Evidence**: `Handler/ReceiverConversionVisitors.cs`; `Handler/AccessorConversionVisitors.cs`; existential getters `Receivers.cs:2081–2152`.  
- **Suggested simplification**: extract shared optional/container expression builders only where expressions are byte-identical; **do not** merge forward vs reverse direction blindly (ownership +1 vs borrow differs).  
- **Risk class**: needs fixture (optional ObjC-bridgeable, collection existential).

#### DA-W2-A5b-007: `propertyNames: null` on fillability projected keys vs property-aware interface names

- **Severity**: P3  
- **Status**: candidate (intentional with safety nets; residual re-collision path)  
- **Confidence**: medium  
- **Lenses**: L1, L5  
- **Claim**: ProtocolHandler projected-key gate, receivers, and StaticInit pass `propertyNames: null` into `EffectiveProjectedKey`, while actual emitted names / `BuildEmittedSignature` / InterfaceImpl use the protocol’s property-name set. Disambiguator documents that property-agnostic maps keep cross-walk lockstep; a property×method rename collision is meant to be caught by **emitted-signature** dedup → `skippedMethodKeys`. Residual: a scenario that collapses only under property-aware projected keys but **not** under raw key or emitted signature could theoretically emit a dead receiver — no live fixture found.  
- **Evidence**: `ProtocolHandler.cs:396` vs `417`; `Receivers.cs:127`; `ProtocolMethodDisambiguator.cs:229–242` (reservation property-agnostic; residual documented).  
- **Probe**: protocol with property `foo` + methods `foo()` and `fooMethod()` same param list; assert single interface member and single receiver.  
- **Prior art**: constraints GetPublicMethodName / P1-21 family (class path); protocol path residual is weaker.

### Refuted / already fixed (do not re-open without new reachability)

#### DA-W2-A5b-R01: Legacy blocking receiver missing `default(CancellationToken)`

- **Severity was**: P1 compile (CS1061)  
- **Status**: **refuted as open** — fixed  
- **Evidence**: `Receivers.cs:1356–1360`; BindingTests + unit above.  
- **Prior art**: AF05 constraints “KNOWN INCOMPLETE EDGE” — **update constraints**.

#### DA-W2-A5b-R02: Orphan receiver for existential overload collapse (CS1503 / CS0103)

- **Severity was**: P0/P1 generated-binding compile break  
- **Status**: already-known (fixed)  
- **Evidence**: raw `emittedRawKeys` in Receivers + StaticInit + cross-module parent; unit tests `TwoExistentialOverloadsSameRawKey_*`; BindingTests `OverloadCollapseDispatch.swift`.

#### DA-W2-A5b-R03: Projected-key protocol path omitting CT → silent member drop

- **Severity was**: P1 silent drop  
- **Status**: already-known (fixed, AF05 ruling b)  
- **Evidence**: `BuildProjectedMethodKey` async append; `KeyBuilderAsyncOverloadProtocol*`; ProtocolHandler emitted-signature CT.

#### DA-W2-A5b-R04: Intra-protocol sync+async same name/params slot collapse

- **Severity was**: P0 layout / missing slot  
- **Status**: already-known (fixed)  
- **Evidence**: async suffix on all three live slot keys; BindingTests `IntraProtocolEffectOverload.swift`.

#### DA-W2-A5b-R05: Suppressed-proxy exception aborting whole module

- **Severity was**: P1 total emit abort  
- **Status**: already-known (fixed)  
- **Evidence**: `EmitReceiverOrDegrade` checkpoint (`Receivers.cs:163–178`); unit `EmitReceiverOrDegrade_SuppressedProxy_RecordsRowNamingTheProxyFromException`.

---

## BindingTests Protocols/ reverse-dispatch coverage (sampled)

| Fixture | What it gates |
|---------|----------------|
| `KeyBuilderAsyncOverloadProtocol` | Projected CT split + real-async reverse |
| `KeyBuilderAsyncBlockingOverloadProtocol` | Legacy CT + blocking reverse (String) |
| `KeyBuilderParentNameProtocol` | Protocol path omits parentTypeName rename |
| `OverloadCollapseDispatch` | Raw-key collapse / single receiver |
| `IntraProtocolEffectOverload` | Intra-protocol async/sync dual slots |
| `SiblingMethodDispatch` / `AsyncSiblingMethodDispatch` | Sibling fan-out |
| `SuppressedProxyChannels` | Degraded proxy channels |
| `DuplicateSignatureDisambiguation` | Label-only overload disambiguation |
| `AsyncReverseDispatch` | Real-async reverse suite |
| Lifetime `ReverseDispatchInvariants` / `ProxyLifetimeFixture` | Design B2 (A3 overlap) |

---

## Files reviewed (deep)

| File | Role | Ledger suggestion |
|------|------|-------------------|
| `…/ProtocolProxyEmitter.Receivers.cs` (~2745) | Reverse receivers | `reviewed-deep` / `hazard` (mega + dual async paths) |
| `…/ProtocolProxyEmitter.StaticInit.cs` | Fillability assignment mirror | `reviewed-deep` |
| `…/ProtocolProxyEmitter.InterfaceImpl.cs` | Forward SBW + SB0003 stubs | `reviewed` (partial; A5c may own more) |
| `…/ProtocolProxyEmitter.Helpers.cs` | Dead `GetMethodKey` | `hazard` (dead wrong key) |
| `…/ProtocolSignatureHelper.cs` | Projected key one-core | `reviewed-deep` |
| `…/ProtocolMethodDisambiguator.cs` | Label-only Effective* | `reviewed-deep` |
| `…/Handler/ProtocolHandler.cs` | Interface dedup triple gate | `reviewed-deep` |
| `…/EveryProtocolEmitter.GetMethodKey` + `EmitsRealAsyncWitness` | Slot key + real-async oracle | `reviewed` (A5 owns full emitter) |
| `…/WitnessDispatchEmitter.GetMethodKey` | Forward key | inventory for A5b contrast |
| `…/VtableLayout.cs` | Shared index model | inventory |
| `…/Reporting/SuppressedProxyReporting.cs` | L3 honesty | `reviewed` |
| `…/Handler/ReceiverConversionVisitors.cs` | L4 visitor family | `reviewed` |
| BindingTests Protocols/* listed above | Runtime gates | inventory |
| Unit `ProtocolProxyEmitterTests` orphan + CT | Compile-time gates | inventory |

---

## Counts

| Bucket | Count |
|--------|------:|
| Confirmed open residual findings | **7** (1 design fillability P2, 1 async-shape P2, 1 L3 already-known residual, 1 docs P3, 2 L4, 1 candidate propertyNames) |
| Refuted / fixed prior defects verified | **5** |
| New emission-live P0/P1 open defects | **0** |
| BindingTests reverse-dispatch fixtures sampled | **10+** |
| Live key builders (must stay distinct) | **3** (+1 dead) |
| Coordinated AF05 CT sites | **4** (projected key, emitted sig, real-async receiver, legacy receiver) — all live |

---

## Suggested backlog (owner-gated; no implementation in this audit)

1. **Docs**: update `constraints.md` AF05 “legacy CT incomplete” + M0-A “incomplete CT edge” → fixed + fixture pointers.  
2. **L4**: delete or rehome dead `Helpers.GetMethodKey`.  
3. **Capability**: product decision on second-slot fillability for collapsed existential overloads (today: null).  
4. **Capability**: widen `EmitsRealAsyncWitness` (void Task, String, higher arity) to shrink legacy blocking surface.  
5. **L3**: continue SB0003 / compile-but-dead surfacing (G1/W7), not re-filed as A5b ABI bugs.

---

## Exit statement

Track A5b **closes the hunt list** for orphan receivers, projected-vs-slot key conflation, and async CT mis-binding as **already fixed and gated**. Residual risk is **low**, concentrated in intentional fillability limits, legacy-blocking runtime hazards, suppress/stub honesty (already-known product class), and documentation drift that still advertises a fixed AF05 edge as open.
