// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text.RegularExpressions;

namespace BindingsGeneration;

/// <summary>
/// Per-module emission context replacing static mutable state across emitters.
///
/// Created fresh for each module in Program.cs. Provides typed dedup APIs
/// (HasEmitted*/TryAdd*) instead of raw collection access, ensuring encapsulation
/// and preventing callers from accidentally clearing or misusing state.
///
/// Uses typed dedup APIs instead of raw collection access to prevent static mutable state from leaking across modules.
/// </summary>
public sealed class ModuleEmissionContext
{
    /// <summary>
    /// True for a context nobody supplied — one manufactured at a call site that was handed
    /// <c>null</c>. Emitters that behave differently when no module context was threaded (the
    /// EveryProtocol vtable / read-only-proxy admission gates, which have nothing to consult)
    /// test this flag.
    /// </summary>
    /// <remarks>
    /// The flag exists so that "no context was supplied" can be a property of a context rather
    /// than the identity of one process-wide instance. A single shared fallback puts every
    /// concurrent emission in one set of dedup registries and per-module accumulators — and those
    /// accumulators are enumerated (the module initializer walks the emitted-type and
    /// payload-semantics lists) while another emission is still adding to them.
    /// </remarks>
    public bool IsImplicitFallback { get; }

    /// <summary>Creates a context for a caller that supplied none. See <see cref="IsImplicitFallback"/>.</summary>
    public static ModuleEmissionContext CreateImplicitFallback() => new(implicitFallback: true);

    /// <summary>Creates a per-module emission context.</summary>
    public ModuleEmissionContext()
    {
    }

    private ModuleEmissionContext(bool implicitFallback) => IsImplicitFallback = implicitFallback;

    // ==================== Module / Type Name Collision ====================

    /// <summary>
    /// Resolved C# namespace for the current module. Identity (== <see cref="ModuleDecl.Name"/>)
    /// under the default <c>{Module}</c> pattern; differs when a binding project sets
    /// <c>&lt;NamespacePattern&gt;</c> to something else (e.g. StoreKit2's csproj maps Swift
    /// module <c>StoreKit</c> to C# namespace <c>StoreKit2</c>). Used by emitters that
    /// generate cross-references to module-scoped helpers/types where the <c>global::</c>
    /// prefix must point at the C# namespace, not the raw Swift module name.
    /// Set once at the start of <c>ModuleHandler.Emit</c>.
    /// </summary>
    public string? ResolvedNamespace { get; set; }

    // ==================== File-per-top-level-type split ====================
    //
    // These offsets/spans are recorded into the shared C# output buffer during
    // ModuleHandler.Emit so ModuleEmitter can slice the byte-identical combined
    // output into one file per top-level type. All are character offsets into the
    // PRE-QualifyNamespaceReferences string (ModuleEmitter slices that string, then
    // re-runs the namespace qualifier per file). Null offsets / an empty span list
    // mean "no split data recorded" — ModuleEmitter falls back to a single file.

    /// <summary>Offset just past <c>namespace X {</c> (the namespace body start).</summary>
    public int? EmissionNamespaceBodyStart { get; set; }

    /// <summary>Offset just before the namespace's closing <c>}</c>.</summary>
    public int? EmissionNamespaceBodyEnd { get; set; }

    /// <summary>Offset just past the namespace's closing <c>}</c> (start of the SwiftInterop trailer).</summary>
    public int? EmissionNamespaceCloseEnd { get; set; }

    /// <summary>
    /// One entry per emitted top-level type, in deterministic emission (topo) order:
    /// the C#-name-derived file leaf and the type's half-open character span in the
    /// pre-qualify output. Recorded only at the outermost HandleBaseDecl walk; nested
    /// types stay inside their parent's span.
    /// </summary>
    public List<(string TypeName, int Start, int End)> TopLevelTypeSpans { get; } = new();

    /// <summary>
    /// Provenance for the files this module's most recent render actually wrote: which artifact
    /// produced each character range of each file. Null until a render completes.
    /// </summary>
    /// <remarks>
    /// Replaced wholesale on every render rather than updated in place. The map is only meaningful
    /// against the exact text it was built from, so a stale one is not a degraded map but a wrong
    /// one — it would attribute a diagnostic about the current file to whatever occupied that
    /// position in the previous attempt.
    /// </remarks>
    public ModuleFragmentSet? FragmentSet { get; private set; }

    /// <summary>Publishes the fragment set for the render that just finished.</summary>
    public void PublishFragmentSet(ModuleFragmentSet fragmentSet) => FragmentSet = fragmentSet;

    /// <summary>
    /// Drops any published map, at the point a render begins.
    /// </summary>
    /// <remarks>
    /// Publication happens only when a render reaches the end, so without this a render that throws
    /// part-way leaves its predecessor's map in place and nothing downstream can tell it is stale.
    /// That is reachable on the shared <see cref="Default"/> context, where one context outlives many
    /// modules — the map would then describe a different module's text entirely.
    /// </remarks>
    public void BeginFragmentRender() => FragmentSet = null;

    /// <summary>
    /// Load-time runtime-contract <em>epoch</em> to emit into this module's
    /// <c>RuntimeContract.AssertCompatible(...)</c> call, derived (<c>major*1000 + minor</c>) from
    /// the <em>same</em> resolved runtime version the binding's bounded
    /// <c>SwiftBindings.Runtime</c> <c>PackageReference</c> targets — i.e.
    /// <c>--swift-runtime-version</c> when supplied, else the generator's baked default. The
    /// asserted epoch and the package range must agree on the targeted minor: a binding pinned to an
    /// older runtime via <c>--swift-runtime-version</c> restores against that runtime, so it must
    /// also assert <em>that</em> runtime's epoch or the module initializer hard-aborts at load
    /// against the very runtime NuGet resolved. The primary generation path always sets this once per
    /// module (in <c>Program.cs</c>, right after the context is constructed) — to the baked-default
    /// epoch in the common no-override case (byte-identical to before), or to the pinned runtime's
    /// epoch under <c>--swift-runtime-version</c>. It stays null only where nothing assigns it (an
    /// implicit-fallback context and emitter unit tests); the emission site then falls back to the
    /// baked-default epoch.
    /// </summary>
    public int? RuntimeContractEpoch { get; set; }

    /// <summary>
    /// The active <see cref="NamespacePatternResolver"/> for the current generation pass.
    /// Threaded through so emitters that need to qualify cross-module references (e.g. the
    /// sibling-property fallback receivers, which emit <c>global::&lt;ns&gt;.IFoo</c> for
    /// each protocol in a sibling group) can resolve each module's actual C# namespace
    /// rather than assuming the Swift module name. Null in legacy/default contexts; the
    /// receivers fall back to the Swift module name in that case to preserve historical
    /// byte-stable output.
    /// </summary>
    public NamespacePatternResolver? NamespaceResolver { get; set; }

    /// <summary>
    /// When the current module has a public type with the same name as the module itself
    /// (e.g. a module named "Foo" containing a class also named "Foo"), Swift name lookup inside
    /// the wrapper file resolves the bare module name as the type, not the module. Any
    /// "<c>Foo.X</c>" reference emitted into Swift wrapper source therefore means
    /// "the nested type X of class Foo" and fails to compile when X is actually a
    /// module-level type. Set to the module name in that case; null otherwise.
    /// </summary>
    public string? ModuleNameForCollision { get; private set; }

    private HashSet<string>? _nestedTypesInCollidingClass;
    private Regex? _collisionPattern;

    /// <summary>
    /// Names of types nested inside the colliding class (e.g. <c>{"Level"}</c> for class
    /// <c>LoggingLib</c>'s nested <c>Level</c> enum). When stripping the module prefix
    /// from "Module.X..." references, the prefix is preserved if X is in this set: the
    /// qualification is now legitimately required to reach the class-nested type.
    /// </summary>
    public IReadOnlySet<string> NestedTypesInCollidingClass =>
        _nestedTypesInCollidingClass ?? EmptyStringSet;

    /// <summary>
    /// Sets the module/type collision context (called once per module from <c>Program.cs</c>
    /// after collision detection). Both arguments may be null when no collision exists.
    /// </summary>
    public void SetCollisionContext(string? moduleNameForCollision, IReadOnlySet<string>? nestedTypesInCollidingClass)
    {
        ModuleNameForCollision = moduleNameForCollision;
        _nestedTypesInCollidingClass = nestedTypesInCollidingClass != null
            ? new HashSet<string>(nestedTypesInCollidingClass, StringComparer.Ordinal)
            : null;
        // The lookbehind exempts the wrapper's own scoped imports. `import class Foo.Bar` is the
        // one place a "Foo." prefix must survive: it is not a type reference to rewrite but the
        // declaration that makes the *stripped* references resolve, and stripping it to
        // `import class Bar` is a syntax error that fails the whole wrapper.
        _collisionPattern = !string.IsNullOrEmpty(moduleNameForCollision)
            ? new Regex(@"(?<!\bimport (?:class|struct|enum|protocol|typealias) )\b"
                    + Regex.Escape(moduleNameForCollision) + @"\.(\w+(?:\.\w+)*)",
                RegexOptions.Compiled)
            : null;
    }

    /// <summary>
    /// Returns the appropriate string for emitting <paramref name="moduleQualifiedName"/>
    /// into Swift wrapper source. When <see cref="ModuleNameForCollision"/> is set and the
    /// name starts with that module prefix, the prefix is stripped — except when the
    /// next segment is in <see cref="NestedTypesInCollidingClass"/>, in which case the
    /// qualification is preserved (the prefix now reaches a class-nested type).
    /// Outside the collision case, the input is returned unchanged.
    /// </summary>
    public string QualifyForWrapperSource(string moduleQualifiedName) =>
        QualifyForWrapperSource(moduleQualifiedName, journal: null);

    /// <summary>
    /// As <see cref="QualifyForWrapperSource(string)"/>, additionally recording each rewrite into
    /// <paramref name="journal"/>.
    /// </summary>
    /// <remarks>
    /// This pass runs over the whole wrapper buffer on its way to disk, so every offset recorded
    /// while emitting Swift is already stale by the time swiftc reads the file — before the wrapper
    /// pre-strip has even run. It shortens text (it deletes a module prefix), so the shift is real
    /// rather than theoretical, and it is the first of the transforms a Swift position has to be
    /// carried through.
    /// </remarks>
    public string QualifyForWrapperSource(string moduleQualifiedName, TextEditJournal? journal)
    {
        if (string.IsNullOrEmpty(moduleQualifiedName) || _collisionPattern == null)
            return moduleQualifiedName;

        return _collisionPattern.Replace(moduleQualifiedName, match =>
        {
            // First captured group is the type path after the module prefix.
            // Preserve qualification when the head segment names a type nested
            // inside the colliding class (e.g. LoggingLib.Level).
            var firstComponent = match.Groups[1].Value;
            var dotIdx = firstComponent.IndexOf('.');
            var topLevelName = dotIdx >= 0 ? firstComponent.Substring(0, dotIdx) : firstComponent;
            if (_nestedTypesInCollidingClass?.Contains(topLevelName) == true)
                return match.Value;
            journal?.Record(match.Index, match.Length, firstComponent.Length);
            return match.Groups[1].Value;
        });
    }

    /// <summary>
    /// Convenience overload for <see cref="SwiftTypeName"/>. Returns the appropriate
    /// type-reference string for emission into Swift wrapper source.
    /// </summary>
    public string QualifyForWrapperSource(SwiftTypeName swiftTypeName) =>
        QualifyForWrapperSource(swiftTypeName.ModuleQualifiedName);

    // ==================== Underscore-Prefix Suppression ====================

    private static readonly IReadOnlySet<string> EmptyStringSet = new HashSet<string>();
    private HashSet<string>? _underscoreSuppressedNames;

    /// <summary>
    /// Module-qualified names of underscore-prefixed types to suppress from C# output.
    /// Set once at pipeline start; checked during HandleBaseDecl to skip type emission.
    /// </summary>
    public IReadOnlySet<string> UnderscoreSuppressedNames =>
        _underscoreSuppressedNames ?? EmptyStringSet;

    /// <summary>Sets the underscore-suppressed type names (called once per module).</summary>
    public void SetUnderscoreSuppressedNames(HashSet<string> names) => _underscoreSuppressedNames = names;

    /// <summary>Checks if a module-qualified type name is underscore-suppressed.</summary>
    public bool IsUnderscoreSuppressed(string moduleQualifiedName) =>
        _underscoreSuppressedNames?.Contains(moduleQualifiedName) == true;

    // ==================== Interface Emitted Property Names ====================

    private readonly Dictionary<string, IReadOnlySet<string>> _interfaceEmittedPropertyNames =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Records the set of C# property names actually emitted in a given protocol's interface
    /// declaration — the same set <c>ProtocolHandler.EmitProtocolImpl</c> uses internally to
    /// decide whether a method needs the <c>FooMethod</c> rename to avoid colliding with a
    /// property <c>Foo</c>. Keyed by the protocol's module-qualified Swift name.
    ///
    /// Consumers (proxy explicit-interface forwarders, BFS shadow detection in
    /// <c>ShadowsInheritedInterfaceMethod</c>) need this set to compute the exact C# member
    /// name a foreign protocol emitted, since that name depends on which of that protocol's
    /// own properties survived gate evaluation.
    /// </summary>
    public void RecordInterfacePropertyNames(string protoQualifiedName, IReadOnlySet<string> propertyNames)
    {
        if (string.IsNullOrEmpty(protoQualifiedName))
            return;
        _interfaceEmittedPropertyNames[protoQualifiedName] = propertyNames;
    }

    /// <summary>
    /// Returns the property-name set recorded for <paramref name="protoQualifiedName"/>, or
    /// <c>null</c> when the protocol's interface emission hasn't run yet (cross-module case
    /// or pre-emission lookup). Callers fall back to a conservative approximation in that case.
    /// </summary>
    public IReadOnlySet<string>? GetInterfacePropertyNames(string protoQualifiedName)
    {
        if (string.IsNullOrEmpty(protoQualifiedName))
            return null;
        return _interfaceEmittedPropertyNames.TryGetValue(protoQualifiedName, out var set) ? set : null;
    }

    // ==================== Sibling Property Fallback Map ====================

    /// <summary>
    /// A sibling fallback entry for a property in a sibling group. <see cref="Proto"/>
    /// is one of the OTHER protocols that declares the same property name+type with
    /// a setter or get-only accessor; <see cref="HasSetter"/> records that accessor
    /// shape so setter receivers can skip get-only siblings.
    /// </summary>
    public readonly record struct SiblingPropertyFallback(ProtocolDecl Proto, bool HasSetter);

    private readonly Dictionary<(string ProtoQName, string PropertyName), IReadOnlyList<SiblingPropertyFallback>>
        _siblingPropertyFallbacks = new();

    /// <summary>
    /// Records the sibling-protocol fallback list for each (protocol, property) participating
    /// in a sibling-property group. A "sibling group" is two or more class-bound protocols
    /// that declare the same property name+type with differing accessor sets — see
    /// <see cref="EveryProtocolEmitter.ComputePropertyEmissionPlans"/> for the resolution rules.
    ///
    /// <para>Consumed by <c>ProtocolProxyEmitter.EmitPropertyReceivers</c>: when a property is
    /// in a sibling group, each receiver tries its own interface first, then falls back to
    /// the recorded sibling interfaces. This makes per-instance dispatch correct regardless
    /// of which sibling vtable the Swift fan-out picks — without this fallback, the owner
    /// receiver cannot locate a smaller-sibling proxy and silently returns empty (getter) or
    /// no-ops (setter) once any larger-sibling proxy has registered its global vtable.</para>
    /// </summary>
    public void SetSiblingPropertyFallbacks(
        IReadOnlyDictionary<(string ProtoQName, string PropertyName), IReadOnlyList<SiblingPropertyFallback>> map)
    {
        _siblingPropertyFallbacks.Clear();
        foreach (var kvp in map)
            _siblingPropertyFallbacks[kvp.Key] = kvp.Value;
    }

    /// <summary>
    /// Returns the sibling-protocol fallback list for <paramref name="protoQualifiedName"/>'s
    /// property <paramref name="propertyName"/>, or <c>null</c> when the property is not in
    /// a sibling group.
    /// </summary>
    public IReadOnlyList<SiblingPropertyFallback>? GetSiblingPropertyFallbacks(
        string protoQualifiedName, string propertyName)
    {
        if (string.IsNullOrEmpty(protoQualifiedName) || string.IsNullOrEmpty(propertyName))
            return null;
        return _siblingPropertyFallbacks.TryGetValue((protoQualifiedName, propertyName), out var list)
            ? list : null;
    }

    // ==================== Sibling Subscript Fallback Map ====================

    /// <summary>
    /// A sibling fallback entry for a subscript in a sibling group. <see cref="Proto"/> is
    /// one of the OTHER protocols that declares the same subscript signature with a setter
    /// or get-only accessor; <see cref="Index"/> is the subscript's position within that
    /// protocol's own non-static subscript list (the index that names its vtable fields
    /// <c>func_subscript_{Index}_get/set</c>); <see cref="HasSetter"/> records the
    /// accessor shape so setter receivers can skip get-only siblings.
    /// </summary>
    public readonly record struct SiblingSubscriptFallback(ProtocolDecl Proto, int Index, bool HasSetter);

    private readonly Dictionary<(string ProtoQName, string SubscriptKey), IReadOnlyList<SiblingSubscriptFallback>>
        _siblingSubscriptFallbacks = new();

    /// <summary>
    /// Records the sibling-protocol fallback list for each (protocol, subscript) participating
    /// in a sibling-subscript group. A "sibling group" is two or more class-bound protocols that
    /// declare the same subscript signature (index parameter types + return type) with differing
    /// accessor sets — see <see cref="EveryProtocolEmitter.ComputeSubscriptEmissionPlans"/>
    /// for the resolution rules.
    ///
    /// <para>Consumed by <c>ProtocolProxyEmitter.EmitSubscriptReceivers</c>: when a subscript is
    /// in a sibling group, each receiver tries its own interface first, then falls back to the
    /// recorded sibling interfaces. Without this fallback the owner receiver cannot locate a
    /// smaller-sibling proxy and silently returns empty/no-ops once any larger-sibling proxy has
    /// registered its global vtable.</para>
    ///
    /// <para>Key shape: <c>(ProtoQName, SubscriptKey)</c> where SubscriptKey is the per-protocol
    /// <c>subscript_{Index}({paramTypes})</c> string produced by <c>GetSubscriptKey</c>. Lookup
    /// in the receiver uses the same key shape so the index is consistent with the vtable field
    /// names emitted for the owning protocol.</para>
    /// </summary>
    public void SetSiblingSubscriptFallbacks(
        IReadOnlyDictionary<(string ProtoQName, string SubscriptKey), IReadOnlyList<SiblingSubscriptFallback>> map)
    {
        _siblingSubscriptFallbacks.Clear();
        foreach (var kvp in map)
            _siblingSubscriptFallbacks[kvp.Key] = kvp.Value;
    }

    /// <summary>
    /// Returns the sibling-protocol fallback list for <paramref name="protoQualifiedName"/>'s
    /// subscript identified by <paramref name="subscriptKey"/>, or <c>null</c> when the
    /// subscript is not in a sibling group.
    /// </summary>
    public IReadOnlyList<SiblingSubscriptFallback>? GetSiblingSubscriptFallbacks(
        string protoQualifiedName, string subscriptKey)
    {
        if (string.IsNullOrEmpty(protoQualifiedName) || string.IsNullOrEmpty(subscriptKey))
            return null;
        return _siblingSubscriptFallbacks.TryGetValue((protoQualifiedName, subscriptKey), out var list)
            ? list : null;
    }

    // ==================== Sibling Method Fallback Map ====================

    /// <summary>
    /// A sibling fallback entry for a method in a same-signature group. <see cref="Proto"/> is one
    /// of the OTHER protocols that declares the same Swift method signature (name + argument labels
    /// + parameter types + return type). Methods have no accessor sets, so — unlike
    /// <see cref="SiblingPropertyFallback"/> / <see cref="SiblingSubscriptFallback"/> — only the
    /// protocol is recorded.
    /// </summary>
    public readonly record struct SiblingMethodFallback(ProtocolDecl Proto);

    private readonly Dictionary<(string ProtoQName, string MethodKey), IReadOnlyList<SiblingMethodFallback>>
        _siblingMethodFallbacks = new();

    /// <summary>
    /// Records the sibling-protocol fallback list for each (protocol, method) participating in a
    /// same-signature method group. A "sibling group" is two or more protocols that declare the same
    /// Swift method signature — see <see cref="EveryProtocolEmitter.ComputeMethodEmissionPlans"/>
    /// for the resolution rules.
    ///
    /// <para>Consumed by <c>ProtocolProxyEmitter.EmitMethodReceiver</c>: when a method is in a
    /// sibling group, the receiver tries its own interface first and then falls back to the recorded
    /// sibling interfaces. The Swift owner-body fan-out can pick any populated sibling vtable, so
    /// whichever receiver runs must locate the proxy via per-instance SwiftObjectRegistry lookups
    /// across all sibling interfaces. Without this fallback the chosen vtable's proxy class cannot
    /// see a handle registered as a different sibling's proxy and returns the dead-impl null value.</para>
    ///
    /// <para>Key shape: <c>(ProtoQName, MethodKey)</c> where MethodKey is the projection-free
    /// <c>GetMethodSiblingMapKey</c> string. The receiver reproduces the same key from the same
    /// <c>MethodDecl</c>, so the lookup is consistent without recomputing the projected signature.</para>
    /// </summary>
    public void SetSiblingMethodFallbacks(
        IReadOnlyDictionary<(string ProtoQName, string MethodKey), IReadOnlyList<SiblingMethodFallback>> map)
    {
        _siblingMethodFallbacks.Clear();
        foreach (var kvp in map)
            _siblingMethodFallbacks[kvp.Key] = kvp.Value;
    }

    /// <summary>
    /// Returns the sibling-protocol fallback list for <paramref name="protoQualifiedName"/>'s method
    /// identified by <paramref name="methodKey"/>, or <c>null</c> when the method is not in a
    /// sibling group.
    /// </summary>
    public IReadOnlyList<SiblingMethodFallback>? GetSiblingMethodFallbacks(
        string protoQualifiedName, string methodKey)
    {
        if (string.IsNullOrEmpty(protoQualifiedName) || string.IsNullOrEmpty(methodKey))
            return null;
        return _siblingMethodFallbacks.TryGetValue((protoQualifiedName, methodKey), out var list)
            ? list : null;
    }

    // ==================== Protocol Extension Defaults Index ====================

    /// <summary>
    /// Index of unconstrained protocol extension default implementations.
    /// Used by ProtocolConformanceValidator to allow conformance when types rely on defaults.
    /// </summary>
    public ProtocolExtensionDefaultsIndex? ExtensionDefaultsIndex { get; set; }

    // ==================== Protocol Extension ====================

    private readonly List<string> _protocolExtWrapperLines = new();
    private readonly HashSet<string> _protocolExtEmittedSymbols = new();

    /// <summary>Count of injected protocol extension methods for logging.</summary>
    public int ProtocolExtInjectedCount { get; set; }

    /// <summary>Accumulated Swift wrapper source lines for protocol extensions.</summary>
    public IReadOnlyList<string> ProtocolExtSwiftWrapperLines => _protocolExtWrapperLines;

    /// <summary>Checks if a protocol extension Swift wrapper symbol was already emitted.</summary>
    public bool HasEmittedProtocolExtSymbol(string symbol) => _protocolExtEmittedSymbols.Contains(symbol);

    /// <summary>Adds a protocol extension symbol. Returns true if newly added.</summary>
    public bool TryAddProtocolExtSymbol(string symbol, DeclId? owner = null) =>
        RegisterWrapperSymbolInternal(_protocolExtEmittedSymbols, symbol, owner);

    /// <summary>Adds a single Swift wrapper line for protocol extensions.</summary>
    public void AddProtocolExtWrapperLine(string line) => _protocolExtWrapperLines.Add(line);

    /// <summary>Adds multiple Swift wrapper lines for protocol extensions.</summary>
    public void AddProtocolExtWrapperLines(IEnumerable<string> lines) => _protocolExtWrapperLines.AddRange(lines);

    // ==================== KeyPath Singleton Containers ====================
    //
    // Module-level dedup for typed KeyPath singleton containers. Two
    // PAT-constrained generic parents may demand the same (conformer, bag) pair
    // (e.g., two different consumer methods that both take
    // KeyPath<Item.LibraryFilter, *>). Each parent's emission pass walks demand
    // independently, so without module-level dedup we would re-emit the same
    // top-level C# container class and the same `SBW_KP_…` Swift @_cdecl symbols,
    // tripping CS0102 (duplicate member) on the C# side or "duplicate symbol" at
    // link time on the Swift side. Key shape: `{conformer-qualified}|{bag-qualified}`.

    private readonly HashSet<string> _emittedKeyPathSingletonContainers = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers a (conformer, bag) KeyPath singleton container for this module.
    /// Returns true if newly added; false if another generic parent already emitted it.
    /// </summary>
    public bool TryAddKeyPathSingletonContainer(string key) =>
        _emittedKeyPathSingletonContainers.Add(key);

    // Per-(conformer × dependency-class × init-shape × value-type) dedup
    // for consumer-side KeyPath-init factory overloads. The factory recognizer walks
    // every dependency generic class's KeyPath-init shapes against every local conformer
    // of the init's generic constraint; two recognized shapes (e.g. getter/getSetter) host
    // overloads in the same `{Conformer}{DepClass}Factory` partial class, and the same shape
    // can surface twice if a dependency module is parsed more than once. Without this guard
    // we would re-emit a duplicate C# overload (CS0111) or a duplicate `SBW_EPF_…` Swift
    // @_cdecl symbol (duplicate-symbol link error). Key shape:
    // `{conformer-qualified}|{dep-class-qualified}|{keypath-arg-label}|{V-CSharp-Type}`.
    private readonly HashSet<string> _emittedKeyPathInitFactories = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers a (conformer, dependency class, init-shape label, value-type) KeyPath-init
    /// factory overload for this module. Returns true if newly added; false if a previous
    /// emission pass already registered it.
    /// </summary>
    public bool TryAddKeyPathInitFactory(string key) =>
        _emittedKeyPathInitFactories.Add(key);

    // Per-(parent × conformer × method × V) sort overload dedup.
    // Two generic parents in the same module that emit the same (conformer × V) sort
    // shape would otherwise collide on both the C# partial-class member set and the
    // Swift @_cdecl symbol. Key shape:
    // `{parent-qualified}|{conformer-qualified}|{method-name}|{V-CSharp-Type}`.
    private readonly HashSet<string> _emittedRouteCSortOverloads = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers a (parent, conformer, method, V) sort overload for this module.
    /// Returns true if newly added; false if a previous emission pass already registered it.
    /// </summary>
    public bool TryAddKeyPathBagValueSpecialization(string key) =>
        _emittedRouteCSortOverloads.Add(key);

    // ==================== Foreign Type Extension ====================

    private readonly List<string> _foreignExtWrapperLines = new();
    private readonly HashSet<string> _foreignExtEmittedSymbols = new();
    private readonly Dictionary<string, ForeignExtensionClassInfo> _foreignExtClasses = new();
    private readonly HashSet<string> _foreignExtNeededImports = new();

    /// <summary>Count of emitted foreign extension members for logging.</summary>
    public int ForeignExtEmittedCount { get; set; }

    /// <summary>Accumulated Swift wrapper source lines for foreign type extensions.</summary>
    public IReadOnlyList<string> ForeignExtSwiftWrapperLines => _foreignExtWrapperLines;

    /// <summary>Foreign modules that need to be imported in the Swift wrapper file.</summary>
    public IReadOnlyCollection<string> ForeignExtNeededImports => _foreignExtNeededImports;

    /// <summary>Collected extension class info grouped by foreign type qualified name.</summary>
    public IReadOnlyDictionary<string, ForeignExtensionClassInfo> ForeignExtClasses => _foreignExtClasses;

    /// <summary>Checks if a foreign extension Swift wrapper symbol was already emitted.</summary>
    public bool HasEmittedForeignExtSymbol(string symbol) => _foreignExtEmittedSymbols.Contains(symbol);

    /// <summary>Adds a foreign extension symbol. Returns true if newly added.</summary>
    public bool TryAddForeignExtSymbol(string symbol, DeclId? owner = null) =>
        RegisterWrapperSymbolInternal(_foreignExtEmittedSymbols, symbol, owner);

    /// <summary>Adds a single Swift wrapper line for foreign type extensions.</summary>
    public void AddForeignExtWrapperLine(string line) => _foreignExtWrapperLines.Add(line);

    /// <summary>Adds multiple Swift wrapper lines for foreign type extensions.</summary>
    public void AddForeignExtWrapperLines(IEnumerable<string> lines) => _foreignExtWrapperLines.AddRange(lines);

    /// <summary>Adds a foreign module import for the Swift wrapper file.</summary>
    public void AddForeignExtNeededImport(string import) => _foreignExtNeededImports.Add(import);

    /// <summary>Gets or creates extension class info for a foreign type.</summary>
    public ForeignExtensionClassInfo GetOrAddForeignExtClass(string key, Func<ForeignExtensionClassInfo> factory)
    {
        if (!_foreignExtClasses.TryGetValue(key, out var info))
        {
            info = factory();
            _foreignExtClasses[key] = info;
        }
        return info;
    }

    // ==================== Shared-Swift-helper commit epoch ====================

    private int _sharedSwiftArtifactEpoch;

    /// <summary>
    /// Counts commits of module-shared Swift helpers — text written once per module into the
    /// wrapper source and recorded here so no later member re-emits it (the
    /// <c>SBW_Utf8Slice</c> struct, <c>SBW_Free</c>, the closure-context box helpers, the
    /// async-closure bridge preamble and boxes, typed-error extractors, the error-mint helper,
    /// the nested-closure inner-box release helpers, and the per-type singletons: metadata
    /// wrappers, metadata-accessor helpers, equality wrappers, Optional-tag helpers and enum
    /// RawRepresentable wrappers).
    /// <para>Read it before speculatively emitting a member and compare after: an unchanged
    /// epoch proves every Swift byte written in between belongs to that member alone, so
    /// truncating the Swift buffer back is safe. A changed epoch means shared helper text sits
    /// inside the same span while this registry already reports it emitted — and there is no
    /// un-register — so truncating would delete a definition nothing will ever write again and
    /// leave later members referring to it. Callers that cannot prove the span is member-private
    /// must keep the Swift text.</para>
    /// <para>The test for a new registry is not "does it write Swift" but: <b>does its return
    /// value gate someone else's re-emission of text that lands in the Swift writer?</b> Bump only
    /// when all three hold — (a) the text goes into the <c>SwiftWriter</c> buffer a rollback can
    /// truncate, (b) the registry is consulted as a dedup gate (<c>if (!TryAdd…) return;</c>), and
    /// (c) the definition outlives the member that happened to write it. Three shapes deliberately
    /// do NOT bump, and bumping them would be a bug, not extra safety:</para>
    /// <list type="bullet">
    /// <item><description><b>Per-member wrapper symbols</b> (method/property/constructor/@objc
    /// property). Their text IS the member's own body and rolls back with it. Bumping would make
    /// every member suppress its own rollback, disabling the recovery path entirely.</description></item>
    /// <item><description><b>Bare registrations</b> whose result every caller discards — the
    /// direct-helper bucket. Gating nothing, they can't cause a skipped re-emit.</description></item>
    /// <item><description><b>Side-list accumulators</b> (protocol-extension and foreign-extension
    /// wrapper lines). That text never enters the writer buffer, so truncation cannot reach
    /// it.</description></item>
    /// </list>
    /// <para>A new shared Swift helper registry that meets (a)+(b)+(c) must bump, or the rollback
    /// path silently regains the hazard — <c>ModuleEmissionContextEpochTests</c> pins the current
    /// set on both sides, bumping and non-bumping.</para>
    /// </summary>
    public int SharedSwiftArtifactEpoch => _sharedSwiftArtifactEpoch;

    /// <summary>Records that module-shared Swift helper text was committed.</summary>
    private bool MarkSharedSwiftArtifact(bool committed)
    {
        if (committed)
        {
            _sharedSwiftArtifactEpoch++;
        }
        return committed;
    }

    // ==================== Utf8Slice ====================

    private readonly HashSet<string> _utf8SliceFreePInvokeTypes = new();
    private bool _utf8SliceStructEmitted;
    private bool _utf8SliceFreeEmitted;

    /// <summary>Whether the SBW_Utf8Slice struct has been emitted for this module.</summary>
    public bool Utf8SliceStructEmitted
    {
        get => _utf8SliceStructEmitted;
        set
        {
            MarkSharedSwiftArtifact(value && !_utf8SliceStructEmitted);
            _utf8SliceStructEmitted = value;
        }
    }

    /// <summary>Whether the SBW_Utf8Slice_Free function has been emitted for this module.</summary>
    public bool Utf8SliceFreeEmitted
    {
        get => _utf8SliceFreeEmitted;
        set
        {
            MarkSharedSwiftArtifact(value && !_utf8SliceFreeEmitted);
            _utf8SliceFreeEmitted = value;
        }
    }

    /// <summary>The current module name for Utf8Slice symbol generation.</summary>
    public string? Utf8SliceCurrentModuleName { get; set; }

    /// <summary>Checks if a Utf8Slice free P/Invoke has been emitted for a type.</summary>
    public bool HasUtf8SliceFreePInvoke(string typeKey) => _utf8SliceFreePInvokeTypes.Contains(typeKey);

    /// <summary>Marks a Utf8Slice free P/Invoke as emitted. Returns true if newly added.</summary>
    public bool TryAddUtf8SliceFreePInvoke(string typeKey) => _utf8SliceFreePInvokeTypes.Add(typeKey);

    // ==================== Nested closure bridge ====================

    // The release symbol is namespaced by the PARENT decl's module (a cross-module extension
    // names a foreign module), so one emission context can need several distinct helpers —
    // the gate must be per symbol, not a single per-context bool.
    private readonly HashSet<string> _ncbInnerBoxReleaseSymbolsEmitted = new(StringComparer.Ordinal);

    /// <summary>Marks the escaping inner-closure box release helper for a symbol as emitted. Returns true if newly added.</summary>
    public bool TryAddNcbInnerBoxReleaseSymbol(string symbol) =>
        MarkSharedSwiftArtifact(_ncbInnerBoxReleaseSymbolsEmitted.Add(symbol));

    // ==================== SwiftUI Bridge Collection ====================
    //
    // Accumulates SwiftUI View types skipped during HandleBaseDecl so SwiftUIBridgeEmitter
    // can consume them after the main module body is emitted. Previously process-global static
    // state on SwiftUIBridgeCollector that depended on a Reset() before/after every module
    // emission; scoping it to the per-module context removes the cross-module bleed risk.
    // The lock is retained so a future parallel per-type emission walk stays safe.

    private readonly object _swiftUIViewsSync = new();
    private readonly List<TypeDecl> _collectedSwiftUIViews = new();
    private readonly HashSet<string> _collectedSwiftUIViewNames = new();

    /// <summary>Records a SwiftUI View type for bridge generation (deduped by name).</summary>
    public void CollectSwiftUIView(TypeDecl viewType)
    {
        ArgumentNullException.ThrowIfNull(viewType);
        lock (_swiftUIViewsSync)
        {
            if (_collectedSwiftUIViewNames.Add(viewType.Name))
                _collectedSwiftUIViews.Add(viewType);
        }
    }

    /// <summary>Returns all SwiftUI View types collected for this module.</summary>
    public IReadOnlyList<TypeDecl> GetCollectedSwiftUIViews()
    {
        lock (_swiftUIViewsSync)
        {
            return _collectedSwiftUIViews.ToList();
        }
    }

    // ==================== Cancellation Task ====================

    private readonly HashSet<string> _cancellationPInvokeTypes = new();

    /// <summary>Whether the cancellation infrastructure has been emitted for this module.</summary>
    public bool CancellationInfrastructureEmitted
    {
        get => _cancellationInfrastructureEmitted;
        set
        {
            MarkSharedSwiftArtifact(value && !_cancellationInfrastructureEmitted);
            _cancellationInfrastructureEmitted = value;
        }
    }

    private bool _cancellationInfrastructureEmitted;

    /// <summary>The current module name for cancellation symbol generation.</summary>
    public string? CancellationCurrentModuleName { get; set; }

    /// <summary>Checks if a cancellation P/Invoke has been emitted for a type.</summary>
    public bool HasCancellationPInvoke(string typeKey) => _cancellationPInvokeTypes.Contains(typeKey);

    /// <summary>Marks a cancellation P/Invoke as emitted. Returns true if newly added.</summary>
    public bool TryAddCancellationPInvoke(string typeKey) => _cancellationPInvokeTypes.Add(typeKey);

    // ==================== Error Description ====================

    private readonly HashSet<string> _errorDescPInvokeTypes = new();
    private readonly HashSet<string> _errorDescExtractorsEmitted = new();
    private readonly HashSet<string> _errorDescExtractorPInvokeTypes = new();

    /// <summary>Whether the error description infrastructure has been emitted for this module.</summary>
    public bool ErrorDescInfrastructureEmitted
    {
        get => _errorDescInfrastructureEmitted;
        set
        {
            MarkSharedSwiftArtifact(value && !_errorDescInfrastructureEmitted);
            _errorDescInfrastructureEmitted = value;
        }
    }

    private bool _errorDescInfrastructureEmitted;

    /// <summary>The current module name for error description symbol generation.</summary>
    public string? ErrorDescCurrentModuleName { get; set; }

    /// <summary>Checks if an error description P/Invoke has been emitted for a type.</summary>
    public bool HasErrorDescPInvoke(string typeKey) => _errorDescPInvokeTypes.Contains(typeKey);

    /// <summary>Marks an error description P/Invoke as emitted. Returns true if newly added.</summary>
    public bool TryAddErrorDescPInvoke(string typeKey) => _errorDescPInvokeTypes.Add(typeKey);

    /// <summary>Marks a typed error extractor as emitted. Returns true if newly added.</summary>
    public bool TryAddTypedErrorExtractor(string swiftErrorType) =>
        MarkSharedSwiftArtifact(_errorDescExtractorsEmitted.Add(swiftErrorType));

    /// <summary>Checks if an extractor P/Invoke has been emitted for a key.</summary>
    public bool HasExtractorPInvoke(string key) => _errorDescExtractorPInvokeTypes.Contains(key);

    /// <summary>Marks an extractor P/Invoke as emitted. Returns true if newly added.</summary>
    public bool TryAddExtractorPInvoke(string key) => _errorDescExtractorPInvokeTypes.Add(key);

    // ==================== Error Type Registry (plain-throws bridge) ====================

    private readonly Dictionary<string, int> _errorTypeIds = new(StringComparer.Ordinal);
    private readonly List<string> _errorTypeOrder = new();
    private readonly Dictionary<string, IReadOnlyList<AvailabilityAnnotation>?> _errorTypeAvailability = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether the per-module error-type registry has been computed by
    /// <see cref="ErrorEnumRegistryEmitter"/>'s precompute pass. Idempotent guard.
    /// </summary>
    public bool ErrorTypeRegistryComputed { get; set; }

    /// <summary>
    /// Per-module registry mapping Swift error type's module-qualified name → assigned id (>= 1).
    /// Built deterministically by <see cref="ErrorEnumRegistryEmitter"/> at module-emission start
    /// (alphabetical ordering for cross-run stability). id 0 is reserved for "untyped" (the
    /// existing string-only fallback path); any non-zero value indexes into this registry so
    /// the C# async-callback dispatcher can reconstruct a typed <c>SwiftException&lt;TError&gt;</c>.
    /// </summary>
    public IReadOnlyDictionary<string, int> ErrorTypeIds => _errorTypeIds;

    /// <summary>
    /// Insertion-ordered Swift module-qualified type names. Consumers (Swift cascade emitter,
    /// C# dictionary emitter) iterate this rather than the dictionary so emit order matches
    /// the registered id order.
    /// </summary>
    public IReadOnlyList<string> ErrorTypeOrder => _errorTypeOrder;

    /// <summary>
    /// Registers a Swift error type and returns its assigned id. Idempotent: re-registration
    /// returns the existing id without renumbering. The first registration assigns id 1.
    /// </summary>
    /// <param name="availabilityAnnotations">
    /// The error type's <c>@available</c> annotations from the swiftinterface,
    /// retained for the cascade dispatcher (which references the type name unconditionally
    /// and needs a matching availability gate to compile against SDKs where the type is
    /// gated on a newer OS version, e.g. <c>WeatherKit.WeatherError</c> on iOS 16+).
    /// </param>
    public int RegisterErrorTypeId(string swiftModuleQualifiedName, IReadOnlyList<AvailabilityAnnotation>? availabilityAnnotations = null)
    {
        if (_errorTypeIds.TryGetValue(swiftModuleQualifiedName, out var existing))
            return existing;
        var newId = _errorTypeIds.Count + 1;
        _errorTypeIds[swiftModuleQualifiedName] = newId;
        _errorTypeOrder.Add(swiftModuleQualifiedName);
        _errorTypeAvailability[swiftModuleQualifiedName] = availabilityAnnotations;
        return newId;
    }

    /// <summary>Tries to look up the registered id for a Swift error type.</summary>
    public bool TryGetErrorTypeId(string swiftModuleQualifiedName, out int id) =>
        _errorTypeIds.TryGetValue(swiftModuleQualifiedName, out id);

    /// <summary>
    /// Per-error-type availability annotations as recorded at registration time. Returns
    /// null when no annotations were captured (e.g., legacy callers, or types declared
    /// without explicit <c>@available</c>).
    /// </summary>
    public IReadOnlyList<AvailabilityAnnotation>? GetErrorTypeAvailability(string swiftModuleQualifiedName) =>
        _errorTypeAvailability.TryGetValue(swiftModuleQualifiedName, out var annotations) ? annotations : null;

    /// <summary>
    /// Whether the Swift-side cascade dispatcher
    /// (<see cref="ErrorRegistryHelperEmitter"/>) has been emitted for this module.
    /// </summary>
    public bool ErrorRegistryHelperEmittedSwift
    {
        get => _errorRegistryHelperEmittedSwift;
        set
        {
            MarkSharedSwiftArtifact(value && !_errorRegistryHelperEmittedSwift);
            _errorRegistryHelperEmittedSwift = value;
        }
    }

    private bool _errorRegistryHelperEmittedSwift;

    /// <summary>
    /// Whether the C#-side typed-exception dispatcher class
    /// (<see cref="ErrorRegistryHelperEmitter"/>) has been emitted for this module.
    /// </summary>
    public bool ErrorRegistryHelperEmittedCSharp { get; set; }

    // ==================== Swift Error Mint Helper ====================
    // Shared by the generic-closure bridge and the standard throwing-closure callback
    // path: both emit the same per-module SBW_CreateError_{module} symbol (see
    // SwiftErrorMintEmitter). Dedup state lives here so the Swift helper is emitted
    // once per module and its C# P/Invoke once per type-key, regardless of which path
    // triggers it first.

    private readonly HashSet<string> _swiftErrorMintPInvokeTypes = new();

    private bool _swiftErrorMintHelperEmitted;

    /// <summary>Whether the SBW_CreateError Swift helper has been emitted for this module.</summary>
    public bool SwiftErrorMintHelperEmitted
    {
        get => _swiftErrorMintHelperEmitted;
        set
        {
            MarkSharedSwiftArtifact(value && !_swiftErrorMintHelperEmitted);
            _swiftErrorMintHelperEmitted = value;
        }
    }

    /// <summary>Checks if the SBW_CreateError C# P/Invoke has been emitted for a type.</summary>
    public bool HasSwiftErrorMintPInvoke(string typeKey) => _swiftErrorMintPInvokeTypes.Contains(typeKey);

    /// <summary>Marks the SBW_CreateError C# P/Invoke as emitted. Returns true if newly added.</summary>
    public bool TryAddSwiftErrorMintPInvoke(string typeKey) => _swiftErrorMintPInvokeTypes.Add(typeKey);

    // ==================== Async Closure Swift Wrapper ====================

    private readonly HashSet<string> _asyncClosureSwiftWrapperKeys = new();

    private bool _asyncClosureBridgeErrorEmitted;

    /// <summary>Whether the SwiftBindingsBridgeError error type stub has been emitted for the current Swift module.</summary>
    public bool AsyncClosureBridgeErrorEmitted
    {
        get => _asyncClosureBridgeErrorEmitted;
        set
        {
            MarkSharedSwiftArtifact(value && !_asyncClosureBridgeErrorEmitted);
            _asyncClosureBridgeErrorEmitted = value;
        }
    }

    /// <summary>Marks an async-closure box + resume-callback trio as emitted for a (module, T) pair. Returns true if newly added.</summary>
    public bool TryAddAsyncClosureSwiftWrapperKey(string key) =>
        MarkSharedSwiftArtifact(_asyncClosureSwiftWrapperKeys.Add(key));

    // ==================== NativeAOT Factory Registration ====================

    private readonly List<string> _emittedSwiftObjectTypes = new();

    private readonly List<(string TypeofExpr, Swift.Runtime.PayloadConstructionSemantics Semantics)> _payloadSemantics = new();

    private readonly Stack<string> _typeNestingStack = new();

    /// <summary>
    /// C# type names of non-generic ISwiftObject types emitted during this module.
    /// Nested types are fully qualified (e.g., "Codec.Encoding").
    /// Used by ModuleHandler to emit [ModuleInitializer] factory registration for NativeAOT.
    /// </summary>
    public IReadOnlyList<string> EmittedSwiftObjectTypes => _emittedSwiftObjectTypes;

    private readonly Dictionary<string, IReadOnlyList<AvailabilityAnnotation>> _typeEffectiveAvailability =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Returns the effective OS-availability floor (own annotations merged with every enclosing
    /// type's) recorded for an emitted type — keyed by the qualified name as it appears in
    /// <see cref="EmittedSwiftObjectTypes"/> / <see cref="EmittedConformances"/> — or <c>null</c>
    /// when the type carries no floor. Consumed by <see cref="ModuleHandler"/> to wrap the module
    /// initializer's eager generic registration / metadata warmup in a positive availability guard,
    /// so launching on an OS below a gated type's floor cannot trigger an uncatchable native
    /// Mono generic-instantiation abort before any managed frame's <c>try/catch</c> is active.
    /// </summary>
    public IReadOnlyList<AvailabilityAnnotation>? GetTypeEffectiveAvailability(string qualifiedTypeName) =>
        _typeEffectiveAvailability.TryGetValue(qualifiedTypeName, out var annotations) ? annotations : null;

    private void RecordTypeAvailability(string qualifiedName, IReadOnlyList<AvailabilityAnnotation>? availability)
    {
        if (availability is { Count: > 0 })
            _typeEffectiveAvailability[qualifiedName] = availability;
    }

    /// <summary>
    /// Each emitted ISwiftObject type's declared <see cref="Swift.Runtime.PayloadConstructionSemantics"/>
    /// paired with the <c>typeof(...)</c> argument that names it in the module initializer. Non-generic
    /// types use their qualified name (e.g. <c>"Codec.Encoding"</c>); generic type definitions use the
    /// <b>open</b> form (e.g. <c>"Pair&lt;,&gt;"</c>) so a single registration covers every closed
    /// instantiation via the dispatcher's open-generic fallback — the value witness for every
    /// instantiation of a generic type is the same shape (Copy/Adopt/Inline) by construction.
    /// Consumed by <see cref="ModuleHandler"/> to emit
    /// <c>SwiftMarshal.RegisterPayloadSemantics(typeof(T), ...)</c> calls.
    /// </summary>
    public IReadOnlyList<(string TypeofExpr, Swift.Runtime.PayloadConstructionSemantics Semantics)> PayloadSemantics =>
        _payloadSemantics;

    /// <summary>Pushes a parent type name onto the nesting stack.</summary>
    public void PushTypeNesting(string parentTypeName) => _typeNestingStack.Push(parentTypeName);

    /// <summary>Pops a parent type name from the nesting stack.</summary>
    public void PopTypeNesting() => _typeNestingStack.Pop();

    /// <summary>
    /// Returns true when any ancestor on the nesting stack is an open generic
    /// (e.g. <c>VerificationOutcome&lt;TSignedType&gt;</c>). Module-initializer
    /// registrations for nested types in that position would reference an outer
    /// type parameter that isn't in scope in the static init context — they must
    /// fall back to lazy resolution at first use.
    /// </summary>
    private bool HasOpenGenericAncestor()
    {
        foreach (var ancestor in _typeNestingStack)
        {
            if (ancestor.Contains('<'))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Records a non-generic ISwiftObject type for NativeAOT factory registration.
    /// Uses the nesting stack to build qualified names for nested types.
    /// Skipped for nested types inside an open generic outer — see
    /// <see cref="HasOpenGenericAncestor"/>.
    /// </summary>
    public void RecordSwiftObjectType(string csharpTypeName,
        IReadOnlyList<AvailabilityAnnotation>? effectiveAvailability = null)
    {
        if (HasOpenGenericAncestor())
            return;
        var qualifiedName = GetQualifiedTypeName(csharpTypeName);
        _emittedSwiftObjectTypes.Add(qualifiedName);
        RecordTypeAvailability(qualifiedName, effectiveAvailability);
    }

    /// <summary>
    /// Records a non-generic ISwiftObject type's declared payload-construction semantics so the module
    /// initializer can call <c>SwiftMarshal.RegisterPayloadSemantics(typeof(T), ...)</c>. Mirrors the
    /// nesting/open-generic-ancestor guards of <see cref="RecordSwiftObjectType"/>: a type nested inside
    /// an open generic outer can't be named in the static-init context, so it falls back to the runtime
    /// reflection backstop instead.
    /// </summary>
    public void RecordPayloadSemantics(string csharpTypeName, Swift.Runtime.PayloadConstructionSemantics semantics)
    {
        if (HasOpenGenericAncestor())
            return;
        var qualifiedName = GetQualifiedTypeName(csharpTypeName);
        _payloadSemantics.Add((qualifiedName, semantics));
    }

    /// <summary>
    /// Records an open-generic ISwiftObject type definition's payload-construction semantics using the
    /// <b>open</b> <c>typeof</c> form (e.g. <c>Pair&lt;,&gt;</c>). One registration covers every closed
    /// instantiation via the dispatcher's open-generic fallback. The handler passes the simple C# name
    /// (no generic suffix) and the arity; nesting is resolved here.
    /// </summary>
    public void RecordOpenGenericPayloadSemantics(string simpleTypeName, int arity, Swift.Runtime.PayloadConstructionSemantics semantics)
    {
        if (string.IsNullOrEmpty(simpleTypeName) || arity <= 0)
            return;
        if (HasOpenGenericAncestor())
            return;
        var qualifiedName = GetQualifiedTypeName(simpleTypeName);
        var openForm = $"{qualifiedName}<{new string(',', arity - 1)}>";
        _payloadSemantics.Add((openForm, semantics));
    }

    // ==================== Open Generic ISwiftObject Trimmer Roots ====================

    private readonly SortedDictionary<string, int> _openGenericISwiftObjectArities =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Open-generic ISwiftObject type definitions emitted in this module, keyed by the
    /// nesting-qualified simple C# name (e.g., <c>"BlittableElementBuffer"</c> or
    /// <c>"Outer.Inner"</c> for a nested open generic) mapped to its arity (1, 2, …).
    /// Consumed by <see cref="TrimmerDescriptorEmitter"/> to write an
    /// <c>ILLink.Descriptors.xml</c> sibling rooted in the consumer's csproj via
    /// <c>TrimmerRootDescriptor</c> — the eager-cctor pattern emitted by the three
    /// handlers gives ILC a call edge for the closed instantiation it can see, but it
    /// does NOT preserve reflection metadata for the open generic type definition. The
    /// descriptor closes that gap for NativeAOT trimming. Descriptor
    /// emission prepends <see cref="ResolvedNamespace"/> to form the fullname attribute.
    /// </summary>
    public IReadOnlyDictionary<string, int> EmittedOpenGenericISwiftObjectTypes =>
        _openGenericISwiftObjectArities;

    /// <summary>
    /// Records an open-generic ISwiftObject type definition (e.g.,
    /// <c>BlittableElementBuffer</c> arity 1) for trimmer-root preservation. The handler
    /// passes the simple C# type name (no generic suffix) and the arity from
    /// <c>GenericParameters.Count</c>; the namespace and any outer-type nesting are
    /// resolved here from the current emission state. Idempotent: re-registration of
    /// the same key replaces the arity (which should match by construction).
    /// </summary>
    public void RecordOpenGenericISwiftObjectType(string simpleTypeName, int arity)
    {
        if (string.IsNullOrEmpty(simpleTypeName) || arity <= 0)
            return;
        // Open generics nested inside another open generic are unrooted in the static-init
        // context (the outer T parameter is not in scope); the eager-cctor path skips them
        // for the same reason, and the descriptor here would name a type C# can't reach
        // without instantiating the outer first.
        if (HasOpenGenericAncestor())
            return;
        var qualifiedName = GetQualifiedTypeName(simpleTypeName);
        _openGenericISwiftObjectArities[qualifiedName] = arity;
    }

    /// <summary>
    /// Records a closed generic ISwiftObject type (e.g., Pair&lt;CoordinateRef, LabelRef&gt;)
    /// for NativeAOT factory registration. Unlike open generics, closed generics can be
    /// instantiated in a module initializer. Called when emitting MarshalFromSwift&lt;T&gt;
    /// for bound generic return types.
    /// </summary>
    public void RecordBoundGenericSwiftObjectType(string closedGenericTypeName)
    {
        if (!closedGenericTypeName.Contains('<'))
            return; // Not a generic type — use RecordSwiftObjectType instead
        if (!IsFullyResolvedGeneric(closedGenericTypeName))
            return;
        // Runtime container types (SwiftOptional, SwiftArray, SwiftDictionary, SwiftSet)
        // have their own lazy metadata resolution. Registering them in the module
        // initializer causes eager element-metadata resolution before dependencies
        // are registered, crashing on NativeAOT device (e.g., SwiftArray<CycleTreeNode>
        // tries to resolve CycleTreeNode metadata before CycleTreeNode is registered).
        if (IsRuntimeContainerType(closedGenericTypeName))
            return;
        if (!_emittedSwiftObjectTypes.Contains(closedGenericTypeName))
            _emittedSwiftObjectTypes.Add(closedGenericTypeName);
    }

    /// <summary>
    /// Returns true if the type name is a runtime container type that has its own
    /// lazy metadata resolution. These must NOT be registered in the module initializer
    /// because they eagerly resolve generic argument metadata during GetTypeMetadata(),
    /// which can fail if the argument types haven't been registered yet.
    /// </summary>
    private static bool IsRuntimeContainerType(string typeName)
    {
        // Extract the base type name (before '<')
        var angleIdx = typeName.IndexOf('<');
        if (angleIdx < 0) return false;
        var baseName = typeName.Substring(0, angleIdx);

        return baseName is "SwiftOptional" or "Swift.SwiftOptional"
            or "SwiftArray" or "Swift.SwiftArray"
            or "SwiftDictionary" or "Swift.SwiftDictionary"
            or "SwiftSet" or "Swift.SwiftSet"
            or "SwiftResult" or "Swift.SwiftResult";
    }

    /// <summary>
    /// Checks whether a generic type name is fully resolved (all type arguments at every
    /// nesting level are namespace-qualified). Returns false for open generics like
    /// Box&lt;T&gt; or nested cases like Outer&lt;Mod.Pair&lt;T, Mod.B&gt;, Mod.C&gt;.
    /// </summary>
    private static bool IsFullyResolvedGeneric(string typeName)
    {
        var args = SplitTopLevelTypeArgs(typeName);
        foreach (var arg in args)
        {
            if (arg.Contains('<'))
            {
                // Nested generic: check the base type and recurse into its args
                var baseName = arg.Substring(0, arg.IndexOf('<'));
                if (!baseName.Contains('.'))
                    return false;
                if (!IsFullyResolvedGeneric(arg))
                    return false;
            }
            else if (!arg.Contains('.'))
            {
                return false; // Unresolved type parameter (e.g., "T", "T1")
            }
        }
        return true;
    }

    /// <summary>
    /// Splits a generic type name into its top-level type arguments, respecting nested
    /// angle bracket depth. For "A&lt;B.X&lt;C.Y, D.Z&gt;, E.W&gt;" returns ["B.X&lt;C.Y, D.Z&gt;", "E.W"].
    /// Returns all fragments (including nested generics) at depth 0 for unresolved-param detection.
    /// </summary>
    internal static List<string> SplitTopLevelTypeArgs(string typeName)
    {
        var result = new List<string>();
        var start = typeName.IndexOf('<');
        if (start < 0)
            return result;
        int depth = 0;
        int argStart = start + 1;
        for (int i = argStart; i < typeName.Length; i++)
        {
            char c = typeName[i];
            if (c == '<') depth++;
            else if (c == '>')
            {
                if (depth == 0)
                {
                    result.Add(typeName.Substring(argStart, i - argStart).Trim());
                    break;
                }
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                result.Add(typeName.Substring(argStart, i - argStart).Trim());
                argStart = i + 1;
            }
        }
        return result;
    }

    private readonly List<(string TypeName, string ProtocolInterface)> _emittedConformances = new();

    /// <summary>
    /// (TypeName, ProtocolInterface) pairs for NativeAOT conformance pre-registration.
    /// </summary>
    public IReadOnlyList<(string TypeName, string ProtocolInterface)> EmittedConformances => _emittedConformances;

    /// <summary>
    /// Records a conformance pair for NativeAOT pre-registration.
    /// Qualifies type references inside generic protocol interfaces (e.g., IEquatable&lt;Encoding&gt;
    /// becomes IEquatable&lt;Codec.Encoding&gt; when Encoding is nested inside Codec).
    /// </summary>
    public void RecordConformance(string csharpTypeName, string protocolInterfaceName,
        IReadOnlyList<AvailabilityAnnotation>? effectiveAvailability = null)
    {
        if (HasOpenGenericAncestor())
            return;
        var qualifiedName = GetQualifiedTypeName(csharpTypeName);
        var qualifiedProtocol = protocolInterfaceName;
        if (_typeNestingStack.Count > 0 && qualifiedProtocol.Contains($"<{csharpTypeName}>"))
        {
            qualifiedProtocol = qualifiedProtocol.Replace($"<{csharpTypeName}>", $"<{qualifiedName}>");
        }
        _emittedConformances.Add((qualifiedName, qualifiedProtocol));
        RecordTypeAvailability(qualifiedName, effectiveAvailability);
    }

    private string GetQualifiedTypeName(string csharpTypeName)
    {
        if (_typeNestingStack.Count > 0)
        {
            var prefix = string.Join(".", _typeNestingStack.Reverse());
            return $"{prefix}.{csharpTypeName}";
        }
        return csharpTypeName;
    }

    // ==================== Simple Enum Metadata Registration ====================

    private readonly List<(string CSharpTypeName, string MetadataSymbol, string WrapperLibName)> _simpleEnumMetadataRegistrations = new();

    /// <summary>
    /// Simple enum types that need metadata P/Invoke registration in the module initializer.
    /// Unlike ISwiftObject types (which self-register via GetTypeMetadata), simple C# enums
    /// need explicit metadata registration so that SwiftOptional&lt;T&gt; gets the correct Swift
    /// metadata (extra-inhabitant encoding) rather than falling back to the underlying integer type.
    /// </summary>
    public IReadOnlyList<(string CSharpTypeName, string MetadataSymbol, string WrapperLibName)> SimpleEnumMetadataRegistrations => _simpleEnumMetadataRegistrations;

    /// <summary>
    /// Records a simple enum type for metadata P/Invoke registration in the module initializer.
    /// </summary>
    public void RecordSimpleEnumMetadata(string csharpTypeName, string metadataSymbol, string wrapperLibName)
    {
        if (HasOpenGenericAncestor())
            return;
        var qualifiedName = GetQualifiedTypeName(csharpTypeName);
        _simpleEnumMetadataRegistrations.Add((qualifiedName, metadataSymbol, wrapperLibName));
    }

    // ==================== Class-Bound Existential Metadata Registration ====================

    private readonly List<(string LibraryName, string ProtocolDescriptorSymbol)> _classBoundExistentialRegistrations = new();
    private readonly HashSet<string> _classBoundExistentialSymbols = new(StringComparer.Ordinal);

    /// <summary>
    /// Class-bound (superclass- or <c>AnyObject</c>-constrained) protocols whose existentials
    /// are marshalled through <c>ClassExistentialContainer1</c> (16-byte <c>[classRef][witnessTable]</c>
    /// stride). The module initializer registers the shared class-existential value-witness metadata
    /// once via <c>TypeMetadata.RegisterClassBoundExistentialMetadata</c> so that
    /// <c>SwiftArray&lt;ClassExistentialContainer1&gt;</c> computes the correct element stride
    /// (the opaque <c>ExistentialContainer1</c> metadata would over-read at 40 bytes and crash).
    /// </summary>
    public IReadOnlyList<(string LibraryName, string ProtocolDescriptorSymbol)> ClassBoundExistentialRegistrations =>
        _classBoundExistentialRegistrations;

    /// <summary>
    /// Records a class-bound protocol's descriptor symbol + exporting library for class-existential
    /// metadata registration in the module initializer. Deduplicated on the descriptor symbol; the
    /// carrier metadata is protocol-agnostic for the arity so the first registration wins at runtime.
    /// </summary>
    public void RecordClassBoundExistentialRegistration(string libraryName, string protocolDescriptorSymbol)
    {
        if (string.IsNullOrEmpty(libraryName) || string.IsNullOrEmpty(protocolDescriptorSymbol))
            return;
        if (_classBoundExistentialSymbols.Add(protocolDescriptorSymbol))
            _classBoundExistentialRegistrations.Add((libraryName, protocolDescriptorSymbol));
    }

    // ==================== Protocol Proxy Sub-Namespace ====================

    private readonly List<string> _deferredProxyClasses = new();
    private readonly HashSet<string> _suppressedProxyClassNames = new(StringComparer.Ordinal);

    /// <summary>Accumulated proxy class source blocks for deferred emission in SwiftInterop sub-namespace.</summary>
    public IReadOnlyList<string> DeferredProxyClasses => _deferredProxyClasses;

    /// <summary>Adds a proxy class source block for deferred emission.</summary>
    public void AddDeferredProxyClass(string proxySource) => _deferredProxyClasses.Add(proxySource);

    /// <summary>Records a proxy class name that was suppressed because its EveryProtocol conformance was not emitted.</summary>
    public void RecordSuppressedProxy(string proxyClassName) => _suppressedProxyClassNames.Add(proxyClassName);

    /// <summary>Set of proxy class names suppressed due to missing EveryProtocol conformance.</summary>
    public IReadOnlySet<string> SuppressedProxyClassNames => _suppressedProxyClassNames;

    // ==================== Deferred Enum Extension Classes ====================

    private readonly List<string> _deferredEnumExtensionClasses = new();

    /// <summary>Accumulated enum extension class source blocks for deferred emission at namespace level.</summary>
    /// <remarks>
    /// Nested simple enums (e.g., ImageProcessingOptions.Unit) generate extension methods,
    /// but C# requires extension methods to be in top-level static classes. These are collected
    /// during nested enum emission and emitted at namespace level by ModuleHandler.
    /// </remarks>
    public IReadOnlyList<string> DeferredEnumExtensionClasses => _deferredEnumExtensionClasses;

    /// <summary>Adds an enum extension class source block for deferred namespace-level emission.</summary>
    public void AddDeferredEnumExtensionClass(string extensionSource) => _deferredEnumExtensionClasses.Add(extensionSource);

    // ==================== Authoritative Wrapper-Symbol Registry ====================
    //
    // Single source of truth for "did wrapper-emit emit a Swift @_cdecl symbol with
    // this name". Every per-kind TryAdd*WrapperSymbol method below funnels through
    // RegisterWrapperSymbolInternal, so the unified set mirrors the union of all per-kind
    // sets without callers having to remember to double-register. Binding-emit consults
    // this set via IsWrapperSymbolRegistered before emitting any P/Invoke whose entry
    // point follows the wrapper-symbol naming convention (SBW_…); the consult catches
    // the failure shape where binding-emit referenced a wrapper symbol that wrapper-emit
    // never actually produced.

    private readonly HashSet<string> _registeredWrapperSymbols = new(StringComparer.Ordinal);

    // symbol -> the artifact that owns it. This is the reverse map a Swift diagnostic needs:
    // swiftc names a symbol, and attribution has to answer "which declaration produced it".
    //
    // Ownership is passed in EXPLICITLY by the registering emitter rather than read from an
    // ambient "currently emitting" scope. An ambient stack is less code at the call sites, but
    // its failure mode is a symbol silently attributed to the enclosing type or module — and a
    // wrong owner is far worse than a missing one for a map that will drive denylisting: it
    // poisons a declaration that was never at fault. With explicit owners, a call site that
    // hasn't been threaded yet simply has no entry, which every consumer can detect.
    private readonly Dictionary<string, ArtifactId> _wrapperSymbolOwners = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns true when wrapper-emit registered the given Swift symbol via any of
    /// the per-kind TryAdd*WrapperSymbol methods. Consulted by
    /// <see cref="PInvokeEmitHelper"/> before emitting a P/Invoke whose entry point
    /// matches the wrapper-symbol naming convention.
    /// </summary>
    public bool IsWrapperSymbolRegistered(string symbol) =>
        !string.IsNullOrEmpty(symbol) && _registeredWrapperSymbols.Contains(symbol);

    /// <summary>
    /// Snapshot of every wrapper symbol registered for this module. Exposed for
    /// diagnostics and unit tests that need to assert against the registry without
    /// going through the per-kind APIs.
    /// </summary>
    public IReadOnlyCollection<string> RegisteredWrapperSymbols => _registeredWrapperSymbols;

    /// <summary>
    /// Looks up the artifact that registered <paramref name="symbol"/>. Returns false when the
    /// symbol is unregistered OR when its registering call site has not been threaded with an
    /// owner yet — callers must treat "no owner" as "unknown", never as "owned by nobody".
    /// </summary>
    public bool TryGetWrapperSymbolOwner(string symbol, out ArtifactId owner)
    {
        if (string.IsNullOrEmpty(symbol))
        {
            owner = default;
            return false;
        }
        return _wrapperSymbolOwners.TryGetValue(symbol, out owner);
    }

    /// <summary>Snapshot of every owned wrapper symbol, for diagnostics and tests.</summary>
    public IReadOnlyDictionary<string, ArtifactId> WrapperSymbolOwners => _wrapperSymbolOwners;

    // ==================== ABI call-plan side table ====================
    //
    // A typed descriptor per emitted native call, recorded as a side effect of rendering the P/Invoke
    // declaration (PInvokeEmitHelper.FormatDeclarationLines) so it is populated from the exact facts the
    // text is rendered from. This is the foundation for typed call-plan validation — the future consumer
    // reads these plans instead of regex-scanning the emitted C# (see AbiCallPlan / AbiContractChecker).
    // Recording here changes no rendered text; nothing reads the plans yet.

    // The set dedups on the whole plan VALUE (AbiCallPlan's structural equality), not on AbiCallPlan.Key.
    // Re-recording the same call within an attempt is a value-equal no-op — idempotent, since the plan is a
    // pure function of the same emission facts. Keying only on Key would silently drop a plan whenever two
    // distinct calls in different containing C# types shared a method name + entry point; value identity
    // keeps every genuinely distinct call.
    private readonly HashSet<AbiCallPlan> _abiCallPlans = new();

    // The side table lives on the static Default context (see the class-level note), which unit tests
    // reach concurrently through TypeHandlerContext.Empty when xUnit runs emitter test classes in
    // parallel. A bare HashSet.Add throws "concurrent update corrupted its state" under that race, so
    // the public RecordAbiCallPlan/AbiCallPlans access paths serialize on this lock. (The reflective
    // recovery snapshot enumerates the set without it, but only ever on a private, single-threaded
    // recovery context — never the shared Default — so that path cannot race.) Production emission is
    // single-threaded, so the lock is uncontended and the rendered text is unchanged.
    private readonly object _abiCallPlansLock = new();

    /// <summary>
    /// Records the typed <see cref="AbiCallPlan"/> for one emitted native call. Deduped by the plan's full
    /// value, so re-recording the same call (a re-render, or a reused context) is an idempotent no-op while
    /// two distinct calls are both kept.
    /// </summary>
    public void RecordAbiCallPlan(AbiCallPlan plan)
    {
        if (plan is null)
            return;
        lock (_abiCallPlansLock)
            _abiCallPlans.Add(plan);
    }

    /// <summary>
    /// Every recorded call plan for this module, in a total deterministic order independent of emission
    /// order (the <see cref="AbiCallPlan.Key"/> is the primary sort, with the remaining fields breaking any
    /// shared-key tie). Exposed for the future validator and for the tests that assert population +
    /// determinism.
    /// </summary>
    public IReadOnlyList<AbiCallPlan> AbiCallPlans
    {
        get
        {
            // Snapshot under the lock (enumerating the HashSet while another thread Adds throws too),
            // then sort the private copy outside it.
            AbiCallPlan[] snapshot;
            lock (_abiCallPlansLock)
                snapshot = _abiCallPlans.ToArray();
            return snapshot
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .ThenBy(p => p.Library, StringComparer.Ordinal)
            .ThenBy(p => p.ReturnCarrier, StringComparer.Ordinal)
            .ThenBy(p => (int)p.CallingConvention)
            .ThenBy(p => p.IsAsync)
            .ThenBy(p => string.Join('\u0001', p.ParameterCarriers), StringComparer.Ordinal)
            .ToList();
        }
    }

    /// <summary>
    /// The one place a symbol enters the unified registry, and therefore the one place ownership
    /// can be recorded. Both <see cref="RegisterWrapperSymbolInternal"/> and
    /// <see cref="TryClaimWrapperSymbol"/> route through it: the claim path used to call
    /// <c>_registeredWrapperSymbols.Add</c> directly, which meant the emitter responsible for most
    /// ordinary method wrappers bypassed the funnel entirely.
    /// </summary>
    /// <remarks>
    /// Ownership is recorded only on a winning registration, so the loser of a collision never
    /// overwrites the surviving artifact's claim.
    /// </remarks>
    private bool AddWrapperSymbolInternal(string symbol, DeclId? owner, ArtifactRole role)
    {
        // Cross-kind collision check via the unified registry — two emitters (e.g.
        // MethodWrapperEmitter + ProtocolExtensionEmitter for the same protocol-extension
        // method synthesised onto a conforming type) can each try to register the same
        // @_cdecl C symbol from independent per-kind sets. Both emissions then fire and
        // swiftc rejects with "multiple definitions of symbol" at link time. The unified
        // set is the linker's view; if it already has this symbol, reject the second
        // registration so the caller skips its emission.
        if (!_registeredWrapperSymbols.Add(symbol))
            return false;

        if (owner is { } declId)
            _wrapperSymbolOwners[symbol] = ArtifactId.Create(declId, role);

        return true;
    }

    private bool RegisterWrapperSymbolInternal(
        HashSet<string> kindSet,
        string symbol,
        DeclId? owner = null,
        ArtifactRole role = ArtifactRole.SwiftWrapper)
    {
        if (!AddWrapperSymbolInternal(symbol, owner, role))
            return false;
        kindSet.Add(symbol);
        return true;
    }

    // The in-band wrapper-symbol contract is now predict-then-skip: a P/Invoke whose
    // @_cdecl wrapper symbol won't be registered is never emitted (nor is its caller), so
    // there is no orphan call site to reconcile after the fact. The former contract-violation
    // side table (consumed by the deleted generate-then-strip post-pass) is gone with it.

    // ==================== Constructor Wrapper ====================

    private readonly HashSet<string> _constructorWrapperSymbols = new();

    /// <summary>Checks if a constructor @_cdecl wrapper symbol was already emitted for this type.</summary>
    public bool HasConstructorWrapperSymbol(string symbol) => _constructorWrapperSymbols.Contains(symbol);

    /// <summary>Adds a constructor wrapper symbol. Returns true if newly added.</summary>
    public bool TryAddConstructorWrapperSymbol(string symbol, DeclId? owner = null) =>
        RegisterWrapperSymbolInternal(_constructorWrapperSymbols, symbol, owner);

    // ==================== ObjC Override Property Wrapper ====================

    private readonly HashSet<string> _objcPropertyWrapperSymbols = new();

    /// <summary>Checks if an ObjC override property wrapper symbol was already emitted.</summary>
    public bool HasObjCPropertyWrapperSymbol(string symbol) => _objcPropertyWrapperSymbols.Contains(symbol);

    /// <summary>Adds an ObjC override property wrapper symbol. Returns true if newly added.</summary>
    public bool TryAddObjCPropertyWrapperSymbol(string symbol, DeclId? owner = null) =>
        RegisterWrapperSymbolInternal(_objcPropertyWrapperSymbols, symbol, owner);

    // ==================== Property @_cdecl Wrapper ====================

    private readonly HashSet<string> _propertyWrapperSymbols = new();

    /// <summary>Adds a property @_cdecl wrapper symbol. Returns true if newly added (not a duplicate).</summary>
    public bool TryAddPropertyWrapperSymbol(string symbol, DeclId? owner = null) =>
        RegisterWrapperSymbolInternal(_propertyWrapperSymbols, symbol, owner);

    // ==================== Method @_cdecl Wrapper ====================

    private readonly HashSet<string> _methodWrapperSymbols = new();

    /// <summary>Adds a method @_cdecl wrapper symbol. Returns true if newly added (not a duplicate).</summary>
    public bool TryAddMethodWrapperSymbol(string symbol, DeclId? owner = null) =>
        RegisterWrapperSymbolInternal(_methodWrapperSymbols, symbol, owner);

    // ============ Existential-bypass `Create` factory name reservation ============

    // Keyed by "{typeKey}::{param-type signature}". The existential-bypass emitter prefers a
    // hash-free `Create` factory but must never emit two `Create` overloads with the same C#
    // parameter TYPES on one type (CS0111). The first bypass factory to reserve a given
    // (type, param-types) slot gets the bare name; a later factory on the same type with the
    // same overload signature loses the reservation and keeps its deterministic Create_{hash}.
    private readonly HashSet<string> _existentialBypassCreateSignatures = new();

    /// <summary>
    /// Reserves the bare <c>Create</c> existential-bypass factory name for a type, keyed by the
    /// factory's parameter-type signature. Returns true if the (type, param-types) slot was free
    /// (caller emits a hash-free <c>Create</c>); false if a prior bypass factory on the same type
    /// already claimed the same C# overload signature (caller keeps <c>Create_{hash}</c>). Keyed
    /// on parameter TYPES, not names, so it mirrors C#'s own overload identity — two
    /// <c>Create(sameTypes)</c> can never coexist.
    /// </summary>
    public bool TryReserveExistentialBypassCreate(string typeKey, string paramTypeSignature) =>
        _existentialBypassCreateSignatures.Add(typeKey + "::" + paramTypeSignature);

    // ==================== Emission Symbol Side Table (AF13) ====================

    // Keyed by MethodDecl *reference identity* — MethodDecl is a record (value equality),
    // so a default dictionary would collapse two structurally-equal-but-distinct decls and
    // a `with`-synthesized decl would never match its source. ReferenceEqualityComparer
    // pins the key to the exact decl instance the base handler emitted.
    private readonly Dictionary<MethodDecl, string> _emissionSymbolByMethod =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// AF13: records the emission-time symbol (<see cref="MethodEnvironment.EmissionSymbol"/>) a
    /// method's main emission settled on, keyed by the decl instance. Lets a later, env-less
    /// emitter that historically read a *sibling* method's promoted <c>MangledName</c> off the
    /// shared decl (e.g. <c>ConcreteProtocolSpecializationEmitter</c> reading a constructor's
    /// promoted symbol after <c>ClassHandler</c> emitted the type's methods) recover that exact
    /// value from the emission-scoped side table instead of from in-place mutation of the parser
    /// model. Called once per method from the base-handler post-emit hook.
    /// </summary>
    public void RecordMethodEmissionSymbol(MethodDecl methodDecl, string emissionSymbol) =>
        _emissionSymbolByMethod[methodDecl] = emissionSymbol;

    /// <summary>
    /// Returns the recorded emission-time symbol for <paramref name="methodDecl"/>, or the decl's
    /// own silgen <see cref="MethodDecl.MangledName"/> when the method was never emitted through the
    /// base handler (or never promoted). The fallback reproduces the historical read of an
    /// un-promoted decl exactly, so consumers stay byte-stable for methods absent from the table.
    /// </summary>
    public string GetMethodEmissionSymbolOrMangled(MethodDecl methodDecl) =>
        _emissionSymbolByMethod.TryGetValue(methodDecl, out var symbol) ? symbol : methodDecl.MangledName;

    // ==================== Suppressed-Proxy Accessor Produce-Throw Side Table ====================

    // A private property/subscript accessor method (`{Name}_Get()`) whose PRODUCE-path proxy was
    // suppressed restubs to a throw in WrapperEmitter — but its PUBLIC surface is the property/subscript
    // getter that *delegates* to it (`get => Name_Get();`). Poisoning the private accessor with
    // [Obsolete(error:true)] would break that generated delegation, so the accessor is recorded here and
    // the *public* getter is poisoned instead. Keyed by reference identity (same rationale as the emission
    // symbol table): the property handler reads the flag off the exact accessor decl instance the wrapper
    // emitter restubbed, and the property surface is emitted after the accessor in the same handler pass.
    private readonly HashSet<MethodDecl> _produceThrowAccessors = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Records that a synthesized property/subscript accessor method restubbed to a suppressed-proxy
    /// throw during its own emission, so the public getter that delegates to it can be marked
    /// <c>[Obsolete(error: true)]</c> instead (poisoning the private accessor would break the generated
    /// <c>get =&gt; Name_Get();</c> delegation).
    /// </summary>
    public void RecordAccessorProduceThrow(MethodDecl accessorMethod) =>
        _produceThrowAccessors.Add(accessorMethod);

    /// <summary>
    /// Returns true if <paramref name="accessorMethod"/> restubbed to a suppressed-proxy throw and its
    /// delegating public getter must therefore carry the compile-time-visible SB0006 marker.
    /// </summary>
    public bool WasAccessorProduceThrow(MethodDecl accessorMethod) =>
        _produceThrowAccessors.Contains(accessorMethod);

    // A public property whose GETTER was compile-poisoned (SB0006) because its suppressed-proxy read can
    // only throw. Set by BOTH EmitGetter poison branches — the scalar/optional-existential early-return
    // (which delegates to a restubbed private accessor) AND the collection/optional-element projection
    // catch (which inlines a throw and records via SuppressedProxyReporting, NOT the accessor side-table
    // above). A CS0542 name-collision explicit-interface bridge reads this to emit a direct throw body
    // instead of `get => {renamedName}` — reading the `[Obsolete(error:true)]` public property would be a
    // CS0619 build error. Keyed by reference identity: the bridge is emitted after the getter in the same
    // handler pass, off the same PropertyDecl instance.
    private readonly HashSet<PropertyDecl> _produceThrowGetters = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Records that <paramref name="propertyDecl"/>'s public getter was compile-poisoned (SB0006) — its
    /// suppressed-proxy read can only throw — so a CS0542 explicit-interface bridge for it must emit a
    /// direct throw rather than read the poisoned public property (which would be CS0619).
    /// </summary>
    public void RecordGetterProduceThrow(PropertyDecl propertyDecl) =>
        _produceThrowGetters.Add(propertyDecl);

    /// <summary>
    /// Returns true if <paramref name="propertyDecl"/>'s public getter carries the SB0006 poison, so any
    /// generated read of the public property (e.g. a CS0542 interface bridge) must be routed to a throw.
    /// </summary>
    public bool WasGetterProduceThrow(PropertyDecl propertyDecl) =>
        _produceThrowGetters.Contains(propertyDecl);

    // A subscript whose PUBLIC indexer getter was compile-poisoned (SB0006) because its suppressed-proxy
    // read can only throw. Set at BOTH poison sites in SubscriptHandler.EmitIndexerGetter — the scalar
    // existential early-return (WasAccessorProduceThrow) AND the collection/optional element projection
    // catch. Read by the deferred int/uint convenience-overload pass (NativeIntOverloadEmitter
    // .TryEmitIndexerOverload): a convenience `this[int]` overload must NOT forward `this[(nint)i]` to a
    // poisoned primary getter (that read is CS0619/SB0006), so it mirrors the poison — a throwing,
    // [Obsolete(error:true)] getter — while keeping its setter forward where the primary has one. Keyed by
    // reference identity, off the same SubscriptDecl instance the second pass sees.
    private readonly HashSet<SubscriptDecl> _produceThrowSubscriptGetters = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Records that <paramref name="subscriptDecl"/>'s public indexer getter was compile-poisoned (SB0006),
    /// so the deferred int/uint convenience overload mirrors the poison instead of forwarding into it.
    /// </summary>
    public void RecordSubscriptGetterProduceThrow(SubscriptDecl subscriptDecl) =>
        _produceThrowSubscriptGetters.Add(subscriptDecl);

    /// <summary>
    /// Returns true if <paramref name="subscriptDecl"/>'s public indexer getter carries the SB0006 poison,
    /// so its convenience overload must emit a throwing getter rather than a forward to the poisoned primary.
    /// </summary>
    public bool WasSubscriptGetterProduceThrow(SubscriptDecl subscriptDecl) =>
        _produceThrowSubscriptGetters.Contains(subscriptDecl);

    // A member (method / property / subscript) that CONSUME-degraded: a setter/parameter position where
    // a C#-authored conformer of an existential can no longer be marshalled in, because the referenced
    // {Protocol}Proxy's EveryProtocol reverse-dispatch conformance was suppressed. The member still works
    // for Swift-vended conformers, so — unlike the produce-throw arm (SB0006, error:true) — the read/return
    // is fine; only a C# conformer passed into the degraded position silently no-fires. The unmarked arm
    // was invisible at compile time; this side table lets the owning emitter mark the member with a
    // warning-level [Obsolete(...SB0008)] AFTER emission (the degrade is discovered while the signature is
    // already written). Keyed by reference identity, like the emission-symbol table: a record is value-equal,
    // so a `with`-cloned or structurally-equal sibling would otherwise steal the flag.
    private readonly HashSet<BaseDecl> _consumeDegradedMembers = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Records that <paramref name="member"/> has a CONSUME-degraded position (a setter/parameter that
    /// drops its C#-conformer wrap fallback for a suppressed <c>{Protocol}Proxy</c>). Idempotent — a
    /// member with several degraded positions is marked once. Read by the owning emitter to inject the
    /// warning-level <c>[Obsolete(..., DiagnosticId = "SB0008")]</c> marker.
    /// </summary>
    public void RecordConsumeDegradedMember(BaseDecl member) => _consumeDegradedMembers.Add(member);

    /// <summary>
    /// Returns true if <paramref name="member"/> was recorded as CONSUME-degraded, so its emitter should
    /// carry the warning-level consume-degrade marker (a C#-authored conformer set/passed here never fires).
    /// </summary>
    public bool WasConsumeDegradedMember(BaseDecl member) => _consumeDegradedMembers.Contains(member);

    // ==================== API Manifest (retarget gate) ====================

    // Accumulates the consumer-visible binding contract: each emitted, overload-
    // disambiguated public member's C# signature key → the native entry symbol the
    // P/Invoke binds. Recorded at the SAME two chokepoints the overload
    // disambiguation runs (the type-body method/ctor loop in IHandler and the free-
    // function loop in ModuleHandler), where the post-collision C# name
    // (MethodEnvironment.CSharpMethodName) and the promoted entry symbol
    // (MethodEnvironment.EmissionSymbol) are both in scope. A *post-hoc* model walk
    // can't reconstruct the C# name — its collision suffix is set only inside that
    // loop — so accumulation at the source is the only place the true key exists.
    //
    // Keyed by the member signature (parent-qualified C# name + projected C# param
    // types) and Ordinal-sorted so the serialized manifest is deterministic across
    // runs. Last-write-wins on a duplicate key: overload disambiguation already makes
    // the key unique within a type body, and parent-qualification separates types, so
    // a collision here would be a manifest-key bug rather than a retarget — the gate
    // compares baseline↔current on matching keys, so any stable key affects both
    // sides equally. Properties/subscripts are intentionally out of v1 scope: they
    // carry no overload disambiguation, so they are not the "same C# name retargets to
    // a different symbol" hazard the gate guards.
    private readonly SortedDictionary<string, string> _apiManifest = new(StringComparer.Ordinal);

    /// <summary>
    /// Records one emitted public member's C# signature key → native entry symbol
    /// into the API manifest. Called from the overload-disambiguation chokepoints once
    /// the post-collision C# name is known.
    /// </summary>
    public void RecordApiManifestEntry(string memberSignature, string entrySymbol) =>
        _apiManifest[memberSignature] = entrySymbol;

    /// <summary>The accumulated C# signature → entry symbol map, Ordinal-sorted.</summary>
    public IReadOnlyDictionary<string, string> ApiManifestEntries => _apiManifest;

    /// <summary>
    /// Builds the API-manifest signature key for an emitted member: the parent-type path
    /// (dot-joined, module excluded — the manifest is per-module) plus the post-collision C#
    /// name and the projected C# parameter portion of <paramref name="projectedKey"/>. The
    /// method/free-function name always precedes its parameter list, so the first '(' reliably
    /// splits name from params even when a projected param type contains parentheses.
    /// </summary>
    public static string BuildApiManifestKey(BaseDecl? parent, string csharpName, string projectedKey,
        ITypeDatabase? typeDatabase = null)
    {
        int paren = projectedKey.IndexOf('(');
        string paramPortion = paren >= 0 ? projectedKey.Substring(paren) : "()";
        string parentPath = BuildParentPath(parent, typeDatabase);
        return parentPath.Length > 0
            ? $"{parentPath}.{csharpName}{paramPortion}"
            : $"{csharpName}{paramPortion}";
    }

    private static string BuildParentPath(BaseDecl? parent, ITypeDatabase? typeDatabase)
    {
        if (parent is null or ModuleDecl) return "";
        // Prefer the authoritative C# nested path from the TypeDatabase so an emission-time
        // nested-type rename (Entry → EntryInfo when a sibling `entry` property collides) is
        // reflected in a member's parent path — keeping a member declared ON the renamed type
        // consistent with that same type as it appears in parameter positions (both would
        // otherwise disagree: parameters resolve through the renamed record, the raw decl-name
        // walk does not). The registered C# name is module-excluded and carries the full nested
        // path, matching this manifest's per-module keying. Fall back to the raw decl-name walk
        // when the parent has no registered record (synthetic/unresolved decls).
        if (typeDatabase != null && parent is TypeDecl parentType
            && typeDatabase.TryGetTypeRecord(parentType.SwiftTypeName, out var rec)
            && !string.IsNullOrEmpty(rec.CSharpTypeName.Name))
        {
            return rec.CSharpTypeName.Name;
        }
        var names = new List<string>();
        for (BaseDecl? d = parent; d is not null and not ModuleDecl; d = d.ParentDecl)
            names.Add(d.Name);
        names.Reverse();
        return string.Join(".", names);
    }

    // ==================== Cross-Emitter Structural Identity ====================
    //
    // Two emitters can produce wrappers for the same Swift method with *different*
    // @_cdecl symbol strings:
    //   - MethodWrapperEmitter:        SBW_<Module>_<Type>_<method>_<hash8>
    //   - ProtocolExtensionEmitter:    SBW_<FlatType>_<method>_<labels>
    // String-keyed dedup misses; both @_cdecl blocks land in the wrapper file and
    // swiftc rejects "multiple definitions of symbol" at link time. The canonical
    // dedup key is structural — a 3-tuple of <c>(typeName, methodName, sourceKey)</c>
    // — independent of the emitter-specific symbol scheme.
    //
    // Tier A emitters (per-emitter sourceKey conventions; all of these route
    // through TryClaimWrapperSymbol on every emit):
    //   - <see cref="MethodWrapperEmitter"/> — typeName is the parent's
    //     <c>SwiftTypeName.ModuleQualifiedName</c> (or parent module name for
    //     free functions); sourceKey is <see cref="MethodDecl.StructuralIdentityKey"/>
    //     when set by an upstream emitter, otherwise the rendered <c>SBW_</c>
    //     symbol itself. The fallback is equivalent to the prior per-kind
    //     <c>TryAddMethodWrapperSymbol</c> for ordinary methods.
    //   - <see cref="Handler.ProtocolExtensionEmitter"/> — stashes
    //     <c>"{ProtocolQualifiedName}::{PrintedName}::{RawSignature}"</c> on the
    //     synthetic <see cref="MethodDecl.StructuralIdentityKey"/> so a later
    //     <see cref="MethodWrapperEmitter"/> pass on the same Swift method
    //     collapses into the same identity.
    //   - <see cref="Handler.ExistentialBypassEmitter"/> — claim-then-emit;
    //     sourceKey is <c>"existential-bypass-{init|free|method}::{MangledName}"</c>
    //     and is also stashed on the original <see cref="MethodDecl.StructuralIdentityKey"/>
    //     so a fallback <see cref="MethodWrapperEmitter"/> pass canonicalizes
    //     to the same triple.
    //   - <see cref="Handler.MetatypeArrayBridgeEmitter"/> — claim-then-emit;
    //     sourceKey is <c>"metatype-array-bridge::{MangledName}"</c> (captured
    //     from the original <c>methodDecl.MangledName</c> before the line-99
    //     stomp) and is also stashed on the original
    //     <see cref="MethodDecl.StructuralIdentityKey"/>.
    //   - <see cref="Handler.ForeignTypeExtensionEmitter"/> — sourceKey is
    //     <c>"{role} {MethodName}::{RawSignature}"</c> where <c>role</c> is
    //     <c>"get"</c>, <c>"set"</c>, or <c>"method"</c>. No
    //     <see cref="MethodWrapperEmitter"/> counterpart exists today (foreign-
    //     type extension surfaces don't flow through a parsed
    //     <see cref="MethodDecl"/>); the claim is preventive against a future
    //     emitter joining the same surface.
    //   - <see cref="Handler.ConstrainedExtensionEmitter"/> — typeName is
    //     <c>"{ParentTypeName.ModuleQualifiedName}&lt;{ConcreteTypeName.ModuleQualifiedName}&gt;"</c>
    //     so two parents that share a leaf name (e.g. <c>Mod.Box</c> vs
    //     <c>Mod.Outer.Box</c>) and two concretes that share a leaf across
    //     modules (e.g. <c>A.User</c> vs <c>B.User</c>) cannot collide; sourceKey
    //     for properties is
    //     <c>"constrained-extension::get::{Parent.ModuleQualifiedName}::{Concrete.ModuleQualifiedName}::{static|instance}::{Name}"</c>
    //     and for methods is
    //     <c>"constrained-extension::method::{Parent.ModuleQualifiedName}::{Concrete.ModuleQualifiedName}::{static|instance}::{Name}"</c>.
    //     The static/instance marker disambiguates Swift's allowance of
    //     <c>func rank()</c> alongside <c>static func rank()</c> (and
    //     analogously <c>var rank</c> alongside <c>static var rank</c>) on the
    //     same type. Intentionally intra-emitter — MWE walks the open generic
    //     parent, never these closed generic concretizations.
    //
    // The first emitter to claim the structural identity wins; subsequent
    // emitters for the same Swift method skip emission. Tier B/C emitters
    // (synthetic / dedicated namespace wrappers like _equality, _XM/_XMA, JSON
    // codable, SwiftUI direct helpers, metadata accessors, closure bridges)
    // remain on per-kind <c>TryAdd*WrapperSymbol</c> sets — see the audit
    // comments at each call site for why their bucket is collision-safe.

    private readonly HashSet<(string TypeName, string MethodName, string SourceKey)> _wrapperStructuralIdentities = new();

    /// <summary>
    /// Cross-emitter @_cdecl wrapper dedup keyed by structural identity rather
    /// than the emitter-specific symbol string. Returns true if this emitter
    /// wins (first to claim the identity); false if a previous emitter already
    /// claimed the same <c>(typeName, methodName, sourceKey)</c> triple — in
    /// which case the caller must skip its emission. Also registers
    /// <paramref name="symbol"/> in the unified
    /// <c>_registeredWrapperSymbols</c> set so
    /// <see cref="IsWrapperSymbolRegistered"/> reflects the surviving wrapper.
    /// <paramref name="sourceKey"/> is the per-emitter canonical string: the
    /// rendered <c>SBW_</c> symbol for <see cref="MethodWrapperEmitter"/> on
    /// ordinary methods, and a <c>ProtocolQualifiedName::PrintedName::RawSignature</c>
    /// string for the protocol-extension path. See the section header above
    /// for the rules each emitter follows.
    /// </summary>
    /// <param name="owner">
    /// Identity of the declaration whose wrapper this is, recorded against
    /// <paramref name="symbol"/> for later attribution. Optional: an un-threaded call site leaves
    /// the symbol unowned rather than mis-owned.
    /// </param>
    public bool TryClaimWrapperSymbol(
        string typeName,
        string methodName,
        string sourceKey,
        string symbol,
        DeclId? owner = null)
    {
        var identity = (typeName ?? string.Empty, methodName ?? string.Empty, sourceKey ?? string.Empty);
        if (!_wrapperStructuralIdentities.Add(identity))
            return false;

        if (!string.IsNullOrEmpty(symbol) && !AddWrapperSymbolInternal(symbol, owner, ArtifactRole.SwiftWrapper))
        {
            // The structural slot is now claimed but the symbol string was
            // already registered by some other path (e.g. constructor wrapper
            // with a coincidentally-similar SBW_ name). Roll the structural
            // claim back so the caller doesn't believe it won.
            _wrapperStructuralIdentities.Remove(identity);
            return false;
        }

        return true;
    }

    // ==================== CSM-Async Signature Claims ====================
    // Two-state claim shared between the Phase-4a eligibility predicate and the
    // actual async emitter. Reservation (predicate) is idempotent for the same
    // owner so repeat predicate calls agree, but the Emitted bit flips exactly
    // once per (sigKey, owner). That keeps the predicate/emitter handoff sound
    // while preventing two pairings of the SAME method that collapse to the same
    // C# signature from producing duplicate CS0111 member emissions.
    private readonly Dictionary<string, (MethodDecl Owner, bool Emitted)> _csmAsyncClaims
        = new(StringComparer.Ordinal);

    /// <summary>
    /// Reserves a CSM-async signature key for <paramref name="owner"/> during the
    /// Phase-4a eligibility dry-run. Returns true if the caller owns the reservation
    /// (either freshly reserved or previously reserved by the same method and not
    /// yet emitted). Returns false if the key is already emitted, or reserved by a
    /// different method — in which case the predicate falls back to other pairings
    /// (or keeps the generic SB0001 fallback alive).
    /// </summary>
    public bool TryReserveCsmAsyncSignature(string sigKey, MethodDecl owner)
    {
        if (_csmAsyncClaims.TryGetValue(sigKey, out var existing))
        {
            if (existing.Emitted) return false;
            return ReferenceEquals(existing.Owner, owner);
        }
        _csmAsyncClaims[sigKey] = (owner, Emitted: false);
        return true;
    }

    /// <summary>
    /// Commits a CSM-async signature key for <paramref name="owner"/> at emission
    /// time. Promotes an existing reservation (owned by the same method) to Emitted,
    /// or creates a fresh Emitted entry when the emitter runs without a predicate
    /// reservation (non-CSM test paths). Returns false when the key was already
    /// emitted (duplicate within the same method or a lost race across methods),
    /// or when a reservation exists for a different method.
    /// </summary>
    public bool TryCommitCsmAsyncSignature(string sigKey, MethodDecl owner)
    {
        if (_csmAsyncClaims.TryGetValue(sigKey, out var existing))
        {
            if (existing.Emitted) return false;
            if (!ReferenceEquals(existing.Owner, owner)) return false;
            _csmAsyncClaims[sigKey] = (owner, Emitted: true);
            return true;
        }
        _csmAsyncClaims[sigKey] = (owner, Emitted: true);
        return true;
    }

    // ==================== Metadata @_cdecl Wrapper ====================

    private readonly HashSet<string> _metadataWrapperSymbols = new();

    /// <summary>Adds a metadata @_cdecl wrapper symbol. Returns true if newly added (not a duplicate).</summary>
    public bool TryAddMetadataWrapperSymbol(string symbol, DeclId? owner = null) =>
        MarkSharedSwiftArtifact(RegisterWrapperSymbolInternal(_metadataWrapperSymbols, symbol, owner, ArtifactRole.MetadataHelper));

    // ==================== Enum Handler RawRepresentable ====================

    private readonly HashSet<string> _enumRawRepSymbols = new();

    /// <summary>Checks if an enum RawRepresentable wrapper symbol was already emitted.</summary>
    public bool HasEnumRawRepWrapperSymbol(string symbol) => _enumRawRepSymbols.Contains(symbol);

    /// <summary>Adds an enum RawRepresentable wrapper symbol. Returns true if newly added.</summary>
    public bool TryAddEnumRawRepWrapperSymbol(string symbol, DeclId? owner = null) =>
        MarkSharedSwiftArtifact(RegisterWrapperSymbolInternal(_enumRawRepSymbols, symbol, owner, ArtifactRole.MetadataHelper));

    // ==================== Equality @_cdecl Wrapper ====================

    private readonly HashSet<string> _equalityWrapperSymbols = new();

    /// <summary>Adds an equality @_cdecl wrapper symbol. Returns true if newly added (not a duplicate).</summary>
    public bool TryAddEqualityWrapperSymbol(string symbol, DeclId? owner = null) =>
        MarkSharedSwiftArtifact(RegisterWrapperSymbolInternal(_equalityWrapperSymbols, symbol, owner, ArtifactRole.MetadataHelper));

    // ==================== Metadata Accessor Helper ====================

    private readonly HashSet<string> _metadataAccessorHelperSymbols = new();

    /// <summary>Adds a metadata accessor helper symbol. Returns true if newly added (not a duplicate).</summary>
    public bool TryAddMetadataAccessorHelper(string typeMangledName, DeclId? owner = null) =>
        MarkSharedSwiftArtifact(RegisterWrapperSymbolInternal(_metadataAccessorHelperSymbols, typeMangledName, owner, ArtifactRole.MetadataHelper));

    // ==================== Optional Tag Helper ====================

    private readonly HashSet<string> _optionalTagHelperSymbols = new();

    /// <summary>Adds an Optional tag helper @_cdecl symbol. Returns true if newly added (not a duplicate).</summary>
    public bool TryAddOptionalTagHelperSymbol(string symbol, DeclId? owner = null) =>
        MarkSharedSwiftArtifact(RegisterWrapperSymbolInternal(_optionalTagHelperSymbols, symbol, owner, ArtifactRole.MetadataHelper));

    // ==================== Direct Helper Symbols ====================
    //
    // Catch-all kind for direct @_cdecl helpers emitted outside MethodWrapperEmitter
    // (e.g., GenericClosureBridgeEmitter's SBW_CreateError_{module}). The matching
    // P/Invoke side goes through PInvokeEmitHelper.EmitDeclaration today, which is
    // not enforcement-backed — but the registry must still reflect the symbol so
    // any future widening of the contract gate doesn't false-trip.

    private readonly HashSet<string> _directHelperSymbols = new();

    /// <summary>Adds a direct @_cdecl helper symbol (non-method/property/constructor).
    /// Returns true if newly added (not a duplicate).</summary>
    public bool TryAddDirectHelperWrapperSymbol(string symbol, DeclId? owner = null) =>
        RegisterWrapperSymbolInternal(_directHelperSymbols, symbol, owner);

    // ==================== Emission Report Accumulators ====================

    private readonly Dictionary<string, int> _wrapperStrategyCounts = new();
    private readonly Dictionary<string, int> _wrapperSkipReasons = new();

    /// <summary>Wrapper strategy usage counts (e.g., "CdeclMethod": 98).</summary>
    public IReadOnlyDictionary<string, int> WrapperStrategyCounts => _wrapperStrategyCounts;

    /// <summary>Wrapper skip reason counts (e.g., "methodLevelGenerics": 3).</summary>
    public IReadOnlyDictionary<string, int> WrapperSkipReasons => _wrapperSkipReasons;

    /// <summary>Increments the count for a wrapper strategy.</summary>
    public void IncrementWrapperStrategy(string strategyName)
    {
        _wrapperStrategyCounts.TryGetValue(strategyName, out var count);
        _wrapperStrategyCounts[strategyName] = count + 1;
    }

    /// <summary>Increments the count for a wrapper skip reason.</summary>
    public void IncrementWrapperSkipReason(string reason)
    {
        _wrapperSkipReasons.TryGetValue(reason, out var count);
        _wrapperSkipReasons[reason] = count + 1;
    }

    // ==================== Silent Tombstones ====================

    private readonly HashSet<string> _silentTombstones = new();

    /// <summary>
    /// Module-qualified names of types that were emitted with [OpaqueSwiftType] but have zero
    /// usable surface (opaqueEmittable == 0 && opaqueSkipped > 0). Used to annotate call sites
    /// whose return type is a silent tombstone so audits can grep for SB0002 diagnostics.
    /// </summary>
    public IReadOnlyCollection<string> SilentTombstones => _silentTombstones;

    /// <summary>Records that a type was emitted as a silent tombstone.</summary>
    public void AddSilentTombstone(string moduleQualifiedName)
    {
        if (!string.IsNullOrEmpty(moduleQualifiedName))
            _silentTombstones.Add(moduleQualifiedName);
    }

    /// <summary>Returns true if the given module-qualified type name was recorded as a silent tombstone.</summary>
    public bool IsSilentTombstone(string moduleQualifiedName)
    {
        return !string.IsNullOrEmpty(moduleQualifiedName) && _silentTombstones.Contains(moduleQualifiedName);
    }

    private readonly HashSet<string> _emittedOpaqueTypes = new();

    /// <summary>
    /// Module-qualified names of types where a handler actually emitted the
    /// <c>[OpaqueSwiftType]</c> annotation at the class header. The registrar pre-pass
    /// in <see cref="SilentTombstoneRegistrar"/> claims to mirror that decision; this set
    /// is the ground-truth side of the invariant <c>SilentTombstones ⊆ EmittedOpaqueTypes</c>,
    /// asserted before <c>binding-emission-report.json</c> is written. A break means the
    /// registrar's predicate has drifted from handler reality and a metadata-cookie reference
    /// to a tombstoned type would dangle.
    /// </summary>
    public IReadOnlyCollection<string> EmittedOpaqueTypes => _emittedOpaqueTypes;

    /// <summary>Records that a handler actually emitted a <c>[OpaqueSwiftType]</c> annotation.</summary>
    public void AddEmittedOpaqueType(string moduleQualifiedName)
    {
        if (!string.IsNullOrEmpty(moduleQualifiedName))
            _emittedOpaqueTypes.Add(moduleQualifiedName);
    }

    // ==================== Degraded Existentials (Defect E) ====================

    private readonly HashSet<string> _degradedExistentials = new();

    /// <summary>
    /// Swift textual forms (e.g. <c>any AttributeKind</c>) of protocol existentials that could not
    /// be projected to a real C# type and degraded to <c>object</c>. Each distinct type is recorded
    /// once across however many member surfaces touched it; <see cref="EmissionReportEmitter.Emit"/>
    /// turns the set into one loud SWIFTBIND023 warning per type so the degradation is no longer
    /// silent behind only the consumer-facing <c>[UnsupportedSwiftType]</c> attribute.
    /// </summary>
    public IReadOnlyCollection<string> DegradedExistentials => _degradedExistentials;

    /// <summary>
    /// Records that a protocol existential degraded to <c>object</c>. Returns true if this was the
    /// first sighting of <paramref name="swiftExistentialType"/> (so callers can dedup work), false
    /// for a repeat or a blank value.
    /// </summary>
    public bool TryRecordExistentialDegradation(string swiftExistentialType)
    {
        if (string.IsNullOrEmpty(swiftExistentialType))
            return false;
        return _degradedExistentials.Add(swiftExistentialType);
    }

    // ============ Degraded Reverse-Dispatch Receivers (suppressed-proxy B3) ============

    private readonly HashSet<string> _degradedReverseDispatchReceivers = new(StringComparer.Ordinal);

    /// <summary>
    /// Member descriptors (e.g. <c>Foo.bar setter</c>, <c>Foo.consume(value: any P)</c>) of EveryProtocol
    /// reverse-dispatch receivers whose existential payload referenced a protocol proxy that was
    /// suppressed at generation (its EveryProtocol conformance was not emitted). The receiver kept its
    /// <c>[UnmanagedCallersOnly]</c> symbol + signature but its body degraded to a fail-fast stub, since
    /// no C#-side proxy exists to marshal the value across the boundary. Recorded once per member;
    /// <see cref="EmissionReportEmitter.Emit"/> turns the set into one SWIFTBIND061 warning each so the
    /// degradation is visible rather than only manifesting as a runtime fail-fast.
    /// </summary>
    public IReadOnlyCollection<string> DegradedReverseDispatchReceivers => _degradedReverseDispatchReceivers;

    /// <summary>
    /// Records that a reverse-dispatch receiver degraded to a suppressed-proxy fail-fast stub. Returns
    /// true on the first sighting of <paramref name="memberDescriptor"/>, false for a repeat or blank.
    /// </summary>
    public bool TryRecordDegradedReverseDispatchReceiver(string memberDescriptor)
    {
        if (string.IsNullOrEmpty(memberDescriptor))
            return false;
        return _degradedReverseDispatchReceivers.Add(memberDescriptor);
    }

    // ==================== Native Thunks ====================

    private readonly System.Text.StringBuilder _assemblyBuilder = new();
    private readonly System.Text.StringBuilder _x64AssemblyBuilder = new();

    /// <summary>Accumulated ARM64 assembly thunk code for this module.</summary>
    public System.Text.StringBuilder AssemblyBuilder => _assemblyBuilder;

    /// <summary>Whether any ARM64 thunk assembly has been emitted.</summary>
    public bool HasThunkAssembly => _assemblyBuilder.Length > 0;

    /// <summary>
    /// Accumulated x86_64 (SysV) assembly thunk code for this module. May be a strict subset of
    /// the ARM64 thunks: signatures whose arguments spill past the SysV register files are bridged
    /// on ARM64 but fall back to an @_cdecl wrapper on x86_64.
    /// </summary>
    public System.Text.StringBuilder X64AssemblyBuilder => _x64AssemblyBuilder;

    /// <summary>Whether any x86_64 thunk assembly has been emitted.</summary>
    public bool HasX64ThunkAssembly => _x64AssemblyBuilder.Length > 0;

    // ==================== Protocol Conformance Decisions ====================

    private readonly Dictionary<string, ProtocolConformanceDecision> _conformanceDecisions = new();
    private readonly HashSet<string> _setVtableEmitted = new();
    private bool _everyProtocolCarrierEmitted;

    /// <summary>
    /// Records that the synthetic <c>EveryProtocol</c> carrier class and its
    /// <c>SBW_CreateEveryProtocol</c> / <c>SBW_GetMetadata_EveryProtocol</c> /
    /// <c>SBW_SetEveryProtocolDeinitCallback</c> factory <c>@_cdecl</c> symbols were emitted into the
    /// wrapper module (i.e. <c>EveryProtocolEmitter.EmitEveryProtocolClass</c> ran). A module whose
    /// suitable-protocol set is empty never emits the carrier, yet a FULL (reverse-dispatch) proxy
    /// calls those factories — so emitting one there would reference undefined wrapper symbols (the
    /// dangling-entry-point defect the wrapper-symbol integrity gate catches). Read by
    /// <see cref="ProtocolProxyEmissionPolicy.Decide"/> to suppress a full proxy when the carrier is
    /// absent. Monotone: once the carrier is emitted this stays true for the rest of the module build.
    /// </summary>
    public void MarkEveryProtocolCarrierEmitted() => _everyProtocolCarrierEmitted = true;

    /// <summary>
    /// Returns true once the <c>EveryProtocol</c> carrier class was emitted for this module. See
    /// <see cref="MarkEveryProtocolCarrierEmitted"/>. A full proxy calling the carrier factory is only
    /// valid when this is true; when false, such a proxy must be suppressed (a read-only proxy, which
    /// reads <c>any P</c> through the existential's own witness table, needs no carrier).
    /// </summary>
    public bool WasEveryProtocolCarrierEmitted => _everyProtocolCarrierEmitted;

    /// <summary>
    /// Records whether a protocol conformance was emitted or skipped. <paramref name="protocolKey"/>
    /// is the <see cref="SwiftTypeName.ModuleQualifiedName"/> (not the simple name), so a dependency
    /// protocol sharing a simple name with a local one records its own decision without colliding.
    /// All readers (<see cref="WasConformanceEmitted"/>, <see cref="GetConformanceSkipReason"/>,
    /// and the ProtocolProxyEmitter / WitnessDispatchEmitter / ProtocolHandler gates) must key identically.
    /// </summary>
    public void RecordConformanceDecision(string protocolKey, bool emitted, string? skipReason)
    {
        _conformanceDecisions[protocolKey] = new ProtocolConformanceDecision(emitted, skipReason);
    }

    /// <summary>Returns true if the given protocol's conformance was emitted (not skipped). <paramref name="protocolKey"/> must be the module-qualified name used by <see cref="RecordConformanceDecision"/>.</summary>
    public bool WasConformanceEmitted(string protocolKey)
    {
        return _conformanceDecisions.TryGetValue(protocolKey, out var decision) && decision.Emitted;
    }

    /// <summary>Returns the skip reason for a protocol whose conformance was not emitted, or null if emitted/unknown. <paramref name="protocolKey"/> must be the module-qualified name used by <see cref="RecordConformanceDecision"/>.</summary>
    public string? GetConformanceSkipReason(string protocolKey)
    {
        return _conformanceDecisions.TryGetValue(protocolKey, out var decision) && !decision.Emitted
            ? decision.SkipReason : null;
    }

    /// <summary>Returns all recorded conformance decisions.</summary>
    public IReadOnlyDictionary<string, ProtocolConformanceDecision> ConformanceDecisions => _conformanceDecisions;

    /// <summary>
    /// Records that a <c>Set&lt;Protocol&gt;_vtable</c> Swift trampoline was emitted into the
    /// wrapper module for the given protocol. Consumed by <see cref="ProtocolProxyEmitter"/>
    /// to switch the C# proxy's <c>InitializeVtable()</c> body to a no-op when the wrapper
    /// did NOT emit the trampoline — the proxy class itself is still emitted (existential
    /// factories reference it by name for read-only Swift→C# wrap), but the static ctor must
    /// not call <c>NativeMethods.Set&lt;Protocol&gt;_vtable</c>, which would throw
    /// <see cref="EntryPointNotFoundException"/> at first proxy use. See
    /// Without this signal, the proxy emitter assumes every protocol got a vtable setter and
    /// produces <see cref="EntryPointNotFoundException"/>-throwing static constructors.
    /// Must be called from the
    /// <see cref="EveryProtocolEmitter"/> at the same point that emits the Swift function.
    /// <paramref name="protocolKey"/> is the <see cref="SwiftTypeName.ModuleQualifiedName"/> (not the
    /// simple name) so a same-simple-name cross-module protocol cannot mis-gate this proxy; the read
    /// side (<see cref="WasSetVtableEmitted"/>) keys identically.
    /// </summary>
    public void MarkSetVtableEmitted(string protocolKey)
    {
        _setVtableEmitted.Add(protocolKey);
    }

    /// <summary>
    /// Returns true when the wrapper module emitted a <c>Set&lt;Protocol&gt;_vtable</c> Swift
    /// trampoline for this protocol. Proxy emission checks this to decide whether
    /// <c>InitializeVtable()</c> calls <c>NativeMethods.Set&lt;Protocol&gt;_vtable</c> (true) or
    /// short-circuits to a no-op (false).
    ///
    /// <b>Caller contract:</b> the "proxy class still emitted, body just changes" framing
    /// only holds <i>after</i> the caller has separately confirmed conformance emission via
    /// <see cref="WasConformanceEmitted"/> / the equivalent ProtocolHandler path. Conformances
    /// that were never emitted (Self requirement, noncopyable param/return, static method/
    /// property requirements, etc.) are suppressed at <c>ProtocolHandler</c>'s
    /// <c>WasConformanceEmitted</c> check before the proxy emitter even runs, and references
    /// to those proxy names are co-gated by the emit-time proxy-reference gate.
    /// This signal partitions the <i>remaining</i> protocols (conformance emitted, proxy
    /// reachable) into "implementable conformance, real InitializeVtable body" (true) versus
    /// "marker / composition shape, no-op InitializeVtable" (false). The C#-impl→Swift
    /// callback path is what fails when the trampoline is absent; the Swift→C# read-only
    /// wrap path goes through the existential's witness table and works either way.
    /// </summary>
    public bool WasSetVtableEmitted(string protocolKey)
    {
        return _setVtableEmitted.Contains(protocolKey);
    }

    private readonly HashSet<string> _witnessTableGetterEmitted = new();

    /// <summary>
    /// Records that <see cref="EveryProtocolEmitter"/> emitted the
    /// <c>Get_EveryProtocol_{Protocol}_WitnessTable</c> Swift getter into the wrapper module
    /// for the given protocol. This is the authoritative "did the wrapper actually export the
    /// getter symbol?" signal — distinct from <see cref="WasSetVtableEmitted"/> (a
    /// marker/composition conformance can emit the getter without a vtable setter) and from
    /// <see cref="WasConformanceEmitted"/> (cross-module conformances are emitted but their
    /// getter lives in the defining module's wrapper, not this one). Consumed by
    /// <see cref="ProtocolProxyEmitter"/> to gate the matching C# getter P/Invoke and the
    /// <c>GetWitnessTableFromSwift()</c> body: when the getter was not exported, the proxy must
    /// not declare or call it (that would surface <see cref="EntryPointNotFoundException"/> on the
    /// C#→Swift CALLBACK path); it throws <see cref="NotSupportedException"/> instead. Must be
    /// called at the same point that emits the Swift getter. <paramref name="protocolKey"/> is the
    /// <see cref="SwiftTypeName.ModuleQualifiedName"/>, not the simple name, so a dependency
    /// protocol that shares a simple name with a local one cannot collide in the marker set and
    /// mis-gate the cross-module proxy. The read side (<see cref="WasWitnessTableGetterEmitted"/>)
    /// must key identically.
    /// </summary>
    public void MarkWitnessTableGetterEmitted(string protocolKey) => _witnessTableGetterEmitted.Add(protocolKey);

    /// <summary>
    /// Returns true when the wrapper module exported the
    /// <c>Get_EveryProtocol_{Protocol}_WitnessTable</c> getter for this protocol. False for
    /// read-only (class-superclass-skipped) and cross-module proxies whose getter symbol is not
    /// present in this wrapper — the C# proxy then suppresses the getter P/Invoke and fails the
    /// CALLBACK direction clean. <paramref name="protocolKey"/> must be the module-qualified name
    /// used by <see cref="MarkWitnessTableGetterEmitted"/>.
    /// </summary>
    public bool WasWitnessTableGetterEmitted(string protocolKey) => _witnessTableGetterEmitted.Contains(protocolKey);

    private readonly HashSet<string> _objCBaseProtocols = new();

    /// <summary>
    /// Records that the EveryProtocol conformance for the given protocol was routed
    /// through the NSObject-rooted <c>EveryObjCProtocol</c> helper class instead of
    /// the plain Swift <c>EveryProtocol</c>. Set by <see cref="EveryProtocolEmitter"/>
    /// for @objc protocols that inherit only <c>NSObjectProtocol</c>. Read by
    /// <see cref="ProtocolProxyEmitter"/> so the C# proxy's static ctor calls the
    /// matching <c>SBW_CreateEveryObjCProtocol</c> / <c>SBW_GetMetadata_EveryObjCProtocol</c>
    /// / <c>SBW_SetEveryObjCProtocolDeinitCallback</c> factories instead of the
    /// EveryProtocol equivalents. <paramref name="protocolKey"/> is the module-qualified name
    /// (matching the witness-getter marker); <see cref="UsesObjCBase"/> keys identically.
    /// </summary>
    public void MarkObjCBase(string protocolKey) => _objCBaseProtocols.Add(protocolKey);

    /// <summary>
    /// Returns true when the given protocol's EveryProtocol conformance was emitted
    /// on the NSObject-rooted <c>EveryObjCProtocol</c> helper class. <paramref name="protocolKey"/>
    /// must be the module-qualified name used by <see cref="MarkObjCBase"/>.
    /// </summary>
    public bool UsesObjCBase(string protocolKey) => _objCBaseProtocols.Contains(protocolKey);

    private readonly HashSet<string> _entityBaseProtocols = new();

    /// <summary>
    /// Records that the EveryProtocol conformance for the given protocol was routed
    /// through the RealityFoundation.Entity-rooted <c>EveryEntityProtocol</c> helper
    /// class instead of the plain Swift <c>EveryProtocol</c>. Set by
    /// <see cref="EveryProtocolEmitter"/> for protocols whose only class-superclass
    /// requirement is <c>Entity</c> (Failure B / RealityKit gesture .Entity getters
    /// and HasAnchoring). Read by <see cref="ProtocolProxyEmitter"/> so the C# proxy's
    /// static ctor and existential factory call the matching <c>SBW_*EveryEntityProtocol*</c>
    /// P/Invokes instead of the EveryProtocol equivalents — otherwise the existential
    /// container's payload would be a plain Swift class and the Entity subclass identity
    /// would not satisfy the class-superclass requirement. <paramref name="protocolKey"/> is the
    /// module-qualified name (matching the witness-getter marker and the pre-scan Mark site);
    /// <see cref="UsesEntityBase"/> keys identically.
    /// </summary>
    public void MarkEntityBase(string protocolKey) => _entityBaseProtocols.Add(protocolKey);

    /// <summary>
    /// Returns true when the given protocol's EveryProtocol conformance was emitted
    /// on the Entity-rooted <c>EveryEntityProtocol</c> helper class. <paramref name="protocolKey"/>
    /// must be the module-qualified name used by <see cref="MarkEntityBase"/>.
    /// </summary>
    public bool UsesEntityBase(string protocolKey) => _entityBaseProtocols.Contains(protocolKey);

    /// <summary>
    /// True when at least one protocol in the current wrapper module was routed
    /// through <c>EveryEntityProtocol</c>. Drives conditional emission of the
    /// <c>EveryEntityProtocol</c> Swift class + its four @_cdecl wrappers from
    /// <c>EveryProtocolEmitter.EmitEveryProtocolClass</c>. The class is emitted
    /// only when needed because <c>Entity</c> lives in RealityFoundation and is
    /// not a universal dependency the way <c>NSObject</c> is; emitting it in a
    /// wrapper that does not import RealityFoundation would fail to compile.
    /// </summary>
    public bool AnyEntityBaseUsed => _entityBaseProtocols.Count > 0;

    private readonly HashSet<string> _readOnlyProxyProtocols = new();

    /// <summary>
    /// Records that the given protocol gets a <i>read-only</i> (Swift-vended-only) proxy:
    /// a superclass-constrained protocol (e.g. <c>protocol EntityGestureRecognizer :
    /// UIGestureRecognizer</c>) that is NOT Entity-rooted, so the synthetic
    /// <c>EveryProtocol</c> / <c>EveryEntityProtocol</c> helper classes cannot subclass
    /// the required class and no EveryProtocol conformance is emitted. The C# proxy is
    /// still emitted so Swift-vended <c>any P</c> returns (and <c>[any P]</c> array
    /// elements) can be wrapped and dispatched through the existential's own witness
    /// table; only the C#→Swift implementation direction is unavailable. Set by
    /// <see cref="EveryProtocolEmitter"/> / ModuleHandler before any proxy emission, so
    /// the suppression gates in <see cref="WitnessDispatchEmitter"/> and
    /// <c>ProtocolHandler</c> can distinguish "no proxy at all" from "Swift-vended proxy,
    /// no C# implementation auto-wrap". Such protocols are NOT recorded as suppressed
    /// proxies — their existential-return projection lambdas stay intact.
    /// </summary>
    public void MarkReadOnlyProxy(string protocolName) => _readOnlyProxyProtocols.Add(protocolName);

    /// <summary>
    /// Returns true when the given protocol gets a read-only (Swift-vended-only) proxy
    /// rather than a full EveryProtocol-backed proxy. See <see cref="MarkReadOnlyProxy"/>.
    /// </summary>
    public bool IsReadOnlyProxy(string protocolName) => _readOnlyProxyProtocols.Contains(protocolName);

    // ==================== Escaping-Closure Context Owner Token ====================

    /// <summary>
    /// Whether the per-module Swift helpers that wrap an escaping closure's GCHandle
    /// pointer in an <c>_SBClosureCtx</c> box (resolved via <c>dlsym</c> from the
    /// already-loaded SwiftBindingsRuntime native framework) have been emitted into
    /// the wrapper source. Each wrapper module emits the dlsym lookup + box-factory
    /// helpers exactly once; per-closure adapter code refers to the helper by a
    /// fixed name.
    /// </summary>
    public bool ClosureContextHelpersEmitted
    {
        get => _closureContextHelpersEmitted;
        set
        {
            MarkSharedSwiftArtifact(value && !_closureContextHelpersEmitted);
            _closureContextHelpersEmitted = value;
        }
    }

    private bool _closureContextHelpersEmitted;

    /// <summary>
    /// Set by <c>ProtocolExtensionEmitter.EmitClosureSwiftWrapper</c> when the buffered
    /// protocol-extension wrapper lines reference <c>_sbWrapClosureContext</c>. Read by
    /// <c>EmitSwiftWrappers</c> before flushing the buffer, to ensure the helper is
    /// emitted into the wrapper Swift source first. Without this flag the helper would
    /// be missing for modules whose only escaping closure user is a protocol extension
    /// (no MCB / NCB site fired in the same module).
    /// </summary>
    public bool ProtocolExtUsesClosureContextHelper { get; set; }

    // ==================== Concrete Protocol Specialization ====================

    /// <summary>
    /// Engine for discovering protocol conformers and specializing methods with
    /// protocol-constrained generic parameters. Set once per module.
    /// </summary>
    public ConcreteSpecializationEngine? SpecializationEngine { get; set; }

    /// <summary>
    /// The per-module marshalling context: the constructed-once, fully-configured handler
    /// instances every marshalling environment shares. Set once per module immediately after
    /// <see cref="SpecializationEngine"/>. When present, <c>MethodEnvironment</c>/<c>PropertyEnvironment</c>
    /// delegate their handler properties to it instead of newing up a per-decl handler quintet,
    /// so the projection path and the env path can never diverge on engine/module configuration.
    /// </summary>
    public MarshalingContext? Marshaling { get; set; }
}

/// <summary>
/// Info for a single C# extension class for a foreign type.
/// Moved from ForeignTypeExtensionEmitter to share via ModuleEmissionContext.
/// </summary>
public class ForeignExtensionClassInfo
{
    public string ForeignTypeQualifiedName { get; init; } = "";
    public string ModuleName { get; init; } = "";
    public List<ForeignExtensionMemberInfo> Members { get; } = new();
}

/// <summary>
/// Info for a single extension member (method or property getter/setter).
/// Moved from ForeignTypeExtensionEmitter to share via ModuleEmissionContext.
/// </summary>
public class ForeignExtensionMemberInfo
{
    public required string SymbolName { get; init; }
    public required string CSharpMethodName { get; init; }
    public required ProtocolExtensionMethodDecl ExtMethod { get; init; }
    public required List<(string label, TypeSpec typeSpec, string swiftType, bool hasDefault)> Parameters { get; init; }
    public TypeSpec? ReturnTypeSpec { get; init; }
    public required string ReturnTypeName { get; init; }
    public required ExtensionMarshallingHelper.ReturnKind ReturnCategory { get; init; }
    public bool IsPropertyGetter { get; init; }
    public bool IsPropertySetter { get; init; }
}

/// <summary>
/// Records whether an EveryProtocol conformance was emitted or skipped, and why.
/// </summary>
public readonly record struct ProtocolConformanceDecision(bool Emitted, string? SkipReason);
