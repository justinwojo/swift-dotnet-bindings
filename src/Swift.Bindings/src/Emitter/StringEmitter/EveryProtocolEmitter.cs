// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Emits Swift code for the EveryProtocol pattern.
/// This enables C# code to implement Swift protocols by:
/// 1. Defining an EveryProtocol class that serves as the concrete type behind protocol proxies
/// 2. Generating protocol extensions that call back to C# via vtable function pointers
/// 3. Creating vtable structures that store function pointers for each protocol method
/// 4. Providing SetVtable functions that C# calls to register its vtable with Swift
/// </summary>
public class EveryProtocolEmitter
{
    private readonly ITypeDatabase _typeDatabase;
    private readonly ILogger _logger;
    private readonly string _moduleName;
    private readonly ModuleEmissionContext? _emissionContext;

    /// <summary>
    /// Tracks protocols whose EveryProtocol conformance was skipped.
    /// Used to detect genericSig constraints that reference unsatisfied protocols.
    /// </summary>
    private readonly HashSet<string> _skippedProtocols = new(StringComparer.Ordinal);

    /// <summary>
    /// Module-local protocols that were never offered for EveryProtocol emission at all — they lost
    /// the module-level candidacy filter before <see cref="PreScanProtocols"/> ever saw them, so no
    /// <c>extension {carrier}: {P}</c> exists for them even though nothing here "skipped" them.
    ///
    /// Deliberately SEPARATE from <see cref="_skippedProtocols"/>: that set drives
    /// <see cref="IsConformanceSkipped"/>, which the sibling-plan and cross-module-parent retention
    /// paths read by SIMPLE name. Folding these in would let a dropped local protocol shadow a
    /// live cross-module parent that happens to share its simple name. Only the inherited-conformance
    /// predicate consults this set.
    /// </summary>
    private readonly HashSet<string> _unavailableConformances = new(StringComparer.Ordinal);

    /// <summary>
    /// Module-local protocol list set during <see cref="PreScanProtocols"/>. Used by
    /// <see cref="IsClassBoundProtocol(ProtocolDecl, IReadOnlyList{ProtocolDecl}?)"/> and
    /// <see cref="IsNSObjectProtocolOnly(ProtocolDecl, IReadOnlyList{ProtocolDecl}?)"/>
    /// at routing checkpoints so a module-local protocol inheriting another module-local
    /// NSObjectProtocol-rooted protocol is correctly routed through EveryObjCProtocol
    /// rather than being treated as a non-class-bound stranger.
    /// </summary>
    private IReadOnlyList<ProtocolDecl>? _allProtocols;

    /// <summary>
    /// Cross-module parent protocol decls (from <c>--framework-dependency</c> modules) that a
    /// module-local child inherits across the boundary, captured during
    /// <see cref="PreScanProtocols"/>. These are NOT in <see cref="_allProtocols"/> (which holds
    /// only the module-local <c>suitableProtocols</c>), but each one HAS its own EveryProtocol
    /// conformance emitted on a carrier chosen by <see cref="GetCarrierClassName"/> — so
    /// <see cref="AnyInheritedProtocolOnDifferentCarrier"/> must resolve inherited names against
    /// this list too, or a cross-module carrier split is silently missed and the child's
    /// <c>extension {carrier}: Child</c> is emitted without a witness for the cross-module parent
    /// requirement, failing wrapper compilation.
    /// </summary>
    private IReadOnlyList<ProtocolDecl>? _crossModuleParents;

    /// <summary>
    /// True while emitting an NSObjectProtocol-only conformance: the Swift extension
    /// must hang off <c>EveryObjCProtocol</c> (NSObject-rooted) rather than the plain
    /// Swift <c>EveryProtocol</c>. Reset between protocols by
    /// <see cref="EmitProtocolConformance(SwiftWriter, ProtocolDecl, HashSet{string}?, HashSet{string}?)"/>.
    /// </summary>
    private bool _useObjCBase;

    /// <summary>
    /// True while emitting an Entity-rooted conformance (Failure B): the Swift
    /// extension must hang off <c>EveryEntityProtocol</c> (RealityFoundation.Entity-
    /// rooted) rather than the plain Swift <c>EveryProtocol</c>, because the
    /// protocol declares <c>: Entity</c> as a class-superclass constraint
    /// (e.g. <c>HasAnchoring</c>, RealityKit gesture protocols). Reset between
    /// protocols alongside <see cref="_useObjCBase"/>.
    /// </summary>
    private bool _useEntityBase;

    /// <summary>
    /// Transitive closure (by simple name) of the protocols RealityFoundation.Entity
    /// already conforms to, computed lazily from the Entity TypeRecord's
    /// <see cref="TypeRecord.ProtocolConformances"/>. The Entity-rooted carrier
    /// (<c>EveryEntityProtocol</c>) subclasses Entity, so it inherits every one of
    /// these conformances; re-declaring any of them via <c>extension
    /// EveryEntityProtocol: P</c> is a redundant-conformance error in swiftc.
    /// Null until first computed.
    /// </summary>
    private HashSet<string>? _entityBaseConformanceClosure;

    /// <summary>
    /// Swift identifier of the base class to emit the current protocol's extension
    /// against — <c>EveryEntityProtocol</c> when routing through the Entity-rooted
    /// helper (Failure B), <c>EveryObjCProtocol</c> for the NSObjectProtocol-only
    /// path, otherwise the default <c>EveryProtocol</c>. Mutually exclusive:
    /// only one of <see cref="_useObjCBase"/> / <see cref="_useEntityBase"/> can be
    /// true for a given protocol because the routing gates in
    /// <see cref="EmitProtocolConformance(SwiftWriter, ProtocolDecl, HashSet{string}?, HashSet{string}?, IReadOnlyDictionary{string, PropertyEmissionPlan}?, IReadOnlyDictionary{ValueTuple{string, string}, SubscriptEmissionPlan}?)"/>
    /// fall through in sequence (class-bound NSObjectProtocol first, then class-
    /// superclass requirement) and each <c>return</c>s on a skip rather than
    /// re-evaluating the next gate.
    /// </summary>
    private string BaseClassName => _useEntityBase
        ? "EveryEntityProtocol"
        : (_useObjCBase ? "EveryObjCProtocol" : "EveryProtocol");

    /// <summary>
    /// Separator joining a carrier-class name to a member's signature in the emission-plan and
    /// sibling-fallback group keys. A control character that cannot appear in a Swift signature
    /// or in a carrier identifier, so the carrier prefix is never confusable with the signature.
    /// </summary>
    private const string CarrierKeySeparator = "\u0001";

    /// <summary>
    /// Resolves which umbrella carrier class a protocol's EveryProtocol conformance is emitted on
    /// — <c>EveryEntityProtocol</c> (RealityFoundation.Entity-rooted), <c>EveryObjCProtocol</c>
    /// (NSObjectProtocol-only @objc), or the plain <c>EveryProtocol</c>. Mirrors the per-protocol
    /// routing decision in <see cref="EmitProtocolConformance"/> exactly — same
    /// <see cref="_allProtocols"/> / <see cref="_typeDatabase"/> inputs and the same
    /// Entity-over-ObjC precedence as <see cref="BaseClassName"/> — so the member-emission plans
    /// and sibling-fallback maps can partition members by the concrete Swift class their witness
    /// will live on.
    ///
    /// <para>Swift cross-extension witness resolution only satisfies a conformance from a witness
    /// emitted on the SAME concrete type: a body in <c>extension EveryProtocol: X</c> cannot
    /// satisfy <c>extension EveryObjCProtocol: Y</c>, even for an identical signature. So a member
    /// shared by two protocols that route to DIFFERENT carriers must emit its OWN owner body on
    /// each carrier. Merging both into one cross-carrier group picks a single owner on one carrier
    /// and leaves the other carrier's extension empty and unsatisfiable ("does not conform"),
    /// which then fails wrapper compilation. Prefixing every group key with the carrier keeps the
    /// ownership/sibling partition carrier-local so each carrier gets a satisfying witness.</para>
    ///
    /// <para>The rooting helpers below (<see cref="HasClassSuperclassRequirement"/> /
    /// <see cref="IsEntityRootedProtocol"/> / <see cref="IsClassBoundProtocol"/> /
    /// <see cref="IsNSObjectProtocolOnly"/>) walk transitive inheritance through
    /// <see cref="_allProtocols"/> only — NOT <see cref="_crossModuleParents"/>. So when this runs on
    /// a cross-module parent whose OWN NSObjectProtocol/Entity rooting is reachable only through a
    /// SECOND cross-module protocol (invisible to the module-local walk), that parent is classified
    /// as plain <c>EveryProtocol</c> even if its true carrier is <c>EveryObjCProtocol</c> /
    /// <c>EveryEntityProtocol</c> — an intra-module-only transitive approximation. This is
    /// deliberately left as-is because it is self-consistent and fail-closed: the SAME
    /// <see cref="GetCarrierClassName"/> is a pure function of these two inputs and runs at BOTH the
    /// split-detection site (<see cref="AnyInheritedProtocolOnDifferentCarrier"/>) and wherever the
    /// parent's own witness is emitted, so a mis-classification is applied identically on both sides
    /// and can never produce the inconsistent child-carrier-vs-parent-carrier pairing that yields an
    /// unsatisfiable <c>extension {carrier}: Child</c>. The only reachable consequence is
    /// OVER-suppressing a child conformance (both sides read plain <c>EveryProtocol</c> and agree
    /// there is no split when a fully-resolved view might have found one), never emitting a wrapper
    /// that fails to compile. Widening the helpers to search <see cref="_crossModuleParents"/> would
    /// only trade over-suppression for a narrower over-suppression, not unlock a recovered
    /// conformance, so it is not worth the added surface.</para>
    /// </summary>
    private string GetCarrierClassName(ProtocolDecl protocolDecl)
    {
        if (HasClassSuperclassRequirement(protocolDecl, _typeDatabase, _allProtocols)
            && IsEntityRootedProtocol(protocolDecl, _typeDatabase, _allProtocols))
            return "EveryEntityProtocol";
        if (IsClassBoundProtocol(protocolDecl, _allProtocols)
            && IsNSObjectProtocolOnly(protocolDecl, _allProtocols))
            return "EveryObjCProtocol";
        return "EveryProtocol";
    }

    /// <summary>
    /// Returns true if this protocol's EveryProtocol conformance would be emitted on a
    /// DIFFERENT umbrella carrier class than one of its transitively-inherited protocols —
    /// resolved against both the module-local protocols AND the cross-module
    /// (<c>--framework-dependency</c>) parents (see <see cref="ResolveInheritedConformer"/>), since
    /// a cross-module parent gets its own EveryProtocol conformance in this module too. An @objc
    /// protocol that refines another @objc protocol can route to
    /// <c>EveryObjCProtocol</c> (it directly lists <c>NSObjectProtocol</c>) while the parent —
    /// @objc but not itself NSObjectProtocol-rooted — routes to the plain <c>EveryProtocol</c>.
    /// Swift then demands the child's carrier also conform to the inherited parent, but the
    /// parent's witness body was emitted on the other carrier, so
    /// <c>extension EveryObjCProtocol: Child</c> fails to compile ("type 'EveryObjCProtocol'
    /// does not conform to protocol 'Parent'"). Cross-extension witness resolution never bridges
    /// carriers (see <see cref="GetCarrierClassName"/>), so the split cannot be stitched. Suppress
    /// the child conformance fail-closed: recovering it would require re-homing every inherited
    /// @objc ancestor onto the child's carrier (a broad carrier-routing change) for no demonstrated
    /// consumer value. Placed in BOTH the emission ladder and the <see cref="WillSkipConformance"/>
    /// pre-scan so the C# proxy and the sibling-plan input stay in lockstep (mirrors the
    /// <see cref="HasNoncopyableMember(ProtocolDecl)"/> gate's dual placement).
    /// </summary>
    private bool HasCrossCarrierInheritedRequirement(ProtocolDecl protocolDecl)
    {
        if (_allProtocols == null)
            return false;
        var childCarrier = GetCarrierClassName(protocolDecl);
        return AnyInheritedProtocolOnDifferentCarrier(
            protocolDecl, childCarrier, new HashSet<string>(StringComparer.Ordinal));
    }

    private bool AnyInheritedProtocolOnDifferentCarrier(
        ProtocolDecl protocolDecl, string childCarrier, HashSet<string> visited)
    {
        var qualifiedName = protocolDecl.SwiftTypeName?.ToString() ?? protocolDecl.Name;
        if (!visited.Add(qualifiedName))
            return false;

        foreach (var inherited in protocolDecl.InheritedProtocols)
        {
            var simpleName = GetSimpleName(inherited.Name);
            // Resolve the inherited entry to a ProtocolDecl that emits its own EveryProtocol
            // conformance — module-local first, then a cross-module (--framework-dependency)
            // parent. Both carry a separate conformance on a carrier chosen by
            // GetCarrierClassName, so both must be compared against the child's carrier. Stdlib /
            // Foundation roots (NSObjectProtocol, AnyObject, Sendable, …) are in neither list and
            // carry no separate conformance, so they are correctly ignored here — the
            // NSObjectProtocol root is what pushes the child onto EveryObjCProtocol in the first
            // place, not a peer conformance the carrier must re-satisfy.
            var inheritedDecl = ResolveInheritedConformer(simpleName, inherited.Name);
            if (inheritedDecl == null)
                continue;

            // Every transitive ancestor that emits its own conformance must land on the SAME
            // carrier as the child, or the child's `extension {carrier}: Child` is left without a
            // witness for the inherited requirements. Compare each against the ORIGINAL child
            // carrier (not the immediate parent's).
            if (GetCarrierClassName(inheritedDecl) != childCarrier)
                return true;

            if (AnyInheritedProtocolOnDifferentCarrier(inheritedDecl, childCarrier, visited))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Resolves an inherited-protocol reference to the <see cref="ProtocolDecl"/> that emits its
    /// own EveryProtocol conformance: module-local (<see cref="_allProtocols"/>) first, then a
    /// cross-module (<c>--framework-dependency</c>) parent (<see cref="_crossModuleParents"/>).
    /// Returns null for stdlib / Foundation roots (which emit no separate conformance) and for any
    /// parent filtered out of both lists. Restricting resolution to exactly the two lists the
    /// emitter drives <see cref="GetCarrierClassName"/> over keeps the gate's carrier computation
    /// identical to where the parent's witness actually lands.
    ///
    /// <para>Unlike the single-module carrier helpers (which search only <see cref="_allProtocols"/>,
    /// where Swift's same-module name uniqueness makes a bare simple-name match unambiguous),
    /// <see cref="_crossModuleParents"/> can hold two protocols with the SAME simple name from two
    /// different <c>--framework-dependency</c> modules (and a local protocol can share that simple
    /// name too). A module-qualified inherited reference (<c>"Dep2.Parent"</c>) must therefore bind
    /// the decl whose OWN module-qualified name matches — a bare <c>p.Name == simpleName</c> would
    /// return the first-seen namesake (e.g. <c>Dep1.Parent</c> or a local <c>Parent</c>) and read
    /// the wrong carrier, over- or under-detecting the split. So prefer an exact module-qualified
    /// match; only fall back to simple-name resolution when the reference itself is unqualified
    /// (where a single in-scope protocol of that name is the only unambiguous reading).</para>
    /// </summary>
    private ProtocolDecl? ResolveInheritedConformer(string simpleName, string qualifiedName)
    {
        bool MatchesQualified(ProtocolDecl p) =>
            // Bare-name arm. Dead for a module-qualified reference (a candidate's simple Name never
            // equals "Module.Name"), but LOAD-BEARING for an unqualified one: it binds an unqualified
            // reference to a local same-named protocol while still inside the _allProtocols-first
            // scan, so an unqualified local ref cannot leak into a same-simple-named cross-module
            // namesake whose SwiftTypeName happens to render bare. Do NOT drop it as "dead" — the
            // qualified phase would then fall through to _crossModuleParents and mis-resolve.
            p.Name == qualifiedName ||
            p.SwiftTypeName?.ToString() == qualifiedName ||
            QualifiedProtocolName(p) == qualifiedName;

        var qualifiedHit = _allProtocols?.FirstOrDefault(MatchesQualified)
            ?? _crossModuleParents?.FirstOrDefault(MatchesQualified);
        if (qualifiedHit != null)
            return qualifiedHit;

        // No module-qualified match: the reference is unqualified (simpleName == qualifiedName), or
        // the candidate carries no module metadata. Resolve by simple name — an unqualified Swift
        // reference is only well-formed when exactly one protocol of that name is in scope.
        bool MatchesSimple(ProtocolDecl p) => p.Name == simpleName;
        return _allProtocols?.FirstOrDefault(MatchesSimple)
            ?? _crossModuleParents?.FirstOrDefault(MatchesSimple);
    }

    /// <summary>
    /// The module-qualified <c>{Module}.{Name}</c> identity of a protocol, used to disambiguate a
    /// same-simple-named protocol across dependency modules. Prefers the parser-supplied
    /// <see cref="ProtocolDecl.ModuleDecl"/>; falls back to the bare name when no module is set.
    /// </summary>
    private static string QualifiedProtocolName(ProtocolDecl p) =>
        p.ModuleDecl?.Name is { Length: > 0 } module ? $"{module}.{p.Name}" : p.Name;

    public EveryProtocolEmitter(ITypeDatabase typeDatabase, ILogger logger, string moduleName, ModuleEmissionContext? emissionContext = null)
    {
        _typeDatabase = typeDatabase;
        _logger = logger;
        _moduleName = moduleName;
        _emissionContext = emissionContext;
    }

    /// <summary>
    /// Emits the EveryProtocol class definition.
    /// This class is the concrete Swift type behind all protocol proxy objects.
    /// </summary>
    /// <param name="writer">Output Swift writer.</param>
    /// <param name="suitableProtocols">Optional protocol list. When supplied, the emitter
    /// pre-scans the list with <see cref="IsEntityRootedProtocol"/> and conditionally emits
    /// the <c>EveryEntityProtocol</c> Swift class + its four @_cdecl wrappers + records
    /// the Entity-base flag on <see cref="ModuleEmissionContext"/> so per-protocol routing
    /// in <see cref="EmitProtocolConformance(SwiftWriter, ProtocolDecl, HashSet{string}?, HashSet{string}?, IReadOnlyDictionary{string, PropertyEmissionPlan}?, IReadOnlyDictionary{ValueTuple{string, string}, SubscriptEmissionPlan}?)"/>
    /// can opt into the Entity-rooted path. When null (unit-test paths that don't carry
    /// a module protocol list) the Entity-rooted class is not emitted and Entity-rooted
    /// protocols continue to skip via <c>HasClassSuperclassRequirement</c>.</param>
    public void EmitEveryProtocolClass(SwiftWriter writer, IReadOnlyList<ProtocolDecl>? suitableProtocols = null)
    {
        // Pre-scan suitableProtocols (when supplied) so the conditional EveryEntityProtocol
        // emission below can run with the same Entity-rooted set the per-protocol routing
        // will later see. Recording the decision on _emissionContext here keeps
        // EmitProtocolConformance lookups O(1) and lets ProtocolProxyEmitter pick the
        // right helper symbols via UsesEntityBase.
        if (suitableProtocols is not null && _emissionContext is not null)
        {
            foreach (var p in suitableProtocols)
            {
                if (IsEntityRootedProtocol(p, _typeDatabase, suitableProtocols))
                    // Key on the module-qualified name (matching the witness-getter marker) so a
                    // dependency protocol sharing a simple name with a local one cannot collide.
                    _emissionContext.MarkEntityBase(p.SwiftTypeName?.ModuleQualifiedName ?? p.Name);
            }
        }
        bool emitEntityBase = _emissionContext?.AnyEntityBaseUsed == true;

        // Register the four hardcoded EveryProtocol @_cdecl symbols with the
        // wrapper-symbol contract. The matching P/Invokes live in
        // ProtocolProxyEmitter.SwiftObject and would trip the contract check if
        // their callsites later opt into EnforceWrapperContract.
        // four singleton literals — there is exactly one
        // EveryProtocol synthetic type per module build, and these names cannot be
        // produced by any per-type emitter, so the per-kind method bucket is
        // collision-safe.
        _emissionContext?.TryAddMethodWrapperSymbol("SBW_CreateEveryProtocol");
        _emissionContext?.TryAddMethodWrapperSymbol("SBW_ReleaseEveryProtocol");
        _emissionContext?.TryAddMethodWrapperSymbol("SBW_GetMetadata_EveryProtocol");
        _emissionContext?.TryAddMethodWrapperSymbol("SBW_SetEveryProtocolDeinitCallback");
        // The carrier class and its factory @_cdecl symbols above are now defined in this wrapper.
        // A full reverse-dispatch proxy calls SBW_CreateEveryProtocol; record that the carrier exists
        // so the proxy-emission policy only emits full proxies in modules that actually carry it. A
        // module with an empty suitable-protocol set never reaches here, so full proxies there are
        // suppressed rather than left referencing an undefined symbol.
        _emissionContext?.MarkEveryProtocolCarrierEmitted();
        // EveryObjCProtocol mirrors the above for NSObject-rooted @objc protocols:
        // the plain Swift EveryProtocol class cannot satisfy NSObjectProtocol
        // because that requirement transitively demands NSObject identity. The
        // NSObject-rooted variant is generated alongside EveryProtocol whenever
        // EveryProtocolEmitter runs, so the wrapper carries both factories
        // unconditionally — register the symbols to keep the wrapper contract honest.
        _emissionContext?.TryAddMethodWrapperSymbol("SBW_CreateEveryObjCProtocol");
        _emissionContext?.TryAddMethodWrapperSymbol("SBW_ReleaseEveryObjCProtocol");
        _emissionContext?.TryAddMethodWrapperSymbol("SBW_GetMetadata_EveryObjCProtocol");
        _emissionContext?.TryAddMethodWrapperSymbol("SBW_SetEveryObjCProtocolDeinitCallback");
        if (emitEntityBase)
        {
            // EveryEntityProtocol mirrors the EveryProtocol / EveryObjCProtocol pattern
            // for protocols whose only class-superclass requirement is RealityFoundation.Entity
            // (Failure B): the plain Swift EveryProtocol class cannot satisfy
            // `protocol HasAnchoring : Entity` because EveryProtocol does not inherit
            // Entity. The Entity-rooted variant is generated only when at least one
            // protocol in the module is Entity-rooted (Entity is not a universal
            // dependency — a wrapper that does not import RealityFoundation must not
            // emit a class that references Entity), so its wrapper-symbol contract
            // entries are likewise conditional.
            _emissionContext?.TryAddMethodWrapperSymbol("SBW_CreateEveryEntityProtocol");
            _emissionContext?.TryAddMethodWrapperSymbol("SBW_ReleaseEveryEntityProtocol");
            _emissionContext?.TryAddMethodWrapperSymbol("SBW_GetMetadata_EveryEntityProtocol");
            _emissionContext?.TryAddMethodWrapperSymbol("SBW_SetEveryEntityProtocolDeinitCallback");
        }

        writer.WriteLines($$"""
            // EveryProtocol is a Swift class that can conform to any protocol.
            // Protocol method implementations call back to C# via vtable function pointers.
            // This class is used by generated proxy classes to implement Swift protocols from C#.
            //
            // @unchecked Sendable: transitive Sendable conformance flows in from framework
            // protocols (e.g. TipKit.Tip inherits Sendable). onDeinit/onDeinitCtx must stay
            // mutable because SBW_SetEveryProtocolDeinitCallback writes them after init,
            // so strict Sendable checking can't verify them. Safety is enforced by the
            // SwiftObjectRegistry lifetime contract, not by the compiler.
            public final class EveryProtocol: @unchecked Sendable {
                // Store a handle back to the C# proxy object
                // This is used by vtable functions to find the C# implementation
                public let handle: UnsafeRawPointer?

                // Deinit callback fired when Swift's last retain drops. The C# proxy
                // registers this so the SwiftObjectRegistry strong root and the
                // ProxyLifetimeTracker entry can be torn down when Swift is finished
                // with the existential container. Storage is fileprivate to prevent
                // accidental access from outside this module.
                fileprivate var onDeinit: (@convention(c) (UnsafeRawPointer) -> Void)?
                fileprivate var onDeinitCtx: UnsafeRawPointer?

                public init() {
                    self.handle = nil
                }

                public init(handle: UnsafeRawPointer) {
                    self.handle = handle
                }

                deinit {
                    // Idempotent, non-throwing. Runs when Swift's retain count reaches 0.
                    // The C# callback is responsible for short-circuiting on process exit.
                    if let cb = onDeinit, let ctx = onDeinitCtx {
                        cb(ctx)
                    }
                }
            }

            // Creates a real Swift EveryProtocol instance (retained +1).
            // C# proxy code calls this instead of raw NativeMemory.Alloc to ensure the
            // existential container payload is a valid ARC-managed Swift object.
            @_cdecl("SBW_CreateEveryProtocol")
            public func _sbw_createEveryProtocol() -> UnsafeMutableRawPointer {
                let instance = EveryProtocol()
                return Unmanaged.passRetained(instance).toOpaque()
            }

            // Releases an EveryProtocol instance created by SBW_CreateEveryProtocol.
            @_cdecl("SBW_ReleaseEveryProtocol")
            public func _sbw_releaseEveryProtocol(_ ptr: UnsafeMutableRawPointer) {
                Unmanaged<EveryProtocol>.fromOpaque(ptr).release()
            }

            // Returns the Swift type metadata pointer for EveryProtocol.
            // Used by C# proxy classes to populate existential container metadata.
            @_cdecl("SBW_GetMetadata_EveryProtocol")
            public func _sbw_getEveryProtocolMetadata() -> UnsafeRawPointer {
                return unsafeBitCast(EveryProtocol.self, to: UnsafeRawPointer.self)
            }

            // Registers a C# deinit callback on an EveryProtocol instance. The callback
            // fires from Swift's deinit when the instance's retain count reaches 0.
            // Uses takeUnretainedValue — we're only reading a property reference, not
            // adding a ref. The caller (C# proxy ctor) already owns a +1 via
            // SBW_CreateEveryProtocol; takeRetainedValue would incorrectly consume it.
            @_cdecl("SBW_SetEveryProtocolDeinitCallback")
            public func _sbw_setEveryProtocolDeinitCallback(
                _ instance: UnsafeMutableRawPointer,
                _ callback: @convention(c) (UnsafeRawPointer) -> Void,
                _ context: UnsafeRawPointer
            ) {
                let ep = Unmanaged<EveryProtocol>.fromOpaque(instance).takeUnretainedValue()
                ep.onDeinit = callback
                ep.onDeinitCtx = context
            }

            // EveryObjCProtocol is the NSObject-rooted twin of EveryProtocol. Swift
            // forbids conforming a non-NSObject class to any @objc protocol that
            // inherits NSObjectProtocol. Conformances synthesised on this class satisfy the
            // NSObjectProtocol requirement via NSObject's built-in implementations
            // of isEqual:/hash/description, leaving the protocol's own requirements
            // to the same vtable-callback pattern used for EveryProtocol.
            @objc public final class EveryObjCProtocol: NSObject, @unchecked Sendable {
                public let handle: UnsafeRawPointer?
                fileprivate var onDeinit: (@convention(c) (UnsafeRawPointer) -> Void)?
                fileprivate var onDeinitCtx: UnsafeRawPointer?

                public override init() {
                    self.handle = nil
                    super.init()
                }

                public init(handle: UnsafeRawPointer) {
                    self.handle = handle
                    super.init()
                }

                deinit {
                    if let cb = onDeinit, let ctx = onDeinitCtx {
                        cb(ctx)
                    }
                }
            }

            @_cdecl("SBW_CreateEveryObjCProtocol")
            public func _sbw_createEveryObjCProtocol() -> UnsafeMutableRawPointer {
                let instance = EveryObjCProtocol()
                return Unmanaged.passRetained(instance).toOpaque()
            }

            @_cdecl("SBW_ReleaseEveryObjCProtocol")
            public func _sbw_releaseEveryObjCProtocol(_ ptr: UnsafeMutableRawPointer) {
                Unmanaged<EveryObjCProtocol>.fromOpaque(ptr).release()
            }

            @_cdecl("SBW_GetMetadata_EveryObjCProtocol")
            public func _sbw_getEveryObjCProtocolMetadata() -> UnsafeRawPointer {
                return unsafeBitCast(EveryObjCProtocol.self, to: UnsafeRawPointer.self)
            }

            @_cdecl("SBW_SetEveryObjCProtocolDeinitCallback")
            public func _sbw_setEveryObjCProtocolDeinitCallback(
                _ instance: UnsafeMutableRawPointer,
                _ callback: @convention(c) (UnsafeRawPointer) -> Void,
                _ context: UnsafeRawPointer
            ) {
                let ep = Unmanaged<EveryObjCProtocol>.fromOpaque(instance).takeUnretainedValue()
                ep.onDeinit = callback
                ep.onDeinitCtx = context
            }

            """);

        if (emitEntityBase)
        {
            // EveryEntityProtocol is the RealityFoundation.Entity-rooted twin of
            // EveryProtocol (Failure B). Swift forbids `extension EveryProtocol: P`
            // when `protocol P : Entity` constrains Self to be (a subclass of) Entity
            // and EveryProtocol does not inherit Entity. This subclass satisfies the
            // class-superclass requirement so the same vtable-callback pattern used
            // for EveryProtocol / EveryObjCProtocol covers protocols like
            // RealityKit.HasAnchoring and the RealityKit gesture .Entity getters.
            //
            // Inherits Entity's lifecycle: Swift retain/release via Unmanaged
            // (matching EveryProtocol's pattern — Entity is a pure Swift class with
            // Swift ARC, not an ObjC class). Entity's `required public init()`
            // requires the subclass to provide its own `required init()` that
            // initializes stored properties and forwards to super.init().
            writer.WriteLines($$"""
                // EveryEntityProtocol is the RealityFoundation.Entity-rooted twin of
                // EveryProtocol. Generated only when at least one protocol in this
                // module's binding is rooted at Entity (Failure B); a wrapper that
                // does not import RealityFoundation must not reference Entity.
                public final class EveryEntityProtocol: Entity, @unchecked Sendable {
                    public let handle: UnsafeRawPointer?
                    fileprivate var onDeinit: (@convention(c) (UnsafeRawPointer) -> Void)?
                    fileprivate var onDeinitCtx: UnsafeRawPointer?

                    public required init() {
                        self.handle = nil
                        super.init()
                    }

                    public init(handle: UnsafeRawPointer) {
                        self.handle = handle
                        super.init()
                    }

                    deinit {
                        if let cb = onDeinit, let ctx = onDeinitCtx {
                            cb(ctx)
                        }
                    }
                }

                @_cdecl("SBW_CreateEveryEntityProtocol")
                public func _sbw_createEveryEntityProtocol() -> UnsafeMutableRawPointer {
                    let instance = EveryEntityProtocol()
                    return Unmanaged.passRetained(instance).toOpaque()
                }

                @_cdecl("SBW_ReleaseEveryEntityProtocol")
                public func _sbw_releaseEveryEntityProtocol(_ ptr: UnsafeMutableRawPointer) {
                    Unmanaged<EveryEntityProtocol>.fromOpaque(ptr).release()
                }

                @_cdecl("SBW_GetMetadata_EveryEntityProtocol")
                public func _sbw_getEveryEntityProtocolMetadata() -> UnsafeRawPointer {
                    return unsafeBitCast(EveryEntityProtocol.self, to: UnsafeRawPointer.self)
                }

                @_cdecl("SBW_SetEveryEntityProtocolDeinitCallback")
                public func _sbw_setEveryEntityProtocolDeinitCallback(
                    _ instance: UnsafeMutableRawPointer,
                    _ callback: @convention(c) (UnsafeRawPointer) -> Void,
                    _ context: UnsafeRawPointer
                ) {
                    let ep = Unmanaged<EveryEntityProtocol>.fromOpaque(instance).takeUnretainedValue()
                    ep.onDeinit = callback
                    ep.onDeinitCtx = context
                }

                """);
        }
    }

    /// <summary>
    /// Emits stub conformances for Decodable, Encodable, and/or Error on EveryProtocol
    /// when any suitable protocol inherits from them. Without these stubs, Swift rejects
    /// `extension EveryProtocol: SomeProtocol` when SomeProtocol inherits Decodable/Encodable/Error.
    /// The stubs are no-ops since actual encoding/decoding happens on the C# side.
    /// </summary>
    public void EmitCodableStubsIfNeeded(SwiftWriter writer, IReadOnlyList<ProtocolDecl> suitableProtocols,
        IReadOnlyList<ProtocolDecl> allProtocols, ITypeDatabase typeDatabase)
    {
        bool needsDecodable = false;
        bool needsEncodable = false;
        bool needsError = false;

        foreach (var protocol in suitableProtocols)
        {
            foreach (var inherited in protocol.InheritedProtocols)
            {
                var simpleName = inherited.NameWithoutModule;
                if (simpleName is "Decodable" or "Codable")
                    needsDecodable = true;
                if (simpleName is "Encodable" or "Codable")
                    needsEncodable = true;
                if (simpleName == "Error")
                    needsError = true;

                // Also check transitively: if an inherited protocol is in allProtocols,
                // check its inherited protocols recursively
                CheckTransitiveCodableNeeds(simpleName, inherited.Name, allProtocols, typeDatabase,
                    ref needsDecodable, ref needsEncodable, ref needsError,
                    new HashSet<string>(StringComparer.Ordinal));
            }
        }

        if (needsDecodable)
        {
            writer.WriteLines("""
                // Stub Decodable conformance for EveryProtocol.
                // Actual decoding happens on the C# side via vtable dispatch.
                extension EveryProtocol: Decodable {
                    public convenience init(from decoder: Decoder) throws {
                        self.init()
                    }
                }

                """);
        }

        if (needsEncodable)
        {
            writer.WriteLines("""
                // Stub Encodable conformance for EveryProtocol.
                // Actual encoding happens on the C# side via vtable dispatch.
                extension EveryProtocol: Encodable {
                    public func encode(to encoder: Encoder) throws {
                        // no-op — encoding is handled by C# proxy
                    }
                }

                """);
        }

        if (needsError)
        {
            writer.WriteLines("""
                // Stub Error conformance for EveryProtocol.
                // Error handling is managed by the C# proxy via vtable dispatch.
                extension EveryProtocol: Swift.Error {}

                """);
        }
    }

    private void CheckTransitiveCodableNeeds(string simpleName, string fullName,
        IReadOnlyList<ProtocolDecl> allProtocols, ITypeDatabase typeDatabase,
        ref bool needsDecodable, ref bool needsEncodable, ref bool needsError,
        HashSet<string> visited)
    {
        if (!visited.Add(fullName))
            return;

        // Look up in same-module protocols
        var found = allProtocols.FirstOrDefault(p =>
            p.Name == simpleName || p.Name == fullName ||
            p.SwiftTypeName?.ToString() == fullName);

        if (found != null)
        {
            foreach (var inherited in found.InheritedProtocols)
            {
                var innerSimpleName = inherited.NameWithoutModule;
                if (innerSimpleName is "Decodable" or "Codable")
                    needsDecodable = true;
                if (innerSimpleName is "Encodable" or "Codable")
                    needsEncodable = true;
                if (innerSimpleName == "Error")
                    needsError = true;

                CheckTransitiveCodableNeeds(innerSimpleName, inherited.Name, allProtocols, typeDatabase,
                    ref needsDecodable, ref needsEncodable, ref needsError, visited);
            }
        }
    }

    /// <summary>
    /// Emits a no-op <c>NSCoding</c> stub conformance on the NSObject-rooted carrier
    /// (<c>EveryObjCProtocol</c>) when any suitable protocol routes through that carrier
    /// AND transitively inherits <c>NSCoding</c> (the <c>RoomPlan.RoomCaptureViewDelegate</c>
    /// shape: <c>protocol X : NSCoding</c>). Without the stub, the synthesised
    /// <c>extension EveryObjCProtocol: X</c> fails to type-check because X's inherited
    /// NSCoding requirement is unsatisfied. Both requirements are witnessed no-op:
    /// <c>encode(with:)</c> does nothing and <c>init?(coder:)</c> forwards to the carrier's
    /// designated init — the carrier is a synthetic reverse-dispatch shim that never
    /// participates in a real archiving round-trip, so there is no state to encode/decode.
    /// Mirrors <see cref="EmitCodableStubsIfNeeded"/> but on the ObjC carrier and gated on
    /// the ObjC route, so a module with no NSCoding-rooted protocols carries no stub and no
    /// real NSCoder path is affected.
    /// </summary>
    public void EmitObjCCodingStubIfNeeded(SwiftWriter writer, IReadOnlyList<ProtocolDecl> suitableProtocols,
        IReadOnlyList<ProtocolDecl> allProtocols)
    {
        bool needsNSCoding = suitableProtocols.Any(p =>
            IsNSObjectProtocolOnly(p, allProtocols)
            && TransitivelyInheritsNSCoding(p, allProtocols, new HashSet<string>(StringComparer.Ordinal)));

        if (!needsNSCoding)
            return;

        writer.WriteLines("""
            // Stub NSCoding conformance for the NSObject-rooted carrier. A protocol that
            // inherits NSCoding (e.g. RoomPlan.RoomCaptureViewDelegate) routes its reverse
            // conformance through EveryObjCProtocol; NSCoding's two requirements are witnessed
            // no-op here because the synthetic carrier never archives — encoding/decoding of
            // real state happens on the C# side via vtable dispatch. Lives ONLY on the carrier,
            // so no real NSCoder path is affected.
            extension EveryObjCProtocol: NSCoding {
                public func encode(with coder: NSCoder) {
                    // no-op — the synthetic carrier holds no archivable state
                }
                public convenience init?(coder: NSCoder) {
                    self.init()
                }
            }

            """);
    }

    /// <summary>
    /// True when <paramref name="protocolDecl"/> transitively inherits Foundation's
    /// <c>NSCoding</c> (directly or through an intra-module parent protocol). Used to
    /// gate the no-op NSCoding stub on the ObjC carrier.
    /// </summary>
    private static bool TransitivelyInheritsNSCoding(ProtocolDecl protocolDecl,
        IReadOnlyList<ProtocolDecl> allProtocols, HashSet<string> visited)
    {
        var qualifiedName = protocolDecl.SwiftTypeName?.ToString() ?? protocolDecl.Name;
        if (!visited.Add(qualifiedName))
            return false;

        // NSCoding conformance is carried as a generic-signature requirement
        // (`<Self : Foundation.NSCoding>`), not an InheritedProtocols entry — see
        // CollectObjCRootKinds. NSSecureCoding refines NSCoding, so it counts too.
        if (protocolDecl.ParsedGenericSignature.Requirements.Any(
                r => r.TargetSimpleName is "NSCoding" or "NSSecureCoding"))
            return true;

        foreach (var inherited in protocolDecl.InheritedProtocols)
        {
            var simpleName = inherited.NameWithoutModule;
            if (simpleName is "NSCoding" or "NSSecureCoding")
                return true;

            var inheritedDecl = allProtocols.FirstOrDefault(p =>
                p.Name == simpleName || p.Name == inherited.Name ||
                p.SwiftTypeName?.ToString() == inherited.Name);
            if (inheritedDecl != null && TransitivelyInheritsNSCoding(inheritedDecl, allProtocols, visited))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Emits the vtable struct for a protocol.
    /// The vtable contains function pointers for each protocol requirement.
    /// </summary>
    public void EmitProtocolVtableStruct(SwiftWriter writer, ProtocolDecl protocolDecl)
    {
        var vtableName = GetVtableStructName(protocolDecl);
        var closureHandler = new ClosureHandler(_typeDatabase);

        writer.WriteLine($"// Vtable for {protocolDecl.Name} protocol - stores function pointers to C# implementations");
        writer.WriteLine($"fileprivate struct {vtableName} {{");
        writer.Indent++;

        // First field: handle to C# vtable (used to pass context back to C#)
        writer.WriteLine("var csVTHandle: OpaquePointer? = nil");

        // Track emitted fields to avoid duplicates
        var emittedFields = new HashSet<string>();

        // Render the single VtableLayout model: one ordered slot list whose membership, index, and
        // width come from VtableLayoutBuilder (the canonical reverse-dispatch oracle). The C# struct
        // mirrors (EmitSwiftVtableStruct / EmitLocalVtableStruct) and the cross-module-parent walks
        // render the SAME list, so the Swift `_vtable` struct cannot drift out of positional agreement
        // with them (the Bug #21 / Defect-F class). Excluded members emit no field but still consume
        // their slot index inside the model (skip-but-consume), so a fatalError-stub member leaves the
        // exact positional hole the C# side expects. A dispatchable-closure property takes the
        // specialised (fnPtr, ctx) field shape; everything else routes through its kind's emitter.
        var layout = new VtableLayoutBuilder(_typeDatabase).Build(protocolDecl);
        foreach (var slot in layout.IncludedSlots)
        {
            switch (slot.Kind)
            {
                case VtableMemberKind.Property:
                    if (slot.IsDispatchableClosure)
                        EmitDispatchableClosurePropertyVtableFields(writer, slot.AsProperty!, closureHandler, emittedFields);
                    else
                        EmitPropertyVtableFields(writer, slot.AsProperty!, protocolDecl, emittedFields);
                    break;
                case VtableMemberKind.Subscript:
                    EmitSubscriptVtableFields(writer, slot.AsSubscript!, protocolDecl, slot.SlotIndex, emittedFields);
                    break;
                case VtableMemberKind.Method:
                    EmitMethodVtableField(writer, slot.AsMethod!, protocolDecl, slot.SlotIndex, slot.Width, emittedFields, closureHandler);
                    break;
            }
        }

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();

        // Emit the global vtable instance
        var instanceName = GetVtableInstanceName(protocolDecl);
        writer.WriteLine($"private var {instanceName} = {vtableName}()");
        writer.WriteLine();
    }

    /// <summary>
    /// Emits the protocol extension that makes EveryProtocol conform to the protocol.
    /// Each method/property implementation calls back to C# via the vtable.
    /// </summary>
    public void EmitProtocolExtension(SwiftWriter writer, ProtocolDecl protocolDecl)
    {
        EmitProtocolExtension(writer, protocolDecl, null);
    }

    /// <summary>
    /// Per-property plan describing how the EveryProtocol extension for a sibling-protocol
    /// group should emit the property body. A "sibling group" is two or more class-bound
    /// protocols that declare the same property name and type with differing accessor sets
    /// (e.g. Nameable's get-only <c>var name: String</c> and MutableNamed's get+set
    /// <c>var name: String</c>); see <see cref="ComputePropertyEmissionPlans"/> for the
    /// resolution rules.
    ///
    /// <para><see cref="Owner"/> emits the property body on its extension; other siblings
    /// emit empty extensions. Swift's cross-extension witness resolution routes
    /// inherited-through-sibling dispatch back into the owner's body. The owner's body fans
    /// out across <see cref="GetterSiblings"/> / <see cref="SetterSiblings"/>, checking
    /// each sibling's vtable for a non-nil function pointer and dispatching through
    /// whichever the registered C# proxy populated. Without this fan-out a C# class that
    /// implemented only a smaller sibling would leave the owner's vtable nil and the
    /// force-unwrapped pointer would SIGSEGV.</para>
    /// </summary>
    /// <param name="Owner">Protocol whose extension emits the property body.</param>
    /// <param name="GetterSiblings">All sibling protocols (including <paramref name="Owner"/>),
    /// ordered owner-first then lexicographically. Every sibling is included because the
    /// shared property always has a getter — that is the minimum-shape accessor set.</param>
    /// <param name="SetterSiblings">Subset of <paramref name="GetterSiblings"/> that declare
    /// a setter, ordered owner-first then lexicographically. Empty when the group has no
    /// setter. A single-entry list (just the owner) means no fan-out is needed for the
    /// setter — the owner's vtable is the only one that can be populated.</param>
    /// <param name="HasFilteredPeers">True when the sibling group contained one or more
    /// protocols that ModuleHandler.IsEmittable filtered out (e.g. mixed-generic protocols
    /// whose properties/subscripts are stubbed with fatalError). Those filtered peers do not
    /// appear in <paramref name="GetterSiblings"/> or <paramref name="SetterSiblings"/>, but
    /// Swift's cross-extension witness resolution can still route a filtered-protocol
    /// existential's dispatch through the owner body. When this flag is true the owner must
    /// emit the nil-check + fatalError fan-out shape even for single-participant plans so a
    /// filtered-only C# implementation surfaces a diagnosable error instead of a force-unwrap
    /// SIGSEGV on the nil owner vtable.</param>
    public sealed record PropertyEmissionPlan(
        ProtocolDecl Owner,
        IReadOnlyList<ProtocolDecl> GetterSiblings,
        IReadOnlyList<ProtocolDecl> SetterSiblings,
        bool HasFilteredPeers = false);

    /// <summary>
    /// Builds the per-property emission plan used by <see cref="EmitProtocolExtension"/>
    /// to resolve accessor-set conflicts across protocols that share a property name and
    /// type. Two get-only declarations of the same <c>var name: String</c> are fine — the
    /// first emits the body, the second emits an empty extension and Swift's
    /// cross-extension witness resolution satisfies the conformance via the first body.
    /// But a get-only and a get+set declaration cannot both be satisfied by the same
    /// body: if the get+set extension emits empty, Swift rejects it ("does not conform —
    /// missing set witness").
    ///
    /// <para>Ownership resolution: the protocol with the fattest accessor set (has-setter
    /// wins over get-only) emits the body; siblings emit empty extensions and rely on the
    /// owner's declaration. Tie-break is lexicographic on protocol name for determinism.
    /// All siblings in the group are recorded on the returned <see cref="PropertyEmissionPlan"/>
    /// so the owner body can fan out across each sibling's vtable.</para>
    ///
    /// <para>Key format: <c>$"{carrier}{property.Name}|{property.SwiftTypeSpec}"</c>, where
    /// <c>carrier</c> is <see cref="GetCarrierClassName"/>. Properties sharing the same name but
    /// different types are already dropped upstream by the type-count gate in
    /// <c>ModuleHandler.EmitEveryProtocolConformances</c>, so the signature part only collides for
    /// true same-name+same-type+different-accessor-set groups. The carrier prefix further keeps
    /// protocols that route to DIFFERENT umbrella classes (plain <c>EveryProtocol</c> vs.
    /// <c>EveryObjCProtocol</c> vs. <c>EveryEntityProtocol</c>) in SEPARATE groups — they emit
    /// witnesses on distinct concrete Swift classes, and Swift cross-extension witness resolution
    /// cannot bridge a witness from one to a conformance on the other.</para>
    ///
    /// <para>Returns a map keyed by that carrier-prefixed key; the lookup in
    /// <see cref="EmitProtocolExtension"/> reconstructs it via <see cref="GetCarrierClassName"/> on
    /// the protocol being emitted. Properties with only one declaring protocol are still recorded
    /// (owner = that protocol, sibling lists contain only the owner) so callers can uniformly query
    /// the plan. Instance (not static) because <see cref="GetCarrierClassName"/> needs the
    /// pre-scanned <see cref="_allProtocols"/> / <see cref="_typeDatabase"/> routing state.</para>
    /// </summary>
    public IReadOnlyDictionary<string, PropertyEmissionPlan> ComputePropertyEmissionPlans(
        IEnumerable<ProtocolDecl> protocols,
        IEnumerable<ProtocolDecl>? filteredPeers = null)
    {
        var groups = new Dictionary<string, List<(ProtocolDecl Proto, PropertyDecl Prop, bool HasSetter)>>(StringComparer.Ordinal);
        foreach (var p in protocols)
        {
            var carrier = GetCarrierClassName(p);
            foreach (var prop in p.Properties)
            {
                if (prop.IsStatic || prop.IsObjCOptional || !prop.IsProtocolRequirement)
                    continue;
                var key = $"{carrier}{CarrierKeySeparator}{prop.Name}|{prop.SwiftTypeSpec}";
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<(ProtocolDecl, PropertyDecl, bool)>();
                    groups[key] = list;
                }
                list.Add((p, prop, prop.Accessors.OfType<SetAccessorDecl>().Any()));
            }
        }

        // Collect group keys that filtered peers also declare. A filtered protocol with the
        // same (carrier, propertyName, propertyType) as an emitted owner means Swift CEWR can
        // still route a filtered-existential dispatch through the owner's body — see the
        // HasFilteredPeers parameter on PropertyEmissionPlan for the rationale. The carrier
        // prefix keeps the match carrier-local, mirroring the owner-group partition above.
        var filteredKeys = new HashSet<string>(StringComparer.Ordinal);
        if (filteredPeers is not null)
        {
            foreach (var p in filteredPeers)
            {
                var carrier = GetCarrierClassName(p);
                foreach (var prop in p.Properties)
                {
                    if (prop.IsStatic || prop.IsObjCOptional || !prop.IsProtocolRequirement)
                        continue;
                    filteredKeys.Add($"{carrier}{CarrierKeySeparator}{prop.Name}|{prop.SwiftTypeSpec}");
                }
            }
        }

        var plans = new Dictionary<string, PropertyEmissionPlan>(StringComparer.Ordinal);
        foreach (var (key, entries) in groups)
        {
            // Tie-break and ordering use GetProtocolFallbackKey (module-qualified plus
            // nested-type chain) rather than unqualified Proto.Name. Cross-module groups
            // and nested protocols can collide on leaf name; sorting on the unqualified
            // name leaves the winner determined by encounter order (suitableProtocols
            // ⋃ crossModuleParents), which is not stable across builds. The qualified
            // key matches the form used elsewhere as the fallback-map key, so ownership,
            // branch order, and fallback order all sort consistently.
            var owner = entries
                .OrderByDescending(e => e.HasSetter)
                .ThenBy(e => GetProtocolFallbackKey(e.Proto), StringComparer.Ordinal)
                .First()
                .Proto;
            // Owner-first, then siblings in qualified-key order. Owner-first makes the
            // common case (proxy implements the largest interface) hit the first branch
            // of the fan-out; the qualified-key order is just for determinism.
            var ordered = entries
                .Select(e => e.Proto)
                .Where(p => p != owner)
                .OrderBy(p => GetProtocolFallbackKey(p), StringComparer.Ordinal)
                .Prepend(owner)
                .ToList();
            // Owner is selected by OrderByDescending(HasSetter), so if ANY entry has a
            // setter the owner does too — meaning the owner leads the setter list and
            // additional has-setter siblings follow it. An empty list means no entry has
            // a setter (pure get-only group; no setter to dispatch).
            IReadOnlyList<ProtocolDecl> settersOrdered = entries.Any(e => e.HasSetter)
                ? entries
                    .Where(e => e.HasSetter && e.Proto != owner)
                    .Select(e => e.Proto)
                    .OrderBy(p => GetProtocolFallbackKey(p), StringComparer.Ordinal)
                    .Prepend(owner)
                    .ToList()
                : Array.Empty<ProtocolDecl>();
            plans[key] = new PropertyEmissionPlan(owner, ordered, settersOrdered,
                HasFilteredPeers: filteredKeys.Contains(key));
        }
        return plans;
    }

    /// <summary>
    /// Builds the per-(protocol, property) sibling-fallback map consumed by
    /// <c>ProtocolProxyEmitter.EmitPropertyReceivers</c>. Each map entry lists the OTHER
    /// protocols in the same sibling group, ordered lexicographically. Properties not in
    /// a sibling group (only one declaring protocol) are omitted.
    ///
    /// <para>Required because the Swift fan-out picks ANY populated sibling vtable, which
    /// may not be the vtable matching the proxy actually registered for this EveryProtocol
    /// instance. The receiver in the chosen vtable's proxy class is hard-coded to look up
    /// <c>IProtocolProxyImpl&lt;TItsOwnInterface&gt;</c>; without the fallback list it would
    /// return empty/no-op for any handle registered as a different sibling's proxy. With the
    /// fallback list, each receiver tries its own interface first, then walks siblings —
    /// so any populated branch correctly locates the proxy regardless of registration order.</para>
    /// </summary>
    public IReadOnlyDictionary<(string ProtoQName, string PropertyName), IReadOnlyList<ModuleEmissionContext.SiblingPropertyFallback>>
        ComputeSiblingPropertyFallbacks(IEnumerable<ProtocolDecl> protocols)
    {
        var groups = new Dictionary<string, List<(ProtocolDecl Proto, PropertyDecl Prop, bool HasSetter)>>(StringComparer.Ordinal);
        foreach (var p in protocols)
        {
            // Carrier-prefix the group key so two protocols routing to DIFFERENT umbrella
            // carriers never become mutual fallback siblings — the owner-body fan-out
            // (ComputePropertyEmissionPlans) is itself carrier-partitioned, so a receiver in
            // one carrier's vtable is only ever reached for same-carrier protocols.
            var carrier = GetCarrierClassName(p);
            foreach (var prop in p.Properties)
            {
                if (prop.IsStatic || prop.IsObjCOptional || !prop.IsProtocolRequirement)
                    continue;
                var key = $"{carrier}{CarrierKeySeparator}{prop.Name}|{prop.SwiftTypeSpec}";
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<(ProtocolDecl, PropertyDecl, bool)>();
                    groups[key] = list;
                }
                list.Add((p, prop, prop.Accessors.OfType<SetAccessorDecl>().Any()));
            }
        }

        var fallback = new Dictionary<(string, string), IReadOnlyList<ModuleEmissionContext.SiblingPropertyFallback>>();
        foreach (var entries in groups.Values)
        {
            if (entries.Count < 2)
                continue;
            foreach (var (proto, prop, _) in entries)
            {
                var siblings = entries
                    .Where(e => e.Proto != proto)
                    .Select(e => new ModuleEmissionContext.SiblingPropertyFallback(e.Proto, e.HasSetter))
                    .OrderBy(s => GetProtocolFallbackKey(s.Proto), StringComparer.Ordinal)
                    .ToList();
                fallback[(GetProtocolFallbackKey(proto), prop.Name)] = siblings;
            }
        }
        return fallback;
    }

    /// <summary>
    /// Per-subscript plan describing how the EveryProtocol extension for a sibling-subscript
    /// group should emit the subscript body. Mirrors <see cref="PropertyEmissionPlan"/>: a
    /// "sibling group" is two or more class-bound protocols declaring the same subscript
    /// signature (index parameter types + return type) with differing accessor sets. The
    /// owner emits the body; siblings emit empty extensions and rely on Swift's
    /// cross-extension witness resolution.
    /// </summary>
    /// <param name="Owner">Protocol whose extension emits the subscript body.</param>
    /// <param name="OwnerIndex">Subscript index within <paramref name="Owner"/>'s own
    /// subscript list — the index that names the owner's vtable fields.</param>
    /// <param name="GetterSiblings">All sibling subscripts (including the owner's), each
    /// recording the sibling protocol and the subscript's index within THAT protocol's
    /// subscript list. Owner-first then lex order on protocol name. Every entry is included
    /// because the shared subscript always has a getter.</param>
    /// <param name="SetterSiblings">Subset of <paramref name="GetterSiblings"/> whose
    /// subscript declares a setter. Empty for get-only groups.</param>
    /// <param name="HasFilteredPeers">Mirrors <see cref="PropertyEmissionPlan.HasFilteredPeers"/>:
    /// true when the sibling group contained one or more protocols that
    /// ModuleHandler.IsEmittable filtered out. Forces the owner body into the nil-check
    /// fan-out shape even for single-participant plans so that filtered-only implementations
    /// fatalError() instead of SIGSEGVing on a nil owner vtable.</param>
    public sealed record SubscriptEmissionPlan(
        ProtocolDecl Owner,
        int OwnerIndex,
        IReadOnlyList<(ProtocolDecl Proto, int Index)> GetterSiblings,
        IReadOnlyList<(ProtocolDecl Proto, int Index)> SetterSiblings,
        bool HasFilteredPeers = false);

    /// <summary>
    /// Walks <paramref name="protocol"/>'s subscript list and yields (subscript, index) pairs
    /// using the same indexing rule as <see cref="EmitProtocolExtension"/> and
    /// <c>EmitProtocolVtableStruct</c>: skip static subscripts entirely; for all other
    /// subscripts (including Self-typed and mixed-generic which only stub out the body),
    /// increment the counter. This keeps the index aligned with the vtable field names
    /// <c>func_subscript_{index}_get/set</c>.
    /// </summary>
    private static IEnumerable<(SubscriptDecl Sub, int Index)> EnumerateIndexedSubscripts(ProtocolDecl protocol)
    {
        int idx = 0;
        foreach (var sub in protocol.Subscripts)
        {
            if (sub.IsStatic)
                continue;
            yield return (sub, idx);
            idx++;
        }
    }

    /// <summary>
    /// Returns the signature key for a subscript used to group siblings: the index parameter
    /// argument labels and type tuple plus the return type. Argument labels are part of the
    /// Swift witness signature: <c>subscript(at index: Int) -&gt; X</c> and
    /// <c>subscript(by index: Int) -&gt; X</c> are distinct witnesses and must NOT share a
    /// sibling group, even though their type tuples match.
    /// </summary>
    private static string GetSubscriptSiblingKey(SubscriptDecl subscript)
    {
        var paramSig = string.Join(",", subscript.IndexParameters.Select(p =>
            $"{NameProvider.GetSubscriptExternalLabel(p)}:{p.SwiftTypeSpec?.ToString() ?? ""}"));
        var returnType = subscript.ReturnTypeSpec?.ToString() ?? "";
        return $"subscript|({paramSig})|{returnType}";
    }

    /// <summary>
    /// Builds the per-subscript emission plan used by <see cref="EmitProtocolExtension"/>
    /// to resolve accessor-set conflicts across protocols sharing a subscript signature.
    /// Mirrors <see cref="ComputePropertyEmissionPlans"/>: the protocol with the fattest
    /// accessor set (has-setter wins) owns the body; ties broken lexicographically on
    /// protocol name.
    ///
    /// <para>Returns a map keyed by <c>(GetProtocolFallbackKey(proto), subscriptKey)</c>
    /// where subscriptKey is the per-protocol <c>subscript_{index}({paramTypes})</c>. Both
    /// owner and non-owner siblings are recorded so the dispatch loop in
    /// <c>EmitProtocolExtension</c> can look up the plan from either side.</para>
    /// </summary>
    public IReadOnlyDictionary<(string ProtoQName, string SubscriptKey), SubscriptEmissionPlan>
        ComputeSubscriptEmissionPlans(IEnumerable<ProtocolDecl> protocols,
            IEnumerable<ProtocolDecl>? filteredPeers = null)
    {
        var groups = new Dictionary<string, List<(ProtocolDecl Proto, SubscriptDecl Sub, int Index, bool HasSetter)>>(StringComparer.Ordinal);
        foreach (var p in protocols)
        {
            var carrier = GetCarrierClassName(p);
            foreach (var (sub, idx) in EnumerateIndexedSubscripts(p))
            {
                var key = $"{carrier}{CarrierKeySeparator}{GetSubscriptSiblingKey(sub)}";
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<(ProtocolDecl, SubscriptDecl, int, bool)>();
                    groups[key] = list;
                }
                list.Add((p, sub, idx, sub.HasSetter));
            }
        }

        // Mirror of ComputePropertyEmissionPlans: collect carrier-prefixed signature keys that
        // filtered peers also declare so HasFilteredPeers can flip on the multi-branch nil-check
        // fan-out. The carrier prefix keeps the match carrier-local (see GetCarrierClassName).
        var filteredKeys = new HashSet<string>(StringComparer.Ordinal);
        if (filteredPeers is not null)
        {
            foreach (var p in filteredPeers)
            {
                var carrier = GetCarrierClassName(p);
                foreach (var (sub, _) in EnumerateIndexedSubscripts(p))
                {
                    filteredKeys.Add($"{carrier}{CarrierKeySeparator}{GetSubscriptSiblingKey(sub)}");
                }
            }
        }

        var plans = new Dictionary<(string, string), SubscriptEmissionPlan>();
        foreach (var (groupKey, entries) in groups.Select(kv => (kv.Key, kv.Value)))
        {
            // Tie-break and ordering use GetProtocolFallbackKey (module-qualified plus
            // nested-type chain) — see ComputePropertyEmissionPlans for rationale.
            var ownerEntry = entries
                .OrderByDescending(e => e.HasSetter)
                .ThenBy(e => GetProtocolFallbackKey(e.Proto), StringComparer.Ordinal)
                .First();
            var owner = ownerEntry.Proto;
            var ownerIndex = ownerEntry.Index;
            var getterSiblings = entries
                .Where(e => e.Proto != owner)
                .OrderBy(e => GetProtocolFallbackKey(e.Proto), StringComparer.Ordinal)
                .Select(e => (e.Proto, e.Index))
                .Prepend((owner, ownerIndex))
                .ToList();
            IReadOnlyList<(ProtocolDecl, int)> setterSiblings = entries.Any(e => e.HasSetter)
                ? entries
                    .Where(e => e.HasSetter && e.Proto != owner)
                    .OrderBy(e => GetProtocolFallbackKey(e.Proto), StringComparer.Ordinal)
                    .Select(e => (e.Proto, e.Index))
                    .Prepend((owner, ownerIndex))
                    .ToList()
                : Array.Empty<(ProtocolDecl, int)>();
            var plan = new SubscriptEmissionPlan(owner, ownerIndex, getterSiblings, setterSiblings,
                HasFilteredPeers: filteredKeys.Contains(groupKey));
            foreach (var entry in entries)
            {
                var subscriptKey = GetSubscriptKey(entry.Sub, entry.Index);
                plans[(GetProtocolFallbackKey(entry.Proto), subscriptKey)] = plan;
            }
        }
        return plans;
    }

    /// <summary>
    /// Builds the per-(protocol, subscript) sibling-fallback map consumed by
    /// <c>ProtocolProxyEmitter.EmitSubscriptReceivers</c>. Mirrors
    /// <see cref="ComputeSiblingPropertyFallbacks"/>: each map entry lists the OTHER
    /// protocols in the same sibling group with their per-protocol subscript indices,
    /// ordered lexicographically. Subscripts not in a sibling group are omitted.
    /// </summary>
    public IReadOnlyDictionary<(string ProtoQName, string SubscriptKey), IReadOnlyList<ModuleEmissionContext.SiblingSubscriptFallback>>
        ComputeSiblingSubscriptFallbacks(IEnumerable<ProtocolDecl> protocols)
    {
        var groups = new Dictionary<string, List<(ProtocolDecl Proto, SubscriptDecl Sub, int Index, bool HasSetter)>>(StringComparer.Ordinal);
        foreach (var p in protocols)
        {
            // Carrier-prefix so cross-carrier protocols never become mutual fallback siblings
            // (see ComputeSiblingPropertyFallbacks for the rationale).
            var carrier = GetCarrierClassName(p);
            foreach (var (sub, idx) in EnumerateIndexedSubscripts(p))
            {
                var key = $"{carrier}{CarrierKeySeparator}{GetSubscriptSiblingKey(sub)}";
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<(ProtocolDecl, SubscriptDecl, int, bool)>();
                    groups[key] = list;
                }
                list.Add((p, sub, idx, sub.HasSetter));
            }
        }

        var fallback = new Dictionary<(string, string), IReadOnlyList<ModuleEmissionContext.SiblingSubscriptFallback>>();
        foreach (var entries in groups.Values)
        {
            if (entries.Count < 2)
                continue;
            foreach (var (proto, sub, index, _) in entries)
            {
                var siblings = entries
                    .Where(e => e.Proto != proto)
                    .Select(e => new ModuleEmissionContext.SiblingSubscriptFallback(e.Proto, e.Index, e.HasSetter))
                    .OrderBy(s => GetProtocolFallbackKey(s.Proto), StringComparer.Ordinal)
                    .ToList();
                fallback[(GetProtocolFallbackKey(proto), GetSubscriptKey(sub, index))] = siblings;
            }
        }
        return fallback;
    }

    /// <summary>
    /// Per-method emission plan, the method counterpart of <see cref="SubscriptEmissionPlan"/>.
    /// When two or more protocols declare the same Swift method signature (name + argument
    /// labels + parameter types + return type), exactly one — chosen by lexicographic
    /// <see cref="GetProtocolFallbackKey"/> order — owns the body. Siblings emit empty
    /// extensions and conform via Swift's cross-extension witness resolution. The owner's body
    /// fans out across every sibling's per-protocol vtable so dispatch through a smaller-sibling
    /// existential (e.g. a C# impl that conforms to only a non-owner protocol) still finds the
    /// populated vtable instead of reading the owner's nil global vtable and crashing.
    /// </summary>
    /// <param name="Owner">The protocol that emits the real method body.</param>
    /// <param name="OwnerIndex">The owner's per-protocol vtable index for this method
    /// (<c>func_{name}_{OwnerIndex}</c>).</param>
    /// <param name="Siblings">Every participant in the group with its own per-protocol method
    /// index, ordered sync-first then by <see cref="GetProtocolFallbackKey"/> — NOT owner-first
    /// (the owner is identified by <see cref="Owner"/>, not <c>Siblings[0]</c>). The sync-first
    /// sort makes a mixed async/sync group dispatch through the ABI-compatible sync witness; see
    /// the fan-out-order comment in <see cref="ComputeMethodEmissionPlans"/>. The owner body emits
    /// one fan-out branch per entry.</param>
    /// <param name="HasFilteredPeers">Mirrors <see cref="SubscriptEmissionPlan.HasFilteredPeers"/>:
    /// true when the group contained one or more protocols that ModuleHandler.IsEmittable filtered
    /// out. Forces the owner body into the nil-check fan-out shape even for single-participant
    /// plans so a filtered-only implementation fatalError()s instead of SIGSEGVing on a nil
    /// owner vtable.</param>
    public sealed record MethodEmissionPlan(
        ProtocolDecl Owner,
        int OwnerIndex,
        IReadOnlyList<(ProtocolDecl Proto, int Index)> Siblings,
        bool HasFilteredPeers = false);

    /// <summary>
    /// Builds the per-method emission plan consumed by <see cref="EmitProtocolExtension"/> to
    /// resolve same-signature collisions across protocols. Method counterpart of
    /// <see cref="ComputeSubscriptEmissionPlans"/>; methods have no accessor-set fatness, so the
    /// owner is purely the lexicographically smallest <see cref="GetProtocolFallbackKey"/>.
    ///
    /// <para>Grouping uses <see cref="GetSwiftMethodFullSignature"/> — the projected Swift
    /// signature that actually determines whether two extensions collide on the same witness —
    /// so the partition matches the legacy <c>globalEmittedSignatures</c> dedup exactly and
    /// solo methods keep byte-identical single-branch output.</para>
    ///
    /// <para>Returns a map keyed by <c>(GetProtocolFallbackKey(proto), carrier-prefixed signature)</c>;
    /// the second element is <c>{carrier}{CarrierKeySeparator}{fullSignature}</c> so two protocols
    /// sharing one signature but routed to different carriers stay in separate plans. Both owner and
    /// non-owner entries are recorded so the dispatch loop can look the plan up from either side.
    /// This is an instance method (not static like the property/subscript versions)
    /// because <see cref="GetSwiftMethodFullSignature"/> needs the type projection state.</para>
    /// </summary>
    public IReadOnlyDictionary<(string ProtoQName, string CarrierAndSignature), MethodEmissionPlan>
        ComputeMethodEmissionPlans(IEnumerable<ProtocolDecl> protocols,
            IEnumerable<ProtocolDecl>? filteredPeers = null)
    {
        var groups = new Dictionary<string, List<(ProtocolDecl Proto, MethodDecl Method, int Index)>>(StringComparer.Ordinal);
        foreach (var p in protocols)
        {
            // Carrier-prefix the group key so two protocols routing to DIFFERENT umbrella carriers
            // (plain EveryProtocol vs. EveryObjCProtocol vs. EveryEntityProtocol) never share one
            // owner. They emit witnesses on distinct concrete Swift classes, and Swift
            // cross-extension witness resolution cannot satisfy a conformance on one carrier from a
            // body emitted on another — so a single cross-carrier owner would leave the other
            // carrier's extension empty and unsatisfiable ("does not conform"), failing wrapper
            // compilation. The triggering shape is a plain Swift protocol and an
            // @objc/NSObjectProtocol protocol that declare one identical method signature.
            var carrier = GetCarrierClassName(p);
            foreach (var (method, idx) in EnumerateProtocolMethodsForDispatch(p))
            {
                // Owner/peer dedup keys off the EMITTED Swift witness shape. A real-async witness
                // (S13 Pillar C) emits `func m(...) async throws -> T` — a DISTINCT Swift declaration
                // from the sync witness `func m(...) -> T` (effect overloading), so it must carry the
                // `async` effect in the key and form its OWN owner group, emitting its own async body.
                // A sync method, and an async method whose shape falls back to the blocking sync witness,
                // both keep the async-OMITTED key so they still share one owner + empty-extension peer.
                // The C# fan-out distinction lives in ComputeSiblingMethodFallbacks (includeAsyncEffect:true).
                var key = $"{carrier}{CarrierKeySeparator}{GetSwiftMethodFullSignature(method, includeAsyncEffect: EmitsRealAsyncWitness(method))}";
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<(ProtocolDecl, MethodDecl, int)>();
                    groups[key] = list;
                }
                list.Add((p, method, idx));
            }
        }

        // Mirror of ComputeSubscriptEmissionPlans: collect carrier-prefixed signatures that filtered
        // peers also declare so HasFilteredPeers can flip on the nil-check fan-out for an
        // otherwise-solo owner. The carrier prefix keeps the match carrier-local.
        var filteredKeys = new HashSet<string>(StringComparer.Ordinal);
        if (filteredPeers is not null)
        {
            foreach (var p in filteredPeers)
            {
                var carrier = GetCarrierClassName(p);
                foreach (var (method, _) in EnumerateProtocolMethodsForDispatch(p))
                    filteredKeys.Add($"{carrier}{CarrierKeySeparator}{GetSwiftMethodFullSignature(method, includeAsyncEffect: EmitsRealAsyncWitness(method))}");
            }
        }

        // A sibling only contributes a fan-out BRANCH if its protocol actually emits a per-protocol
        // vtable FUNC field for this method — a branch reads `branchVtable.func_{name}_{idx}`, which
        // exists only when the field was emitted. The vtable layout is the single source of truth for
        // slot existence, so ask it directly (ProtocolVtableMembers.IncludesMethod == the layout's
        // ClassifyMethod == Included) rather than re-deriving membership here; a branch can then never
        // reference a member the layout omitted (e.g. a nested @objc-protocol existential requirement,
        // which the layout drops fail-closed but a divergent local predicate would keep → Swift
        // wrapper compile failure).
        var closureHandler = new ClosureHandler(_typeDatabase);
        bool EntryEmitsVtableField(ProtocolDecl proto, MethodDecl method)
            => ProtocolVtableMembers.IncludesMethod(method, proto, closureHandler);

        var plans = new Dictionary<(string, string), MethodEmissionPlan>();
        foreach (var (groupKey, entries) in groups.Select(kv => (kv.Key, kv.Value)))
        {
            // Owner selection prefers the maximally-SATISFYING witness shape — non-async AND
            // non-throwing — before falling back to GetProtocolFallbackKey. The owner is the one
            // sibling whose extension emits the real witness BODY; every other sibling gets an empty
            // stitched extension that the owner's body must satisfy. A sync non-throwing witness
            // satisfies an async and/or throwing requirement (Swift's effect subtyping), but the
            // reverse fails: an async (or throwing) owner emits an async/throwing body — and for a
            // CLOSURE-param method that body is the non-dispatchable fatalError stub (all real
            // closure dispatch gates reject method.IsAsync) — leaving the sync siblings' empty
            // extensions unsatisfied ("does not conform") and force-routing the sync siblings through
            // a stub. The grouping key carries `async` ONLY for a real-async witness
            // (includeAsyncEffect: EmitsRealAsyncWitness), so a real-async method forms its OWN
            // async-keyed group and never lands here mixed with sync siblings; the residual mixed
            // groups are sync members plus NON-real-async async members (e.g. closure-param async,
            // whose key omits `async`), and such a group must still hand the body to a sync,
            // non-throwing sibling when one exists. All-sync / all-async(-non-real) / all-throwing
            // groups keep GetProtocolFallbackKey order (stable sort), so output is byte-identical for
            // every non-mixed group.
            var ownerEntry = entries
                .OrderBy(e => e.Method.IsAsync ? 1 : 0)
                .ThenBy(e => e.Method.Throws ? 1 : 0)
                .ThenBy(e => GetProtocolFallbackKey(e.Proto), StringComparer.Ordinal)
                .First();
            var owner = ownerEntry.Proto;
            var ownerIndex = ownerEntry.Index;
            // Fan-out branch order: try ABI-matching siblings first. The emitted pure-Swift
            // EveryProtocol witness always drops `async` (EmitMethodImplementation's asyncDecl is
            // gated on _useObjCBase) — a sync witness satisfies an async requirement — so the
            // witness's @convention(c) vtable call is sync-ABI. A NON-CLOSURE async sibling's
            // per-protocol global vtable slot, however, holds an async C# receiver thunk that marshals
            // a Task as the result; reached through the sync pointer it returns garbage (a sync
            // protocol refining an async one and declaring the same selector). Ordering sync
            // (non-async) siblings first makes a mixed async/sync group dispatch through the
            // ABI-compatible vtable; the async branch stays a trailing fallback.
            //
            // The branch set is also FILTERED to siblings that actually emit a vtable func field
            // (EntryEmitsVtableField) — a CLOSURE-param method that is itself async has no dispatchable
            // field (IsDispatchableAsyncClosureMethod bails on method.IsAsync), so its per-protocol
            // vtable struct carries only csVTHandle and a branch over it would reference a missing
            // member. Such siblings still register their plan below (so they emit an empty conformance
            // extension satisfied by the owner's sync witness) — they are dropped only from the
            // fan-out branch list, never from the conformance set.
            //
            // The owner still emits the body (gated on plan.Owner, not Siblings[0]); Siblings carries
            // no owner-first contract — it is purely the nil-check fan-out order. All-sync groups keep
            // GetProtocolFallbackKey order (stable sort), so output is byte-identical for every
            // non-mixed group of field-emitting siblings.
            var siblings = entries
                .Where(e => EntryEmitsVtableField(e.Proto, e.Method))
                .OrderBy(e => e.Method.IsAsync ? 1 : 0)
                .ThenBy(e => GetProtocolFallbackKey(e.Proto), StringComparer.Ordinal)
                .Select(e => (e.Proto, e.Index))
                .ToList();
            // HasFilteredPeers forces the nil-check fan-out (guarded `if let fn` branches + a
            // fatalError fallback) even when only ONE branch survives. Two filters can shrink the
            // emitted branch set below the conformance set: the ModuleHandler-level filteredKeys
            // (mixed-generic/conformance-skipped owners) AND the local EntryEmitsVtableField pass
            // above (async/throwing closure-param, method-level-generic, Self-typed, mixed-generic
            // members that emit a stub and NO vtable field). Either one dropping a peer means a C#
            // impl conforming ONLY to the dropped peer dispatches through the sole surviving
            // sibling's vtable, whose field is nil for that instance — so a bare single-branch
            // force-unwrap (`!`) would SIGSEGV. `siblings.Count < entries.Count` detects the
            // field-filter drop and routes to the guarded fatalError path (a cleaner failure for an
            // already by-design-non-dispatchable peer). Byte-identical for every group where no peer
            // was filtered (siblings.Count == entries.Count) and for genuine solo groups.
            var plan = new MethodEmissionPlan(owner, ownerIndex, siblings,
                HasFilteredPeers: filteredKeys.Contains(groupKey) || siblings.Count < entries.Count);
            foreach (var entry in entries)
                plans[(GetProtocolFallbackKey(entry.Proto), groupKey)] = plan;
        }
        return plans;
    }

    /// <summary>
    /// Builds the per-(protocol, method) sibling-fallback map consumed by
    /// <c>ProtocolProxyEmitter.EmitMethodReceiver</c>. Method counterpart of
    /// <see cref="ComputeSiblingSubscriptFallbacks"/>: each entry lists the OTHER protocols in the
    /// same same-signature group so a receiver whose interface didn't register the proxy can fall
    /// back across the sibling interfaces. Methods not in a sibling group are omitted.
    ///
    /// <para>Keyed by <c>(GetProtocolFallbackKey(proto), GetMethodSiblingMapKey(method))</c>.
    /// The map key is a structural, projection-free identifier so the receiver — which iterates
    /// the same <c>protocolDecl.Methods</c> objects but has no <see cref="EveryProtocolEmitter"/>
    /// instance to call <see cref="GetSwiftMethodFullSignature"/> — reproduces it identically.
    /// Grouping uses the projected full signature in its C#-member-identity form
    /// (<c>includeAsyncEffect: true</c>): unlike the Swift owner/peer fan-out
    /// (<see cref="ComputeMethodEmissionPlans"/>), a sync requirement and the async one it refines
    /// are DISTINCT C# members and must NOT be siblings — so this grouping deliberately diverges from
    /// the owner/peer grouping on the <c>async</c> axis (it matches <see cref="GetMethodSiblingMapKey"/>,
    /// which also carries <c>async</c>).</para>
    /// </summary>
    public IReadOnlyDictionary<(string ProtoQName, string MethodKey), IReadOnlyList<ModuleEmissionContext.SiblingMethodFallback>>
        ComputeSiblingMethodFallbacks(IEnumerable<ProtocolDecl> protocols)
    {
        var groups = new Dictionary<string, List<(ProtocolDecl Proto, MethodDecl Method, int Index)>>(StringComparer.Ordinal);
        foreach (var p in protocols)
        {
            // Carrier-prefix so cross-carrier protocols never become mutual fallback siblings — the
            // owner-body fan-out (ComputeMethodEmissionPlans) is carrier-partitioned, so a receiver
            // in one carrier's vtable is only reached for same-carrier protocols (see
            // ComputeSiblingPropertyFallbacks for the rationale).
            var carrier = GetCarrierClassName(p);
            foreach (var (method, idx) in EnumerateProtocolMethodsForDispatch(p))
            {
                // C# receiver siblings key off MEMBER identity (includeAsyncEffect: true): a sync
                // requirement and the async one it refines are distinct C# members (`Foo` vs
                // `FooAsync`), so they must land in SEPARATE groups — the sync receiver must not
                // fall back into the async-base interface (CS1061). Contrast ComputeMethodEmissionPlans,
                // which keys off the emitted Swift witness (async omitted) so the two share one owner.
                //
                // Inout-sensitivity: a value param and an inout param of the same type also project to
                // DIFFERENT C# members (`T arg` vs `ref T arg`), so a value-param method and an
                // otherwise-identical inout-param method are NOT siblings. The base key's renderer
                // (GetSwiftTypeName) drops the `inout` annotation, so without a discriminator the two
                // collapse into one group; a value receiver would then fall back into the inout
                // sibling's interface and emit `impl.F(arg)` against an `F(ref T)` member (CS1620), and
                // the real-async fan-out would do the same on its widened slot. Append a per-param
                // inout shape to split them — restoring the inout sensitivity GetMethodSiblingMapKey
                // (the storage/lookup key) already carries via TypeSpec.ToString. A homogeneous
                // (all-value or all-inout) group keeps an identical shape across members, so the only
                // groups this splits are the mixed value/inout ones the receivers cannot dispatch.
                var inoutShape = string.Join(",", method.CSSignature.Skip(1)
                    .Select(p => (p.IsInOut || (p.SwiftTypeSpec?.IsInOut ?? false)) ? "1" : "0"));
                var key = $"{carrier}{CarrierKeySeparator}" + GetSwiftMethodFullSignature(method, includeAsyncEffect: true) + "|inout:" + inoutShape;
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<(ProtocolDecl, MethodDecl, int)>();
                    groups[key] = list;
                }
                list.Add((p, method, idx));
            }
        }

        var fallback = new Dictionary<(string, string), IReadOnlyList<ModuleEmissionContext.SiblingMethodFallback>>();
        foreach (var entries in groups.Values)
        {
            if (entries.Count < 2)
                continue;
            foreach (var (proto, method, _) in entries)
            {
                var siblings = entries
                    .Where(e => e.Proto != proto)
                    .Select(e => new ModuleEmissionContext.SiblingMethodFallback(e.Proto))
                    .OrderBy(s => GetProtocolFallbackKey(s.Proto), StringComparer.Ordinal)
                    .ToList();
                fallback[(GetProtocolFallbackKey(proto), GetMethodSiblingMapKey(method))] = siblings;
            }
        }
        return fallback;
    }

    /// <summary>
    /// Structural, projection-free key identifying a method within a protocol, shared by
    /// <see cref="ComputeSiblingMethodFallbacks"/> (emitter side) and
    /// <c>ProtocolProxyEmitter.EmitMethodReceiver</c> (receiver side) to agree on the
    /// sibling-fallback map key. Uses raw <c>TypeSpec.ToString()</c> rather than the projected
    /// signature so it is computable without an <see cref="EveryProtocolEmitter"/> instance, and
    /// includes the return type so return-type overloads stay distinct, and the <c>async</c> effect
    /// so a sync method and its async refinement (sharing name + params + return type) stay distinct
    /// — they are separate witnesses projecting to separate C# members (`Foo` vs `FooAsync`).
    /// (`throws` is deliberately excluded: a non-throwing function satisfies a throwing requirement
    /// in Swift, so throwing/non-throwing same-signature methods share a witness and must stay
    /// grouped.) Must stay structurally aligned with the C#-member-identity form of
    /// <see cref="GetSwiftMethodFullSignature"/> (<c>includeAsyncEffect: true</c>) — the variant
    /// <see cref="ComputeSiblingMethodFallbacks"/> groups by — NOT the owner/peer default, which omits
    /// <c>async</c>.
    /// </summary>
    internal static string GetMethodSiblingMapKey(MethodDecl method)
    {
        var parts = method.CSSignature.Skip(1).Select(p =>
            (p.GetSwiftName() ?? p.Name ?? "_") + ":" + (p.SwiftTypeSpec?.ToString() ?? ""));
        var ret = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec?.ToString() ?? "Void";
        var effects = method.IsAsync ? " async" : "";
        return $"{method.Name}(" + string.Join(",", parts) + ")" + effects + "->" + ret;
    }

    /// <summary>
    /// Builds the dictionary key used by <see cref="ModuleEmissionContext.GetSiblingPropertyFallbacks"/>:
    /// the module name plus the full nested-type chain of the protocol's qualified name (e.g.
    /// <c>"FooModule.Outer.Inner.MyProto"</c>). Including the parent chain is required because
    /// nested protocols with the same simple name are legal in Swift; a key of just
    /// <c>{module}.{leaf}</c> would last-writer-wins-collide them in the dictionary and return
    /// the wrong sibling list (or null) at lookup time.
    /// </summary>
    public static string GetProtocolFallbackKey(ProtocolDecl protocolDecl)
    {
        var nameChain = new List<string> { protocolDecl.Name };
        BaseDecl? cursor = protocolDecl.ParentDecl;
        while (cursor is TypeDecl td)
        {
            nameChain.Insert(0, td.Name);
            cursor = td.ParentDecl;
        }
        var qualified = string.Join(".", nameChain);
        var moduleName = protocolDecl.ModuleDecl?.Name;
        return string.IsNullOrEmpty(moduleName) ? qualified : $"{moduleName}.{qualified}";
    }

    /// <summary>
    /// Emits the protocol extension that makes EveryProtocol conform to the protocol.
    /// Each method/property implementation calls back to C# via the vtable.
    /// </summary>
    /// <param name="globalEmittedSignatures">Optional set to track signatures globally across protocols.</param>
    /// <param name="nonThrowingOverrides">Signatures where throws must be suppressed (see EmitProtocolConformance).</param>
    /// <param name="propertyPlans">Optional per-property plan from <see cref="ComputePropertyEmissionPlans"/>.
    /// When non-null, a property is emitted only by its owner; sibling protocols emit empty
    /// extensions and rely on Swift's cross-extension witness resolution. The owner's body
    /// fans out across all sibling vtables so dispatch through a smaller-sibling existential
    /// still finds the populated vtable. When null, falls back to the legacy first-seen-wins
    /// dedup via <paramref name="globalEmittedSignatures"/>.</param>
    /// <param name="subscriptPlans">Optional per-subscript plan from <see cref="ComputeSubscriptEmissionPlans"/>.
    /// Sibling-subscript counterpart of <paramref name="propertyPlans"/>; same ownership / empty-extension
    /// pattern, fan-out across sibling vtables in the owner's body.</param>
    /// <param name="methodPlans">Optional per-method plan from <see cref="ComputeMethodEmissionPlans"/>.
    /// Same-signature-method counterpart of <paramref name="subscriptPlans"/>; same ownership /
    /// empty-extension pattern, fan-out across sibling vtables in the owner's body.</param>
    private void EmitProtocolExtension(SwiftWriter writer, ProtocolDecl protocolDecl,
        HashSet<string>? globalEmittedSignatures, HashSet<string>? nonThrowingOverrides = null,
        IReadOnlyDictionary<string, PropertyEmissionPlan>? propertyPlans = null,
        IReadOnlyDictionary<(string ProtoQName, string SubscriptKey), SubscriptEmissionPlan>? subscriptPlans = null,
        IReadOnlyDictionary<(string ProtoQName, string CarrierAndSignature), MethodEmissionPlan>? methodPlans = null)
    {
        var protocolName = protocolDecl.SwiftTypeName.ModuleQualifiedName;
        var vtableInstanceName = GetVtableInstanceName(protocolDecl);
        var closureHandler = new ClosureHandler(_typeDatabase);

        // Real-async reverse-dispatch witnesses (S13 Pillar C) suspend on withCheckedThrowingContinuation
        // and hand the continuation to C# through the widened Start-thunk slot; the resume path reuses the
        // forward async-closure continuation box (keyed per (module, return-type, throwing) triple) verbatim.
        // That box is a file-scope class + its success/error @_cdecl symbols, which CANNOT nest inside the
        // `extension {` method body — so emit them HERE, before the conformance comment / @available /
        // `extension` lines (a `@available` emitted at 1225 would otherwise bind to the box, not the
        // extension). EmitAsyncClosureBoxIfNeeded dedups across protocols and shares the box with any
        // forward closure of the same return type; the witness body (EmitRealAsyncWitnessImplementation)
        // references the SAME box symbols via ClosureEmitter.GetAsyncClosureBoxSymbols.
        var realAsyncBoxCtx = _emissionContext ?? ModuleEmissionContext.CreateImplicitFallback();
        bool emittedRealAsyncPreamble = false;
        foreach (var realAsyncMethod in protocolDecl.Methods)
        {
            if (!EmitsRealAsyncWitness(realAsyncMethod))
                continue;
            if (!emittedRealAsyncPreamble)
            {
                ClosureEmitter.EmitAsyncClosureBridgePreambleIfNeeded(writer, realAsyncBoxCtx);
                emittedRealAsyncPreamble = true;
            }
            var realAsyncReturnType = GetSwiftTypeName(realAsyncMethod.CSSignature.FirstOrDefault()?.SwiftTypeSpec);
            var realAsyncModule = realAsyncMethod.ModuleDecl?.Name ?? _moduleName;
            // Throwing vs non-throwing box: a throwing requirement boxes CheckedContinuation<T, Error>
            // (emits paired _success/_error @_cdecl symbols); a non-throwing requirement boxes
            // CheckedContinuation<T, Never> (emits _success only, no error channel). The witness body and
            // the C# receiver read method.Throws to match, so the box must be keyed on it too.
            ClosureEmitter.EmitAsyncClosureBoxIfNeeded(writer, realAsyncModule, realAsyncReturnType, realAsyncBoxCtx, isThrowing: realAsyncMethod.Throws);
        }

        writer.WriteLine($"// {BaseClassName} conformance to {protocolDecl.Name}");
        var availAnnotations = WrapperEmitterHelpers.MergeAvailabilityFromAncestors(
            protocolDecl.AvailabilityAnnotations, protocolDecl.ParentDecl);
        // The conformance extension carries no @_cdecl symbol; a compile failure inside it (or on the
        // `extension ... {` header) would tile to the coarse module scope. The anchor — led ahead of
        // the availability, after the human-readable comment so the strip's backward scan reaches it —
        // pins the symbol-less block to the protocol it conforms to; the post-processor strips it with
        // the block it names.
        OriginAnchorEmitter.Write(writer, FragmentOwners.ForDeclWrapper(protocolDecl).Artifact);
        WrapperEmitterHelpers.EmitSwiftAvailability(writer, availAnnotations);
        writer.WriteLine($"extension {BaseClassName}: {protocolName} {{");
        writer.Indent++;

        // Emit typealiases for associated types
        // For PAT protocols, we use type erasure by mapping associated types to Any
        foreach (var associatedType in protocolDecl.AssociatedTypes)
        {
            writer.WriteLine($"public typealias {NameProvider.EscapeSwiftKeyword(associatedType.Name)} = Any");
        }
        if (protocolDecl.AssociatedTypes.Count > 0)
        {
            writer.WriteLine();
        }

        // Track emitted members to avoid duplicates within this protocol
        var emittedMembers = new HashSet<string>();

        // Detect protocols with mixed method-level generics and non-generic members.
        // These protocols need ALL members emitted as stubs because the type projection
        // pipeline generates incorrect types for non-generic members when method-level
        // generic parameters are in scope. Stubs use raw TypeSpec rendering which is correct.
        bool isMixedGenericProtocol = IsMixedGenericProtocol(protocolDecl);

        // Emit property implementations (skip static and @objc optional properties)
        foreach (var property in protocolDecl.Properties)
        {
            if (property.IsStatic || property.IsObjCOptional)
                continue;
            // Ownership-aware dedup: when a property name+type is shared across multiple
            // protocols (e.g., Nameable's get-only `var name: String` and MutableNamed's
            // get+set `var name: String`), exactly one protocol — chosen by accessor-set
            // fatness with lexicographic tie-break — emits the body. Other protocols emit
            // empty extensions and conform via Swift's cross-extension witness resolution.
            // Without this, the first-seen-wins legacy dedup let a get-only declaration
            // win, leaving the get+set extension empty and rejected by swiftc as
            // "does not conform — missing set witness".
            PropertyEmissionPlan? plan = null;
            // Carrier-prefixed to match the partition in ComputePropertyEmissionPlans: a property
            // shared across umbrella carriers has a distinct plan (and owner) per carrier.
            var planKey = $"{GetCarrierClassName(protocolDecl)}{CarrierKeySeparator}{property.Name}|{property.SwiftTypeSpec}";
            if (propertyPlans != null && propertyPlans.TryGetValue(planKey, out plan))
            {
                if (plan.Owner != protocolDecl)
                {
                    _logger.LogDebug($"Skipping property '{property.Name}' in {protocolDecl.Name}: owned by {plan.Owner.Name}");
                    continue;
                }
            }
            else if (globalEmittedSignatures != null)
            {
                var swiftSignature = $"var_{property.Name}";
                if (!globalEmittedSignatures.Add(swiftSignature))
                {
                    _logger.LogDebug($"Skipping property '{property.Name}' in {protocolDecl.Name}: conflicts with already-emitted property");
                    continue;
                }
            }
            if (emittedMembers.Add($"property:{property.Name}"))
            {
                // @objc protocol existential in an unsupported nested position (container/tuple/closure):
                // dropped fail-closed from the C# interface AND the reverse-dispatch vtable slot (see
                // VtableLayoutBuilder.ClassifyProperty, which makes it skip-but-consume). The Swift
                // requirement still needs a witness so this EveryProtocol conformance compiles, so emit a
                // fatalError stub — never dispatched, since C# has no interface member and no vtable slot to
                // fill. Checked FIRST to match the classifier's precedence.
                if (HasUnsupportedObjCExistentialProperty(property))
                    EmitObjCExistentialPropertyStub(writer, property);
                // Closure properties: dispatchable shapes go through the real proxy
                // dispatch path; non-dispatchable shapes get fatalError stubs.
                else if (HasClosureInPropertyType(property))
                {
                    if (!isMixedGenericProtocol && IsDispatchableClosureProperty(property, closureHandler))
                        EmitDispatchableClosurePropertyImplementation(writer, property, protocolDecl, vtableInstanceName, closureHandler, plan, availAnnotations);
                    else
                        EmitClosurePropertyStub(writer, property);
                }
                // Self-typed properties get fatalError() stubs — τ_0_0 can't be dispatched
                // through the vtable. Renders τ_0_0 as EveryProtocol (the conforming type).
                else if (ContainsSelfTypeParam(property.SwiftTypeSpec))
                    EmitSelfTypedPropertyStub(writer, property);
                // Mixed generic protocols: all properties get stubs to avoid incorrect type projections
                else if (isMixedGenericProtocol)
                    EmitClosurePropertyStub(writer, property);
                else
                    EmitPropertyImplementation(writer, property, protocolDecl, vtableInstanceName, plan, availAnnotations);
            }
        }

        // Emit subscript implementations (skip static subscripts - not part of witness table)
        int subscriptIndex = 0;
        var protoQNameForSubscripts = GetProtocolFallbackKey(protocolDecl);
        foreach (var subscript in protocolDecl.Subscripts)
        {
            if (subscript.IsStatic)
                continue;
            var subscriptKey = GetSubscriptKey(subscript, subscriptIndex);
            var swiftSignature = $"subscript_{subscriptKey}";
            // Ownership-aware dedup: when a subscript signature is shared across multiple
            // protocols with differing accessor sets (e.g. SiblingIndexed's get-only and
            // SiblingMutableIndexed's get+set), the owner (fattest accessor set wins, lex
            // tie-break) emits the body. Siblings emit empty extensions and conform via
            // Swift's cross-extension witness resolution against the owner's declaration.
            // Without this, the first-seen-wins global dedup let a get-only declaration win,
            // leaving the get+set extension empty and rejected by swiftc as "does not conform —
            // missing set/get witness".
            SubscriptEmissionPlan? subscriptPlan = null;
            if (subscriptPlans != null && subscriptPlans.TryGetValue((protoQNameForSubscripts, subscriptKey), out subscriptPlan))
            {
                if (subscriptPlan.Owner != protocolDecl)
                {
                    _logger.LogDebug($"Skipping subscript '{subscriptKey}' in {protocolDecl.Name}: owned by {subscriptPlan.Owner.Name}");
                    subscriptIndex++;
                    continue;
                }
            }
            else if (globalEmittedSignatures != null && !globalEmittedSignatures.Add(swiftSignature))
            {
                _logger.LogDebug($"Skipping subscript in {protocolDecl.Name}: conflicts with already-emitted subscript");
                subscriptIndex++;
                continue;
            }
            if (emittedMembers.Add(subscriptKey))
            {
                // @objc existential in an unsupported nested position → dropped fail-closed from the
                // interface + vtable slot (skip-but-consume); witness the Swift requirement with a stub.
                if (HasUnsupportedObjCExistentialSubscript(subscript))
                    EmitObjCExistentialSubscriptStub(writer, subscript, subscriptIndex);
                // Self-typed subscripts get fatalError() stubs
                else if (ContainsSelfTypeParam(subscript.ReturnTypeSpec) ||
                    subscript.IndexParameters.Any(ip => ContainsSelfTypeParam(ip.SwiftTypeSpec)))
                    EmitSelfTypedSubscriptStub(writer, subscript, subscriptIndex);
                // Mixed generic protocols: all subscripts get stubs to avoid incorrect type projections
                else if (isMixedGenericProtocol)
                    EmitSelfTypedSubscriptStub(writer, subscript, subscriptIndex);
                else
                    EmitSubscriptImplementation(writer, subscript, protocolDecl, vtableInstanceName, subscriptIndex, subscriptPlan, availAnnotations);
            }
            subscriptIndex++;
        }

        // Emit method implementations.
        // The vtable SLOT INDEX for each method comes from the shared VtableLayout model — the SAME
        // ordered slot list EmitProtocolVtableStruct and the C# vtable structs render — so the
        // extension body cannot drift out of index agreement with the struct it dispatches through
        // (Bug #21). methodIndices stays the local "first-seen" set that drives isNewMethod (one body
        // per raw-distinct requirement); only the index VALUE is now model-sourced.
        var methodSlotIndices = new VtableLayoutBuilder(_typeDatabase).Build(protocolDecl).MethodSlotIndexByKey;
        // Slots the layout KEEPS a Swift field for but the C# fillability walk leaves NULL — the
        // collapsed existential overloads. Their witness must trap, not force-unwrap the nil field.
        var collapsedUnfilledSlotKeys = ComputeCollapsedUnfilledMethodSlotKeys(protocolDecl, closureHandler);
        var methodIndices = new Dictionary<string, int>();
        // Tracks emitted EveryProtocol witness-body signatures (witnessGroupKey — async-included only
        // for a real-async witness) so each rendered Swift `func` body appears at most once per
        // extension. Distinct from methodIndices, which is async-SENSITIVE and allocates a separate
        // vtable slot per effect-overloaded requirement. See the intra-protocol effect-overload guard below.
        var emittedBodySignatures = new HashSet<string>();
        var protoQNameForMethods = GetProtocolFallbackKey(protocolDecl);
        foreach (var method in protocolDecl.Methods)
        {
            // Skip constructors, static, and @objc optional methods
            if (method.IsConstructor || method.MethodType == MethodType.Static)
                continue;
            if (method.IsObjCOptional)
                continue;

            var methodKey = GetMethodKey(method);

            // Assign vtable index matching EmitProtocolVtableStruct logic.
            // This MUST happen before the global skip check to prevent index drift (Bug #21).
            // The vtable struct assigns sequential indices without knowledge of global skips,
            // so the extension must use the same indices.
            bool isNewMethod = false;
            if (!methodIndices.TryGetValue(methodKey, out var idx))
            {
                idx = methodSlotIndices[methodKey];
                methodIndices[methodKey] = idx;
                isNewMethod = true;
            }

            var swiftSignature = GetSwiftMethodSignature(method);
            var fullSignature = GetSwiftMethodFullSignature(method);
            // The witness GROUP/DEDUP key carries the `async` effect ONLY for a real-async witness
            // (S13 Pillar C), which emits a distinct `func m(...) async throws -> T` declaration and so
            // must group, look up its plan, and dedup its body separately from the sync witness sharing
            // the same name/params/return. Matches the key ComputeMethodEmissionPlans grouped on, so the
            // plan lookup below resolves. `fullSignature` (async-OMITTED) stays the key for the
            // nonThrowingOverrides lookup, which is tracked async-blind by design (a non-throwing method
            // satisfies a throwing requirement regardless of effect).
            var witnessGroupKey = GetSwiftMethodFullSignature(method, includeAsyncEffect: EmitsRealAsyncWitness(method));

            // Ownership-aware dedup: when a method full-signature (name + parameter types + return
            // type) is shared across multiple protocols, exactly one — chosen by lexicographic
            // GetProtocolFallbackKey order — owns the body. Other protocols emit empty extensions
            // and conform via Swift's cross-extension witness resolution. The owner's body fans out
            // across every sibling vtable so dispatch through a smaller-sibling existential (e.g. a
            // C# impl conforming to only a non-owner protocol) finds the populated vtable instead of
            // reading the owner's nil global vtable and crashing. See ComputeMethodEmissionPlans.
            //
            // Full signatures let same-name methods with different parameter types coexist as Swift
            // overloads (validate(input: String) vs validate(input: Int32) from different protocols).
            // Return-type-only conflicts (parse(data:)->Int vs parse(data:)->Void) land in DISTINCT
            // groups (return is part of the key), so each still emits its own body — same as the
            // legacy dedup, which the wrapper strip/retry mechanism handles.
            MethodEmissionPlan? methodPlan = null;
            // Carrier-prefixed to match the partition in ComputeMethodEmissionPlans, whose plans dict
            // is keyed by the carrier-prefixed group key: a method shared across umbrella carriers has
            // a distinct owner/plan per carrier. (witnessGroupKey stays bare for the per-extension body
            // dedup and the legacy globalEmittedSignatures fallback, both carrier-constant.)
            var methodPlanLookupKey = $"{GetCarrierClassName(protocolDecl)}{CarrierKeySeparator}{witnessGroupKey}";
            if (methodPlans != null && methodPlans.TryGetValue((protoQNameForMethods, methodPlanLookupKey), out methodPlan))
            {
                if (methodPlan.Owner != protocolDecl)
                {
                    _logger.LogDebug($"Skipping method '{method.Name}' in {protocolDecl.Name}: owned by {methodPlan.Owner.Name}");
                    continue;
                }
            }
            // Legacy first-seen-wins dedup for callers that don't supply method plans.
            else if (globalEmittedSignatures != null && !globalEmittedSignatures.Add(witnessGroupKey))
            {
                _logger.LogDebug($"Skipping method '{method.Name}' in {protocolDecl.Name}: conflicts with already-emitted method");
                continue;
            }

            // Only emit method implementation for new methods (not within-protocol duplicates).
            //
            // Intra-protocol effect-overload guard: a single protocol may declare
            // BOTH a sync and an async method sharing name + parameter types + return type — Swift
            // effectful overloading produces two DISTINCT witness-table requirements. The slot key
            // (GetMethodKey) is async-sensitive, so each gets its OWN vtable slot and `isNewMethod`
            // is true for both. But the EveryProtocol witness BODY rendered for each is the
            // IDENTICAL sync-shaped `func m(...) -> T`: EmitMethodImplementation suppresses the
            // `async` keyword on the pure-Swift carrier path (a sync witness satisfies an async
            // requirement), so emitting both bodies is an invalid Swift redeclaration. Gate the body
            // on the async-omitted full signature (== the rendered witness signature) so it emits at
            // most ONCE per protocol; the single sync body's sibling fan-out already covers both
            // slots. The async slot still exists for the C# interface/proxy layout — only its
            // duplicate Swift body is suppressed. This is a no-op for every other shape: a
            // return-type-only same-protocol conflict already has `isNewMethod == false` for the
            // second method (return-insensitive GetMethodKey), and genuine overloads have distinct
            // full signatures, so HashSet.Add returns true.
            //
            // The key is witnessGroupKey, NOT the async-omitted fullSignature: a real-async witness
            // (S13 Pillar C) emits `func m(...) async throws -> T`, a DISTINCT Swift declaration from
            // the sync `func m(...) -> T`, so an intra-protocol sync+real-async effect overload must
            // emit BOTH bodies. Keying on witnessGroupKey (async-included only for the real-async one)
            // lets both Add, whereas the async-omitted key would suppress the second as a redeclaration.
            if (isNewMethod && emittedBodySignatures.Add(witnessGroupKey))
            {
                // @objc protocol existential in an unsupported nested position (container/tuple/closure)
                // on any parameter or the return: dropped fail-closed from the C# interface AND the
                // reverse-dispatch vtable slot (see VtableLayoutBuilder.ClassifyMethod, skip-but-consume).
                // The Swift requirement still needs a witness, so emit a fatalError stub — never dispatched
                // (no interface member, no filled vtable slot). Checked first here for clarity; note
                // ClassifyMethod orders the non-dispatchable-closure and Self/mixed-generic exclusions BEFORE
                // this one, so a member that is BOTH would record a different SlotVerdict there. That is
                // harmless: every one of those verdicts is Included=false skip-but-consume, so the vtable
                // struct omits the field and consumes the index identically regardless of which reason wins,
                // and the stub emitted here is a fatalError either way. Membership — not the verdict label —
                // is the lockstep invariant, and it agrees.
                if (MethodHasUnsupportedObjCExistential(method))
                {
                    EmitObjCExistentialMethodStub(writer, method);
                }
                // Closure methods on the dispatch surface get a real implementation that
                // extracts (fnPtr, ctx) and forwards to C# through the expanded cdecl
                // vtable. Off-surface closure methods that aren't yet lifted into dispatch
                // get the fatalError stub.
                else if (HasClosureInMethodSignature(method))
                {
                    if (IsDispatchableClosureMethod(method, closureHandler))
                        EmitClosureMethodImplementation(writer, method, protocolDecl, vtableInstanceName, idx, closureHandler, methodPlan, availAnnotations);
                    else if (IsDispatchableAsyncClosureMethod(method, closureHandler))
                        EmitClosureMethodImplementation(writer, method, protocolDecl, vtableInstanceName, idx, closureHandler, methodPlan, availAnnotations);
                    else if (IsDispatchableClosureReturningMethod(method, closureHandler))
                        EmitDispatchableClosureReturningMethodImplementation(writer, method, protocolDecl, vtableInstanceName, idx, closureHandler, methodPlan, availAnnotations);
                    else
                        EmitClosureMethodStub(writer, method);
                }
                // Methods with method-level generics (τ_1_0+) get stub implementations.
                // EveryProtocol satisfies the protocol requirement, but can't dispatch through
                // the vtable (C# can't handle method-level generic dispatch). This branch must
                // also catch the combined "method-level + Self" shape (a method that uses both
                // <T> and Self in its signature): EmitMethodLevelGenericStub's RenderTypeSpec
                // already handles τ_0_* → EveryProtocol substitution, so it can render the
                // combined signature correctly. Routing only the "pure method-level" case here
                // and letting the combined case fall through to EmitSelfTypedMethodStub would
                // silently drop the generic clause and emit a witness signature that doesn't
                // satisfy the protocol requirement (Any vs T mismatch → Swift compile error).
                else if (HasMethodLevelGenericInSignature(method))
                {
                    EmitMethodLevelGenericStub(writer, method);
                }
                // Methods with Self-typed (τ_0_*) params/return but no method-level generics
                // get fatalError() stubs. Renders τ_0_0 as EveryProtocol (the conforming type)
                // to satisfy Swift's type system — Self IS EveryProtocol in the conformance
                // context.
                else if (HasSelfTypeParamInSignature(method))
                {
                    EmitSelfTypedMethodStub(writer, method);
                }
                // Mixed generic protocols: all methods get stubs to avoid incorrect type projections
                else if (isMixedGenericProtocol)
                {
                    EmitClosureMethodStub(writer, method);
                }
                // An inout ObjC-bridgeable param (inout URL/URLRequest/Decimal, or the optional
                // inout URL?) would need the mutated ObjC pointer bridged back into the Swift value
                // type after the vtable call — a writeback path neither the Swift caller arm nor the
                // C# receiver implements. Emit a trap stub so the requirement is satisfied, rather
                // than the type-mismatched pointer writeback EmitMethodImplementation would otherwise
                // produce (the optional param-in arm's `{p}Ref` writeback source is an
                // UnsafeMutableRawPointer?, not the URL? the inout signature declares).
                else if (MethodHasInOutObjCBridgeableParam(method))
                {
                    EmitInOutObjCBridgeableMethodStub(writer, method);
                }
                // Collapsed existential overload: this method KEEPS its own vtable slot (raw-distinct
                // GetMethodKey) but the C# fillability walk (proxy receiver + static-init) leaves that
                // slot NULL, because two raw-distinct existential overloads collapse onto ONE C#
                // interface method — the first (declaration-order) overload fills the slot, this later
                // one has no C# member to reverse-dispatch into. The real dispatch body below would
                // force-unwrap the nil slot and trap at the point of dispatch; emit a branded fatalError
                // stub instead so the Swift requirement is satisfied and the failure mode is explicit.
                // ComputeCollapsedUnfilledMethodSlotKeys mirrors the receiver loop's fillability filters.
                else if (collapsedUnfilledSlotKeys.Contains(methodKey))
                {
                    EmitCollapsedOverloadMethodStub(writer, method);
                }
                // Real-async reverse-dispatch witness (S13 Pillar C): emit a genuine
                // `func m(...) async throws -> T` that suspends on withCheckedThrowingContinuation and
                // hands the continuation to C# through the widened Start-thunk slot, replacing the
                // thread-BLOCKING sync witness. Subordinate to every structural guard above
                // (closure / generic / Self / mixed-generic / inout), which EmitsRealAsyncWitness also
                // rejects, so a method only reaches here once it is the plain value-marshalled shape.
                else if (EmitsRealAsyncWitness(method))
                {
                    EmitRealAsyncWitnessImplementation(writer, method, protocolDecl, vtableInstanceName, idx, methodPlan, availAnnotations);
                }
                else
                {
                    // If this full signature is in the non-throwing overrides set, suppress throws.
                    // A non-throwing method satisfies both throwing and non-throwing protocol requirements,
                    // but a throwing method does NOT satisfy a non-throwing requirement.
                    // Uses full signature so overloads with different types are tracked independently.
                    var effectiveThrows = method.Throws &&
                        !(nonThrowingOverrides?.Contains(fullSignature) == true);
                    EmitMethodImplementation(writer, method, protocolDecl, vtableInstanceName, idx, effectiveThrows, methodPlan, availAnnotations);
                }
            }
        }

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    /// <summary>
    /// Gets a Swift method signature string for conflict detection.
    /// Internal so ModuleHandler can use it for pre-pass analysis.
    /// </summary>
    internal string GetSwiftMethodSignature(MethodDecl method)
    {
        // Generate signature like "removeAll()" or "process(_:)"
        var paramLabels = new List<string>();
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            var param = method.CSSignature[i];
            var label = GetSwiftParameterLabel(param, i);
            paramLabels.Add(label == "_" ? "_" : label);
        }
        return $"{method.Name}({string.Join(":", paramLabels)}{(paramLabels.Count > 0 ? ":" : "")})";
    }

    /// <summary>
    /// Gets a full Swift method signature including parameter types and return type.
    /// Used for global dedup and non-throwing override tracking.
    /// </summary>
    /// <param name="includeAsyncEffect">When false (default) the <c>async</c> effect is OMITTED so
    /// the key mirrors the EMITTED pure-Swift witness signature — used by the Swift owner/peer dedup
    /// (<see cref="ComputeMethodEmissionPlans"/>) and non-throwing-override tracking, where a sync
    /// method and the async one it refines emit the same witness and must group together. When true
    /// the <c>async</c> effect is INCLUDED so the key mirrors C# MEMBER identity — used by the C#
    /// receiver sibling-fallback grouping (<see cref="ComputeSiblingMethodFallbacks"/>), where the
    /// sync and async requirements project to distinct members (<c>Foo</c> vs <c>FooAsync</c>) and
    /// must NOT be siblings. See the body for the full rationale.</param>
    internal string GetSwiftMethodFullSignature(MethodDecl method, bool includeAsyncEffect = false)
    {
        var parts = new List<string>();
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            var param = method.CSSignature[i];
            var label = GetSwiftParameterLabel(param, i);
            var typeName = GetSwiftTypeName(param.SwiftTypeSpec);
            parts.Add($"{label}:{typeName}");
        }
        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var returnStr = returnType != null && !returnType.IsEmptyTuple ? GetSwiftTypeName(returnType) : "Void";
        // `async` is included ONLY when includeAsyncEffect is set (the C# receiver
        // sibling-fallback grouping). For the default — Swift owner/peer dedup and non-throwing
        // override tracking — it is OMITTED, because the EveryProtocol witness emitted for a
        // pure-Swift base drops `async` (a sync candidate satisfies an async requirement; see
        // EmitMethodImplementation's asyncDecl, gated on `_useObjCBase`). A sync method and the
        // async method it refines therefore emit the IDENTICAL Swift signature and MUST stay in one
        // owner/peer group: one owner emits the shared sync witness, the other an empty extension.
        // Distinguishing them in this key splits them into two owners that BOTH emit
        // `func foo(...) -> T` on EveryProtocol -> "invalid redeclaration" (a sync protocol
        // conforming to an async protocol and declaring the same selector).
        //
        // The async DISTINCTION the C# side needs — a sync `Foo` and an async `FooAsync` are
        // separate C# members, so the sync receiver must NOT treat the async-base interface as a
        // sibling (else it emits `impl.Foo(...)` against an interface declaring only `FooAsync` ->
        // CS1061) — is applied by the includeAsyncEffect:true caller (ComputeSiblingMethodFallbacks).
        //
        // (`throws` is deliberately NOT in the key in either mode: a non-throwing function satisfies
        // a throwing requirement in Swift, so throwing/non-throwing same-signature methods share a
        // witness and must stay grouped — see the nonThrowingOverrides mechanism.)
        var effects = (method.IsAsync && includeAsyncEffect) ? " async" : "";
        return $"{method.Name}({string.Join(",", parts)}){effects}->{returnStr}";
    }

    /// <summary>
    /// Emits Swift functions that export the protocol witness table and type metadata.
    /// These are called via P/Invoke from C# to get the witness table pointer.
    /// </summary>
    public void EmitWitnessTableGetter(SwiftWriter writer, ProtocolDecl protocolDecl)
    {
        var protocolName = protocolDecl.SwiftTypeName.ModuleQualifiedName;
        var getterFunctionName = GetWitnessTableGetterFunctionName(protocolDecl);
        var mangledGetterName = GetWitnessTableGetterMangledName(protocolDecl);
        var sizeFunctionName = GetExistentialSizeGetterFunctionName(protocolDecl);
        var mangledSizeName = GetExistentialSizeGetterMangledName(protocolDecl);
        var availAnnotations = WrapperEmitterHelpers.MergeAvailabilityFromAncestors(
            protocolDecl.AvailabilityAnnotations, protocolDecl.ParentDecl);
        var availPrefix = WrapperEmitterHelpers.BuildAvailabilityHeredocPrefix(availAnnotations, "            ");

        var baseClass = BaseClassName;
        writer.WriteLines($$"""
            // Returns the protocol witness table pointer for {{baseClass}} conforming to {{protocolDecl.Name}}.
            // C# calls this via P/Invoke (CallConvCdecl) to obtain the witness table for existential
            // container construction. Exported as a C entry point (@_cdecl) so the symbol is a linker
            // root that survives dead-stripping on NativeAOT/device builds — nothing in the Swift
            // wrapper references it, so an unreferenced free function would otherwise be dropped.
            {{availPrefix}}@_cdecl("{{mangledGetterName}}")
            public func {{getterFunctionName}}() -> UnsafeRawPointer {
                let instance = {{baseClass}}()
                return withExtendedLifetime(instance) {
                    var proto: any {{protocolName}} = instance
                    return withUnsafeBytes(of: &proto) { buffer in
                        // Witness table is the last pointer-sized word in the existential container.
                        // Layout depends on class-bound vs opaque:
                        //   Opaque:      [payload0] [payload1] [payload2] [metadata] [WT] (5 words)
                        //   Class-bound: [classRef] [WT] (2 words)
                        // Using MemoryLayout<any Protocol>.size - pointer size handles both.
                        let witnessTableOffset = MemoryLayout<any {{protocolName}}>.size - MemoryLayout<Int>.size
                        return buffer.baseAddress!.advanced(by: witnessTableOffset)
                            .assumingMemoryBound(to: UnsafeRawPointer.self).pointee
                    }
                }
            }

            // Reports the existential size Swift itself uses for `any {{protocolDecl.Name}}`, so the C#
            // proxy can check ONCE — when it first resolves the witness table above — that the container
            // shape it fills matches the shape Swift reads. The two shapes place the witness table in
            // different words (opaque: word 4 of 5; class-bound: word 1 of 2; a pure @objc existential is
            // a single object pointer with no witness word at all), and the C# side picks between them
            // from parsed ABI facts. When those facts mis-classify the protocol, Swift reads a witness
            // table out of a word C# left zero and traps inside the framework on the first callback, with
            // no managed frame to blame. This accessor makes that a named exception at the boundary.
            // Exported as a C entry point (@_cdecl) alongside the getter above, for the same
            // dead-strip-survival reason, and emitted in lockstep with it so a proxy that declares the
            // witness getter can always reach the size too.
            {{availPrefix}}@_cdecl("{{mangledSizeName}}")
            public func {{sizeFunctionName}}() -> Int {
                return MemoryLayout<any {{protocolName}}>.size
            }

            """);
    }

    /// <summary>
    /// Emits Swift function that exports the EveryProtocol type metadata.
    /// </summary>
    public void EmitTypeMetadataGetter(SwiftWriter writer)
    {
        writer.WriteLines($$"""
            // Returns the type metadata pointer for EveryProtocol.
            // C# calls this via P/Invoke (CallConvCdecl) to construct existential containers.
            // Exported as a C entry point (@_cdecl) for the same dead-strip-survival reason as the
            // witness-table getters above.
            @_cdecl("Get_EveryProtocol_TypeMetadata")
            public func getEveryProtocolTypeMetadata() -> UnsafeRawPointer {
                return unsafeBitCast(EveryProtocol.self as Any.Type, to: UnsafeRawPointer.self)
            }

            """);
    }

    /// <summary>
    /// Emits the SetVtable function that C# calls to register its vtable.
    /// </summary>
    public void EmitSetVtableFunction(SwiftWriter writer, ProtocolDecl protocolDecl)
    {
        var vtableName = GetVtableStructName(protocolDecl);
        var vtableInstanceName = GetVtableInstanceName(protocolDecl);
        var setFunctionName = GetSetVtableFunctionName(protocolDecl);
        var mangledSetFunctionName = GetSetVtableMangledName(protocolDecl);
        var availAnnotations = WrapperEmitterHelpers.MergeAvailabilityFromAncestors(
            protocolDecl.AvailabilityAnnotations, protocolDecl.ParentDecl);
        var availPrefix = WrapperEmitterHelpers.BuildAvailabilityHeredocPrefix(availAnnotations, "            ");

        writer.WriteLines($$"""
            // Called by C# (CallConvCdecl) to register the protocol vtable. Exported as a C entry
            // point (@_cdecl) so the symbol is a linker root that survives dead-stripping on
            // NativeAOT/device builds, matching the witness-table getters.
            {{availPrefix}}@_cdecl("{{mangledSetFunctionName}}")
            public func {{setFunctionName}}(uvt: UnsafeRawPointer) {
                let vt: UnsafePointer<{{vtableName}}> = uvt.assumingMemoryBound(to: {{vtableName}}.self)
                {{vtableInstanceName}} = vt.pointee
            }

            """);
    }

    /// <summary>
    /// Emits all Swift code needed for a protocol's EveryProtocol conformance.
    /// </summary>
    public void EmitProtocolConformance(SwiftWriter writer, ProtocolDecl protocolDecl)
    {
        EmitProtocolConformance(writer, protocolDecl, null);
    }

    /// <summary>
    /// Pre-scans all protocols to populate _skippedProtocols BEFORE any emission.
    /// This makes inherited-conformance checks order-independent: even if ChildProtocol
    /// appears before ParentProtocol in the list, the pre-scan will have already identified
    /// ParentProtocol as unsatisfied if it has static method requirements, etc.
    /// </summary>
    /// <param name="protocols">The module-local protocols offered for conformance emission.</param>
    /// <param name="crossModuleParents">Parent decls from --framework-dependency modules.</param>
    /// <param name="unavailableConformances">
    /// Names of module-local protocols the caller excluded from <paramref name="protocols"/>, so
    /// this pass can propagate from them even though it never sees their decls. Both the
    /// simple and module-qualified spellings should be supplied.
    /// </param>
    public void PreScanProtocols(
        IReadOnlyList<ProtocolDecl> protocols,
        IReadOnlyList<ProtocolDecl>? crossModuleParents = null,
        IEnumerable<string>? unavailableConformances = null)
    {
        // Capture the module-local protocol list so transitive class-bound /
        // NSObjectProtocol-only checks can resolve inherited module-local protocols
        // by name at the routing checkpoints below.
        _allProtocols = protocols;

        // Capture the cross-module parent decls so the cross-carrier suppression gate can
        // resolve a child's inherited parent that lives in a --framework-dependency module and
        // compare its emitted carrier (see AnyInheritedProtocolOnDifferentCarrier). Without this,
        // `local Child : Dep.Parent, NSObjectProtocol` routes to EveryObjCProtocol while Dep.Parent
        // emits on plain EveryProtocol, and the split is missed.
        _crossModuleParents = crossModuleParents;

        // Seed BEFORE pass 1 so the fixpoint below propagates from protocols this emitter never
        // sees. A protocol dropped by the module-level candidacy filter gets no conformance
        // extension, but nothing in passes 1-2 can learn that — they only iterate the survivors.
        if (unavailableConformances != null)
            _unavailableConformances.UnionWith(unavailableConformances);

        // Pass 1: identify protocols that will be skipped by structural gates
        foreach (var protocolDecl in protocols)
        {
            if (WillSkipConformance(protocolDecl))
            {
                _skippedProtocols.Add(protocolDecl.Name);
                if (protocolDecl.SwiftTypeName != null)
                    _skippedProtocols.Add(protocolDecl.SwiftTypeName.ModuleQualifiedName);
            }
        }

        // Pass 2: propagate skips through inherited conformances.
        // A protocol inheriting one that gets no witness must also be skipped — declaring the
        // child conformance makes Swift demand the parent's witnesses, which won't exist.
        // Repeat until no new skips are found (handles transitive chains).
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var protocolDecl in protocols)
            {
                if (_skippedProtocols.Contains(protocolDecl.Name))
                    continue;
                if (HasUnavailableInheritedConformance(protocolDecl))
                {
                    _skippedProtocols.Add(protocolDecl.Name);
                    if (protocolDecl.SwiftTypeName != null)
                        _skippedProtocols.Add(protocolDecl.SwiftTypeName.ModuleQualifiedName);
                    changed = true;
                }
            }
        }
    }

    /// <summary>
    /// Checks whether a protocol's EveryProtocol conformance would be skipped by the structural gates.
    /// Does NOT check order-dependent gates (method type conflicts) — those are checked at emission time.
    /// </summary>
    private bool WillSkipConformance(ProtocolDecl protocolDecl)
    {
        if (protocolDecl.HasSelfRequirement)
            return true;

        if (protocolDecl.HasMissingRequirements)
            return true;

        if (protocolDecl.HasConventionCClosureParameters)
            return true;

        // Required-but-suppressed gate: a protocol requirement that parsed
        // successfully but is `@_spi`-protected cannot have a witness in EveryProtocol's
        // conformance — PropertyHandler/MethodHandler skip SPI members, leaving Swift's
        // type-checker to reject the extension at compile time. Skip the entire
        // conformance to keep the wrapper buildable. The gate covers any required member
        // Kind (property, method, constructor). IsModuleInternal is intentionally NOT
        // consulted: see HasSuppressedRequiredMember for the rationale.
        if (HasSuppressedRequiredMember(protocolDecl))
            return true;

        // Hidden-requirement gate: the protocol body declares an `__`-prefixed requirement
        // that swift-api-digester strips from the ABI JSON, and no same-protocol extension
        // supplies a default. The parser never sees the requirement, so the EveryProtocol
        // extension emits no witness — Swift rejects the conformance at compile time
        // (e.g. RealityFoundation.MaterialFunction.__linkSPI). Detected via swiftinterface
        // cross-reference by the SwiftSyntax host.
        if (protocolDecl.HasUnsatisfiedHiddenRequirements)
            return true;

        // TBD-method-descriptor gate: at least one required method's Tq descriptor is
        // missing from the framework's TBD on this slice (Apple ships the swiftinterface
        // declaration but not the descriptor symbol — observed on Mac Catalyst for
        // LiveCommunicationKit.ConversationManagerDelegate.didActivate / didDeactivate).
        // The synthesized EveryProtocol witness table would reference an unresolved
        // symbol and the wrapper link would fail. Detected during ABI parsing in
        // SwiftABIParser.HandleNominalDecl.
        if (protocolDecl.HasMissingTbdMethodDescriptors)
            return true;

        // Self-typed members (τ_0_*) and mixed method-level generics no longer skip the
        // entire protocol. Self-typed members get fatalError() stubs with τ_0_0→EveryProtocol,
        // and method-level generic methods get fatalError() stubs alongside normal vtable
        // dispatch for non-generic members.

        // Subscript-level generics gate (e.g. RealityFoundation.MeshBufferContainer's
        // `subscript<S: MeshBufferSemantic>(_: S) -> MeshBuffer<S.Element>?`). The Self-typed
        // stub path substitutes S → EveryProtocol and S.Element → Any, producing
        // `subscript(_: EveryProtocol) -> MeshBuffer<Any>?` which does not satisfy a
        // method-generic protocol requirement. SubscriptDecl has no generics field, so we
        // detect the shape from the parsed signature: a dependent member whose base is a
        // single-letter generic (NOT Self / τ_0_*) means the base is bound at subscript
        // scope and EveryProtocol cannot witness it.
        if (protocolDecl.Subscripts.Any(s => !s.IsStatic && HasSubscriptLevelGenericDependentMember(s)))
            return true;

        if (IsClassBoundProtocol(protocolDecl, _allProtocols))
        {
            // NSObjectProtocol-only protocols (e.g. PaymentSdkAuthenticationContext) re-route
            // through the NSObject-rooted EveryObjCProtocol helper class instead of skipping.
            // NSCoding is also admitted: it implies NSObjectProtocol and its two requirements
            // (encode(with:) / init?(coder:)) are witnessed by a no-op stub on the synthetic
            // carrier (EmitObjCCodingStubIfNeeded), which never archives. Protocols that require
            // NSSecureCoding / NSCopying / NSMutableCopying still skip — those need real
            // encoding / copying surfaces a synthesised proxy can't supply.
            if (!IsNSObjectProtocolOnly(protocolDecl, _allProtocols))
                return true;
        }

        if (HasClassSuperclassRequirement(protocolDecl, _typeDatabase, _allProtocols))
        {
            // Failure B: protocols rooted at RealityFoundation.Entity reroute through
            // the EveryEntityProtocol helper class instead of skipping. Any other
            // concrete class-superclass requirement still skips because the helper
            // can inherit only one Swift class. See IsEntityRootedProtocol.
            if (!IsEntityRootedProtocol(protocolDecl, _typeDatabase, _allProtocols))
                return true;
        }

        // Carrier-split gate: a refined @objc protocol whose inherited @objc parent routes to a
        // different umbrella carrier cannot have its `extension {carrier}: Child` satisfied — the
        // parent's witness lives on the other carrier. Seed the skip here so PreScanProtocols marks
        // it in _skippedProtocols: IsConformanceSkipped then drops it from the sibling-plan owner
        // input (and keeps it as a filtered peer), and Pass-2 genericSig propagation sees it, in
        // lockstep with the emission-ladder gate in EmitProtocolConformance. See
        // HasCrossCarrierInheritedRequirement.
        if (HasCrossCarrierInheritedRequirement(protocolDecl))
            return true;

        if (InheritsCaseIterable(protocolDecl))
            return true;

        if (ModuleHandler.InheritsProtocolWithAssociatedTypes(protocolDecl))
            return true;

        if (InheritsUnsatisfiedStdlibProtocol(protocolDecl))
            return true;

        if (protocolDecl.Methods.Any(m => m.IsConstructor))
            return true;

        // Noncopyable-member gate. The emission ladder (EmitProtocolConformance) skips any
        // protocol whose method signatures contain ~Copyable parameters or return types,
        // because the trampoline copies values through `inout` pointers. This prescan MUST
        // record the same skip: Pass-2 transitive `genericSig` propagation keys off
        // _skippedProtocols, so if a noncopyable parent is not seeded here, a genericSig-
        // constrained child declared *before* its parent emits a conformance referencing a
        // parent conformance that the emission ladder then refuses to produce — a dangling
        // `extension EveryProtocol: Child` that fails `swiftc` (order-dependent, fail-closed).
        // Keeping this in lockstep with the EmitProtocolConformance emission-ladder gate (the other
        // `HasNoncopyableMember(protocolDecl)` call site) is the whole point.
        if (HasNoncopyableMember(protocolDecl))
            return true;

        var hasImplementableMembers = protocolDecl.Properties.Any(p => !p.IsStatic) ||
                                      protocolDecl.Methods.Any(m => !m.IsConstructor && m.MethodType != MethodType.Static) ||
                                      protocolDecl.Subscripts.Any(s => !s.IsStatic);
        var hasStaticRequirements = protocolDecl.Properties.Any(p => p.IsStatic) ||
                                    protocolDecl.Methods.Any(m => !m.IsConstructor && m.MethodType == MethodType.Static);
        bool hasNonTrivialInheritance = protocolDecl.InheritedProtocols.Any(inh =>
                inh.NameWithoutModule != "AnyObject" &&
                inh.NameWithoutModule != "Escapable" &&
                inh.NameWithoutModule != "Copyable" &&
                inh.NameWithoutModule != "Sendable" &&
                inh.NameWithoutModule != "SendableMetatype");

        // Empty marker protocols (no members, no inheritance) are allowed — they need
        // a trivial EveryProtocol conformance for existential container creation.
        if (!hasImplementableMembers && !hasStaticRequirements && !hasNonTrivialInheritance)
        {
            // Truly empty marker protocol — don't skip
            if (!protocolDecl.Properties.Any() && !protocolDecl.Methods.Any() && !protocolDecl.Subscripts.Any())
                return false;
            // Has members but none are implementable (all constructors/static) — skip
            return true;
        }

        if (protocolDecl.Methods.Any(m => !m.IsConstructor && m.MethodType == MethodType.Static))
            return true;

        // Bug #5: protocols whose only requirements are `static var` properties are skipped.
        // `fatalError()` stubs don't reliably satisfy Swift's type-checker for protocols whose
        // static-var requirements carry inherited-protocol or same-type constraints
        // (RealityFoundation.RealityCoordinateSpace, MaterialFunction). PreScan records the
        // skip so transitive `genericSig` constraint propagation through Pass 2 sees it too.
        if (!hasImplementableMembers && protocolDecl.Properties.Any(p => p.IsStatic))
            return true;

        return false;
    }

    /// <summary>
    /// Returns true if any protocol requirement is suppressed by an explicit
    /// <c>@_spi</c> annotation. Member emission validators drop such members as
    /// <see cref="SkipReason.ModuleInternal"/>, leaving the EveryProtocol extension
    /// without a witness for that requirement — Swift's type-checker rejects the
    /// conformance at compile time.
    ///
    /// Only <see cref="PropertyDecl.IsSpiProtected"/> / <see cref="MethodDecl.IsSpiProtected"/>
    /// are checked here. <c>IsModuleInternal</c> is intentionally NOT consulted: protocol
    /// requirements appear in the public swiftinterface body without a leading
    /// <c>public</c> keyword, which causes the parser's negative-space heuristic
    /// (<c>IsInternalFromPublicMemberNames</c>) to flag legitimate public requirements
    /// as internal. Treating those as "suppressed" would skip conformances that already
    /// emit working witnesses on baseline.
    /// </summary>
    private static bool HasSuppressedRequiredMember(ProtocolDecl protocolDecl)
    {
        foreach (var property in protocolDecl.Properties)
        {
            if (property.IsProtocolRequirement && property.IsSpiProtected)
                return true;
        }
        foreach (var method in protocolDecl.Methods)
        {
            if (method.IsProtocolRequirement && method.IsSpiProtected)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Emits all Swift code needed for a protocol's EveryProtocol conformance.
    /// </summary>
    /// <param name="globalEmittedSignatures">Optional set to track method signatures globally across protocols.
    /// When provided, methods that would conflict with already-emitted signatures are skipped.</param>
    public void EmitProtocolConformance(SwiftWriter writer, ProtocolDecl protocolDecl, HashSet<string>? globalEmittedSignatures)
    {
        EmitProtocolConformance(writer, protocolDecl, globalEmittedSignatures, null, null);
    }

    /// <summary>
    /// Emits all Swift code needed for a protocol's EveryProtocol conformance.
    /// </summary>
    public void EmitProtocolConformance(SwiftWriter writer, ProtocolDecl protocolDecl,
        HashSet<string>? globalEmittedSignatures, HashSet<string>? nonThrowingOverrides)
    {
        EmitProtocolConformance(writer, protocolDecl, globalEmittedSignatures, nonThrowingOverrides, null);
    }

    /// <summary>
    /// Emits all Swift code needed for a protocol's EveryProtocol conformance.
    /// </summary>
    public void EmitProtocolConformance(SwiftWriter writer, ProtocolDecl protocolDecl,
        HashSet<string>? globalEmittedSignatures, HashSet<string>? nonThrowingOverrides,
        IReadOnlyDictionary<string, PropertyEmissionPlan>? propertyPlans)
    {
        EmitProtocolConformance(writer, protocolDecl, globalEmittedSignatures, nonThrowingOverrides, propertyPlans, null);
    }

    /// <summary>
    /// Emits all Swift code needed for a protocol's EveryProtocol conformance.
    /// </summary>
    /// <param name="globalEmittedSignatures">Optional set to track method signatures globally across protocols.</param>
    /// <param name="nonThrowingOverrides">Signatures where non-throwing MUST be emitted because at least one
    /// protocol requires the method non-throwing. A non-throwing method satisfies both throwing and non-throwing
    /// protocol requirements, but a throwing method does NOT satisfy a non-throwing requirement.</param>
    /// <param name="propertyPlans">Optional per-property plan from <see cref="ComputePropertyEmissionPlans"/>.
    /// When non-null, each property is emitted only by its owning protocol; siblings emit empty extensions
    /// and conform via Swift's cross-extension witness resolution against the owner's declaration. The owner's
    /// body fans out across every sibling vtable so dispatch through any sibling existential finds the
    /// populated vtable.</param>
    /// <param name="subscriptPlans">Optional per-subscript plan from <see cref="ComputeSubscriptEmissionPlans"/>.
    /// Sibling-subscript counterpart of <paramref name="propertyPlans"/>: the owner of a shared subscript
    /// signature emits the body with fan-out across sibling vtables; siblings emit empty subscripts.</param>
    /// <param name="methodPlans">Optional per-method plan from <see cref="ComputeMethodEmissionPlans"/>.
    /// Same-signature-method counterpart of <paramref name="subscriptPlans"/>: the owner of a shared
    /// method signature emits the body with fan-out across sibling vtables; siblings emit empty
    /// extensions. When null, falls back to the legacy first-seen-wins dedup via
    /// <paramref name="globalEmittedSignatures"/>.</param>
    public void EmitProtocolConformance(SwiftWriter writer, ProtocolDecl protocolDecl,
        HashSet<string>? globalEmittedSignatures, HashSet<string>? nonThrowingOverrides,
        IReadOnlyDictionary<string, PropertyEmissionPlan>? propertyPlans,
        IReadOnlyDictionary<(string ProtoQName, string SubscriptKey), SubscriptEmissionPlan>? subscriptPlans,
        IReadOnlyDictionary<(string ProtoQName, string CarrierAndSignature), MethodEmissionPlan>? methodPlans = null)
    {
        // Reset per-protocol routing state — the NSObjectProtocol-only gate below sets
        // _useObjCBase=true only for the protocols that need EveryObjCProtocol; the
        // class-superclass gate further down sets _useEntityBase=true only for the
        // Entity-rooted protocols (Failure B).
        _useObjCBase = false;
        _useEntityBase = false;

        // Helper to record a skip decision and track the protocol for genericSig constraint checks
        void RecordSkip(string reason)
        {
            _skippedProtocols.Add(protocolDecl.Name);
            if (protocolDecl.SwiftTypeName != null)
                _skippedProtocols.Add(protocolDecl.SwiftTypeName.ModuleQualifiedName);
            // Key on the module-qualified name so a dependency protocol sharing a simple
            // name with a local one records its own decision without collision.
            _emissionContext?.RecordConformanceDecision(protocolDecl.SwiftTypeName?.ModuleQualifiedName ?? protocolDecl.Name, false, reason);
        }

        // Skip protocols with Self requirements - these require special handling
        // that can't be done with simple type erasure to Any
        if (protocolDecl.HasSelfRequirement)
        {
            _logger.LogDebug($"Skipping EveryProtocol conformance for {protocolDecl.Name}: has Self requirement");
            RecordSkip("HasSelfRequirement");
            return;
        }

        // Skip protocols with requirements that failed ABI parsing (e.g., methods with
        // `some` parameters cause GenericSignatureParser count mismatch). The emitter
        // cannot generate stubs for requirements it doesn't know about.
        if (protocolDecl.HasMissingRequirements)
        {
            _logger.LogDebug($"Skipping EveryProtocol conformance for {protocolDecl.Name}: has requirements that failed ABI parsing");
            RecordSkip("MissingRequirements");
            return;
        }

        // Skip protocols with @convention(c) or @convention(block) closure parameters.
        // ABI JSON doesn't encode calling conventions on TypeFunc nodes, so the closure
        // stub would emit @escaping instead of @convention(c), causing a type mismatch.
        if (protocolDecl.HasConventionCClosureParameters)
        {
            _logger.LogDebug($"Skipping EveryProtocol conformance for {protocolDecl.Name}: has @convention(c)/@convention(block) closure parameters");
            RecordSkip("ConventionCClosureParameters");
            return;
        }

        // Skip protocols with required-but-suppressed members. Mirrors the WillSkipConformance
        // pre-scan so the skip reason is recorded for reporting and the proxy class is
        // suppressed via _skippedProtocols. See HasSuppressedRequiredMember.
        if (HasSuppressedRequiredMember(protocolDecl))
        {
            _logger.LogDebug($"Skipping EveryProtocol conformance for {protocolDecl.Name}: required member suppressed by parser-time validation");
            RecordSkip("RequiredMemberSuppressed");
            return;
        }

        // Skip protocols whose swiftinterface body declares an `__`-prefixed requirement
        // that swift-api-digester strips from the ABI JSON and that no same-protocol extension
        // satisfies. See WillSkipConformance for the rationale.
        if (protocolDecl.HasUnsatisfiedHiddenRequirements)
        {
            _logger.LogDebug($"Skipping EveryProtocol conformance for {protocolDecl.Name}: swiftinterface declares an __-prefixed requirement stripped from ABI JSON with no extension default");
            RecordSkip("UnsatisfiedHiddenRequirements");
            return;
        }

        // Skip protocols where at least one required method's `Tq` method-descriptor symbol
        // is absent from the framework's TBD on this slice. The emitted EveryProtocol
        // witness table would reference an unresolved descriptor and fail to link
        // (observed on Mac Catalyst for LiveCommunicationKit.ConversationManagerDelegate's
        // didActivate / didDeactivate). See WillSkipConformance for the rationale.
        if (protocolDecl.HasMissingTbdMethodDescriptors)
        {
            _logger.LogDebug($"Skipping EveryProtocol conformance for {protocolDecl.Name}: required method has no Tq method descriptor in framework TBD");
            RecordSkip("MissingTbdMethodDescriptors");
            return;
        }

        // Skip protocols whose subscript signatures bind a generic param at subscript scope
        // and reference its associated type (e.g. MeshBufferContainer's
        // `subscript<S: MeshBufferSemantic>(_: S) -> MeshBuffer<S.Element>?`). The Self-typed
        // stub path substitutes Self/τ_0_* only, leaving the subscript-bound base unsatisfied.
        if (protocolDecl.Subscripts.Any(s => !s.IsStatic && HasSubscriptLevelGenericDependentMember(s)))
        {
            _logger.LogDebug($"Skipping EveryProtocol conformance for {protocolDecl.Name}: subscript binds a generic param whose associated type EveryProtocol cannot witness");
            RecordSkip("SubscriptLevelGenericDependentMember");
            return;
        }

        // Self-typed members (τ_0_*) and method-level generic methods (τ_1_*) get
        // fatalError() stubs in the extension — they can't be dispatched through the vtable.
        // Non-Self, non-generic members get normal vtable dispatch. This allows protocols
        // with a mix of dispatchable and non-dispatchable members to emit partial conformances.

        // Skip protocols that require NSObjectProtocol identity semantics.
        // Pure AnyObject (class-bound) protocols are allowed since EveryProtocol is a class.
        // Only NSObjectProtocol requires NSObject methods (isEqual:, hash, description).
        //
        // Protocols whose only ObjC-rooted requirement is NSObjectProtocol itself
        // (no NSCoding / NSSecureCoding / NSCopying / NSMutableCopying) are routed
        // through the NSObject-rooted EveryObjCProtocol helper class instead of being
        // skipped. NSObject's built-in isEqual:/hash/description satisfy the
        // NSObjectProtocol requirement automatically, and the rest of the protocol
        // body emits via the same vtable-callback pattern as EveryProtocol.
        if (IsClassBoundProtocol(protocolDecl, _allProtocols))
        {
            if (IsNSObjectProtocolOnly(protocolDecl, _allProtocols))
            {
                _useObjCBase = true;
                // Module-qualified key (see witness-getter marker) — avoids a same-simple-name
                // cross-module collision flipping the wrong proxy's carrier class.
                _emissionContext?.MarkObjCBase(protocolDecl.SwiftTypeName?.ModuleQualifiedName ?? protocolDecl.Name);
            }
            else
            {
                _logger.LogDebug($"Skipping EveryProtocol conformance for {protocolDecl.Name}: requires NSObjectProtocol identity semantics");
                RecordSkip("NSObjectProtocolRequired");
                return;
            }
        }

        // Skip protocols that name a concrete class in their inheritance list
        // (e.g. RealityKit.EntityGestureRecognizer : UIKit.UIGestureRecognizer).
        // Such a declaration constrains Self to be a subclass of that class —
        // EveryProtocol is a plain Swift class and inherits no UIKit / AppKit
        // / Foundation classes, so the conformance cannot type-check.
        //
        // Failure B exception: protocols whose only class-superclass requirement is
        // RealityFoundation.Entity (e.g. HasAnchoring) route through the Entity-
        // rooted EveryEntityProtocol helper class instead of skipping. The
        // pre-scan in EmitEveryProtocolClass already recorded MarkEntityBase /
        // ensured the class was emitted; here we flip the per-protocol routing
        // flag so BaseClassName returns "EveryEntityProtocol" through the rest
        // of this emission.
        if (HasClassSuperclassRequirement(protocolDecl, _typeDatabase, _allProtocols))
        {
            if (IsEntityRootedProtocol(protocolDecl, _typeDatabase, _allProtocols))
            {
                _useEntityBase = true;
                // Module-qualified key, in lockstep with the pre-scan MarkEntityBase above.
                _emissionContext?.MarkEntityBase(protocolDecl.SwiftTypeName?.ModuleQualifiedName ?? protocolDecl.Name);
            }
            else
            {
                _logger.LogDebug($"Skipping EveryProtocol conformance for {protocolDecl.Name}: requires class superclass EveryProtocol cannot inherit");
                RecordSkip("ClassSuperclassRequired");
                return;
            }
        }

        // Skip protocols whose EveryProtocol conformance would emit on a different umbrella
        // carrier (EveryObjCProtocol / EveryEntityProtocol / EveryProtocol) than a
        // transitively-inherited module-local protocol. The child's `extension {carrier}: Child`
        // would be left without a witness for the parent's requirements — Swift rejects it with
        // "type '{carrier}' does not conform to protocol 'Parent'". Cross-extension witness
        // resolution never bridges carriers, so suppress fail-closed rather than emit a wrapper
        // that fails to compile. Mirrors the WillSkipConformance pre-scan gate.
        if (HasCrossCarrierInheritedRequirement(protocolDecl))
        {
            _logger.LogDebug($"Skipping EveryProtocol conformance for {protocolDecl.Name}: inherited protocol routes to a different umbrella carrier (cross-carrier witness split)");
            RecordSkip("CrossCarrierInheritedRequirement");
            return;
        }

        // Skip protocols that inherit a conformance EveryProtocol can't satisfy — a known ObjC
        // module's protocol, or a same-module protocol that gets no witness of its own. Reads the
        // same predicate the pre-scan fixpoint uses, so the plan and the emission agree by
        // construction rather than by two gates being kept in step by hand.
        if (HasUnavailableInheritedConformance(protocolDecl))
        {
            _logger.LogDebug($"Skipping EveryProtocol conformance for {protocolDecl.Name}: inherits a protocol EveryProtocol cannot witness");
            RecordSkip(EveryProtocolSkipCause.DroppedInheritsUnsatisfiable);
            return;
        }

        // Skip CaseIterable — requires compiler-synthesized `allCases` static property
        // that EveryProtocol can't provide. Checked transitively.
        if (InheritsCaseIterable(protocolDecl))
        {
            _logger.LogDebug($"Skipping EveryProtocol conformance for {protocolDecl.Name}: CaseIterable requires compiler synthesis");
            RecordSkip("CaseIterable");
            return;
        }

        // Skip protocols that inherit from protocols with associated types.
        // EveryProtocol can't provide concrete associated types for inherited PATs.
        if (ModuleHandler.InheritsProtocolWithAssociatedTypes(protocolDecl))
        {
            _logger.LogDebug($"Skipping EveryProtocol conformance for {protocolDecl.Name}: inherits protocol with associated types");
            RecordSkip("InheritedAssociatedTypes");
            return;
        }

        // Skip protocols that inherit from stdlib protocols with requirements
        // EveryProtocol can't satisfy (CustomStringConvertible, CodingKey, etc.).
        if (InheritsUnsatisfiedStdlibProtocol(protocolDecl))
        {
            _logger.LogDebug($"Skipping EveryProtocol conformance for {protocolDecl.Name}: inherits unsatisfied stdlib protocol");
            RecordSkip("UnsatisfiedStdlibProtocol");
            return;
        }

        // Skip protocols with constructor requirements — EveryProtocol can't provide init methods
        // via the vtable callback pattern. The conformance would be incomplete (missing inits).
        if (protocolDecl.Methods.Any(m => m.IsConstructor))
        {
            _logger.LogDebug($"Skipping EveryProtocol conformance for {protocolDecl.Name}: has constructor requirements");
            RecordSkip("ConstructorRequirements");
            return;
        }

        // Skip protocols whose method signatures contain noncopyable parameters or return types.
        // The EveryProtocol trampoline forwards into the C# vtable via `inout` pointers, which
        // requires copying the value into a local var. Noncopyable types (~Copyable) cannot be
        // copied, so the generated trampoline fails to type-check ("copy of noncopyable typed
        // value"). Forwarding noncopyable values across the C# boundary needs a richer protocol
        // — for now, skip the conformance so the C# proxy class is suppressed.
        if (HasNoncopyableMember(protocolDecl))
        {
            _logger.LogDebug($"Skipping EveryProtocol conformance for {protocolDecl.Name}: has noncopyable parameter or return type");
            RecordSkip("NoncopyableParamOrReturn");
            return;
        }

        // Check for implementable instance members.
        // Static members are not part of the witness table, so we only count non-static members.
        var hasImplementableMembers = protocolDecl.Properties.Any(p => !p.IsStatic) ||
                                      protocolDecl.Methods.Any(m => !m.IsConstructor && m.MethodType != MethodType.Static) ||
                                      protocolDecl.Subscripts.Any(s => !s.IsStatic);

        // Check if this protocol has static requirements that need stub implementations
        var hasStaticRequirements = protocolDecl.Properties.Any(p => p.IsStatic) ||
                                    protocolDecl.Methods.Any(m => !m.IsConstructor && m.MethodType == MethodType.Static);

        // Composition/marker protocols (no own instance members) still need EveryProtocol
        // conformances so C# proxy classes can create existential containers. They are allowed
        // if they have static requirements OR inherit from non-trivial protocols.
        bool hasNonTrivialInheritance = protocolDecl.InheritedProtocols.Any(inh =>
                inh.NameWithoutModule != "AnyObject" &&
                inh.NameWithoutModule != "Escapable" &&
                inh.NameWithoutModule != "Copyable" &&
                inh.NameWithoutModule != "Sendable" &&
                inh.NameWithoutModule != "SendableMetatype");

        if (!hasImplementableMembers && !hasStaticRequirements && !hasNonTrivialInheritance)
        {
            // Empty marker protocols (no members at all) need a trivial conformance
            // for existential container creation. Let them through to the else branch below.
            bool isEmptyMarker = !protocolDecl.Properties.Any() && !protocolDecl.Methods.Any() && !protocolDecl.Subscripts.Any();
            if (!isEmptyMarker)
            {
                _logger.LogDebug($"Skipping EveryProtocol conformance for {protocolDecl.Name}: no implementable instance members and no static requirements");
                RecordSkip("NoImplementableMembers");
                return;
            }
        }

        // Skip protocols with static method requirements — static method stubs can't
        // render correct Swift signatures (parameter labels, types, return type).
        // Static properties work with fatalError() but methods need full signatures.
        var hasStaticMethodRequirements = protocolDecl.Methods.Any(m => !m.IsConstructor && m.MethodType == MethodType.Static);
        if (hasStaticMethodRequirements)
        {
            _logger.LogDebug($"Skipping EveryProtocol conformance for {protocolDecl.Name}: has static method requirements (can't generate correct stub signatures)");
            RecordSkip("StaticMethodRequirements");
            return;
        }

        // Note: MethodTypeConflict pre-scan was removed. Methods with the same label signature
        // but different parameter types are valid Swift overloads. The method dedup in
        // EmitProtocolExtension now uses full signatures (name + types) instead of label-only,
        // so methods like validate(input: String) and validate(input: Int32) from different
        // protocols coexist correctly on EveryProtocol.

        // The Entity-rooted carrier subclasses RealityFoundation.Entity, which already
        // conforms to a fixed set of base capabilities (HasTransform / HasHierarchy /
        // HasSynchronization, …). EveryEntityProtocol inherits those conformances, so
        // re-declaring one via `extension EveryEntityProtocol: P` is a redundant-conformance
        // error. The subclass-only protocols (HasCollision / HasModel / HasPhysics / …) add
        // requirements Entity does not satisfy, so they are NOT in this set and still emit
        // their full vtable-backed extension below.
        bool entityInheritsConformance = _useEntityBase && EntityBaseConformsTo(protocolDecl);

        if (entityInheritsConformance)
        {
            // Emit no extension and no vtable machinery: the inherited conformance already
            // satisfies the witness-table getter (`var proto: any P = instance`), and the C#
            // proxy reads a returned existential through the real object's witness table
            // (forward path). Reverse-dispatch through a pure-C# implementer is meaningless
            // for a base-Entity capability — no real Entity backing means no valid witnesses —
            // so we deliberately skip MarkSetVtableEmitted, which makes ProtocolProxyEmitter
            // emit a forward-only proxy with no SetXxx_vtable reference (see the MusicKit note
            // in the hasImplementableMembers branch). RecordConformanceDecision(true) below
            // still emits the C# proxy class.
            _logger.LogDebug($"EveryEntityProtocol inherits conformance to {protocolDecl.Name} from Entity; skipping redundant extension, emitting forward-only witness getter");
        }
        else if (hasImplementableMembers)
        {
            // Dispatchable closure-property getter and closure-returning method materialise
            // a Swift closure from (fnPtr, ctx) by wrapping the context in
            // `_sbWrapClosureContext`'s Swift-ARC owner-token box. The helper is a top-level
            // fileprivate declaration, so emit it BEFORE the protocol extension to keep it at
            // file scope.
            {
                var closureHandlerForHelper = new ClosureHandler(_typeDatabase);
                bool hasDispatchableClosureProp = protocolDecl.Properties
                    .Any(p => IsDispatchableClosureProperty(p, closureHandlerForHelper));
                bool hasDispatchableClosureReturningMethod = protocolDecl.Methods
                    .Any(m => IsDispatchableClosureReturningMethod(m, closureHandlerForHelper));
                if (hasDispatchableClosureProp || hasDispatchableClosureReturningMethod)
                    ClosureContextHelperEmitter.EmitIfNeeded(writer, _emissionContext);
            }
            EmitProtocolVtableStruct(writer, protocolDecl);
            EmitProtocolExtension(writer, protocolDecl, globalEmittedSignatures, nonThrowingOverrides, propertyPlans, subscriptPlans, methodPlans);
            EmitSetVtableFunction(writer, protocolDecl);
            // Per-shape @_cdecl invoke thunks for dispatchable closure params. C# proxy
            // receivers wrap the (fnPtr, ctx) pair into a managed Action whose
            // invocation calls back into Swift via a Cdecl P/Invoke — avoiding the
            // delegate* unmanaged[Swift] indirect call that Mono JIT and NativeAOT can't
            // synthesize. The thunk reconstructs the closure from typed memory + invokes it.
            EmitProtocolClosureInvokeThunks(writer, protocolDecl);
            // Symmetric signal to ProtocolProxyEmitter: the C# proxy's InitializeVtable() may
            // reference the SetXxx_vtable PInvoke now that the Swift trampoline is in place.
            // Without this signal the proxy emitter would assume every protocol got a vtable
            // setter and produce EntryPointNotFoundException-throwing static constructors
            // for protocols that only have a Swift→C# wrap path (e.g. marker conformances).
            // Module-qualified key (matching the witness-getter marker and ProtocolProxyEmitter's
            // read) so a dependency protocol sharing a simple name cannot mis-gate the local
            // proxy into emitting a dangling Set{Name}_vtable P/Invoke.
            _emissionContext?.MarkSetVtableEmitted(protocolDecl.SwiftTypeName?.ModuleQualifiedName ?? protocolDecl.Name);
        }
        else
        {
            // Skip protocols whose only requirements are static properties.
            // Earlier emission attempted to satisfy them with `fatalError()` stub bodies,
            // but Swift's type-checker rejects the conformance: the static property
            // declaration's stated type (e.g. `static var scene: SceneRealityCoordinateSpace`)
            // must be satisfied by a witness whose type matches, and Apple's framework
            // protocols (RealityCoordinateSpace, MaterialFunction, etc.) commonly bake in
            // additional same-type / inherited-protocol constraints that EveryProtocol
            // cannot satisfy via a Never-returning stub. Skipping the conformance lets
            // ProtocolHandler suppress the C# proxy class via the existing
            // `EveryProtocolConformanceSkipped` propagation path.
            //
            // The `!hasImplementableMembers` clause is redundant here (we're already in
            // the else branch) but mirrors `WillSkipConformance` exactly so a future
            // refactor that flattens the if/else can't accidentally fire this gate when
            // the protocol has dispatchable instance members.
            if (!hasImplementableMembers && protocolDecl.Properties.Any(p => p.IsStatic))
            {
                _logger.LogDebug($"Skipping EveryProtocol conformance for {protocolDecl.Name}: has static property requirements (fatalError stubs don't reliably satisfy Swift type-checker for protocols with constrained static-var requirements)");
                RecordSkip("StaticPropertyRequirements");
                return;
            }

            // Composition / empty marker protocol: emit a trivial empty conformance so
            // C# can construct existential containers via the witness-table getter below.
            var protocolName = protocolDecl.SwiftTypeName!.ModuleQualifiedName;
            writer.WriteLine($"// {BaseClassName} conformance to {protocolDecl.Name} (composition/marker protocol)");
            var staticAvailAnnotations = WrapperEmitterHelpers.MergeAvailabilityFromAncestors(
                protocolDecl.AvailabilityAnnotations, protocolDecl.ParentDecl);
            // Anchor the symbol-less composition conformance to the protocol it names — led ahead of the
            // availability, after the comment so the strip's backward scan reaches it. Inert while the
            // block is preserved; if an Entity/ObjC-base variant is stripped (Pattern 3), the anchor
            // goes with it.
            OriginAnchorEmitter.Write(writer, FragmentOwners.ForDeclWrapper(protocolDecl).Artifact);
            WrapperEmitterHelpers.EmitSwiftAvailability(writer, staticAvailAnnotations);
            writer.WriteLine($"extension {BaseClassName}: {protocolName} {{");
            writer.WriteLine("}");
            writer.WriteLine();
        }
        // The witness-table getter is symbol-named via `@_cdecl`
        // (`Get_EveryProtocol_{Name}_WitnessTable`) without the
        // source-module prefix the vtable setter carries. For cross-module
        // parents the dependency module's wrapper already emits this symbol
        // for the same protocol; re-emitting it here would create a
        // duplicate `@_cdecl` symbol and the consumer's P/Invoke would
        // resolve to whichever dylib dyld saw first. The consumer-side .cs
        // for cross-module parents reaches the impl through the covariant
        // `IProtocolProxyImpl<TInterface>` lookup, never through the local
        // wrapper's getter — so suppressing it here drops dead emission and
        // closes the symbol collision.
        var sourceModule = protocolDecl.ModuleDecl?.Name;
        if (string.IsNullOrEmpty(sourceModule) || sourceModule == _moduleName)
        {
            EmitWitnessTableGetter(writer, protocolDecl);
            // Record that THIS wrapper exported the getter so ProtocolProxyEmitter can gate the
            // matching C# P/Invoke. Cross-module parents (and skipped class-superclass conformances
            // that return before reaching here) do not export it, so their proxies must fail the
            // CALLBACK direction clean instead of declaring a dangling EntryPoint. Key on the
            // module-qualified name: a dependency protocol that shares a simple name with a local
            // one must not flip the local getter's mark onto the cross-module proxy.
            _emissionContext?.MarkWitnessTableGetterEmitted(protocolDecl.SwiftTypeName!.ModuleQualifiedName);
        }
        _emissionContext?.RecordConformanceDecision(protocolDecl.SwiftTypeName?.ModuleQualifiedName ?? protocolDecl.Name, true, null);
    }

    #region Private Helper Methods

    private void EmitPropertyVtableFields(SwiftWriter writer, PropertyDecl property, ProtocolDecl protocolDecl, HashSet<string> emittedFields)
    {
        var hasGetter = property.Accessors.OfType<GetAccessorDecl>().Any();
        var hasSetter = property.Accessors.OfType<SetAccessorDecl>().Any();

        if (hasGetter)
        {
            var fieldName = $"func_{property.Name}_get";
            if (emittedFields.Add(fieldName))
            {
                var funcType = $"(@convention(c)(OpaquePointer?, UnsafeRawPointer) -> UnsafeRawPointer)?";
                writer.WriteLine($"var {fieldName}: {funcType}");
            }
        }

        if (hasSetter)
        {
            var fieldName = $"func_{property.Name}_set";
            if (emittedFields.Add(fieldName))
            {
                var funcType = $"(@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer) -> Void)?";
                writer.WriteLine($"var {fieldName}: {funcType}");
            }
        }
    }

    private void EmitSubscriptVtableFields(SwiftWriter writer, SubscriptDecl subscript, ProtocolDecl protocolDecl, int index, HashSet<string> emittedFields)
    {
        // Build parameter types: OpaquePointer? (vtable handle), UnsafeRawPointer (self), then index params
        if (subscript.HasGetter)
        {
            var fieldName = $"func_subscript_{index}_get";
            if (emittedFields.Add(fieldName))
            {
                var paramCount = subscript.IndexParameters.Count;
                var paramList = "OpaquePointer?, UnsafeRawPointer" + string.Concat(Enumerable.Repeat(", UnsafeRawPointer", paramCount));
                var funcType = $"(@convention(c)({paramList}) -> UnsafeRawPointer)?";
                writer.WriteLine($"var {fieldName}: {funcType}");
            }
        }

        if (subscript.HasSetter)
        {
            var fieldName = $"func_subscript_{index}_set";
            if (emittedFields.Add(fieldName))
            {
                var paramCount = subscript.IndexParameters.Count;
                // For setter: vtable handle, self, newValue, then index params
                var paramList = "OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer" + string.Concat(Enumerable.Repeat(", UnsafeRawPointer", paramCount));
                var funcType = $"(@convention(c)({paramList}) -> Void)?";
                writer.WriteLine($"var {fieldName}: {funcType}");
            }
        }
    }

    private void EmitMethodVtableField(SwiftWriter writer, MethodDecl method, ProtocolDecl protocolDecl, int index, int expectedWidth, HashSet<string> emittedFields, ClosureHandler closureHandler)
    {
        var fieldName = GetMethodVtableFieldName(method, index);
        if (!emittedFields.Add(fieldName))
            return;

        // Build function pointer type
        // Parameters: OpaquePointer? (vtable handle), UnsafeRawPointer (self), then method params.
        // Dispatchable closure params expand into TWO pointer slots (fnPtr + ctx) — see
        // IsDispatchableClosureShape. Non-optional closures use `UnsafeRawPointer`; `Optional<Closure>`
        // uses `UnsafeRawPointer?` so nil round-trips as `0` to the C# trampoline.
        // Async closure params share the 2-pointer-slot layout — identical Swift-side
        // extraction, async-specific bridging on the C# side.
        //
        // Param skip + per-param slot count MUST mirror VtableLayout.GetWidth: debug and empty-tuple
        // params contribute no ABI slot, and each remaining param widens by CountVtableSlots (1, or 2
        // for a dispatchable/async closure). The trailing parity check pins this field's slot count to
        // the layout oracle's width, so a hand-count drift here can never silently shrink the Swift
        // struct below its C# mirror (the slot-corruption that only SIGSEGVs on the NativeAOT device).
        var slotTypes = new List<string> { "OpaquePointer?", "UnsafeRawPointer" };
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            if (DefaultParameterOverloadEmitter.IsDebugParameter(method.CSSignature[i]) || method.CSSignature[i].SwiftTypeSpec.IsEmptyTuple)
                continue;
            var p = method.CSSignature[i].SwiftTypeSpec;
            if (TryGetDispatchableClosureParam(p, closureHandler, out _, out var isOpt))
            {
                var slotType = isOpt ? "UnsafeRawPointer?" : "UnsafeRawPointer";
                slotTypes.Add(slotType);
                slotTypes.Add(slotType);
            }
            else if (IsDispatchableAsyncClosureParam(p, closureHandler, out _))
            {
                slotTypes.Add("UnsafeRawPointer");
                slotTypes.Add("UnsafeRawPointer");
            }
            else
            {
                slotTypes.Add("UnsafeRawPointer");
            }
        }

        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var hasReturn = returnType != null && !returnType.IsEmptyTuple;

        // Real-async reverse-dispatch witness (S13 Pillar C): the slot is a Start-thunk, not a
        // value-returning call. It carries THREE extra trailing pointers — the Swift continuation box,
        // and the success/error @_cdecl resume function pointers — and returns Void (the result and any
        // error flow back asynchronously through the box, never as the slot's return). Width here MUST
        // match VtableLayoutBuilder.GetWidth (+3) and the C# local delegate field, both keyed on the
        // SAME EmitsRealAsyncWitness verdict, so the @convention(c) ABI cannot drift.
        bool realAsync = EmitsRealAsyncWitness(method);
        if (realAsync)
        {
            slotTypes.Add("UnsafeRawPointer"); // continuation box
            slotTypes.Add("UnsafeRawPointer"); // success FP
            slotTypes.Add("UnsafeRawPointer"); // error FP
        }

        // Fail closed on any drift from the layout oracle: the two fixed leading slots (vtable handle +
        // self) are not part of the method's ABI width, so everything after them must total exactly
        // expectedWidth (VtableLayout.GetWidth). A mismatch would shift every later field in the C#
        // mirror and corrupt reverse dispatch, so refuse to emit rather than ship a divergent struct.
        int producedWidth = slotTypes.Count - 2;
        if (producedWidth != expectedWidth)
            throw new InvalidOperationException(
                $"EveryProtocol vtable field '{fieldName}' produced {producedWidth} ABI slots but the layout oracle expects {expectedWidth}.");

        var paramList = string.Join(", ", slotTypes);

        var returnTypeStr = (hasReturn && !realAsync) ? "UnsafeRawPointer" : "Void";
        var funcType = $"(@convention(c)({paramList}) -> {returnTypeStr})?";

        writer.WriteLine($"var {fieldName}: {funcType}");
    }

    /// <summary>
    /// Emits a fatalError() stub for a protocol property that has a closure type.
    /// Satisfies the protocol conformance requirement without vtable dispatch.
    /// </summary>
    private void EmitClosurePropertyStub(SwiftWriter writer, PropertyDecl property)
    {
        var hasGetter = property.Accessors.OfType<GetAccessorDecl>().Any();
        var hasSetter = property.Accessors.OfType<SetAccessorDecl>().Any();
        var swiftTypeName = GetSwiftTypeNameForDeclaration(property.SwiftTypeSpec);

        writer.WriteLine($"public var {NameProvider.ParserNameToSwift(property)}: {swiftTypeName} {{");
        writer.Indent++;
        if (hasGetter)
        {
            writer.WriteLine($"get {{ fatalError(\"[SwiftBindings] EveryProtocol: closure property '{property.Name}' cannot be dispatched through vtable\") }}");
        }
        if (hasSetter)
        {
            writer.WriteLine($"set {{ fatalError(\"[SwiftBindings] EveryProtocol: closure property '{property.Name}' cannot be dispatched through vtable\") }}");
        }
        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    /// <summary>
    /// Emits a fatalError() stub for a protocol property that contains Self-typed (τ_0_*) references.
    /// Substitutes τ_0_0 with EveryProtocol (the conforming type) so Swift's type system is satisfied.
    /// </summary>
    private void EmitSelfTypedPropertyStub(SwiftWriter writer, PropertyDecl property)
    {
        var hasGetter = property.Accessors.OfType<GetAccessorDecl>().Any();
        var hasSetter = property.Accessors.OfType<SetAccessorDecl>().Any();
        var swiftTypeName = RenderTypeSpecWithSelfSubstitutionForDeclaration(property.SwiftTypeSpec);

        writer.WriteLine($"public var {NameProvider.ParserNameToSwift(property)}: {swiftTypeName} {{");
        writer.Indent++;
        if (hasGetter)
        {
            writer.WriteLine($"get {{ fatalError(\"[SwiftBindings] EveryProtocol: Self-typed property '{property.Name}' cannot be dispatched through vtable\") }}");
        }
        if (hasSetter)
        {
            writer.WriteLine($"set {{ fatalError(\"[SwiftBindings] EveryProtocol: Self-typed property '{property.Name}' cannot be dispatched through vtable\") }}");
        }
        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    /// <summary>
    /// Emits the dispatch body for a protocol property requirement on the EveryProtocol
    /// extension. When <paramref name="plan"/> describes a sibling group with more than
    /// one member, the body fans out across each sibling's vtable — checking each
    /// function-pointer field for non-nil and dispatching through whichever sibling the
    /// registered C# proxy populated. This is required because Swift's cross-extension
    /// witness resolution routes dispatch through a smaller-sibling existential into the
    /// owner's body; without fan-out the owner would force-unwrap its own nil pointer
    /// and SIGSEGV. See <see cref="PropertyEmissionPlan"/> for the resolution rules.
    /// </summary>
    private void EmitPropertyImplementation(SwiftWriter writer, PropertyDecl property,
        ProtocolDecl protocolDecl, string vtableInstanceName, PropertyEmissionPlan? plan = null,
        IReadOnlyList<AvailabilityAnnotation>? extensionAvailability = null)
    {
        var hasGetter = property.Accessors.OfType<GetAccessorDecl>().Any();
        var hasSetter = property.Accessors.OfType<SetAccessorDecl>().Any();

        var swiftTypeName = GetSwiftTypeNameForDeclaration(property.SwiftTypeSpec);
        var swiftTypeNameForMetatype = GetSwiftTypeNameForMetatype(property.SwiftTypeSpec);

        // Resolve the dispatch branches. A solo group (no siblings) keeps the original
        // single-branch shape via a one-entry list; a real sibling group fans out across
        // every sibling for the getter, and across the has-setter subset for the setter.
        IReadOnlyList<ProtocolDecl> getterBranches = plan?.GetterSiblings.Count > 1
            ? plan.GetterSiblings
            : new[] { protocolDecl };
        IReadOnlyList<ProtocolDecl> setterBranches = plan?.SetterSiblings.Count > 1
            ? plan.SetterSiblings
            : new[] { protocolDecl };
        // Safety net: when the sibling group contained ModuleHandler-filtered peers (e.g.
        // mixed-generic siblings) the owner body must use the nil-check fan-out shape even
        // for a single emittable branch. CEWR can still route the filtered protocol's
        // dispatch through the owner; without the nil-check the force-unwrap would SIGSEGV
        // when only the filtered side has been implemented C#-side.
        bool forceSafeFanOut = plan?.HasFilteredPeers == true;

        writer.WriteLine($"public var {NameProvider.ParserNameToSwift(property)}: {swiftTypeName} {{");
        writer.Indent++;

        if (hasGetter)
        {
            EmitPropertyGetterBody(writer, property, swiftTypeNameForMetatype, getterBranches, extensionAvailability, forceSafeFanOut);
        }

        if (hasSetter)
        {
            EmitPropertySetterBody(writer, property, setterBranches, extensionAvailability, forceSafeFanOut);
        }

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    /// <summary>
    /// Emits the getter body. When <paramref name="branches"/> has a single entry the body
    /// is the historic single-branch shape; for two or more entries the body fans out
    /// across every sibling vtable, picking the first one whose function pointer is
    /// non-nil. See <see cref="EmitPropertyImplementation"/> for the rationale.
    /// </summary>
    private void EmitPropertyGetterBody(SwiftWriter writer, PropertyDecl property,
        string swiftTypeNameForMetatype, IReadOnlyList<ProtocolDecl> branches,
        IReadOnlyList<AvailabilityAnnotation>? extensionAvailability = null,
        bool forceSafeFanOut = false)
    {
        bool isStringGetter = property.SwiftTypeSpec is NamedTypeSpec getterNts && getterNts.Name == "Swift.String";
        bool isObjCBridgeableGetter = !isStringGetter && IsObjCBridgeableParam(property.SwiftTypeSpec);
        bool isObjCBridgeableContainerGetter = !isStringGetter && !isObjCBridgeableGetter
            && property.SwiftTypeSpec != null
            && CdeclParamMapper.IsObjCBridgeableContainer(property.SwiftTypeSpec, _typeDatabase);

        writer.WriteLine("get {");
        writer.Indent++;

        // Single-branch fast path — keep the original shape so generated output stays
        // byte-identical for the (overwhelming) non-sibling case. forceSafeFanOut overrides
        // the fast path when the plan recorded filtered peers (see HasFilteredPeers).
        if (branches.Count == 1 && !forceSafeFanOut)
        {
            var soloVtable = GetVtableInstanceName(branches[0]);
            var soloProtoName = branches[0].SwiftTypeName.ModuleQualifiedName;
            writer.WriteLine($"var selfProto: {soloProtoName} = self");
            writer.WriteLine($"let resultPtr = {soloVtable}.func_{property.Name}_get!(");
            writer.WriteLine($"    {soloVtable}.csVTHandle, &selfProto)");
        }
        else
        {
            writer.WriteLine("let resultPtr: UnsafeRawPointer");
            // Box `self` as the OWNER's type for every branch. For PROPERTIES branches[0] IS the
            // owner (ComputePropertyEmissionPlans Prepends(owner)), unlike the method fan-out whose
            // Siblings list is sync-first. The box type is behaviorally IMMATERIAL either way: the
            // C# receiver reads only word 0 (the class reference) and never the witness table, and
            // EveryProtocol unconditionally conforms to every sibling so a sibling box type-checks
            // too. Owner-box is the robust clarity invariant, not a correctness gate. See
            // EmitMethodFanOutBody.
            var ownerProtoName = branches[0].SwiftTypeName.ModuleQualifiedName;
            for (int i = 0; i < branches.Count; i++)
            {
                var branch = branches[i];
                var branchVtable = GetVtableInstanceName(branch);
                var clause = i == 0 ? "if" : "else if";
                var guard = BuildBranchGuardPrefix(branch, extensionAvailability);
                writer.WriteLine($"{clause} {guard}let fn = {branchVtable}.func_{property.Name}_get {{");
                writer.Indent++;
                writer.WriteLine($"var selfProto: {ownerProtoName} = self");
                writer.WriteLine($"resultPtr = fn({branchVtable}.csVTHandle, &selfProto)");
                writer.Indent--;
                writer.Write("} ");
            }
            writer.WriteLine("else {");
            writer.Indent++;
            writer.WriteLine($"fatalError(\"[SwiftBindings] EveryProtocol: no sibling vtable populated for getter of '{property.Name}'\")");
            writer.Indent--;
            writer.WriteLine("}");
        }

        if (isStringGetter)
        {
            writer.WriteLines("""
                let slice = resultPtr.load(as: SBW_Utf8Slice.self)
                var str: Swift.String = ""
                if slice.len > 0 {
                    let buffer = UnsafeBufferPointer(start: slice.ptr, count: slice.len)
                    str = String(decoding: buffer, as: UTF8.self)
                }
                slice.ptr.deallocate()
                resultPtr.deallocate()
                return str
                """);
        }
        else if (isObjCBridgeableGetter || isObjCBridgeableContainerGetter)
        {
            // The C# receiver placed one ObjC object pointer in the return buffer at +1, whether the
            // return is a SCALAR ObjC-bridgeable value or a whole-CONTAINER bridge. The scalar receiver
            // returns Arc.UnknownObjectRetain(wrapper.Handle); the container receiver builds a fresh
            // NSSet/NSArray/NSDictionary and transfers the same retain — because in either case the
            // managed wrapper is often freshly allocated and has no guaranteed lifetime once the receiver
            // frame returns. Consume the transferred +1 with takeRetainedValue and let ARC free the
            // temporary ObjC object after `as!` bridges it into the native Swift value/container. Mirrors,
            // in reverse, the forward accessor's Unmanaged.passRetained(+1) / C# owns:true adoption.
            const string take = "takeRetainedValue";
            writer.WriteLines($$"""
                let resultObjPtr = resultPtr.load(as: UnsafeRawPointer.self)
                resultPtr.deallocate()
                return Unmanaged<AnyObject>.fromOpaque(resultObjPtr).{{take}}() as! {{swiftTypeNameForMetatype}}
                """);
        }
        else if (GetOptionalObjCBridgeableValueInnerName(property.SwiftTypeSpec) is string optBridgeableInner)
        {
            EmitOptionalObjCBridgeableValueReturn(writer, optBridgeableInner);
        }
        else
        {
            // Consume the C#-allocated result buffer: move() returns the value AND deinitializes
            // the buffer (transferring the MarshalToSwiftBuffer +1 into the return, no extra
            // retain), then deallocate frees the raw memory. The String / ObjC siblings above
            // both deallocate; the value path used to return `.pointee` and leak the buffer every
            // call.
            writer.WriteLine($"let __result = UnsafeMutableRawPointer(mutating: resultPtr).assumingMemoryBound(to: {swiftTypeNameForMetatype}.self).move()");
            writer.WriteLine("resultPtr.deallocate()");
            writer.WriteLine("return __result");
        }

        writer.Indent--;
        writer.WriteLine("}");
    }

    /// <summary>
    /// Emits the setter body. Setter fan-out only applies when more than one sibling
    /// carries a setter; the get-only-plus-get+set sibling case keeps the single-branch
    /// shape because only the owner could ever have its setter vtable populated.
    /// </summary>
    private void EmitPropertySetterBody(SwiftWriter writer, PropertyDecl property,
        IReadOnlyList<ProtocolDecl> branches,
        IReadOnlyList<AvailabilityAnnotation>? extensionAvailability = null,
        bool forceSafeFanOut = false)
    {
        bool isObjCBridgeableSetter = IsObjCBridgeableParam(property.SwiftTypeSpec);

        writer.WriteLine("set {");
        writer.Indent++;

        if (branches.Count == 1 && !forceSafeFanOut)
        {
            // Single-branch shape: keep the historical `vtable.func_X_set!(...)` force-unwrap
            // path so generated output stays byte-identical for the non-sibling case.
            var branch = branches[0];
            var branchVtable = GetVtableInstanceName(branch);
            EmitSetterCallSite(writer, property, branch,
                fnExpr: $"{branchVtable}.func_{property.Name}_set!",
                branchVtableExpr: $"{branchVtable}.csVTHandle",
                isObjCBridgeableSetter);
        }
        else
        {
            for (int i = 0; i < branches.Count; i++)
            {
                var branch = branches[i];
                var branchVtable = GetVtableInstanceName(branch);
                var clause = i == 0 ? "if" : "else if";
                var guard = BuildBranchGuardPrefix(branch, extensionAvailability);
                writer.WriteLine($"{clause} {guard}let fn = {branchVtable}.func_{property.Name}_set {{");
                writer.Indent++;
                EmitSetterCallSite(writer, property, branches[0],
                    fnExpr: "fn",
                    branchVtableExpr: $"{branchVtable}.csVTHandle",
                    isObjCBridgeableSetter);
                writer.Indent--;
                writer.Write("} ");
            }
            writer.WriteLine("else {");
            writer.Indent++;
            writer.WriteLine($"fatalError(\"[SwiftBindings] EveryProtocol: no sibling vtable populated for setter of '{property.Name}'\")");
            writer.Indent--;
            writer.WriteLine("}");
        }

        writer.Indent--;
        writer.WriteLine("}");
    }

    private void EmitSetterCallSite(SwiftWriter writer, PropertyDecl property,
        ProtocolDecl ownerProto, string fnExpr, string branchVtableExpr, bool isObjCBridgeableSetter)
    {
        // Box `self` as the OWNER's protocol type, not the dispatching branch's (for properties
        // branches[0]/ownerProto IS the owner — ComputePropertyEmissionPlans Prepends(owner)). The
        // box type is behaviorally IMMATERIAL to dispatch — the C# receiver reads only word 0 (the
        // class reference) of the existential, never the witness table, and EveryProtocol conforms
        // to every sibling so a sibling box type-checks too — but owner-box is the robust clarity
        // invariant. The actual branch's vtable is already encoded in fnExpr/branchVtableExpr.
        // See EmitMethodFanOutBody.
        var protoName = ownerProto.SwiftTypeName.ModuleQualifiedName;
        if (isObjCBridgeableSetter)
        {
            writer.WriteLines($$"""
                var selfProto: {{protoName}} = self
                let newValueNS = newValue as AnyObject
                var newValueRef = Unmanaged.passUnretained(newValueNS).toOpaque()
                {{fnExpr}}({{branchVtableExpr}}, &selfProto, &newValueRef)
                """);
        }
        else if (GetOptionalObjCBridgeableValueInnerName(property.SwiftTypeSpec) is not null)
        {
            // Optional ObjC-bridgeable VALUE setter (URL?): pass a single optional ObjC POINTER word
            // (nil = 0x0), borrowing (+0) the bridged NSObject — the write mirror of the reverse-RETURN
            // optional-bridgeable arm. `newValueNS` (Optional<AnyObject>) holds the bridged object alive
            // across the call; `newValueRef` (Optional<UnsafeMutableRawPointer>) is the one nil-optimized
            // word the C# receiver reads as a bare IntPtr. The plain-value `else` arm below would pass the
            // multi-word Optional<URL> bytes, which the receiver then misreads as one word → layout mismatch.
            writer.WriteLines($$"""
                var selfProto: {{protoName}} = self
                let newValueNS = newValue.map { $0 as AnyObject }
                var newValueRef = newValueNS.map { Unmanaged.passUnretained($0).toOpaque() }
                {{fnExpr}}({{branchVtableExpr}}, &selfProto, &newValueRef)
                """);
        }
        else if (RequiresExplicitValuePointer(property.SwiftTypeSpec))
        {
            // Array/String setter value: route through an explicitly-typed pointer so Swift's
            // implicit array/string-to-pointer conversion does not hand the C# receiver a
            // pointer to the element buffer / UTF-8 bytes. See RequiresExplicitValuePointer.
            var swiftType = GetSwiftTypeName(property.SwiftTypeSpec);
            writer.WriteLines($$"""
                var selfProto: {{protoName}} = self
                let newValuePtr = UnsafeMutablePointer<{{swiftType}}>.allocate(capacity: 1)
                newValuePtr.initialize(to: newValue)
                defer { newValuePtr.deinitialize(count: 1); newValuePtr.deallocate() }
                {{fnExpr}}({{branchVtableExpr}}, &selfProto, UnsafeRawPointer(newValuePtr))
                """);
        }
        else
        {
            writer.WriteLines($$"""
                var selfProto: {{protoName}} = self
                var newValueCopy = newValue
                {{fnExpr}}({{branchVtableExpr}}, &selfProto, &newValueCopy)
                """);
        }
    }

    /// <summary>
    /// Emits the dispatch body for a protocol subscript requirement on the EveryProtocol
    /// extension. When <paramref name="plan"/> describes a sibling group with more than
    /// one member, the body fans out across each sibling's vtable — checking each
    /// function-pointer field for non-nil and dispatching through whichever sibling the
    /// registered C# proxy populated. Mirrors <see cref="EmitPropertyImplementation"/>;
    /// see <see cref="SubscriptEmissionPlan"/> for the resolution rules.
    /// </summary>
    private void EmitSubscriptImplementation(SwiftWriter writer, SubscriptDecl subscript, ProtocolDecl protocolDecl, string vtableInstanceName, int index, SubscriptEmissionPlan? plan = null, IReadOnlyList<AvailabilityAnnotation>? extensionAvailability = null)
    {
        // Build parameter list. Swift subscripts default to NO external label when only one
        // name is written (`subscript(at: Int)` parses as external=_, internal=at — opposite of
        // `func foo(at: Int)`). Always emit the explicit `<external> <internal>:` form so an
        // external label declared on the protocol (`subscript(by index: Int)`) survives into
        // the EveryProtocol witness.
        var parameters = new List<string>();
        for (int i = 0; i < subscript.IndexParameters.Count; i++)
        {
            var param = subscript.IndexParameters[i];
            var paramTypeName = GetSwiftTypeNameForDeclaration(param.SwiftTypeSpec);
            var externalLabel = NameProvider.GetSubscriptExternalLabel(param);
            var internalName = $"arg{i}";
            parameters.Add($"{externalLabel} {internalName}: {paramTypeName}");
        }
        var parametersString = string.Join(", ", parameters);

        var returnTypeName = GetSwiftTypeNameForDeclaration(subscript.ReturnTypeSpec);
        var returnTypeNameForMetatype = GetSwiftTypeNameForMetatype(subscript.ReturnTypeSpec);

        // Resolve the dispatch branches. A solo group (no siblings) keeps the original
        // single-branch shape via a one-entry list; a real sibling group fans out across
        // every sibling for the getter, and across the has-setter subset for the setter.
        IReadOnlyList<(ProtocolDecl Proto, int Index)> getterBranches = plan?.GetterSiblings.Count > 1
            ? plan.GetterSiblings
            : new[] { (protocolDecl, index) };
        IReadOnlyList<(ProtocolDecl Proto, int Index)> setterBranches = plan?.SetterSiblings.Count > 1
            ? plan.SetterSiblings
            : new[] { (protocolDecl, index) };
        // Mirror of property safety net — see EmitPropertyImplementation.
        bool forceSafeFanOut = plan?.HasFilteredPeers == true;

        writer.WriteLine($"public subscript({parametersString}) -> {returnTypeName} {{");
        writer.Indent++;

        if (subscript.HasGetter)
        {
            EmitSubscriptGetterBody(writer, subscript, returnTypeNameForMetatype, getterBranches, extensionAvailability, forceSafeFanOut);
        }

        if (subscript.HasSetter)
        {
            EmitSubscriptSetterBody(writer, subscript, setterBranches, extensionAvailability, forceSafeFanOut);
        }

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    /// <summary>
    /// Emits the subscript getter body. When <paramref name="branches"/> has a single entry
    /// the body is the historic single-branch shape; for two or more entries the body fans
    /// out across every sibling vtable, picking the first one whose function pointer is
    /// non-nil. Each branch uses its own per-protocol subscript index so the
    /// <c>func_subscript_{Index}_get</c> field name matches the vtable struct emitted for
    /// that sibling protocol. See <see cref="EmitSubscriptImplementation"/> for the rationale.
    /// </summary>
    private void EmitSubscriptGetterBody(SwiftWriter writer, SubscriptDecl subscript,
        string returnTypeNameForMetatype, IReadOnlyList<(ProtocolDecl Proto, int Index)> branches,
        IReadOnlyList<AvailabilityAnnotation>? extensionAvailability = null,
        bool forceSafeFanOut = false)
    {
        var argRefs = BuildArgRefs(subscript.IndexParameters);
        // String/ObjC-bridge returns must mirror the property fan-out wrapper's decode shape
        // (see EmitPropertyImplementation around `isStringGetter`). The C# subscript receiver
        // returns a pointer to SBW_Utf8Slice for strings, not a Swift.String buffer, so the
        // wrapper must decode the slice rather than reinterpret-casting.
        bool isStringGetter = subscript.ReturnTypeSpec is NamedTypeSpec returnNts && returnNts.Name == "Swift.String";
        bool isObjCBridgeableGetter = !isStringGetter && IsObjCBridgeableParam(subscript.ReturnTypeSpec);
        bool isObjCBridgeableContainerGetter = !isStringGetter && !isObjCBridgeableGetter
            && subscript.ReturnTypeSpec != null
            && CdeclParamMapper.IsObjCBridgeableContainer(subscript.ReturnTypeSpec, _typeDatabase);

        writer.WriteLine("get {");
        writer.Indent++;

        if (branches.Count == 1 && !forceSafeFanOut)
        {
            var (soloProto, soloIndex) = branches[0];
            var soloVtable = GetVtableInstanceName(soloProto);
            var soloProtoName = soloProto.SwiftTypeName.ModuleQualifiedName;
            writer.WriteLine($"var selfProto: {soloProtoName} = self");
            EmitSubscriptArgCopies(writer, subscript.IndexParameters);
            writer.WriteLine($"let resultPtr = {soloVtable}.func_subscript_{soloIndex}_get!(");
            writer.WriteLine($"    {soloVtable}.csVTHandle, &selfProto{argRefs})");
        }
        else
        {
            EmitSubscriptArgCopies(writer, subscript.IndexParameters);
            writer.WriteLine("let resultPtr: UnsafeRawPointer");
            // Owner-typed box for every branch (branches[0] IS the owner — ComputeSubscriptEmissionPlans
            // Prepends(owner)). Box type is behaviorally immaterial; see EmitMethodFanOutBody.
            var ownerProtoName = branches[0].Proto.SwiftTypeName.ModuleQualifiedName;
            for (int i = 0; i < branches.Count; i++)
            {
                var (branchProto, branchIndex) = branches[i];
                var branchVtable = GetVtableInstanceName(branchProto);
                var clause = i == 0 ? "if" : "else if";
                var guard = BuildBranchGuardPrefix(branchProto, extensionAvailability);
                writer.WriteLine($"{clause} {guard}let fn = {branchVtable}.func_subscript_{branchIndex}_get {{");
                writer.Indent++;
                writer.WriteLine($"var selfProto: {ownerProtoName} = self");
                writer.WriteLine($"resultPtr = fn({branchVtable}.csVTHandle, &selfProto{argRefs})");
                writer.Indent--;
                writer.Write("} ");
            }
            writer.WriteLine("else {");
            writer.Indent++;
            writer.WriteLine($"fatalError(\"[SwiftBindings] EveryProtocol: no sibling vtable populated for getter of subscript\")");
            writer.Indent--;
            writer.WriteLine("}");
        }

        if (isStringGetter)
        {
            writer.WriteLines("""
                let slice = resultPtr.load(as: SBW_Utf8Slice.self)
                var str: Swift.String = ""
                if slice.len > 0 {
                    let buffer = UnsafeBufferPointer(start: slice.ptr, count: slice.len)
                    str = String(decoding: buffer, as: UTF8.self)
                }
                slice.ptr.deallocate()
                resultPtr.deallocate()
                return str
                """);
        }
        else if (isObjCBridgeableGetter || isObjCBridgeableContainerGetter)
        {
            // Scalar and whole-container ObjC returns both cross at +1 (takeRetainedValue);
            // see EmitPropertyGetterBody for the ownership rationale.
            const string take = "takeRetainedValue";
            writer.WriteLines($$"""
                let resultObjPtr = resultPtr.load(as: UnsafeRawPointer.self)
                resultPtr.deallocate()
                return Unmanaged<AnyObject>.fromOpaque(resultObjPtr).{{take}}() as! {{returnTypeNameForMetatype}}
                """);
        }
        else if (GetOptionalObjCBridgeableValueInnerName(subscript.ReturnTypeSpec) is string optBridgeableInner)
        {
            EmitOptionalObjCBridgeableValueReturn(writer, optBridgeableInner);
        }
        else
        {
            // Consume the C#-allocated result buffer (move() = value + deinitialize), then
            // deallocate — the String / ObjC siblings deallocate; the value path leaked it
            // every call.
            writer.WriteLine($"let __result = UnsafeMutableRawPointer(mutating: resultPtr).assumingMemoryBound(to: {returnTypeNameForMetatype}.self).move()");
            writer.WriteLine("resultPtr.deallocate()");
            writer.WriteLine("return __result");
        }

        writer.Indent--;
        writer.WriteLine("}");
    }

    /// <summary>
    /// Emits the subscript setter body. Setter fan-out only applies when more than one
    /// sibling carries a setter. Each branch uses its own per-protocol subscript index so
    /// the <c>func_subscript_{Index}_set</c> field name matches the vtable struct emitted
    /// for that sibling protocol.
    /// </summary>
    private void EmitSubscriptSetterBody(SwiftWriter writer, SubscriptDecl subscript,
        IReadOnlyList<(ProtocolDecl Proto, int Index)> branches,
        IReadOnlyList<AvailabilityAnnotation>? extensionAvailability = null,
        bool forceSafeFanOut = false)
    {
        var argRefs = BuildArgRefs(subscript.IndexParameters);

        writer.WriteLine("set {");
        writer.Indent++;

        if (branches.Count == 1 && !forceSafeFanOut)
        {
            var (soloProto, soloIndex) = branches[0];
            var soloVtable = GetVtableInstanceName(soloProto);
            var soloProtoName = soloProto.SwiftTypeName.ModuleQualifiedName;
            writer.WriteLine($"var selfProto: {soloProtoName} = self");
            var soloNewValueRef = EmitSubscriptSetterValueSetup(writer, subscript.ReturnTypeSpec);
            EmitSubscriptArgCopies(writer, subscript.IndexParameters);
            writer.WriteLine($"{soloVtable}.func_subscript_{soloIndex}_set!(");
            writer.WriteLine($"    {soloVtable}.csVTHandle, &selfProto, {soloNewValueRef}{argRefs})");
        }
        else
        {
            var newValueRef = EmitSubscriptSetterValueSetup(writer, subscript.ReturnTypeSpec);
            EmitSubscriptArgCopies(writer, subscript.IndexParameters);
            // Owner-typed box for every branch (branches[0] IS the owner — ComputeSubscriptEmissionPlans
            // Prepends(owner)). Box type is behaviorally immaterial; see EmitMethodFanOutBody.
            var ownerProtoName = branches[0].Proto.SwiftTypeName.ModuleQualifiedName;
            for (int i = 0; i < branches.Count; i++)
            {
                var (branchProto, branchIndex) = branches[i];
                var branchVtable = GetVtableInstanceName(branchProto);
                var clause = i == 0 ? "if" : "else if";
                var guard = BuildBranchGuardPrefix(branchProto, extensionAvailability);
                writer.WriteLine($"{clause} {guard}let fn = {branchVtable}.func_subscript_{branchIndex}_set {{");
                writer.Indent++;
                writer.WriteLine($"var selfProto: {ownerProtoName} = self");
                writer.WriteLine($"fn({branchVtable}.csVTHandle, &selfProto, {newValueRef}{argRefs})");
                writer.Indent--;
                writer.Write("} ");
            }
            writer.WriteLine("else {");
            writer.Indent++;
            writer.WriteLine($"fatalError(\"[SwiftBindings] EveryProtocol: no sibling vtable populated for setter of subscript\")");
            writer.Indent--;
            writer.WriteLine("}");
        }

        writer.Indent--;
        writer.WriteLine("}");
    }

    /// <summary>
    /// Writes one <c>var argNCopy = arg</c> line per subscript index parameter at the
    /// current writer indent. Distinct from <see cref="BuildArgumentPassList"/> which
    /// returns a single string with hard-coded inter-line indentation tailored to the
    /// historic heredoc emission path.
    /// </summary>
    private void EmitSubscriptArgCopies(SwiftWriter writer, IReadOnlyList<ArgumentDecl> parameters)
    {
        // Internal names are always `arg{i}` (see EmitSubscriptImplementation for why subscripts
        // get synthetic internal names rather than re-using the external label). Array/String
        // index params route through an explicitly-typed pointer (BuildArgRefs mirrors this
        // exact predicate to stay aligned). See RequiresExplicitValuePointer.
        for (int i = 0; i < parameters.Count; i++)
        {
            var internalName = $"arg{i}";
            if (RequiresExplicitValuePointer(parameters[i].SwiftTypeSpec))
            {
                foreach (var line in BuildValueStorageSetup(
                    $"{internalName}CopyPtr", GetSwiftTypeName(parameters[i].SwiftTypeSpec), internalName))
                    writer.WriteLine(line);
            }
            else
            {
                writer.WriteLine($"var {internalName}Copy = {internalName}");
            }
        }
    }

    /// <summary>
    /// True when taking <c>&amp;local</c> of a value of this Swift type triggers Swift's implicit
    /// array-to-pointer / string-to-pointer conversion in argument position — i.e. the static type
    /// is exactly <c>Array</c>, <c>ContiguousArray</c>, or <c>String</c>. Passing such an
    /// <c>&amp;x</c> to an <c>UnsafeRawPointer</c> vtable parameter hands the C# receiver a pointer
    /// to the element buffer / UTF-8 bytes instead of the value, corrupting every read (root cause
    /// of nested class-bound existential collection corruption in EveryProtocol reverse dispatch).
    /// Such values must be routed through an explicitly-typed <c>UnsafeMutablePointer</c> so the
    /// conversion does not fire. The conversion is a property of the static top-level type only —
    /// <c>Optional&lt;Array&gt;</c>, <c>Dictionary</c>, and structs containing arrays are unaffected.
    /// </summary>
    private static bool RequiresExplicitValuePointer(TypeSpec? spec)
        => spec is NamedTypeSpec nts &&
           (nts.Name == "Swift.Array" || nts.Name == "Swift.ContiguousArray" || nts.Name == "Swift.String");

    /// <summary>
    /// Emits the three setup lines that allocate, initialize, and (via <c>defer</c>) free a typed
    /// pointer holding a copy of <paramref name="sourceExpr"/>. The call site passes
    /// <c>UnsafeRawPointer(<paramref name="ptrName"/>)</c> — an explicit conversion that suppresses
    /// the implicit array/string-to-pointer conversion (see <see cref="RequiresExplicitValuePointer"/>).
    /// Mirrors the existing return-side idiom (<c>UnsafeMutablePointer&lt;T&gt;.allocate</c> +
    /// <c>initialize(to:)</c>) used by the witness getters.
    /// </summary>
    private static string[] BuildValueStorageSetup(string ptrName, string swiftType, string sourceExpr) => new[]
    {
        $"let {ptrName} = UnsafeMutablePointer<{swiftType}>.allocate(capacity: 1)",
        $"{ptrName}.initialize(to: {sourceExpr})",
        $"defer {{ {ptrName}.deinitialize(count: 1); {ptrName}.deallocate() }}",
    };

    /// <summary>
    /// Emits the <c>newValue</c> local setup for a subscript setter and returns the call-site
    /// reference expression. Array/String values route through an explicitly-typed pointer (see
    /// <see cref="RequiresExplicitValuePointer"/>); every other type keeps the historic
    /// <c>var newValueCopy = newValue</c> + <c>&amp;newValueCopy</c> shape.
    /// </summary>
    private string EmitSubscriptSetterValueSetup(SwiftWriter writer, TypeSpec valueType)
    {
        if (RequiresExplicitValuePointer(valueType))
        {
            foreach (var line in BuildValueStorageSetup("newValuePtr", GetSwiftTypeName(valueType), "newValue"))
                writer.WriteLine(line);
            return "UnsafeRawPointer(newValuePtr)";
        }
        writer.WriteLine("var newValueCopy = newValue");
        return "&newValueCopy";
    }

    // Subscript external-label resolution is centralized in NameProvider.GetSubscriptExternalLabel.

    /// <summary>
    /// Builds the per-branch <c>#available(...)</c> guard prefix for an <c>if</c>/<c>else if</c>
    /// condition in a sibling fan-out. Returns the guard expression with a trailing comma+space
    /// suitable for prepending before <c>let fn = ...</c>, or an empty string if the branch's
    /// availability is already satisfied by the enclosing extension's availability.
    ///
    /// Required when a sibling protocol carries stricter availability than the owner's enclosing
    /// extension — referencing the sibling's vtable type from the owner's body would otherwise
    /// fail Swift's availability check (e.g., MusicKit.AlbumFilter@iOS15.0 fan-out body referencing
    /// MusicKit.CuratorFilter_witness@iOS15.4 must guard the branch with <c>#available(iOS 15.4, *)</c>).
    /// </summary>
    private static string BuildBranchGuardPrefix(ProtocolDecl branch,
        IReadOnlyList<AvailabilityAnnotation>? extensionAvailability)
    {
        var branchAvail = WrapperEmitterHelpers.MergeAvailabilityFromAncestors(
            branch.AvailabilityAnnotations, branch.ParentDecl);
        var guard = WrapperEmitterHelpers.BuildBranchAvailabilityGuard(branchAvail, extensionAvailability);
        return string.IsNullOrEmpty(guard) ? string.Empty : guard + ", ";
    }

    /// <summary>
    /// Emits a stub implementation for a method with method-level generic parameters (τ_1_0+).
    /// Returns nil for Optional returns, fatalError for non-Optional. This satisfies the Swift
    /// protocol conformance without vtable dispatch (C# can't handle method-level generics).
    /// Uses the raw TypeSpec to preserve generic param references (GetSwiftTypeName resolves them to Any).
    /// </summary>
    /// <summary>
    /// Emits a fatalError() stub for a protocol method that has closure parameters/return.
    /// The method signature is correct (satisfying the protocol conformance), but the body
    /// crashes if called. The C# proxy already throws NotSupportedException for closure methods,
    /// so the fatalError() is a safety net only.
    /// </summary>
    private void EmitClosureMethodStub(SwiftWriter writer, MethodDecl method)
    {
        // Build generic param name mapping for method-level generics (τ_1_0 → _G0, etc.)
        // Closure methods can also have method-level generics — we need both in the stub.
        var genericNameMap = new Dictionary<string, string>();
        int genericIdx = 0;
        foreach (var gp in method.GenericParameters)
        {
            if (gp.TypeName?.StartsWith("τ_0_") == true)
                continue; // Skip depth-0 (Self)
            var safeName = $"_G{genericIdx++}";
            if (gp.TypeName != null) genericNameMap[gp.TypeName] = safeName;
        }

        // Build generic clause with constraints (e.g., <_G0: Decodable>)
        var genericParts = new List<string>();
        foreach (var gp in method.GenericParameters)
        {
            if (gp.TypeName?.StartsWith("τ_0_") == true) continue;
            if (!genericNameMap.TryGetValue(gp.TypeName ?? "", out var safeName)) continue;
            if (gp.GenericConformances.Count > 0)
            {
                var constraints = string.Join(" & ", gp.GenericConformances
                    .Select(c => c.ConformanceTarget.Name));
                genericParts.Add($"{safeName}: {constraints}");
            }
            else
            {
                genericParts.Add(safeName);
            }
        }
        var genericClause = genericParts.Count > 0
            ? $"<{string.Join(", ", genericParts)}>"
            : "";

        // Render TypeSpec, substituting generic param names.
        // suppressEscaping: true when inside Optional — Optional closures are always escaping in Swift,
        // so @escaping on Optional<Closure> is invalid syntax.
        string RenderTypeSpec(TypeSpec? ts, bool suppressEscaping = false)
        {
            if (ts == null) return "Any";
            if (ts is NamedTypeSpec named)
            {
                // Direct generic param match: τ_1_0 → _G0
                if (genericNameMap.TryGetValue(named.Name, out var safeName))
                    return safeName;
                // Metatype of generic param: τ_1_0.Type → _G0.Type
                foreach (var (tauName, gName) in genericNameMap)
                {
                    if (named.Name.StartsWith(tauName + "."))
                        return gName + named.Name.Substring(tauName.Length);
                }
                // Self-typed (depth-0) generic params: τ_0_0 → EveryProtocol
                // Dependent member types (τ_0_0.RowDecoder) → Any (associated type erasure)
                // Metatype (τ_0_0.Type) → EveryProtocol.Type
                if (named.Name.StartsWith("τ_0_"))
                {
                    var dotIdx = named.Name.IndexOf('.');
                    if (dotIdx > 0)
                    {
                        var suffix = named.Name.Substring(dotIdx);
                        // .Type is metatype, not an associated type
                        if (suffix == ".Type")
                            return "EveryProtocol.Type";
                        // Dependent member types (associated types) → Any
                        return "Any";
                    }
                    return "EveryProtocol";
                }
                if (!TypeSpecHelpers.IsGenericTypeParameter(named.Name))
                {
                    if (named.ContainsGenericParameters && named.GenericParameters.Count > 0)
                    {
                        bool isOptional = named.Name == "Swift.Optional";
                        var renderedParams = string.Join(", ", named.GenericParameters
                            .Select(p => RenderTypeSpec(p, suppressEscaping: isOptional)));
                        return $"{named.Name}<{renderedParams}>";
                    }
                    return GetSwiftTypeName(ts);
                }
                return "Any"; // Fallback for unrecognized generic params
            }
            if (ts is ClosureTypeSpec closure)
            {
                // Render closure arguments: unwrap tuple elements to avoid double-wrapping.
                // A closure (A, B, C) -> D has Arguments as TupleTypeSpec{A, B, C}.
                // If we render the tuple as "(A, B, C)" then wrap in closure parens, we get "((A, B, C))".
                // Instead, render elements directly and let the closure format add the parens.
                string args;
                if (closure.Arguments is TupleTypeSpec argTuple && argTuple.Elements.Count > 0)
                    args = string.Join(", ", argTuple.Elements.Select(e => RenderTypeSpec(e)));
                else if (closure.Arguments.IsEmptyTuple)
                    args = "";
                else
                    args = RenderTypeSpec(closure.Arguments);
                var ret = RenderTypeSpec(closure.ReturnType);
                var attrs = new List<string>();
                if (closure.IsEscaping && !suppressEscaping) attrs.Add("@escaping");
                if (closure.HasAttributes)
                {
                    foreach (var attr in closure.Attributes)
                    {
                        if (attr.Name != "escaping")
                            attrs.Add($"@{attr.Name}");
                    }
                }
                var attrPrefix = attrs.Count > 0 ? string.Join(" ", attrs) + " " : "";
                var asyncStr = closure.IsAsync ? " async" : "";
                var throwsStr = closure.Throws ? " throws" : "";
                return $"{attrPrefix}({args}){asyncStr}{throwsStr} -> {ret}";
            }
            if (ts is TupleTypeSpec tuple)
            {
                if (tuple.Elements.Count == 0) return "()";
                var rendered = tuple.Elements.Select(e =>
                {
                    var typeName = RenderTypeSpec(e);
                    return e.TypeLabel != null ? $"{e.TypeLabel}: {typeName}" : typeName;
                });
                return $"({string.Join(", ", rendered)})";
            }
            if (ts is AssociatedTypeReferenceSpec assocRef)
            {
                // Dependent member types on Self (τ_0_0.RowDecoder) → Any
                // Method-level generic params (_G0.Element) → Any (unconstrained)
                if (assocRef.BaseType.StartsWith("τ_0_") || genericNameMap.ContainsKey(assocRef.BaseType))
                    return "Any";
                return GetSwiftTypeName(ts);
            }
            if (ts.IsEmptyTuple) return "()";
            return GetSwiftTypeName(ts);
        }

        // Build parameter list with proper Swift labeling

        // Declaration-position form: a stub must still satisfy its requirement, so a `T!` requirement
        // needs a `T!` stub. Top level only — the recursion above stays on the plain form, where `!`
        // would be a syntax error.
        string RenderTypeSpecForDeclaration(TypeSpec? ts) =>
            SwiftTypeNameHelper.ApplyImplicitlyUnwrappedOptionalSigil(RenderTypeSpec(ts), ts);
        var parameters = new List<string>();
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            var param = method.CSSignature[i];
            // RenderTypeSpec already handles @escaping for direct closures and
            // suppresses it for Optional<Closure> (always escaping in Swift).
            var paramTypeName = RenderTypeSpecForDeclaration(param.SwiftTypeSpec);
            var externalLabel = GetSwiftParameterLabel(param, i);
            var internalName = GetSwiftParameterName(param, i);
            var inoutPrefix = param.IsInOut ? "inout " : "";

            if (externalLabel == "_")
                parameters.Add($"_ {internalName}: {inoutPrefix}{paramTypeName}");
            else if (externalLabel == internalName)
                parameters.Add($"{internalName}: {inoutPrefix}{paramTypeName}");
            else
                parameters.Add($"{externalLabel} {internalName}: {inoutPrefix}{paramTypeName}");
        }

        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var hasReturn = returnType != null && !returnType.IsEmptyTuple;
        var returnTypeName = hasReturn ? RenderTypeSpecForDeclaration(returnType!) : "Void";
        // This non-dispatchable fatalError stub keeps the requirement's own effects (`async`/`throws`)
        // — a stub satisfies its protocol either way, and a mixed sync/async (or throws/non-throws)
        // fan-out group never routes through an effect-mismatched stub: ComputeMethodEmissionPlans
        // owner-selection hands the witness to the sync, non-throwing sibling whenever one exists, so
        // the owner whose extension emits this stub is async/throwing only when EVERY sibling is
        // (an all-async or all-throwing group), where the matching-effect stub satisfies them all.
        var asyncDecl = method.IsAsync ? " async" : "";
        var throwsDecl = method.Throws ? " throws" : "";
        var returnDecl = hasReturn ? $" -> {returnTypeName}" : "";

        writer.WriteLine($"public func {NameProvider.ParserNameToSwift(method)}{genericClause}({string.Join(", ", parameters)}){asyncDecl}{throwsDecl}{returnDecl} {{");
        writer.Indent++;
        writer.WriteLine($"fatalError(\"[SwiftBindings] EveryProtocol: closure method '{method.Name}' cannot be dispatched through vtable\")");
        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    private void EmitMethodLevelGenericStub(SwiftWriter writer, MethodDecl method)
    {
        // Build generic param name mapping: τ_1_0 → _G0, τ_1_1 → _G1, etc.
        // Filter out depth-0 params (Self).
        var genericNameMap = new Dictionary<string, string>();
        int genericIdx = 0;
        foreach (var gp in method.GenericParameters)
        {
            if (gp.TypeName?.StartsWith("τ_0_") == true)
                continue;
            var safeName = $"_G{genericIdx++}";
            if (gp.TypeName != null) genericNameMap[gp.TypeName] = safeName;
        }
        // Build generic clause with constraints (e.g., <_G0: Decodable>)
        var genericParts = new List<string>();
        foreach (var gp in method.GenericParameters)
        {
            if (gp.TypeName?.StartsWith("τ_0_") == true) continue;
            if (!genericNameMap.TryGetValue(gp.TypeName ?? "", out var safeName)) continue;
            if (gp.GenericConformances.Count > 0)
            {
                var constraints = string.Join(" & ", gp.GenericConformances
                    .Select(c => c.ConformanceTarget.Name));
                genericParts.Add($"{safeName}: {constraints}");
            }
            else
            {
                genericParts.Add(safeName);
            }
        }
        var genericClause = genericParts.Count > 0
            ? $"<{string.Join(", ", genericParts)}>"
            : "";

        // Render TypeSpec preserving generic params (replacing τ_1_0 → _G0, etc.)
        // suppressEscaping: true when inside Optional — Optional closures are always escaping in Swift.
        string RenderTypeSpec(TypeSpec? ts, bool suppressEscaping = false)
        {
            if (ts == null) return "Any";
            if (ts is NamedTypeSpec named)
            {
                // Direct generic param match: τ_1_0 → _G0
                if (genericNameMap.TryGetValue(named.Name, out var safeName))
                    return safeName;
                // Metatype of generic param: τ_1_0.Type → _G0.Type
                foreach (var (tauName, gName) in genericNameMap)
                {
                    if (named.Name.StartsWith(tauName + "."))
                        return gName + named.Name.Substring(tauName.Length);
                }
                // Self-typed (depth-0) generic params: τ_0_0 → EveryProtocol
                // Dependent member types (τ_0_0.RowDecoder) → Any (associated type erasure)
                // Metatype (τ_0_0.Type) → EveryProtocol.Type
                if (named.Name.StartsWith("τ_0_"))
                {
                    var dotIdx = named.Name.IndexOf('.');
                    if (dotIdx > 0)
                    {
                        var suffix = named.Name.Substring(dotIdx);
                        if (suffix == ".Type")
                            return "EveryProtocol.Type";
                        return "Any";
                    }
                    return "EveryProtocol";
                }
                // Non-generic types: use standard renderer
                if (!TypeSpecHelpers.IsGenericTypeParameter(named.Name))
                {
                    if (named.ContainsGenericParameters && named.GenericParameters.Count > 0)
                    {
                        bool isOptional = named.Name == "Swift.Optional";
                        // Render generic params recursively (e.g., ServiceEntry<τ_1_1> → ServiceEntry<_G1>)
                        var renderedParams = string.Join(", ", named.GenericParameters
                            .Select(p => RenderTypeSpec(p, suppressEscaping: isOptional)));
                        return $"{named.Name}<{renderedParams}>";
                    }
                    return GetSwiftTypeName(ts);
                }
                return "Any"; // Fallback for unrecognized generic params
            }
            if (ts is ClosureTypeSpec closure)
            {
                // Unwrap tuple arguments to avoid double-wrapping (see closure stub RenderTypeSpec).
                string args;
                if (closure.Arguments is TupleTypeSpec argTuple && argTuple.Elements.Count > 0)
                    args = string.Join(", ", argTuple.Elements.Select(e => RenderTypeSpec(e)));
                else if (closure.Arguments.IsEmptyTuple)
                    args = "";
                else
                    args = RenderTypeSpec(closure.Arguments);
                var ret = RenderTypeSpec(closure.ReturnType);
                var attrs = new List<string>();
                if (closure.IsEscaping && !suppressEscaping) attrs.Add("@escaping");
                if (closure.HasAttributes)
                {
                    foreach (var attr in closure.Attributes)
                    {
                        if (attr.Name != "escaping") // already handled above
                            attrs.Add($"@{attr.Name}");
                    }
                }
                var attrPrefix = attrs.Count > 0 ? string.Join(" ", attrs) + " " : "";
                var asyncStr = closure.IsAsync ? " async" : "";
                var throwsStr = closure.Throws ? " throws" : "";
                return $"{attrPrefix}({args}){asyncStr}{throwsStr} -> {ret}";
            }
            if (ts is TupleTypeSpec tuple)
            {
                if (tuple.Elements.Count == 0) return "()";
                var rendered = tuple.Elements.Select(e =>
                {
                    var typeName = RenderTypeSpec(e);
                    return e.TypeLabel != null ? $"{e.TypeLabel}: {typeName}" : typeName;
                });
                return $"({string.Join(", ", rendered)})";
            }
            if (ts is AssociatedTypeReferenceSpec assocRef)
            {
                // Dependent member types on Self (τ_0_0.RowDecoder) → Any
                // Method-level generic params (_G0.Element) → Any (unconstrained)
                if (assocRef.BaseType.StartsWith("τ_0_") || genericNameMap.ContainsKey(assocRef.BaseType))
                    return "Any";
                return GetSwiftTypeName(ts);
            }
            if (ts.IsEmptyTuple) return "()";
            return GetSwiftTypeName(ts);
        }

        // Build parameter list using raw TypeSpec

        // Declaration-position form: a stub must still satisfy its requirement, so a `T!` requirement
        // needs a `T!` stub. Top level only — the recursion above stays on the plain form, where `!`
        // would be a syntax error.
        string RenderTypeSpecForDeclaration(TypeSpec? ts) =>
            SwiftTypeNameHelper.ApplyImplicitlyUnwrappedOptionalSigil(RenderTypeSpec(ts), ts);
        var parameters = new List<string>();
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            var param = method.CSSignature[i];
            var paramTypeName = RenderTypeSpecForDeclaration(param.SwiftTypeSpec);
            var externalLabel = GetSwiftParameterLabel(param, i);
            var internalName = GetSwiftParameterName(param, i);
            var inoutPrefix = param.IsInOut ? "inout " : "";
            if (externalLabel == "_")
                parameters.Add($"_ {internalName}: {inoutPrefix}{paramTypeName}");
            else if (externalLabel == internalName)
                parameters.Add($"{internalName}: {inoutPrefix}{paramTypeName}");
            else
                parameters.Add($"{externalLabel} {internalName}: {inoutPrefix}{paramTypeName}");
        }

        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var hasReturn = returnType != null && !returnType.IsEmptyTuple;
        var returnTypeName = hasReturn ? RenderTypeSpecForDeclaration(returnType!) : "Void";
        // This non-dispatchable fatalError stub keeps the requirement's own effects (`async`/`throws`)
        // — a stub satisfies its protocol either way, and a mixed sync/async (or throws/non-throws)
        // fan-out group never routes through an effect-mismatched stub: ComputeMethodEmissionPlans
        // owner-selection hands the witness to the sync, non-throwing sibling whenever one exists, so
        // the owner whose extension emits this stub is async/throwing only when EVERY sibling is
        // (an all-async or all-throwing group), where the matching-effect stub satisfies them all.
        var asyncDecl = method.IsAsync ? " async" : "";
        var throwsDecl = method.Throws ? " throws" : "";
        var returnDecl = hasReturn ? $" -> {returnTypeName}" : "";
        bool isOptionalReturn = hasReturn && returnType is NamedTypeSpec nts &&
            nts.Name == "Swift.Optional";

        writer.WriteLine($"public func {NameProvider.ParserNameToSwift(method)}{genericClause}({string.Join(", ", parameters)}){asyncDecl}{throwsDecl}{returnDecl} {{");
        writer.Indent++;

        if (!hasReturn)
            writer.WriteLine("// Method-level generic stub: no-op for Void return");
        else if (isOptionalReturn)
            writer.WriteLine("return nil // Method-level generic stub: can't dispatch through vtable");
        else if (method.Throws)
        {
            writer.WriteLine("// Method-level generic stub: throws error — can't dispatch through vtable");
            writer.WriteLine($"throw NSError(domain: \"SwiftBindings\", code: -1, userInfo: [NSLocalizedDescriptionKey: \"Protocol method with generic parameters is not supported\"])");
        }
        else
            writer.WriteLine($"fatalError(\"[SwiftBindings] EveryProtocol: method-level generic method '{method.Name}' cannot be dispatched through vtable\")");

        writer.Indent--;
        writer.WriteLine("}");
    }

    /// <summary>
    /// Emits a fatalError() stub for a protocol method that contains Self-typed (τ_0_*) references
    /// in its parameters or return type. Substitutes τ_0_0 with EveryProtocol so the Swift
    /// conformance compiles — Self IS EveryProtocol in the conformance context.
    /// </summary>
    private void EmitSelfTypedMethodStub(SwiftWriter writer, MethodDecl method)
    {
        // Build parameter list using Self-substituted type rendering
        var parameters = new List<string>();
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            var param = method.CSSignature[i];
            var paramTypeName = RenderTypeSpecWithSelfSubstitutionForDeclaration(param.SwiftTypeSpec);
            var externalLabel = GetSwiftParameterLabel(param, i);
            var internalName = GetSwiftParameterName(param, i);
            var inoutPrefix = param.IsInOut ? "inout " : "";
            if (externalLabel == "_")
                parameters.Add($"_ {internalName}: {inoutPrefix}{paramTypeName}");
            else if (externalLabel == internalName)
                parameters.Add($"{internalName}: {inoutPrefix}{paramTypeName}");
            else
                parameters.Add($"{externalLabel} {internalName}: {inoutPrefix}{paramTypeName}");
        }

        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var hasReturn = returnType != null && !returnType.IsEmptyTuple;
        var returnTypeName = hasReturn ? RenderTypeSpecWithSelfSubstitutionForDeclaration(returnType!) : "Void";
        // This non-dispatchable fatalError stub keeps the requirement's own effects (`async`/`throws`)
        // — a stub satisfies its protocol either way, and a mixed sync/async (or throws/non-throws)
        // fan-out group never routes through an effect-mismatched stub: ComputeMethodEmissionPlans
        // owner-selection hands the witness to the sync, non-throwing sibling whenever one exists, so
        // the owner whose extension emits this stub is async/throwing only when EVERY sibling is
        // (an all-async or all-throwing group), where the matching-effect stub satisfies them all.
        var asyncDecl = method.IsAsync ? " async" : "";
        var throwsDecl = method.Throws ? " throws" : "";
        var returnDecl = hasReturn ? $" -> {returnTypeName}" : "";

        writer.WriteLine($"public func {NameProvider.ParserNameToSwift(method)}({string.Join(", ", parameters)}){asyncDecl}{throwsDecl}{returnDecl} {{");
        writer.Indent++;
        writer.WriteLine($"fatalError(\"[SwiftBindings] EveryProtocol: Self-typed method '{method.Name}' cannot be dispatched through vtable\")");
        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    /// <summary>
    /// True when any parameter is both <c>inout</c> and an ObjC-bridgeable value type — either the
    /// non-optional shape (<c>inout URL</c>/<c>URLRequest</c>/<c>Decimal</c>) or the optional shape
    /// (<c>inout URL?</c>). The reverse-dispatch path cannot write the mutated value back across the
    /// ObjC bridge in either case — the param-in arms bind a bridged ObjC-pointer temporary
    /// (<c>{p}Ref</c>) as the writeback source, so a dispatched body would assign that pointer to the
    /// Swift value type and fail to compile — so such methods get a trap stub instead. The optional
    /// arm must be caught here too because <see cref="IsObjCBridgeableParam"/> does NOT unwrap
    /// <c>Optional</c>, so it alone would let <c>inout URL?</c> slip past into the dispatch body.
    /// </summary>
    private bool MethodHasInOutObjCBridgeableParam(MethodDecl method)
    {
        for (int i = 1; i < method.CSSignature.Count; i++) // skip return at [0]
        {
            var param = method.CSSignature[i];
            if (param.IsInOut &&
                (IsObjCBridgeableParam(param.SwiftTypeSpec) ||
                 GetOptionalObjCBridgeableValueInnerName(param.SwiftTypeSpec) is not null))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Emits a fatalError() stub for a method with an inout ObjC-bridgeable parameter. The
    /// witness satisfies the protocol requirement but traps if dispatched, because bridging the
    /// mutated ObjC pointer back into the Swift value type is not wired on either side.
    /// </summary>
    private void EmitInOutObjCBridgeableMethodStub(SwiftWriter writer, MethodDecl method)
    {
        var parameters = new List<string>();
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            var param = method.CSSignature[i];
            var paramTypeName = RenderTypeSpecWithSelfSubstitutionForDeclaration(param.SwiftTypeSpec);
            var externalLabel = GetSwiftParameterLabel(param, i);
            var internalName = GetSwiftParameterName(param, i);
            var inoutPrefix = param.IsInOut ? "inout " : "";
            if (externalLabel == "_")
                parameters.Add($"_ {internalName}: {inoutPrefix}{paramTypeName}");
            else if (externalLabel == internalName)
                parameters.Add($"{internalName}: {inoutPrefix}{paramTypeName}");
            else
                parameters.Add($"{externalLabel} {internalName}: {inoutPrefix}{paramTypeName}");
        }

        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var hasReturn = returnType != null && !returnType.IsEmptyTuple;
        var returnTypeName = hasReturn ? RenderTypeSpecWithSelfSubstitutionForDeclaration(returnType!) : "Void";
        // This non-dispatchable fatalError stub keeps the requirement's own effects (`async`/`throws`)
        // — a stub satisfies its protocol either way, and a mixed sync/async (or throws/non-throws)
        // fan-out group never routes through an effect-mismatched stub: ComputeMethodEmissionPlans
        // owner-selection hands the witness to the sync, non-throwing sibling whenever one exists, so
        // the owner whose extension emits this stub is async/throwing only when EVERY sibling is
        // (an all-async or all-throwing group), where the matching-effect stub satisfies them all.
        var asyncDecl = method.IsAsync ? " async" : "";
        var throwsDecl = method.Throws ? " throws" : "";
        var returnDecl = hasReturn ? $" -> {returnTypeName}" : "";

        writer.WriteLine($"public func {NameProvider.ParserNameToSwift(method)}({string.Join(", ", parameters)}){asyncDecl}{throwsDecl}{returnDecl} {{");
        writer.Indent++;
        writer.WriteLine($"fatalError(\"[SwiftBindings] EveryProtocol: method '{method.Name}' with an inout ObjC-bridgeable parameter cannot be dispatched through vtable\")");
        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    // ---- Collapsed existential overload (fillability-null slot) -----------------------------------
    //
    // Two raw-DISTINCT existential overloads of the same method name can collapse onto a SINGLE C#
    // interface method — either because their raw signature keys erase to the same Swift.AnyType
    // (add(any Expression)/add(any Sendable), the FirebaseFirestore shape) or because their projected
    // C# param types coincide (consume(any A)/consume(any B) → Consume(object)). The reverse-dispatch
    // LAYOUT still gives each overload its OWN vtable slot (GetMethodKey keys off the raw Swift type,
    // so the struct is sized correctly and never shrinks below Swift's — see EmitProtocolVtableStruct),
    // but the C# FILLABILITY walk (ProtocolProxyEmitter's receiver + static-init loops) fills only the
    // FIRST overload's slot: the collapsed interface has exactly one method to reverse-dispatch into.
    // The later overload's slot is therefore left null. Its Swift witness must NOT emit the real
    // dispatch body — that force-unwraps the nil slot and traps at the point of dispatch — so it gets a
    // branded fatalError stub instead. The first overload's witness is untouched (real dispatch body).

    /// <summary>
    /// Computes the set of vtable slot keys (<see cref="GetMethodKey"/>) that the reverse-dispatch
    /// layout keeps a field for but the C# fillability walk leaves NULL — the collapsed existential
    /// overloads whose witness must trap rather than force-unwrap a nil slot. Mirrors the
    /// declaration-order, first-wins fillability filters in
    /// <c>ProtocolProxyEmitter.EmitMethodReceivers</c> / <c>EmitVtableStaticInit</c>: the same
    /// ctor/static/@objc-optional pre-skip, the same <see cref="ProtocolVtableMembers.IncludesMethod"/>
    /// layout gate, then the same first-wins dedup on
    /// <see cref="ProtocolMethodDisambiguator.EffectiveRawKey"/> (raw-signature collapse) and
    /// <see cref="ProtocolMethodDisambiguator.EffectiveProjectedKey"/> (projected-C# collapse).
    /// </summary>
    /// <remarks>
    /// The receiver loop additionally checks <c>_skippedMethodKeys</c> BEFORE the raw/projected dedup —
    /// that set (populated in <c>ProtocolHandler</c>) is not visible here, so its two Included=true
    /// members are reproduced directly instead: the projected-C#-collision subset is caught by the
    /// EffectiveProjectedKey dedup below (identical decision), and the member-gate skip subset
    /// (AnyType / unsupported-module parameters — a member kept in the layout but dropped from the C#
    /// interface) is reproduced by re-running <see cref="MemberGateEvaluator.EvaluateMethod"/>, which is
    /// exactly what populated <c>_skippedMethodKeys</c> there. The one residual it does not reproduce is
    /// the emitted-C#-signature collision (a member whose projected KEY is unique but whose fully
    /// rendered C# signature collides): that requires ProtocolHandler's private emitted-signature
    /// builder and is vanishingly narrow (distinct projected keys, identical rendered signatures). If it
    /// ever fires it fails closed exactly as before this change — the witness force-unwraps the nil slot
    /// — so no regression is introduced; it is simply not additionally covered.
    /// </remarks>
    private HashSet<string> ComputeCollapsedUnfilledMethodSlotKeys(ProtocolDecl protocolDecl, ClosureHandler closureHandler)
    {
        var unfilled = new HashSet<string>();
        var seenSlotKeys = new HashSet<string>();
        var emittedRawKeys = new HashSet<string>();
        var emittedCSharpKeys = new HashSet<string>();
        var gateEvaluator = new MemberGateEvaluator(_typeDatabase);
        foreach (var method in protocolDecl.Methods)
        {
            // Ctor/static/@objc-optional consume no slot — pre-skipped by the layout and the receiver
            // loop alike, BEFORE the index lookup (mirrors the receiver loop's leading continues).
            if (method.IsConstructor || method.MethodType == MethodType.Static)
                continue;
            if (method.IsObjCOptional)
                continue;

            var slotKey = GetMethodKey(method);
            // First occurrence of each raw slot only: a true GetMethodKey duplicate reuses the earlier
            // slot and the witness loop's isNewMethod guard already suppresses its body.
            if (!seenSlotKeys.Add(slotKey))
                continue;
            // No Swift vtable field (skip-but-consume: non-dispatchable closure / method-generic /
            // Self-typed / mixed-generic / nested @objc existential). Not a null-filled slot; the
            // witness loop's structural stub branches already cover these before this one runs.
            if (!ProtocolVtableMembers.IncludesMethod(method, protocolDecl, closureHandler))
                continue;
            // Member-gate skip (AnyType / unsupported-module parameter): kept in the layout, dropped
            // from the C# interface → no receiver → null slot. Reproduces the _skippedMethodKeys subset
            // the receiver loop consults; must run BEFORE claiming the raw/projected keys so a surviving
            // later sibling can still fill this slot's raw/projected identity.
            if (gateEvaluator.EvaluateMethod(method, protocolDecl.ModuleDecl, protocolDecl).IsSkipped)
            {
                unfilled.Add(slotKey);
                continue;
            }
            var collapsingKey = ProtocolMethodDisambiguator.EffectiveRawKey(method, protocolDecl, _typeDatabase);
            if (!emittedRawKeys.Add(collapsingKey))
            {
                unfilled.Add(slotKey);
                continue;
            }
            var projectedKey = ProtocolMethodDisambiguator.EffectiveProjectedKey(method, protocolDecl, _typeDatabase, propertyNames: null);
            if (!emittedCSharpKeys.Add(projectedKey))
            {
                unfilled.Add(slotKey);
                continue;
            }
        }
        return unfilled;
    }

    /// <summary>
    /// Emits a fatalError() stub for a collapsed existential overload whose vtable slot the C#
    /// fillability walk leaves null. Satisfies the Swift protocol requirement; never dispatched (only
    /// the first, surviving overload has a filled slot and a C# interface member).
    /// </summary>
    private void EmitCollapsedOverloadMethodStub(SwiftWriter writer, MethodDecl method)
    {
        var parameters = new List<string>();
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            var param = method.CSSignature[i];
            var paramTypeName = RenderTypeSpecWithSelfSubstitutionForDeclaration(param.SwiftTypeSpec);
            var externalLabel = GetSwiftParameterLabel(param, i);
            var internalName = GetSwiftParameterName(param, i);
            var inoutPrefix = param.IsInOut ? "inout " : "";
            if (externalLabel == "_")
                parameters.Add($"_ {internalName}: {inoutPrefix}{paramTypeName}");
            else if (externalLabel == internalName)
                parameters.Add($"{internalName}: {inoutPrefix}{paramTypeName}");
            else
                parameters.Add($"{externalLabel} {internalName}: {inoutPrefix}{paramTypeName}");
        }

        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var hasReturn = returnType != null && !returnType.IsEmptyTuple;
        var returnTypeName = hasReturn ? RenderTypeSpecWithSelfSubstitutionForDeclaration(returnType!) : "Void";
        // Keep the requirement's own effects — a stub satisfies its protocol either way (see
        // EmitSelfTypedMethodStub for the effect-mismatch fan-out rationale).
        var asyncDecl = method.IsAsync ? " async" : "";
        var throwsDecl = method.Throws ? " throws" : "";
        var returnDecl = hasReturn ? $" -> {returnTypeName}" : "";

        writer.WriteLine($"public func {NameProvider.ParserNameToSwift(method)}({string.Join(", ", parameters)}){asyncDecl}{throwsDecl}{returnDecl} {{");
        writer.Indent++;
        writer.WriteLine($"fatalError(\"[SwiftBindings] EveryProtocol: collapsed existential overload '{method.Name}' is not representable in C# — only the first overload dispatches\")");
        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    // ---- @objc protocol existential in an unsupported nested position (fail-closed drop) ----------
    //
    // An @objc protocol's existential marshals as a single 8-byte ObjC object pointer (no witness-table
    // word). Only a bare `any P` / `Optional<any P>` is supported; a nested position (container/tuple/
    // closure) would route the reverse receiver through the 40-byte ExistentialContainer1 carrier over
    // that 8-byte stride — a buffer over-read. Such a requirement is dropped fail-closed from BOTH the C#
    // interface (MemberGateEvaluator) and the reverse-dispatch vtable slot (VtableLayoutBuilder.Classify*,
    // where it is skip-but-consume: index consumed, field omitted). The Swift protocol still requires a
    // witness, so the EveryProtocol extension emits a fatalError stub — it is never dispatched, because C#
    // has no interface member and no filled vtable slot pointing at it. The predicates below MUST use the
    // identical ExistentialHandler.HasUnsupportedObjCProtocolExistentialPosition oracle the classifier
    // uses, or the stub/skip-but-consume decision here desyncs from the vtable struct → SIGSEGV.

    private bool HasUnsupportedObjCExistentialProperty(PropertyDecl property) =>
        ExistentialHandler.HasUnsupportedObjCProtocolExistentialPosition(property.SwiftTypeSpec, _typeDatabase);

    private bool MethodHasUnsupportedObjCExistential(MethodDecl method) =>
        method.CSSignature.Any(arg =>
            ExistentialHandler.HasUnsupportedObjCProtocolExistentialPosition(arg.SwiftTypeSpec, _typeDatabase));

    private bool HasUnsupportedObjCExistentialSubscript(SubscriptDecl subscript) =>
        subscript.IndexParameters.Select(p => p.SwiftTypeSpec).Prepend(subscript.ReturnTypeSpec)
            .Any(spec => ExistentialHandler.HasUnsupportedObjCProtocolExistentialPosition(spec, _typeDatabase));

    /// <summary>
    /// Emits a fatalError() stub for a property whose type carries an @objc protocol existential in an
    /// unsupported nested position. Satisfies the Swift requirement; never dispatched.
    /// </summary>
    private void EmitObjCExistentialPropertyStub(SwiftWriter writer, PropertyDecl property)
    {
        var hasGetter = property.Accessors.OfType<GetAccessorDecl>().Any();
        var hasSetter = property.Accessors.OfType<SetAccessorDecl>().Any();
        var swiftTypeName = RenderTypeSpecWithSelfSubstitutionForDeclaration(property.SwiftTypeSpec);

        writer.WriteLine($"public var {NameProvider.ParserNameToSwift(property)}: {swiftTypeName} {{");
        writer.Indent++;
        if (hasGetter)
            writer.WriteLine($"get {{ fatalError(\"[SwiftBindings] EveryProtocol: property '{property.Name}' with a nested @objc protocol existential cannot be dispatched through vtable\") }}");
        if (hasSetter)
            writer.WriteLine($"set {{ fatalError(\"[SwiftBindings] EveryProtocol: property '{property.Name}' with a nested @objc protocol existential cannot be dispatched through vtable\") }}");
        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    /// <summary>
    /// Emits a fatalError() stub for a method whose signature carries an @objc protocol existential in an
    /// unsupported nested position. Satisfies the Swift requirement; never dispatched.
    /// </summary>
    private void EmitObjCExistentialMethodStub(SwiftWriter writer, MethodDecl method)
    {
        var parameters = new List<string>();
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            var param = method.CSSignature[i];
            var paramTypeName = RenderTypeSpecWithSelfSubstitutionForDeclaration(param.SwiftTypeSpec);
            var externalLabel = GetSwiftParameterLabel(param, i);
            var internalName = GetSwiftParameterName(param, i);
            var inoutPrefix = param.IsInOut ? "inout " : "";
            if (externalLabel == "_")
                parameters.Add($"_ {internalName}: {inoutPrefix}{paramTypeName}");
            else if (externalLabel == internalName)
                parameters.Add($"{internalName}: {inoutPrefix}{paramTypeName}");
            else
                parameters.Add($"{externalLabel} {internalName}: {inoutPrefix}{paramTypeName}");
        }

        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var hasReturn = returnType != null && !returnType.IsEmptyTuple;
        var returnTypeName = hasReturn ? RenderTypeSpecWithSelfSubstitutionForDeclaration(returnType!) : "Void";
        // Keep the requirement's own effects — a stub satisfies its protocol either way (see
        // EmitSelfTypedMethodStub for the effect-mismatch fan-out rationale).
        var asyncDecl = method.IsAsync ? " async" : "";
        var throwsDecl = method.Throws ? " throws" : "";
        var returnDecl = hasReturn ? $" -> {returnTypeName}" : "";

        writer.WriteLine($"public func {NameProvider.ParserNameToSwift(method)}({string.Join(", ", parameters)}){asyncDecl}{throwsDecl}{returnDecl} {{");
        writer.Indent++;
        writer.WriteLine($"fatalError(\"[SwiftBindings] EveryProtocol: method '{method.Name}' with a nested @objc protocol existential cannot be dispatched through vtable\")");
        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    /// <summary>
    /// Emits a fatalError() stub for a subscript whose signature carries an @objc protocol existential in
    /// an unsupported nested position. Satisfies the Swift requirement; never dispatched.
    /// </summary>
    private void EmitObjCExistentialSubscriptStub(SwiftWriter writer, SubscriptDecl subscript, int index)
    {
        var parameters = new List<string>();
        for (int i = 0; i < subscript.IndexParameters.Count; i++)
        {
            var param = subscript.IndexParameters[i];
            var typeName = RenderTypeSpecWithSelfSubstitutionForDeclaration(param.SwiftTypeSpec);
            var externalLabel = NameProvider.GetSubscriptExternalLabel(param);
            var internalName = $"arg{i}";
            parameters.Add($"{externalLabel} {internalName}: {typeName}");
        }

        var returnTypeName = RenderTypeSpecWithSelfSubstitutionForDeclaration(subscript.ReturnTypeSpec);

        writer.WriteLine($"public subscript({string.Join(", ", parameters)}) -> {returnTypeName} {{");
        writer.Indent++;
        if (subscript.HasGetter)
            writer.WriteLine($"get {{ fatalError(\"[SwiftBindings] EveryProtocol: subscript with a nested @objc protocol existential cannot be dispatched through vtable\") }}");
        if (subscript.HasSetter)
            writer.WriteLine($"set {{ fatalError(\"[SwiftBindings] EveryProtocol: subscript with a nested @objc protocol existential cannot be dispatched through vtable\") }}");
        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    /// <summary>
    /// Emits a fatalError() stub for a protocol subscript that contains Self-typed (τ_0_*) references.
    /// </summary>
    private void EmitSelfTypedSubscriptStub(SwiftWriter writer, SubscriptDecl subscript, int index)
    {
        // Same external-label hazard as EmitSubscriptImplementation: subscripts require the
        // explicit `<external> <internal>:` form to preserve the protocol's argument label.
        var parameters = new List<string>();
        for (int i = 0; i < subscript.IndexParameters.Count; i++)
        {
            var param = subscript.IndexParameters[i];
            var typeName = RenderTypeSpecWithSelfSubstitutionForDeclaration(param.SwiftTypeSpec);
            var externalLabel = NameProvider.GetSubscriptExternalLabel(param);
            var internalName = $"arg{i}";
            parameters.Add($"{externalLabel} {internalName}: {typeName}");
        }

        var returnTypeName = RenderTypeSpecWithSelfSubstitutionForDeclaration(subscript.ReturnTypeSpec);

        writer.WriteLine($"public subscript({string.Join(", ", parameters)}) -> {returnTypeName} {{");
        writer.Indent++;
        if (subscript.HasGetter)
        {
            writer.WriteLine($"get {{ fatalError(\"[SwiftBindings] EveryProtocol: Self-typed subscript cannot be dispatched through vtable\") }}");
        }
        if (subscript.HasSetter)
        {
            writer.WriteLine($"set {{ fatalError(\"[SwiftBindings] EveryProtocol: Self-typed subscript cannot be dispatched through vtable\") }}");
        }
        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    private void EmitMethodImplementation(SwiftWriter writer, MethodDecl method, ProtocolDecl protocolDecl,
        string vtableInstanceName, int index, bool? effectiveThrows = null,
        MethodEmissionPlan? plan = null, IReadOnlyList<AvailabilityAnnotation>? extensionAvailability = null)
    {
        // Build parameter list with proper Swift labeling
        var parameters = new List<string>();
        var internalNames = new List<string>(); // Names used inside the function body
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            var param = method.CSSignature[i];
            var paramTypeName = GetSwiftTypeNameForDeclaration(param.SwiftTypeSpec);
            var externalLabel = GetSwiftParameterLabel(param, i);
            var internalName = GetSwiftParameterName(param, i);
            internalNames.Add(internalName);

            // Add inout modifier if the parameter is passed by reference.
            // Add `consuming` for ~Copyable params (Swift 6 requires explicit ownership; the
            // wrapper body moves the value into a local var before passing inout to the vtable).
            var ownershipPrefix = param.IsInOut
                ? "inout "
                : (WrapperValidation.IsNonCopyableType(param.SwiftTypeSpec, _typeDatabase, method.ModuleDecl)
                    ? "consuming "
                    : "");

            // Swift parameter format: "externalLabel internalName: Type" or "_ internalName: Type"
            if (externalLabel == "_")
            {
                parameters.Add($"_ {internalName}: {ownershipPrefix}{paramTypeName}");
            }
            else if (externalLabel == internalName)
            {
                // Same label and name - just use one
                parameters.Add($"{internalName}: {ownershipPrefix}{paramTypeName}");
            }
            else
            {
                parameters.Add($"{externalLabel} {internalName}: {ownershipPrefix}{paramTypeName}");
            }
        }
        var parametersString = string.Join(", ", parameters);

        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var hasReturn = returnType != null && !returnType.IsEmptyTuple;
        var returnTypeName = hasReturn ? GetSwiftTypeNameForDeclaration(returnType!) : "Void";
        var returnTypeNameForMetatype = hasReturn ? GetSwiftTypeNameForMetatype(returnType!) : "Void";
        // `async` must be propagated to the conformance declaration ONLY for the
        // EveryObjCProtocol path (NSObject-rooted twin used for @objc protocols):
        // @objc async requirements bridge to ObjC `:completion:`-suffixed selectors,
        // and swiftc rejects sync candidates with "candidate is not 'async', but
        // '@objc' protocol requirement is".
        //
        // For pure-Swift protocols (EveryProtocol base, `_useObjCBase == false`),
        // a sync candidate trivially satisfies an async requirement (sync = "never
        // suspends"). Emitting `async` here is not just unnecessary — it actively
        // breaks any sibling/child protocol whose SYNC requirement was being
        // satisfied by member-inheritance from this same conformance (e.g., a sync
        // sub-protocol refining an async base; marking the base witness `async`
        // breaks the sub-protocol's empty-body conformance).
        var asyncDecl = (method.IsAsync && _useObjCBase) ? " async" : "";
        var throwsDecl = (effectiveThrows ?? method.Throws) ? " throws" : "";
        var returnDecl = hasReturn ? $" -> {returnTypeName}" : "";

        var fieldName = GetMethodVtableFieldName(method, index);

        writer.WriteLine($"public func {NameProvider.ParserNameToSwift(method)}({parametersString}){asyncDecl}{throwsDecl}{returnDecl} {{");
        writer.Indent++;

        // Build argument copies for passing to vtable function
        // ObjC-bridgeable types (e.g., URL, URLRequest) need special handling:
        // bridge to AnyObject and pass the ObjC pointer instead of the Swift struct bytes.
        // The C# side uses GetNSObject<T>() which expects a valid ObjC pointer.
        var argPassList = new List<string>();
        var argRefList = new List<string>();
        var argWritebackSources = new List<string>(); // parallel to internalNames: inout writeback RHS
        for (int i = 0; i < internalNames.Count; i++)
        {
            var paramName = internalNames[i];
            var param = method.CSSignature[i + 1]; // +1 to skip return type
            var escapedParam = NameProvider.EscapeSwiftKeyword(paramName);
            bool isObjCBridgeable = IsObjCBridgeableParam(param.SwiftTypeSpec);
            if (isObjCBridgeable)
            {
                // Bridge Swift value type → ObjC object, pass pointer to the opaque reference.
                // C# MarshalFromSwift<IntPtr> reads the 8-byte pointer, then GetNSObject<T> resolves it.
                argPassList.Add($"let {paramName}NS = {escapedParam} as AnyObject");
                argPassList.Add($"var {paramName}Ref = Unmanaged.passUnretained({paramName}NS).toOpaque()");
                argRefList.Add($"&{paramName}Ref");
                argWritebackSources.Add($"{paramName}Copy");
            }
            else if (GetOptionalObjCBridgeableValueInnerName(param.SwiftTypeSpec) is not null)
            {
                // Optional ObjC-bridgeable VALUE param (URL?): pass a single optional ObjC POINTER word
                // (nil = 0x0), borrowing (+0) the bridged NSObject — the Optional.map wrapper of the
                // non-optional arm above so .none passes a nil (0x0) word. The C# receiver reads a bare
                // IntPtr and +0-bridges the live NSObject. `{p}NS` (Optional<AnyObject>) holds it alive
                // across the call; `{p}Ref` (Optional<UnsafeMutableRawPointer>) is the nil-optimized word.
                // The plain `&{p}Copy` else arm would pass the multi-word Optional<URL> bytes → receiver
                // misreads one word → layout mismatch.
                argPassList.Add($"let {paramName}NS = {escapedParam}.map {{ $0 as AnyObject }}");
                argPassList.Add($"var {paramName}Ref = {paramName}NS.map {{ Unmanaged.passUnretained($0).toOpaque() }}");
                argRefList.Add($"&{paramName}Ref");
                argWritebackSources.Add($"{paramName}Ref");
            }
            else if (RequiresExplicitValuePointer(param.SwiftTypeSpec))
            {
                // `&array` / `&string` passed to an UnsafeRawPointer vtable parameter triggers
                // Swift's implicit array/string-to-pointer conversion, handing the C# receiver a
                // pointer to the element buffer / UTF-8 bytes instead of the value. Route through
                // an explicitly-typed pointer so the receiver reads the value. See
                // RequiresExplicitValuePointer.
                var ptrName = $"{paramName}CopyPtr";
                argPassList.AddRange(BuildValueStorageSetup(ptrName, GetSwiftTypeName(param.SwiftTypeSpec), escapedParam));
                argRefList.Add($"UnsafeRawPointer({ptrName})");
                argWritebackSources.Add($"{ptrName}.pointee");
            }
            else
            {
                argPassList.Add($"var {paramName}Copy = {escapedParam}");
                argRefList.Add($"&{paramName}Copy");
                argWritebackSources.Add($"{paramName}Copy");
            }
        }
        var argRefs = argRefList.Count > 0 ? ", " + string.Join(", ", argRefList) : "";

        var argPassCode = argPassList.Count > 0 ? string.Join("\n        ", argPassList) + "\n        " : "";

        // Build writeback code for inout parameters
        var writebackLines = new List<string>();
        for (int i = 0; i < internalNames.Count; i++)
        {
            var param = method.CSSignature[i + 1]; // +1 to skip return type
            if (param.IsInOut)
            {
                writebackLines.Add($"{NameProvider.EscapeSwiftKeyword(internalNames[i])} = {argWritebackSources[i]}");
            }
        }
        var writebackCode = writebackLines.Count > 0 ? "\n        " + string.Join("\n        ", writebackLines) : "";

        // Resolve dispatch branches. A solo group (no same-signature siblings) keeps the
        // historic single-branch shape via a one-entry list — byte-identical to the pre-fan-out
        // output. A real sibling group fans out across each sibling's per-protocol vtable index,
        // picking the first one whose function pointer is non-nil, so a C# impl conforming to only
        // a non-owner protocol dispatches through ITS populated vtable instead of the owner's nil
        // global vtable (which the force-unwrap would otherwise SIGSEGV on). HasFilteredPeers forces
        // the nil-check fan-out even for a single emitted branch — see EmitSubscriptImplementation.
        IReadOnlyList<(ProtocolDecl Proto, int Index)> branches = plan?.Siblings.Count > 1
            ? plan.Siblings
            : new[] { (protocolDecl, index) };
        bool forceSafeFanOut = plan?.HasFilteredPeers == true;

        // String returns use Utf8Slice encoding from C# to avoid ARC issues. ObjC-bridgeable
        // returns (e.g. URL) arrive as an ObjC pointer the body bridges back to the value type.
        bool isStringMethodReturn = hasReturn && returnType is NamedTypeSpec retNts && retNts.Name == "Swift.String";
        bool isObjCBridgeableReturn = hasReturn && !isStringMethodReturn && returnType != null && IsObjCBridgeableParam(returnType);
        bool isObjCBridgeableContainerReturn = hasReturn && !isStringMethodReturn && !isObjCBridgeableReturn
            && returnType != null && CdeclParamMapper.IsObjCBridgeableContainer(returnType, _typeDatabase);

        if (branches.Count == 1 && !forceSafeFanOut)
        {
            if (hasReturn)
            {
                if (isStringMethodReturn)
                {
                    writer.WriteLines($$"""
                            var selfProto: {{protocolDecl.SwiftTypeName.ModuleQualifiedName}} = self
                            {{argPassCode}}let resultPtr = {{vtableInstanceName}}.{{fieldName}}!(
                                {{vtableInstanceName}}.csVTHandle, &selfProto{{argRefs}}){{writebackCode}}
                            let slice = resultPtr.load(as: SBW_Utf8Slice.self)
                            var str: Swift.String = ""
                            if slice.len > 0 {
                                let buffer = UnsafeBufferPointer(start: slice.ptr, count: slice.len)
                                str = String(decoding: buffer, as: UTF8.self)
                            }
                            slice.ptr.deallocate()
                            resultPtr.deallocate()
                            return str
                        """);
                }
                else if (isObjCBridgeableReturn || isObjCBridgeableContainerReturn)
                {
                    // Scalar and whole-container ObjC returns both cross at +1 (takeRetainedValue);
                    // see EmitPropertyGetterBody for the ownership rationale.
                    const string take = "takeRetainedValue";
                    writer.WriteLines($$"""
                            var selfProto: {{protocolDecl.SwiftTypeName.ModuleQualifiedName}} = self
                            {{argPassCode}}let resultPtr = {{vtableInstanceName}}.{{fieldName}}!(
                                {{vtableInstanceName}}.csVTHandle, &selfProto{{argRefs}}){{writebackCode}}
                            let resultObjPtr = resultPtr.load(as: UnsafeRawPointer.self)
                            resultPtr.deallocate()
                            return Unmanaged<AnyObject>.fromOpaque(resultObjPtr).{{take}}() as! {{returnTypeNameForMetatype}}
                        """);
                }
                else if (GetOptionalObjCBridgeableValueInnerName(returnType) is string optBridgeableInner)
                {
                    // Optional scalar ObjC-bridgeable VALUE return (URL? etc.): the receiver deposited a
                    // one-word optional ObjC pointer, not a multi-word Optional<T> buffer — read it as an
                    // UnsafeRawPointer? and consume the +1 on .some. See EmitOptionalObjCBridgeableValueReturn.
                    writer.WriteLines($$"""
                            var selfProto: {{protocolDecl.SwiftTypeName.ModuleQualifiedName}} = self
                            {{argPassCode}}let resultPtr = {{vtableInstanceName}}.{{fieldName}}!(
                                {{vtableInstanceName}}.csVTHandle, &selfProto{{argRefs}}){{writebackCode}}
                            let resultObjPtr = resultPtr.load(as: UnsafeRawPointer?.self)
                            resultPtr.deallocate()
                            return resultObjPtr.map { Unmanaged<AnyObject>.fromOpaque($0).takeRetainedValue() as! {{optBridgeableInner}} }
                        """);
                }
                else
                {
                    writer.WriteLines($$"""
                            var selfProto: {{protocolDecl.SwiftTypeName.ModuleQualifiedName}} = self
                            {{argPassCode}}let resultPtr = {{vtableInstanceName}}.{{fieldName}}!(
                                {{vtableInstanceName}}.csVTHandle, &selfProto{{argRefs}}){{writebackCode}}
                            let __result = UnsafeMutableRawPointer(mutating: resultPtr).assumingMemoryBound(to: {{returnTypeNameForMetatype}}.self).move()
                            resultPtr.deallocate()
                            return __result
                        """);
                }
            }
            else
            {
                writer.WriteLines($$"""
                        var selfProto: {{protocolDecl.SwiftTypeName.ModuleQualifiedName}} = self
                        {{argPassCode}}{{vtableInstanceName}}.{{fieldName}}!(
                            {{vtableInstanceName}}.csVTHandle, &selfProto{{argRefs}}){{writebackCode}}
                    """);
            }
        }
        else
        {
            EmitMethodFanOutBody(writer, method, protocolDecl.SwiftTypeName.ModuleQualifiedName,
                branches, argPassList, writebackLines, argRefs,
                hasReturn, isStringMethodReturn, isObjCBridgeableReturn, isObjCBridgeableContainerReturn,
                returnTypeNameForMetatype,
                hasReturn ? GetOptionalObjCBridgeableValueInnerName(returnType) : null,
                extensionAvailability);
        }

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    /// <summary>
    /// Emits the body of a same-signature method whose owner fans out across sibling vtables.
    /// Each branch reads <c>func_{name}_{Index}</c> off that sibling's per-protocol global vtable,
    /// and is reached only if its predecessors' function pointers were nil — so dispatch through a
    /// smaller-sibling existential lands on the populated vtable. Argument copies and inout
    /// writebacks are handle-independent, so they are emitted once around the branch chain.
    /// Mirrors <see cref="EmitSubscriptGetterBody"/>'s fan-out shape.
    /// </summary>
    private void EmitMethodFanOutBody(SwiftWriter writer, MethodDecl method, string ownerProtoName,
        IReadOnlyList<(ProtocolDecl Proto, int Index)> branches,
        IReadOnlyList<string> argPassList, IReadOnlyList<string> writebackLines, string argRefs,
        bool hasReturn, bool isStringMethodReturn, bool isObjCBridgeableReturn, bool isObjCBridgeableContainerReturn,
        string returnTypeNameForMetatype, string? optBridgeableValueInner,
        IReadOnlyList<AvailabilityAnnotation>? extensionAvailability)
    {
        // Param copies + ObjC bridge locals reference only the function arguments, so emit them
        // once before the branch chain (every branch passes the same &copy references).
        foreach (var line in argPassList)
            writer.WriteLine(line);

        if (hasReturn)
            writer.WriteLine("let resultPtr: UnsafeRawPointer");

        // Box `self` as the OWNER's protocol type for EVERY branch — the protocol whose extension this
        // body is emitted in (`ownerProtoName`, the caller's `protocolDecl`), NOT branches[0]. The
        // fan-out branch order is sorted sync-first (see ComputeMethodEmissionPlans), so in a mixed
        // async/sync sibling group branches[0] is the first *sync* participant, which need not be the
        // owner. The box's protocol type is BEHAVIORALLY IMMATERIAL to dispatch: every sibling in the
        // group is a same-module protocol EveryProtocol unconditionally conforms to (the non-owner
        // siblings emit an empty extension that borrows this very witness), so `self as any <sibling>`
        // always type-checks; and the C# receiver reads only word 0 of the existential container — the
        // class reference it looks the proxy up by — never the witness table, so word 0 is the identical
        // EveryProtocol instance pointer regardless of the box type. Boxing as the OWNER is therefore not
        // a correctness gate but the robust INVARIANT: the box reflects the protocol whose extension and
        // witness table back this code, rather than an arbitrary sync sibling that the sort happened to
        // place first. (For all-sync / all-async groups the sort is stable, so branches[0] already equals
        // the owner and output is byte-identical; only mixed groups see the box type change.)

        for (int i = 0; i < branches.Count; i++)
        {
            var (branchProto, branchIndex) = branches[i];
            var branchVtable = GetVtableInstanceName(branchProto);
            var branchField = GetMethodVtableFieldName(method, branchIndex);
            var clause = i == 0 ? "if" : "else if";
            var guard = BuildBranchGuardPrefix(branchProto, extensionAvailability);
            writer.WriteLine($"{clause} {guard}let fn = {branchVtable}.{branchField} {{");
            writer.Indent++;
            writer.WriteLine($"var selfProto: {ownerProtoName} = self");
            if (hasReturn)
                writer.WriteLine($"resultPtr = fn({branchVtable}.csVTHandle, &selfProto{argRefs})");
            else
                writer.WriteLine($"fn({branchVtable}.csVTHandle, &selfProto{argRefs})");
            writer.Indent--;
            writer.Write("} ");
        }
        writer.WriteLine("else {");
        writer.Indent++;
        writer.WriteLine($"fatalError(\"[SwiftBindings] EveryProtocol: no sibling vtable populated for method {method.Name}\")");
        writer.Indent--;
        writer.WriteLine("}");

        // Inout writeback runs after the dispatch, regardless of which branch fired.
        foreach (var wb in writebackLines)
            writer.WriteLine(wb);

        if (!hasReturn)
            return;

        if (isStringMethodReturn)
        {
            writer.WriteLines("""
                let slice = resultPtr.load(as: SBW_Utf8Slice.self)
                var str: Swift.String = ""
                if slice.len > 0 {
                    let buffer = UnsafeBufferPointer(start: slice.ptr, count: slice.len)
                    str = String(decoding: buffer, as: UTF8.self)
                }
                slice.ptr.deallocate()
                resultPtr.deallocate()
                return str
                """);
        }
        else if (isObjCBridgeableReturn || isObjCBridgeableContainerReturn)
        {
            // Scalar and whole-container ObjC returns both cross at +1 (takeRetainedValue);
            // see EmitPropertyGetterBody for the ownership rationale.
            const string take = "takeRetainedValue";
            writer.WriteLines($$"""
                let resultObjPtr = resultPtr.load(as: UnsafeRawPointer.self)
                resultPtr.deallocate()
                return Unmanaged<AnyObject>.fromOpaque(resultObjPtr).{{take}}() as! {{returnTypeNameForMetatype}}
                """);
        }
        else if (optBridgeableValueInner != null)
        {
            EmitOptionalObjCBridgeableValueReturn(writer, optBridgeableValueInner);
        }
        else
        {
            writer.WriteLine($"let __result = UnsafeMutableRawPointer(mutating: resultPtr).assumingMemoryBound(to: {returnTypeNameForMetatype}.self).move()");
            writer.WriteLine("resultPtr.deallocate()");
            writer.WriteLine("return __result");
        }
    }

    /// <summary>
    /// Emits the S13 Pillar C real-async reverse-dispatch witness body for a primitive-shaped
    /// <c>async throws</c> requirement — the genuine continuation handoff that REPLACES the legacy
    /// thread-blocking sync witness (<see cref="EmitMethodImplementation"/> + the C# receiver's
    /// <c>.GetAwaiter().GetResult()</c>). It emits a real <c>func m(...) async throws -&gt; T</c> that
    /// suspends on <c>withCheckedThrowingContinuation</c>, retains the continuation in the shared box,
    /// and hands the box pointer + the success/error resume function pointers to C# through the widened
    /// Start-thunk vtable slot — the exact inverse of the forward async-closure handoff. The C# receiver
    /// (<c>EmitRealAsyncWitnessReceiver</c>) kicks the impl's <c>Task</c> off and resumes the box later
    /// via those function pointers, so this Swift thread suspends instead of blocking.
    /// <para>The continuation box (class + success/error <c>@_cdecl</c>) is the SAME per-(module, T,
    /// throwing) box the forward async-closure path emits — declared at file scope by the box pre-pass in
    /// <see cref="EmitProtocolExtension"/>, since a class/<c>@_cdecl</c> cannot nest in the extension
    /// method body — so this body only references its symbols via
    /// <see cref="ClosureEmitter.GetAsyncClosureBoxSymbols"/>. <see cref="EmitsRealAsyncWitness"/> gates
    /// this to the plain value-marshalled primitive shape (non-inout blittable-primitive params + return,
    /// no closures/generics/Self), so there are no ObjC-bridge / value-pointer / inout-writeback arms to
    /// reproduce here.</para>
    /// </summary>
    private void EmitRealAsyncWitnessImplementation(SwiftWriter writer, MethodDecl method,
        ProtocolDecl protocolDecl, string vtableInstanceName, int index, MethodEmissionPlan? plan = null,
        IReadOnlyList<AvailabilityAnnotation>? extensionAvailability = null)
    {
        var protocolName = protocolDecl.SwiftTypeName.ModuleQualifiedName;
        var fieldName = GetMethodVtableFieldName(method, index);

        // EmitsRealAsyncWitness guarantees a single blittable-primitive scalar return (no Void variant
        // in Phase 1/2), so returnType is non-null and renders to a concrete Swift primitive name.
        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var returnTypeName = GetSwiftTypeName(returnType!);

        // Same (module, T, throwing) triple the box pre-pass emitted the box under — so these symbol
        // names resolve to the file-scope box class + its success (and, when throwing, error) @_cdecl
        // functions. A non-throwing requirement boxes CheckedContinuation<T, Never> and emits NO _error
        // symbol, so its witness suspends on withCheckedContinuation and passes a sentinel error FP.
        var isThrowing = method.Throws;
        var moduleName = method.ModuleDecl?.Name ?? _moduleName;
        var (boxClassName, symbolRoot) = ClosureEmitter.GetAsyncClosureBoxSymbols(moduleName, returnTypeName, isThrowing);

        // Build the labeled parameter list and the matching `&copy` argument refs. Each param is a
        // non-inout blittable primitive (EmitsRealAsyncWitness), so the only arg form is `var copy`
        // passed by `&copy` — identical to EmitMethodImplementation's value-param else-branch, minus
        // the ObjC-bridge / explicit-value-pointer / inout-writeback cases it can never reach.
        var parameters = new List<string>();
        var argCopyLines = new List<string>();
        var argRefList = new List<string>();
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            var param = method.CSSignature[i];
            if (DefaultParameterOverloadEmitter.IsDebugParameter(param) || param.SwiftTypeSpec.IsEmptyTuple)
                continue;
            var paramTypeName = GetSwiftTypeNameForDeclaration(param.SwiftTypeSpec);
            var externalLabel = GetSwiftParameterLabel(param, i);
            var internalName = GetSwiftParameterName(param, i);
            var escaped = NameProvider.EscapeSwiftKeyword(internalName);
            if (externalLabel == "_")
                parameters.Add($"_ {internalName}: {paramTypeName}");
            else if (externalLabel == internalName)
                parameters.Add($"{internalName}: {paramTypeName}");
            else
                parameters.Add($"{externalLabel} {internalName}: {paramTypeName}");
            argCopyLines.Add($"var {internalName}Copy = {escaped}");
            argRefList.Add($"&{internalName}Copy");
        }
        var parametersString = string.Join(", ", parameters);
        var argRefs = argRefList.Count > 0 ? ", " + string.Join(", ", argRefList) : "";

        // Effect clause + continuation kind track method.Throws: a throwing requirement suspends on
        // withCheckedThrowingContinuation (CheckedContinuation<T, Error>) and is itself `async throws`;
        // a non-throwing one suspends on withCheckedContinuation (CheckedContinuation<T, Never>) and is
        // plain `async`. The vtable slot's trailing error-FP stays in the ABI either way (the slot width
        // is throwing-agnostic, +3) — the non-throwing witness fills it with a never-dereferenced
        // sentinel, since its box emits no _error symbol.
        var effectClause = isThrowing ? "async throws" : "async";
        var continuationFn = isThrowing ? "withCheckedThrowingContinuation" : "withCheckedContinuation";
        var continuationErr = isThrowing ? "Swift.Error" : "Never";
        var awaitExpr = isThrowing ? "return try await" : "return await";

        // Resolve dispatch branches — mirror EmitMethodImplementation / EmitMethodFanOutBody. A solo
        // group (no same-signature siblings) keeps the byte-identical single force-unwrap of THIS
        // protocol's own widened slot. A real sibling group (two+ protocols declaring the same
        // real-async signature) fans out across each sibling's per-protocol widened vtable slot,
        // dispatching through the FIRST one whose function pointer is non-nil — so a C# impl conforming
        // to only a non-owner sibling lands on ITS populated vtable instead of the owner's nil global
        // vtable, which the force-unwrap would otherwise SIGSEGV on (exactly the Bug #2 crash class the
        // sync witness already fans out to avoid). HasFilteredPeers forces the nil-check fan-out even
        // for a single emitted branch. Only the OWNER reaches this body (gated on plan.Owner upstream),
        // so `protocolDecl`/`protocolName` is the owner — `self` is boxed as the owner protocol type for
        // every branch, matching EmitMethodFanOutBody (the C# receiver reads only word 0 of the
        // existential, never the witness table, so the box type is behaviorally immaterial).
        IReadOnlyList<(ProtocolDecl Proto, int Index)> branches = plan?.Siblings.Count > 1
            ? plan.Siblings
            : new[] { (protocolDecl, index) };
        bool forceSafeFanOut = plan?.HasFilteredPeers == true;

        writer.WriteLine($"public func {NameProvider.ParserNameToSwift(method)}({parametersString}) {effectClause} -> {returnTypeName} {{");
        writer.Indent++;

        // Suspend and hand the continuation to C#. The continuation closure is non-escaping and
        // non-@Sendable, so capturing `self` (the EveryProtocol instance) and the primitive args is
        // fine; everything inside it is synchronous and local. The C# receiver reads word 0 of
        // `&selfProto` (the class reference) to look the proxy up — identical to the sync witness — and
        // returns immediately after spawning the impl's Task; the box resumes `__cont` exactly once.
        writer.WriteLine($"{awaitExpr} {continuationFn} {{ (__cont: {SwiftConcurrencyNames.CheckedContinuation}<{returnTypeName}, {continuationErr}>) in");
        writer.Indent++;
        writer.WriteLine($"var selfProto: {protocolName} = self");
        foreach (var line in argCopyLines)
            writer.WriteLine(line);
        writer.WriteLine($"let __box = {boxClassName}(__cont)");
        writer.WriteLine("let __boxPtr = Unmanaged.passRetained(__box).toOpaque()");
        writer.WriteLine($"let __successFP = unsafeBitCast({symbolRoot}_success as @convention(c) (UnsafeMutableRawPointer, UnsafeMutableRawPointer) -> Void, to: UnsafeRawPointer.self)");
        if (isThrowing)
            writer.WriteLine($"let __errorFP = unsafeBitCast({symbolRoot}_error as @convention(c) (UnsafeMutableRawPointer, UnsafePointer<CChar>) -> Void, to: UnsafeRawPointer.self)");
        else
            // Non-throwing box has no _error symbol; the slot's error-FP is never invoked (the C#
            // receiver FailFasts on a fault instead of resuming-with-error), so a non-null sentinel
            // keeps the ABI uniform without referencing a missing symbol.
            writer.WriteLine("let __errorFP = UnsafeRawPointer(bitPattern: 1)!");

        // The box + resume FPs + arg copies above are handle-INDEPENDENT (built once); only the vtable
        // instance/field differ per branch, so the dispatch is the sole per-branch fragment.
        if (branches.Count == 1 && !forceSafeFanOut)
        {
            // Solo group: byte-identical single force-unwrap of this protocol's own widened slot.
            writer.WriteLine($"{vtableInstanceName}.{fieldName}!({vtableInstanceName}.csVTHandle, &selfProto{argRefs}, __boxPtr, __successFP, __errorFP)");
        }
        else
        {
            // Sibling fan-out: dispatch through the first sibling whose widened slot is non-nil. Each
            // branch reads func_{name}_{Index} off that sibling's per-protocol global vtable. The
            // branch list was filtered to layout-included slots (ProtocolVtableMembers.IncludesMethod)
            // when the plan was built, so no branch references a missing member. A box retained but
            // never handed off (no branch fires) leaks, but fatalError
            // terminates — matching EmitMethodFanOutBody's unrecoverable-invariant fallback.
            for (int i = 0; i < branches.Count; i++)
            {
                var (branchProto, branchIndex) = branches[i];
                var branchVtable = GetVtableInstanceName(branchProto);
                var branchField = GetMethodVtableFieldName(method, branchIndex);
                var clause = i == 0 ? "if" : "else if";
                var guard = BuildBranchGuardPrefix(branchProto, extensionAvailability);
                writer.WriteLine($"{clause} {guard}let fn = {branchVtable}.{branchField} {{");
                writer.Indent++;
                writer.WriteLine($"fn({branchVtable}.csVTHandle, &selfProto{argRefs}, __boxPtr, __successFP, __errorFP)");
                writer.Indent--;
                writer.Write("} ");
            }
            writer.WriteLine("else {");
            writer.Indent++;
            writer.WriteLine($"fatalError(\"[SwiftBindings] EveryProtocol: no sibling vtable populated for method {method.Name}\")");
            writer.Indent--;
            writer.WriteLine("}");
        }
        writer.Indent--;
        writer.WriteLine("}");

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    /// <summary>
    /// Emits the Swift extension body for a dispatchable closure-receiving method.
    /// Extracts the closure's `(fnPtr, ctx)` pair via `unsafeBitCast`, retains the context so
    /// the C# side can outlive this Swift call, and forwards to the expanded cdecl trampoline
    /// (`vtable, &selfProto, fnPtr, ctx, [more closure pairs…]`). The C# receiver wraps the
    /// pair into a managed delegate via a per-shape `@_cdecl` invoke thunk.
    /// </summary>
    private void EmitClosureMethodImplementation(SwiftWriter writer, MethodDecl method, ProtocolDecl protocolDecl,
        string vtableInstanceName, int index, ClosureHandler closureHandler,
        MethodEmissionPlan? plan = null, IReadOnlyList<AvailabilityAnnotation>? extensionAvailability = null)
    {
        // Build parameter list — same shape as EmitMethodImplementation so the Swift protocol
        // signature matches the requirement. Only closure params get expanded passing logic.
        var parameters = new List<string>();
        var internalNames = new List<string>();
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            var param = method.CSSignature[i];
            var paramTypeName = GetSwiftTypeNameForDeclaration(param.SwiftTypeSpec);
            var externalLabel = GetSwiftParameterLabel(param, i);
            var internalName = GetSwiftParameterName(param, i);
            internalNames.Add(internalName);

            // Closure params are escaping — they outlive this call. Other params follow
            // the same ownership rules as EmitMethodImplementation.
            var ownershipPrefix = param.IsInOut
                ? "inout "
                : (WrapperValidation.IsNonCopyableType(param.SwiftTypeSpec, _typeDatabase, method.ModuleDecl)
                    ? "consuming "
                    : "");

            // For closure params, render the type with @escaping so the parameter is escaping
            // (otherwise Swift would reject capturing the closure for ARC retain).
            string renderedType = paramTypeName;
            if (param.SwiftTypeSpec is ClosureTypeSpec cts && cts.IsEscaping && !paramTypeName.Contains("@escaping"))
                renderedType = $"@escaping {paramTypeName}";

            if (externalLabel == "_")
                parameters.Add($"_ {internalName}: {ownershipPrefix}{renderedType}");
            else if (externalLabel == internalName)
                parameters.Add($"{internalName}: {ownershipPrefix}{renderedType}");
            else
                parameters.Add($"{externalLabel} {internalName}: {ownershipPrefix}{renderedType}");
        }
        var parametersString = string.Join(", ", parameters);

        var fieldName = GetMethodVtableFieldName(method, index);

        writer.WriteLine($"public func {NameProvider.ParserNameToSwift(method)}({parametersString}) {{");
        writer.Indent++;

        // Build the per-parameter passing code. For each closure param, emit the
        // (fnPtr, ctx) extraction; `Optional<Closure>` adds a nil branch that passes
        // `UnsafeRawPointer(bitPattern: 0)` for both slots when the optional is .none.
        // Non-closure params follow the standard "copy + ref" pattern.
        var passLines = new List<string>();
        var argRefList = new List<string>();
        for (int i = 0; i < internalNames.Count; i++)
        {
            var paramName = internalNames[i];
            var escapedParam = NameProvider.EscapeSwiftKeyword(paramName);
            var param = method.CSSignature[i + 1];
            var isRegularDispatchable = TryGetDispatchableClosureParam(param.SwiftTypeSpec, closureHandler, out _, out var isOptional);
            var isAsyncParam = IsDispatchableAsyncClosureParam(param.SwiftTypeSpec, closureHandler, out _);
            if (isRegularDispatchable || isAsyncParam)
            {
                // For async closures the (fnPtr, ctx) byte-extraction shape is identical
                // to the sync path — the only difference is on the C# side and in the
                // @_cdecl invoke thunk body; the Swift extension just forwards the pair.
                if (isAsyncParam)
                    isOptional = false;
                // Extract the raw (fp, ctx) bytes from the closure parameter without
                // triggering Swift's generic-abstraction reabstraction thunk.
                //
                // Trap: passing a concrete `@escaping () -> Void` to ANY generic helper —
                // including `unsafeBitCast<T, U>(_:to:)`, `withUnsafeBytes(of: T, ...)`,
                // or `MemoryLayout<T>` against a non-inout T — forces Swift to convert
                // the in-register closure (`Ieg_`) into the generic memory abstraction
                // (`Iegr_`, @out-Void). The compiler implements that conversion by
                // allocating a fresh partial-application context, wrapping the original
                // (fn, ctx) inside, and substituting `$sIeg_ytIegr_TRTA` for the function
                // pointer. The temporary partial-app context is then released as soon as
                // the generic call returns, so the (fn, ctx) bytes that surface in the
                // result point at freed memory. C# stores those dangling pointers and
                // the InvCR-thunk call later SIGSEGVs deep inside `$sIeg_ytIegr_TR`.
                //
                // Inout-binding through `withUnsafeBytes(of: inout T, _: ...)` sidesteps
                // this: the inout parameter passes the address of the existing storage
                // straight through, so the closure value never gets materialized in
                // generic form. The bytes read out of the buffer are the original
                // (Ieg_ fn pointer, original ctx) pair.
                //
                // ARC: `handler` retains its context for the whole scope of this
                // extension; the vtable call is synchronous, so C# runs
                // `SwiftEscapingClosure.FromSwift` (which calls `Arc.Retain`) before we
                // return. When `handler` releases on exit, the context survives via C#'s
                // retain.
                var fnVar = $"{paramName}FnPtr";
                var ctxVar = $"{paramName}CtxPtr";
                var localVar = $"{paramName}Local";
                if (isOptional)
                {
                    // Optional<Closure> shares the closure's 2-word layout via the
                    // function-pointer extra-inhabitant: `.some(closure)` stores
                    // (fnPtr, ctxPtr); `.none` stores (nil, _). Read raw bytes as
                    // Optional pointers directly — DO NOT unwrap into a local
                    // `() -> Void`.
                    //
                    // Why: unwrapping via `if var localVar = param` copies the
                    // Optional payload into a concrete `() -> Void`. The Optional's
                    // payload storage holds the closure in the generically-abstracted
                    // form (`Iegr_`, @out-Void) — assigning to a concrete `Ieg_`
                    // local triggers the exact `$sIeg_ytIegr_TR` reabstraction trap
                    // described above: Swift inserts a partial-application context
                    // and `localVar` ends up holding `(reabstraction-thunk fnPtr,
                    // partial-app ctx)`. That partial-app ctx is deallocated as
                    // soon as the unwrap scope exits, so the (fn, ctx) bytes captured
                    // here point at freed memory and SIGSEGV inside `$sIeg_ytIegr_TR`
                    // when the C# side later invokes the InvCR thunk.
                    //
                    // Inout-binding directly on the Optional value sidesteps the
                    // reabstraction the same way it does for the concrete case:
                    // the storage's bytes flow through untouched. The vtable signature
                    // uses `UnsafeRawPointer?` for these slots so `.none`'s null
                    // fnPtr round-trips as `IntPtr.Zero` on the C# trampoline.
                    passLines.Add($"var {localVar} = {escapedParam}");
                    passLines.Add(
                        $"let ({fnVar}, {ctxVar}): (UnsafeRawPointer?, UnsafeRawPointer?) = withUnsafeBytes(of: &{localVar}) {{ _bytes in" +
                        " return (" +
                        "_bytes.load(as: UnsafeRawPointer?.self), " +
                        "_bytes.load(fromByteOffset: MemoryLayout<UnsafeRawPointer>.size, as: UnsafeRawPointer?.self)" +
                        ") }");
                    argRefList.Add(fnVar);
                    argRefList.Add(ctxVar);
                }
                else
                {
                    passLines.Add($"var {localVar} = {escapedParam}");
                    passLines.Add(
                        $"let ({fnVar}, {ctxVar}) = withUnsafeBytes(of: &{localVar}) {{ _bytes -> (UnsafeRawPointer, UnsafeRawPointer) in" +
                        " return (" +
                        "_bytes.load(as: UnsafeRawPointer.self), " +
                        "_bytes.load(fromByteOffset: MemoryLayout<UnsafeRawPointer>.size, as: UnsafeRawPointer.self)" +
                        ") }");
                    argRefList.Add(fnVar);
                    argRefList.Add(ctxVar);
                }
            }
            else if (IsObjCBridgeableParam(param.SwiftTypeSpec))
            {
                // ObjC-bridgeable value types (URL, URLRequest, Data, Date, …) must be bridged
                // to their ObjC object and passed as an opaque reference: the proxy receiver
                // reads the slot via `MarshalFromSwift<IntPtr>` then `GetNSObject<T>`, so it
                // expects an ObjC pointer, NOT the raw Swift struct bytes. The plain `&{p}Copy`
                // form below would hand the receiver the value bytes, which it then misreads as
                // an ObjC pointer → corruption. Mirror the non-closure method fan-out's
                // ObjC-bridgeable arm (see EmitMethodImplementation; IsObjCBridgeableParam) — but
                // WITHOUT its `argWritebackSources` entry, since the dispatchable-closure path
                // rejects inout params (IsDispatchableClosureMethod) and never writes back.
                // Reachable for the gate-admitted shape `f(amount: Decimal, completion: @escaping ...)`:
                // one dispatchable closure param plus non-closure value params. Pinned by
                // TestAmountProcessorProxy_ObjCBridgeableParamRoundTrips, which uses Decimal (not
                // URL): URL is NSURL-backed, so its first word IS the bridged pointer and the buggy
                // `&urlCopy` path accidentally survives; Decimal's first word is mantissa data, so
                // the buggy path reads it as an ObjC pointer and crashes — a genuine guard.
                passLines.Add($"let {paramName}NS = {escapedParam} as AnyObject");
                passLines.Add($"var {paramName}Ref = Unmanaged.passUnretained({paramName}NS).toOpaque()");
                argRefList.Add($"&{paramName}Ref");
            }
            else if (GetOptionalObjCBridgeableValueInnerName(param.SwiftTypeSpec) is not null)
            {
                // Optional ObjC-bridgeable VALUE param (URL?) alongside a dispatchable closure: the same
                // single optional ObjC POINTER word (nil = 0x0) as the non-closure method fan-out's
                // optional arm, borrowing (+0) the bridged NSObject. `{p}NS` (Optional<AnyObject>) holds it
                // alive across the call; `{p}Ref` (Optional<UnsafeMutableRawPointer>) is the nil-optimized
                // word the receiver reads as a bare IntPtr. No writeback entry (this path rejects inout).
                // The plain `&{p}Copy` else would pass the multi-word Optional<URL> bytes → receiver misreads
                // one word → layout mismatch.
                passLines.Add($"let {paramName}NS = {escapedParam}.map {{ $0 as AnyObject }}");
                passLines.Add($"var {paramName}Ref = {paramName}NS.map {{ Unmanaged.passUnretained($0).toOpaque() }}");
                argRefList.Add($"&{paramName}Ref");
            }
            else if (RequiresExplicitValuePointer(param.SwiftTypeSpec))
            {
                // `&array` / `&string` passed to an UnsafeRawPointer vtable parameter triggers
                // Swift's implicit array/string-to-pointer conversion, handing the C# receiver a
                // pointer to the element buffer / UTF-8 bytes instead of the value. Route through
                // an explicitly-typed pointer so the receiver reads the value — same treatment as
                // the non-closure method fan-out (see EmitMethodImplementation) and
                // RequiresExplicitValuePointer. Reachable here for the closure-method shape
                // the gate (IsDispatchableClosureMethod) explicitly admits: one dispatchable closure
                // param plus zero or more non-closure value params. (A top-level Array value param
                // crashes without this; pinned by TestTagBatchProcessorProxy_ArrayParamRoundTrips.)
                var ptrName = $"{paramName}CopyPtr";
                passLines.AddRange(BuildValueStorageSetup(ptrName, GetSwiftTypeName(param.SwiftTypeSpec), escapedParam));
                argRefList.Add($"UnsafeRawPointer({ptrName})");
            }
            else
            {
                // Non-closure params follow the standard "copy + ref" pattern.
                passLines.Add($"var {paramName}Copy = {escapedParam}");
                argRefList.Add($"&{paramName}Copy");
            }
        }

        var argRefs = argRefList.Count > 0 ? ", " + string.Join(", ", argRefList) : "";

        // Resolve dispatch branches — same sibling fan-out as EmitMethodImplementation. A solo group
        // (no same-signature siblings) keeps the historic single-branch `!(...)` shape, byte-identical
        // to the pre-fan-out output. A real sibling group fans out across each sibling's per-protocol
        // vtable index, picking the first whose function pointer is non-nil, so a C# impl conforming to
        // only a NON-owner protocol dispatches through ITS populated vtable instead of force-unwrapping
        // the owner's nil global vtable (which `!` would otherwise SIGSEGV on). This is the closure-param
        // counterpart of the fix in EmitMethodFanOutBody: two protocols sharing a closure-param method
        // signature (e.g. `func applyFactory(_ factory: @escaping () -> Int32)`) have a single
        // EveryProtocol witness satisfying both, and dispatch through the peer existential must not
        // assume the owner vtable is populated.
        IReadOnlyList<(ProtocolDecl Proto, int Index)> branches = plan?.Siblings.Count > 1
            ? plan.Siblings
            : new[] { (protocolDecl, index) };
        bool forceSafeFanOut = plan?.HasFilteredPeers == true;

        // `selfProto` box and the closure (fnPtr, ctx) extraction reference only `self` and the
        // function arguments — both handle-independent — so emit them once before the branch chain.
        writer.WriteLine($"var selfProto: {protocolDecl.SwiftTypeName.ModuleQualifiedName} = self");
        foreach (var line in passLines)
            writer.WriteLine(line);

        if (branches.Count == 1 && !forceSafeFanOut)
        {
            writer.WriteLines($$"""
                {{vtableInstanceName}}.{{fieldName}}!(
                    {{vtableInstanceName}}.csVTHandle, &selfProto{{argRefs}})
                """);
        }
        else
        {
            for (int i = 0; i < branches.Count; i++)
            {
                var (branchProto, branchIndex) = branches[i];
                var branchVtable = GetVtableInstanceName(branchProto);
                var branchField = GetMethodVtableFieldName(method, branchIndex);
                var clause = i == 0 ? "if" : "else if";
                var guard = BuildBranchGuardPrefix(branchProto, extensionAvailability);
                writer.WriteLine($"{clause} {guard}let fn = {branchVtable}.{branchField} {{");
                writer.Indent++;
                writer.WriteLine($"fn({branchVtable}.csVTHandle, &selfProto{argRefs})");
                writer.Indent--;
                writer.Write("} ");
            }
            writer.WriteLine("else {");
            writer.Indent++;
            writer.WriteLine($"fatalError(\"[SwiftBindings] EveryProtocol: no sibling vtable populated for closure method {method.Name}\")");
            writer.Indent--;
            writer.WriteLine("}");
        }

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    /// <summary>
    /// Emits the (fnPtr, ctx) vtable slot pair for a dispatchable closure property.
    /// Mirrors the closure-method param shape: each accessor takes
    /// (or returns) two pointer-width slots — function pointer and context — instead
    /// of the single <c>UnsafeRawPointer</c> used for value-shaped properties.
    /// <para>
    /// Setter slot signature:
    /// <c>@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer?, UnsafeRawPointer?) -&gt; Void</c>
    /// (handle, self, fnPtr, ctxPtr). Optional vs non-Optional closures share the same
    /// signature — non-Optional cannot be nil at the source level, but Swift's
    /// <c>UnsafeRawPointer?</c> permits writing a nil through the same field.
    /// </para>
    /// <para>
    /// Getter slot signature:
    /// <c>@convention(c)(OpaquePointer?, UnsafeRawPointer) -&gt; UnsafeRawPointer</c>
    /// — same as the existing value-property getter shape; the returned pointer
    /// targets a 16-byte buffer containing (fnPtr, ctxPtr) read by the Swift
    /// adapter materialiser.
    /// </para>
    /// </summary>
    private void EmitDispatchableClosurePropertyVtableFields(SwiftWriter writer, PropertyDecl property,
        ClosureHandler closureHandler, HashSet<string> emittedFields)
    {
        var hasGetter = property.Accessors.OfType<GetAccessorDecl>().Any();
        var hasSetter = property.Accessors.OfType<SetAccessorDecl>().Any();

        if (hasGetter)
        {
            var fieldName = $"func_{property.Name}_get";
            if (emittedFields.Add(fieldName))
            {
                var funcType = "(@convention(c)(OpaquePointer?, UnsafeRawPointer) -> UnsafeRawPointer)?";
                writer.WriteLine($"var {fieldName}: {funcType}");
            }
        }
        if (hasSetter)
        {
            var fieldName = $"func_{property.Name}_set";
            if (emittedFields.Add(fieldName))
            {
                var funcType = "(@convention(c)(OpaquePointer?, UnsafeRawPointer, UnsafeRawPointer?, UnsafeRawPointer?) -> Void)?";
                writer.WriteLine($"var {fieldName}: {funcType}");
            }
        }
    }

    /// <summary>
    /// Emits the Swift property implementation for a dispatchable closure property.
    /// Setter extracts (fnPtr, ctx) bytes via the
    /// reabstraction-trap-safe inout-bytes pattern (see
    /// <c>feedback_optional_closure_reabstraction</c>) and forwards through the vtable;
    /// getter calls the vtable and materialises a Swift closure value from the returned
    /// (fnPtr, ctx) buffer using the <c>_sbWrapClosureContext</c> Swift-ARC owner-token
    /// box.
    /// </summary>
    private void EmitDispatchableClosurePropertyImplementation(SwiftWriter writer, PropertyDecl property,
        ProtocolDecl protocolDecl, string vtableInstanceName, ClosureHandler closureHandler,
        PropertyEmissionPlan? plan = null,
        IReadOnlyList<AvailabilityAnnotation>? extensionAvailability = null)
    {
        var hasGetter = property.Accessors.OfType<GetAccessorDecl>().Any();
        var hasSetter = property.Accessors.OfType<SetAccessorDecl>().Any();
        var swiftTypeName = GetSwiftTypeNameForDeclaration(property.SwiftTypeSpec);
        var hasClosure = TryGetDispatchableClosureParam(property.SwiftTypeSpec, closureHandler, out var closure, out var isOptional);
        if (!hasClosure || closure is null)
            throw new InvalidOperationException(
                $"EmitDispatchableClosurePropertyImplementation called on non-dispatchable property '{property.Name}'.");

        // Same sibling fan-out shape as the value-typed property path (see
        // EmitPropertyImplementation / EmitPropertyGetterBody): when the
        // property belongs to a sibling group, the owner body walks each
        // sibling's vtable and dispatches through whichever function pointer
        // the registered proxy populated. Without this the owner force-unwraps
        // its own nil pointer when a smaller-sibling proxy is in play.
        IReadOnlyList<ProtocolDecl> getterBranches = plan?.GetterSiblings.Count > 1
            ? plan.GetterSiblings
            : new[] { protocolDecl };
        IReadOnlyList<ProtocolDecl> setterBranches = plan?.SetterSiblings.Count > 1
            ? plan.SetterSiblings
            : new[] { protocolDecl };
        // Mirror of EmitPropertyImplementation safety net — when peers were filtered out
        // of the plan (e.g. mixed-generic siblings) the owner must use the nil-check
        // fan-out shape even for a single emittable branch.
        bool forceSafeFanOut = plan?.HasFilteredPeers == true;

        writer.WriteLine($"public var {NameProvider.ParserNameToSwift(property)}: {swiftTypeName} {{");
        writer.Indent++;

        if (hasGetter)
        {
            var conventionCType = ClosureEmitter.GetSwiftConventionCType(closure, closureHandler);

            writer.WriteLine("get {");
            writer.Indent++;
            if (getterBranches.Count == 1 && !forceSafeFanOut)
            {
                var soloProto = getterBranches[0].SwiftTypeName.ModuleQualifiedName;
                var soloVtable = GetVtableInstanceName(getterBranches[0]);
                writer.WriteLine($"var selfProto: {soloProto} = self");
                writer.WriteLine($"let resultPtr = {soloVtable}.func_{property.Name}_get!(");
                writer.WriteLine($"    {soloVtable}.csVTHandle, &selfProto)");
            }
            else
            {
                writer.WriteLine("let resultPtr: UnsafeRawPointer");
                // Owner-typed box for every branch — see EmitMethodFanOutBody for the rationale.
                var ownerProto = getterBranches[0].SwiftTypeName.ModuleQualifiedName;
                for (int i = 0; i < getterBranches.Count; i++)
                {
                    var branch = getterBranches[i];
                    var branchVtable = GetVtableInstanceName(branch);
                    var clause = i == 0 ? "if" : "else if";
                    var guard = BuildBranchGuardPrefix(branch, extensionAvailability);
                    writer.WriteLine($"{clause} {guard}let fn = {branchVtable}.func_{property.Name}_get {{");
                    writer.Indent++;
                    writer.WriteLine($"var selfProto: {ownerProto} = self");
                    writer.WriteLine($"resultPtr = fn({branchVtable}.csVTHandle, &selfProto)");
                    writer.Indent--;
                    writer.Write("} ");
                }
                writer.WriteLine("else {");
                writer.Indent++;
                writer.WriteLine($"fatalError(\"[SwiftBindings] EveryProtocol: no sibling vtable populated for closure property '{property.Name}' getter\")");
                writer.Indent--;
                writer.WriteLine("}");
            }
            writer.WriteLines("""
                let fnPtrSlot = resultPtr.load(as: UnsafeRawPointer?.self)
                let ctxPtrSlot = resultPtr.load(fromByteOffset: MemoryLayout<UnsafeRawPointer>.size, as: UnsafeMutableRawPointer?.self)
                resultPtr.deallocate()
                """);
            if (isOptional)
                writer.WriteLine("guard let _fnPtr = fnPtrSlot else { return nil }");
            else
                writer.WriteLine("guard let _fnPtr = fnPtrSlot else { fatalError(\"[SwiftBindings] EveryProtocol: closure property '" + property.Name + "' getter returned nil function pointer\") }");
            writer.WriteLine($"let _ctxPtr: UnsafeMutableRawPointer? = ctxPtrSlot");
            writer.WriteLine($"let _box: AnyObject? = ctxPtrSlot.map {{ {ClosureContextHelperEmitter.WrapFunctionName}($0) }}");
            writer.WriteLine($"let _cdecl = unsafeBitCast(_fnPtr, to: ({conventionCType}).self)");
            EmitDispatchableClosureGetterAdapter(writer, closure, closureHandler);
            writer.Indent--;
            writer.WriteLine("}");
        }

        if (hasSetter)
        {
            writer.WriteLine("set {");
            writer.Indent++;
            writer.WriteLines("""
                var newValueLocal = newValue
                let (_fnPtr, _ctxPtr): (UnsafeRawPointer?, UnsafeRawPointer?) = withUnsafeBytes(of: &newValueLocal) { _bytes in
                    return (
                        _bytes.load(as: UnsafeRawPointer?.self),
                        _bytes.load(fromByteOffset: MemoryLayout<UnsafeRawPointer>.size, as: UnsafeRawPointer?.self)
                    )
                }
                """);
            if (setterBranches.Count == 1 && !forceSafeFanOut)
            {
                var soloProto = setterBranches[0].SwiftTypeName.ModuleQualifiedName;
                var soloVtable = GetVtableInstanceName(setterBranches[0]);
                writer.WriteLine($"var selfProto: {soloProto} = self");
                writer.WriteLine($"{soloVtable}.func_{property.Name}_set!(");
                writer.WriteLine($"    {soloVtable}.csVTHandle, &selfProto, _fnPtr, _ctxPtr)");
            }
            else
            {
                // Owner-typed box for every branch — see EmitMethodFanOutBody for the rationale.
                var ownerProto = setterBranches[0].SwiftTypeName.ModuleQualifiedName;
                for (int i = 0; i < setterBranches.Count; i++)
                {
                    var branch = setterBranches[i];
                    var branchVtable = GetVtableInstanceName(branch);
                    var clause = i == 0 ? "if" : "else if";
                    var guard = BuildBranchGuardPrefix(branch, extensionAvailability);
                    writer.WriteLine($"{clause} {guard}let fn = {branchVtable}.func_{property.Name}_set {{");
                    writer.Indent++;
                    writer.WriteLine($"var selfProto: {ownerProto} = self");
                    writer.WriteLine($"fn({branchVtable}.csVTHandle, &selfProto, _fnPtr, _ctxPtr)");
                    writer.Indent--;
                    writer.Write("} ");
                }
                writer.WriteLine();
            }
            writer.Indent--;
            writer.WriteLine("}");
        }

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    /// <summary>
    /// Emits the Swift method implementation for a closure-returning protocol method.
    /// Mirrors <see cref="EmitDispatchableClosurePropertyImplementation"/>'s getter — the
    /// only structural difference is that this is a method (`func`) not a computed
    /// property, and there are no method parameters by this shape's gate.
    /// </summary>
    private void EmitDispatchableClosureReturningMethodImplementation(SwiftWriter writer, MethodDecl method,
        ProtocolDecl protocolDecl, string vtableInstanceName, int methodIdx, ClosureHandler closureHandler,
        MethodEmissionPlan? plan = null, IReadOnlyList<AvailabilityAnnotation>? extensionAvailability = null)
    {
        if (method.CSSignature.FirstOrDefault()?.SwiftTypeSpec is not ClosureTypeSpec retClosure)
            throw new InvalidOperationException(
                $"EmitDispatchableClosureReturningMethodImplementation called on method '{method.Name}' without a closure return type.");

        var protocolName = protocolDecl.SwiftTypeName.ModuleQualifiedName;
        var conventionCType = ClosureEmitter.GetSwiftConventionCType(retClosure, closureHandler);
        var swiftReturnTypeName = GetSwiftTypeNameForDeclaration(retClosure);

        // Same sibling fan-out as the closure-param / value-method paths: a same-signature
        // sibling group must walk each sibling's vtable and dispatch through whichever the
        // registered proxy populated, rather than force-unwrapping the owner's nil global
        // vtable when a smaller-sibling proxy is in play. A solo group keeps the historic
        // single-branch shape, byte-identical to the pre-fan-out output.
        IReadOnlyList<(ProtocolDecl Proto, int Index)> branches = plan?.Siblings.Count > 1
            ? plan.Siblings
            : new[] { (protocolDecl, methodIdx) };
        bool forceSafeFanOut = plan?.HasFilteredPeers == true;

        writer.WriteLine($"public func {NameProvider.ParserNameToSwift(method)}() -> {swiftReturnTypeName} {{");
        writer.Indent++;
        if (branches.Count == 1 && !forceSafeFanOut)
        {
            writer.WriteLines($$"""
                var selfProto: {{protocolName}} = self
                let resultPtr = {{vtableInstanceName}}.{{GetMethodVtableFieldName(method, methodIdx)}}!(
                    {{vtableInstanceName}}.csVTHandle, &selfProto)
                """);
        }
        else
        {
            writer.WriteLine("let resultPtr: UnsafeRawPointer");
            for (int i = 0; i < branches.Count; i++)
            {
                var (branchProto, branchIndex) = branches[i];
                var branchVtable = GetVtableInstanceName(branchProto);
                var branchField = GetMethodVtableFieldName(method, branchIndex);
                var clause = i == 0 ? "if" : "else if";
                var guard = BuildBranchGuardPrefix(branchProto, extensionAvailability);
                writer.WriteLine($"{clause} {guard}let fn = {branchVtable}.{branchField} {{");
                writer.Indent++;
                writer.WriteLine($"var selfProto: {protocolName} = self");
                writer.WriteLine($"resultPtr = fn({branchVtable}.csVTHandle, &selfProto)");
                writer.Indent--;
                writer.Write("} ");
            }
            writer.WriteLine("else {");
            writer.Indent++;
            writer.WriteLine($"fatalError(\"[SwiftBindings] EveryProtocol: no sibling vtable populated for closure-returning method {method.Name}\")");
            writer.Indent--;
            writer.WriteLine("}");
        }
        writer.WriteLines($$"""
            let fnPtrSlot = resultPtr.load(as: UnsafeRawPointer?.self)
            let ctxPtrSlot = resultPtr.load(fromByteOffset: MemoryLayout<UnsafeRawPointer>.size, as: UnsafeMutableRawPointer?.self)
            resultPtr.deallocate()
            """);
        writer.WriteLine("guard let _fnPtr = fnPtrSlot else { fatalError(\"[SwiftBindings] EveryProtocol: closure-returning method '" + method.Name + "' returned nil function pointer\") }");
        writer.WriteLine($"let _ctxPtr: UnsafeMutableRawPointer? = ctxPtrSlot");
        writer.WriteLine($"let _box: AnyObject? = ctxPtrSlot.map {{ {ClosureContextHelperEmitter.WrapFunctionName}($0) }}");
        writer.WriteLine($"let _cdecl = unsafeBitCast(_fnPtr, to: ({conventionCType}).self)");
        EmitDispatchableClosureGetterAdapter(writer, retClosure, closureHandler);
        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    /// <summary>
    /// Emits the Swift closure-value materialisation body for a dispatchable closure
    /// property getter. Produces an adapted Swift closure that invokes the C# cdecl
    /// thunk with the original (fnPtr, ctx) bytes captured under a <c>[_box]</c>
    /// capture list so Swift ARC keeps the owner-token box alive for the closure's
    /// lifetime. Mirrors the non-throwing/non-async happy path of
    /// <see cref="ClosureEmitter.GetSwiftClosureAdapterCode"/>; Shape 3 currently
    /// restricts the closure type to <c>() -&gt; Void</c> (see
    /// <see cref="IsDispatchableClosureProperty"/>) so the adapter body is the
    /// minimal Swift→cdecl call.
    /// </summary>
    private static void EmitDispatchableClosureGetterAdapter(SwiftWriter writer, ClosureTypeSpec closure, ClosureHandler closureHandler)
    {
        // Shape 3 restricts to () -> Void closure values: the materialised Swift closure
        // takes no arguments and returns Void, calling the cdecl thunk with the captured
        // context pointer.
        writer.WriteLine("let _adapted: () -> Void = { [_box] in");
        writer.Indent++;
        writer.WriteLine("_ = _box");
        writer.WriteLine("_cdecl(_ctxPtr)");
        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine("return _adapted");
    }

    /// <summary>
    /// Checks if a method has closure types (ClosureTypeSpec) in any parameter or return type.
    /// Methods with closure types can't be dispatched through the EveryProtocol vtable
    /// because closures aren't representable as UnsafeRawPointer in @convention(c) callbacks.
    /// These methods get fatalError() stubs to satisfy the protocol conformance.
    /// </summary>
    internal static bool HasClosureInMethodSignature(MethodDecl method)
    {
        // Check return type (CSSignature[0])
        if (method.CSSignature.Count > 0 && ContainsClosureType(method.CSSignature[0].SwiftTypeSpec))
            return true;

        // Check non-self parameters (skip return type at index 0)
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            if (ContainsClosureType(method.CSSignature[i].SwiftTypeSpec))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Recursively checks if a TypeSpec contains a ClosureTypeSpec.
    /// </summary>
    private static bool ContainsClosureType(TypeSpec? typeSpec)
    {
        if (typeSpec == null)
            return false;

        switch (typeSpec)
        {
            case ClosureTypeSpec:
                return true;

            case NamedTypeSpec namedType:
                return namedType.GenericParameters.Any(ContainsClosureType);

            case TupleTypeSpec tupleType:
                return tupleType.Elements.Any(e => ContainsClosureType(e));

            case ProtocolListTypeSpec protocolListType:
                return protocolListType.Protocols.Keys.Any(ContainsClosureType);

            default:
                return false;
        }
    }

    /// <summary>
    /// Checks if a property has closure types in its type spec.
    /// </summary>
    internal static bool HasClosureInPropertyType(PropertyDecl property)
    {
        return ContainsClosureType(property.SwiftTypeSpec);
    }

    // Vtable-slot membership is NOT re-derived here: the VtableLayout model (via
    // ProtocolVtableMembers.IncludesMethod / ClassifyMethod == Included) is the single source of truth
    // for whether a protocol method occupies a func_{name}_{idx} slot. Every walk that lays out, fills,
    // or fans out over that slot asks the layout, so no local predicate can drift from the struct that
    // actually emits the field.

    internal static bool IsDispatchableClosureMethod(MethodDecl method, ClosureHandler closureHandler)
    {
        if (method.IsAsync || method.Throws)
            return false;

        // Method-level return must be Void (the closure is the only "output" path).
        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var hasReturn = returnType != null && !returnType.IsEmptyTuple;
        if (hasReturn)
            return false;

        // No Self / method-level generics.
        if (HasSelfTypeParamInSignature(method) || HasOnlyMethodLevelGenerics(method))
            return false;

        // Accept exactly one dispatchable closure-bearing param (bare closure or
        // `Optional<Closure>`) plus zero or more non-closure value-shape params.
        // Multi-arg shapes marshal each non-closure param through the standard receiver
        // path and the dispatchable closure param through the (fnPtr, ctx) pair — same
        // machinery as the single-arg case, just iterated. Reject other closure shapes (async,
        // non-dispatchable, multi-closure) and inout — those need richer plumbing.
        var nonSelfParams = method.CSSignature.Skip(1).ToList();
        if (nonSelfParams.Count == 0)
            return false;

        int dispatchableClosureCount = 0;
        foreach (var p in nonSelfParams)
        {
            if (p.IsInOut)
                return false;
            if (TryGetDispatchableClosureParam(p.SwiftTypeSpec, closureHandler, out _, out _))
            {
                dispatchableClosureCount++;
                continue;
            }
            // Reject any remaining param that carries a closure anywhere in its type tree:
            // bare non-dispatchable closures, Optional<Closure>, async closures, and value
            // shapes that nest a closure (tuple/array/dictionary/Result with a closure
            // payload, etc.). The dispatch path can only marshal one (fnPtr, ctx) pair per
            // method; any other reachable closure would produce a vtable slot the proxy
            // receiver cannot wire.
            if (ContainsClosureType(p.SwiftTypeSpec))
                return false;
        }
        return dispatchableClosureCount == 1;
    }

    /// <summary>
    /// Returns true for protocol methods that take a single async closure parameter the
    /// dispatch path can handle. Scope is restricted to the
    /// minimum viable shape — bare closure (not Optional), zero args, primitive Int32
    /// return, non-throwing. The Swift @_cdecl invoke thunk wraps the closure body in
    /// <c>Task { let r = await closure(); completion(ctx, r) }</c>; the C# proxy
    /// surfaces the closure as <c>Func&lt;Task&lt;int&gt;&gt;</c> by allocating a
    /// <see cref="TaskCompletionSource{TResult}"/> and pinning it via GCHandle for
    /// the completion callback to resume.
    /// </summary>
    internal static bool IsDispatchableAsyncClosureMethod(MethodDecl method, ClosureHandler closureHandler)
    {
        if (method.IsAsync || method.Throws)
            return false;
        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        if (returnType != null && !returnType.IsEmptyTuple)
            return false;
        if (HasSelfTypeParamInSignature(method) || HasOnlyMethodLevelGenerics(method))
            return false;
        var nonSelfParams = method.CSSignature.Skip(1).ToList();
        if (nonSelfParams.Count != 1)
            return false;
        var only = nonSelfParams[0];
        return IsDispatchableAsyncClosureParam(only.SwiftTypeSpec, closureHandler, out _);
    }

    /// <summary>
    /// Gate for the async closure-param shape currently supported by EveryProtocol
    /// forward dispatch. Restricted to <c>@escaping () async -&gt; Int32</c>
    /// (no args, Int32 return, non-throwing). Multi-arg / non-Int32-return / throwing
    /// async closure shapes are out of scope and need richer Task-bridging machinery
    /// before they can be admitted.
    /// </summary>
    internal static bool IsDispatchableAsyncClosureParam(TypeSpec? paramType, ClosureHandler closureHandler, out ClosureTypeSpec? closure)
    {
        closure = null;
        if (paramType is not ClosureTypeSpec direct)
            return false;
        if (!direct.IsAsync)
            return false;
        if (!direct.IsEscaping)
            return false;
        if (direct.Throws)
            return false;
        if (direct.HasAttributes)
        {
            foreach (var attr in direct.Attributes)
            {
                if (attr.Name != "escaping")
                    return false;
            }
        }
        if (direct.EachArgument().Count() != 0)
            return false;
        // Return must be Int32 (the only primitive we currently bridge through the
        // async completion callback). Extending the gate to other primitives needs a
        // matching expansion of the completion-callback signature.
        if (direct.ReturnType is not NamedTypeSpec named || named.Name != "Swift.Int32")
            return false;
        closure = direct;
        return true;
    }

    /// <summary>
    /// Stable @_cdecl entry point name for the per-method async invoke thunk. Mirrors
    /// <see cref="GetProtocolClosureInvokeThunkEntryPoint"/> but suffixed with
    /// <c>_Async</c> so the symbol table makes the shape visible at audit time.
    /// </summary>
    internal static string GetProtocolAsyncClosureInvokeThunkEntryPoint(ProtocolDecl protocolDecl, MethodDecl method, int methodIdx, int argIdx)
    {
        return $"SBW_{protocolDecl.Name}_{method.Name}_m{methodIdx}_arg{argIdx}_AsyncInvCR";
    }

    /// <summary>
    /// Deterministic C# helper method name for the async invoke thunk's P/Invoke.
    /// Mirrors <see cref="GetProtocolClosureInvokeThunkHelperName"/>.
    /// </summary>
    internal static string GetProtocolAsyncClosureInvokeThunkHelperName(string entryPointName)
    {
        var hash = EmitterUtility.DeterministicHash8(entryPointName);
        return $"_InvokeAsyncClosureThunk_{hash}";
    }

    /// <summary>
    /// Enumerates dispatchable async closure parameters on a method. Yields
    /// (param, argIdx, closure) for each parameter whose <see cref="TypeSpec"/> passes
    /// <see cref="IsDispatchableAsyncClosureParam"/>.
    /// </summary>
    internal static IEnumerable<(ArgumentDecl Param, int ArgIdx, ClosureTypeSpec Closure)> EnumerateDispatchableAsyncClosureParams(MethodDecl method, ClosureHandler closureHandler)
    {
        int argIdx = 0;
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            var p = method.CSSignature[i];
            if (DefaultParameterOverloadEmitter.IsDebugParameter(p) || p.SwiftTypeSpec.IsEmptyTuple)
                continue;
            if (IsDispatchableAsyncClosureParam(p.SwiftTypeSpec, closureHandler, out var closure))
                yield return (p, argIdx, closure!);
            argIdx++;
        }
    }

    /// <summary>
    /// Returns true for protocol methods that take no closure parameters but whose
    /// return type is a dispatchable closure value. The Swift adapter calls into C#
    /// through the vtable, the C# proxy returns a (fnPtr, ctx) pair, and Swift wraps the
    /// pair into a real Swift closure via `_sbWrapClosureContext` for the caller to
    /// invoke later. Currently restricted to `() -&gt; Void` returns and zero non-Self
    /// params — the dispatch surface mirrors the dispatchable closure property getter.
    /// </summary>
    internal static bool IsDispatchableClosureReturningMethod(MethodDecl method, ClosureHandler closureHandler)
    {
        if (method.IsAsync || method.Throws)
            return false;
        if (HasSelfTypeParamInSignature(method) || HasOnlyMethodLevelGenerics(method))
            return false;

        // Return type must be a dispatchable closure shape (no Optional<Closure> for now —
        // the Swift adapter for a method return uses a non-optional shape).
        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        if (returnType is not ClosureTypeSpec returnClosure)
            return false;
        if (!IsDispatchableClosureShape(returnClosure, closureHandler))
            return false;

        // Shape 4 restricts to () -> Void return: the materialiser only knows how to
        // call a zero-arg void cdecl thunk. Other shapes need richer adapter bodies
        // before they can pass this gate.
        if (returnClosure.Throws || returnClosure.IsAsync)
            return false;
        if (returnClosure.EachArgument().Count() != 0)
            return false;
        if (!returnClosure.ReturnType.IsEmptyTuple)
            return false;

        // No non-self parameters (the return value is the only "output" path and a
        // multi-param method needs a more complex receiver shape than Shape 4 emits).
        var nonSelfParams = method.CSSignature.Skip(1).ToList();
        if (nonSelfParams.Count != 0)
            return false;

        return true;
    }

    /// <summary>
    /// Returns true for closure-bearing protocol properties that have a real
    /// Swift↔C# proxy dispatch implementation. Accepts properties whose declared type
    /// is either a bare <see cref="ClosureTypeSpec"/> or <c>Optional&lt;Closure&gt;</c>
    /// with the closure passing <see cref="IsDispatchableClosureShape"/>. Required
    /// setter accessor mirrors the method-param dispatch path; getter materialisation
    /// goes through the dedicated property-getter thunk emitted by
    /// <see cref="ProtocolProxyEmitter"/>.
    /// </summary>
    internal static bool IsDispatchableClosureProperty(PropertyDecl property, ClosureHandler closureHandler)
    {
        if (property.IsStatic || property.IsObjCOptional)
            return false;
        if (ContainsSelfTypeParam(property.SwiftTypeSpec))
            return false;
        if (!TryGetDispatchableClosureParam(property.SwiftTypeSpec, closureHandler, out var closure, out _) || closure is null)
            return false;
        // Shape 3 restricts the dispatch path to () -> Void closure properties: the
        // getter-side Swift adapter materialiser only knows how to call a zero-arg
        // void cdecl thunk. Multi-arg / return-typed / throwing closure-property shapes
        // remain stubbed until the adapter emitter learns the corresponding cdecl bodies.
        if (closure.Throws || closure.IsAsync)
            return false;
        var argCount = closure.EachArgument().Count();
        if (argCount != 0)
            return false;
        if (!closure.ReturnType.IsEmptyTuple)
            return false;
        return true;
    }

    /// <summary>
    /// Recognises closure-bearing parameter shapes that the dispatch path can handle:
    /// bare `ClosureTypeSpec` or `Optional&lt;Closure&gt;`. The closure must independently pass
    /// <see cref="IsDispatchableClosureShape"/>.
    /// </summary>
    internal static bool TryGetDispatchableClosureParam(TypeSpec? paramType, ClosureHandler closureHandler, out ClosureTypeSpec? closure, out bool isOptional)
    {
        closure = null;
        isOptional = false;
        if (paramType is ClosureTypeSpec direct && IsDispatchableClosureShape(direct, closureHandler))
        {
            closure = direct;
            return true;
        }
        if (paramType is NamedTypeSpec named &&
            named.Name == "Swift.Optional" &&
            named.GenericParameters.Count == 1 &&
            named.GenericParameters[0] is ClosureTypeSpec inner &&
            IsDispatchableClosureShape(inner, closureHandler, treatAsEscaping: true))
        {
            closure = inner;
            isOptional = true;
            return true;
        }
        return false;
    }

    /// <summary>
    /// True when the closure shape can pass through the dispatch path: escaping, non-async,
    /// and with arg/return types the invoke thunk can marshal. Throwing closures are
    /// allowed when the invoke thunk supports them (Cdecl error-out parameter; see
    /// <see cref="ClosureEmitter.EmitSwiftInvokeThunk"/>).
    /// </summary>
    /// <param name="treatAsEscaping">
    /// Set when the caller has already established that the closure is implicitly escaping
    /// (Optional&lt;Closure&gt; is always escaping in Swift, but the ABI parser doesn't
    /// propagate the attribute through the Optional).
    /// </param>
    internal static bool IsDispatchableClosureShape(ClosureTypeSpec closure, ClosureHandler closureHandler, bool treatAsEscaping = false)
    {
        if (!closure.IsEscaping && !treatAsEscaping)
            return false;
        if (closure.IsAsync)
            return false;
        // @convention(c), @autoclosure, and actor/Sendable-qualified closures need additional
        // thunk plumbing we don't emit yet — only `escaping` is allowed.
        if (closure.HasAttributes)
        {
            foreach (var attr in closure.Attributes)
            {
                if (attr.Name != "escaping")
                    return false;
            }
        }
        // The cdecl invoke thunk must be able to marshal every arg and the return.
        // Mirrors ClosureEmitter.CanUseInvokeThunk so accepted shapes have a working emit path.
        if (!ClosureEmitter.CanUseInvokeThunk(closure, closureHandler))
            return false;
        return true;
    }

    /// <summary>
    /// Counts the number of cdecl pointer slots a single parameter occupies in the vtable
    /// signature. Closure params (when dispatchable) expand into two slots — function pointer
    /// + context — instead of the single `UnsafeRawPointer` used for value-shaped params.
    /// `Optional&lt;Closure&gt;` also expands into two slots and nil maps to (0, 0).
    /// </summary>
    internal static int CountVtableSlots(TypeSpec paramType, ClosureHandler closureHandler)
    {
        if (TryGetDispatchableClosureParam(paramType, closureHandler, out _, out _))
            return 2;
        if (IsDispatchableAsyncClosureParam(paramType, closureHandler, out _))
            return 2;
        return 1;
    }

    /// <summary>
    /// Stable @_cdecl entry point name for the per-closure-param invoke thunk.
    /// The thunk is the Swift function the C# proxy calls (via Cdecl P/Invoke) when the
    /// user-stored Action is invoked — it reconstructs the closure from (fnPtr, ctx) and
    /// calls it. Uses the wrapper-symbol "SBW_" prefix so the wrapper-symbol contract
    /// applies and binding-emit can register/verify the symbol.
    /// </summary>
    internal static string GetProtocolClosureInvokeThunkEntryPoint(ProtocolDecl protocolDecl, MethodDecl method, int methodIdx, int argIdx)
    {
        return $"SBW_{protocolDecl.Name}_{method.Name}_m{methodIdx}_arg{argIdx}_InvCR";
    }

    /// <summary>
    /// Human-readable Swift function name for the invoke thunk. Only visible inside
    /// the wrapper module — the @_cdecl entry point name from
    /// <see cref="GetProtocolClosureInvokeThunkEntryPoint"/> is the linker-visible symbol.
    /// </summary>
    internal static string GetProtocolClosureInvokeThunkSwiftFuncName(ProtocolDecl protocolDecl, MethodDecl method, int methodIdx, int argIdx)
    {
        return $"_invokeProtocolClosure_{protocolDecl.Name}_{method.Name}_m{methodIdx}_arg{argIdx}";
    }

    /// <summary>
    /// Deterministic C# helper method name for the closure invoke thunk's P/Invoke.
    /// Mirrors <see cref="ClosureEmitter.GetInvokeThunkHelperName"/>, but keyed on the
    /// protocol-method entry point (not a wrapper @_cdecl mangled name).
    /// </summary>
    internal static string GetProtocolClosureInvokeThunkHelperName(string entryPointName)
    {
        var hash = EmitterUtility.DeterministicHash8(entryPointName);
        return $"_InvokeClosureThunk_{hash}";
    }

    /// <summary>
    /// Enumerates the dispatchable closure parameter positions on a protocol method.
    /// Yields (param, argIdx) for each parameter whose <see cref="TypeSpec"/> is a dispatchable
    /// closure shape. The argIdx skips the receiver, so it's the same as the position in the
    /// C# receiver's expanded P/Invoke parameter list.
    /// </summary>
    internal static IEnumerable<(ArgumentDecl Param, int ArgIdx, ClosureTypeSpec Closure, bool IsOptional)> EnumerateDispatchableClosureParams(MethodDecl method, ClosureHandler closureHandler)
    {
        int argIdx = 0;
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            var p = method.CSSignature[i];
            if (DefaultParameterOverloadEmitter.IsDebugParameter(p) || p.SwiftTypeSpec.IsEmptyTuple)
                continue;
            if (TryGetDispatchableClosureParam(p.SwiftTypeSpec, closureHandler, out var closure, out var isOptional))
                yield return (p, argIdx, closure!, isOptional);
            argIdx++;
        }
    }

    /// <summary>
    /// Stable @_cdecl entry point name for the per-closure-property invoke
    /// thunk. Mirrors <see cref="GetProtocolClosureInvokeThunkEntryPoint"/> but keyed on the
    /// property — the C# proxy needs this same thunk when the user-supplied delegate stored
    /// on the impl's closure-property is invoked from Swift (setter direction).
    /// </summary>
    internal static string GetProtocolClosurePropertyInvokeThunkEntryPoint(ProtocolDecl protocolDecl, PropertyDecl property)
    {
        return $"SBW_{protocolDecl.Name}_{property.Name}_PropInvCR";
    }

    /// <summary>
    /// Human-readable Swift function name for the property invoke thunk.
    /// </summary>
    internal static string GetProtocolClosurePropertyInvokeThunkSwiftFuncName(ProtocolDecl protocolDecl, PropertyDecl property)
    {
        return $"_invokeProtocolClosureProperty_{protocolDecl.Name}_{property.Name}";
    }

    /// <summary>
    /// Stable @_cdecl entry point name for the closure-returning method's
    /// invoke thunk. Mirrors <see cref="GetProtocolClosureInvokeThunkEntryPoint"/> but keyed on
    /// the method only — the C# proxy needs this thunk when the user-supplied delegate stored
    /// on the impl is invoked from Swift after materialisation.
    /// </summary>
    internal static string GetProtocolClosureReturningMethodInvokeThunkEntryPoint(ProtocolDecl protocolDecl, MethodDecl method, int methodIdx)
    {
        return $"SBW_{protocolDecl.Name}_{method.Name}_m{methodIdx}_RetInvCR";
    }

    /// <summary>
    /// Human-readable Swift function name for the closure-returning method's
    /// invoke thunk.
    /// </summary>
    internal static string GetProtocolClosureReturningMethodInvokeThunkSwiftFuncName(ProtocolDecl protocolDecl, MethodDecl method, int methodIdx)
    {
        return $"_invokeProtocolClosureReturningMethod_{protocolDecl.Name}_{method.Name}_m{methodIdx}";
    }

    /// <summary>
    /// Enumerates dispatchable closure-returning methods on a protocol —
    /// each yielded entry produces a per-(protocol, method) Swift cdecl thunk and matching
    /// C# DllImport + invoker class. Uses the same method-index dedup as
    /// <see cref="EnumerateProtocolMethodsForDispatch"/>.
    /// </summary>
    internal static IEnumerable<(MethodDecl Method, int MethodIdx, ClosureTypeSpec ReturnClosure)> EnumerateDispatchableClosureReturningMethods(ProtocolDecl protocolDecl, ClosureHandler closureHandler)
    {
        foreach (var (method, methodIdx) in EnumerateProtocolMethodsForDispatch(protocolDecl))
        {
            if (!IsDispatchableClosureReturningMethod(method, closureHandler))
                continue;
            if (method.CSSignature.FirstOrDefault()?.SwiftTypeSpec is not ClosureTypeSpec retClosure)
                continue;
            yield return (method, methodIdx, retClosure);
        }
    }

    /// <summary>
    /// Enumerates dispatchable closure properties on a protocol — each
    /// yielded entry produces a per-(protocol, property) Swift cdecl thunk and matching
    /// C# DllImport + invoker class. Skips static / ObjC-optional properties and any
    /// property that does not pass <see cref="IsDispatchableClosureProperty"/>.
    /// </summary>
    internal static IEnumerable<(PropertyDecl Property, ClosureTypeSpec Closure, bool IsOptional)> EnumerateDispatchableClosureProperties(ProtocolDecl protocolDecl, ClosureHandler closureHandler)
    {
        foreach (var property in protocolDecl.Properties)
        {
            if (!IsDispatchableClosureProperty(property, closureHandler))
                continue;
            if (!TryGetDispatchableClosureParam(property.SwiftTypeSpec, closureHandler, out var closure, out var isOptional) || closure is null)
                continue;
            yield return (property, closure, isOptional);
        }
    }

    /// <summary>
    /// Enumerates protocol methods with their assigned vtable index, mirroring the
    /// dedup logic used by <see cref="EmitProtocolExtension"/> and
    /// <see cref="EmitProtocolVtableStruct"/>. Only yields each unique method-key once (skipping
    /// constructors, statics, and ObjC-optional methods) — the assigned idx matches the local
    /// vtable field name <c>Func_{name}_{idx}</c> emitted by ProtocolProxyEmitter.
    /// </summary>
    internal static IEnumerable<(MethodDecl Method, int MethodIdx)> EnumerateProtocolMethodsForDispatch(ProtocolDecl protocolDecl)
    {
        int methodIndex = 0;
        var methodIndices = new Dictionary<string, int>();
        foreach (var method in protocolDecl.Methods)
        {
            if (method.IsConstructor || method.MethodType == MethodType.Static)
                continue;
            if (method.IsObjCOptional)
                continue;

            var methodKey = GetMethodKey(method);
            if (methodIndices.ContainsKey(methodKey))
                continue;

            int idx = methodIndex++;
            methodIndices[methodKey] = idx;
            yield return (method, idx);
        }
    }

    /// <summary>
    /// Emits @_cdecl invoke thunks for every dispatchable closure parameter on the
    /// protocol's instance methods. Each thunk reconstructs the closure from (funcPtr, context)
    /// via typed memory binding and invokes it — the matching C# DllImport + invoker class are
    /// emitted by <see cref="ProtocolProxyEmitter"/> (see EmitProtocolClosureInvokeThunkHelpers).
    /// Lives next to the EveryProtocol extension so wrapper-emit + binding-emit speak through
    /// the same per-(protocol, method, argIdx) entry-point convention.
    /// </summary>
    private void EmitProtocolClosureInvokeThunks(SwiftWriter writer, ProtocolDecl protocolDecl)
    {
        var closureHandler = new ClosureHandler(_typeDatabase);
        foreach (var (method, methodIdx) in EnumerateProtocolMethodsForDispatch(protocolDecl))
        {
            if (!IsDispatchableClosureMethod(method, closureHandler))
                continue;
            foreach (var (param, argIdx, closure, _) in EnumerateDispatchableClosureParams(method, closureHandler))
            {
                var entryPoint = GetProtocolClosureInvokeThunkEntryPoint(protocolDecl, method, methodIdx, argIdx);
                var swiftFuncName = GetProtocolClosureInvokeThunkSwiftFuncName(protocolDecl, method, methodIdx, argIdx);
                ClosureEmitter.EmitSwiftInvokeThunk(writer, closure, closureHandler,
                    entryPoint, swiftFuncName, _emissionContext);
            }
        }

        // Per-property closure invoke thunks. When the user-supplied
        // delegate stored on impl.<PascalProp> is invoked from Swift via the materialised
        // adapter, the adapter calls this @_cdecl thunk which reconstructs the original
        // Swift closure (when it was set from Swift) and calls it. Same invoke-thunk
        // machinery as method-param closures, keyed on (protocol, property).
        foreach (var (property, closure, _) in EnumerateDispatchableClosureProperties(protocolDecl, closureHandler))
        {
            var entryPoint = GetProtocolClosurePropertyInvokeThunkEntryPoint(protocolDecl, property);
            var swiftFuncName = GetProtocolClosurePropertyInvokeThunkSwiftFuncName(protocolDecl, property);
            ClosureEmitter.EmitSwiftInvokeThunk(writer, closure, closureHandler,
                entryPoint, swiftFuncName, _emissionContext);
        }

        // Closure-returning methods do NOT need a Swift-side @_cdecl invoke thunk: the closure
        // returned from impl.<Method>() is a pure managed Action and the (fnPtr, ctx)
        // pair points at the C# proxy's `_MethodClosureThunk_<name>_<idx>` directly.
        // Swift wraps the pair into a `() -> Void` via `_sbWrapClosureContext` and calls
        // that C# thunk by function pointer — no Swift→C# invoke shim is required.

        // Per-method async closure invoke thunks. Spawns Task to drive
        // `await closure()` and signals completion to C# via a function-pointer callback
        // (TaskCompletionSource bridge on the C# side). Restricted to () async -> Int32.
        foreach (var (method, methodIdx) in EnumerateProtocolMethodsForDispatch(protocolDecl))
        {
            if (!IsDispatchableAsyncClosureMethod(method, closureHandler))
                continue;
            foreach (var (param, argIdx, closure) in EnumerateDispatchableAsyncClosureParams(method, closureHandler))
            {
                EmitProtocolAsyncClosureInvokeThunk(writer, protocolDecl, method, methodIdx, argIdx);
            }
        }
    }

    /// <summary>
    /// Emits the Swift @_cdecl invoke thunk for an async closure
    /// parameter on a protocol method. The thunk takes the closure's (fnPtr, ctx)
    /// pair plus a TaskCompletionSource handle and a C# completion function pointer,
    /// reconstructs the Swift async closure via raw-byte interpretation, spawns a
    /// detached Task to drive <c>await closure()</c>, then calls the completion
    /// callback with the Int32 result so the C# side can resume the TCS.
    /// </summary>
    private void EmitProtocolAsyncClosureInvokeThunk(SwiftWriter writer, ProtocolDecl protocolDecl, MethodDecl method, int methodIdx, int argIdx)
    {
        var entryPoint = GetProtocolAsyncClosureInvokeThunkEntryPoint(protocolDecl, method, methodIdx, argIdx);
        var swiftFuncName = $"_invokeProtocolAsyncClosure_{protocolDecl.Name}_{method.Name}_m{methodIdx}_arg{argIdx}";

        writer.WriteLine("// Async closure invoke thunk. Spawns a Task to drive");
        writer.WriteLine($"// `await closure()` and signals completion to C# via the function-pointer callback.");
        writer.WriteLine($"@_cdecl(\"{entryPoint}\")");
        writer.WriteLine($"public func {swiftFuncName}(");
        writer.WriteLine("    _ _funcPtr: Int,");
        writer.WriteLine("    _ _context: Int,");
        writer.WriteLine("    _ _tcsHandle: Int,");
        writer.WriteLine("    _ _completion: @convention(c) (Int, Int32) -> Void) {");
        writer.Indent++;
        writer.WriteLine("let _buf = UnsafeMutableRawPointer.allocate(byteCount: MemoryLayout<(Int, Int)>.size, alignment: MemoryLayout<(Int, Int)>.alignment)");
        writer.WriteLine("_buf.storeBytes(of: _funcPtr, as: Int.self)");
        writer.WriteLine("_buf.storeBytes(of: _context, toByteOffset: MemoryLayout<Int>.size, as: Int.self)");
        writer.WriteLine("let _closure = _buf.assumingMemoryBound(to: (() async -> Int32).self).pointee");
        writer.WriteLine("_buf.deallocate()");
        writer.WriteLine($"{SwiftConcurrencyNames.Task} {{");
        writer.Indent++;
        writer.WriteLine("let result = await _closure()");
        writer.WriteLine("_completion(_tcsHandle, result)");
        writer.Indent--;
        writer.WriteLine("}");
        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
        // protocol async-closure invoke thunk — the entry-point
        // shape is `SBW_{protocol}_{method}_m{i}_arg{j}_AsyncInvCR`, structurally
        // disjoint from any non-thunk wrapper symbol. Per-kind method bucket is
        // collision-safe.
        _emissionContext?.TryAddMethodWrapperSymbol(entryPoint, DeclIdFactory.ForMethod(method));
    }

    /// <summary>
    /// Checks if a method has generic type parameters (e.g., τ_0_0 representing Self)
    /// in its return type or non-self parameters. Uses recursive TypeSpec traversal.
    /// </summary>
    private static bool HasGenericTypeParamInSignature(MethodDecl method)
    {
        // Check return type (CSSignature[0])
        if (method.CSSignature.Count > 0 && ContainsGenericTypeParam(method.CSSignature[0].SwiftTypeSpec))
            return true;

        // Check non-self parameters (skip return type at index 0)
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            if (ContainsGenericTypeParam(method.CSSignature[i].SwiftTypeSpec))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if a method has ONLY method-level generic parameters (τ_1_0+) but NO
    /// protocol-level Self type params (τ_0_*). Methods like resolve&lt;Service&gt;() have
    /// method-level generics that EveryProtocol can satisfy with stub implementations,
    /// unlike Self-typed methods which can't be properly dispatched.
    /// </summary>
    internal static bool HasOnlyMethodLevelGenerics(MethodDecl method)
    {
        return HasGenericTypeParamInSignature(method) && !HasSelfTypeParamInSignature(method);
    }

    /// <summary>
    /// Detects protocols with both method-level generic and non-generic instance members.
    /// These need ALL members emitted as stubs because the type projection pipeline generates
    /// incorrect types for non-generic members when method-level generic parameters are in scope.
    ///
    /// <para>The "has method-level generic" leg uses <see cref="HasMethodLevelGenericInSignature"/>
    /// instead of <see cref="HasOnlyMethodLevelGenerics"/>: a method carrying BOTH a method-level
    /// generic AND a Self-type param taints type projection just as much as a pure method-level
    /// generic one, but HasOnlyMethodLevelGenerics filters it out (HasSelfTypeParamInSignature
    /// short-circuits). Using the broader predicate keeps such protocols out of the sibling
    /// plan input so they cannot win owner selection via lex tie-break and poison the group.</para>
    /// </summary>
    internal static bool IsMixedGenericProtocol(ProtocolDecl protocolDecl)
    {
        return protocolDecl.Methods
            .Where(m => !m.IsConstructor && m.MethodType != MethodType.Static)
            .Any(HasMethodLevelGenericInSignature) &&
            (protocolDecl.Properties.Any(p => !p.IsStatic) ||
             protocolDecl.Subscripts.Any(s => !s.IsStatic) ||
             protocolDecl.Methods
                .Where(m => !m.IsConstructor && m.MethodType != MethodType.Static)
                .Any(m => !HasMethodLevelGenericInSignature(m)));
    }

    /// <summary>
    /// Returns true when the method's signature contains a method-level (non-Self) generic
    /// type parameter — a τ_1_+-depth name or a non-Self associated-type reference. Used by
    /// <see cref="IsMixedGenericProtocol"/> to broaden the "has polluting generic method"
    /// leg so methods carrying BOTH a method-level generic AND a Self param still count
    /// toward the mixed-generic classification. <see cref="HasOnlyMethodLevelGenerics"/>
    /// excludes that shape because it short-circuits on Self.
    /// </summary>
    internal static bool HasMethodLevelGenericInSignature(MethodDecl method)
    {
        for (int i = 0; i < method.CSSignature.Count; i++)
        {
            if (ContainsSubscriptLevelGenericDependent(method.CSSignature[i].SwiftTypeSpec))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Checks if a method has protocol-level (Self/depth-0) generic type params in its signature.
    /// Returns false for method-level generics (τ_1_0+) which are independent of the conforming type.
    /// </summary>
    internal static bool HasSelfTypeParamInSignature(MethodDecl method)
    {
        if (method.CSSignature.Count > 0 && ContainsSelfTypeParam(method.CSSignature[0].SwiftTypeSpec))
            return true;
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            if (ContainsSelfTypeParam(method.CSSignature[i].SwiftTypeSpec))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Recursively checks if a TypeSpec contains a protocol-level (Self/depth-0) generic type param.
    /// </summary>
    internal static bool ContainsSelfTypeParam(TypeSpec? typeSpec)
    {
        if (typeSpec == null)
            return false;

        switch (typeSpec)
        {
            case NamedTypeSpec namedType:
                if (TypeSpecHelpers.IsProtocolLevelGenericParam(namedType.Name))
                    return true;
                // Self.AssociatedType references (e.g., Self.Action, Self.Element) — can't
                // be expressed from EveryProtocol because it has no matching associated type.
                if (namedType.Name.StartsWith("Self.", StringComparison.Ordinal))
                    return true;
                foreach (var genericParam in namedType.GenericParameters)
                {
                    if (ContainsSelfTypeParam(genericParam))
                        return true;
                }
                return false;

            case TupleTypeSpec tupleType:
                return tupleType.Elements.Any(e => ContainsSelfTypeParam(e));

            case ClosureTypeSpec closureType:
                return ContainsSelfTypeParam(closureType.Arguments) ||
                       ContainsSelfTypeParam(closureType.ReturnType);

            case ProtocolListTypeSpec protocolListType:
                return protocolListType.Protocols.Keys.Any(p => ContainsSelfTypeParam(p));

            case AssociatedTypeReferenceSpec assocType:
                return TypeSpecHelpers.IsProtocolLevelGenericParam(assocType.BaseType)
                    || assocType.BaseType == "Self";

            default:
                return false;
        }
    }

    /// <summary>
    /// Returns true when a subscript's signature references a generic param that is
    /// bound at subscript scope rather than at the protocol's <c>Self</c>. Catches both
    /// the dependent-member form (<c>S.Element</c>) and the bare form
    /// (<c>subscript&lt;T&gt;(key: String) -&gt; T?</c>). <see cref="SubscriptDecl"/> does not
    /// preserve a generic clause, so the Self-typed stub emitter substitutes the bare
    /// generic with <c>EveryProtocol</c> — which fails to witness a generic subscript
    /// requirement. Skipping the conformance is safer than emitting a bad stub.
    /// </summary>
    private static bool HasSubscriptLevelGenericDependentMember(SubscriptDecl subscript)
    {
        if (ContainsSubscriptLevelGenericDependent(subscript.ReturnTypeSpec))
            return true;
        return subscript.IndexParameters.Any(ip => ContainsSubscriptLevelGenericDependent(ip.SwiftTypeSpec));
    }

    private static bool ContainsSubscriptLevelGenericDependent(TypeSpec? typeSpec)
    {
        if (typeSpec == null)
            return false;
        switch (typeSpec)
        {
            case AssociatedTypeReferenceSpec assoc:
                return IsNonSelfGenericParamName(assoc.BaseType);
            case NamedTypeSpec named:
                // Nested DependentMember nodes do not flow through CreateTypeSpec — TypeSpecParser
                // parses generic params from the PrintedName text, so "S.Element" surfaces as a
                // NamedTypeSpec with a literal dotted name. Match both the dotted form and the
                // bare-param form (<T> / <τ_1_0>) where the generic appears alone with no member
                // access. IsGenericTypeParameter (rather than IsProtocolLevelGenericParam) is
                // required so canonical method/subscript-scope spellings like τ_1_0 are caught.
                var dotIdx = named.Name.IndexOf('.');
                if (dotIdx > 0)
                {
                    if (IsNonSelfGenericParamName(named.Name.Substring(0, dotIdx)))
                        return true;
                }
                else if (IsNonSelfGenericParamName(named.Name))
                {
                    return true;
                }
                return named.GenericParameters.Any(ContainsSubscriptLevelGenericDependent);
            case TupleTypeSpec tuple:
                return tuple.Elements.Any(ContainsSubscriptLevelGenericDependent);
            case ClosureTypeSpec closure:
                return ContainsSubscriptLevelGenericDependent(closure.Arguments) ||
                       ContainsSubscriptLevelGenericDependent(closure.ReturnType);
            case ProtocolListTypeSpec protocolList:
                return protocolList.Protocols.Keys.Any(ContainsSubscriptLevelGenericDependent);
            default:
                return false;
        }
    }

    private static bool IsNonSelfGenericParamName(string name)
    {
        if (name == "Self" || name.StartsWith("τ_0_", StringComparison.Ordinal))
            return false;
        return TypeSpecHelpers.IsGenericTypeParameter(name);
    }

    /// <summary>
    /// Recursively checks if a TypeSpec contains a generic type parameter.
    /// Walks through NamedTypeSpec, TupleTypeSpec, ClosureTypeSpec, and ProtocolListTypeSpec.
    /// </summary>
    private static bool ContainsGenericTypeParam(TypeSpec? typeSpec)
    {
        if (typeSpec == null)
            return false;

        switch (typeSpec)
        {
            case NamedTypeSpec namedType:
                if (TypeSpecHelpers.IsGenericTypeParameter(namedType.Name))
                    return true;
                foreach (var genericParam in namedType.GenericParameters)
                {
                    if (ContainsGenericTypeParam(genericParam))
                        return true;
                }
                return false;

            case TupleTypeSpec tupleType:
                foreach (var element in tupleType.Elements)
                {
                    if (ContainsGenericTypeParam(element))
                        return true;
                }
                return false;

            case ClosureTypeSpec closureType:
                if (ContainsGenericTypeParam(closureType.Arguments))
                    return true;
                if (ContainsGenericTypeParam(closureType.ReturnType))
                    return true;
                return false;

            case ProtocolListTypeSpec protocolListType:
                foreach (var protocol in protocolListType.Protocols.Keys)
                {
                    if (ContainsGenericTypeParam(protocol))
                        return true;
                }
                return false;

            case AssociatedTypeReferenceSpec assocType:
                // Associated types like Self.Element or τ_0_0.Element reference
                // unresolved generic type parameters through their base type.
                return TypeSpecHelpers.IsGenericTypeParameter(assocType.BaseType)
                    || assocType.BaseType == "Self";

            default:
                return false;
        }
    }

    /// <summary>
    /// Checks if a parameter's Swift type is ObjC-bridgeable (e.g., URL, URLRequest).
    /// ObjC-bridgeable types need special vtable marshalling: bridge to AnyObject and pass
    /// the ObjC pointer instead of the raw Swift struct bytes.
    /// </summary>
    private bool IsObjCBridgeableParam(TypeSpec? typeSpec)
    {
        if (typeSpec is not NamedTypeSpec named)
            return false;
        if (!_typeDatabase.TryGetTypeRecord(named, out var record))
            return false;
        return MarshallingHelpers.IsObjCBridgeable(record);
    }

    /// <summary>
    /// If <paramref name="typeSpec"/> is <c>Optional&lt;T&gt;</c> where T is an ObjC-bridgeable VALUE
    /// type (Foundation.URL and NS_TYPED_ENUM newtypes — carrying the ObjCBridgeable flag, NOT a
    /// bridged/rooted ObjC CLASS), returns the inner type's Swift metatype name (e.g. "Foundation.URL");
    /// otherwise null. Class optionals (ObjCBridged / ObjCRooted) are deliberately EXCLUDED: an
    /// <c>Optional&lt;class-reference&gt;</c> is a nil-pointer-optimized single word whose layout already
    /// matches the one-word slot the C# receiver deposits, so its generic <c>move()</c> read is sound.
    /// Only a MULTI-word bridgeable VALUE optional (a resilient value type such as URL) mismatches that
    /// buffer and reads past it — this arm redirects exactly that case to a one-word pointer-optional read.
    /// </summary>
    private string? GetOptionalObjCBridgeableValueInnerName(TypeSpec? typeSpec)
    {
        if (!MarshallingHelpers.IsSwiftOptional(typeSpec))
            return null;
        var named = (NamedTypeSpec)typeSpec!;
        if (named.GenericParameters.Count != 1)
            return null;
        var inner = named.GenericParameters[0];
        if (!IsObjCBridgeableParam(inner))
            return null;
        return GetSwiftTypeNameForMetatype(inner);
    }

    /// <summary>
    /// Emits the reverse-return tail for an Optional scalar ObjC-bridgeable VALUE requirement (e.g.
    /// <c>URL?</c>), consuming a <c>resultPtr</c> the caller has already populated from the C# receiver.
    /// The receiver deposited a single optional ObjC POINTER — <c>IntPtr.Zero</c> for .none, a
    /// +1-retained handle for .some — occupying one nil-pointer-optimized word, NOT a SwiftOptional
    /// buffer. Read it back as an <c>UnsafeRawPointer?</c> (same one-word layout) and, on .some, consume
    /// the transferred +1 (<c>takeRetainedValue</c>) while <c>as!</c>-bridging the live ObjC object into
    /// the native value type. Consuming it through the generic <c>move()</c> arm would treat the one word
    /// as a multi-word <c>Optional&lt;T&gt;</c> and read past the buffer → corrupt value → SIGSEGV.
    /// Symmetric with the non-optional bridgeable arm's takeRetainedValue consume.
    /// </summary>
    private static void EmitOptionalObjCBridgeableValueReturn(SwiftWriter writer, string innerTypeName)
    {
        writer.WriteLines($$"""
            let resultObjPtr = resultPtr.load(as: UnsafeRawPointer?.self)
            resultPtr.deallocate()
            return resultObjPtr.map { Unmanaged<AnyObject>.fromOpaque($0).takeRetainedValue() as! {{innerTypeName}} }
            """);
    }

    private string GetSwiftTypeName(TypeSpec? typeSpec) =>
        SwiftTypeNameHelper.GetSwiftTypeName(typeSpec);

    /// <summary>
    /// Renders a witness's declaration type — the var annotation, parameter, or result. Differs from
    /// <see cref="GetSwiftTypeName"/> only for a `T!` requirement, which must render back as `T!` or
    /// the conformance checker rejects the witness. Never use it for a body expression: `!` is a
    /// syntax error in a generic argument or a `.self` metatype.
    /// </summary>
    private string GetSwiftTypeNameForDeclaration(TypeSpec? typeSpec) =>
        SwiftTypeNameHelper.GetSwiftTypeNameForDeclaration(typeSpec);

    private string GetSwiftTypeNameForMetatype(TypeSpec? typeSpec) =>
        SwiftTypeNameHelper.GetSwiftTypeNameForMetatype(typeSpec);

    /// <summary>
    /// Renders a TypeSpec to a Swift type string, substituting protocol-level generic params
    /// (τ_0_0, τ_0_1, etc.) with EveryProtocol. Used by Self-typed member stubs so the
    /// conformance compiles — in the extension context, Self IS EveryProtocol.
    /// </summary>
    /// <summary>
    /// Declaration-position form of <see cref="RenderTypeSpecWithSelfSubstitution"/>. A stub still
    /// has to satisfy its requirement, so a `T!` requirement needs a `T!` stub — the fatalError body
    /// is irrelevant to the conformance checker. Only the top level is re-spelled, so the recursive
    /// calls inside the renderer stay on the plain form.
    /// </summary>
    private string RenderTypeSpecWithSelfSubstitutionForDeclaration(TypeSpec? typeSpec) =>
        SwiftTypeNameHelper.ApplyImplicitlyUnwrappedOptionalSigil(
            RenderTypeSpecWithSelfSubstitution(typeSpec), typeSpec);

    private string RenderTypeSpecWithSelfSubstitution(TypeSpec? typeSpec, bool suppressEscaping = false)
    {
        if (typeSpec == null)
            return "Any";

        switch (typeSpec)
        {
            case AssociatedTypeReferenceSpec assocRef:
                // Dependent member types on Self (τ_0_0.RowDecoder) or on a method-level
                // generic param (S.Element, T.RawValue) collapse to Any — neither has a
                // concrete associated-type witness in the extension context. Stays in sync
                // with ContainsSelfTypeParam's detection, which uses the same generic-name
                // signal to flag the member for stub emission.
                if (assocRef.BaseType.StartsWith("τ_0_") ||
                    TypeSpecHelpers.IsGenericTypeParameter(assocRef.BaseType) ||
                    assocRef.BaseType == "Self")
                    return "Any";
                return GetSwiftTypeName(typeSpec);

            case NamedTypeSpec namedType:
                // τ_0_0 (Self) → EveryProtocol
                if (TypeSpecHelpers.IsProtocolLevelGenericParam(namedType.Name))
                    return "EveryProtocol";
                // Metatype of Self: τ_0_0.Type → EveryProtocol.Type
                // Associated types: τ_0_0.SomeName → Any (EveryProtocol doesn't have associated types)
                if (namedType.Name.StartsWith("τ_0_") && namedType.Name.Contains('.'))
                {
                    var suffix = namedType.Name.Substring(namedType.Name.IndexOf('.'));
                    if (suffix == ".Type")
                        return "EveryProtocol.Type";
                    return "Any";
                }
                // Self.X — Self metatype or associated type reference. EveryProtocol has no
                // matching associated types, so associated-type references map to Any while
                // Self.Type maps to EveryProtocol.Type.
                if (namedType.Name.StartsWith("Self.", StringComparison.Ordinal))
                {
                    return namedType.Name == "Self.Type" ? "EveryProtocol.Type" : "Any";
                }
                // Method-level generic associated types like S.Element / T.RawValue come in
                // as a NamedTypeSpec whose literal Name is "S.Element" (TypeSpecParser keeps
                // it as a single dotted token). Without this branch, GetSwiftTypeName emits
                // the raw "S.Element" into the stub and the wrapper fails to compile because
                // S is unbound in the EveryProtocol extension. Treat this the same way as
                // the Self.X / τ_0_*.X cases above.
                var dotIndex = namedType.Name.IndexOf('.');
                if (dotIndex > 0 &&
                    TypeSpecHelpers.IsGenericTypeParameter(namedType.Name.Substring(0, dotIndex)))
                {
                    return namedType.Name.Substring(dotIndex) == ".Type" ? "EveryProtocol.Type" : "Any";
                }
                // Non-generic types: recurse into generic params
                if (!TypeSpecHelpers.IsGenericTypeParameter(namedType.Name))
                {
                    if (namedType.ContainsGenericParameters && namedType.GenericParameters.Count > 0)
                    {
                        bool isOptional = namedType.Name == "Swift.Optional";
                        var renderedParams = string.Join(", ", namedType.GenericParameters
                            .Select(p => RenderTypeSpecWithSelfSubstitution(p, suppressEscaping: isOptional)));
                        return $"{namedType.Name}<{renderedParams}>";
                    }
                    return GetSwiftTypeName(typeSpec);
                }
                return "Any"; // Fallback for other generic params

            case ClosureTypeSpec closure:
                string args;
                if (closure.Arguments is TupleTypeSpec argTuple && argTuple.Elements.Count > 0)
                    args = string.Join(", ", argTuple.Elements.Select(e => RenderTypeSpecWithSelfSubstitution(e)));
                else if (closure.Arguments.IsEmptyTuple)
                    args = "";
                else
                    args = RenderTypeSpecWithSelfSubstitution(closure.Arguments);
                var ret = RenderTypeSpecWithSelfSubstitution(closure.ReturnType);
                var attrs = new List<string>();
                if (closure.IsEscaping && !suppressEscaping) attrs.Add("@escaping");
                if (closure.HasAttributes)
                {
                    foreach (var attr in closure.Attributes)
                    {
                        if (attr.Name != "escaping")
                            attrs.Add($"@{attr.Name}");
                    }
                }
                var attrPrefix = attrs.Count > 0 ? string.Join(" ", attrs) + " " : "";
                var asyncStr = closure.IsAsync ? " async" : "";
                var throwsStr = closure.Throws ? " throws" : "";
                return $"{attrPrefix}({args}){asyncStr}{throwsStr} -> {ret}";

            case TupleTypeSpec tuple:
                if (tuple.Elements.Count == 0) return "()";
                var rendered = tuple.Elements.Select(e =>
                {
                    var typeName = RenderTypeSpecWithSelfSubstitution(e);
                    return e.TypeLabel != null ? $"{e.TypeLabel}: {typeName}" : typeName;
                });
                return $"({string.Join(", ", rendered)})";

            default:
                if (typeSpec.IsEmptyTuple) return "()";
                return GetSwiftTypeName(typeSpec);
        }
    }

    private string BuildArgumentPassList(IReadOnlyList<ArgumentDecl> parameters)
    {
        var lines = new List<string>();
        for (int i = 0; i < parameters.Count; i++)
        {
            var param = parameters[i];
            var paramName = string.IsNullOrEmpty(param.Name) || param.Name == "_" ? $"arg{i}" : param.Name;
            lines.Add($"var {paramName}Copy = {NameProvider.EscapeSwiftKeyword(paramName)}");
        }
        return lines.Count > 0 ? string.Join("\n        ", lines) : "";
    }

    private string BuildArgRefs(IReadOnlyList<ArgumentDecl> parameters)
    {
        // Internal names are always synthetic `arg{i}` to stay aligned with the
        // EmitSubscriptImplementation parameter list, which uses `<external> arg{i}: Type`
        // form to preserve the protocol's external argument label.
        var refs = new List<string>();
        for (int i = 0; i < parameters.Count; i++)
        {
            // Mirror EmitSubscriptArgCopies' predicate exactly so the setup and the call-site ref
            // stay aligned for Array/String index params. See RequiresExplicitValuePointer.
            refs.Add(RequiresExplicitValuePointer(parameters[i].SwiftTypeSpec)
                ? $"UnsafeRawPointer(arg{i}CopyPtr)"
                : $"&arg{i}Copy");
        }
        return refs.Count > 0 ? ", " + string.Join(", ", refs) : "";
    }

    /// <summary>
    /// Returns the source-module prefix when <paramref name="protocolDecl"/> is being
    /// emitted in a wrapper that isn't its native module — i.e., the cross-module
    /// parent companion path driven from <see cref="Handler.ModuleHandler"/>. The prefix
    /// disambiguates wrapper symbols when two dependency modules export protocols with
    /// the same simple name; same-module emission yields an empty prefix so the existing
    /// symbol shape is preserved.
    /// </summary>
    private string GetCrossModulePrefix(ProtocolDecl protocolDecl)
    {
        var sourceModule = protocolDecl.ModuleDecl?.Name;
        if (string.IsNullOrEmpty(sourceModule) || sourceModule == _moduleName)
            return string.Empty;
        return sourceModule + "_";
    }

    private string GetVtableStructName(ProtocolDecl protocolDecl)
    {
        return $"{GetCrossModulePrefix(protocolDecl)}{protocolDecl.Name}_vtable";
    }

    private string GetVtableInstanceName(ProtocolDecl protocolDecl)
    {
        var name = protocolDecl.Name;
        var prefix = GetCrossModulePrefix(protocolDecl);
        if (prefix.Length == 0)
        {
            // Same-module convention: lowercase first letter, drop a leading underscore is unneeded.
            return $"_{char.ToLowerInvariant(name[0])}{name.Substring(1)}_vtable";
        }
        // Cross-module: keep the module prefix uppercase so it reads as "_Module_protocolName_vtable".
        return $"_{prefix}{char.ToLowerInvariant(name[0])}{name.Substring(1)}_vtable";
    }

    private string GetSetVtableFunctionName(ProtocolDecl protocolDecl)
    {
        return $"set{GetCrossModulePrefix(protocolDecl)}{protocolDecl.Name}_vtable";
    }

    private string GetSetVtableMangledName(ProtocolDecl protocolDecl)
    {
        // @_cdecl symbol name that C# will call.
        // ProtocolProxyEmitter.GetSetVtablePInvokeName must produce the matching entry point.
        return $"Set{GetCrossModulePrefix(protocolDecl)}{protocolDecl.Name}_vtable";
    }

    private static string GetWitnessTableGetterFunctionName(ProtocolDecl protocolDecl)
    {
        return $"getEveryProtocol{protocolDecl.Name}WitnessTable";
    }

    private static string GetWitnessTableGetterMangledName(ProtocolDecl protocolDecl)
    {
        // @_cdecl symbol name that C# will call
        return $"Get_EveryProtocol_{protocolDecl.Name}_WitnessTable";
    }

    private static string GetExistentialSizeGetterFunctionName(ProtocolDecl protocolDecl)
    {
        return $"getEveryProtocol{protocolDecl.Name}ExistentialSize";
    }

    /// <summary>
    /// The <c>@_cdecl</c> symbol of the existential-size accessor emitted beside the witness-table
    /// getter. <c>ProtocolProxyEmitter</c> declares the matching P/Invoke under the same
    /// getter-emitted gate, so the declared and exported sets cannot drift.
    /// </summary>
    internal static string GetExistentialSizeGetterMangledName(ProtocolDecl protocolDecl)
    {
        return $"Get_EveryProtocol_{protocolDecl.Name}_ExistentialSize";
    }

    /// <summary>
    /// The authoritative reverse-dispatch slot key: method name + argument labels + RAW Swift
    /// parameter type specs + async effect. This is the producer's index allocator
    /// (<see cref="EmitProtocolVtableStruct"/>), so the C# reverse-dispatch consumers
    /// (ProtocolProxyEmitter Vtables/Receivers/StaticInit) MUST key their slot indices on this
    /// same builder to stay byte-for-byte aligned with the Swift `_vtable` struct. Distinct from
    /// <see cref="ProtocolSignatureHelper.GetMethodSignatureKey"/> (which projects params through
    /// the C# type fallback and collapses raw-distinct existential overloads) — the projected key
    /// is correct for interface dedup / skip-set membership but MUST NOT gate slot positions.
    /// </summary>
    internal static string GetMethodKey(MethodDecl method)
    {
        // Create a unique key for method overloading based on name, argument labels, and parameter types.
        // Argument labels are needed to distinguish Swift overloads like:
        //   pageViewController(_:viewControllerBeforeViewController:)
        //   pageViewController(_:viewControllerAfterViewController:)
        // which have the same name and parameter types but different labels.
        // The async effect is part of the key: `func m()` and `func m() async` are distinct Swift
        // witness-table requirements occupying separate vtable slots, so this builder (which the
        // EveryProtocol wrapper's methodIndices, extension-body loop, and dispatch enumerator all
        // key off) must allocate the async overload its own slot rather than aliasing it onto the
        // sync one — matching ProtocolSignatureHelper.GetMethodSignatureKey's default.
        var asyncSuffix = method.IsAsync ? ":async" : "";
        return method.Name + "(" + string.Join(",", method.CSSignature.Skip(1).Select(p =>
            (p.GetSwiftName() ?? p.Name) + ":" + (p.SwiftTypeSpec?.ToString() ?? ""))) + ")" + asyncSuffix;
    }

    /// <summary>
    /// THE single classifier oracle for whether an <c>async</c> protocol requirement is satisfied by a
    /// REAL asynchronous reverse-dispatch witness (Swift suspends on <c>withCheckedThrowingContinuation</c>
    /// and hands the continuation to C# through the widened vtable slot) rather than the legacy
    /// thread-BLOCKING sync witness (<c>impl.FooAsync(...).GetAwaiter().GetResult()</c>, S13 Pillar C).
    ///
    /// <para>Method-shape-ONLY by construction: it consults nothing but the method's own signature, so
    /// every site that must agree on the slot's width and effect — <see cref="VtableLayoutBuilder.GetWidth"/>
    /// (which has no access to <c>_useObjCBase</c>), <see cref="EmitMethodVtableField"/>, the C# local
    /// delegate field, the receiver, and this extension's witness body — reaches the IDENTICAL verdict and
    /// cannot drift the slot's parameter count or return effect (the Bug #21 SIGSEGV class). A real-async
    /// slot keeps the SAME index (one function pointer) but a WIDER signature: +3 trailing pointers
    /// (continuation box, success FP, error FP) and a <c>Void</c> return — the suspend/resume handoff
    /// reuses the forward async-closure box machinery verbatim (<see cref="ClosureEmitter"/>).</para>
    ///
    /// <para>The supported shape is deliberately narrow: an <c>async</c> instance requirement
    /// returning a blittable primitive scalar, with blittable-primitive value params only, bounded arity,
    /// and none of the shapes that take a different emit path (closures, method-level generics, Self-typed
    /// params, inout, constructors, statics, @objc-optional). Anything outside the shape returns
    /// <see langword="false"/> and keeps the legacy blocking sync witness — a clean fallback, never a
    /// half-real witness. The blittable-primitive gate is what lets the box marshal the result through
    /// <c>resultPtr.load(as: T.self)</c> / <c>SwiftMarshal.MarshalToSwift</c> without a value-witness copy.</para>
    ///
    /// <para>Both <c>async throws</c> and non-throwing <c>async</c> requirements qualify; the throwing
    /// effect is NOT part of this predicate. Each emission site reads <see cref="MethodDecl.Throws"/> to
    /// pick the variant: a throwing witness boxes a <c>CheckedContinuation&lt;T, Error&gt;</c> and resumes
    /// with the error on a C# fault; a non-throwing witness boxes a <c>CheckedContinuation&lt;T, Never&gt;</c>
    /// (no Swift error channel) and FailFasts on a C# fault — mirroring the forward async-closure throwing
    /// vs. non-throwing box exactly. The two are genuinely distinct emitted shapes, gated on
    /// <see cref="MethodDecl.Throws"/> per-site, not a flag on one path.</para>
    /// </summary>
    public static bool EmitsRealAsyncWitness(MethodDecl method)
    {
        if (!method.IsAsync)
            return false;
        if (method.IsConstructor || method.MethodType == MethodType.Static || method.IsObjCOptional)
            return false;
        // Shapes that take a dedicated, non-vtable-value emit path — exactly the predicates
        // ClassifyMethod uses to exclude or specially route a slot. A real-async witness is the
        // plain value-marshalled reverse-dispatch shape, so it must reject all of them.
        // ClassifyMethod's remaining arms (@objc-existential positions, mixed-generic protocols)
        // are deliberately absent here: they can only appear in a signature that already fails
        // the blittable-primitive-scalar whitelist below (an existential or generic-dependent
        // type is never a blittable primitive), so this predicate rejects those methods without
        // naming the arms — and a rejection here just keeps the legacy blocking witness.
        if (HasClosureInMethodSignature(method))
            return false;
        if (HasOnlyMethodLevelGenerics(method) || HasMethodLevelGenericInSignature(method))
            return false;
        if (HasSelfTypeParamInSignature(method))
            return false;

        // Return: a single blittable primitive scalar. The Swift box resumes via
        // `resultPtr.load(as: T.self)` and C# fills the buffer via SwiftMarshal.MarshalToSwift<T>,
        // both of which require a BitwiseCopyable fixed-layout scalar (Phase 1 omits the Void variant).
        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        if (returnType is null || returnType.IsEmptyTuple)
            return false;
        if (returnType is not NamedTypeSpec retNts || !CdeclParamMapper.IsBlittablePrimitiveSwiftType(retNts.Name))
            return false;

        // Params: blittable primitive scalars only, no inout, bounded arity. Each is passed to the
        // widened slot as a value pointer (&copy) exactly as the sync witness passes a value param.
        var valueParams = method.CSSignature.Skip(1)
            .Where(p => !DefaultParameterOverloadEmitter.IsDebugParameter(p) && !p.SwiftTypeSpec.IsEmptyTuple)
            .ToList();
        if (valueParams.Count > RealAsyncWitnessMaxArity)
            return false;
        foreach (var p in valueParams)
        {
            if (p.IsInOut)
                return false;
            if (p.SwiftTypeSpec is not NamedTypeSpec pNts || !CdeclParamMapper.IsBlittablePrimitiveSwiftType(pNts.Name))
                return false;
        }
        return true;
    }

    /// <summary>Upper bound on the value-param arity a real-async reverse-dispatch witness supports.</summary>
    private const int RealAsyncWitnessMaxArity = 4;

    private static string GetSubscriptKey(SubscriptDecl subscript, int index)
    {
        // Create a unique key for subscript overloading
        return $"subscript_{index}(" + string.Join(",", subscript.IndexParameters.Select(p => p.SwiftTypeSpec?.ToString() ?? "")) + ")";
    }

    private static string GetMethodVtableFieldName(MethodDecl method, int index)
    {
        return $"func_{method.Name}_{index}";
    }

    /// <summary>
    /// Gets the Swift parameter label for a method argument.
    /// Uses "_" for unlabeled parameters (Swift convention).
    /// </summary>
    private static string GetSwiftParameterLabel(ArgumentDecl param, int index)
    {
        // The parser converts "_" to "argN" for internal C# use
        // For Swift code generation, we need to convert back to "_"
        if (string.IsNullOrEmpty(param.Name) || param.Name == "_" || NameProvider.IsGeneratedArgName(param.Name))
        {
            return "_";
        }
        // Strip the underscore prefix added by ExtractUniqueName for C# keywords
        return NameProvider.StripCSharpKeywordPrefix(param.Name);
    }

    /// <summary>
    /// Gets the internal parameter name used in the implementation.
    /// </summary>
    private static string GetSwiftParameterName(ArgumentDecl param, int index)
    {
        // Use private name if available (but not _ which is a discard pattern, not a variable)
        if (!string.IsNullOrEmpty(param.PrivateName) && param.PrivateName != "_")
        {
            return param.PrivateName;
        }
        // If name looks like a generated "argN", keep using it as internal name
        if (NameProvider.IsGeneratedArgName(param.Name))
        {
            return param.Name;
        }
        // Otherwise use the public name or generate one
        if (!string.IsNullOrEmpty(param.Name) && param.Name != "_")
        {
            // Strip C# keyword prefix for Swift
            var swiftName = NameProvider.StripCSharpKeywordPrefix(param.Name);
            // If the name is a Swift keyword, use a modified internal name
            // to avoid conflicts (Swift allows keyword names with backticks, but
            // for simplicity we'll use a suffix for the internal name)
            if (NameProvider.IsSwiftKeyword(swiftName))
            {
                return $"{swiftName}Value"; // Use suffix for Swift keywords
            }
            return swiftName;
        }
        return $"arg{index}";
    }

    /// <summary>
    /// Checks if a protocol requires NSObjectProtocol identity semantics that EveryProtocol can't provide.
    /// Pure AnyObject (class-bound) protocols are allowed — EveryProtocol is a Swift class and
    /// satisfies the AnyObject constraint. Only NSObjectProtocol requires NSObject methods
    /// (isEqual:, hash, description, etc.) that EveryProtocol doesn't implement.
    /// </summary>
    /// <param name="protocolDecl">The protocol to check.</param>
    /// <param name="allProtocols">All protocols in the module for intra-module transitive lookup.
    /// If null, only direct inheritance is checked.</param>
    internal static bool IsClassBoundProtocol(ProtocolDecl protocolDecl, IReadOnlyList<ProtocolDecl>? allProtocols = null)
    {
        return IsClassBoundProtocolRecursive(protocolDecl, allProtocols, new HashSet<string>(StringComparer.Ordinal));
    }

    private static bool IsClassBoundProtocolRecursive(ProtocolDecl protocolDecl, IReadOnlyList<ProtocolDecl>? allProtocols, HashSet<string> visited)
    {
        var qualifiedName = protocolDecl.SwiftTypeName?.ToString() ?? protocolDecl.Name;
        if (!visited.Add(qualifiedName))
            return false;

        // Note: protocolDecl.IsClassBound (AnyObject / : class) is NOT a skip reason.
        // EveryProtocol is a Swift class and trivially satisfies the AnyObject constraint.
        // Only NSObjectProtocol requires NSObject identity methods that EveryProtocol can't provide.

        // Check GenericSignature for class-bound ObjC-rooted constraints (Finding 19).
        // ObjC protocols often declare constraints like "<τ_0_0 : ObjectiveC.NSObjectProtocol>"
        // or "<Self : Foundation.NSCoding>" in genericSig instead of listing them in
        // inheritedProtocols. NSCoding / NSSecureCoding / NSCopying / NSMutableCopying all refine
        // NSObjectProtocol and are equally class-bound (only an NSObject subclass can conform), so
        // they must flip class-bound here too — matching the InheritedProtocols arm below. Without
        // this, the RoomPlan.RoomCaptureViewDelegate shape (`@objc protocol X : NSCoding`, whose
        // only ObjC root lives in genericSig) is misclassified non-class-bound, the EmitProtocol-
        // Conformance _useObjCBase gate is skipped, and the reverse conformance emits on plain
        // EveryProtocol — which is not an NSObject and cannot satisfy NSCoding.
        if (protocolDecl.ParsedGenericSignature.Requirements.Any(
                r => r.TargetSimpleName is "NSObjectProtocol" or "NSCoding"
                    or "NSSecureCoding" or "NSCopying" or "NSMutableCopying"))
            return true;

        foreach (var inherited in protocolDecl.InheritedProtocols)
        {
            var name = inherited.Name;
            var simpleName = GetSimpleName(name);
            // NSObjectProtocol and other ObjC-rooted protocols require NSObject identity
            // methods that EveryProtocol (plain Swift class) cannot provide. NSCoding/NSCopying
            // /NSSecureCoding inherit from NSObjectProtocol and can only be conformed to by
            // NSObject subclasses. AnyObject is satisfied by EveryProtocol.
            if (simpleName is "NSObjectProtocol" or "NSCoding" or "NSSecureCoding" or "NSCopying" or "NSMutableCopying")
                return true;

            // Intra-module transitive check: if an inherited protocol requires NSObjectProtocol,
            // this protocol transitively requires it too.
            if (allProtocols != null)
            {
                var inheritedDecl = allProtocols.FirstOrDefault(p =>
                    p.Name == simpleName || p.Name == name ||
                    p.SwiftTypeName?.ToString() == name);
                if (inheritedDecl != null && IsClassBoundProtocolRecursive(inheritedDecl, allProtocols, visited))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true if the protocol's inheritance chain (transitively) requires
    /// <c>NSObjectProtocol</c> (optionally alongside <c>NSCoding</c>) but does NOT
    /// require NSSecureCoding / NSCopying / NSMutableCopying. Such protocols can be
    /// satisfied by routing the generated <c>extension</c> through an NSObject-rooted
    /// helper class (<c>EveryObjCProtocol</c>) instead of the plain Swift
    /// <c>EveryProtocol</c>. NSCoding is admitted because its two requirements are
    /// witnessed by a no-op stub conformance emitted on the carrier
    /// (<see cref="EmitObjCCodingStubIfNeeded"/>); NSSecureCoding / NSCopying /
    /// NSMutableCopying still additionally require encoding / copying surfaces a
    /// synthesised proxy cannot provide, so they remain in the skip path.
    /// </summary>
    internal static bool IsNSObjectProtocolOnly(ProtocolDecl protocolDecl, IReadOnlyList<ProtocolDecl>? allProtocols = null)
    {
        // Walk the inheritance chain collecting every transitive ObjC-rooted requirement.
        // If any of the disqualifying super-protocols (NSSecureCoding/NSCopying/
        // NSMutableCopying) appear at any depth, fall back to the skip path.
        var visited = new HashSet<string>(StringComparer.Ordinal);
        bool sawNSObjectProtocol = false;
        bool sawDisqualifying = false;
        CollectObjCRootKinds(protocolDecl, allProtocols, visited, ref sawNSObjectProtocol, ref sawDisqualifying);
        return sawNSObjectProtocol && !sawDisqualifying;
    }

    private static void CollectObjCRootKinds(
        ProtocolDecl protocolDecl,
        IReadOnlyList<ProtocolDecl>? allProtocols,
        HashSet<string> visited,
        ref bool sawNSObjectProtocol,
        ref bool sawDisqualifying)
    {
        var qualifiedName = protocolDecl.SwiftTypeName?.ToString() ?? protocolDecl.Name;
        if (!visited.Add(qualifiedName))
            return;

        // ObjC-rooted requirements arrive in the generic signature, NOT InheritedProtocols:
        // `@objc protocol P : NSObjectProtocol` parses to `<Self : ObjectiveC.NSObjectProtocol>`
        // and `@objc protocol P : NSCoding` (the RoomPlan.RoomCaptureViewDelegate shape) to
        // `<Self : Foundation.NSCoding>`. NSCoding is admitted (it implies NSObjectProtocol and its
        // two requirements are witnessed by the no-op EmitObjCCodingStubIfNeeded stub on the
        // carrier); NSSecureCoding / NSCopying / NSMutableCopying disqualify because they add
        // encoding / copying surfaces a synthesised proxy cannot honestly provide.
        foreach (var req in protocolDecl.ParsedGenericSignature.Requirements)
        {
            if (req.TargetSimpleName is "NSObjectProtocol" or "NSCoding")
                sawNSObjectProtocol = true;
            if (req.TargetSimpleName is "NSSecureCoding" or "NSCopying" or "NSMutableCopying")
            {
                sawDisqualifying = true;
                sawNSObjectProtocol = true;
            }
        }

        foreach (var inherited in protocolDecl.InheritedProtocols)
        {
            var simpleName = GetSimpleName(inherited.Name);
            if (simpleName == "NSObjectProtocol")
            {
                sawNSObjectProtocol = true;
                continue;
            }
            // NSCoding is satisfiable on the synthetic carrier: its two requirements
            // (encode(with:) / init?(coder:)) are witnessed by a no-op stub conformance
            // emitted alongside EveryObjCProtocol (EmitObjCCodingStubIfNeeded), so a
            // protocol whose only ObjC-rooted requirement is NSObjectProtocol + NSCoding
            // (the RoomPlan.RoomCaptureViewDelegate shape) routes through EveryObjCProtocol
            // rather than dropping. It still inherits NSObjectProtocol, so mark that.
            if (simpleName == "NSCoding")
            {
                sawNSObjectProtocol = true;
                continue;
            }
            // NSSecureCoding (adds `static var supportsSecureCoding`), NSCopying /
            // NSMutableCopying (add copy(with:) returning a real copy) require surfaces a
            // no-op synthesised proxy cannot honestly provide — keep them disqualified.
            if (simpleName is "NSSecureCoding" or "NSCopying" or "NSMutableCopying")
            {
                sawDisqualifying = true;
                // These inherit from NSObjectProtocol — we don't need to recurse
                // further to confirm the NSObjectProtocol requirement.
                sawNSObjectProtocol = true;
                continue;
            }

            if (allProtocols != null)
            {
                var inheritedDecl = allProtocols.FirstOrDefault(p =>
                    p.Name == simpleName || p.Name == inherited.Name ||
                    p.SwiftTypeName?.ToString() == inherited.Name);
                if (inheritedDecl != null)
                    CollectObjCRootKinds(inheritedDecl, allProtocols, visited, ref sawNSObjectProtocol, ref sawDisqualifying);
            }
        }
    }

    /// <summary>
    /// Checks whether the protocol's inheritance list (transitively) names a
    /// concrete class. A Swift declaration of the form
    /// <c>protocol P : SomeClass</c> constrains Self to be (a subclass of)
    /// SomeClass; EveryProtocol is a plain Swift class with no UIKit / AppKit
    /// / Foundation lineage, so any class superclass requirement makes the
    /// synthesized <c>extension EveryProtocol: P</c> unsatisfiable. The check
    /// resolves each inherited entry against the type database and reports
    /// <c>true</c> on a <see cref="TypeRecordKind.Class"/> hit; intra-module
    /// protocol transitivity is followed when <paramref name="allProtocols"/>
    /// is supplied.
    /// </summary>
    internal static bool HasClassSuperclassRequirement(
        ProtocolDecl protocolDecl,
        ITypeDatabase typeDatabase,
        IReadOnlyList<ProtocolDecl>? allProtocols = null)
    {
        return HasClassSuperclassRequirementRecursive(
            protocolDecl, typeDatabase, allProtocols,
            new HashSet<string>(StringComparer.Ordinal));
    }

    private static bool HasClassSuperclassRequirementRecursive(
        ProtocolDecl protocolDecl,
        ITypeDatabase typeDatabase,
        IReadOnlyList<ProtocolDecl>? allProtocols,
        HashSet<string> visited)
    {
        var qualifiedName = protocolDecl.SwiftTypeName?.ToString() ?? protocolDecl.Name;
        if (!visited.Add(qualifiedName))
            return false;

        foreach (var inherited in protocolDecl.InheritedProtocols)
        {
            var simpleName = GetSimpleName(inherited.Name);

            // Trivial marker / stdlib entries that are not concrete classes.
            if (simpleName is "AnyObject" or "Sendable" or "Escapable" or "Copyable"
                or "SendableMetatype" or "Error")
                continue;

            // The ABI parser records superclass conformances alongside protocol
            // conformances in InheritedProtocols. Resolve through the type
            // database — a class entry returns Kind == Class.
            if (typeDatabase.TryGetTypeRecord(inherited, out var record) &&
                record.Kind == TypeRecordKind.Class)
            {
                return true;
            }

            if (allProtocols != null)
            {
                var inheritedDecl = allProtocols.FirstOrDefault(p =>
                    p.Name == simpleName || p.Name == inherited.Name ||
                    p.SwiftTypeName?.ToString() == inherited.Name);
                if (inheritedDecl != null &&
                    HasClassSuperclassRequirementRecursive(inheritedDecl, typeDatabase, allProtocols, visited))
                {
                    return true;
                }
            }
        }

        // A class-superclass requirement is frequently recorded only in the
        // protocol's generic signature (`<Self : RealityKit.Entity>`) rather than
        // InheritedProtocols. Reading genericSig here lets the Entity-rooted routing
        // in EmitProtocolConformance fire (the gate that flips _useEntityBase is
        // behind HasClassSuperclassRequirement), and transitive protocol-typed
        // constraints are followed through allProtocols.
        if (!string.IsNullOrEmpty(protocolDecl.GenericSignature))
        {
            foreach (var constraint in ParseGenericSigConstraints(protocolDecl.GenericSignature))
            {
                var constraintSimple = GetSimpleName(constraint);

                if (constraintSimple is "AnyObject" or "Sendable" or "Escapable" or "Copyable"
                    or "SendableMetatype" or "Error")
                    continue;

                // Known @objc-rooted PROTOCOL constraints are never class superclasses. They
                // arrive in the generic signature (`<Self : Foundation.NSCoding>`) and the type
                // database can mis-resolve an out-of-module Foundation name like NSCoding to a
                // spurious concrete-class fallback record (Kind == Class) below, which would
                // misclassify the protocol as class-superclass-constrained and force it onto the
                // read-only / skip path. NSObjectProtocol (ObjectiveC) resolves correctly and never
                // reaches here, but NSCoding / NSSecureCoding / NSCopying / NSMutableCopying can — so
                // treat them as the protocols they are and let CollectObjCRootKinds route them
                // through the NSObject-rooted EveryObjCProtocol carrier instead.
                if (constraintSimple is "NSObjectProtocol" or "NSCoding" or "NSSecureCoding"
                    or "NSCopying" or "NSMutableCopying")
                    continue;

                if (IsRealityFoundationEntityName(constraint, constraintSimple))
                    return true;

                // Protocol-typed constraints (resolvable in the module protocol list)
                // are followed transitively; checking allProtocols before the
                // TypeDatabase class lookup avoids a concreteClassFallback module
                // synthesizing a spurious Class record for a protocol-typed name.
                if (allProtocols != null)
                {
                    var constraintDecl = allProtocols.FirstOrDefault(p =>
                        p.Name == constraintSimple || p.Name == constraint ||
                        p.SwiftTypeName?.ToString() == constraint);
                    if (constraintDecl != null)
                    {
                        if (HasClassSuperclassRequirementRecursive(constraintDecl, typeDatabase, allProtocols, visited))
                            return true;
                        continue;
                    }
                }

                if (typeDatabase.TryGetTypeRecord(new NamedTypeSpec(constraint), out var constraintRecord) &&
                    constraintRecord.Kind == TypeRecordKind.Class)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true if the protocol's class-superclass requirement is exactly
    /// RealityFoundation's <c>Entity</c> (or its umbrella spelling
    /// <c>RealityKit.Entity</c>) and no other concrete class is required. Such
    /// protocols can be routed through the Entity-rooted <c>EveryEntityProtocol</c>
    /// helper class instead of being skipped by
    /// <see cref="HasClassSuperclassRequirement"/>. A protocol that requires both
    /// Entity and another concrete class is not Entity-rooted (the helper can
    /// inherit only one superclass).
    /// </summary>
    /// <remarks>
    /// Recognized name shapes (both spellings appear in ABI JSON depending on
    /// whether the protocol is declared in RealityFoundation directly or surfaced
    /// through the RealityKit umbrella):
    /// <list type="bullet">
    /// <item><c>RealityFoundation.Entity</c></item>
    /// <item><c>RealityKit.Entity</c></item>
    /// </list>
    /// </remarks>
    internal static bool IsEntityRootedProtocol(
        ProtocolDecl protocolDecl,
        ITypeDatabase typeDatabase,
        IReadOnlyList<ProtocolDecl>? allProtocols = null)
    {
        bool sawEntity = false;
        bool sawOtherClass = false;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        CollectClassSuperclassKinds(
            protocolDecl, typeDatabase, allProtocols, visited,
            ref sawEntity, ref sawOtherClass);
        return sawEntity && !sawOtherClass;
    }

    private static void CollectClassSuperclassKinds(
        ProtocolDecl protocolDecl,
        ITypeDatabase typeDatabase,
        IReadOnlyList<ProtocolDecl>? allProtocols,
        HashSet<string> visited,
        ref bool sawEntity,
        ref bool sawOtherClass)
    {
        var qualifiedName = protocolDecl.SwiftTypeName?.ToString() ?? protocolDecl.Name;
        if (!visited.Add(qualifiedName))
            return;

        foreach (var inherited in protocolDecl.InheritedProtocols)
        {
            var simpleName = GetSimpleName(inherited.Name);

            if (simpleName is "AnyObject" or "Sendable" or "Escapable" or "Copyable"
                or "SendableMetatype" or "Error")
                continue;

            if (typeDatabase.TryGetTypeRecord(inherited, out var record) &&
                record.Kind == TypeRecordKind.Class)
            {
                if (IsRealityFoundationEntityName(inherited.Name, simpleName))
                    sawEntity = true;
                else
                    sawOtherClass = true;
                continue;
            }

            if (allProtocols != null)
            {
                var inheritedDecl = allProtocols.FirstOrDefault(p =>
                    p.Name == simpleName || p.Name == inherited.Name ||
                    p.SwiftTypeName?.ToString() == inherited.Name);
                if (inheritedDecl != null)
                {
                    CollectClassSuperclassKinds(
                        inheritedDecl, typeDatabase, allProtocols, visited,
                        ref sawEntity, ref sawOtherClass);
                }
            }
        }

        // The ABI records class-superclass and protocol Self-constraints in the
        // protocol's generic signature (`<Self : RealityKit.Entity>`,
        // `<Self : RealityKit.HasTransform>`) rather than InheritedProtocols — this
        // is the real shape for RealityFoundation.HasTransform / HasAnchoring /
        // HasCollision. Walk genericSig too so the Entity root is found when it is
        // reachable only through genericSig, directly or transitively via a
        // protocol-typed constraint.
        if (!string.IsNullOrEmpty(protocolDecl.GenericSignature))
        {
            foreach (var constraint in ParseGenericSigConstraints(protocolDecl.GenericSignature))
            {
                var constraintSimple = GetSimpleName(constraint);

                if (constraintSimple is "AnyObject" or "Sendable" or "Escapable" or "Copyable"
                    or "SendableMetatype" or "Error")
                    continue;

                // Name-first so an Entity root is recognized even when its
                // cross-module TypeRecord is not loaded in this wrapper build.
                if (IsRealityFoundationEntityName(constraint, constraintSimple))
                {
                    sawEntity = true;
                    continue;
                }

                // A name present in the module's own protocol list is definitively a
                // protocol — recurse into it. This must precede the TypeDatabase class
                // check: concreteClassFallback modules (e.g. RealityKit) synthesize a
                // Class record for any unregistered umbrella-qualified name, which would
                // otherwise mis-classify a protocol-typed constraint (RealityKit.HasTransform)
                // as a foreign class superclass and hide the transitive Entity root.
                if (allProtocols != null)
                {
                    var constraintDecl = allProtocols.FirstOrDefault(p =>
                        p.Name == constraintSimple || p.Name == constraint ||
                        p.SwiftTypeName?.ToString() == constraint);
                    if (constraintDecl != null)
                    {
                        CollectClassSuperclassKinds(
                            constraintDecl, typeDatabase, allProtocols, visited,
                            ref sawEntity, ref sawOtherClass);
                        continue;
                    }
                }

                if (typeDatabase.TryGetTypeRecord(new NamedTypeSpec(constraint), out var constraintRecord) &&
                    constraintRecord.Kind == TypeRecordKind.Class)
                {
                    sawOtherClass = true;
                }
            }
        }
    }

    private static bool IsRealityFoundationEntityName(string fullName, string simpleName)
    {
        if (simpleName != "Entity")
            return false;
        return fullName == "RealityFoundation.Entity"
            || fullName == "RealityKit.Entity"
            || fullName == "Entity";
    }

    /// <summary>
    /// True when RealityFoundation.Entity already conforms to <paramref name="protocolDecl"/>,
    /// meaning the Entity-rooted carrier inherits the conformance and must NOT re-declare it.
    /// Compared by simple name against Entity's transitive conformance closure.
    /// </summary>
    private bool EntityBaseConformsTo(ProtocolDecl protocolDecl)
    {
        _entityBaseConformanceClosure ??= ComputeEntityBaseConformanceClosure();
        var simple = GetSimpleName(protocolDecl.SwiftTypeName?.ToString() ?? protocolDecl.Name);
        return _entityBaseConformanceClosure.Contains(simple);
    }

    /// <summary>
    /// Walks RealityFoundation.Entity's <see cref="TypeRecord.ProtocolConformances"/> graph and
    /// returns the transitive set of conformed-protocol simple names. The walk expands each
    /// conformance's own ProtocolConformances so a refinement Entity satisfies transitively
    /// (e.g. <c>HasTransform : SomeBase</c>) is also treated as inherited. The first Entity
    /// spelling whose record carries a populated conformance list wins — the real
    /// RealityFoundation.Entity record, not a <c>concreteClassFallback</c>-synthesized
    /// RealityKit.Entity stub that has no conformances.
    /// </summary>
    private HashSet<string> ComputeEntityBaseConformanceClosure()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<SwiftTypeName>();

        foreach (var entityName in new[] { "RealityFoundation.Entity", "RealityKit.Entity" })
        {
            if (_typeDatabase.TryGetTypeRecord(new NamedTypeSpec(entityName), out var rec) &&
                rec.ProtocolConformances is { Count: > 0 } confs)
            {
                foreach (var c in confs)
                    queue.Enqueue(c);
                break;
            }
        }

        while (queue.Count > 0)
        {
            var conformance = queue.Dequeue();
            var simple = GetSimpleName(conformance.ToString());
            if (!result.Add(simple))
                continue;
            if (_typeDatabase.TryGetTypeRecord(conformance, out var rec) &&
                rec.ProtocolConformances is { Count: > 0 } refines)
            {
                foreach (var r in refines)
                    queue.Enqueue(r);
            }
        }

        return result;
    }

    /// <summary>
    /// Checks if a protocol's genericSig constrains Self (τ_0_0) to conform to a protocol that
    /// EveryProtocol can't satisfy. This is the genericSig ARM of
    /// <see cref="HasUnavailableInheritedConformance"/> — prefer that predicate for gating
    /// decisions; this one exists for callers that mean the genericSig edge specifically.
    /// </summary>
    internal bool HasUnsatisfiedProtocolConstraintInGenericSig(ProtocolDecl protocolDecl)
    {
        if (string.IsNullOrEmpty(protocolDecl.GenericSignature))
            return false;

        foreach (var constraint in ParseGenericSigConstraints(protocolDecl.GenericSignature))
        {
            if (IsInheritedConformanceUnavailable(constraint, protocolDecl))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Trivial protocols whose conformance never needs a synthesized witness — markers the
    /// compiler satisfies implicitly, plus the ones a carrier class supplies for free
    /// (<c>AnyObject</c>/<c>NSObjectProtocol</c> on a class, <c>Error</c> via a stub).
    /// </summary>
    private static readonly HashSet<string> s_trivialInheritedProtocols = new(StringComparer.Ordinal)
    {
        "Copyable", "Escapable", "Sendable", "SendableMetatype",
        "AnyObject", "Error", "NSObjectProtocol"
    };

    /// <summary>
    /// Decides whether conforming to <paramref name="constraint"/> would leave the carrier without a
    /// witness — i.e. no <c>extension {carrier}: {constraint}</c> will exist and the constraint is not
    /// satisfied by other means. This is the SINGLE fact behind every inherited-conformance decision;
    /// both edges that can carry an inheritance (the protocol's <c>genericSig</c> <c>Self :</c> clauses
    /// and its parsed <see cref="ProtocolDecl.InheritedProtocols"/>) resolve through it, so the two
    /// can't drift into disagreeing about the same parent.
    ///
    /// Declaring <c>extension EveryProtocol: Child</c> makes Swift require the inherited conformance
    /// too, but the extension body only witnesses <c>Child</c>'s OWN members — so an unavailable parent
    /// surfaces as "type 'EveryProtocol' does not conform to protocol '{parent}'" at wrapper compile.
    /// </summary>
    private bool IsInheritedConformanceUnavailable(string constraint, ProtocolDecl protocolDecl)
    {
        // Check unqualified names
        if (s_trivialInheritedProtocols.Contains(constraint))
            return false;

        var constraintSimple = GetSimpleName(constraint);

        // A class-superclass constraint on RealityFoundation.Entity (umbrella
        // spelling RealityKit.Entity) is satisfiable through the Entity-rooted
        // EveryEntityProtocol helper — it is NOT an unsatisfied protocol
        // constraint. Likewise a protocol-typed constraint that is itself
        // Entity-rooted (e.g. RealityKit.HasTransform) is satisfied transitively.
        // Without these allowances, the autoBridge / optionalFallback module gate
        // below would mis-skip every Entity-rooted RealityFoundation protocol
        // (their genericSig names the root as RealityKit.Entity / RealityKit.*).
        if (IsRealityFoundationEntityName(constraint, constraintSimple))
            return false;
        if (_allProtocols != null)
        {
            var constraintDecl = _allProtocols.FirstOrDefault(p =>
                p.Name == constraintSimple || p.Name == constraint ||
                p.SwiftTypeName?.ToString() == constraint);
            if (constraintDecl != null &&
                IsEntityRootedProtocol(constraintDecl, _typeDatabase, _allProtocols))
                return false;
        }

        var dotIdx = constraint.IndexOf('.');
        if (dotIdx < 0)
        {
            // Unqualified name — check if it's a same-module protocol that will get no witness,
            // either because a structural gate skipped it or because it never reached emission.
            return IsUnwitnessed(constraint);
        }

        var moduleName = constraint.Substring(0, dotIdx);
        var typeName = constraint.Substring(dotIdx + 1);

        // Qualified form of a trivial protocol (e.g. ObjectiveC.NSObjectProtocol)
        // is also satisfied — the trivial set above is consulted as the unqualified
        // typeName here. Without this branch, NSObjectProtocol-only @objc
        // protocols would still skip below because their protocol-level genericSig
        // names the constraint as ObjectiveC.NSObjectProtocol, which the unqualified
        // trivial-set check at the top never sees.
        if (s_trivialInheritedProtocols.Contains(typeName))
            return false;

        // Foundation.NSCoding is satisfiable on the NSObject-rooted EveryObjCProtocol carrier
        // via the no-op stub conformance (EmitObjCCodingStubIfNeeded), so a `Self : NSCoding`
        // constraint is NOT unsatisfied when the protocol routes through that carrier (the
        // RoomPlan.RoomCaptureViewDelegate shape). Gated on IsNSObjectProtocolOnly so the
        // disqualifying refinements (NSSecureCoding / NSCopying / NSMutableCopying, which make
        // that predicate false) still fall through to the Foundation skip below. Mirrors the
        // NSObjectProtocol trivial-set allowance above.
        if (typeName == "NSCoding" && IsNSObjectProtocolOnly(protocolDecl, _allProtocols))
            return false;

        // Codable and its halves get explicit EveryProtocol stub conformances
        // (EmitCodableStubsIfNeeded), so inheriting them is satisfied rather than unavailable.
        // The genericSig arm never needed this — Swift.Decodable's module isn't autoBridge and
        // its typeName doesn't start with "_", so it fell through to `return false` anyway — but
        // the InheritedProtocols arm reaches here for the same edge and must agree.
        if (s_codableStubbedProtocols.Contains(typeName))
            return false;

        // Case 1: Known ObjC/Apple framework module
        if (AppleFrameworkRegistry.IsAutoBridgeModule(moduleName) ||
            AppleFrameworkRegistry.IsOptionalFallbackModule(moduleName) ||
            moduleName == "ObjectiveC" || moduleName == "Foundation")
        {
            return true;
        }

        // Case 2: a protocol that will get no witness.
        //
        // The bare typeName arm is LOAD-BEARING, not a same-module convenience — do not restrict it
        // to `moduleName == _moduleName`. The unwitnessed sets are NOT module-local: a module's ABI
        // lists foreign protocols it conforms to or extends, and those lose candidacy (their mangled
        // name fails the module prefix) and get seeded here. Such a parent genuinely has no witness,
        // so its child must skip — but its two spellings disagree. ChartView is the worked example:
        // the ABI declares `View` with moduleName `SwiftUICore` and mangled `$s7SwiftUI4ViewP`, so
        // the qualified seed reads `SwiftUICore.View` while the child's constraint spells the
        // re-exported `SwiftUI.View`. Only the bare `View` bridges the two; drop it and the child
        // emits `extension EveryProtocol: ChartBase` against an unsatisfiable `View` requirement.
        //
        // The cost is a known false positive: a dropped local `Foo` also makes a live cross-module
        // `Dep.Foo` parent look unwitnessed and over-skips its child. That needs a real conformer
        // lookup to separate (ResolveInheritedConformer), not a module comparison — a module
        // comparison rejects the re-exported spellings this arm exists to catch.
        if (IsUnwitnessed(constraint) || IsUnwitnessed(typeName))
            return true;

        // Case 3: Underscore-prefixed internal protocol from external module.
        // These are often ObjC protocol backing types (e.g., ExternalModule._internal_SomeDelegate)
        // that we can't inspect. Conservative: skip rather than emit broken conformance.
        if (moduleName != _moduleName && typeName.StartsWith("_"))
            return true;

        return false;
    }

    /// <summary>
    /// Whether no <c>extension {carrier}: {name}</c> will be emitted for this protocol — it was
    /// either skipped by a structural gate at emission time (<see cref="_skippedProtocols"/>) or
    /// never offered for emission at all (<see cref="_unavailableConformances"/>).
    /// </summary>
    private bool IsUnwitnessed(string name)
        => _skippedProtocols.Contains(name) || _unavailableConformances.Contains(name);

    /// <summary>
    /// Protocols whose conformance EveryProtocol carries via an emitted stub rather than a
    /// per-protocol witness extension (see <c>EmitCodableStubsIfNeeded</c>).
    /// </summary>
    private static readonly HashSet<string> s_codableStubbedProtocols = new(StringComparer.Ordinal)
    {
        "Codable", "Decodable", "Encodable"
    };

    /// <summary>
    /// Whether any protocol this one inherits will be left without a witness on the carrier.
    /// Unions the two independent channels that can record an inheritance — the <c>genericSig</c>
    /// <c>Self :</c> clauses and the parsed <see cref="ProtocolDecl.InheritedProtocols"/> — because
    /// the parser populates them from DIFFERENT ABI fields and they are not interchangeable: a
    /// protocol can carry an empty genericSig while declaring inheritance through conformances,
    /// marker protocols are filtered out of InheritedProtocols, and superclass constraints appear
    /// only in the genericSig. Both planes that decide stub synthesis read THIS one predicate.
    /// </summary>
    internal bool HasUnavailableInheritedConformance(ProtocolDecl protocolDecl)
    {
        if (HasUnsatisfiedProtocolConstraintInGenericSig(protocolDecl))
            return true;

        foreach (var inherited in protocolDecl.InheritedProtocols)
        {
            // NamedTypeSpec.Name carries the same possibly-module-qualified spelling the
            // genericSig constraint strings use, so both arms feed the predicate one shape.
            if (IsInheritedConformanceUnavailable(inherited.Name, protocolDecl))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The verbatim conformance targets of every DIRECT Self / τ_0_0 constraint in a genericSig
    /// (Finding 19). swift-api-digester uses both spellings: protocol declarations carry
    /// <c>&lt;Self : Foo&gt;</c> while bound generic signatures use the substituted form
    /// <c>&lt;τ_0_0 : Foo&gt;</c>. The <c>Self.Member</c> dotted form is excluded — that targets an
    /// associated type, not Self itself (the parsed model marks such a clause non-direct).
    /// </summary>
    private static IEnumerable<string> ParseGenericSigConstraints(string sig)
        => GenericSignatureParser.ParseSignature(sig).DirectConformanceTargets("τ_0_0", "Self");

    /// <summary>
    /// Checks if a protocol is or inherits from CaseIterable, directly or transitively.
    /// </summary>
    internal static bool InheritsCaseIterable(ProtocolDecl protocolDecl, IReadOnlyList<ProtocolDecl>? allProtocols = null)
    {
        return InheritsCaseIterableRecursive(protocolDecl, allProtocols, new HashSet<string>(StringComparer.Ordinal));
    }

    private static bool InheritsCaseIterableRecursive(ProtocolDecl protocolDecl, IReadOnlyList<ProtocolDecl>? allProtocols, HashSet<string> visited)
    {
        var qualifiedName = protocolDecl.SwiftTypeName?.ToString() ?? protocolDecl.Name;
        if (!visited.Add(qualifiedName))
            return false;

        if (protocolDecl.Name == "CaseIterable")
            return true;

        foreach (var inherited in protocolDecl.InheritedProtocols)
        {
            var name = inherited.Name;
            var simpleName = GetSimpleName(name);
            if (simpleName == "CaseIterable")
                return true;

            if (allProtocols != null)
            {
                var inheritedDecl = allProtocols.FirstOrDefault(p =>
                    p.Name == simpleName || p.Name == name ||
                    p.SwiftTypeName?.ToString() == name);
                if (inheritedDecl != null && InheritsCaseIterableRecursive(inheritedDecl, allProtocols, visited))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true if the named protocol's EveryProtocol conformance has already been
    /// classified as un-emittable by <see cref="PreScanProtocols"/> (HasSelfRequirement,
    /// HasMissingRequirements, HasConventionCClosureParameters, HasSuppressedRequiredMember,
    /// HasUnsatisfiedHiddenRequirements, HasMissingTbdMethodDescriptors,
    /// HasSubscriptLevelGenericDependentMember, HasNoncopyableMember, or transitive genericSig
    /// propagation).
    /// Sibling-plan input must filter these out: if such a protocol won ownership, the owner
    /// body would never land in the wrapper and the (otherwise emittable) siblings would skip
    /// their own bodies, leaving the entire group with no usable witness.
    /// </summary>
    public bool IsConformanceSkipped(ProtocolDecl protocolDecl)
    {
        if (_skippedProtocols.Contains(protocolDecl.Name))
            return true;
        if (protocolDecl.SwiftTypeName != null &&
            _skippedProtocols.Contains(protocolDecl.SwiftTypeName.ModuleQualifiedName))
            return true;
        return false;
    }

    /// <summary>
    /// Returns true if any instance method, property accessor, or subscript on the protocol
    /// has a noncopyable parameter or return type. The EveryProtocol trampoline uses inout
    /// pointers via a local-var copy, which the compiler rejects for ~Copyable types.
    /// Suppressing the entire conformance avoids generating ill-formed Swift in the wrapper.
    /// </summary>
    internal bool HasNoncopyableMember(ProtocolDecl protocolDecl)
        => HasNoncopyableMember(protocolDecl, _typeDatabase);

    /// <summary>
    /// Static counterpart to the instance <see cref="HasNoncopyableMember(ProtocolDecl)"/>
    /// gate so callers without an <c>EveryProtocolEmitter</c> instance (e.g.
    /// <c>ModuleHandler</c> when filtering the input to
    /// <see cref="ComputePropertyEmissionPlans"/> /
    /// <see cref="ComputeSubscriptEmissionPlans"/>) can apply the same emittability
    /// check. Mirrors the early-return at the top of <c>EmitProtocolConformance</c>:
    /// protocols whose method/property/subscript signatures touch a noncopyable type
    /// have their EveryProtocol conformance suppressed because the wrapper's
    /// <c>inout</c> trampoline cannot copy the value across the C# boundary. Keeping
    /// these protocols out of the sibling plan input prevents the owner body from
    /// referencing a vtable that the wrapper never emits.
    /// </summary>
    public static bool HasNoncopyableMember(ProtocolDecl protocolDecl, ITypeDatabase typeDatabase)
    {
        bool IsNoncopyable(TypeSpec? spec)
        {
            if (spec == null || spec.IsEmptyTuple) return false;
            return WrapperValidation.IsNonCopyableType(spec, typeDatabase, protocolDecl.ModuleDecl);
        }

        foreach (var method in protocolDecl.Methods)
        {
            if (method.IsConstructor || method.MethodType == MethodType.Static) continue;
            foreach (var param in method.CSSignature.Skip(1))
            {
                if (IsNoncopyable(param.SwiftTypeSpec)) return true;
            }
            var returnSpec = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
            if (IsNoncopyable(returnSpec)) return true;
        }

        foreach (var property in protocolDecl.Properties)
        {
            if (property.IsStatic) continue;
            if (IsNoncopyable(property.SwiftTypeSpec)) return true;
        }

        foreach (var subscript in protocolDecl.Subscripts)
        {
            if (subscript.IsStatic) continue;
            foreach (var idx in subscript.IndexParameters)
            {
                if (IsNoncopyable(idx.SwiftTypeSpec)) return true;
            }
            if (IsNoncopyable(subscript.ReturnTypeSpec)) return true;
        }

        return false;
    }

    /// <summary>
    /// A protocol that CANNOT host an EveryProtocol reverse-dispatch conformance for a
    /// forward-SAFE structural reason, yet whose <c>any P</c> existential is still a valid
    /// READ target through its own witness table. These get a forward-only (read-only) proxy
    /// instead of being fully suppressed — the suppression turned a getter returning
    /// <c>any P</c> / <c>[any P]</c> / <c>(any P)?</c> into a throwing
    /// <c>NotSupportedException</c> stub (the RealityFoundation <c>Material</c> /
    /// <c>PhysicsJoint</c> shape).
    ///
    /// The forward-safe reasons are an explicit allowlist, NOT every reverse-conformance
    /// skip:
    ///   - <see cref="ProtocolDecl.HasUnsatisfiedHiddenRequirements"/> — an <c>__</c>-prefixed
    ///     requirement swift-api-digester strips from the ABI JSON. It only concerns the
    ///     reverse witness; the existential reads fine.
    ///   - <see cref="InheritsUnsatisfiedStdlibProtocol"/> — inherits a stdlib protocol
    ///     (Equatable / Hashable / CustomStringConvertible / …) EveryProtocol can't synthesize.
    ///     The non-inherited members are still forward-dispatchable.
    ///
    /// Forward-UNSAFE skip reasons are deliberately excluded: missing/suppressed requirements,
    /// missing TBD method descriptors, and <c>@convention(c)</c> closure parameters all signal
    /// incomplete or unavailable ABI surface, so a forward read would move the failure from
    /// conformance compilation to wrapper link / runtime. Superclass-constrained protocols are
    /// excluded here because they are the ORIGINAL read-only population, admitted by the
    /// caller's class-superclass branch (this predicate is the disjoint forward-safe arm).
    /// Protocols with their own or inherited associated types are excluded by the caller (their
    /// interface is generic and cannot host the proxy's [DllImport]/[UnmanagedCallersOnly]
    /// members).
    /// </summary>
    internal static bool HasForwardSafeReverseImpossibleReason(
        ProtocolDecl protocolDecl,
        ITypeDatabase typeDatabase,
        IReadOnlyList<ProtocolDecl>? allProtocols = null)
    {
        // The original read-only population is the class-superclass arm in the caller; this
        // predicate is the disjoint non-class arm, so a class-superclass requirement disqualifies.
        if (HasClassSuperclassRequirement(protocolDecl, typeDatabase, allProtocols))
            return false;

        // Must be reverse-impossible for a forward-SAFE reason: a stripped `__`-prefixed hidden
        // requirement, or inheritance of an unsatisfiable stdlib protocol. Both leave `any P` a
        // valid read target through its own witness table.
        if (!protocolDecl.HasUnsatisfiedHiddenRequirements
            && !InheritsUnsatisfiedStdlibProtocol(protocolDecl, allProtocols))
            return false;

        // ...AND carry NO forward-UNSAFE skip reason. A protocol can be reverse-impossible for a
        // forward-safe reason AND simultaneously carry one of these signals (e.g.
        // `P: CustomStringConvertible` whose required method has no TBD descriptor). The forward
        // read dispatches each readable member through the existential's own witness table, but
        // these signals mean a required member's ABI surface is missing/unavailable, so a forward
        // read of THAT member moves the failure from conformance compilation to wrapper link /
        // runtime. Fail closed — keep the full suppression (throwing stub) for the mixed case.
        // Mirrors the forward-unsafe subset of WillSkipConformance; HasSelfRequirement, associated
        // types, and class-bound protocols are already excluded by the caller's filter chain.
        //
        // IsMixedGenericProtocol is the method-level companion of the subscript-generic exclusion
        // below: when a method-level-generic requirement coexists with a non-generic instance
        // member, the Swift wrapper emits NO witness-dispatch accessors for the WHOLE protocol
        // (EmitWitnessDispatchFunctions is gated protocol-wide on !IsMixedGenericProtocol because
        // the type-projection pipeline mis-renders the non-generic members when a method-level
        // generic is in scope). The C# forward-read proxy gates per-member, so its plain
        // dispatchable property/method would still emit a NativeMethods P/Invoke whose @_cdecl
        // target was never generated -> runtime EntryPointNotFoundException. Fail closed.
        if (protocolDecl.HasMissingRequirements
            || protocolDecl.HasConventionCClosureParameters
            || protocolDecl.HasMissingTbdMethodDescriptors
            || HasSuppressedRequiredMember(protocolDecl)
            || IsMixedGenericProtocol(protocolDecl)
            || protocolDecl.Subscripts.Any(s => !s.IsStatic && HasSubscriptLevelGenericDependentMember(s)))
            return false;

        return true;
    }

    /// <summary>
    /// Checks if a protocol inherits (directly or transitively) from a standard library
    /// protocol that has requirements EveryProtocol can't satisfy. These protocols have
    /// property or initializer requirements that aren't included in the vtable.
    /// </summary>
    internal static bool InheritsUnsatisfiedStdlibProtocol(ProtocolDecl protocolDecl, IReadOnlyList<ProtocolDecl>? allProtocols = null)
    {
        return InheritsUnsatisfiedStdlibProtocolRecursive(protocolDecl, allProtocols, new HashSet<string>(StringComparer.Ordinal));
    }

    /// <summary>
    /// Set of stdlib protocol names whose requirements EveryProtocol can't implement.
    /// These protocols require properties (description), initializers (init(from:)),
    /// or static members that can't be provided via the vtable callback pattern.
    /// Note: Codable (Decodable/Encodable) and Error are handled separately via Codable stubs.
    /// </summary>
    private static readonly HashSet<string> s_unsatisfiedStdlibProtocols = new(StringComparer.Ordinal)
    {
        "CustomStringConvertible",
        "CustomDebugStringConvertible",
        "LosslessStringConvertible",
        "CodingKey",
        "RawRepresentable",
        "ExpressibleByStringLiteral",
        "ExpressibleByIntegerLiteral",
        "ExpressibleByFloatLiteral",
        "ExpressibleByBooleanLiteral",
        "ExpressibleByNilLiteral",
        "ExpressibleByArrayLiteral",
        "ExpressibleByDictionaryLiteral",
        "ExpressibleByStringInterpolation",
        "ExpressibleByUnicodeScalarLiteral",
        "ExpressibleByExtendedGraphemeClusterLiteral",
        "Strideable",
        "AdditiveArithmetic",
        "Numeric",
        "IteratorProtocol",
        "Hashable",
        "Equatable",
        "Comparable",
    };

    private static bool InheritsUnsatisfiedStdlibProtocolRecursive(ProtocolDecl protocolDecl, IReadOnlyList<ProtocolDecl>? allProtocols, HashSet<string> visited)
    {
        var qualifiedName = protocolDecl.SwiftTypeName?.ToString() ?? protocolDecl.Name;
        if (!visited.Add(qualifiedName))
            return false;

        if (s_unsatisfiedStdlibProtocols.Contains(protocolDecl.Name) && IsSwiftStdlibProtocol(protocolDecl))
            return true;

        foreach (var inherited in protocolDecl.InheritedProtocols)
        {
            var name = inherited.Name;
            var simpleName = GetSimpleName(name);

            // Only short-circuit for explicitly Swift-module-qualified names.
            // Unqualified names (no dot) must fall through to the allProtocols
            // recursive lookup to disambiguate library-defined protocols with
            // the same name (e.g., a library-local "Hashable" vs Swift.Hashable).
            if (s_unsatisfiedStdlibProtocols.Contains(simpleName) &&
                name.StartsWith("Swift.", StringComparison.Ordinal))
                return true;

            // For non-Swift-qualified names (including unqualified), resolve via
            // the allProtocols list which has full ProtocolDecl with module info.
            if (allProtocols != null)
            {
                var inheritedDecl = allProtocols.FirstOrDefault(p =>
                    p.Name == simpleName || p.Name == name ||
                    p.SwiftTypeName?.ToString() == name);
                if (inheritedDecl != null && InheritsUnsatisfiedStdlibProtocolRecursive(inheritedDecl, allProtocols, visited))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true if a protocol declaration is from the Swift standard library.
    /// Prevents false positives where a library defines a protocol with a common
    /// stdlib name (e.g., "Hashable").
    /// </summary>
    private static bool IsSwiftStdlibProtocol(ProtocolDecl protocolDecl)
    {
        // If we have module info, verify it's Swift
        if (protocolDecl.SwiftTypeName != null)
            return protocolDecl.SwiftTypeName.Module == "Swift";
        // If no module info, check mangled name prefix ($ss = Swift stdlib)
        if (!string.IsNullOrEmpty(protocolDecl.MangledName))
            return ManglingProbes.IsStdlibMangledName(protocolDecl.MangledName);
        // No module info available — assume stdlib for backward compat
        return true;
    }

    /// <summary>
    /// Gets the simple (unqualified) name from a potentially module-qualified type name.
    /// </summary>
    private static string GetSimpleName(string name)
    {
        var dotIndex = name.LastIndexOf('.');
        return dotIndex >= 0 ? name.Substring(dotIndex + 1) : name;
    }

    #endregion
}
