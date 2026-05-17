// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Emits C# proxy classes for Swift protocols.
/// The proxy pattern allows C# code to implement Swift protocols by:
/// 1. Wrapping either a C# implementation or a Swift existential container
/// 2. Providing a vtable of function pointers that Swift can call back into
/// 3. Managing the EveryProtocol instance and protocol witness table
/// </summary>
public partial class ProtocolProxyEmitter
{
    private readonly ITypeDatabase _typeDatabase;
    private readonly ILogger _logger;
    private readonly string _moduleName;
    private readonly ModuleEmissionContext _emissionContext;
    private static readonly TypeProjectionFactory s_projectionFactory = new();
    private HashSet<string> _skippedMethodKeys = new HashSet<string>();
    private HashSet<string> _skippedPropertyNames = new HashSet<string>();
    private HashSet<int> _skippedSubscriptIndices = new HashSet<int>();
    private HashSet<string> _closureSkippedMethodKeys = new HashSet<string>();
    private HashSet<string> _closureSkippedPropertyNames = new HashSet<string>();
    private HashSet<string> _staticAbstractPropertyNames = new HashSet<string>();
    private HashSet<string> _staticAbstractMethodKeys = new HashSet<string>();

    /// <summary>
    /// True when EveryProtocolEmitter emitted a <c>SetXxx_vtable</c> Swift trampoline for the
    /// protocol currently being emitted (or when running outside a ModuleEmissionContext, e.g.,
    /// in unit tests). When false, <see cref="EmitStaticConstructor"/> emits a no-op
    /// <c>InitializeVtable</c> so the static ctor doesn't throw EntryPointNotFoundException
    /// at first proxy use.
    /// </summary>
    private bool _setVtableEmitted = true;

    /// <summary>
    /// True when the corresponding EveryProtocol conformance was emitted on the
    /// NSObject-rooted <c>EveryObjCProtocol</c> helper class (S-2 NSObjectProtocol-only
    /// path). The proxy's static ctor and instance ctor must then call the matching
    /// <c>SBW_CreateEveryObjCProtocol</c> / <c>SBW_GetMetadata_EveryObjCProtocol</c> /
    /// <c>SBW_SetEveryObjCProtocolDeinitCallback</c> P/Invokes instead of the
    /// EveryProtocol equivalents — otherwise the existential container's payload would
    /// reference an EveryProtocol instance and Swift's NSObjectProtocol witness call
    /// (isEqual: / hash / description) would land on a non-NSObject class and trap.
    /// </summary>
    private bool _useObjCBase;

    /// <summary>Name of the C# Swift-side factory P/Invoke used to allocate the helper instance.</summary>
    private string CreateHelperMethodName => _useObjCBase ? "CreateEveryObjCProtocol" : "CreateEveryProtocol";

    /// <summary>Symbol name of the Swift @_cdecl factory for the helper instance.</summary>
    private string CreateHelperEntryPoint => _useObjCBase ? "SBW_CreateEveryObjCProtocol" : "SBW_CreateEveryProtocol";

    /// <summary>C# method name for the metadata accessor of the helper class.</summary>
    private string GetMetadataMethodName => _useObjCBase ? "GetEveryObjCProtocolMetadata" : "GetEveryProtocolMetadata";

    /// <summary>Symbol name of the metadata accessor for the helper class.</summary>
    private string GetMetadataEntryPoint => _useObjCBase ? "SBW_GetMetadata_EveryObjCProtocol" : "SBW_GetMetadata_EveryProtocol";

    /// <summary>C# method name for the deinit-callback setter of the helper class.</summary>
    private string SetDeinitCallbackMethodName => _useObjCBase ? "SetEveryObjCProtocolDeinitCallback" : "SetEveryProtocolDeinitCallback";

    /// <summary>Symbol name of the deinit-callback setter for the helper class.</summary>
    private string SetDeinitCallbackEntryPoint => _useObjCBase ? "SBW_SetEveryObjCProtocolDeinitCallback" : "SBW_SetEveryProtocolDeinitCallback";

    public ProtocolProxyEmitter(ITypeDatabase typeDatabase, ILogger logger, string moduleName, ModuleEmissionContext? ctx = null)
    {
        _typeDatabase = typeDatabase;
        _logger = logger;
        _moduleName = moduleName;
        _emissionContext = ctx ?? ModuleEmissionContext.Default;
    }

    /// <summary>
    /// Emits the complete proxy class for a protocol.
    /// </summary>
    public void EmitProxyClass(CSharpWriter writer, ProtocolDecl protocolDecl,
        HashSet<string>? skippedMethodKeys = null,
        HashSet<string>? skippedPropertyNames = null,
        HashSet<int>? skippedSubscriptIndices = null,
        HashSet<string>? closureSkippedMethodKeys = null,
        HashSet<string>? closureSkippedPropertyNames = null,
        HashSet<string>? staticAbstractPropertyNames = null,
        HashSet<string>? staticAbstractMethodKeys = null)
    {
        _skippedMethodKeys = skippedMethodKeys ?? new HashSet<string>();
        _skippedPropertyNames = skippedPropertyNames ?? new HashSet<string>();
        _skippedSubscriptIndices = skippedSubscriptIndices ?? new HashSet<int>();
        _closureSkippedMethodKeys = closureSkippedMethodKeys ?? new HashSet<string>();
        _closureSkippedPropertyNames = closureSkippedPropertyNames ?? new HashSet<string>();
        _staticAbstractPropertyNames = staticAbstractPropertyNames ?? new HashSet<string>();
        _staticAbstractMethodKeys = staticAbstractMethodKeys ?? new HashSet<string>();

        // Skip protocols with Self requirements - these require special handling
        // that can't be done with simple generic parameters
        if (protocolDecl.HasSelfRequirement)
        {
            _logger.LogDebug($"Skipping proxy class for {protocolDecl.Name}: has Self requirement");
            return;
        }

        // Skip protocols with associated types (would create generic proxy classes).
        // C# doesn't allow [UnmanagedCallersOnly] or [DllImport] in generic types,
        // and nested classes inside generic types inherit this restriction.
        // Future approaches: Reflection.Emit at runtime, non-generic base class with
        // object-typed dispatch, or source-generated specializations per concrete type.
        if (protocolDecl.AssociatedTypes.Count > 0)
        {
            _logger.LogWarning($"Skipping proxy class for {protocolDecl.Name}: protocols with associated types are not yet supported for proxy generation (would require [UnmanagedCallersOnly] in generic type)");
            return;
        }

        // Determine whether EveryProtocolEmitter emitted a SetXxx_vtable Swift trampoline for
        // this protocol. The signal partitions the protocols that REACH this emitter at all
        // (i.e. ones whose EveryProtocol conformance WAS recorded as emitted — ProtocolHandler
        // suppresses the proxy entirely for the conformance-skipped paths) into two groups:
        //
        //   1. Implementable conformance (hasImplementableMembers=true): the wrapper emits a
        //      real SetXxx_vtable cdecl trampoline, MarkSetVtableEmitted is called, the proxy's
        //      static ctor calls into it normally. Default path.
        //   2. Marker / composition protocol (hasImplementableMembers=false but NOT skipped for
        //      static-property requirements): the wrapper emits an empty `extension EveryProtocol:
        //      Foo {}` and records the conformance, but does NOT emit a SetXxx_vtable trampoline.
        //      Calling NativeMethods.SetXxx_vtable from the static constructor would throw
        //      EntryPointNotFoundException at first proxy use. The proxy class itself MUST still
        //      be emitted, because other emitters reference it by name (existential factories
        //      wrap Swift-side containers as `new XxxProxy(handle)` for read-only consumption);
        //      skipping the class produces CS0246 across the binding.
        //
        // For (2), routing the "no vtable trampoline" signal into EmitStaticConstructor emits a
        // no-op InitializeVtable() that only sets _vtableInitialized = true. The Swift→C# wrap
        // path (the one that actually fires for marker conformances) does not depend on the
        // local vtable — instance method dispatch goes through _swiftContainer.WitnessTable,
        // populated by the existential ctor. The C#-impl→Swift path won't function (no callbacks
        // registered) but the read-only path compiles AND runs correctly. See
        // bug-0.10.0-proxy-vtable-setters-not-exported.md.
        //
        // (Protocols whose conformance was SKIPPED — Self requirement, noncopyable
        // member, static method/property requirement, etc. — do not reach this emitter:
        // ProtocolHandler suppresses the proxy at its EmissionContext.WasConformanceEmitted
        // check before EmitProxyClass is called. Existential factory references to those proxy
        // names are co-gated by CSharpWrapperCoGater.ProcessSuppressedProxyReferences.)
        //
        // The unit-test path (no ModuleEmissionContext supplied) keeps the legacy behaviour
        // — _setVtableEmitted is treated as true so existing tests stay green.
        _setVtableEmitted = _emissionContext == ModuleEmissionContext.Default
            || _emissionContext.WasSetVtableEmitted(protocolDecl.Name);

        if (!_setVtableEmitted)
        {
            _logger.LogDebug($"Emitting proxy class for {protocolDecl.Name} with no-op InitializeVtable: EveryProtocolEmitter did not emit Set{protocolDecl.Name}_vtable; only Swift→C# wrap path will function.");
        }

        // S-2: pick the NSObject-rooted helper symbols when EveryProtocolEmitter routed
        // this protocol through EveryObjCProtocol. The proxy P/Invoke names and entry
        // points all switch to the matching SBW_*EveryObjCProtocol* symbols.
        _useObjCBase = _emissionContext != ModuleEmissionContext.Default
            && _emissionContext.UsesObjCBase(protocolDecl.Name);

        // Inherited protocol requirements are now handled: the proxy emits implementations
        // for inherited interface members (see EmitInheritedInterfaceImplementations).
        // No skip needed for protocols with only inherited requirements.

        var interfaceName = NameProvider.GetInterfaceName(protocolDecl.Name, moduleName: protocolDecl.ModuleDecl?.Name ?? "");
        var proxyClassName = GetProxyClassName(protocolDecl);
        var proxyClassNameWithGenerics = GetProxyClassNameWithGenerics(protocolDecl);
        var interfaceNameWithGenerics = GetInterfaceNameWithGenerics(protocolDecl);
        var constraints = GetProxyClassConstraints(protocolDecl);

        // Suppress SB0003/SB0004 warnings from the proxy's own references to the interface.
        // SB0004 marks the interface as obsolete, and the proxy implements it.
        // SB0003 marks non-dispatchable members, which the proxy itself declares.
        writer.WriteLine("#pragma warning disable SB0003, SB0004");
        writer.WriteLine($"/// <summary>");
        writer.WriteLine($"/// Proxy class that enables C# implementations of the {protocolDecl.Name} protocol.");
        writer.WriteLine($"/// Can wrap either a C# implementation or receive Swift existential containers.");
        writer.WriteLine($"/// </summary>");
        // Inherit @available platform-gating attributes from the source protocol so
        // the proxy class type itself carries [SupportedOSPlatform]/[UnsupportedOSPlatform]
        // rather than relying solely on call-site CA1416 suppression. The interface
        // declaration emits these in ProtocolHandler with emitObsolete:true; the proxy
        // class uses emitObsolete:false to avoid duplicating the SB0004 obsolete tag.
        AvailabilityAttributeEmitter.EmitAvailabilityAttributes(writer, protocolDecl, emitObsolete: false);
        writer.WriteLine("[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
        writer.WriteLine($"public unsafe partial class {proxyClassNameWithGenerics} : {interfaceNameWithGenerics}, ISwiftObject, IDisposable, Swift.Runtime.ISwiftExistentialConvertible<ExistentialContainer1>{constraints}");
        writer.WriteLine("{");
        writer.Indent++;

        // Emit vtable structs
        EmitSwiftVtableStruct(writer, protocolDecl);
        EmitLocalVtableStruct(writer, protocolDecl);

        // Emit static fields
        EmitStaticFields(writer, protocolDecl);

        // Emit instance fields
        EmitInstanceFields(writer, protocolDecl, interfaceNameWithGenerics);

        // Emit static constructor (registers vtable with Swift)
        EmitStaticConstructor(writer, protocolDecl);

        // Emit per-closure-param DllImport thunks + invoker classes BEFORE receivers —
        // receivers reference the invoker class names when wrapping the
        // (fnPtr, ctx) IntPtr pair into a managed delegate (e.g. Action).
        EmitProtocolClosureInvokeThunkHelpers(writer, protocolDecl);

        // Emit receiver methods (UnmanagedCallersOnly callbacks)
        EmitReceiverMethods(writer, protocolDecl, interfaceNameWithGenerics);

        // Emit constructors
        EmitConstructors(writer, protocolDecl, interfaceNameWithGenerics);

        // Emit interface implementation (with witness dispatch for blittable members)
        var dispatchEmitter = new WitnessDispatchEmitter(_typeDatabase, _logger, _moduleName, _emissionContext);
        EmitInterfaceImplementation(writer, protocolDecl, interfaceNameWithGenerics, dispatchEmitter);

        // Emit ISwiftObject implementation
        EmitISwiftObjectImplementation(writer, protocolDecl, dispatchEmitter);

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine("#pragma warning restore SB0003, SB0004");
        writer.WriteLine();
    }
}
