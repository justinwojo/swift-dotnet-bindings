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
    /// True when EveryProtocolEmitter exported the <c>Get_EveryProtocol_{P}_WitnessTable</c> Swift
    /// getter for the protocol currently being emitted (or when running outside a
    /// ModuleEmissionContext, e.g. unit tests). When false, the proxy suppresses the matching
    /// C# getter P/Invoke and <c>GetWitnessTableFromSwift()</c> throws
    /// <see cref="NotSupportedException"/> instead of calling a symbol the wrapper never exported —
    /// the case for read-only (class-superclass-skipped) and cross-module proxies. Only the
    /// C#-implements-protocol (CALLBACK) direction reaches the getter; Swift-vended RETURN/ACCEPT
    /// dispatch through the existential's own witness table and are unaffected.
    /// </summary>
    private bool _witnessGetterEmitted = true;

    /// <summary>
    /// True when the corresponding EveryProtocol conformance was emitted on the
    /// NSObject-rooted <c>EveryObjCProtocol</c> helper class (NSObjectProtocol-only
    /// path). The proxy's static ctor and instance ctor must then call the matching
    /// <c>SBW_CreateEveryObjCProtocol</c> / <c>SBW_GetMetadata_EveryObjCProtocol</c> /
    /// <c>SBW_SetEveryObjCProtocolDeinitCallback</c> P/Invokes instead of the
    /// EveryProtocol equivalents — otherwise the existential container's payload would
    /// reference an EveryProtocol instance and Swift's NSObjectProtocol witness call
    /// (isEqual: / hash / description) would land on a non-NSObject class and trap.
    /// </summary>
    private bool _useObjCBase;

    /// <summary>
    /// True when the corresponding EveryProtocol conformance was emitted on the
    /// RealityFoundation.Entity-rooted <c>EveryEntityProtocol</c> helper class
    /// (Failure B path). Mutually exclusive with <see cref="_useObjCBase"/>: the
    /// routing gates in <see cref="EveryProtocolEmitter"/> classify each protocol
    /// into exactly one base. When true, the proxy's static ctor and existential
    /// factory call the matching <c>SBW_*EveryEntityProtocol*</c> P/Invokes so
    /// the payload satisfies the protocol's class-superclass requirement on
    /// Entity (e.g. HasAnchoring).
    /// </summary>
    private bool _useEntityBase;

    /// <summary>
    /// True when this protocol is emitted as a READ-ONLY (Swift-vended-only) proxy: it carries a
    /// class-superclass requirement that is NOT Entity-rooted, so neither EveryProtocol nor
    /// EveryEntityProtocol can synthesize a conformance for it (ModuleHandler marks these via
    /// <c>MarkReadOnlyProxy</c>). Such a proxy only WRAPS Swift-vended <c>any P</c> values; the
    /// C#-implements-protocol (synthesis) direction is unsupported. Critically, the module may
    /// emit NO EveryProtocol scaffolding at all (when it has zero suitable protocols), so the
    /// <c>SBW_GetMetadata_EveryProtocol</c> accessor does not exist. The proxy must therefore NOT
    /// emit the eager <c>s_everyProtocolMetadata</c> static field (its initializer P/Invokes that
    /// missing symbol and throws <see cref="TypeInitializationException"/> on first proxy use,
    /// even on the wrap path) and <c>GetTypeMetadata()</c> must fail clean instead of reading it —
    /// the wrap path never needs the helper metadata (it reads the existential's own metadata word).
    /// Keyed on the simple name to match <c>ModuleHandler.MarkReadOnlyProxy(p.Name)</c>.
    /// </summary>
    private bool _isReadOnlyProxy;

    /// <summary>Name of the C# Swift-side factory P/Invoke used to allocate the helper instance.</summary>
    private string CreateHelperMethodName => _useEntityBase
        ? "CreateEveryEntityProtocol"
        : (_useObjCBase ? "CreateEveryObjCProtocol" : "CreateEveryProtocol");

    /// <summary>Symbol name of the Swift @_cdecl factory for the helper instance.</summary>
    private string CreateHelperEntryPoint => _useEntityBase
        ? "SBW_CreateEveryEntityProtocol"
        : (_useObjCBase ? "SBW_CreateEveryObjCProtocol" : "SBW_CreateEveryProtocol");

    /// <summary>C# method name for the metadata accessor of the helper class.</summary>
    private string GetMetadataMethodName => _useEntityBase
        ? "GetEveryEntityProtocolMetadata"
        : (_useObjCBase ? "GetEveryObjCProtocolMetadata" : "GetEveryProtocolMetadata");

    /// <summary>Symbol name of the metadata accessor for the helper class.</summary>
    private string GetMetadataEntryPoint => _useEntityBase
        ? "SBW_GetMetadata_EveryEntityProtocol"
        : (_useObjCBase ? "SBW_GetMetadata_EveryObjCProtocol" : "SBW_GetMetadata_EveryProtocol");

    /// <summary>C# method name for the deinit-callback setter of the helper class.</summary>
    private string SetDeinitCallbackMethodName => _useEntityBase
        ? "SetEveryEntityProtocolDeinitCallback"
        : (_useObjCBase ? "SetEveryObjCProtocolDeinitCallback" : "SetEveryProtocolDeinitCallback");

    /// <summary>Symbol name of the deinit-callback setter for the helper class.</summary>
    private string SetDeinitCallbackEntryPoint => _useEntityBase
        ? "SBW_SetEveryEntityProtocolDeinitCallback"
        : (_useObjCBase ? "SBW_SetEveryObjCProtocolDeinitCallback" : "SBW_SetEveryProtocolDeinitCallback");

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

        // Class-bound protocol existentials marshal through the 16-byte ClassExistentialContainer1
        // carrier (see ExistentialHandler.IsClassBoundArity1Existential). SwiftArray<T> derives its
        // element stride from the Swift type metadata of T, so the module initializer must register
        // the shared class-existential value-witness metadata for ClassExistentialContainer1 — the
        // opaque ExistentialContainer1 metadata would over-read at 40 bytes and crash on the first
        // array index. Recorded here (the proxy-class chokepoint) because a proxy is emitted exactly
        // for the protocols whose existentials cross the boundary.
        RecordClassBoundExistentialMetadata(protocolDecl);

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
        // The vtable setter trampoline may be absent for certain protocols (marker,
        // static-only-requirement, noncopyable), and the proxy emitter must not call
        // Set{Protocol}_vtable in that case or it will throw EntryPointNotFoundException.
        //
        // (Protocols whose conformance was SKIPPED — Self requirement, noncopyable
        // member, static method/property requirement, etc. — do not reach this emitter:
        // ProtocolHandler suppresses the proxy at its EmissionContext.WasConformanceEmitted
        // check before EmitProxyClass is called. Existential factory references to those proxy
        // names are co-gated by the emit-time proxy-reference gate.)
        //
        // The unit-test path (no ModuleEmissionContext supplied) keeps the legacy behaviour
        // — _setVtableEmitted is treated as true so existing tests stay green.
        // Keyed on the module-qualified name (matching EveryProtocolEmitter's Mark site) so a
        // dependency protocol sharing a simple name with a local one cannot mis-gate this proxy.
        _setVtableEmitted = _emissionContext == ModuleEmissionContext.Default
            || _emissionContext.WasSetVtableEmitted(protocolDecl.SwiftTypeName?.ModuleQualifiedName ?? protocolDecl.Name);
        // Keyed on the module-qualified name (matching EveryProtocolEmitter's Mark site) so a
        // dependency protocol sharing a simple name with a local one cannot mis-gate this proxy.
        _witnessGetterEmitted = _emissionContext == ModuleEmissionContext.Default
            || _emissionContext.WasWitnessTableGetterEmitted(protocolDecl.SwiftTypeName!.ModuleQualifiedName);

        if (!_setVtableEmitted)
        {
            _logger.LogDebug($"Emitting proxy class for {protocolDecl.Name} with no-op InitializeVtable: EveryProtocolEmitter did not emit Set{protocolDecl.Name}_vtable; only Swift→C# wrap path will function.");
        }

        // Pick the NSObject-rooted helper symbols when EveryProtocolEmitter routed
        // this protocol through EveryObjCProtocol. The proxy P/Invoke names and entry
        // points all switch to the matching SBW_*EveryObjCProtocol* symbols.
        // Failure B: pick the Entity-rooted helper symbols when EveryProtocolEmitter
        // routed this protocol through EveryEntityProtocol (mutually exclusive with
        // the ObjC base — the gates in EveryProtocolEmitter classify each protocol
        // into exactly one base).
        // Both keyed on the module-qualified name, matching EveryProtocolEmitter's Mark sites.
        _useObjCBase = _emissionContext != ModuleEmissionContext.Default
            && _emissionContext.UsesObjCBase(protocolDecl.SwiftTypeName?.ModuleQualifiedName ?? protocolDecl.Name);
        _useEntityBase = _emissionContext != ModuleEmissionContext.Default
            && _emissionContext.UsesEntityBase(protocolDecl.SwiftTypeName?.ModuleQualifiedName ?? protocolDecl.Name);
        // Read-only (Swift-vended-only) proxy: no synthesizable EveryProtocol conformance, and the
        // module may export no EveryProtocol metadata accessor at all. Keyed on the SIMPLE name to
        // match ModuleHandler.MarkReadOnlyProxy(p.Name). The unit-test path (no ModuleEmissionContext)
        // keeps the legacy non-read-only behaviour so existing proxy tests stay green.
        _isReadOnlyProxy = _emissionContext != ModuleEmissionContext.Default
            && _emissionContext.IsReadOnlyProxy(protocolDecl.Name);

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
        writer.WriteLine($"public unsafe partial class {proxyClassNameWithGenerics} : {interfaceNameWithGenerics}, ISwiftObject, IDisposable, Swift.Runtime.ISwiftExistentialConvertible<ExistentialContainer1>, Swift.Runtime.IProtocolProxyImpl<{interfaceNameWithGenerics}>{constraints}");
        writer.WriteLine("{");
        writer.Indent++;

        // Emit vtable structs. A read-only (Swift-vended-only) proxy has no reverse
        // EveryProtocol conformance, so EveryProtocolEmitter emits no Swift `{P}_vtable`
        // struct and no Set{P}_vtable trampoline. The C# `{P}SwiftVTable`/`{P}LocalVTable`
        // mirrors (and the `_swiftVTable`/`_localVTable`/`_localVTableHandle` fields that
        // reference them, emitted in EmitStaticFields) would then be dead AND would diverge
        // from the Swift side — the ArtifactParityGate `vtable-cs-only` violation. Suppress
        // them so the C# mirror tracks the Swift side exactly. The unit-test path keeps the
        // legacy (always-emit) behaviour: _isReadOnlyProxy is false without an emission context.
        if (!_isReadOnlyProxy)
        {
            EmitSwiftVtableStruct(writer, protocolDecl);
            EmitLocalVtableStruct(writer, protocolDecl);
        }

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

        // Cross-module parent scaffolding (justinwojo/swift-dotnet-bindings#40 cross-module
        // variant). For each parent the child inherits across a module boundary, emit
        // a per-parent vtable struct + local vtable + receivers + SetParent_vtable
        // P/Invoke inside the child proxy class. The child's InitializeVtable populates
        // these in addition to the child's own vtable so Swift's witness dispatch
        // for the inherited requirement reaches the C# impl through the local module's
        // parent _p_vtable (populated here) and the covariant proxy registry lookup.
        // Empty list when the child has no cross-module parents — no emission.
        var crossModuleParents = CollectCrossModuleParents(protocolDecl);
        EmitCrossModuleParentScaffolding(writer, protocolDecl, crossModuleParents);

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
