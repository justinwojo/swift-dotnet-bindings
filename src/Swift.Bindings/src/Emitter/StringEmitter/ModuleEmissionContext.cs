// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

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

    // ==================== Generic Closure Bridge ====================

    private readonly HashSet<string> _genericClosureBridgeTypes = new();

    /// <summary>Whether the generic closure bridge CreateError helper has been emitted.</summary>
    public bool GenericClosureBridgeCreateErrorEmitted { get; set; }

    /// <summary>Checks if a generic closure bridge error P/Invoke has been emitted for a type.</summary>
    public bool HasGenericClosureBridgeErrorPInvoke(string typeKey) => _genericClosureBridgeTypes.Contains(typeKey);

    /// <summary>Marks a generic closure bridge error P/Invoke as emitted. Returns true if newly added.</summary>
    public bool TryAddGenericClosureBridgeErrorPInvoke(string typeKey) => _genericClosureBridgeTypes.Add(typeKey);

    // ==================== Protocol Proxy Sub-Namespace ====================

    private readonly List<string> _deferredProxyClasses = new();

    /// <summary>Accumulated proxy class source blocks for deferred emission in SwiftInterop sub-namespace.</summary>
    public IReadOnlyList<string> DeferredProxyClasses => _deferredProxyClasses;

    /// <summary>Adds a proxy class source block for deferred emission.</summary>
    public void AddDeferredProxyClass(string proxySource) => _deferredProxyClasses.Add(proxySource);

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

    // ==================== Destroy Wrapper ====================

    private readonly HashSet<string> _destroyWrapperSymbols = new();

    /// <summary>Checks if a destroy wrapper Swift symbol was already emitted for this type.</summary>
    public bool HasDestroyWrapperSymbol(string symbol) => _destroyWrapperSymbols.Contains(symbol);

    /// <summary>Adds a destroy wrapper symbol. Returns true if newly added.</summary>
    public bool TryAddDestroyWrapperSymbol(string symbol) => _destroyWrapperSymbols.Add(symbol);

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

    // ==================== Protocol Conformance Decisions ====================

    private readonly Dictionary<string, ProtocolConformanceDecision> _conformanceDecisions = new();

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

    /// <summary>Returns all recorded conformance decisions.</summary>
    public IReadOnlyDictionary<string, ProtocolConformanceDecision> ConformanceDecisions => _conformanceDecisions;
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
