# Corpus compile-red root causes — 2026-07-23

Status: **root-cause investigation complete; fix wave proposed, not yet funded.**
Companion to `corpus-compile-red-recheck-2026-07.md` (the 2026-07-22 recheck that showed
0 of 6 reds flipped by the proxy-availability work). This doc records the actual
mechanisms behind all four families, with fix sites, sizes, and a session plan.

**Headline: six of the seven underlying bugs are localized with known fix sites; one is
cross-cutting. Fixing everything here is worth +7 greens (58 → 65/120).**

Evidence base: fresh corpus artifacts under
`/Users/wojo/Dev/internal-binding-testing/corpus-sweep/output/{DGCharts,Moya,CombineCocoa,rive-ios,Macaw,SwiftDate}/`
(compile.log / generate.log / generated C# / packed nupkgs), traced into the generator
source on `main` at `ee8a7602`.

---

## Family: Moya sibling reference (+2 greens: Moya, plus CombineMoya cascade)

**Triage correction: NOT a sibling-surface gap.** The recheck doc's label ("Alamofire
does not surface `IParameterEncoding`") is wrong. Confirmed three ways: the generated
`Alamofire.Types.IParameterEncoding.cs` declares `public interface IParameterEncoding`
with full proxy/witness-table wiring; the api-manifest lists it in `Session.Request`
overloads; and the packed `Alamofire.Swift.iOS.dll` metadata shows it as
`Public, Interface, Abstract`. There is no skip diagnostic in Alamofire's generate.log
because no gate fired. The protocol has neither Self requirements nor associated types —
there is nothing to lift.

**Actual bug (Moya side, emission):** all 8 CS0246s are 4 unique sites × the build's
double-print, and all reference the *bare* name `IParameterEncoding` where every other
cross-module reference in Moya's output is correctly qualified (`Alamofire.HTTPMethod`,
`Alamofire.Session`, …). The 4 sites are the enum-case factory methods
(`Task.RequestParameters`, `Task.RequestCompositeParameters` — `Moya.Types.Task.cs:131,204`)
and the TryGet deconstructors (`:479,620`).

**Root cause:** `EnumHandler.CaseConstruction.cs:1013-1034`
(`GetPublicCSharpTypeNameForEnumCase`) constructs `new ExistentialHandler(typeDatabase)`
at line 1016 **without setting `CurrentModuleName`**, even though it receives
`moduleDecl` as a parameter. `ExistentialHandler.GetPublicExistentialType`'s
cross-module qualification block (`ExistentialHandler.cs:880-886`) is gated on
`CurrentModuleName` being non-empty, so it never fires and returns the bare interface
name. The correct wiring exists 41 lines earlier in the same method
(`EnumHandler.CaseConstruction.cs:234`:
`new ExistentialHandler(typeDatabase) { CurrentModuleName = moduleDecl.Name }`) and at
~6 other call sites (`ProtocolSignatureHelper.cs:445`,
`ProtocolProxyEmitter.InterfaceImpl.cs:1494/2011/2087`). `git blame`: `moduleDecl` was
added to the helper's signature 2026-04-23 (`309913dbf`) for an unrelated bound-generics
fix and never connected.

Callers of the buggy helper (= the 4 failing sites): `EnumHandler.CaseConstruction.cs:63`
(factory params) and `EnumHandler.CaseInspection.cs:236,464` (TryGet out-params).

**Fix:** one line — set `CurrentModuleName` on the handler at
`EnumHandler.CaseConstruction.cs:1016`. Do NOT build "degrade gracefully when sibling
type is missing" machinery: the type isn't missing, and degrading the enum surface to
`object` would paper over the bug.

**Hardening observation (separate, optional):** there is no post-emission gate that
reconciles bare cross-module C# *type identifiers* in generated signatures against
anything. `ProxyReferenceIntegrityGate` covers only same-module `new XProxy(` call
expressions (cross-module proxies are deliberately out of its scope);
`WrapperSymbolIntegrityGate` covers native `SBW_*` symbols. A bare unqualified
cross-module type name sails through and manifests only as CS0246 in the *consuming*
package — exactly how this shipped silently. Same shape of gate as the existing two,
different reference class.

**Difficulty: trivial** (the fix) — reproducible with the Moya/Alamofire corpus pair.

---

## Family: Operator-return CS0029 (+2 greens: Macaw, SwiftDate)

**Trigger shape:** Swift **class**-typed operator returns. All failing cases are
`open class` parents with operators returning a Swift class (`Macaw.Size` from
`Size.+/-` and `Point.-`; `SwiftDate.TimePeriod` from `TimePeriod.+/-` with a `Double`
rhs). Frozen-struct operators (all existing BindingTests fixtures) work fine.

**Root cause:** class-kind returns are correctly non-indirect
(`MarshallingHelpers.IsTypeInherentlyIndirect`, `MarshallingHelpers.cs:471-472` — class
pointer comes back in x0), so the operator takes `OperatorHandler.EmitOperatorPInvokeCall`'s
direct-call branch (`OperatorHandler.cs:542-561`). That branch emits a bare
`return {pinvokeName}({callArgs});` — it never wraps the raw `IntPtr` in
`SwiftMarshal.MarshalFromSwift<T>`. The P/Invoke returns `IntPtr`, the operator declares
the projected class → CS0029. `OperatorHandler` maintains its own thin return-emission
routine that only special-cases `void`; the general (non-operator) machinery already has
the correct dispatch for exactly this case at `WrapperEmitter.Return.cs:943-949`
(`Class` → `MarshalFromSwift`), which is why ordinary static factory methods returning
classes work.

The indirect-result branch of the same method (line ~528) and the `@_cdecl`-wrapper path
(`EmitCdeclOperatorCall`) already marshal correctly. The branch selection
(`MethodRequiresIndirectResult`, `OperatorHandler.cs:305`) is NOT the bug.

**Fix:** in the direct-call branch (both the helper-context and plain sub-branches,
lines 552 and 559), when the return type resolves to `TypeRecordKind.Class`, emit
`return SwiftMarshal.MarshalFromSwift<{returnType}>({call});`. Mirror
`WrapperEmitter.Return.cs`'s full dispatch order (ObjC-bridged/bridgeable → GetNSObject,
Class → MarshalFromSwift, non-simple Enum → MarshalFromSwift) so enum- and
ObjC-bridged-returning operators don't become the next gap. `methodEnv` is already a
parameter of `EmitOperatorPInvokeCall`, so no new plumbing.

**Confirmed test blind spot:** `BindingTests/Sources/SwiftBindingsTestLib/Operators/*`
fixtures are all `@frozen struct` (plus one class whose `==` returns `Bool`) — no class
with an arithmetic operator returning the class itself.
`OperatorHandlerOutputTests.cs`'s `EmitOperator_NonFrozenClassReturn_EmitsIndirectResult`
(line 284) registers the parent as `TypeRecordKind.Struct` despite its name; the one
`TypeRecordKind.Class` helper (line 788) is only used for calling-convention checks on
`Bool`-returning `==`. Ship the fix with: (1) a true class-return direct-path unit test,
(2) a `public class` arithmetic-operator Swift fixture + RuntimeTestsApp test.

**Difficulty: low-to-moderate.** One branch in one function; template exists.

---

## Family: CombineCocoa incomplete DelegateProxy (+1 green)

**One root cause, two cascading diagnostics.** The failing line is the generated class's
base list (`CombineCocoa.Types.DelegateProxy.cs:30`):

```csharp
public partial class DelegateProxy : Runtime.ObjcDelegateProxy, ISwiftObject, ...
```

- CS0246: `Runtime.ObjcDelegateProxy` doesn't exist in any referenced assembly.
- CS0535 (`IDisposable.Dispose()` missing): pure cascade. The ObjC-boundary emission
  branch (`ClassHandler.cs:206-219`) deliberately strips `IDisposable` and emits no
  `Dispose()` on the assumption the ObjC base class (an NSObject-derived binding type)
  supplies it. With the base unresolvable, the inherited `Dispose()` never materializes
  and `ISwiftObject : IDisposable` goes unimplemented. No validation gate wrongly fired;
  fixing the base name restores `Dispose()` for free.

**Where `Runtime.` comes from:** the Swift ABI JSON itself. `ObjcDelegateProxy` is a
pure-ObjC class declared in CombineCocoa's *own* bridging header (USR
`c:objc(cs)ObjcDelegateProxy`, no module marker; correctly synthesized by
`ObjCBridgeRecordFactory.CreateRecords`, `ObjCBridgeRecordFactory.cs:64-78`, under the
key `CombineCocoa.ObjcDelegateProxy`). But the ABI dump's `superclassNames` mislabels it
`Runtime.ObjcDelegateProxy` — CombineCocoa genuinely `import`s a third-party package
named `Runtime`, and Swift's digester attributes the un-modularized ObjC class to it.

**Root cause in our code:** `MarshallingHelpers.GetObjCBaseTypeName`
(`MarshallingHelpers.cs:704-742`) trusts the ABI-reported module segment. Its
unknown-module + pure-ObjC-USR branch extracts the true class name from the USR but then
passes the bogus module through `MapSwiftModuleToNetNamespace` (dictionary miss → input
returned as-is) → `Runtime.ObjcDelegateProxy`. The function is a pure function of
`ClassDecl` — it never consults the type database, so it *cannot* discover the
same-module mixed-bridge record that already correctly resolves this class.

**Precedent to mirror:** this exact "ABI `superclassNames` disagrees with the true owning
module" problem is already recognized and fixed for pure-Swift superclasses via
`ModuleProcessor.TryResolveSuperclassByUsr` (`ModuleProcessor.cs:1258-1266`, "The USR is
canonical") — which explicitly bails on ObjC (`c:`) USRs. The ObjC analogue is the gap.

**Fix:** before trusting the ABI module segment for a bare `c:objc(cs)…` USR, check the
current module's mixed-bridge records (the `ObjCBridgeRecordFactory` registry) for a
matching ObjC class name and prefer that resolution. Requires threading the current
module name + type database into `GetObjCBaseTypeName` (call site
`ClassHandler.cs:206-219` has both available) — small signature plumbing, not
architectural. Secondary consistency fix in the same pass:
`ModuleProcessor.RegisterClassType` (`ModuleProcessor.cs:855-858`) writes the same raw
string into the persisted module database (`CombineCocoaDatabase.xml`), so a downstream
consumer of CombineCocoa's database would inherit the bad reference.

**Difficulty: localized** (one helper + one call site + one consistency site). Recurrence
shape is real but narrow: mixed framework with a locally-declared un-modularized ObjC
helper class as a Swift superclass, while the module also imports an unrelated package.

---

## Family: DGCharts + rive-ios marshalling bodies (+2 greens) — actually THREE independent bugs

Not one mechanism. DGCharts exercises Shapes A, B, C1; rive-ios exercises C2, D.
Neither package flips until its full set is fixed.

### Shape A — `resultPtr` undefined (moderate)

**Symptom** (`DGCharts.Types.ChartBaseDataSet.cs:427-438`, also `ChartData.cs:589`,
`CombinedChartView.cs:88`): existential-typed **property getters** on the
`@_cdecl`-property-wrapper path emit `var result = PInvoke_…(Handle);` then read a
never-declared `resultPtr` on the next line:

```csharp
var result = PInvoke_valueFormatter_Get_3B50B705(Handle);
var existentialResult = SwiftMarshal.MarshalFromSwift<ExistentialContainer1>(resultPtr); // CS0103
```

**Root cause:** two independently-computed decisions disagree. The call-emission side
(`MethodMarshalPlanBuilder.BuildPInvokeCallStatement`, driven by
`_requiresIndirectResult`, `MethodMarshalPlanBuilder.cs:1267`) correctly chose the
direct-return convention (getter returns the box pointer as the function result). The
return-consumption side (`WrapperEmitter.Return.cs:447-478`, existential branch) decides
which local to read **solely from `UsesCdeclWrapper`** and hardcodes `ResultPtrName`
("resultPtr", the indirect-out-param name) without consulting `_requiresIndirectResult`.

**Fix:** the read should be `_requiresIndirectResult ? ResultPtrName : ReturnLocalName`
(pattern already used at `WrapperEmitter.Return.cs:205`). **Applies at TWO byte-identical
duplicated blocks** — `:447-478` and `:799-829` — which must be fixed in sync (and are a
latent maintenance hazard worth deduplicating while there).

### Shape B — `_handle` undefined (one line)

**Symptom** (`DGCharts.Types.ChartDataSet.cs:1129`, `ChartData.cs:1589`): concrete
generic-protocol specializations emitted directly on a class pass
`_handle.DangerousGetHandle()` as the self argument; no `_handle` field exists — the
class exposes `Handle`.

**Root cause:** `ConcreteProtocolSpecializationEmitter.cs:1455-1474`. The `isExtension`
branch correctly routes self through `((ISwiftObject)self).SwiftHandle`; the
`!isExtension` class branch hardcodes the nonexistent private field name at line 1471.

**Fix:** one line at `ConcreteProtocolSpecializationEmitter.cs:1471` — use the class's
real `Handle` accessor (used bare as an `IntPtr`-convertible P/Invoke arg everywhere
else in the same generated files). Single call site.

### Shape C — `.Payload` on `.Handle`-based classes (moderate; one predicate gap, two faces)

"Does this class expose `.Handle` instead of `.Payload`" requires the **union** of two
independent facts: (a) ObjC-rooted (Swift class inherits NSObject → generator emits
`.Handle`), and (b) ObjC-bridged/native-remapped (C# type is an existing platform
binding → also `.Handle`). No shared predicate exists; two emitters each check exactly
one half:

- **C1 (DGCharts, `CGContext.Payload`):** `ProtocolProxyEmitter.InterfaceImpl.cs:2586-2613`
  (proxy witness-dispatch forwarding, e.g. `DataRenderer.DrawData(CGContext)`) checks
  only `IsObjCRootedClassType` (`WitnessDispatchEmitter.cs:756-771`,
  `TypeRecordFlags.ObjCRooted`). Swift's `CGContext` wraps a `CFTypeRef` and does NOT
  inherit NSObject → false → falls to `.Payload`. The real problem: CoreGraphics types
  lack the native-remap classification (`NativeTypeName`/`ObjCBridged`-style flag) that
  `Foundation.URL`/`Data` correctly carry, so `IsSwiftClassType` doesn't exclude them.
- **C2 (rive-ios, `RiveViewModel.Payload`):** `SwiftUIBridgeEmitter.InitAnalyzer.cs:488-511`
  builds `IsObjCBridgeable` from `IsObjCBridgeable || IsObjCBridged` only — never
  consults `IsObjCRooted`. `RiveViewModel` IS ObjC-rooted (a genuine generator-emitted
  NSObject subclass using `.Handle`) but isn't remapped → both flags false → `.Payload`
  emitted at `SwiftUIBridgeEmitter.cs:3084`.

**Fix:** (1) one-line OR-in `IsObjCRooted` at `SwiftUIBridgeEmitter.InitAnalyzer.cs:501-502`
(resolves C2); (2) populate the native-remap classification for CoreGraphics types in the
type database / `AppleFrameworkRegistry` (the registry is the stated single source of
truth for Apple type remapping) or widen the ProtocolProxyEmitter check (resolves C1);
(3) preferably introduce a shared helper — e.g.
`MarshallingHelpers.UsesHandleAccessor(record)` = `IsObjCRooted || IsObjCBridged ||
IsObjCBridgeable` — consumed by both emitters so a third emitter can't reintroduce the
split. Audit the other `.Payload.DangerousGetHandle()` ternaries in
`ProtocolProxyEmitter.InterfaceImpl.cs` (`:1290,:1325,:1400`) for the same gap.

### Shape D — non-baseline async-closure triple fault (cross-cutting; the one real project)

**Symptom** (`RiveRuntime.Types.RiveUIView.cs:642-667` and the sibling overload at
`:695-719`): a constructor taking `() async throws -> Rive` (escaping async closure with
a non-blittable Swift-class return — NOT "baseline" per `ClosureHandler.cs:1010-1014`)
emits a body referencing an undeclared `riveBox` (3× CS0103), an never-emitted static
trampoline `s_init_rive_*_Callback` (CS0103), builds a correct `riveClosure` it never
uses, and then passes the raw managed `Func<Task<Rive>>` to a P/Invoke parameter typed
`Swift.AnyType` (CS1503). The member already carries `[Obsolete(SB0001)]` ("P/Invoke
calling convention may not match Swift ABI") — the generator *knows* the shape is
unreliable but still emits a full broken body.

**Root cause:** three independently-maintained stages classify the same closure shape
with **different predicates**:

1. Declaration stage (`MethodMarshalPlanBuilder.cs:373`) gates the `{csName}Box`
   declaration on the **broad** `!IsAsyncClosure` → any async closure loses the `Box`
   local. (The sibling `GCHandle {csName}Handle` decl at `:348-354` correctly uses the
   **narrow** `!(IsAsyncClosure && IsBaselineAsyncClosure)` — hence `riveHandle` exists
   but `riveBox` doesn't.)
2. Body-emission routing (`WrapperEmitter.Marshalling.cs:401-489`): only
   `IsAsyncClosure && IsBaselineAsyncClosure` routes to the self-contained async setup;
   non-baseline async **falls through to the legacy sync escaping-closure path**
   (`:435-489`), which unconditionally references `{csName}Box` (`:457,:477`) and the
   callback-trampoline name, with no async awareness.
3. P/Invoke typing (`PInvokeEmitter`, per the comment at
   `MethodMarshalPlanBuilder.cs:407`) correctly recognizes the shape as unsupported and
   downgrades the parameter to the `Swift.AnyType` placeholder — but the call-argument
   builder still passes the raw local.

**Fix direction:** make the three stages agree. Either (a) tighten
`MethodMarshalPlanBuilder.cs:373` to the narrow predicate AND make the legacy escaping
path refuse `IsAsyncClosure && !IsBaselineAsyncClosure`, or — likely better — (b) route
non-baseline async closures to the existing SB0003-style "member kept for signature but
throws NotSupportedException" stub emission, making the already-present SB0001 marking
actually skip real-body emission instead of only warning. **Freeze-policy note:** this is
aligning *existing* gates that already disagree, not adding a new emission-time
prediction gate; the failure is compile-visible, and the resolution is a clean stub, not
a new predictor.

**Difficulty: cross-cutting** — the only fix in this wave with real behavioral blast
radius (it changes what gets emitted for a whole closure shape). Do it alone, with a
BindingTests fixture for the non-baseline async shape and a no-output-diff check over
packages that don't contain the shape.

---

## Session plan (proposed, 3–4 sessions)

| Session | Contents | Expected flips |
|---|---|---|
| 1. Quick wins | Moya `CurrentModuleName` one-liner; operator-return marshal wrap (+ class-operator fixture/tests); Shape B `_handle`→`Handle` one-liner | Moya, CombineMoya, Macaw, SwiftDate (+4 → 62/120) |
| 2. CombineCocoa + DGCharts | ObjC base-name USR resolution (+ module-DB consistency fix); Shape A convention-agreement fix (both duplicated blocks); Shape C shared `.Handle` predicate + CoreGraphics remap classification | CombineCocoa, DGCharts (+2 → 64/120) |
| 3. rive-ios | Shape D async-closure predicate alignment → clean stub. Alone, because of blast radius | rive-ios (+1 → 65/120) |
| 4. Optional buffer / hardening | Shape D spillover; else the bare cross-module type-identifier post-emission gate (Moya hardening observation) | — |

Every fix ships with unit tests at the right layer plus BindingTests fixtures where the
shape is runtime-relevant (class-operator returns, non-baseline async stub), and a corpus
re-run of the affected packages (`run_library.py NAME --skip-convert`) to confirm the
flip and the error-histogram change.

Ordering vs 0.18.0: all additive generator work; can land before the cut without
touching release mechanics. Out of scope for this wave (unchanged from the recheck doc):
the 10 SWIFTBIND111 generate-fails, graph-closure/named-input reds, and the
OrderedCollections/Crypto/SwiftDrawDOM sibling mechanisms that make up the rest of the
old ~68 projection.
