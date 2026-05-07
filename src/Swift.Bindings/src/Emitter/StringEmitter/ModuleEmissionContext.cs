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
/// Resolves H3 (Static Mutable State With Manual Reset) from architectural-review-v2.md.
/// </summary>
public sealed class ModuleEmissionContext
{
    /// <summary>
    /// Default singleton for use in tests and backward-compatible code paths.
    /// Safe because unit tests disable xUnit parallelization via [Collection] attributes.
    /// </summary>
    public static ModuleEmissionContext Default { get; } = new();

    // ==================== Module / Type Name Collision ====================

    /// <summary>
    /// When the current module has a public type with the same name as the module itself
    /// (e.g. module "Reachability" containing class "Reachability"), Swift name lookup inside
    /// the wrapper file resolves the bare module name as the type, not the module. Any
    /// "<c>Reachability.X</c>" reference emitted into Swift wrapper source therefore means
    /// "the nested type X of class Reachability" and fails to compile when X is actually a
    /// module-level type. Set to the module name in that case; null otherwise.
    /// </summary>
    public string? ModuleNameForCollision { get; private set; }

    private HashSet<string>? _nestedTypesInCollidingClass;
    private Regex? _collisionPattern;

    /// <summary>
    /// Names of types nested inside the colliding class (e.g. <c>{"Level"}</c> for class
    /// <c>SwiftyBeaver</c>'s nested <c>Level</c> enum). When stripping the module prefix
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
        _collisionPattern = !string.IsNullOrEmpty(moduleNameForCollision)
            ? new Regex(@"\b" + Regex.Escape(moduleNameForCollision) + @"\.(\w+(?:\.\w+)*)", RegexOptions.Compiled)
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
    public string QualifyForWrapperSource(string moduleQualifiedName)
    {
        if (string.IsNullOrEmpty(moduleQualifiedName) || _collisionPattern == null)
            return moduleQualifiedName;

        return _collisionPattern.Replace(moduleQualifiedName, match =>
        {
            // First captured group is the type path after the module prefix.
            // Preserve qualification when the head segment names a type nested
            // inside the colliding class (e.g. SwiftyBeaver.Level).
            var firstComponent = match.Groups[1].Value;
            var dotIdx = firstComponent.IndexOf('.');
            var topLevelName = dotIdx >= 0 ? firstComponent.Substring(0, dotIdx) : firstComponent;
            if (_nestedTypesInCollidingClass?.Contains(topLevelName) == true)
                return match.Value;
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
    public bool TryAddProtocolExtSymbol(string symbol) => _protocolExtEmittedSymbols.Add(symbol);

    /// <summary>Adds a single Swift wrapper line for protocol extensions.</summary>
    public void AddProtocolExtWrapperLine(string line) => _protocolExtWrapperLines.Add(line);

    /// <summary>Adds multiple Swift wrapper lines for protocol extensions.</summary>
    public void AddProtocolExtWrapperLines(IEnumerable<string> lines) => _protocolExtWrapperLines.AddRange(lines);

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
    public bool TryAddForeignExtSymbol(string symbol) => _foreignExtEmittedSymbols.Add(symbol);

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

    // ==================== Utf8Slice ====================

    private readonly HashSet<string> _utf8SliceFreePInvokeTypes = new();

    /// <summary>Whether the SBW_Utf8Slice struct has been emitted for this module.</summary>
    public bool Utf8SliceStructEmitted { get; set; }

    /// <summary>Whether the SBW_Utf8Slice_Free function has been emitted for this module.</summary>
    public bool Utf8SliceFreeEmitted { get; set; }

    /// <summary>The current module name for Utf8Slice symbol generation.</summary>
    public string? Utf8SliceCurrentModuleName { get; set; }

    /// <summary>Checks if a Utf8Slice free P/Invoke has been emitted for a type.</summary>
    public bool HasUtf8SliceFreePInvoke(string typeKey) => _utf8SliceFreePInvokeTypes.Contains(typeKey);

    /// <summary>Marks a Utf8Slice free P/Invoke as emitted. Returns true if newly added.</summary>
    public bool TryAddUtf8SliceFreePInvoke(string typeKey) => _utf8SliceFreePInvokeTypes.Add(typeKey);

    // ==================== Cancellation Task ====================

    private readonly HashSet<string> _cancellationPInvokeTypes = new();

    /// <summary>Whether the cancellation infrastructure has been emitted for this module.</summary>
    public bool CancellationInfrastructureEmitted { get; set; }

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
    public bool ErrorDescInfrastructureEmitted { get; set; }

    /// <summary>The current module name for error description symbol generation.</summary>
    public string? ErrorDescCurrentModuleName { get; set; }

    /// <summary>Checks if an error description P/Invoke has been emitted for a type.</summary>
    public bool HasErrorDescPInvoke(string typeKey) => _errorDescPInvokeTypes.Contains(typeKey);

    /// <summary>Marks an error description P/Invoke as emitted. Returns true if newly added.</summary>
    public bool TryAddErrorDescPInvoke(string typeKey) => _errorDescPInvokeTypes.Add(typeKey);

    /// <summary>Marks a typed error extractor as emitted. Returns true if newly added.</summary>
    public bool TryAddTypedErrorExtractor(string swiftErrorType) => _errorDescExtractorsEmitted.Add(swiftErrorType);

    /// <summary>Checks if an extractor P/Invoke has been emitted for a key.</summary>
    public bool HasExtractorPInvoke(string key) => _errorDescExtractorPInvokeTypes.Contains(key);

    /// <summary>Marks an extractor P/Invoke as emitted. Returns true if newly added.</summary>
    public bool TryAddExtractorPInvoke(string key) => _errorDescExtractorPInvokeTypes.Add(key);

    // ==================== Error Type Registry (Phase 4 plain-throws bridge) ====================

    private readonly Dictionary<string, int> _errorTypeIds = new(StringComparer.Ordinal);
    private readonly List<string> _errorTypeOrder = new();

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
    public int RegisterErrorTypeId(string swiftModuleQualifiedName)
    {
        if (_errorTypeIds.TryGetValue(swiftModuleQualifiedName, out var existing))
            return existing;
        var newId = _errorTypeIds.Count + 1;
        _errorTypeIds[swiftModuleQualifiedName] = newId;
        _errorTypeOrder.Add(swiftModuleQualifiedName);
        return newId;
    }

    /// <summary>Tries to look up the registered id for a Swift error type.</summary>
    public bool TryGetErrorTypeId(string swiftModuleQualifiedName, out int id) =>
        _errorTypeIds.TryGetValue(swiftModuleQualifiedName, out id);

    // ==================== Generic Closure Bridge ====================

    private readonly HashSet<string> _genericClosureBridgeTypes = new();

    /// <summary>Whether the generic closure bridge CreateError helper has been emitted.</summary>
    public bool GenericClosureBridgeCreateErrorEmitted { get; set; }

    /// <summary>Checks if a generic closure bridge error P/Invoke has been emitted for a type.</summary>
    public bool HasGenericClosureBridgeErrorPInvoke(string typeKey) => _genericClosureBridgeTypes.Contains(typeKey);

    /// <summary>Marks a generic closure bridge error P/Invoke as emitted. Returns true if newly added.</summary>
    public bool TryAddGenericClosureBridgeErrorPInvoke(string typeKey) => _genericClosureBridgeTypes.Add(typeKey);

    // ==================== Async Closure Swift Wrapper ====================

    private readonly HashSet<string> _asyncClosureSwiftWrapperKeys = new();

    /// <summary>Whether the SwiftBindingsBridgeError error type stub has been emitted for the current Swift module.</summary>
    public bool AsyncClosureBridgeErrorEmitted { get; set; }

    /// <summary>Marks an async-closure box + resume-callback trio as emitted for a (module, T) pair. Returns true if newly added.</summary>
    public bool TryAddAsyncClosureSwiftWrapperKey(string key) => _asyncClosureSwiftWrapperKeys.Add(key);

    // ==================== NativeAOT Factory Registration ====================

    private readonly List<string> _emittedSwiftObjectTypes = new();

    private readonly Stack<string> _typeNestingStack = new();

    /// <summary>
    /// C# type names of non-generic ISwiftObject types emitted during this module.
    /// Nested types are fully qualified (e.g., "Codec.Encoding").
    /// Used by ModuleHandler to emit [ModuleInitializer] factory registration for NativeAOT.
    /// </summary>
    public IReadOnlyList<string> EmittedSwiftObjectTypes => _emittedSwiftObjectTypes;

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
    public void RecordSwiftObjectType(string csharpTypeName)
    {
        if (HasOpenGenericAncestor())
            return;
        var qualifiedName = GetQualifiedTypeName(csharpTypeName);
        _emittedSwiftObjectTypes.Add(qualifiedName);
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
    public void RecordConformance(string csharpTypeName, string protocolInterfaceName)
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

    // ==================== Constructor Wrapper ====================

    private readonly HashSet<string> _constructorWrapperSymbols = new();

    /// <summary>Checks if a constructor @_cdecl wrapper symbol was already emitted for this type.</summary>
    public bool HasConstructorWrapperSymbol(string symbol) => _constructorWrapperSymbols.Contains(symbol);

    /// <summary>Adds a constructor wrapper symbol. Returns true if newly added.</summary>
    public bool TryAddConstructorWrapperSymbol(string symbol) => _constructorWrapperSymbols.Add(symbol);

    // ==================== ObjC Override Property Wrapper ====================

    private readonly HashSet<string> _objcPropertyWrapperSymbols = new();

    /// <summary>Checks if an ObjC override property wrapper symbol was already emitted.</summary>
    public bool HasObjCPropertyWrapperSymbol(string symbol) => _objcPropertyWrapperSymbols.Contains(symbol);

    /// <summary>Adds an ObjC override property wrapper symbol. Returns true if newly added.</summary>
    public bool TryAddObjCPropertyWrapperSymbol(string symbol) => _objcPropertyWrapperSymbols.Add(symbol);

    // ==================== Property @_cdecl Wrapper ====================

    private readonly HashSet<string> _propertyWrapperSymbols = new();

    /// <summary>Adds a property @_cdecl wrapper symbol. Returns true if newly added (not a duplicate).</summary>
    public bool TryAddPropertyWrapperSymbol(string symbol) => _propertyWrapperSymbols.Add(symbol);

    // ==================== Method @_cdecl Wrapper ====================

    private readonly HashSet<string> _methodWrapperSymbols = new();

    /// <summary>Adds a method @_cdecl wrapper symbol. Returns true if newly added (not a duplicate).</summary>
    public bool TryAddMethodWrapperSymbol(string symbol) => _methodWrapperSymbols.Add(symbol);

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
    public bool TryAddMetadataWrapperSymbol(string symbol) => _metadataWrapperSymbols.Add(symbol);

    // ==================== Enum Handler RawRepresentable ====================

    private readonly HashSet<string> _enumRawRepSymbols = new();

    /// <summary>Checks if an enum RawRepresentable wrapper symbol was already emitted.</summary>
    public bool HasEnumRawRepWrapperSymbol(string symbol) => _enumRawRepSymbols.Contains(symbol);

    /// <summary>Adds an enum RawRepresentable wrapper symbol. Returns true if newly added.</summary>
    public bool TryAddEnumRawRepWrapperSymbol(string symbol) => _enumRawRepSymbols.Add(symbol);

    // ==================== Equality @_cdecl Wrapper ====================

    private readonly HashSet<string> _equalityWrapperSymbols = new();

    /// <summary>Adds an equality @_cdecl wrapper symbol. Returns true if newly added (not a duplicate).</summary>
    public bool TryAddEqualityWrapperSymbol(string symbol) => _equalityWrapperSymbols.Add(symbol);

    // ==================== Metadata Accessor Helper ====================

    private readonly HashSet<string> _metadataAccessorHelperSymbols = new();

    /// <summary>Adds a metadata accessor helper symbol. Returns true if newly added (not a duplicate).</summary>
    public bool TryAddMetadataAccessorHelper(string typeMangledName) => _metadataAccessorHelperSymbols.Add(typeMangledName);

    // ==================== Optional Tag Helper ====================

    private readonly HashSet<string> _optionalTagHelperSymbols = new();

    /// <summary>Adds an Optional tag helper @_cdecl symbol. Returns true if newly added (not a duplicate).</summary>
    public bool TryAddOptionalTagHelperSymbol(string symbol) => _optionalTagHelperSymbols.Add(symbol);

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

    // ==================== Native ARM64 Thunks ====================

    private readonly System.Text.StringBuilder _assemblyBuilder = new();

    /// <summary>Accumulated ARM64 assembly thunk code for this module.</summary>
    public System.Text.StringBuilder AssemblyBuilder => _assemblyBuilder;

    /// <summary>Whether any thunk assembly has been emitted.</summary>
    public bool HasThunkAssembly => _assemblyBuilder.Length > 0;

    // ==================== Protocol Conformance Decisions ====================

    private readonly Dictionary<string, ProtocolConformanceDecision> _conformanceDecisions = new();
    private readonly HashSet<string> _setVtableEmitted = new();

    /// <summary>Records whether a protocol conformance was emitted or skipped.</summary>
    public void RecordConformanceDecision(string protocolName, bool emitted, string? skipReason)
    {
        _conformanceDecisions[protocolName] = new ProtocolConformanceDecision(emitted, skipReason);
    }

    /// <summary>Returns true if the given protocol's conformance was emitted (not skipped).</summary>
    public bool WasConformanceEmitted(string protocolName)
    {
        return _conformanceDecisions.TryGetValue(protocolName, out var decision) && decision.Emitted;
    }

    /// <summary>Returns the skip reason for a protocol whose conformance was not emitted, or null if emitted/unknown.</summary>
    public string? GetConformanceSkipReason(string protocolName)
    {
        return _conformanceDecisions.TryGetValue(protocolName, out var decision) && !decision.Emitted
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
    /// <c>bug-0.10.0-proxy-vtable-setters-not-exported.md</c>. Must be called from the
    /// <see cref="EveryProtocolEmitter"/> at the same point that emits the Swift function.
    /// </summary>
    public void MarkSetVtableEmitted(string protocolName)
    {
        _setVtableEmitted.Add(protocolName);
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
    /// to those proxy names are co-gated by <c>CSharpWrapperCoGater.ProcessSuppressedProxyReferences</c>.
    /// This signal partitions the <i>remaining</i> protocols (conformance emitted, proxy
    /// reachable) into "implementable conformance, real InitializeVtable body" (true) versus
    /// "marker / composition shape, no-op InitializeVtable" (false). The C#-impl→Swift
    /// callback path is what fails when the trampoline is absent; the Swift→C# read-only
    /// wrap path goes through the existential's witness table and works either way.
    /// </summary>
    public bool WasSetVtableEmitted(string protocolName)
    {
        return _setVtableEmitted.Contains(protocolName);
    }

    // ==================== Escaping-Closure Context Owner Token ====================

    /// <summary>
    /// Whether the per-module Swift helpers that wrap an escaping closure's GCHandle
    /// pointer in an <c>_SBClosureCtx</c> box (resolved via <c>dlsym</c> from the
    /// already-loaded <c>libSwiftBindingsRuntime.dylib</c>) have been emitted into
    /// the wrapper source. Each wrapper module emits the dlsym lookup + box-factory
    /// helpers exactly once; per-closure adapter code refers to the helper by a
    /// fixed name. Bridges Bug 1 Cat 3 / Bug 3 Case 2.
    /// </summary>
    public bool ClosureContextHelpersEmitted { get; set; }

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
