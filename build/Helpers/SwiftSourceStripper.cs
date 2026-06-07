// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Serilog;

/// <summary>
/// Strips known-broken Swift wrapper code before compilation.
/// Ports the Python stripping logic from build-async-wrapper.sh.
/// The generator emits code for ALL features, including unsupported ones that
/// produce uncompilable Swift. This class strips those sections so the good
/// async method wrappers can compile.
/// </summary>
public static class SwiftSourceStripper
{
    // Protocols to preserve for runtime testing.
    // EveryProtocol conformances for these protocols are kept so proxy dispatch works at runtime.
    private static readonly HashSet<string> PreservedProtocols = new()
    {
        "HasValue", "ExistentialParamDelegate",
        "ProcessingMode",
        "Describable", "TestIdentifiable", "Displayable",
        "Nameable", "Ageable", "Addable", "Subtractable", "Multipliable", "Dividable",
        "Named", "Prioritized",
        "TaskDescriptor", "StringProcessor",
        "StatusHandler", "PriorityHandler",
        "URLProcessorDelegate",
        "EventDelegate",
        // Optional<ClosedRange<Float>> proxy-getter regression: OptionalClosedRangeProviderTests
        // has a C# conformer whose AllowedRange getter is read back by Swift through the
        // EveryProtocol proxy receiver. ClosedRange is handle-backed (no nested .Buffer), so the
        // receiver getter packs the wrapper into SwiftOptional<SwiftClosedRange<Float>>; the
        // conformance and its witness-table getter must survive stripping for the existential
        // construction P/Invoke (Get_EveryProtocol_RangeBoundsProvider_WitnessTable) to resolve.
        "RangeBoundsProvider",
        // Auto-wrap regression for justinwojo/swift-dotnet-bindings#16 (GDPerformanceView).
        // AutoWrappedDelegateTests drives Swift→C# callbacks through the property setter,
        // constructor arg, and method arg emit sites, so the EveryProtocol conformance and
        // its witness table accessor must survive wrapper stripping.
        "AutoWrappedMonitorDelegate",
        // Multi-protocol auto-wrap cache regression: same C# instance is wrapped for two
        // distinct protocols and dispatched via two distinct witness tables in the same
        // call. Both protocol conformances on EveryProtocol must survive stripping.
        "AutoWrappedSecondaryDelegate",
        // Proxy lifetime regression: ProxyLifetimeTests exercises the impl-anchored
        // EveryProtocol release path (tracker + Swift deinit callback). The fixture
        // lives in BindingTests/Sources/SwiftBindingsTestLib/Lifetime/ProxyLifetimeFixture.swift
        // and dispatches Swift→C# via a blittable ping() method.
        "ProxyLifetimeReceiver",
        // Vtable-slot-collision regression fixture: DataLoadingDelegate has a
        // non-dispatchable closure method (onDataLoaded with multi-arg closure) plus a
        // non-closure method (sourceIdentifier). The C# proxy struct must omit the
        // closure slot to match Swift's omission; the runtime test drives this via
        // loader.Delegate = proxy → sourceIdentifier() round trip, so the EveryProtocol
        // conformance and witness table getter must survive stripping.
        "DataLoadingDelegate",
        // Closure-dispatch fixtures: multi-arg primitive closure (NumericDataDelegate),
        // Optional<Closure> nil-and-non-nil (CompletionDelegate), and return-typed closure
        // (IntFactoryDelegate). Each runtime test does router.Delegate = proxy → fire(),
        // which requires the witness-table getter to be present on the dylib.
        "NumericDataDelegate",
        "CompletionDelegate",
        "IntFactoryDelegate",
        // Shape 1: throwing closure param `(Int32) throws -> Int32`.
        // ThrowingIntRouter.fireProcessInt drives the cdecl invoke thunk + errorOut path
        // through the C# proxy; the witness-table getter must survive stripping.
        "ThrowingIntDelegate",
        // Shape 3: closure property `var handler: (() -> Void)? { get set }`.
        // CallbackRouter.invokeHandler / setHandlerFromSwift drive the setter+getter
        // round-trip through the EveryProtocol vtable; both witness-table accessors must
        // survive stripping.
        "HasCallbackDelegate",
        // Shape 4: closure-returning method `func makeHandler() -> () -> Void`.
        // HandlerFactoryRouter.fetchAndInvokeHandler routes through the EveryProtocol
        // vtable to a C# receiver that returns (fnPtr, ctx); Swift materialises a
        // `() -> Void` from the pair and invokes it. The witness-table getter must
        // survive stripping.
        "HandlerFactoryDelegate",
        // Shape 2: async closure parameter `func runAsync(handler: @escaping () async -> Int32)`.
        // AsyncIntRouter.fireRunAsync drives Swift→C# async-closure dispatch; the
        // cdecl invoke thunk spawns Task and completes via a function-pointer callback.
        "AsyncIntDelegate",
        // Multi-shape composite protocol used as a regression sentinel — every member
        // (Bool / closure property / closure-returning method / async closure / throwing
        // closure / Int32 property) must dispatch via a real vtable receiver, mirroring
        // Nuke ImagePipelineDelegate / BlinkIDUX CameraModel richness.
        "MultiShapeDelegate",
        // S-2 multi-arg dispatchable-closure shape (Stripe STPIssuingCard/STPCustomer
        // EphemeralKeyProvider parity). EphemeralKeyProvider's closure currently has a
        // String arg that the invoke thunk doesn't yet marshal — the EveryProtocol
        // extension still emits fatalError — but the proxy-construction test depends on
        // the witness-table accessor existing. RetryingKeyProvider has a primitive-arg
        // closure and exercises the full multi-arg vtable dispatch end-to-end.
        "EphemeralKeyProvider",
        "RetryingKeyProvider",
        // Inherited-delegate dispatch regression (justinwojo/swift-dotnet-bindings#40,
        // KidozSDK). Child protocol with no new requirements inherits a callback from
        // a parent protocol; Swift routes the call through the parent's vtable. Both
        // EveryProtocol conformances and both witness-table getters must survive so
        // InheritedDelegateDispatchTests can exercise the cross-proxy delivery path.
        "InheritedParentDelegate",
        "InheritedChildDelegate",
        // Class-parameter reverse-callback regression (justinwojo/swift-dotnet-bindings#40,
        // KidozSDK KidozError). Swift calls back into a C# protocol implementation with a
        // method whose parameter is a Swift *class* instance; the generated proxy receiver
        // used to reinterpret the heap pointer via Unsafe.Read and crash. ClassParamCallback
        // .swift exercises both the pure-Swift and `@objc … : NSObject` payload variants and
        // their Optional<class> overloads through the EveryProtocol proxy receiver, so both
        // conformances and their witness-table getters must survive stripping for the
        // existential construction P/Invoke to resolve.
        "ClassParamReceiver",
        "ObjCClassParamReceiver",
        // 3-level chain and non-empty-child variants of the same dispatch shape.
        // The grandchild walks the cctor cascade two ancestors deep; the non-empty
        // child verifies a real-world layout (parent inherited + child own method).
        "InheritedGrandchildDelegate",
        "InheritedNonEmptyChildDelegate",
        // Cross-module inherited-delegate variant — parent lives in
        // SwiftBindingsTestLibDependency, child here. Both conformances must
        // survive so CrossModuleInheritedDelegateTests can exercise the
        // cross-module witness-table forwarding path.
        "CrossModuleParentDelegate",
        "CrossModuleInheritedChildDelegate",
        // Transitive cross-module ancestor chain (H1): local child inherits
        // a dep-module parent which itself inherits a dep-module grandparent.
        // All three conformances on EveryProtocol must survive so the child
        // proxy cctor can populate both ancestor vtables in the local wrapper.
        "CrossModuleTransitiveChildDelegate",
        "CrossModuleTransitiveParentDelegate",
        "CrossModuleTransitiveGrandparentDelegate",
        // Cross-module parent with non-dispatchable closure property (H2):
        // the C# cross-module-parent vtable struct + receivers + Swift wrapper
        // vtable struct must ALL skip the closure-property slot in lock-step
        // so the layouts match. Witness-table getters must survive so the
        // CrossModuleClosurePropertyDelegateTests can drive the round trip.
        "CrossModuleClosurePropertyChildDelegate",
        "CrossModuleClosurePropertyParentDelegate",
        // Cross-module parent with a skipped (non-dispatchable two-closure)
        // method declared BEFORE a dispatchable method. Exercises the cctor
        // index-ordering parity: the struct emitters increment-then-skip, so
        // the cctor must do the same. Witness-table getters must survive so
        // the CrossModuleSkippedMethodDelegateTests can drive the round trip.
        "CrossModuleSkippedMethodChildDelegate",
        "CrossModuleSkippedMethodParentDelegate",
        // Dep-module protocols whose EveryProtocol conformance is fully VALID (it
        // compiles and its witness-table getter ships in the simulator slice) but
        // which has no inheritance/sibling reason to already appear above. The
        // device dependency wrapper is rebuilt from preserved sources through
        // StripFile, whose Pattern 1 drops every non-preserved `extension
        // EveryProtocol: <P>`; without that conformance the matching
        // `Get_EveryProtocol_<P>_WitnessTable` getter loses its `any <P> = instance`
        // bind, fails to compile, and the device retry strips it — so the symbol is
        // present on simulator (built via the generator's compile-only retry, which
        // never pre-strips a conformance that compiles) but missing on device. These
        // must be preserved to keep that parity. DependencyProtocol backs
        // CrossModuleTests.TestDescribeAnyDependencyDispatchesIntoCSharpConformer;
        // the three CrossModule*Parent property shapes back their respective
        // cross-module property-dispatch tests.
        "DependencyProtocol",
        "CrossModuleConflictingPropertyParent",
        "CrossModuleMemberKindPropertyParent",
        "CrossModuleInverseMemberKindParent",
        // Sibling-protocol property dispatch regression. A "sibling" group is
        // two or more class-bound protocols declaring the same property
        // name+type with different accessor sets; the EveryProtocolEmitter
        // picks the fattest as the owner and emits the body on its extension,
        // while siblings get empty extensions and Swift cross-extension
        // witness resolution routes the dispatch back to the owner body.
        // The owner body fans out across all sibling vtables; the runtime
        // tests in SiblingPropertyDispatchTests exercise C# proxies for each
        // sibling individually, so every sibling's witness-table getter must
        // survive stripping.
        "SiblingNamed",
        "SiblingMutableNamed",
        "SiblingTagged",
        "SiblingMutableTagged",
        "SiblingMutableTaggedAlt",
        // Inheritance-shape sibling group: Child refines Parent's get-only
        // requirement into get+set. Probes whether the parser duplicates
        // inherited PropertyDecls into the child protocol's .Properties so
        // ComputeSiblingPropertyFallbacks treats them as a real sibling group.
        "SiblingInheritedParent",
        "SiblingInheritedChild",
        // Closure-property sibling group: same shape as the value-typed
        // siblings but the property type is Optional<() -> Void>. Exercises
        // the EmitDispatchableClosurePropertyImplementation fan-out.
        "SiblingClosureProperty",
        "SiblingMutableClosureProperty",
        // Subscript sibling group: subscript(siblingIndexKey:) declared with
        // different accessor sets across two protocols. Exercises the
        // EmitSubscriptImplementation fan-out path.
        "SiblingIndexed",
        "SiblingMutableIndexed",
        // Divergent-argument-label subscript pair: identical index type / return
        // type but different external labels (at: vs by:). Exercises the
        // GetSubscriptSiblingKey label-aware grouping and the subscript-witness
        // explicit `<external> <internal>:` form in EmitSubscriptImplementation.
        "SiblingLabelAt",
        "SiblingLabelBy",
        // External-label edge cases: keyword (`default`) and collision-with-
        // synthetic (`index0`). The former forces NameProvider.EscapeSwiftKeyword
        // on the emitted label; the latter forces the flag-driven unlabeled
        // check instead of a brittle name-pattern match.
        "SiblingLabelKeyword",
        "SiblingLabelLooksLikeSynthetic",
        // r6 phantom-owner regression: mixed-generic protocol vs plain sibling.
        // PhantomOwnerMixedGeneric emits fatalError stubs for properties (mixed-
        // generic gate); PhantomOwnerRegular declares the same property name+type
        // get-only. ModuleHandler.IsEmittable must keep the mixed-generic OUT of
        // the sibling-plan input so the regular sibling owns its body standalone
        // instead of routing through the stub. Both witness-table accessors must
        // survive stripping so the runtime dispatch path exercises the fix.
        "PhantomOwnerMixedGeneric",
        "PhantomOwnerRegular",
        // Mixed-generic under-detection (Grok H1): the original
        // HasOnlyMethodLevelGenerics predicate short-circuited on Self, so a
        // method carrying BOTH method-level generic (τ_1_*) AND Self (τ_0_*)
        // was not counted toward the "has polluting generic method" leg. A
        // protocol whose only generic method has that shape slipped past the
        // IsEmittable filter and could win the sibling-group lex tie-break.
        // The CombinedMixedSelfGeneric / CombinedRegularSibling pair locks in
        // the broader HasMethodLevelGenericInSignature classification.
        "CombinedMixedSelfGeneric",
        "CombinedRegularSibling",
        // Sibling-protocol METHOD dispatch (audit item 1, Bug #2): two class-bound
        // protocols declare the same method signature. The owner (SiblingMethodOwner,
        // lex-min) emits the shared body; SiblingMethodPeer gets an EMPTY extension
        // routed via Swift cross-extension resolution. The owner body fans out across
        // both vtables and the receiver tries each sibling interface, so the Peer's
        // witness-table getter must survive stripping for the per-instance C# proxy of
        // the non-owner to be located. SiblingMethodDispatchTests is the runtime gate.
        "SiblingMethodOwner",
        "SiblingMethodPeer",
        // Sibling-method NAME divergence (audit item 1, Codex r1 Medium): same same-signature
        // fan-out shape, but SiblingNameOwner ALSO declares a `collidingTag` property that
        // collides with its `collidingTag(_:)` method, renaming the method on the owner side
        // only. SiblingNamePeer keeps the plain name; the owner receiver's sibling-fallback
        // must call the Peer's OWN name. Both conformances are reverse-dispatched by
        // SiblingMethodDispatchTests, so their witness-table getters must survive stripping.
        "SiblingNameOwner",
        "SiblingNamePeer",
        // Async/sync effect-overload sibling divergence (Kingfisher regression): a sync
        // protocol (SyncRefineModifier) refines an async one (AsyncRefineModifierBase),
        // both declaring refineModify, which differ only in the `async` effect. The sync
        // requirement is reverse-dispatched through `any SyncRefineModifier` by
        // SiblingMethodDispatchTests, so the EveryProtocol witness-table getter for the
        // SyncRefineModifier conformance must survive stripping.
        "AsyncRefineModifierBase",
        "SyncRefineModifier",
        // Unrelated (non-refining) async/sync sibling divergence: two INDEPENDENT protocols
        // (no refinement between them) declare mixedFanModify differing only in the `async`
        // effect. "Async" < "Sync", so MixedFanAsyncOwner is the lex-min OWNER that emits the
        // shared sync witness body; MixedFanSyncPeer gets an EMPTY extension that borrows it.
        // SiblingMethodDispatchTests reverse-dispatches the sync requirement through
        // `any MixedFanSyncPeer`, so Get_EveryProtocol_MixedFanSyncPeer_WitnessTable must
        // resolve — and because the peer extension is empty, the OWNER extension carrying the
        // mixedFanModify witness must survive too. Preserve BOTH (same as the refine pair
        // above) so the borrowed witness is never stripped out from under the peer.
        "MixedFanAsyncOwner",
        "MixedFanSyncPeer",
        // Intra-protocol async/sync effect-overload (audit §6 #12, Kingfisher parity). A
        // SINGLE protocol declares both `intraEffectTag(_:) -> Int32` and
        // `intraEffectTag(_:) async -> Int32`, occupying two distinct vtable slots. The C#
        // proxy implements both members; IntraProtocolEffectOverloadTests reverse-dispatches
        // the SYNC requirement through `any IntraEffectTagged` via callIntraEffectTagSync, so
        // the EveryProtocol conformance and Get_EveryProtocol_IntraEffectTagged_WitnessTable
        // must survive stripping for the existential-construction P/Invoke to resolve. This is
        // the intra-protocol twin of the AsyncRefineModifierBase/SyncRefineModifier pair above.
        "IntraEffectTagged",
        // Audit P1-08 WRITE direction: a C# class implements MarkerProvider and is vended
        // to Swift through consumeMarkerProvider. The marshaller wraps the C# conformer in
        // the EveryProtocol-backed proxy, so Get_EveryProtocol_MarkerProvider_WitnessTable
        // must resolve; Swift then reads the getter's `[any Marker]` (class-bound) array back
        // through the EveryProtocol vtable. The conformance is fully valid (a plain get-only
        // property), it is only stripped because nothing referenced it before this test, so
        // both the conformance and its witness-table getter must survive stripping. Marker
        // itself is NOT listed: its elements are only ever Swift-vended proxies or concrete
        // Swift class boxables (MarkerImpl), never a pure-C# IMarker, so its EveryProtocol
        // conformance is never exercised.
        "MarkerProvider",
        // Audit P1-08 dict-value sibling: same WRITE direction as MarkerProvider but the
        // requirement is `var markerMap: [String: any Marker] { get }`. A C# class implements
        // MarkerMapProvider and is vended to Swift through consumeMarkerMapProvider, so
        // Get_EveryProtocol_MarkerMapProvider_WitnessTable must resolve; Swift then reads the
        // getter's [String: any Marker] (class-bound value) dictionary back through the
        // EveryProtocol vtable. Like MarkerProvider the conformance is fully valid (a plain
        // get-only property) and is only stripped because nothing referenced it before this
        // test, so both the conformance and its witness-table getter must survive stripping.
        "MarkerMapProvider",
        // Audit L229 nested sibling: same WRITE/reverse-dispatch direction as MarkerProvider but the
        // requirement is `var markerGrid: [[any Marker]] { get }` (a NESTED class-bound existential
        // collection). A C# class implements NestedMarkerProvider and is vended to Swift through
        // consumeNestedMarkerProvider, so Get_EveryProtocol_NestedMarkerProvider_WitnessTable must
        // resolve; Swift then reads the getter's [[any Marker]] grid back through the EveryProtocol
        // vtable, exercising the recursive owned-adoption receiver conversion. Stripped otherwise
        // because nothing referenced it before this test, so the conformance and its witness-table
        // getter must survive stripping.
        "NestedMarkerProvider",
        // Audit L229 READ/method-param sibling of NestedMarkerProvider: the requirement is
        // `func consume(grid: [[String: any Marker]]) -> Int` (a NESTED class-bound existential
        // collection as an incoming METHOD PARAM, not a getter). A C# class implements
        // NestedMarkerMapConsumer and is vended to Swift through driveNestedMarkerMapConsumer, so
        // Get_EveryProtocol_NestedMarkerMapConsumer_WitnessTable must resolve; Swift then calls the
        // method through the EveryProtocol vtable and the generated receiver materializes the
        // [[String: any Marker]] param (Swift→C# READ) before handing it to the C# impl — the exact
        // FirebaseFirestore mapMerge([[String: Any]]) reverse-dispatch path. Stripped otherwise
        // because nothing referenced it before this test, so the conformance and its witness-table
        // entry must survive stripping.
        "NestedMarkerMapConsumer",
        // Audit L229 SETTER sibling: the requirement is a SETTABLE
        // `var markerMapGrid: [String: [String: any Marker]] { get set }` (a NESTED class-bound existential
        // dictionary VALUE). A C# class implements MutableMarkerMapGridHolder and is vended to Swift through
        // writeAndSumMarkerMapGrid, so Get_EveryProtocol_MutableMarkerMapGridHolder_WitnessTable must
        // resolve; Swift ASSIGNS the dict-of-dict grid through the EveryProtocol vtable SETTER (the receiver
        // setter converts the SwiftDictionary into the impl's invariant
        // IReadOnlyDictionary<…, IReadOnlyDictionary<…>> param — CS0266 without the shared invariant-slot
        // cast). Stripped otherwise because nothing referenced it before this test, so the conformance and
        // its witness-table entry must survive stripping.
        "MutableMarkerMapGridHolder",
    };

    private static readonly Regex PreservedProtocolPattern = new(
        @"\b(" + string.Join("|", PreservedProtocols.Select(Regex.Escape)) + @")\b",
        RegexOptions.Compiled);

    private static readonly Regex ClosureParamPattern = new(
        @",\s*\w+:\s*\([^)]*\)\s*->", RegexOptions.Compiled);

    private static readonly Regex EveryProtocolExtensionHeader = new(
        @"^\s*extension\s+EveryProtocol\s*:\s*(?:[\w.]+\.)?(\w+)\b", RegexOptions.Compiled);

    // Captures `public [modifiers] func name(params)`, `public [modifiers] var/let name`,
    // or `public [modifiers] subscript`. `kind` discriminates so cross-extension witness
    // checks only match like-with-like (var-vs-func with the same bare name is a redeclaration,
    // not a witness — see `ProvidesCrossExtensionWitness`).
    private static readonly Regex DeclaredMember = new(
        @"\bpublic\s+(?:static\s+|nonisolated\s+|final\s+)*(?:(?<kind>func|var|let)\s+(?<name>\w+)|(?<sub>subscript)\b)",
        RegexOptions.Compiled);

    // `fileprivate struct <Protocol>_vtable {` — protocol witness vtable struct header.
    private static readonly Regex VtableStructHeader = new(
        @"^\s*fileprivate\s+struct\s+(\w+)_vtable\s*\{", RegexOptions.Compiled);

    // `var func_<barename>_get|set|<digit>` inside a vtable struct.
    // Suffix `_get`/`_set` → property (var); suffix `_<digit>` → method (func).
    // Greedy `.+` so names containing underscores (e.g. `snake_case`) survive intact.
    private static readonly Regex VtableField = new(
        @"\bvar\s+func_(?<name>.+)_(?<suffix>get|set|\d+)\b", RegexOptions.Compiled);

    // `@_cdecl("Get_EveryProtocol_<Protocol>_WitnessTable")` — the witness-table getter
    // that binds `var proto: any <Protocol> = instance`. It is the ONLY symbol that depends
    // on the `extension EveryProtocol: <Protocol>` conformance actually existing; no other
    // strip pattern catches it (it is a plain `public func`, not `SBW_`/`PInvoke_`). When
    // Pattern 1 removes the conformance extension this getter is orphaned — swiftc rejects
    // its `any <Protocol>` bind, and the coarse line-based retry strip then deletes the
    // error-enclosing functions, which cascades into UNRELATED symbols in the same file
    // (observed: a stripped same-signature loser conformance taking out an unrelated
    // `..._init`, surfacing at runtime as EntryPointNotFoundException). Strip the getter in
    // lock-step with its extension. The captured name is the unqualified protocol name, which
    // matches EveryProtocolExtensionHeader's capture for the common (non-nested) case.
    private static readonly Regex WitnessTableGetterCdecl = new(
        @"^\s*@_cdecl\(""Get_EveryProtocol_(\w+)_WitnessTable""\)", RegexOptions.Compiled);

    /// <summary>
    /// Result of stripping a single file.
    /// </summary>
    public record StripResult(string OutputPath, int StrippedCount);

    /// <summary>
    /// Strips broken wrapper code from a Swift source file and writes the cleaned version.
    /// Returns the number of blocks stripped.
    /// </summary>
    public static StripResult StripFile(string inputPath, string outputPath)
    {
        var lines = File.ReadAllLines(inputPath);
        var outputLines = new List<string>();
        int removedCount = 0;
        int i = 0;
        bool seenUtf8Slice = false;
        bool seenEmptyBuffer = false;

        // Pre-scan: figure out which bare member names a preserved EveryProtocol conformance
        // depends on but doesn't declare in its own extension body. EveryProtocolEmitter dedups
        // same-signature witnesses across protocols by emitting the body in only one extension
        // and leaving siblings without it; Swift normally satisfies the empty conformance via
        // cross-extension method visibility. If the *only* extension declaring the witness is
        // a non-preserved one, stripping it here breaks the preserved sibling's conformance
        // with no recoverable error pattern. We keep a non-preserved extension only when it
        // declares at least one of those missing required names.
        var crossExtensionRequired = CollectCrossExtensionRequiredNames(lines);

        // Pre-scan the conformance extensions Pattern 1 will strip so the orphaned
        // witness-table getter (Pattern 1b below) can be removed in lock-step. Uses the
        // exact same decision predicate as Pattern 1 (ShouldStripEveryProtocolBlock) so the
        // two never disagree — a getter stripped for a kept conformance would itself break
        // compile.
        var strippedExtensionProtocols = CollectStrippedExtensionProtocols(lines, crossExtensionRequired);

        while (i < lines.Length)
        {
            var line = lines[i];
            var stripped = line.Trim();

            // Pattern 1: Skip EveryProtocol conformance extensions and class definition,
            // EXCEPT those for preserved protocols needed for runtime testing.
            if (stripped.StartsWith("extension EveryProtocol") || stripped.StartsWith("class EveryProtocol"))
            {
                int end = FindBlockEnd(lines, i);
                if (ShouldStripEveryProtocolBlock(lines, i, end, crossExtensionRequired))
                {
                    removedCount++;
                    i = end + 1;
                    continue;
                }
            }

            // Pattern 1b: strip the witness-table getter for any EveryProtocol conformance
            // Pattern 1 removed. Without this the getter's `any <Protocol> = instance` bind is
            // orphaned and the retry strip cascades into unrelated symbols. Safe because a
            // non-preserved conformance has no runtime test reverse-dispatching it, so its
            // getter is never P/Invoked.
            var wtGetterMatch = WitnessTableGetterCdecl.Match(line);
            if (wtGetterMatch.Success && strippedExtensionProtocols.Contains(wtGetterMatch.Groups[1].Value))
            {
                int end = FindBlockEnd(lines, i);
                removedCount++;
                i = end + 1;
                continue;
            }

            // Pattern 2: Skip @_silgen_name + function blocks that have broken patterns.
            if (stripped.StartsWith("@_silgen_name("))
            {
                int end = FindBlockEnd(lines, i);
                var body = ScanBlockBody(lines, i, end);

                if (IsBrokenSilgenBlock(lines, i, end, body))
                {
                    removedCount++;
                    i = end + 1;
                    continue;
                }
            }

            // Pattern 3: Skip extension blocks that contain broken code.
            if (stripped.StartsWith("extension ") && !stripped.StartsWith("extension EveryProtocol"))
            {
                int end = FindBlockEnd(lines, i);
                var body = ScanBlockBody(lines, i, end);

                if (IsBrokenExtensionBlock(body))
                {
                    removedCount++;
                    i = end + 1;
                    continue;
                }
            }

            // Pattern 4: Standalone public func blocks (without @_silgen_name prefix)
            if (stripped.StartsWith("public func SBW_") || stripped.StartsWith("public func PInvoke_"))
            {
                int end = FindBlockEnd(lines, i);
                var body = ScanBlockBody(lines, i, end);

                bool broken = false;
                if (body.Contains("EveryProtocol()"))
                {
                    if (!ReferencesPreservedProtocol(body))
                        broken = true;
                }
                if (!broken && body.Contains("let existential") && body.Contains("existential.") && body.Contains(".load(as: (any "))
                    broken = true;

                if (broken)
                {
                    removedCount++;
                    i = end + 1;
                    continue;
                }
            }

            // Fix: Strip @escaping from return type position
            if (line.Contains(") -> @escaping "))
                line = line.Replace(") -> @escaping ", ") -> ");

            // Fix: Strip @escaping from .load(as:) type context
            if (line.Contains(".load(as: @escaping "))
                line = line.Replace(".load(as: @escaping ", ".load(as: ");

            // Dedup: Skip duplicate SBW_Utf8Slice / _sbw_emptyBuffer declarations
            bool isUtf8SliceBlock = false;
            if (stripped.StartsWith("public struct SBW_Utf8Slice"))
            {
                isUtf8SliceBlock = true;
            }
            else if (stripped == "@frozen" && i + 1 < lines.Length && lines[i + 1].Contains("SBW_Utf8Slice"))
            {
                isUtf8SliceBlock = true;
            }

            if (isUtf8SliceBlock)
            {
                if (seenUtf8Slice)
                {
                    int end = FindBlockEnd(lines, i);
                    i = end + 1;
                    continue;
                }
                if (stripped.StartsWith("public struct SBW_Utf8Slice"))
                    seenUtf8Slice = true;
            }

            if (stripped.StartsWith("fileprivate var _sbw_emptyBuffer") || stripped.StartsWith("private var _sbw_emptyBuffer"))
            {
                if (seenEmptyBuffer)
                {
                    i++;
                    continue;
                }
                seenEmptyBuffer = true;
            }

            outputLines.Add(line);
            i++;
        }

        File.WriteAllLines(outputPath, outputLines);
        return new StripResult(outputPath, removedCount);
    }

    /// <summary>
    /// Strips broken functions from cleaned files based on compilation error line numbers.
    /// Used in the retry loop when initial compilation fails.
    /// </summary>
    public static int StripErrorFunctions(string cleanedDir, string compileErrors)
    {
        // Parse error line numbers per file
        var fileErrorLines = new Dictionary<string, HashSet<int>>();
        var errorPattern = new Regex(@"(.+\.swift):(\d+):\d+: error:");

        foreach (var errorLine in compileErrors.Split('\n'))
        {
            var match = errorPattern.Match(errorLine);
            if (match.Success)
            {
                var filename = Path.GetFileName(match.Groups[1].Value);
                var lineno = int.Parse(match.Groups[2].Value);
                if (!fileErrorLines.ContainsKey(filename))
                    fileErrorLines[filename] = new HashSet<int>();
                fileErrorLines[filename].Add(lineno);
            }
        }

        int totalStripped = 0;
        foreach (var (filename, errorLines) in fileErrorLines)
        {
            var filepath = Path.Combine(cleanedDir, filename);
            if (!File.Exists(filepath))
                continue;

            var lines = File.ReadAllLines(filepath);

            // Identify function blocks containing error lines
            var blocksToStrip = new HashSet<(int Start, int End)>();
            int idx = 0;
            while (idx < lines.Length)
            {
                var strippedLine = lines[idx].Trim();
                if (strippedLine.StartsWith("@_cdecl(") || strippedLine.StartsWith("@_silgen_name(")
                    || strippedLine.StartsWith("public func SBW_") || strippedLine.StartsWith("public func PInvoke_")
                    || strippedLine.StartsWith("public func _sbw_"))
                {
                    int end = FindBlockEnd(lines, idx);
                    foreach (var eline in errorLines)
                    {
                        // Error lines are 1-based, our indices are 0-based
                        if (idx + 1 <= eline && eline <= end + 1)
                        {
                            blocksToStrip.Add((idx, end));
                            break;
                        }
                    }
                    idx = end + 1;
                }
                else
                {
                    idx++;
                }
            }

            if (blocksToStrip.Count == 0)
                continue;

            // Walk backwards to include decorators and comments
            var expandedBlocks = new HashSet<(int Start, int End)>();
            foreach (var (start, end) in blocksToStrip)
            {
                int actualStart = start;
                while (actualStart > 0)
                {
                    var prev = lines[actualStart - 1].Trim();
                    if (prev.StartsWith("@_cdecl(") || prev.StartsWith("@_silgen_name(")
                        || prev.StartsWith("//") || prev.StartsWith("@MainActor"))
                    {
                        actualStart--;
                    }
                    else
                    {
                        break;
                    }
                }
                expandedBlocks.Add((actualStart, end));
            }

            var skipLines = new HashSet<int>();
            foreach (var (start, end) in expandedBlocks)
            {
                for (int j = start; j <= end; j++)
                    skipLines.Add(j);
            }

            var outputLines = lines.Where((_, j) => !skipLines.Contains(j)).ToArray();
            File.WriteAllLines(filepath, outputLines);

            totalStripped += expandedBlocks.Count;
            Log.Debug("Stripped {Count} broken function(s) from {File}", expandedBlocks.Count, filename);
        }

        return totalStripped;
    }

    /// <summary>
    /// Find the end of a brace-delimited block starting at `start`.
    /// </summary>
    private static int FindBlockEnd(string[] lines, int start)
    {
        int depth = 0;
        bool seenOpen = false;
        for (int j = start; j < lines.Length; j++)
        {
            depth += lines[j].Count(c => c == '{') - lines[j].Count(c => c == '}');
            if (lines[j].Contains('{'))
                seenOpen = true;
            if (seenOpen && depth <= 0 && j > start)
                return j;
        }
        return lines.Length - 1;
    }

    /// <summary>
    /// Return concatenated text of lines[start..end].
    /// </summary>
    private static string ScanBlockBody(string[] lines, int start, int end)
    {
        return string.Join("\n", lines.Skip(start).Take(end - start + 1));
    }

    private static bool ReferencesPreservedProtocol(string body)
    {
        return PreservedProtocolPattern.IsMatch(body);
    }

    /// <summary>
    /// The shared decision predicate for Pattern 1: a non-preserved EveryProtocol block whose
    /// body neither references a preserved protocol nor supplies a cross-extension witness for
    /// a preserved sibling is dropped. Extracted so the Pattern 1b getter pre-scan
    /// (<see cref="CollectStrippedExtensionProtocols"/>) and the inline Pattern 1 strip stay in
    /// lock-step — if they ever diverged, a stripped getter could outlive its conformance (or
    /// vice-versa), reintroducing the orphan-compile cascade this is meant to prevent.
    /// </summary>
    private static bool ShouldStripEveryProtocolBlock(
        string[] lines, int start, int end, HashSet<WitnessKey> crossExtensionRequired)
    {
        var body = ScanBlockBody(lines, start, end);
        return !ReferencesPreservedProtocol(body)
            && !ProvidesCrossExtensionWitness(body, crossExtensionRequired);
    }

    /// <summary>
    /// Pre-scans which <c>extension EveryProtocol: P</c> blocks Pattern 1 will strip and returns
    /// their (unqualified) protocol names. The matching <c>Get_EveryProtocol_P_WitnessTable</c>
    /// getter binds <c>any P = instance</c> and only compiles while the conformance survives;
    /// it is caught by no other strip pattern, so when its extension is removed the getter is
    /// orphaned, fails to compile, and the line-based retry strip cascades into unrelated
    /// symbols. Collecting the names here lets <see cref="StripFile"/> drop the getter in
    /// lock-step. Only the <c>extension</c> form (not the bare <c>class EveryProtocol</c>) is
    /// scanned, since only conformance extensions back a witness-table getter.
    /// </summary>
    private static HashSet<string> CollectStrippedExtensionProtocols(
        string[] lines, HashSet<WitnessKey> crossExtensionRequired)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        int i = 0;
        while (i < lines.Length)
        {
            var match = EveryProtocolExtensionHeader.Match(lines[i]);
            if (match.Success)
            {
                int end = FindBlockEnd(lines, i);
                if (ShouldStripEveryProtocolBlock(lines, i, end, crossExtensionRequired))
                    result.Add(match.Groups[1].Value);
                i = end + 1;
                continue;
            }
            i++;
        }
        return result;
    }

    /// <summary>
    /// (Kind, Name) tuple identifying a witness slot. Kind is the normalised member shape:
    /// "method", "property", or "subscript". A non-preserved extension only counts as a
    /// cross-extension witness source if it declares a member with the *same* kind as the
    /// missing requirement — otherwise we'd over-preserve unrelated extensions whose bare
    /// name happens to collide.
    /// </summary>
    private readonly record struct WitnessKey(string Kind, string Name);

    /// <summary>
    /// Builds the set of (kind, bare-name) pairs that some preserved EveryProtocol conformance
    /// requires (per its `<Protocol>_vtable` struct) but that the preserved extension body
    /// does not itself declare. Those are the slots whose witness must come from a sibling
    /// extension via Swift's cross-extension method-visibility rule. Stripping the sibling
    /// would silently break the preserved conformance, so we keep any non-preserved extension
    /// that declares a kind+name match.
    /// </summary>
    private static HashSet<WitnessKey> CollectCrossExtensionRequiredNames(string[] lines)
    {
        // Step 1: vtable struct → required (kind, bare-name) pairs per protocol.
        var protocolRequired = new Dictionary<string, HashSet<WitnessKey>>(StringComparer.Ordinal);
        for (int idx = 0; idx < lines.Length; idx++)
        {
            var headerMatch = VtableStructHeader.Match(lines[idx]);
            if (!headerMatch.Success) continue;

            var protocolName = headerMatch.Groups[1].Value;
            int end = FindBlockEnd(lines, idx);
            var required = new HashSet<WitnessKey>();
            for (int j = idx + 1; j < end; j++)
            {
                var fieldMatch = VtableField.Match(lines[j]);
                if (!fieldMatch.Success) continue;
                var key = MakeVtableWitnessKey(fieldMatch.Groups["name"].Value, fieldMatch.Groups["suffix"].Value);
                required.Add(key);
            }
            if (required.Count > 0)
                protocolRequired[protocolName] = required;
            idx = end;
        }

        // Step 2: walk preserved extensions, subtract the (kind, name) pairs they actually
        // declare, collect the leftover requirements — those must be supplied cross-extension.
        var missing = new HashSet<WitnessKey>();
        for (int idx = 0; idx < lines.Length; idx++)
        {
            var match = EveryProtocolExtensionHeader.Match(lines[idx]);
            if (!match.Success) continue;

            int end = FindBlockEnd(lines, idx);
            var protocolName = match.Groups[1].Value;
            if (PreservedProtocols.Contains(protocolName)
                && protocolRequired.TryGetValue(protocolName, out var required))
            {
                var body = ScanBlockBody(lines, idx, end);
                var declared = CollectDeclaredWitnessKeys(body);
                foreach (var slot in required)
                    if (!declared.Contains(slot))
                        missing.Add(slot);
            }
            idx = end;
        }
        return missing;
    }

    /// <summary>
    /// Builds the WitnessKey for a vtable field. Properties surface as `func_<name>_get/_set`
    /// (suffix get/set, name = the property name). Methods surface as `func_<name>_<index>`
    /// (digit suffix). Subscripts are emitted by EveryProtocolEmitter as
    /// `func_subscript_<index>_get/_set`, so the parsed (name, suffix) is
    /// (<c>subscript_&lt;index&gt;</c>, <c>get|set</c>) — both name and kind are normalized to
    /// the literal "subscript" so they collate with the declared `public subscript` side.
    ///
    /// <para>Design note — subscript single-key collation: the vtable side carries a
    /// per-protocol index in the field name; the declared side (`public subscript(...)`) has
    /// no index. So both are normalized to <c>("subscript", "subscript")</c>. The result is
    /// that ALL declared subscripts in a non-preserved extension count as cross-extension
    /// witnesses for ANY required subscript in a preserved sibling. This deliberately favors
    /// OVER-preservation (a non-preserved extension declaring an unrelated subscript is kept)
    /// over a signature-based match. Doing signature matching would require parsing Swift
    /// type tuples out of the vtable field's function-type tail and the declared subscript's
    /// parameter list, with all their corner cases (closure params, generic substitutions,
    /// label-vs-internal-name distinctions). A regex-layer attempt at that risks the opposite
    /// failure mode — UNDER-preservation, where the stripper drops an extension that does
    /// witness a required subscript, breaking compile. The safe single-key shape is preferred
    /// while the stripper remains text-based.</para>
    /// </summary>
    private static WitnessKey MakeVtableWitnessKey(string name, string suffix)
    {
        if (SubscriptVtableName.IsMatch(name))
            return new WitnessKey("subscript", "subscript");
        if (suffix == "get" || suffix == "set")
            return new WitnessKey("property", name);
        return new WitnessKey("method", name);
    }

    private static readonly Regex SubscriptVtableName = new(@"^subscript(_\d+)?$", RegexOptions.Compiled);

    private static HashSet<WitnessKey> CollectDeclaredWitnessKeys(string body)
    {
        var declared = new HashSet<WitnessKey>();
        foreach (Match m in DeclaredMember.Matches(body))
        {
            if (m.Groups["sub"].Success)
            {
                declared.Add(new WitnessKey("subscript", "subscript"));
                continue;
            }
            var kind = m.Groups["kind"].Value switch
            {
                "func" => "method",
                "var" or "let" => "property",
                _ => null,
            };
            if (kind == null) continue;
            declared.Add(new WitnessKey(kind, m.Groups["name"].Value));
        }
        return declared;
    }

    /// <summary>
    /// Returns true when a non-preserved EveryProtocol extension declares a (kind, bare-name)
    /// pair that some preserved sibling needs but doesn't declare itself. That makes this
    /// extension the cross-extension witness source — stripping it would break compile.
    /// Kind discrimination prevents an unrelated `describe` property in a non-preserved
    /// extension from being kept just because some preserved protocol needs a `describe()`
    /// method.
    /// </summary>
    private static bool ProvidesCrossExtensionWitness(string body, HashSet<WitnessKey> crossExtensionRequired)
    {
        if (crossExtensionRequired.Count == 0) return false;
        foreach (var key in CollectDeclaredWitnessKeys(body))
        {
            if (crossExtensionRequired.Contains(key))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns true when the line at <paramref name="start"/> is nested inside an
    /// `extension TypeName { ... }` block. Scans upward: the nearest unmatched `{`
    /// that sits on a line starting with `extension ` means we're in an extension.
    /// </summary>
    private static bool IsInsideExtension(string[] lines, int start)
    {
        int depth = 0;
        for (int j = start - 1; j >= 0; j--)
        {
            var trimmed = lines[j].Trim();
            foreach (var c in trimmed)
            {
                if (c == '}') depth++;
                else if (c == '{') depth--;
            }
            if (depth < 0)
            {
                return trimmed.StartsWith("extension ");
            }
        }
        return false;
    }

    private static bool IsBrokenSilgenBlock(string[] lines, int start, int end, string body)
    {
        // (a) EveryProtocol() — protocol witness dispatch for unimplemented conformances
        if (body.Contains("EveryProtocol()"))
        {
            if (!ReferencesPreservedProtocol(body))
                return true;
        }

        // (b) self.functionName() in free function (no _self: parameter).
        // NestedClosureBridge emits its wrapper inside `extension TypeName { @_silgen_name ... }`,
        // where `self` is a valid reference to the instance. Skip rule (b) when the silgen block
        // is nested inside an extension declaration.
        if (!body.Contains("_self:") && !body.Contains("_self :") && !IsInsideExtension(lines, start))
        {
            for (int j = start; j <= end; j++)
            {
                var s = lines[j].Trim();
                if (s.StartsWith("self.") || s.Contains(" self.") || s.Contains("\tself."))
                    return true;
            }
        }

        // (c) __self.init( — async init wrapper (invalid Swift)
        if (body.Contains("__self.init("))
            return true;

        // (d) mutating member on let existential
        if (body.Contains(".load(as: (any ") && body.Contains("existential.") && body.Contains("let existential"))
            return true;

        // (e) Non-escaping closure param passed to Task (async closure methods)
        if (body.Contains("Task {"))
        {
            int sigEnd = body.IndexOf('{');
            if (sigEnd > 0)
            {
                var sig = body[..sigEnd];
                if (ClosureParamPattern.IsMatch(sig))
                    return true;
            }
        }

        return false;
    }

    private static bool IsBrokenExtensionBlock(string body)
    {
        if (body.Contains("EveryProtocol()"))
        {
            if (!ReferencesPreservedProtocol(body))
                return true;
        }

        if (body.Contains("__self.init("))
            return true;

        // Non-escaping closure in Task
        if (body.Contains("Task {"))
        {
            int taskIdx = body.IndexOf("Task {");
            var beforeTask = body[..taskIdx];
            if (ClosureParamPattern.IsMatch(beforeTask))
                return true;
        }

        return false;
    }
}
