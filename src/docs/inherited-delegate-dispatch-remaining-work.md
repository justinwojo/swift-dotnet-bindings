# Inherited-delegate dispatch — remaining runtime-gate work

> Generator gap surfaced by an attempted BindingTests fixture on 2026-05-21.
> The parse-time `TypeRecordFlags.ClassBound` walk (shipped in the same
> session) correctly propagates class-boundedness across modules, but three
> downstream emission paths still treat a cross-module inherited parent as
> if it does not exist. The runtime gate cannot be wired up until those
> are addressed.

## Summary

`protocol Child: External.Parent` (where `External` is a different Swift
module and `Parent: AnyObject` is class-bound) parses correctly with the
transitive `ProtocolIsClassBoundTransitive` walk: `Child`'s TypeRecord gets
`TypeRecordFlags.ClassBound` set, and `ChildProxy` is emitted with the
2-word class-bound existential layout.

But the wrapper Swift compilation fails at:

```
error: type 'EveryProtocol' does not conform to protocol 'CrossModuleParentDelegate'
extension EveryProtocol: SwiftBindingsTestLib.CrossModuleInheritedChildDelegate {}
^
```

…because the emitter generates a single empty extension declaring
EveryProtocol's conformance to the child, treating it as a
"composition/marker protocol," and **never** emits a sibling extension
on the main module's EveryProtocol that supplies the cross-module
parent's witnesses. The parent lives in another module's EveryProtocol
class — a different type — so Swift sees the main-module EveryProtocol
as non-conforming.

The in-module case works because the parent protocol gets its own
EveryProtocol extension in the same Wrapper.swift, which provides the
inherited method. Compare the two against `output/.../Wrapper.swift`:

| Inheritance shape | Parent ext on EveryProtocol | Child ext on EveryProtocol | Result |
|---|---|---|---|
| In-module: `InheritedChildDelegate: InheritedParentDelegate` | ✅ emitted | ✅ emitted (empty) | Compiles, witness table populated |
| Cross-module: `Child: External.Parent` | ❌ **not emitted** | ✅ emitted (empty) | Wrapper compile error, WT getter stripped |

## Where the gaps live (from Codex r2 Medium #2 / observed during fixture build)

1. **`src/Swift.Bindings/src/Emitter/StringEmitter/EveryProtocolEmitter.cs`** —
   the main loop that emits `extension EveryProtocol: <Module>.<Protocol>`
   blocks is keyed on protocols declared in the *current* module. A child
   protocol whose ancestors live in a different module gets no
   cross-module-parent stub extension on the main module's EveryProtocol.
   Fix shape: when emitting an EveryProtocol extension for a protocol with
   cross-module inherited parents, walk those ancestors and emit a
   companion extension on the main module's EveryProtocol that supplies
   each inherited method. The implementations can call the corresponding
   `SwiftBindingsTestLibDependency.EveryProtocol` instance's method, or
   forward through a stored proxy — design TBD.

2. **`src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ProtocolHandler.cs:637`** —
   skips cross-module inherited interfaces when building the C#
   `IChild : IParent` chain. Generated `IChild` extends only same-module
   parents, so `IProtocolProxyImpl<IChild>` cannot be looked up
   covariantly as `IProtocolProxyImpl<IParent>` for inherited callbacks.

3. **`src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.StaticInit.cs:366`** —
   skips cross-module ancestor proxy cctor initialization. Without the
   cross-module parent proxy's cctor running, the parent's Swift module
   global `_p_vtable` stays nil → force-unwrap SIGTRAP on inherited
   call (this is the original Kidoz issue #40 crash shape, applied to a
   cross-module parent).

(1) blocks the wrapper from compiling at all when the cross-module case is
present in a BindingTests fixture, which is why the runtime gate cannot
even be attempted yet. (2) and (3) are the L1/L2 analogues of what the
in-module fix shipped, but for cross-module parents.

## Why this wasn't shipped with the source-level transitive flag

The parse-time fix (ModuleProcessor.cs:1389 `ProtocolIsClassBoundTransitive`
+ symmetric Codable walk) ensures the *flag* is correct on the TypeRecord
regardless of module boundaries. That fix enables the rest, but the
downstream emitters still need their own cross-module paths. The user
authorized the parse-time fix only; expanding into the three emitter
sites was out of scope.

## Repro fixture (when picked up)

Restore the deleted fixture from git history (single commit on `main`
referencing this doc, or check the working-tree state on
2026-05-21 around the categorical BindingTests session):

- Add `public protocol CrossModuleParentDelegate: AnyObject { func crossModuleDidNotify(value: Int32) }`
  to `BindingTests/Sources/SwiftBindingsTestLibDependency/DependencyTypes.swift`
- Add `public protocol CrossModuleInheritedChildDelegate: CrossModuleParentDelegate {}`
  + `CrossModuleInheritedDelegateSource` to `BindingTests/Sources/SwiftBindingsTestLib/Protocols/InheritedDelegateDispatch.swift`
- Add `TestCrossModuleInheritedChildDeliversCallback` to
  `BindingTests/RuntimeTestsApp/Protocols/InheritedDelegateDispatchTests.cs`
- `nuke binding-tests` will surface the EveryProtocol wrapper conformance
  error first, then (once that is fixed) any L1/L2 cross-module gaps.

## Pre-existing wrapper-emission bugs blocking in-module gates too

Attempting to add 3-level (`Grandchild → Child → Parent → AnyObject`) and
non-empty-child runtime fixtures exposes three pre-existing
EveryProtocol wrapper-emission failures that are unrelated to the
categorical fix but get triggered when the EveryProtocol conformance set
grows. They block any further BindingTests expansion on this axis.

Full table, repro shapes, fix sketch, and links: see
[wrapper-emission-bugs.md](wrapper-emission-bugs.md).

At minimum the `label`/`label()` collision must be fixed before the
in-module 3-level and non-empty-child fixtures can be wired up as
runtime tests. The parse-time transitive walk is correct (proven by
the unit test below); landing runtime coverage requires this
prerequisite emitter work.

## Runtime gates already in place from the same session

- Unit test `FinalizeTypeProcessing_QualifiedInheritedShadowsLocalSimpleName_PrefersCrossModule`
  in `src/Swift.Bindings/tests/UnitTests/ParserTests/ModuleProcessorCycleTests.cs` —
  proves the parse-time walk's cross-module fallback correctly picks the
  external parent over a same-simple-named local. The flag is set
  correctly; only the downstream emission is missing.
- Existing `InheritedDelegateDispatchTests` (4 tests) — covers the
  single-level inherited-delegate dispatch shape (issue #40's exact
  Kidoz repro). Continues to pass against the source-level transitive
  fix.
- Kidoz SMOKE 5 in `/Users/wojo/Dev/internal-binding-testing/Kidoz/Program.cs` —
  end-to-end third-party-SDK verification of the same shape, including
  callback delivery.

**Not yet landed as runtime gates** (blocked on the wrapper-emission
prerequisites above):

- 3-level chain (`Grandchild → Child → Parent → AnyObject`).
- Non-empty child (child has own method on top of inherited parent
  method).
- Cross-module chain (parent in `SwiftBindingsTestLibDependency`,
  child here).

## Remaining Kidoz validation work

**None on the inherited-delegate axis.** Static audit of the generated
binding shows `IKidozInitDelegate: ISDKInitDelegate` is the **only**
inherited-protocol delegate in the SDK. The other four
(`IKidozRewardedDelegate`, `IKidozInterstitialDelegate`,
`IKidozBidRequestDelegate`, `IKidozBannerDelegate`) are flat protocols
and never exercised the issue #40 pattern.

`/Users/wojo/Dev/internal-binding-testing/Kidoz/Program.cs` SMOKE 5 covers
end-to-end inherited-delegate dispatch through `IKidozInitDelegate` and
verifies callback delivery (the Swift SDK fires `OnInitError` with
`errorCode: 10000 message: Failed to Parse Init Response` when handed a
fake publisher key — that proves the inherited vtable hop works).

Optional, low-priority Kidoz follow-ups (not currently planned):

- "Construct + register + dispose, no crash" smoke tests for the four
  flat delegates. These would broaden Kidoz surface coverage but do not
  add categorical signal beyond what BindingTests already provides for
  flat-protocol dispatch. Kidoz is third-party safety-net scaffolding
  ([project_internal_binding_testing_temporary]) — invest in BindingTests
  for durable categorical signal.
- Live ad-cycle tests (load → show → callback → dismiss). Require valid
  Kidoz publisher credentials; not available. Would test SDK behavior
  rather than binding correctness; out of scope.
