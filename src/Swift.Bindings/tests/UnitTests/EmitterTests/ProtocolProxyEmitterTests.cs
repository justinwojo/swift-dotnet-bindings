// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for ProtocolProxyEmitter C# code generation.
/// </summary>
public class ProtocolProxyEmitterTests
{
    private readonly TypeDatabase _typeDatabase;
    private readonly ProtocolProxyEmitter _emitter;

    public ProtocolProxyEmitterTests()
    {
        _typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/fake/path");
        _typeDatabase.AddModuleDatabase(module);
        _emitter = new ProtocolProxyEmitter(_typeDatabase, NullLogger.Instance, "TestModule");
    }

    #region Proxy Class Structure Tests

    [Fact]
    public void EmitProxyClass_GeneratesClassDeclaration()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("public unsafe partial class TestProtocolProxy", output);
    }

    [Fact]
    public void EmitProxyClass_ImplementsInterface()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains(": ITestProtocol, ISwiftObject, IDisposable", output);
    }

    [Fact]
    public void EmitProxyClass_GeneratesSwiftVtableStruct()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("private struct TestProtocolSwiftVTable", output);
        Assert.Contains("[StructLayout(LayoutKind.Sequential)]", output);
    }

    [Fact]
    public void EmitProxyClass_GeneratesLocalVtableStruct()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("private struct TestProtocolLocalVTable", output);
    }

    [Fact]
    public void EmitProxyClass_HasEditorBrowsableNever()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]", output);
        // Attribute should appear before the class declaration
        var attrIdx = output.IndexOf("EditorBrowsable");
        var classIdx = output.IndexOf("public unsafe partial class");
        Assert.True(attrIdx < classIdx, "EditorBrowsable attribute should appear before class declaration");
    }

    [Fact]
    public void EmitProxyClass_StampsPerModuleEveryProtocolMetadata_NotGlobalLatch()
    {
        // Finding 33: an opaque (non-class-bound, non-ObjC) proxy must source its
        // EveryProtocol metadata from THIS module's own NativeMethods accessor via the
        // per-proxy s_everyProtocolMetadata static — never through the deleted
        // process-global EveryProtocol.SetTypeMetadata latch (which stamped whichever
        // module initialised first onto every module's opaque existentials).
        var protocolDecl = CreateSimpleProtocol("MetadataProto");
        var output = EmitProxyClass(protocolDecl);

        // The per-module static is sourced from the module's own metadata accessor.
        Assert.Contains(
            "private static readonly TypeMetadata s_everyProtocolMetadata = TypeMetadata.FromHandle(NativeMethods.GetEveryProtocolMetadata());",
            output);
        // The opaque existential's metadata word and GetTypeMetadata() both read the
        // per-module field, not a shared latch.
        Assert.Contains("_swiftContainer.ObjectMetadata = s_everyProtocolMetadata;", output);
        Assert.Contains("return s_everyProtocolMetadata;", output);
        // The global latch is gone: the proxy must not reference it in any direction.
        Assert.DoesNotContain("EveryProtocol.SetTypeMetadata", output);
        Assert.DoesNotContain("EveryProtocol.GetTypeMetadata", output);
    }

    [Fact]
    public void EmitProxyClass_ReadOnlyProxy_OmitsEagerEveryProtocolMetadataField()
    {
        // RealityKit EntityGestureRecognizer regression: a read-only (Swift-vended-only)
        // proxy lives in a module that may export NO EveryProtocol scaffolding (zero suitable
        // protocols), so the SBW_GetMetadata_EveryProtocol accessor does not exist. An eager
        // `s_everyProtocolMetadata` static initializer P/Invokes that missing symbol in the
        // type's cctor — even on the wrap-only path that never needs it — and throws
        // TypeInitializationException the first time the proxy type is touched. The field must
        // be suppressed for read-only proxies.
        var protocolDecl = CreateSimpleProtocol("EntityGestureRecognizer");
        var ctx = new ModuleEmissionContext();
        ctx.MarkReadOnlyProxy(protocolDecl.Name);

        var output = EmitProxyClassWithContext(protocolDecl, ctx);

        // No eager static field initializer that would P/Invoke the (absent) metadata accessor.
        // Target the field DECLARATION specifically — comments elsewhere legitimately name the
        // field, so a bare substring check would false-positive on those.
        Assert.DoesNotContain("static readonly TypeMetadata s_everyProtocolMetadata", output);
        // Sanity: an unmarked proxy of the same shape DOES emit the field — proving the
        // suppression is keyed on the read-only marking, not on the protocol shape.
        var unmarked = EmitProxyClass(CreateSimpleProtocol("EntityGestureRecognizer"));
        Assert.Contains("static readonly TypeMetadata s_everyProtocolMetadata", unmarked);
    }

    [Fact]
    public void EmitProxyClass_ReadOnlyProxy_GetTypeMetadataFailsCleanInsteadOfTouchingMissingAccessor()
    {
        // The C#-implements-protocol (synthesis) direction is unsupported for a read-only
        // proxy — its existential cannot be synthesized from a managed implementation. With
        // the eager metadata field suppressed, GetTypeMetadata() must fail clean
        // (NotSupportedException) rather than reference the removed field or P/Invoke an
        // accessor the wrapper never exported.
        var protocolDecl = CreateSimpleProtocol("EntityGestureRecognizer");
        var ctx = new ModuleEmissionContext();
        ctx.MarkReadOnlyProxy(protocolDecl.Name);

        var output = EmitProxyClassWithContext(protocolDecl, ctx);

        var body = ExtractMethodBody(output, "public static TypeMetadata GetTypeMetadata()");
        Assert.Contains("throw new global::System.NotSupportedException", body);
        Assert.DoesNotContain("s_everyProtocolMetadata", body);
        Assert.DoesNotContain("NativeMethods.GetEveryProtocolMetadata()", body);
    }

    [Fact]
    public void EmitProxyClass_InheritsAvailabilityFromProtocol()
    {
        // The proxy class declaration should inherit the source protocol's
        // [SupportedOSPlatform] so consumer call sites under a lower iOS
        // baseline don't trip CA1416 against the proxy's internal calls.
        var protocolDecl = CreateProtocolWithProperty("GatedProtocol", "value", hasGetter: true, hasSetter: false);
        protocolDecl.AvailabilityAnnotations = new List<AvailabilityAnnotation>
        {
            new(Platform: "iOS", IntroducedVersion: "16.0", DeprecatedVersion: null,
                ObsoletedVersion: null, IsUnconditionallyDeprecated: false,
                IsUnconditionallyUnavailable: false, Message: null, Renamed: null),
        };

        var output = EmitProxyClass(protocolDecl);

        // The platform attribute must appear at all
        Assert.Contains("[global::System.Runtime.Versioning.SupportedOSPlatform(\"ios16.0\")]", output);
        // …and must precede the proxy class declaration so it lands on the
        // class type, not on a member further down.
        var platformIdx = output.IndexOf("SupportedOSPlatform(\"ios16.0\")");
        var classIdx = output.IndexOf("public unsafe partial class GatedProtocolProxy");
        Assert.True(platformIdx >= 0 && classIdx >= 0,
            "Both SupportedOSPlatform attribute and proxy class declaration must exist.");
        Assert.True(platformIdx < classIdx,
            "SupportedOSPlatform should be emitted before the proxy class declaration.");
    }

    #endregion

    #region Static Fields Tests

    [Fact]
    public void EmitProxyClass_GeneratesProtocolWitnessTableField()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("private static IntPtr _protocolWitnessTable;", output);
    }

    [Fact]
    public void EmitProxyClass_GeneratesSwiftVTableField()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("private static TestProtocolSwiftVTable _swiftVTable;", output);
    }

    [Fact]
    public void EmitProxyClass_GeneratesLocalVTableField()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("private static TestProtocolLocalVTable _localVTable;", output);
    }

    #endregion

    #region Instance Fields Tests

    [Fact]
    public void EmitProxyClass_GeneratesCSharpImplField()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        // _csharpImpl is now a weak-reference-backed property (to break the
        // impl-anchor lifetime cycle). The field is _csharpImplRef.
        Assert.Contains("private readonly WeakReference<ITestProtocol>? _csharpImplRef;", output);
        Assert.Contains("private ITestProtocol? _csharpImpl", output);
    }

    [Fact]
    public void EmitProxyClass_GeneratesEveryProtocolHandleField()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        // The proxy holds a plain IntPtr now — ProxyLifetimeTracker owns the +1 release.
        Assert.Contains("private readonly IntPtr _everyProtocolHandle;", output);
        Assert.DoesNotContain("private readonly EveryProtocol? _everyProtocol;", output);
    }

    [Fact]
    public void EmitProxyClass_GeneratesSwiftContainerField()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("private ExistentialContainer1 _swiftContainer;", output);
        Assert.DoesNotContain("private readonly ExistentialContainer1 _swiftContainer;", output);
    }

    #endregion

    #region Static Constructor Tests

    [Fact]
    public void EmitProxyClass_GeneratesStaticConstructor()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("static TestProtocolProxy()", output);
    }

    [Fact]
    public void EmitProxyClass_GeneratesInitializeVtableMethod()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("private static void InitializeVtable()", output);
    }

    [Fact]
    public void EmitProxyClass_GeneratesVtableInitializationCheck()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("if (_vtableInitialized) return;", output);
    }

    [Fact]
    public void EmitProxyClass_NoSetVtableTrampoline_EmitsNoOpInitializeVtable()
    {
        // When the wrapper
        // module did NOT call MarkSetVtableEmitted for this protocol — the marker /
        // composition shape where EveryProtocolEmitter records the conformance but emits
        // no Set<Protocol>_vtable Swift trampoline — the proxy class MUST still be emitted
        // (existential factories reference it by name) but its static ctor must not call
        // NativeMethods.SetXxx_vtable (would throw EntryPointNotFoundException at first
        // proxy use). The static ctor short-circuits to a no-op InitializeVtable that only
        // sets _vtableInitialized = true.
        var ctx = new ModuleEmissionContext();
        // Deliberately do NOT call ctx.MarkSetVtableEmitted("TestProtocol") — this is the
        // marker/composition path the bug 5 fix targets.
        var emitter = new ProtocolProxyEmitter(_typeDatabase, NullLogger.Instance, "TestModule", ctx);
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);

        var stringWriter = new StringWriter();
        var writer = new CSharpWriter(stringWriter);
        emitter.EmitProxyClass(writer, protocolDecl);
        var output = stringWriter.ToString();

        // Proxy class is still emitted (existential factories need the symbol).
        Assert.Contains("public unsafe partial class TestProtocolProxy", output);
        // InitializeVtable still exists and the _vtableInitialized guard still gates it,
        // but the body must NOT call any Set*_vtable PInvoke. Match the call shape
        // (NativeMethods.SetTestProtocol_vtable(...) — see ProtocolProxyEmitter.StaticInit.cs:250)
        // rather than the bare symbol name; the no-op path emits a documenting
        // comment that mentions the symbol by name and would otherwise match.
        Assert.Contains("private static void InitializeVtable()", output);
        Assert.Contains("if (_vtableInitialized) return;", output);
        Assert.DoesNotContain("NativeMethods.SetTestProtocol_vtable(", output);
    }

    [Fact]
    public void EmitProxyClass_SetVtableTrampolineEmitted_KeepsRealInitializeVtableBody()
    {
        // Counterpart to the no-op test: when the wrapper DID emit Set<Protocol>_vtable
        // (the implementable-conformance path), the proxy's InitializeVtable must call
        // NativeMethods.SetXxx_vtable normally.
        var ctx = new ModuleEmissionContext();
        // The SetVtable marker keys on the module-qualified name (T2.6), matching the proxy's
        // read site; CreateProtocolWithProperty gives this decl SwiftTypeName "TestModule.TestProtocol".
        ctx.MarkSetVtableEmitted("TestModule.TestProtocol");
        var emitter = new ProtocolProxyEmitter(_typeDatabase, NullLogger.Instance, "TestModule", ctx);
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);

        var stringWriter = new StringWriter();
        var writer = new CSharpWriter(stringWriter);
        emitter.EmitProxyClass(writer, protocolDecl);
        var output = stringWriter.ToString();

        Assert.Contains("public unsafe partial class TestProtocolProxy", output);
        Assert.Contains("private static void InitializeVtable()", output);
        // The real path emits `NativeMethods.SetTestProtocol_vtable(...)`. Match
        // the call shape (ProtocolProxyEmitter.StaticInit.cs:250) rather than the
        // bare symbol so the assertion doesn't accidentally match a comment.
        Assert.Contains("NativeMethods.SetTestProtocol_vtable(", output);
    }

    #endregion

    #region Receiver Method Tests

    [Fact]
    public void EmitProxyClass_GeneratesPropertyGetterReceiver()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("[UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]", output);
        Assert.Contains("private static IntPtr Receive_value_get(", output);
    }

    [Fact]
    public void EmitProxyClass_GeneratesPropertySetterReceiver()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: true);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("private static void Receive_value_set(", output);
    }

    [Fact]
    public void EmitProxyClass_GeneratesMethodReceiver()
    {
        var protocolDecl = CreateProtocolWithMethod("TestProtocol", "doSomething");
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("private static void Receive_doSomething_0(", output);
    }

    [Fact]
    public void EmitProxyClass_ObjCOptionalMethodBeforeRequired_DoesNotConsumeReverseDispatchSlot()
    {
        // Defect C (Session 1): an @objc optional method that PRECEDES a required method
        // must NOT consume a reverse-dispatch slot index. The Swift producers
        // (EveryProtocolEmitter.EmitProtocolVtableStruct, WitnessDispatchEmitter) skip
        // @objc-optional methods BEFORE incrementing the slot index, so the required method
        // that follows lands at slot 0. The C# proxy consumers (vtable struct + local vtable
        // + vtable assignments + receivers + witness P/Invoke + interface impl) must do the
        // same — otherwise the required method lands one pointer slot past where Swift reads
        // it (Finding-8 positional corruption) and the C# vtable struct grows an extra field
        // Swift never wrote.
        var protocol = CreateSimpleProtocol("OptionalFirstProto");
        var optional = CreateMethodDecl("willFireOptional");
        optional.IsObjCOptional = true;
        var required = CreateMethodDecl("didFireRequired");
        protocol.Methods.Add(optional);
        protocol.Methods.Add(required);

        var output = EmitProxyClass(protocol);

        // The required method occupies slot 0 across every numbering scheme.
        // Scheme #2 — vtable field index (Swift↔C# positional slot) + its receiver:
        Assert.Contains("Receive_didFireRequired_0(", output);
        Assert.DoesNotContain("Receive_didFireRequired_1(", output);
        Assert.Contains("func_didFireRequired_0;", output);
        Assert.DoesNotContain("func_didFireRequired_1;", output);
        // Scheme #1 — the SBW witness-accessor symbol the C#→Swift forward call binds to. Pre-fix
        // the SwiftObject.cs / InterfaceImpl.cs consumer walks named `_1`, a symbol the dylib never
        // exports → EntryPointNotFoundException at the first forward dispatch. (Substring match
        // sidesteps the protocol-name prefix.)
        Assert.Contains("method_didFireRequired_0", output);
        Assert.DoesNotContain("method_didFireRequired_1", output);
        // ...and the @objc-optional method gets no reverse-dispatch slot of its own.
        Assert.DoesNotContain("Receive_willFireOptional", output);
        Assert.DoesNotContain("func_willFireOptional", output);
    }

    [Fact]
    public void IncludesProperty_AgreesWithPlanBuilder_AcrossFlagMatrix()
    {
        // Finding 31 (Session 1): the predicates that decide "does this protocol property get a
        // vtable slot?" — the documented single-source-of-truth ProtocolVtableMembers.IncludesProperty
        // and the plan/fan-out populators (EveryProtocolEmitter.ComputePropertyEmissionPlans) —
        // must agree across the IsStatic × IsObjCOptional × IsProtocolRequirement × IsFromExtension
        // matrix for a plain (non-closure, non-Self, non-generic) property. Divergence on any axis
        // means a struct slot the populator never fills (or vice versa); Swift copies the vtable
        // positionally, so a mismatched slot shifts every later field (Defect F / Finding-8
        // corruption). This locks the sites together so they cannot silently re-diverge.
        var closureHandler = new ClosureHandler(_typeDatabase);

        foreach (var isStatic in new[] { false, true })
        foreach (var isObjCOptional in new[] { false, true })
        foreach (var isRequirement in new[] { false, true })
        foreach (var isFromExtension in new[] { false, true })
        {
            var protocol = CreateSimpleProtocol("FlagMatrixProto");
            var prop = new PropertyDecl
            {
                Name = "value",
                SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                IsStatic = isStatic,
                HasStorage = false,
                Accessors = new List<AccessorDecl>
                {
                    new GetAccessorDecl { Method = CreateMethodDecl("value_get") }
                },
                IsObjCOptional = isObjCOptional,
                IsProtocolRequirement = isRequirement,
                IsFromExtension = isFromExtension,
                ParentDecl = null,
                ModuleDecl = null
            };
            protocol.Properties.Add(prop);

            var label = $"static={isStatic} objcOpt={isObjCOptional} req={isRequirement} ext={isFromExtension}";

            // Documented slot rule: a plain property earns a slot iff it is an instance,
            // non-optional protocol requirement. IsFromExtension has no independent effect at the
            // emitter layer — the parser already drops (IsFromExtension && !IsProtocolRequirement)
            // upstream — so it must not move the outcome here.
            bool expected = !isStatic && !isObjCOptional && isRequirement;

            bool includesProperty = ProtocolVtableMembers.IncludesProperty(prop, protocol, closureHandler);
            Assert.True(expected == includesProperty,
                $"IncludesProperty disagreed with the documented slot rule for [{label}]");

            // Exercise the real populator (not a re-encoded predicate) so a future change to
            // ComputePropertyEmissionPlans that re-diverges from IncludesProperty is caught here.
            var plans = EveryProtocolEmitter.ComputePropertyEmissionPlans(new[] { protocol });
            bool planIncludes = plans.ContainsKey($"{prop.Name}|{prop.SwiftTypeSpec}");
            Assert.True(includesProperty == planIncludes,
                $"IncludesProperty and ComputePropertyEmissionPlans diverged for [{label}]");
        }
    }

    [Fact]
    public void EmitProxyClass_SetterReceiver_OptionalString_AppliesConversion()
    {
        // Regression: Protocol property setter receiver marshals Swift ABI type (SwiftOptional<SwiftString>)
        // but the C# interface property uses idiomatic type (string?). The receiver must apply
        // GetReturnConversion to bridge the two — without this, assignment fails at compile time.
        var optionalString = new NamedTypeSpec("Swift.Optional");
        optionalString.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        var protocolDecl = CreateProtocolWithProperty("ConvertProto", "label", hasGetter: false, hasSetter: true, optionalString);
        var output = EmitProxyClass(protocolDecl);

        // The receiver should apply type conversion in the assignment (not just raw "value")
        Assert.Contains("Receive_label_set", output);
        Assert.Contains("?.ToString()", output);
    }

    [Fact]
    public void EmitProxyClass_GetterReceiver_String_UsesUtf8Slice()
    {
        // String returns use Utf8Slice encoding to avoid ARC issues with SwiftString.
        // MarshalToSwiftBuffer<SwiftString> does Unsafe.Write which doesn't retain ARC references,
        // causing crashes when Swift reads the result. Utf8Slice passes raw bytes safely.
        var typeSpec = new NamedTypeSpec("Swift.String");
        var protocolDecl = CreateProtocolWithProperty("StringProto", "name", hasGetter: true, hasSetter: false, typeSpec);
        var output = EmitProxyClass(protocolDecl);

        // Getter should use Utf8Slice encoding instead of SwiftString
        Assert.Contains("Receive_name_get", output);
        Assert.Contains("MarshalStringToUtf8Slice(result)", output);
        Assert.DoesNotContain("new SwiftString(result)", output);
    }

    [Fact]
    public void EmitProxyClass_GetterReceiver_OptionalString_ConvertsToSwiftOptional()
    {
        // Regression (P0 #1): Optional<String> getter must convert string? → SwiftOptional<SwiftString>
        var optionalString = new NamedTypeSpec("Swift.Optional");
        optionalString.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        var protocolDecl = CreateProtocolWithProperty("OptStringProto", "label", hasGetter: true, hasSetter: false, optionalString);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("Receive_label_get", output);
        Assert.Contains("SwiftOptional<", output);
        Assert.Contains("new SwiftString(", output);
        Assert.Contains("MarshalToSwiftBuffer(swiftResult)", output);
    }

    [Fact]
    public void EmitProxyClass_GetterReceiver_BlittableType_NoConversion()
    {
        // Non-convertible (blittable) types should NOT get intermediate conversion.
        // F1: Swift.Int properties ARE narrowed (int) and get ABI widening cast (nint)result.
        // Use Swift.Int32 to test a truly non-narrowed blittable type.
        RegisterSwiftInt32();
        var protocolDecl = CreateProtocolWithProperty("IntProto", "count", hasGetter: true, hasSetter: false, new NamedTypeSpec("Swift.Int32"));
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("Receive_count_get", output);
        Assert.Contains("MarshalToSwiftBuffer(result)", output);
        Assert.DoesNotContain("swiftResult", output.Substring(output.IndexOf("Receive_count_get")));
    }

    [Fact]
    public void EmitProxyClass_GetterReceiver_OptionalInt_WrapsInSwiftOptional()
    {
        // Regression: Optional<Int32> getter must wrap int? → SwiftOptional<int>.NewSome/NewNone.
        // MarshalToSwiftBuffer uses Unsafe.Write<T> — Nullable<int> is NOT layout-compatible with
        // SwiftOptional<int> (a class with SafeHandle). Without explicit wrapping, raw Nullable<int>
        // bytes are written instead of a proper SwiftOptional allocation.
        RegisterSwiftInt32();
        var optionalInt = new NamedTypeSpec("Swift.Optional");
        optionalInt.GenericParameters.Add(new NamedTypeSpec("Swift.Int32"));
        var protocolDecl = CreateProtocolWithProperty("OptIntProto", "count", hasGetter: true, hasSetter: false, optionalInt);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("Receive_count_get", output);
        Assert.Contains("SwiftOptional<", output);
        Assert.Contains(".NewSome(", output);
        Assert.Contains(".NewNone()", output);
        Assert.Contains("MarshalToSwiftBuffer(swiftResult)", output);
    }

    [Fact]
    public void EmitProxyClass_GetterReceiver_OptionalBool_WrapsInSwiftOptional()
    {
        // Regression: Same as Optional<Int> — bool? must be wrapped in SwiftOptional<bool>.
        var optionalBool = new NamedTypeSpec("Swift.Optional");
        optionalBool.GenericParameters.Add(new NamedTypeSpec("Swift.Bool"));
        var protocolDecl = CreateProtocolWithProperty("OptBoolProto", "flag", hasGetter: true, hasSetter: false, optionalBool);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("Receive_flag_get", output);
        Assert.Contains("SwiftOptional<", output);
        Assert.Contains(".NewSome(", output);
        Assert.Contains(".NewNone()", output);
        Assert.Contains("MarshalToSwiftBuffer(swiftResult)", output);
    }

    [Fact]
    public void EmitProxyClass_GetterReceiver_OptionalSimpleEnum_WrapsInSwiftOptional()
    {
        // Regression: Optional<SimpleEnum> getter must wrap in SwiftOptional.
        // Register a simple enum type so the factory resolves it.
        _typeDatabase.AddOutOfModuleTypes(new[]
        {
            (SwiftTypeName.FromModuleQualifiedName("TestModule.MyStatus"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "MyStatus"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyStatus"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum,
                Kind = TypeRecordKind.Enum,
                RawValueTypeName = "Int"
            })
        });

        var optionalEnum = new NamedTypeSpec("Swift.Optional");
        optionalEnum.GenericParameters.Add(new NamedTypeSpec("TestModule.MyStatus"));
        var protocolDecl = CreateProtocolWithProperty("OptEnumProto", "status", hasGetter: true, hasSetter: false, optionalEnum);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("Receive_status_get", output);
        Assert.Contains("SwiftOptional<", output);
        Assert.Contains(".NewSome(", output);
        Assert.Contains(".NewNone()", output);
        Assert.Contains("MarshalToSwiftBuffer(swiftResult)", output);
    }

    [Fact]
    public void EmitProxyClass_GetterReceiver_OptionalClass_UsesDangerousGetHandle()
    {
        // Optional<Class> getter must extract IntPtr via .Payload.DangerousGetHandle()
        // because optType is IntPtr (PInvokeType) but the property value is the public C# class.
        _typeDatabase.AddOutOfModuleTypes(new[]
        {
            (SwiftTypeName.FromModuleQualifiedName("TestModule.MyService"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "MyService"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyService"),
                MetadataAccessor = "$s10TestModule9MyServiceCMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Class
            })
        });

        var optionalClass = new NamedTypeSpec("Swift.Optional");
        optionalClass.GenericParameters.Add(new NamedTypeSpec("TestModule.MyService"));
        var protocolDecl = CreateProtocolWithProperty("OptClassProto", "service", hasGetter: true, hasSetter: true, optionalClass);
        var output = EmitProxyClass(protocolDecl);

        // Getter: must use DangerousGetHandle to extract IntPtr from the class instance
        Assert.Contains("Receive_service_get", output);
        Assert.Contains("DangerousGetHandle()", output);
        Assert.Contains("SwiftOptional<", output);

        // Setter (issue #40): an Optional<class> arrives as a single nil-pointer-optimised
        // word in the borrowed slot, NOT a managed SwiftOptional<MyService>. The receiver must
        // read it via the optional copy-out helper (deref slot + ObjC-aware retain +
        // NewFromPayload), NOT Unsafe.Read<SwiftOptional<MyService>> — a managed class read from
        // raw Swift memory. Scope the guard to the setter body (brace-matched): the witness-
        // dispatch getter, emitted later, does legitimately use MarshalFromSwift<MyService>,
        // so an unscoped check false-trips.
        Assert.Contains("Receive_service_set", output);
        var setterBody = ExtractMethodBody(output, "private static void Receive_service_set(");
        Assert.Contains("MarshalBorrowedOptionalClassFromSlot<TestModule.MyService>(valuePtr)", setterBody);
        Assert.DoesNotContain("MarshalFromSwift<SwiftOptional<TestModule.MyService>>", setterBody);
        Assert.DoesNotContain("(TestModule.MyService?)", setterBody);

        // Witness-dispatch getter (proxy -> Swift): materialises the returned class from
        // the raw Swift pointer via MarshalFromSwift on the inner public type. This is the
        // correct shape for an existential/class-bound returning property accessor.
        Assert.Contains("MarshalFromSwift<TestModule.MyService>", output);
    }

    [Fact]
    public void EmitProxyClass_GetterReceiver_OptionalNonFrozenStruct_PassesTypedWrapper()
    {
        // Optional<NonFrozenStruct> getter must pass the typed C# wrapper directly to
        // SwiftOptional<TWrapper>.NewSome — NOT extract via DangerousGetHandle().
        // NonFrozenStructProjection.SwiftContainerGenericType returns the typed wrapper,
        // and SwiftOptional<TWrapper>.NewSome routes through ISwiftObject.MarshalToSwift,
        // which copies the struct's payload bytes by value via VWT.InitializeWithCopy.
        // Lowering to .Payload.DangerousGetHandle() would type-mismatch the SwiftOptional
        // generic parameter and emit Dictionary<nint, …>-shaped bugs at the same ABI
        // boundary as described for raw ISwiftStruct IntPtr dictionary bugs.
        _typeDatabase.AddOutOfModuleTypes(new[]
        {
            (SwiftTypeName.FromModuleQualifiedName("TestModule.MyConfig"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "MyConfig"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyConfig"),
                MetadataAccessor = "$s10TestModule8MyConfigVMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            })
        });

        var optionalStruct = new NamedTypeSpec("Swift.Optional");
        optionalStruct.GenericParameters.Add(new NamedTypeSpec("TestModule.MyConfig"));
        var protocolDecl = CreateProtocolWithProperty("OptStructProto", "config", hasGetter: true, hasSetter: true, optionalStruct);
        var output = EmitProxyClass(protocolDecl);

        // Getter: must pass the typed wrapper directly to NewSome — not via DangerousGetHandle.
        Assert.Contains("Receive_config_get", output);
        Assert.Contains("SwiftOptional<TestModule.MyConfig>.NewSome(", output);
        // The Some-arg must be the typed wrapper variable, not the wrapper's IntPtr handle.
        Assert.DoesNotContain("Val.Payload.DangerousGetHandle())", output);

        // Setter: simple nullable cast, no redundant MarshalFromSwift
        Assert.Contains("Receive_config_set", output);
        Assert.DoesNotContain("MarshalFromSwift<TestModule.MyConfig>", output);
    }

    [Fact]
    public void EmitProxyClass_ReceiverResolvesImplFromTracker()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        // Receivers read only the first existential word (handle) — the sole field the
        // lookup uses. Reading the full ExistentialContainer1 (5 words) over-reads stack
        // memory when Swift passes a class-bound (2-word) existential for EveryObjCProtocol.
        //
        // Design B2 change 2: on the canonical (no-sibling) path the impl is resolved from
        // ProxyLifetimeTracker's strong root via ResolveImpl<IInterface>(handle) — alive for
        // exactly as long as Swift holds the proxy — NOT from a SwiftObjectRegistry.TryGetProxy
        // proxy lookup (that primitive survives only on the sibling-fan-out / cross-module-parent
        // paths). ResolveImpl<IFace>(handle) has the identical truth value: it succeeds iff the
        // rooted impl implements IFace.
        Assert.Contains("var handle = *(IntPtr*)selfContainer;", output);
        Assert.Contains("Swift.Runtime.ProxyLifetimeTracker.ResolveImpl<ITestProtocol>(handle)", output);
        Assert.DoesNotContain("SwiftObjectRegistry.TryGetProxy<Swift.Runtime.IProtocolProxyImpl<ITestProtocol>>(handle", output);
    }

    [Fact]
    public void EmitProxyClass_ReceiverGuardsAgainstDeadImpl()
    {
        // Design B2: the impl is strong-rooted by ProxyLifetimeTracker for exactly as long as
        // Swift holds the proxy, so on the canonical (no-sibling) path a null resolve is an
        // invariant violation — not a recoverable "GC'd while Swift held it" state. The receiver
        // therefore trips the LOUD backstop via `throw FailFastDeadProxyImpl(...)` (naming the member
        // + handle) rather than fabricating a default and silently corrupting the boundary. The
        // fabrication branch is DELETED on this path (the carrier-sized fallback survives only on the
        // sibling-fan-out path, a genuine cross-protocol miss). The backstop is reached via `throw`,
        // not a bare call: C#'s definite-return analysis (CS0161) is purely syntactic and does NOT
        // consult [DoesNotReturn], so a bare call would leave the value-returning receiver short a
        // terminal return.
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: true);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("var impl = Swift.Runtime.ProxyLifetimeTracker.ResolveImpl<ITestProtocol>(handle);", output);
        Assert.Contains("if (impl is null)", output);
        Assert.Contains("throw global::Swift.Runtime.SwiftClosureMarshaller.FailFastDeadProxyImpl(", output);
        // Never the bare (non-throw) form that fails CS0161.
        Assert.DoesNotContain("global::System.Environment.FailFast(", output);
        // Dispatch uses the rooted local `impl` — never the proxy's weak field (the bang-deref
        // and the proxy.UserImpl read are both gone on the canonical path).
        Assert.DoesNotContain("proxy._csharpImpl!", output);
        Assert.DoesNotContain("var impl = proxy.UserImpl;", output);
    }

    [Fact]
    public void EmitProxyClass_SiblingFanoutAllMiss_PropertyGetter_FailFasts()
    {
        // Finding 14(b): on the sibling-fan-out path, when this interface AND every recorded sibling
        // miss the per-handle lookup, the impl was collected while Swift still held the proxy — the
        // same Design B2 lifetime-invariant violation the no-sibling path catches. The terminal must
        // trip the LOUD FailFastDeadProxyImpl backstop, NOT fabricate a carrier-sized .none/zero buffer
        // (the old silent data-corruption failure mode). It is emitted as `throw FailFastDeadProxyImpl(...)`:
        // the `throw` token supplies the receiver's terminal control-flow exit (CS0161 is syntactic and
        // does NOT consult [DoesNotReturn], so a bare call would not stand in for the terminal return).
        var optionalBool = new NamedTypeSpec("Swift.Optional");
        optionalBool.GenericParameters.Add(new NamedTypeSpec("Swift.Bool"));
        var protocolDecl = CreateProtocolWithProperty("OptBoolDeadProto", "flag", hasGetter: true, hasSetter: false, optionalBool);
        var output = EmitProxyClassWithPropertySibling(protocolDecl, "flag");

        // Find the getter receiver function definition (not the vtable assignment).
        var receiverIdx = output.IndexOf("private static IntPtr Receive_flag_get(");
        Assert.True(receiverIdx >= 0, "Receive_flag_get function definition not found in output");
        var receiverEnd = output.IndexOf("[UnmanagedCallersOnly", receiverIdx + 1);
        if (receiverEnd < 0) receiverEnd = output.Length;
        var receiverBody = output.Substring(receiverIdx, receiverEnd - receiverIdx);

        // The all-siblings-missed terminal throws the FailFastDeadProxyImpl backstop, naming the member
        // and the "all sibling proxies" exhaustion (unique to EmitSiblingFanOutFailFast — the no-sibling
        // backstop says nothing about siblings). Reached via `throw`, never a bare CS0161 call.
        Assert.Contains("throw global::Swift.Runtime.SwiftClosureMarshaller.FailFastDeadProxyImpl(", receiverBody);
        Assert.DoesNotContain("global::System.Environment.FailFast(", receiverBody);
        Assert.Contains("across the primary proxy and all sibling proxies", receiverBody);
        // No fabrication: the old terminal `return MarshalToSwiftBuffer(SwiftOptional<bool>.NewNone())`
        // and the carrier-sized zero buffer are both gone (NewNone still appears INSIDE the success
        // lookup-hit's conversion, but never wrapped directly by MarshalToSwiftBuffer here).
        Assert.DoesNotContain("MarshalToSwiftBuffer(SwiftOptional<bool>.NewNone())", receiverBody);
        Assert.DoesNotContain("AllocZeroedSwiftBuffer", receiverBody);
    }

    [Fact]
    public void EmitProxyClass_SiblingFanoutAllMiss_Method_FailFasts()
    {
        // Same throw-FailFast terminal, exercised on the method-receiver emit site (separate code path
        // from EmitPropertyReceivers) when this interface and every recorded sibling miss. The old code
        // fabricated SwiftOptional<int>.NewNone(); Finding 14(b) replaces it with a loud throw-FailFast.
        RegisterSwiftInt32();
        var optionalInt = new NamedTypeSpec("Swift.Optional");
        optionalInt.GenericParameters.Add(new NamedTypeSpec("Swift.Int32"));
        var protocolDecl = CreateSimpleProtocol("OptIntMethodDeadProto");
        var method = CreateMethodDecl("getMaybe");
        method.CSSignature.Insert(0, new ArgumentDecl
        {
            Name = "",
            PrivateName = "",
            SwiftTypeSpec = optionalInt,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        });
        protocolDecl.Methods.Add(method);
        var output = EmitProxyClassWithMethodSibling(protocolDecl, method);

        var receiverIdx = output.IndexOf("private static IntPtr Receive_getMaybe_0(");
        Assert.True(receiverIdx >= 0, "Receive_getMaybe_0 function definition not found in output");
        var receiverEnd = output.IndexOf("[UnmanagedCallersOnly", receiverIdx + 1);
        if (receiverEnd < 0) receiverEnd = output.Length;
        var receiverBody = output.Substring(receiverIdx, receiverEnd - receiverIdx);

        Assert.Contains("throw global::Swift.Runtime.SwiftClosureMarshaller.FailFastDeadProxyImpl(", receiverBody);
        Assert.DoesNotContain("global::System.Environment.FailFast(", receiverBody);
        Assert.Contains("across the primary proxy and all sibling proxies", receiverBody);
        Assert.DoesNotContain("MarshalToSwiftBuffer(SwiftOptional<int>.NewNone())", receiverBody);
        Assert.DoesNotContain("AllocZeroedSwiftBuffer", receiverBody);
    }

    [Fact]
    public void EmitProxyClass_SiblingFanoutAllMiss_VoidMethod_FailFasts()
    {
        // A void sibling-method all-miss previously emitted a silent `return;`. Finding 14(b): a
        // dropped side-effect is the same lifetime-invariant violation as a dropped value, so the
        // void terminal throws the FailFast backstop too (`throw FailFastDeadProxyImpl(...)` is
        // well-formed for a void method and is the terminal control-flow exit).
        var protocolDecl = CreateSimpleProtocol("VoidMethodDeadProto");
        var method = CreateMethodDecl("doWork");
        protocolDecl.Methods.Add(method);
        var output = EmitProxyClassWithMethodSibling(protocolDecl, method);

        var receiverIdx = output.IndexOf("private static void Receive_doWork_0(");
        Assert.True(receiverIdx >= 0, "Receive_doWork_0 function definition not found in output");
        var receiverEnd = output.IndexOf("[UnmanagedCallersOnly", receiverIdx + 1);
        if (receiverEnd < 0) receiverEnd = output.Length;
        var receiverBody = output.Substring(receiverIdx, receiverEnd - receiverIdx);

        Assert.Contains("throw global::Swift.Runtime.SwiftClosureMarshaller.FailFastDeadProxyImpl(", receiverBody);
        Assert.DoesNotContain("global::System.Environment.FailFast(", receiverBody);
        Assert.Contains("across the primary proxy and all sibling proxies", receiverBody);
    }

    [Fact]
    public void EmitProxyClass_SiblingFanoutAllMiss_SubscriptGetter_FailFasts()
    {
        // Third emit site: the subscript getter receiver is a separate code path from
        // property getters and method returns, so it gets its own sibling-miss FailFast test.
        RegisterSwiftInt32();
        var optionalInt = new NamedTypeSpec("Swift.Optional");
        optionalInt.GenericParameters.Add(new NamedTypeSpec("Swift.Int32"));
        var protocolDecl = CreateSimpleProtocol("OptSubscriptDeadProto");
        var subscript = new SubscriptDecl
        {
            Name = "subscript",
            MangledName = "$s7IndexedP9subscriptSiSgSicig",
            ReturnTypeSpec = optionalInt,
            IndexParameters = new List<ArgumentDecl>
            {
                new()
                {
                    Name = "index",
                    PrivateName = "index",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            IsStatic = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("subscript_get") }
            },
            ParentDecl = null,
            ModuleDecl = null
        };
        protocolDecl.Subscripts.Add(subscript);

        var output = EmitProxyClassWithSubscriptSibling(protocolDecl, subscript, 0);

        var receiverIdx = output.IndexOf("private static IntPtr Receive_subscript_0_get(");
        Assert.True(receiverIdx >= 0, "Receive_subscript_0_get function definition not found in output");
        var receiverEnd = output.IndexOf("[UnmanagedCallersOnly", receiverIdx + 1);
        if (receiverEnd < 0) receiverEnd = output.Length;
        var receiverBody = output.Substring(receiverIdx, receiverEnd - receiverIdx);

        // The subscript all-siblings-missed terminal throws the FailFast backstop (Finding 14(b)), never
        // fabricating a carrier .none or a zero buffer.
        Assert.Contains("throw global::Swift.Runtime.SwiftClosureMarshaller.FailFastDeadProxyImpl(", receiverBody);
        Assert.DoesNotContain("global::System.Environment.FailFast(", receiverBody);
        Assert.Contains("across the primary proxy and all sibling proxies", receiverBody);
        Assert.DoesNotContain("MarshalToSwiftBuffer(SwiftOptional<int>.NewNone())", receiverBody);
        Assert.DoesNotContain("AllocZeroedSwiftBuffer", receiverBody);
    }

    #endregion

    #region Constructor Tests

    [Fact]
    public void EmitProxyClass_GeneratesCSharpImplConstructor()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        // The ctor is unsafe because it takes a function-pointer address for the
        // EveryProtocol deinit callback (&ProxyLifetimeTracker.OnEveryProtocolDeinit).
        Assert.Contains("public unsafe TestProtocolProxy(ITestProtocol implementation)", output);
    }

    [Fact]
    public void EmitProxyClass_GeneratesExistentialContainerConstructor()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("public TestProtocolProxy(ExistentialContainer1 container, bool ownsContainer = false)", output);
    }

    [Fact]
    public void EmitProxyClass_ConstructorRegistersWithSwiftObjectRegistry()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        // Design B2 change 3: the C#-impl proxy registers WEAKLY (Register, not RegisterStrong)
        // so the consumer dropping it lets the proxy be collected — that collection is the signal
        // that releases R0. A strong registry root would pin the proxy for the EveryProtocol's
        // whole lifetime and break the release-on-drop story.
        Assert.Contains("SwiftObjectRegistry.Register(_everyProtocolHandle, this)", output);
        Assert.DoesNotContain("SwiftObjectRegistry.RegisterStrong(_everyProtocolHandle, this)", output);
    }

    [Fact]
    public void EmitProxyClass_ConstructorWiresDeinitCallbackAndTracksImpl()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        // The proxy must wire the deinit callback AND anchor the +1 to impl lifetime.
        Assert.Contains("NativeMethods.SetEveryProtocolDeinitCallback", output);
        Assert.Contains("Swift.Runtime.ProxyLifetimeTracker.OnEveryProtocolDeinit", output);
        Assert.Contains("Swift.Runtime.ProxyLifetimeTracker.Track(implementation, _everyProtocolHandle)", output);
    }

    #endregion

    #region Interface Implementation Tests

    [Fact]
    public void EmitProxyClass_ImplementsPropertyGetter()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        // Property type comes from Swift.Int which maps to Swift.AnyType in the default translation
        Assert.Contains("public Swift.AnyType Value", output);
        Assert.Contains("if (_csharpImpl != null)", output);
        Assert.Contains("return _csharpImpl.Value;", output);
    }

    [Fact]
    public void EmitProxyClass_ImplementsPropertySetter()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: true);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("_csharpImpl.Value = value;", output);
    }

    [Fact]
    public void EmitProxyClass_ImplementsMethod()
    {
        var protocolDecl = CreateProtocolWithMethod("TestProtocol", "doSomething");
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("public void DoSomething()", output);
        Assert.Contains("_csharpImpl.DoSomething();", output);
    }

    #endregion

    #region ISwiftObject Implementation Tests

    [Fact]
    public void EmitProxyClass_ImplementsGetTypeMetadata()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("public static TypeMetadata GetTypeMetadata()", output);
    }

    [Fact]
    public void EmitProxyClass_ImplementsNewFromPayload()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("public static ISwiftObject NewFromPayload(IntPtr payload)", output);
    }

    [Fact]
    public void EmitProxyClass_ImplementsMarshalToSwift()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("public int MarshalToSwift(ref Span<byte> swiftDestSpan)", output);
    }

    #endregion

    #region NativeMethods Tests

    [Fact]
    public void EmitProxyClass_GeneratesNativeMethodsClass()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("private static partial class NativeMethods", output);
    }

    [Fact]
    public void EmitProxyClass_GeneratesSetVtablePInvoke()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        // P/Invoke should target the module library path (fallback when AsyncLibraryName is null)
        Assert.Contains("[LibraryImport(\"/fake/path\"", output);
        Assert.Contains("EntryPoint = \"SetTestProtocol_vtable\"", output);
    }

    [Fact]
    public void EmitProxyClass_DllImportUsesAsyncLibraryName()
    {
        _typeDatabase.AsyncLibraryName = "DocScanSwiftBindings";
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("[LibraryImport(\"DocScanSwiftBindings\"", output);
        Assert.DoesNotContain("[LibraryImport(\"SwiftBindings\"", output);
    }

    [Fact]
    public void EmitProxyClass_DllImportFallsBackToModuleLibrary()
    {
        // No AsyncLibraryName set — should fall back to module library path
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("[LibraryImport(\"/fake/path\"", output);
        Assert.DoesNotContain("[LibraryImport(\"SwiftBindings\"", output);
    }

    #endregion

    #region Protocol Conformance Filtering Tests

    [Fact]
    public void EmitProxyClass_SkipsProtocolsWithSelfRequirement()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        protocolDecl.HasSelfRequirement = true;

        var output = EmitProxyClass(protocolDecl);

        Assert.DoesNotContain("public unsafe class TestProtocolProxy", output);
    }

    [Fact]
    public void EmitProxyClass_SkipsProtocolsWithAssociatedTypes()
    {
        // Protocols with associated types would create generic proxy classes,
        // but C# doesn't allow [UnmanagedCallersOnly] or [DllImport] in generic types.
        // So we skip proxy generation for these protocols.
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        protocolDecl.AssociatedTypes.Add(new AssociatedTypeDecl { Name = "Element" });

        var output = EmitProxyClass(protocolDecl);

        // Verify no proxy class is generated
        Assert.DoesNotContain("public unsafe class TestProtocolProxy", output);
    }

    [Fact]
    public void EmitProxyClass_SkipsProtocolsWithMultipleAssociatedTypes()
    {
        // Protocols with multiple associated types would also be skipped
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        protocolDecl.AssociatedTypes.Add(new AssociatedTypeDecl { Name = "Key" });
        protocolDecl.AssociatedTypes.Add(new AssociatedTypeDecl { Name = "Value" });

        var output = EmitProxyClass(protocolDecl);

        // Verify no proxy class is generated
        Assert.DoesNotContain("TestProtocolProxy", output);
    }

    [Fact]
    public void EmitProxyClass_EmptyProtocol_GeneratesProxyClass()
    {
        // Protocols with no implementable instance members still need proxy classes —
        // return types like ILayoutConstraintItem require a proxy constructor.
        // The emission code gracefully handles zero members (loops iterate zero times).
        var protocolDecl = CreateSimpleProtocol("EmptyProtocol");

        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("public unsafe partial class EmptyProtocolProxy", output);
        Assert.Contains(": IEmptyProtocol, ISwiftObject, IDisposable", output);
        // Constructor and ISwiftObject implementation still emitted
        Assert.Contains("public unsafe EmptyProtocolProxy(IEmptyProtocol implementation)", output);
        Assert.Contains("public EmptyProtocolProxy(ExistentialContainer1 container, bool ownsContainer = false)", output);
        Assert.Contains("public static TypeMetadata GetTypeMetadata()", output);
    }

    [Fact]
    public void EmitProxyClass_EmptyProtocol_WithInheritedRequirements_StillGeneratesProxy()
    {
        // The inherited requirements guard is intentionally disabled — InheritedProtocols
        // was recently populated but enabling the guard would skip proxy emission for
        // protocols that previously worked. The proxy is still generated even for
        // empty protocols with inherited requirements.
        var protocolDecl = CreateSimpleProtocol("DerivedProtocol");
        protocolDecl.InheritedProtocols.Add(new NamedTypeSpec("TestModule.BaseProtocol"));

        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("DerivedProtocolProxy", output);
    }

    [Fact]
    public void EmitProxyClass_EmptyProtocol_InheritingOnlyAnyObject_GeneratesProxy()
    {
        // AnyObject is filtered out of inherited interface lists, so a protocol
        // inheriting only AnyObject is effectively empty — safe to generate proxy.
        var protocolDecl = CreateSimpleProtocol("MarkerProtocol");
        protocolDecl.InheritedProtocols.Add(new NamedTypeSpec("Swift.AnyObject"));

        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("public unsafe partial class MarkerProtocolProxy", output);
    }

    [Fact]
    public void EmitProxyClass_WithSubscript_EmitsSubscriptReceiversAndIndexer()
    {
        var protocolDecl = CreateSimpleProtocol("IndexedProtocol");
        protocolDecl.Subscripts.Add(new SubscriptDecl
        {
            Name = "subscript",
            MangledName = "$s7IndexedP9subscriptS2icig",
            ReturnTypeSpec = new NamedTypeSpec("Swift.Int"),
            IndexParameters = new List<ArgumentDecl>
            {
                new()
                {
                    Name = "index",
                    PrivateName = "index",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            IsStatic = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("subscript_get") },
                new SetAccessorDecl { Method = CreateMethodDecl("subscript_set") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });

        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("private static IntPtr Receive_subscript_0_get(", output);
        Assert.Contains("private static void Receive_subscript_0_set(", output);
        Assert.Contains("public Swift.AnyType this[Swift.AnyType index]", output);
    }

    [Fact]
    public void EmitProxyClass_WithDuplicateMethodSignatures_EmitsSingleReceiver()
    {
        var protocolDecl = CreateSimpleProtocol("DuplicateProtocol");
        protocolDecl.Methods.Add(CreateMethodDecl("refresh"));
        protocolDecl.Methods.Add(CreateMethodDecl("refresh"));

        var output = EmitProxyClass(protocolDecl);

        Assert.Equal(1, EmitterTestHelpers.CountOccurrences(output, "private static void Receive_refresh_0("));
    }

    [Fact]
    public void EmitProxyClass_WithTupleReturnMethod_UsesValueTupleSignature()
    {
        var protocolDecl = CreateSimpleProtocol("TupleProtocol");
        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "decompose",
            MangledName = "$s12TupleProtocol9decomposeSi_SbtF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    SwiftTypeSpec = new TupleTypeSpec(new List<TypeSpec>
                    {
                        new NamedTypeSpec("Swift.Int"),
                        new NamedTypeSpec("Swift.Bool")
                    }),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        });

        var output = EmitProxyClass(protocolDecl);

        // Factory returns null for tuple (Swift.Int not in test DB), but fallback
        // resolves elements individually: Swift.Int → AnyType, Swift.Bool → bool (well-known)
        Assert.Contains("public (Swift.AnyType, bool) Decompose()", output);
    }

    [Fact]
    public void EmitProxyClass_WithClosureParameter_UsesActionSignature()
    {
        var protocolDecl = CreateSimpleProtocol("ClosureProtocol");
        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "apply",
            MangledName = "$s14ClosureProtocol5applyyyySiXEF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                },
                new()
                {
                    Name = "callback",
                    PrivateName = "callback",
                    SwiftTypeSpec = new ClosureTypeSpec(
                        arguments: new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("Swift.Int") }),
                        returnType: TupleTypeSpec.Empty),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        });

        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("public void Apply(Action<Swift.AnyType> callback)", output);
    }

    [Fact]
    public void EmitProxyClass_WithProtocolCompositionProperty_UsesCompositionInterface()
    {
        // Protocol compositions produce a combined interface name (IP1AndP2) via
        // ExistentialHandler.GetCompositionInterfaceName. The factory routes through
        // ExistentialProjection which uses GetPublicExistentialType. Both protocols
        // must be registered in the TypeDatabase — the multi-protocol path's
        // AllProtocolsHaveTypeRecords gate collapses to `object` otherwise so we
        // don't emit unresolvable interface references for marker/suppressed protocols.
        RegisterProtocol("P1");
        RegisterProtocol("P2");
        var protocolDecl = CreateSimpleProtocol("ExistentialProtocol");
        protocolDecl.Properties.Add(new PropertyDecl
        {
            Name = "delegate",
            SwiftTypeSpec = new ProtocolListTypeSpec(new[]
            {
                new NamedTypeSpec("TestModule.P1"),
                new NamedTypeSpec("TestModule.P2")
            }),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("delegate_get") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });

        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("IP1AndP2 Delegate", output);
    }

    [Fact]
    public void EmitProxyClass_WithAnyExistentialProperty_UsesObjectForAnyType()
    {
        // Swift "any" existential resolves to "object" via the ExistentialProjection
        // 3-tier fallback: well-known → proxy → object.
        var protocolDecl = CreateSimpleProtocol("AnyExistentialProtocol");
        var anyExistential = new NamedTypeSpec("Swift.Any.Type") { IsAny = true };
        protocolDecl.Properties.Add(new PropertyDecl
        {
            Name = "valueType",
            SwiftTypeSpec = anyExistential,
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("valueType_get") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });

        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("public object ValueType", output);
    }

    [Fact]
    public void EmitProxyClass_CrossModuleExistentialProperty_QualifiesInterfaceAndProxy()
    {
        // A proxy emitted in TestModule for a protocol whose property is an Optional
        // existential `any DepModule.Shape?` must namespace-qualify the cross-module
        // interface (DepModule.IShape) and proxy class (DepModule.SwiftInterop.ShapeProxy)
        // in BOTH the property declaration and the receiver get/set conversions. A bare
        // `IShape` / `ShapeProxy` does not resolve in the TestModule compilation unit
        // (CS0246) and mismatches the interface member return type (CS0738) — the
        // RealityKit/RealityFoundation EntityGestureRecognizer.entity (any HasCollision?)
        // failure surfaced once RealityFoundation began compiling.
        RegisterCrossModuleProtocol("DepModule", "Shape");

        var optExistential = new NamedTypeSpec("Swift.Optional");
        optExistential.GenericParameters.Add(
            new ProtocolListTypeSpec(new[] { new NamedTypeSpec("DepModule.Shape") }));

        var protocolDecl = CreateProtocolWithProperty(
            "ShapeHolder", "shape", hasGetter: true, hasSetter: true, optExistential);

        var output = EmitProxyClass(protocolDecl);

        // Property declaration + any emitted reference is namespace-qualified.
        Assert.Contains("DepModule.IShape", output);
        Assert.Contains("public DepModule.IShape? Shape", output);
        // Regression guards: no bare unqualified forms survive. Each would compile-fail
        // in the consuming module; the qualified forms (DepModule.IShape, ...SwiftInterop.ShapeProxy)
        // do not contain these substrings. Guard the bare property-declaration form as
        // `public IShape?` (the unqualified `Optional<any Shape>` type) — NOT `public IShape`,
        // which spuriously matches the proxy's own same-module interface `public IShapeHolder`
        // appearing in B2 lifetime comments.
        Assert.DoesNotContain("public IShape?", output);
        Assert.DoesNotContain("GetOrCreate<IShape>", output);
        Assert.DoesNotContain("new ShapeProxy(", output);
        Assert.DoesNotContain("(IShape?)", output);
    }

    [Fact]
    public void EmitProxyClass_WithOptionalExistentialGeneric_UsesAnyTypeFallback()
    {
        var protocolDecl = CreateSimpleProtocol("OptionalExistentialProtocol");
        _typeDatabase.AddOutOfModuleTypes(new[]
        {
            (SwiftTypeName.FromModuleQualifiedName("TestModule.Box"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Box"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Box"),
                MetadataAccessor = "$s10TestModule3BoxVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            })
        });

        var boxedExistential = new NamedTypeSpec("TestModule.Box");
        boxedExistential.GenericParameters.Add(
            new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.P1") }));

        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "update",
            MangledName = "$s10TestModule26OptionalExistentialProtocolP6updateyyAA3BoxVyAA2P1_pGF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                },
                new()
                {
                    Name = "value",
                    PrivateName = "value",
                    SwiftTypeSpec = boxedExistential,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        });

        var output = EmitProxyClass(protocolDecl);

        // "value" is no longer sanitized — it's valid as a parameter name in all positions we generate
        Assert.Contains("public void Update(TestModule.Box<Swift.AnyType> value)", output);
    }

    [Fact]
    public void EmitProxyClass_ClosureAndArrayParamsSameResolvedKey_EmitsSingleMethod()
    {
        // G6 bug shape: two methods with the same name but different Swift parameter types —
        // a closure param and an array param — that both resolve to AnyType via
        // GetTypeRecordOrAnyType (ClosureTypeSpec → default AnyType, unregistered
        // NamedTypeSpec("Swift.Array<...>") → AnyType).
        // Before G6, raw GetMethodKey used Swift type ToString() which produced different
        // keys ("(Swift.Int) -> ()" vs "Swift.Array<Swift.Double>"), emitting duplicates.
        // G6 fix: ProtocolSignatureHelper.GetMethodSignatureKey resolves through TypeDatabase,
        // normalizing both to "Swift.AnyType" → same key → single method emitted.
        // Note: ProtocolHandler (interface declaration) uses the same GetMethodSignatureKey,
        // so interface dedup is implicitly covered by testing the same key function here.
        var protocolDecl = CreateSimpleProtocol("DedupProtocol");

        // Method 1: param is a closure (ClosureTypeSpec → _ => AnyType in GetTypeRecordOrAnyType)
        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "update",
            MangledName = "$supdate_closure",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty, PrivateName = string.Empty,
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = null
                },
                new()
                {
                    Name = "handler", PrivateName = "handler",
                    SwiftTypeSpec = new ClosureTypeSpec(
                        new TupleTypeSpec(new NamedTypeSpec("Swift.Int")),
                        TupleTypeSpec.Empty),
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = false, IsAsync = false,
            IsSynthesizedAccessor = false
        });

        // Method 2: param is an array (unregistered NamedTypeSpec → AnyType via TypeDatabase miss)
        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "update",
            MangledName = "$supdate_array",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty, PrivateName = string.Empty,
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = null
                },
                new()
                {
                    Name = "items", PrivateName = "items",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.Double")),
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = false, IsAsync = false,
            IsSynthesizedAccessor = false
        });

        var output = EmitProxyClass(protocolDecl);

        // Both params resolve to "Swift.AnyType" via ProtocolSignatureHelper →
        // same key "update(Swift.AnyType)" → only one proxy class method emitted
        Assert.Equal(1, EmitterTestHelpers.CountOccurrences(output, "public void Update("));

        // Verify receiver count matches interface method count (no orphaned receivers).
        // Before the fix, receivers used GetMethodKey (ToString-based) producing different keys
        // for closure vs array params, while interface used GetMethodSignatureKey (TypeDB-based)
        // collapsing both to AnyType. This mismatch caused orphaned receivers → CS1503.
        var receiverCount = EmitterTestHelpers.CountOccurrences(output, "static void Receive_update_");
        Assert.Equal(1, receiverCount);
    }

    [Fact]
    public void EmitProxyClass_ClosureAndArrayParams_ReceiverMatchesInterfaceDedup()
    {
        // Two methods "finish(output:)" (closure param) and "finish(withBytes:)" (array param)
        // both resolve to AnyType through ProtocolSignatureHelper. Interface dedup correctly
        // emits a single method. After H2 fix, receiver/vtable/staticinit also use the same
        // key function, so receiver count matches interface count (1, not 2).
        var protocolDecl = CreateSimpleProtocol("UpdatableProtocol");

        // Method 1: closure param → AnyType
        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "finish",
            MangledName = "$sfinish_closure",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty, PrivateName = string.Empty,
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = null
                },
                new()
                {
                    Name = "output", PrivateName = "output",
                    SwiftTypeSpec = new ClosureTypeSpec(
                        new TupleTypeSpec(new NamedTypeSpec("Swift.UInt8")),
                        TupleTypeSpec.Empty),
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = false, IsAsync = false,
            IsSynthesizedAccessor = false
        });

        // Method 2: array param → AnyType
        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "finish",
            MangledName = "$sfinish_array",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty, PrivateName = string.Empty,
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = null
                },
                new()
                {
                    Name = "withBytes", PrivateName = "withBytes",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.UInt8")),
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = false, IsAsync = false,
            IsSynthesizedAccessor = false
        });

        var output = EmitProxyClass(protocolDecl);

        // Single interface method emitted (both collapse to same key)
        Assert.Equal(1, EmitterTestHelpers.CountOccurrences(output, "public void Finish("));
        // Single receiver emitted (consistent dedup with interface)
        Assert.Equal(1, EmitterTestHelpers.CountOccurrences(output, "static void Receive_finish_"));
    }

    [Fact]
    public void EmitProxyClass_TwoExistentialOverloadsSameRawKey_EmitsSingleReceiver()
    {
        // FirebaseFirestore regression (S20): a protocol carries two overloads of the SAME
        // method name whose params are DIFFERENT existentials — `record(any TagA)` and
        // `record(any TagB)`. The three protocol key functions diverge on this shape:
        //   • ProtocolSignatureHelper.GetMethodSignatureKey resolves each param via
        //     GetTypeRecordOrAnyType — an existential ProtocolListTypeSpec is NOT understood
        //     there, so BOTH collapse to "record(Swift.AnyType)". ProtocolHandler dedups on
        //     this key → the interface emits exactly ONE method (Record(ITagA), first by
        //     declaration order). This mirrors Firestore's add(any Expression)/add(any Sendable).
        //   • EveryProtocolEmitter.GetMethodKey (vtable layout / slot allocation) keys off the
        //     RAW Swift type, which is distinct (TagA vs TagB) → TWO witness slots.
        //   • GetProjectedCSharpMethodKey routes through ProjectTypeToCSharp's existential
        //     fallback → distinct projected keys (ITagA vs ITagB), so the projected-key dedup
        //     in the receiver/static-init loops does NOT collapse them.
        // Before the fix the receiver + static-init loops therefore emitted a SECOND receiver
        // (Receive_record_1) dispatching to a non-existent Record(ITagB) overload → CS1503 in
        // the generated binding. The fix adds a raw-signature dedup (emittedRawKeys, keyed on
        // GetMethodSignatureKey) to both loops so only the surviving (first) overload's receiver
        // is emitted; the collapsed slot is left null (the documented fillability model — the
        // vtable struct is still sized for both slots, matching Swift's witness table).
        // The protocols are REGISTERED so the factory-first ProjectTypeToCSharp (used by
        // GetProjectedCSharpMethodKey) resolves each existential to its DISTINCT interface
        // (ITagA / ITagB). GetMethodSignatureKey, by contrast, reads GetTypeRecordOrAnyType on
        // the ProtocolListTypeSpec — not a named lookup, so it returns AnyType for both
        // regardless of registration. That asymmetry is the whole bug.
        RegisterProtocol("TagA");
        RegisterProtocol("TagB");
        var protocol = CreateSimpleProtocol("OverloadCollapseProto");

        var first = CreateMethodDecl("record");
        first.CSSignature.Add(new ArgumentDecl
        {
            Name = "value", PrivateName = "value",
            SwiftTypeSpec = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.TagA") }),
            IsGeneric = false, IsInOut = false, ParentDecl = null, ModuleDecl = null
        });
        protocol.Methods.Add(first);

        var second = CreateMethodDecl("record");
        second.CSSignature.Add(new ArgumentDecl
        {
            Name = "value", PrivateName = "value",
            SwiftTypeSpec = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.TagB") }),
            IsGeneric = false, IsInOut = false, ParentDecl = null, ModuleDecl = null
        });
        protocol.Methods.Add(second);

        var output = EmitProxyClass(protocol);

        // The interface collapses both overloads to one method; the reverse-dispatch receiver
        // and its static-init wiring must follow suit — exactly ONE receiver, no orphan.
        Assert.Equal(1, EmitterTestHelpers.CountOccurrences(output, "static void Receive_record_"));
        // ...and exactly ONE local-vtable assignment wiring a receiver into the struct.
        Assert.Equal(1, EmitterTestHelpers.CountOccurrences(output, "= &Receive_record_"));
    }

    [Fact]
    public void EmitProxyClass_CrossModuleParent_TwoExistentialOverloadsSameRawKey_EmitsSingleReceiver()
    {
        // Cross-module sibling of EmitProxyClass_TwoExistentialOverloadsSameRawKey_EmitsSingleReceiver.
        // Here the collapsing existential-overload pair (`record(any TagA)` / `record(any TagB)`)
        // lives on a PARENT protocol in a DIFFERENT module that the child (in TestModule) inherits
        // across the boundary. The child proxy's cctor populates the cross-module parent's local
        // vtable via EmitCrossModuleParentVtableInit (ProtocolProxyEmitter.StaticInit.cs). That
        // loop shares the SAME GetMethodSignatureKey-collapse / GetProjectedCSharpMethodKey-diverge
        // asymmetry as the same-module child path, and the receiver emitter (EmitReceiverMethods,
        // reached here with applyVtableMembershipFilter: true) already dedups on the raw key →
        // exactly ONE `Receive_record_`. Without the matching raw-key dedup in the cross-module
        // local-vtable loop the initializer emits `Func_record_1 = &Receive_record_1`, an orphan
        // reference to a receiver the deduped emitter never wrote (CS0103 in the generated binding).
        // The collapse is keyed on the method's existential params (registered TagA/TagB project to
        // distinct C# interfaces, while GetMethodSignatureKey reads them as Swift.AnyType), so the
        // declaring protocol need only live cross-module; its own registration is incidental.
        RegisterProtocol("TagA");
        RegisterProtocol("TagB");
        RegisterCrossModuleProtocol("OtherModule", "ParentProto");

        var parentModule = new ModuleDecl
        {
            Name = "OtherModule",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var parent = CreateSimpleProtocol("ParentProto");
        parent.SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("OtherModule.ParentProto");
        parent.ModuleDecl = parentModule;

        var first = CreateMethodDecl("record");
        first.CSSignature.Add(new ArgumentDecl
        {
            Name = "value", PrivateName = "value",
            SwiftTypeSpec = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.TagA") }),
            IsGeneric = false, IsInOut = false, ParentDecl = null, ModuleDecl = null
        });
        parent.Methods.Add(first);

        var second = CreateMethodDecl("record");
        second.CSSignature.Add(new ArgumentDecl
        {
            Name = "value", PrivateName = "value",
            SwiftTypeSpec = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.TagB") }),
            IsGeneric = false, IsInOut = false, ParentDecl = null, ModuleDecl = null
        });
        parent.Methods.Add(second);

        var childModule = new ModuleDecl
        {
            Name = "TestModule",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
        childModule.DependencyProtocols["OtherModule"] = new List<ProtocolDecl> { parent };

        var child = CreateSimpleProtocol("ChildProto");
        child.ModuleDecl = childModule;
        child.InheritedProtocols.Add(new NamedTypeSpec("OtherModule.ParentProto"));

        var output = EmitProxyClass(child);

        // The cross-module parent scaffolding must collapse the existential-overload pair to ONE
        // receiver + ONE local-vtable assignment, exactly like the same-module path.
        Assert.Equal(1, EmitterTestHelpers.CountOccurrences(output, "static void Receive_record_"));
        Assert.Equal(1, EmitterTestHelpers.CountOccurrences(output, "= &Receive_record_"));
    }

    #endregion

    #region Swift Existential Degradation Tests

    [Fact]
    public void EmitProxyClass_BlittablePropertyGetter_RegisteredType_EmitsWitnessDispatch()
    {
        // With a properly registered type, the projected type is blittable (int)
        // and dispatch should be enabled.
        RegisterSwiftInt32();
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false, new NamedTypeSpec("Swift.Int32"));
        var output = EmitProxyClass(protocolDecl);

        // Blittable property getter should dispatch via P/Invoke, not throw
        Assert.Contains("NativeMethods.SBW_TestProtocol_get_value_0", output);
        Assert.Contains("MarshalFromSwift<", output);
        Assert.Contains("NativeMethods.SBW_TestProtocol_free_get_value_0", output);
        Assert.Contains("fixed (ExistentialContainer1*", output);
    }

    [Fact]
    public void EmitProxyClass_BlittableSwiftProperty_ProjectedNonBlittable_DisablesDispatch()
    {
        // When TypeDatabase is incomplete, Swift.Int projects to Swift.AnyType in C#.
        // Even though the Swift type is blittable, returning MarshalFromSwift<nint>
        // from a Swift.AnyType property would be a type mismatch.
        // Dispatch must be disabled — fall back to NotSupportedException.
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        // Projected type is Swift.AnyType (not blittable) — dispatch disabled
        Assert.Contains("Cannot get property 'Value'", output);
        Assert.DoesNotContain("NativeMethods.SBW_TestProtocol_get_value_0((IntPtr)containerPtr)", output);
    }

    [Fact]
    public void EmitProxyClass_ThrowingBlittableMethod_EmitsDispatchWithErrorOut()
    {
        RegisterSwiftInt32();
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "tryGetValue",
            MangledName = "$stryGetValue",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = true,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int32"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsSynthesizedAccessor = false
        });
        var output = EmitProxyClass(protocolDecl);

        // Throwing blittable methods now dispatch via P/Invoke with error-out
        Assert.Contains("SBW_TestProtocol_method_tryGetValue", output);
        Assert.Contains("resultPtr == IntPtr.Zero", output);
        Assert.Contains("SwiftException", output);
        Assert.DoesNotContain("Cannot call method 'TryGetValue'", output);
    }

    [Fact]
    public void EmitProxyClass_ThrowingMethod_ProjectedNonBlittable_DisablesDispatch()
    {
        // Without TypeDatabase registration, Swift.Int projects to Swift.AnyType (non-blittable).
        // Even though ClassifyMethodDispatch returns ThrowingBlittableOrString (Swift-side check passes),
        // the secondary C#-side validation must catch the degraded projection and fall back to SB0003.
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "tryGetValue",
            MangledName = "$stryGetValue",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = true,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsSynthesizedAccessor = false
        });
        var output = EmitProxyClass(protocolDecl);

        // Projected type is AnyType (not blittable) — dispatch disabled despite throwing classification
        Assert.Contains("SB0003", output);
        Assert.Contains("Cannot call method 'TryGetValue'", output);
        Assert.DoesNotContain("SwiftException", output);
    }

    [Fact]
    public void EmitProxyClass_AsyncMethod_EmitsNotSupportedException()
    {
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "fetchValue",
            MangledName = "$sfetchValue",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = true,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsSynthesizedAccessor = false
        });
        var output = EmitProxyClass(protocolDecl);

        // Async methods should NOT be dispatched, even with blittable types
        Assert.Contains("Cannot call method 'FetchValueAsync'", output);
        Assert.DoesNotContain("SBW_TestProtocol_method_fetchValue", output);
    }

    [Fact]
    public void EmitProxyClass_AsyncMethodReceiver_UnwrapsTaskBeforeMarshalling()
    {
        // Forward witness dispatch is disabled for async (test above), but the REVERSE-dispatch
        // receiver still satisfies the async requirement through the sync-ABI witness slot: the
        // C# impl returns Task<T> while the Swift witness reads the unwrapped T. The receiver must
        // block the Task and marshal T — emitting MarshalToSwiftBuffer(Task<T>) directly hands
        // Swift a managed Task object header where it expects the value, silently corrupting the
        // return ABI. Asserts the unwrap is present and that the marshalled value is `result`
        // (the unwrapped value), never the Task.
        RegisterSwiftInt32();
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "fetchValue",
            MangledName = "$sfetchValue",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = true,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int32"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsSynthesizedAccessor = false
        });
        var output = EmitProxyClass(protocolDecl);

        var receiverBody = ExtractMethodBody(output, "private static IntPtr Receive_fetchValue_0(");
        // The Task is blocked synchronously so `result` is the unwrapped value the sync witness reads.
        Assert.Contains(".GetAwaiter().GetResult()", receiverBody);
        Assert.Contains("MarshalToSwiftBuffer(result)", receiverBody);
        // The impl call itself must carry the unwrap (not a bare Task assignment). With the fix the
        // call reads `impl.FetchValueAsync().GetAwaiter().GetResult()`, so the bare-Task form
        // `FetchValueAsync();` (call immediately terminated) must never appear.
        Assert.Contains("impl.FetchValueAsync()", receiverBody);
        Assert.DoesNotContain("FetchValueAsync();", receiverBody);
    }

    [Fact]
    public void EmitProxyClass_AsyncReceiver_FailFastsWithMemberName_SyncReceiverKeepsPlainFailFast()
    {
        // Finding 36: the async receiver blocks the Task on the synchronously-blocked reverse-dispatch
        // slot (upstream Issue 1) and has no Swift error channel, so any escape — cancellation or
        // otherwise — is process-terminating. The async close must therefore be member-named (loud,
        // attributable) and split the cancellation case out, while a SIBLING sync receiver in the same
        // proxy keeps the anonymous plain FailFast. This pins the `method.IsAsync` close selection so a
        // refactor can't silently regress either receiver onto the other's policy.
        RegisterSwiftInt32();
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "fetchValue",
            MangledName = "$sfetchValue",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = true,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int32"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsSynthesizedAccessor = false
        });
        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "refresh",
            MangledName = "$srefresh",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int32"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsSynthesizedAccessor = false
        });
        var output = EmitProxyClass(protocolDecl);

        var asyncBody = ExtractMethodBody(output, "private static IntPtr Receive_fetchValue_0(");
        // Member-named, both arms, cancellation split out.
        Assert.Contains("FailFastAsyncWitnessCancellation", asyncBody);
        Assert.Contains("FailFastAsyncWitnessException", asyncBody);
        Assert.Contains("\"TestProtocol.fetchValue\"", asyncBody);
        Assert.Contains("catch (global::System.OperationCanceledException", asyncBody);
        // The async receiver must NOT fall back to the anonymous sync FailFast.
        Assert.DoesNotContain("FailFastUnhandledClosureException", asyncBody);

        var syncBody = ExtractMethodBody(output, "private static IntPtr Receive_refresh_1(");
        // The sync receiver keeps the plain (anonymous) FailFast and never adopts the async policy.
        Assert.Contains("FailFastUnhandledClosureException", syncBody);
        Assert.DoesNotContain("FailFastAsyncWitness", syncBody);
        Assert.DoesNotContain("OperationCanceledException", syncBody);
    }

    [Fact]
    public void EmitProxyClass_ThrowingVoidMethod_EmitsErrorOutCheck()
    {
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "disconnect",
            MangledName = "$sdisconnect",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = true,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsSynthesizedAccessor = false
        });
        var output = EmitProxyClass(protocolDecl);

        // Throwing void methods dispatch with errorOut check
        Assert.Contains("SBW_TestProtocol_method_disconnect", output);
        Assert.Contains("if (errorOut != IntPtr.Zero)", output);
        Assert.Contains("SwiftException", output);
        Assert.DoesNotContain("Cannot call method 'Disconnect'", output);
    }

    [Fact]
    public void EmitProxyClass_ThrowingStringMethod_EmitsUtf8DecodeWithError()
    {
        RegisterSwiftString();
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "tryGetName",
            MangledName = "$stryGetName",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = true,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsSynthesizedAccessor = false
        });
        var output = EmitProxyClass(protocolDecl);

        // Throwing string methods dispatch with error check + UTF-8 decode
        Assert.Contains("SBW_TestProtocol_method_tryGetName", output);
        Assert.Contains("resultPtr == IntPtr.Zero", output);
        Assert.Contains("Encoding.UTF8.GetString", output);
        Assert.Contains("SwiftException", output);
        Assert.DoesNotContain("Cannot call method", output);
    }

    [Fact]
    public void EmitProxyClass_ThrowingBlittableMethod_EmitsPInvokeWithErrorOut()
    {
        RegisterSwiftInt32();
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "tryGetBool",
            MangledName = "$stryGetBool",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = true,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Bool"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsSynthesizedAccessor = false
        });
        var output = EmitProxyClass(protocolDecl);

        // P/Invoke should have errorOut parameter
        Assert.Contains("SBW_TestProtocol_method_tryGetBool", output);
        Assert.Contains("SBW_GetErrorDescription", output);
        Assert.Contains("SBW_ReleaseError", output);
    }

    [Fact]
    public void EmitProxyClass_StringPropertyGetter_RegisteredType_EmitsUtf8SliceDispatch()
    {
        RegisterSwiftString();
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "name", hasGetter: true, hasSetter: false, new NamedTypeSpec("Swift.String"));
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("NativeMethods.SBW_TestProtocol_get_name_0", output);
        Assert.Contains("Utf8Slice", output);
        Assert.Contains("Encoding.UTF8.GetString", output);
        Assert.Contains("return str;", output);
        Assert.DoesNotContain("Cannot get property 'Name'", output);
    }

    [Fact]
    public void EmitProxyClass_StringPropertyGetter_NoTypeDB_StillUsesIdiomaticDispatch()
    {
        // TypeConversionHandler recognizes Swift.String by name (not via TypeDB registration),
        // so idiomatic string dispatch is used even without explicit TypeDB registration.
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "name", hasGetter: true, hasSetter: false, new NamedTypeSpec("Swift.String"));
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("Encoding.UTF8.GetString", output);
        Assert.Contains("return str;", output);
    }

    [Fact]
    public void EmitProxyClass_PropertySetter_BlittableSwift_EmitsNotSupportedWithoutTypeDB()
    {
        // Without TypeDatabase registration, Swift.Int projects to Swift.AnyType (non-blittable)
        // so setter dispatch is disabled — falls back to NotSupportedException
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: true);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("Cannot set property 'Value'", output);
    }

    [Fact]
    public void EmitProxyClass_BlittableMethodWithReturn_RegisteredType_EmitsWitnessDispatch()
    {
        // With a properly registered type, the projected return type is blittable
        // and dispatch should be enabled.
        RegisterSwiftInt32();
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "getValue",
            MangledName = "$sgetValue",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int32"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        });
        var output = EmitProxyClass(protocolDecl);

        // Blittable method should dispatch via P/Invoke
        Assert.Contains("NativeMethods.SBW_TestProtocol_method_getValue_0", output);
        Assert.Contains("NativeMethods.SBW_TestProtocol_free_method_getValue_0", output);
        Assert.Contains("MarshalFromSwift<", output);
        Assert.DoesNotContain("Cannot call method 'GetValue'", output);
    }

    [Fact]
    public void EmitProxyClass_BlittableSwiftMethodReturn_ProjectedNonBlittable_DisablesDispatch()
    {
        // When TypeDatabase is incomplete, Swift.Int projects to Swift.AnyType.
        // Even though the Swift return type is blittable, the C# method signature
        // would return Swift.AnyType while dispatch emits MarshalFromSwift<nint> —
        // a type mismatch. Dispatch must be disabled.
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "getValue",
            MangledName = "$sgetValue",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        });
        var output = EmitProxyClass(protocolDecl);

        // Projected return type is Swift.AnyType (not blittable) — dispatch disabled
        Assert.Contains("Cannot call method 'GetValue'", output);
        Assert.DoesNotContain("NativeMethods.SBW_TestProtocol_method_getValue", output);
    }

    [Fact]
    public void EmitProxyClass_StringMethodWithReturn_EmitsUtf8SliceDispatch()
    {
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "getName",
            MangledName = "$sgetName",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        });
        var output = EmitProxyClass(protocolDecl);

        // String method return should dispatch via Utf8Slice
        Assert.Contains("NativeMethods.SBW_TestProtocol_method_getName_0", output);
        Assert.Contains("Utf8Slice", output);
        Assert.Contains("Encoding.UTF8.GetString", output);
        Assert.DoesNotContain("Cannot call method 'GetName'", output);
    }

    [Fact]
    public void EmitProxyClass_VoidMethod_EmitsWitnessDispatch()
    {
        var protocolDecl = CreateProtocolWithMethod("TestProtocol", "doSomething");
        var output = EmitProxyClass(protocolDecl);

        // Void method with no params is dispatchable (all types are blittable — there are none)
        Assert.Contains("NativeMethods.SBW_TestProtocol_method_doSomething_0", output);
        Assert.DoesNotContain("Cannot call method 'DoSomething'", output);
    }

    [Fact]
    public void EmitProxyClass_SubscriptGetterSetter_EmitsNotSupportedException()
    {
        var protocolDecl = CreateSimpleProtocol("IndexedProtocol");
        protocolDecl.Subscripts.Add(new SubscriptDecl
        {
            Name = "subscript",
            MangledName = "$s7IndexedP9subscriptS2icig",
            ReturnTypeSpec = new NamedTypeSpec("Swift.Int"),
            IndexParameters = new List<ArgumentDecl>
            {
                new()
                {
                    Name = "index",
                    PrivateName = "index",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            IsStatic = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("subscript_get") },
                new SetAccessorDecl { Method = CreateMethodDecl("subscript_set") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("Cannot get subscript", output);
        Assert.Contains("Cannot set subscript", output);
    }

    [Fact]
    public void EmitProxyClass_ConformanceDescriptor_EmitsNotSupportedException()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("throw new NotSupportedException(", output);
        Assert.Contains("Protocol conformance descriptor is not available for proxy types", output);
        Assert.Contains("EveryProtocol's witness table", output);
    }

    [Fact]
    public void EmitProxyClass_ZeroNotImplementedExceptions()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: true);
        var output = EmitProxyClass(protocolDecl);

        Assert.DoesNotContain("NotImplementedException", output);
    }

    [Fact]
    public void EmitProxyClass_ExistentialConstructorXmlDoc_MentionsDispatchCapabilities()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("<remarks>", output);
        Assert.Contains("blittable and String", output);
        Assert.Contains("witness table accessors", output);
    }

    #endregion

    #region Dispose and Lifecycle Tests

    [Fact]
    public void EmitProxyClass_HasDisposedField()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("private bool _disposed;", output);
    }

    [Fact]
    public void EmitProxyClass_DisposeUnregistersFromSwiftObjectRegistry()
    {
        // Dispose unregisters the proxy from the strong registry. The concern that
        // subsequent Swift callbacks may throw through [UnmanagedCallersOnly]
        // is mitigated by the null-safe receiver guards added in the same fix —
        // see EmitProxyClass_ReceiverGuardsAgainstDeadImpl.
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("SwiftObjectRegistry.Unregister(_everyProtocolHandle)", output);
    }

    [Fact]
    public void EmitProxyClass_DisposeDoesNotReleaseEveryProtocolHandle()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        // ProxyLifetimeTracker (anchored on the impl) owns the +1 release path now.
        // Dispose must NOT call Arc.Release on the handle — that would deallocate
        // the Swift EveryProtocol while in-flight dispatches may still be running.
        var disposeIndex = output.IndexOf("public void Dispose()");
        Assert.NotEqual(-1, disposeIndex);
        // Find the closing brace of Dispose by walking forward a bounded amount.
        var disposeBody = output.Substring(disposeIndex, Math.Min(1200, output.Length - disposeIndex));
        Assert.DoesNotContain("Arc.Release(_everyProtocolHandle)", disposeBody);
        Assert.DoesNotContain("_everyProtocol.Dispose()", disposeBody);
    }

    [Fact]
    public void EmitProxyClass_DisposeIsIdempotent()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("if (_disposed) return;", output);
    }

    [Fact]
    public void EmitProxyClass_PropertyGetterThrowsAfterDispose()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        // Find the property getter body and verify ObjectDisposedException is there
        var getterIdx = output.IndexOf("public Swift.AnyType Value");
        Assert.True(getterIdx >= 0, "Property not found in output");
        var getterSection = output.Substring(getterIdx, Math.Min(500, output.Length - getterIdx));
        Assert.Contains("ObjectDisposedException", getterSection);
    }

    [Fact]
    public void EmitProxyClass_PropertySetterThrowsAfterDispose()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: true);
        var output = EmitProxyClass(protocolDecl);

        // Find the property setter body and verify ObjectDisposedException is there
        var setIdx = output.IndexOf("set\n");
        if (setIdx < 0) setIdx = output.IndexOf("set\r\n");
        Assert.True(setIdx >= 0, "Property setter not found in output");
        var setterSection = output.Substring(setIdx, Math.Min(500, output.Length - setIdx));
        Assert.Contains("ObjectDisposedException", setterSection);
    }

    [Fact]
    public void EmitProxyClass_NotSupportedPropertyStubThrowsAfterDispose()
    {
        // Create a protocol with a property that is in closureSkippedPropertyNames
        var protocolDecl = CreateProtocolWithProperty("StubProtocol", "callback", hasGetter: true, hasSetter: true);

        // Emit with the property name in BOTH skippedPropertyNames and closureSkippedPropertyNames
        // to route through EmitNotSupportedPropertyStub
        var stringWriter = new StringWriter();
        var writer = new CSharpWriter(stringWriter);
        _emitter.EmitProxyClass(writer, protocolDecl,
            skippedPropertyNames: new HashSet<string> { "callback" },
            closureSkippedPropertyNames: new HashSet<string> { "callback" });
        var output = stringWriter.ToString();

        // Find the Callback property stub (skip past the weak-ref _csharpImpl
        // property that is emitted first, before any interface members).
        // The generator pascal-cases "callback" -> "Callback" for the C# name,
        // but the exact member modifiers can vary — just search for " Callback".
        var propertyIdx = output.IndexOf(" Callback\n");
        if (propertyIdx < 0) propertyIdx = output.IndexOf(" Callback ");
        Assert.True(propertyIdx >= 0, "Callback property not found in output");
        var propertySection = output.Substring(propertyIdx, Math.Min(1500, output.Length - propertyIdx));

        // Find the property and verify ObjectDisposedException guard in getter
        var getterIdx = propertySection.IndexOf("get\n");
        if (getterIdx < 0) getterIdx = propertySection.IndexOf("get\r\n");
        Assert.True(getterIdx >= 0, "Property getter stub not found in output");
        var getterSection = propertySection.Substring(getterIdx, Math.Min(500, propertySection.Length - getterIdx));
        Assert.Contains("ObjectDisposedException", getterSection);
        // Guard must appear before NotSupportedException
        var disposeIdx = getterSection.IndexOf("ObjectDisposedException");
        var notSupportedIdx = getterSection.IndexOf("NotSupportedException");
        Assert.True(notSupportedIdx >= 0, "NotSupportedException not found in getter stub");
        Assert.True(disposeIdx < notSupportedIdx, "ObjectDisposedException guard must come before NotSupportedException in getter");

        // Verify ObjectDisposedException guard in setter
        var setIdx = propertySection.IndexOf("set\n");
        if (setIdx < 0) setIdx = propertySection.IndexOf("set\r\n");
        Assert.True(setIdx >= 0, "Property setter stub not found in output");
        var setterSection = propertySection.Substring(setIdx, Math.Min(500, propertySection.Length - setIdx));
        Assert.Contains("ObjectDisposedException", setterSection);
        var setDisposeIdx = setterSection.IndexOf("ObjectDisposedException");
        var setNotSupportedIdx = setterSection.IndexOf("NotSupportedException");
        Assert.True(setNotSupportedIdx >= 0, "NotSupportedException not found in setter stub");
        Assert.True(setDisposeIdx < setNotSupportedIdx, "ObjectDisposedException guard must come before NotSupportedException in setter");
    }

    [Fact]
    public void EmitProxyClass_MethodThrowsAfterDispose()
    {
        var protocolDecl = CreateProtocolWithMethod("TestProtocol", "doSomething");
        var output = EmitProxyClass(protocolDecl);

        // Find the method body and verify ObjectDisposedException is there
        var methodIdx = output.IndexOf("public void DoSomething()");
        Assert.True(methodIdx >= 0, "Method not found in output");
        var methodSection = output.Substring(methodIdx, Math.Min(500, output.Length - methodIdx));
        Assert.Contains("ObjectDisposedException", methodSection);
    }

    [Fact]
    public void EmitProxyClass_SubscriptThrowsAfterDispose()
    {
        var protocolDecl = CreateSimpleProtocol("IndexedProtocol");
        protocolDecl.Subscripts.Add(new SubscriptDecl
        {
            Name = "subscript",
            MangledName = "$s7IndexedP9subscriptS2icig",
            ReturnTypeSpec = new NamedTypeSpec("Swift.Int"),
            IndexParameters = new List<ArgumentDecl>
            {
                new()
                {
                    Name = "index",
                    PrivateName = "index",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            IsStatic = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("subscript_get") },
                new SetAccessorDecl { Method = CreateMethodDecl("subscript_set") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });
        var output = EmitProxyClass(protocolDecl);

        // Find the subscript getter body and verify ObjectDisposedException guard
        var subscriptIdx = output.IndexOf("public Swift.AnyType this[");
        Assert.True(subscriptIdx >= 0, "Subscript not found in output");
        var subscriptSection = output.Substring(subscriptIdx, Math.Min(800, output.Length - subscriptIdx));
        Assert.Contains("ObjectDisposedException", subscriptSection);
    }

    [Fact]
    public void EmitProxyClass_NotSupportedMethodStubThrowsAfterDispose()
    {
        // Create a protocol with a method that routes through EmitNotSupportedMethodStub
        var protocolDecl = CreateProtocolWithMethod("StubMethodProtocol", "apply");

        // Get the method signature key to populate skipped sets
        var method = protocolDecl.Methods.First(m => m.Name == "apply");
        var methodKey = ProtocolSignatureHelper.GetMethodSignatureKey(method, _typeDatabase, protocolDecl);

        // Emit with the method key in BOTH skippedMethodKeys and closureSkippedMethodKeys
        // to route through EmitNotSupportedMethodStub
        var stringWriter = new StringWriter();
        var writer = new CSharpWriter(stringWriter);
        _emitter.EmitProxyClass(writer, protocolDecl,
            skippedMethodKeys: new HashSet<string> { methodKey },
            closureSkippedMethodKeys: new HashSet<string> { methodKey });
        var output = stringWriter.ToString();

        // Find the method stub and verify ObjectDisposedException is emitted before _csharpImpl
        var methodIdx = output.IndexOf("public void Apply(");
        Assert.True(methodIdx >= 0, "Method stub not found in output");
        var methodSection = output.Substring(methodIdx, Math.Min(500, output.Length - methodIdx));
        Assert.Contains("ObjectDisposedException", methodSection);
        // Guard must appear before _csharpImpl check
        var disposeIdx = methodSection.IndexOf("ObjectDisposedException");
        var implIdx = methodSection.IndexOf("_csharpImpl");
        Assert.True(disposeIdx < implIdx, "ObjectDisposedException guard must come before _csharpImpl check");
        // Should also contain NotSupportedException (this is a stub)
        Assert.Contains("NotSupportedException", methodSection);
    }

    [Fact]
    public void EmitProxyClass_MarshalToSwiftThrowsAfterDispose()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        // Find MarshalToSwift body and verify ObjectDisposedException guard
        var marshalIdx = output.IndexOf("public int MarshalToSwift(");
        Assert.True(marshalIdx >= 0, "MarshalToSwift not found in output");
        var marshalSection = output.Substring(marshalIdx, Math.Min(500, output.Length - marshalIdx));
        Assert.Contains("ObjectDisposedException", marshalSection);
    }

    [Fact]
    public void EmitProxyClass_GetExistentialContainerThrowsAfterDispose()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        // E2: GetExistentialContainer is now an explicit interface implementation (hidden from public API)
        var containerIdx = output.IndexOf("ISwiftExistentialConvertible<ExistentialContainer1>.GetExistentialContainer()");
        Assert.True(containerIdx >= 0, "GetExistentialContainer explicit interface impl not found in output");
        var containerSection = output.Substring(containerIdx, Math.Min(500, output.Length - containerIdx));
        Assert.Contains("ObjectDisposedException", containerSection);
    }

    #endregion

    #region Witness Dispatch P/Invoke Tests

    [Fact]
    public void EmitProxyClass_BlittableGetter_GeneratesPInvokeDeclaration()
    {
        RegisterSwiftInt32();
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false, new NamedTypeSpec("Swift.Int32"));
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("EntryPoint = \"SBW_TestProtocol_get_value_0\"", output);
        Assert.Contains("public static partial IntPtr SBW_TestProtocol_get_value_0(IntPtr containerPtr)", output);
    }

    [Fact]
    public void EmitProxyClass_BlittableGetter_GeneratesFreePInvoke()
    {
        RegisterSwiftInt32();
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false, new NamedTypeSpec("Swift.Int32"));
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("EntryPoint = \"SBW_TestProtocol_free_get_value_0\"", output);
        Assert.Contains("public static partial void SBW_TestProtocol_free_get_value_0(IntPtr ptr)", output);
    }

    [Fact]
    public void EmitProxyClass_BlittableGetter_UsesCdeclCallingConvention()
    {
        RegisterSwiftInt32();
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false, new NamedTypeSpec("Swift.Int32"));
        var output = EmitProxyClass(protocolDecl);

        // Both accessor and free should use Cdecl via UnmanagedCallConv attribute
        var lines = output.Split('\n').Select(l => l.Trim()).ToArray();

        // Verify accessor has its own UnmanagedCallConv + LibraryImport pair
        var accessorLibraryImportIdx = Array.FindIndex(lines, l => l.Contains("LibraryImport") && l.Contains("SBW_TestProtocol_get_value_0"));
        Assert.True(accessorLibraryImportIdx > 0, "Accessor LibraryImport not found");
        Assert.Contains("CallConvCdecl", lines[accessorLibraryImportIdx - 1]);

        // Verify free has its own UnmanagedCallConv + LibraryImport pair
        var freeLibraryImportIdx = Array.FindIndex(lines, l => l.Contains("LibraryImport") && l.Contains("SBW_TestProtocol_free_get_value_0"));
        Assert.True(freeLibraryImportIdx > 0, "Free LibraryImport not found");
        Assert.Contains("CallConvCdecl", lines[freeLibraryImportIdx - 1]);
    }

    [Fact]
    public void EmitProxyClass_BlittableMethod_GeneratesPInvokeDeclaration()
    {
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "getValue",
            MangledName = "$sgetValue",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        });
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("EntryPoint = \"SBW_TestProtocol_method_getValue_0\"", output);
        Assert.Contains("EntryPoint = \"SBW_TestProtocol_free_method_getValue_0\"", output);
    }

    [Fact]
    public void EmitProxyClass_BlittableGetter_UsesFixedContainerPattern()
    {
        RegisterSwiftInt32();
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false, new NamedTypeSpec("Swift.Int32"));
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("fixed (ExistentialContainer1* containerPtr = &_swiftContainer)", output);
        Assert.Contains("(IntPtr)containerPtr", output);
    }

    [Fact]
    public void EmitProxyClass_BlittableGetter_UsesTryFinally()
    {
        RegisterSwiftInt32();
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false, new NamedTypeSpec("Swift.Int32"));
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("try {", output);
        Assert.Contains("finally {", output);
    }

    [Fact]
    public void EmitProxyClass_StringGetter_RegisteredType_GeneratesPInvoke()
    {
        RegisterSwiftString();
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "name", hasGetter: true, hasSetter: false, new NamedTypeSpec("Swift.String"));
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("SBW_TestProtocol_get_name_0", output);
        Assert.Contains("SBW_TestProtocol_free_get_name_0", output);
    }

    [Fact]
    public void EmitProxyClass_BlittableSetter_RegisteredType_EmitsDispatch()
    {
        // With a properly registered type, setters should dispatch
        RegisterSwiftInt32();
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: true, new NamedTypeSpec("Swift.Int32"));
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("NativeMethods.SBW_TestProtocol_set_value_0", output);
        Assert.DoesNotContain("Cannot set property 'Value'", output);
    }

    [Fact]
    public void EmitProxyClass_StringSetter_RegisteredType_EmitsUtf8SliceDispatch()
    {
        RegisterSwiftString();
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "name", hasGetter: true, hasSetter: true, new NamedTypeSpec("Swift.String"));
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("NativeMethods.SBW_TestProtocol_set_name_0", output);
        Assert.Contains("Encoding.UTF8.GetBytes", output);
        Assert.Contains("Utf8Slice", output);
        Assert.DoesNotContain("Cannot set property 'Name'", output);
    }

    [Fact]
    public void EmitProxyClass_SetterPInvoke_GeneratesDeclaration()
    {
        RegisterSwiftInt32();
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: true, new NamedTypeSpec("Swift.Int32"));
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("EntryPoint = \"SBW_TestProtocol_set_value_0\"", output);
        Assert.Contains("public static partial void SBW_TestProtocol_set_value_0(IntPtr containerPtr, IntPtr valuePtr)", output);
    }

    [Fact]
    public void EmitProxyClass_VoidMethodDispatch_NoPInvokeReturn()
    {
        var protocolDecl = CreateProtocolWithMethod("TestProtocol", "doSomething");
        var output = EmitProxyClass(protocolDecl);

        // The void method dispatch should call NativeMethods directly, no resultPtr
        Assert.Contains("NativeMethods.SBW_TestProtocol_method_doSomething_0", output);
        Assert.DoesNotContain("SBW_TestProtocol_free_method_doSomething_0", output);
    }

    [Fact]
    public void EmitProxyClass_MethodWithBlittableParam_RegisteredType_DispatchEnabled()
    {
        // When the TypeDatabase properly registers a primitive type, the projected
        // C# type is blittable (e.g. int) and dispatch should be enabled.
        _typeDatabase.AddOutOfModuleTypes(new[]
        {
            (SwiftTypeName.FromModuleQualifiedName("Swift.Int32"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int32"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int32"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            })
        });

        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "setValue",
            MangledName = "$ssetValue",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                },
                new()
                {
                    Name = "newValue",
                    PrivateName = "newValue",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int32"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        });
        var output = EmitProxyClass(protocolDecl);

        // Projected type int is blittable → dispatch enabled
        Assert.Contains("NativeMethods.SBW_TestProtocol_method_setValue_0", output);
        Assert.DoesNotContain("Cannot call method 'SetValue'", output);
        Assert.Contains("var arg0Slice = newValue;", output);
    }

    [Fact]
    public void EmitProxyClass_MethodWithBlittableSwiftType_ProjectedNonBlittable_DisablesDispatch()
    {
        // When the TypeDatabase is incomplete, a blittable Swift type (Swift.Int)
        // projects to Swift.AnyType in C#. The dispatch gate must detect this
        // mismatch and fall back to NotSupportedException — otherwise the emitted
        // code would attempt pointer operations on a non-primitive type.
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "setValue",
            MangledName = "$ssetValue",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                },
                // Swift.Int is blittable in Swift, but with empty TypeDatabase
                // projects to Swift.AnyType in C# — not a blittable primitive
                new()
                {
                    Name = "newValue",
                    PrivateName = "newValue",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        });
        var output = EmitProxyClass(protocolDecl);

        // Projected type is Swift.AnyType (not blittable) → dispatch disabled
        Assert.Contains("Cannot call method 'SetValue'", output);
        Assert.DoesNotContain("NativeMethods.SBW_TestProtocol_method_setValue", output);
    }

    [Fact]
    public void EmitProxyClass_MethodWithStringParam_EmitsStringDispatch()
    {
        // A method with a String parameter should now be dispatched via Utf8Slice
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "setName",
            MangledName = "$ssetName",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                },
                new()
                {
                    Name = "name",
                    PrivateName = "name",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        });
        var output = EmitProxyClass(protocolDecl);

        // Should dispatch — String params are now supported via Utf8Slice
        Assert.Contains("NativeMethods.SBW_TestProtocol_method_setName_0", output);
        Assert.Contains("Encoding.UTF8.GetBytes", output);
        Assert.DoesNotContain("Cannot call method 'SetName'", output);

        // P2 fix: handles declared before try, IsAllocated check in finally
        Assert.Contains("var arg0Handle = default(GCHandle);", output);
        Assert.Contains("if (arg0Handle.IsAllocated) arg0Handle.Free();", output);
    }

    #endregion

    #region SB0003 Diagnostic Tests

    [Fact]
    public void EmitProxyClass_NonDispatchableMethod_EmitsSB0003()
    {
        // Without TypeDB registration, Swift.Int returns AnyType → non-dispatchable
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "getValue",
            MangledName = "$sgetValue",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty, PrivateName = string.Empty,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = false, IsAsync = false,
            IsSynthesizedAccessor = false
        });
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("SB0003", output);
        Assert.Contains("cannot be called on protocol-typed values", output);
    }

    [Fact]
    public void EmitProxyClass_DispatchableMethod_DoesNotEmitSB0003()
    {
        RegisterSwiftInt32();
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "getValue",
            MangledName = "$sgetValue",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty, PrivateName = string.Empty,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int32"),
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = false, IsAsync = false,
            IsSynthesizedAccessor = false
        });
        var output = EmitProxyClass(protocolDecl);

        // Dispatchable method should NOT have SB0003 on its declaration
        // (SB0003 may still appear in other members, so check near the method)
        var methodIdx = output.IndexOf("public int GetValue()", StringComparison.Ordinal);
        Assert.True(methodIdx >= 0, "Expected to find 'public int GetValue()' in output");
        // Look at the 300 chars before the method declaration for the absence of SB0003
        var preMethodText = output.Substring(Math.Max(0, methodIdx - 300), Math.Min(300, methodIdx));
        Assert.DoesNotContain("SB0003", preMethodText);
    }

    [Fact]
    public void EmitProxyClass_NonDispatchableProperty_EmitsSB0003()
    {
        // Without TypeDB, property is non-dispatchable → SB0003
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("SB0003", output);
    }

    [Fact]
    public void EmitProxyClass_DispatchablePropertyGetter_NoSB0003()
    {
        RegisterSwiftInt32();
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "count", hasGetter: true, hasSetter: false, new NamedTypeSpec("Swift.Int32"));
        var output = EmitProxyClass(protocolDecl);

        // Property with dispatchable getter should NOT have SB0003
        var propIdx = output.IndexOf("public int Count", StringComparison.Ordinal);
        Assert.True(propIdx >= 0, "Expected to find 'public int Count' in output");
        var preText = output.Substring(Math.Max(0, propIdx - 300), Math.Min(300, propIdx));
        Assert.DoesNotContain("SB0003", preText);
    }

    [Fact]
    public void EmitProxyClass_Subscript_AlwaysEmitsSB0003()
    {
        // Subscripts are always non-dispatchable → SB0003
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        protocolDecl.Subscripts.Add(new SubscriptDecl
        {
            Name = "subscript",
            MangledName = "$sTestProtocol_subscript",
            ReturnTypeSpec = new NamedTypeSpec("Swift.Int"),
            IndexParameters = new List<ArgumentDecl>
            {
                new()
                {
                    Name = "index",
                    PrivateName = "index",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            IsStatic = false,
            ParentDecl = null,
            ModuleDecl = null,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl
                {
                    Method = new MethodDecl
                    {
                        Name = "subscript_get",
                        MangledName = "$ssubscriptg",
                        MethodType = MethodType.Instance,
                        IsConstructor = false,
                        CSSignature = new List<ArgumentDecl>(),
                        GenericParameters = new List<GenericArgumentDecl>(),
                        ParentDecl = null,
                        ModuleDecl = null,
                        Throws = false,
                        IsAsync = false,
                        IsSynthesizedAccessor = true
                    }
                }
            }
        });
        var output = EmitProxyClass(protocolDecl);

        // Subscripts always get SB0003
        var subscriptIdx = output.IndexOf("public Swift.AnyType this[", StringComparison.Ordinal);
        Assert.True(subscriptIdx >= 0, "Expected subscript indexer in output");
        var preText = output.Substring(Math.Max(0, subscriptIdx - 300), Math.Min(300, subscriptIdx));
        Assert.Contains("SB0003", preText);
    }

    #endregion

    #region Utf8Slice Struct Tests

    [Fact]
    public void EmitProxyClass_DoesNotEmitPrivateUtf8Slice()
    {
        // E9: Utf8Slice is now shared at module level, not per-class
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        Assert.DoesNotContain("private struct Utf8Slice", output);
    }

    #endregion

    #region Witness Table Lookup Tests

    [Fact]
    public void EmitProxyClass_GeneratesWitnessTablePInvoke()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("EntryPoint = \"Get_EveryProtocol_TestProtocol_WitnessTable\"", output);
        Assert.Contains("public static partial IntPtr GetWitnessTable()", output);
    }

    [Fact]
    public void EmitProxyClass_GetWitnessTableFromSwiftCallsNativeMethod()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("return NativeMethods.GetWitnessTable()", output);
    }

    #endregion

    #region Receiver ABI Type Marshalling

    [Fact]
    public void EmitProxyClass_SetterReceiver_String_UsesRuntimeMarshal()
    {
        // String property setter: local MarshalFromSwift<SwiftString> uses Unsafe.Read which can't
        // construct a managed SwiftString from raw Swift memory. Must use runtime's SwiftMarshal.
        RegisterSwiftString();
        var typeSpec = new NamedTypeSpec("Swift.String");
        var protocolDecl = CreateProtocolWithProperty("StringPropProto", "label", hasGetter: false, hasSetter: true, typeSpec);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("Receive_label_set", output);
        // Must use runtime SwiftMarshal for String (not local helper which uses Unsafe.Read)
        Assert.Contains("global::Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwiftObject<Swift.SwiftString>", output);
        Assert.DoesNotContain("MarshalFromSwift<string>", output);
    }

    [Fact]
    public void EmitProxyClass_MethodReceiver_StringParam_UsesRuntimeMarshal()
    {
        // String method params: local MarshalFromSwift<SwiftString> uses Unsafe.Read which can't
        // construct a managed SwiftString from raw Swift memory. Must use runtime's SwiftMarshal.
        RegisterSwiftString();
        var protocol = CreateSimpleProtocol("MethodStringProto");
        var method = CreateMethodDecl("greet");
        // Add a String parameter (CSSignature[0] is return, [1+] are params)
        method.CSSignature.Add(new ArgumentDecl
        {
            Name = "name",
            PrivateName = "name",
            SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
            IsGeneric = false,
            IsInOut = false,
            ParentDecl = null,
            ModuleDecl = null
        });
        protocol.Methods.Add(method);
        var output = EmitProxyClass(protocol);

        Assert.Contains("Receive_greet_0", output);
        // Must use runtime SwiftMarshal for String (not local helper which uses Unsafe.Read)
        Assert.Contains("global::Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwiftObject<Swift.SwiftString>", output);
        Assert.Contains(".ToString()", output);
        Assert.DoesNotContain("MarshalFromSwift<string>", output);
    }

    [Fact]
    public void EmitProxyClass_SetterReceiver_Int_StillUsesCorrectType()
    {
        // Primitives should be unaffected by the P0 fix — Int has no idiomatic conversion.
        RegisterSwiftInt32();
        var typeSpec = new NamedTypeSpec("Swift.Int32");
        var protocolDecl = CreateProtocolWithProperty("IntPropProto", "count", hasGetter: false, hasSetter: true, typeSpec);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("Receive_count_set", output);
        Assert.Contains("MarshalFromSwift<int>", output);
    }

    #endregion

    #region F1: nint→int Property Narrowing in Proxy

    [Fact]
    public void EmitProxyClass_NintProperty_InterfaceUsesInt()
    {
        // F1: Protocol interface property with Swift.Int type → narrowed to int
        RegisterSwiftInt();
        var protocolDecl = CreateProtocolWithProperty("CountableProto", "count", hasGetter: true, hasSetter: false, new NamedTypeSpec("Swift.Int"));
        var output = EmitProxyClass(protocolDecl);

        // The proxy's property should be int (not nint)
        Assert.Contains("public int Count", output);
    }

    [Fact]
    public void EmitProxyClass_NuintProperty_InterfaceUsesUint()
    {
        // F1: Protocol interface property with Swift.UInt type → narrowed to uint
        RegisterSwiftUInt();
        var protocolDecl = CreateProtocolWithProperty("SizeProto", "size", hasGetter: true, hasSetter: false, new NamedTypeSpec("Swift.UInt"));
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("public uint Size", output);
    }

    [Fact]
    public void EmitProxyClass_NintGetterReceiver_WidensToNint()
    {
        // F1: Getter receiver must widen int result → (nint)result for 8-byte ABI
        RegisterSwiftInt();
        var protocolDecl = CreateProtocolWithProperty("IndexProto", "index", hasGetter: true, hasSetter: false, new NamedTypeSpec("Swift.Int"));
        var output = EmitProxyClass(protocolDecl);

        // Receiver should have (nint)result widening cast
        var receiverSection = output.Substring(output.IndexOf("Receive_index_get"));
        Assert.Contains("(nint)result", receiverSection);
        Assert.Contains("MarshalToSwiftBuffer", receiverSection);
    }

    [Fact]
    public void EmitProxyClass_NintSetterReceiver_NarrowsToInt()
    {
        // F1: Setter receiver must narrow nint ABI value → (int)value for property assignment
        RegisterSwiftInt();
        var protocolDecl = CreateProtocolWithProperty("MutableIndexProto", "index", hasGetter: true, hasSetter: true, new NamedTypeSpec("Swift.Int"));
        var output = EmitProxyClass(protocolDecl);

        // Setter receiver should have (int) narrowing cast
        var setterSection = output.Substring(output.IndexOf("Receive_index_set"));
        Assert.Contains("(int)", setterSection);
    }

    [Fact]
    public void EmitProxyClass_NuintGetterReceiver_WidensToNuint()
    {
        // F1: Getter receiver for Swift.UInt → (nuint)result for ABI
        RegisterSwiftUInt();
        var protocolDecl = CreateProtocolWithProperty("UnsignedProto", "offset", hasGetter: true, hasSetter: false, new NamedTypeSpec("Swift.UInt"));
        var output = EmitProxyClass(protocolDecl);

        var receiverSection = output.Substring(output.IndexOf("Receive_offset_get"));
        Assert.Contains("(nuint)result", receiverSection);
    }

    [Fact]
    public void EmitProxyClass_NintDispatch_CastsFromNint()
    {
        // F1: InterfaceImpl dispatch should cast: (int)MarshalFromSwift<nint>(ptr)
        RegisterSwiftInt();
        var protocolDecl = CreateProtocolWithProperty("DispatchProto", "position", hasGetter: true, hasSetter: false, new NamedTypeSpec("Swift.Int"));
        var output = EmitProxyClass(protocolDecl);

        // Dispatch getter should narrow from nint to int
        Assert.Contains("(int)MarshalFromSwift<nint>", output);
    }

    [Fact]
    public void EmitProxyClass_OptionalNintProperty_NarrowsToNullableInt()
    {
        // F1: Optional<Swift.Int> property → int? with ABI casts
        RegisterSwiftInt();
        var optNint = new NamedTypeSpec("Swift.Optional");
        optNint.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var protocolDecl = CreateProtocolWithProperty("OptionalCountProto", "count", hasGetter: true, hasSetter: false, optNint);
        var output = EmitProxyClass(protocolDecl);

        // Property type should be int? (not nint?)
        Assert.Contains("int? Count", output);
    }

    #endregion

    #region Generic Type Preservation in Closure Params

    [Fact]
    public void EmitProxyClass_ClosureParam_OptionalDictionary_PreservesGenericArgs()
    {
        // Bug fix: Optional<Dictionary<AnyHashable, Any>> in closure params must emit
        // SwiftDictionary<AnyType, AnyType>? (with generic args), not bare SwiftDictionary?
        // which causes CS0305. The fix passes a typeTranslator to GetIdiomaticCSharpType
        // so GetElementType can recursively resolve generic type arguments.
        RegisterSwiftOptional();
        RegisterSwiftDictionary();

        var protocolDecl = CreateSimpleProtocol("CompletionProtocol");

        // Method with closure param: (Optional<Dictionary<AnyHashable, Any>>, Optional<Error>) -> Void
        var closureParams = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.Dictionary",
                new NamedTypeSpec("Swift.AnyHashable"),
                new NamedTypeSpec("Swift.Int"))),
            new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.Bool"))
        });
        var closureType = new ClosureTypeSpec(closureParams, TupleTypeSpec.Empty);

        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "fetchData",
            MangledName = "$sfetchData",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty, PrivateName = string.Empty,
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = null
                },
                new()
                {
                    Name = "completion", PrivateName = "completion",
                    SwiftTypeSpec = closureType,
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = false, IsAsync = false,
            IsSynthesizedAccessor = false
        });

        var output = EmitProxyClass(protocolDecl);

        // The Dictionary in the closure param must have generic type arguments (not bare SwiftDictionary).
        // With factory-based projection, the raw ABI type is used when the factory can't fully resolve
        // the closure (e.g., AnyHashable not in TypeDatabase). The key requirement is generic args present.
        Assert.Contains("SwiftDictionary<", output);
        // Must NOT emit bare type without generic args
        Assert.DoesNotContain("SwiftDictionary?", output.Replace("SwiftDictionary<", ""));
    }

    #endregion

    #region Existential Parameter Receiver Tests

    [Fact]
    public void EmitProxyClass_ExistentialParam_EmitsReceiver()
    {
        // Protocol methods with existential parameters should emit receivers
        // (not NotSupportedException stubs). The receiver unmarshals ExistentialContainer1
        // and wraps it in a proxy before dispatching to _csharpImpl.
        RegisterProtocol("SourceProtocol");
        var protocolDecl = CreateSimpleProtocol("DelegateProtocol");
        var existentialType = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.SourceProtocol") });

        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "handle",
            MangledName = "$shandle",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty, PrivateName = string.Empty,
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = null
                },
                new()
                {
                    Name = "source", PrivateName = "source",
                    SwiftTypeSpec = existentialType,
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = false, IsAsync = false,
            IsSynthesizedAccessor = false
        });

        var output = EmitProxyClass(protocolDecl);

        // Receiver should be emitted (not skipped)
        Assert.Contains("Receive_handle_0", output);
        Assert.Contains("Swift.Runtime.ExistentialContainer1", output);
        // Should create a proxy from the existential container
        Assert.Contains("SourceProtocolProxy(", output);
        // Should NOT emit NotSupportedException for this method
        Assert.DoesNotContain("Existential parameters cannot be marshalled", output);
    }

    [Fact]
    public void EmitProxyClass_ExistentialParam_EmitsVtableAssignment()
    {
        // Vtable should include the function pointer for the existential-param method
        RegisterProtocol("SourceProtocol");
        var protocolDecl = CreateSimpleProtocol("DelegateProtocol");
        var existentialType = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.SourceProtocol") });

        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "handle",
            MangledName = "$shandle",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty, PrivateName = string.Empty,
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = null
                },
                new()
                {
                    Name = "source", PrivateName = "source",
                    SwiftTypeSpec = existentialType,
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = false, IsAsync = false,
            IsSynthesizedAccessor = false
        });

        var output = EmitProxyClass(protocolDecl);

        // Vtable should have the function pointer assignment
        Assert.Contains("&Receive_handle_0", output);
    }

    [Fact]
    public void EmitProxyClass_ClosureAndExistentialParams_ClosureSkippedNotExistential()
    {
        // When a method has BOTH a closure param AND an existential param,
        // the closure param causes the method to be skipped (NotSupportedException).
        // The existential param alone would be fine, but closure takes priority.
        RegisterProtocol("SourceProtocol");
        var protocolDecl = CreateSimpleProtocol("MixedProtocol");

        var closureType = new ClosureTypeSpec
        {
            Arguments = TupleTypeSpec.Empty,
            ReturnType = TupleTypeSpec.Empty,
        };
        var existentialType = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.SourceProtocol") });

        // Method with both closure AND existential params → should be skipped
        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "update",
            MangledName = "$supdate_closure_existential",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty, PrivateName = string.Empty,
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = null
                },
                new()
                {
                    Name = "source", PrivateName = "source",
                    SwiftTypeSpec = existentialType,
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = null
                },
                new()
                {
                    Name = "handler", PrivateName = "handler",
                    SwiftTypeSpec = closureType,
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = false, IsAsync = false,
            IsSynthesizedAccessor = false
        });

        var methodKey = ProtocolSignatureHelper.GetMethodSignatureKey(protocolDecl.Methods[0], _typeDatabase, protocolDecl);
        var stringWriter = new StringWriter();
        var writer = new CSharpWriter(stringWriter);
        _emitter.EmitProxyClass(writer, protocolDecl,
            skippedMethodKeys: new HashSet<string> { methodKey },
            closureSkippedMethodKeys: new HashSet<string> { methodKey });
        var output = stringWriter.ToString();

        // Closure + existential method should have NotSupportedException stub on the C# side
        // (the consumer-facing `public void Update(...)` interface impl).
        Assert.Contains("closure parameters cannot be marshalled", output);

        // Vtable-slot-collision fix: closure-skipped methods must NOT emit a vtable slot
        // (no Swift-struct field, no local-vtable field, no static-ctor assignment).
        // Swift's EveryProtocolEmitter omits the slot entirely (fatalError stub bypasses
        // the vtable), so any C# slot would shift every subsequent slot one pointer past
        // the address Swift reads — dispatch on the next method would land on the wrong
        // function pointer.
        Assert.DoesNotContain("Receive_update_0(", output);
        Assert.DoesNotContain("Func_update_0", output);
        Assert.DoesNotContain("func_update_0", output);
    }

    [Fact]
    public void EmitProxyClass_MultipleExistentialParams_EmitsReceiverWithProxies()
    {
        // Method with two existential params — both should get proxy wrapping
        RegisterProtocol("SourceProtocol");
        RegisterProtocol("TargetProtocol");
        var protocolDecl = CreateSimpleProtocol("BridgeProtocol");
        var existentialType1 = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.SourceProtocol") });
        var existentialType2 = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.TargetProtocol") });

        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "bridge",
            MangledName = "$sbridge",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty, PrivateName = string.Empty,
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = null
                },
                new()
                {
                    Name = "from", PrivateName = "from",
                    SwiftTypeSpec = existentialType1,
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = null
                },
                new()
                {
                    Name = "to", PrivateName = "to",
                    SwiftTypeSpec = existentialType2,
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = false, IsAsync = false,
            IsSynthesizedAccessor = false
        });

        var output = EmitProxyClass(protocolDecl);

        // Receiver emitted with both params
        Assert.Contains("Receive_bridge_0", output);
        // Both existential params should be unmarshalled as ExistentialContainer1
        Assert.Contains("MarshalFromSwift<Swift.Runtime.ExistentialContainer1>", output);
        // Both should get proxy wrapping
        Assert.Contains("SourceProtocolProxy(", output);
        Assert.Contains("TargetProtocolProxy(", output);
    }

    [Fact]
    public void EmitProxyClass_ExistentialParam_DispatchesToCSharpImpl()
    {
        // The method implementation in the proxy should dispatch to _csharpImpl,
        // not throw NotSupportedException
        RegisterProtocol("SourceProtocol");
        var protocolDecl = CreateSimpleProtocol("DelegateProtocol");
        var existentialType = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.SourceProtocol") });

        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "handle",
            MangledName = "$shandle",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty, PrivateName = string.Empty,
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = null
                },
                new()
                {
                    Name = "source", PrivateName = "source",
                    SwiftTypeSpec = existentialType,
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = false, IsAsync = false,
            IsSynthesizedAccessor = false
        });

        var output = EmitProxyClass(protocolDecl);

        // Interface implementation should dispatch to _csharpImpl
        Assert.Contains("_csharpImpl", output);
        // The Handle method should contain a dispatch call (not NotSupportedException)
        var methodIdx = output.IndexOf("public void Handle(");
        Assert.True(methodIdx >= 0, "Expected to find 'public void Handle(' in output");
        var methodSection = output.Substring(methodIdx, Math.Min(500, output.Length - methodIdx));
        Assert.Contains("_csharpImpl", methodSection);
        // Should NOT have "Cannot call method 'Handle'" (non-dispatchable fallback is OK, but existential shouldn't block)
    }

    [Fact]
    public void EmitProxyClass_OptionalExistentialParam_EmitsReceiver()
    {
        // Optional<any Protocol> param should also emit receiver
        RegisterProtocol("SourceProtocol");
        var protocolDecl = CreateSimpleProtocol("OptDelegateProtocol");
        var existentialType = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.SourceProtocol") });
        var optionalExistential = new NamedTypeSpec("Swift.Optional");
        optionalExistential.GenericParameters.Add(existentialType);

        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "handleOptional",
            MangledName = "$shandleOptional",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty, PrivateName = string.Empty,
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = null
                },
                new()
                {
                    Name = "source", PrivateName = "source",
                    SwiftTypeSpec = optionalExistential,
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = false, IsAsync = false,
            IsSynthesizedAccessor = false
        });

        var output = EmitProxyClass(protocolDecl);

        // Receiver should be emitted
        Assert.Contains("Receive_handleOptional_0", output);
        // Should contain SwiftOptional unmarshalling for the optional existential
        Assert.Contains("SwiftOptional", output);
    }

    private void RegisterSwiftOptional()
    {
        _typeDatabase.AddOutOfModuleTypes(new[]
        {
            (SwiftTypeName.FromModuleQualifiedName("Swift.Optional"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftOptional"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            })
        });
    }

    /// <summary>
    /// Registers a protocol type in the test TypeDatabase so that ExistentialHandler
    /// resolves it to IProtocol (not "object" fallback).
    /// </summary>
    private void RegisterProtocol(string name)
    {
        _typeDatabase.AddOutOfModuleTypes(new[]
        {
            (SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", $"I{name}"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            })
        });
    }

    /// <summary>
    /// Registers a protocol that lives in a DIFFERENT module than the emitting one
    /// (the proxy emitter is constructed for "TestModule"). Used to exercise
    /// cross-module existential qualification: the protocol's C# namespace is the
    /// dependency module, so a proxy/signature in TestModule must namespace-qualify
    /// references to it.
    /// </summary>
    private void RegisterCrossModuleProtocol(string module, string name)
    {
        _typeDatabase.AddOutOfModuleTypes(new[]
        {
            (SwiftTypeName.FromModuleQualifiedName($"{module}.{name}"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(module, $"I{name}"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{module}.{name}"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            })
        });
    }

    private void RegisterSwiftDictionary()
    {
        _typeDatabase.AddOutOfModuleTypes(new[]
        {
            (SwiftTypeName.FromModuleQualifiedName("Swift.Dictionary"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftDictionary"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Dictionary"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            })
        });
    }

    /// <summary>
    /// Registers an ObjC bridged type (e.g., NSUrlSession) in the TypeDatabase
    /// so TypeProjectionFactory creates ObjCBridgedProjection for it.
    /// </summary>
    private void RegisterObjCBridgedType(string swiftName, string csharpName)
    {
        _typeDatabase.AddOutOfModuleTypes(new[]
        {
            (SwiftTypeName.FromModuleQualifiedName(swiftName), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(csharpName.Substring(0, csharpName.LastIndexOf('.')), csharpName.Substring(csharpName.LastIndexOf('.') + 1)),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(swiftName),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.ObjCBridged,
                Kind = TypeRecordKind.Class
            })
        });
    }

    /// <summary>
    /// Registers a native-remapped type (e.g., URL → NSUrl) in the TypeDatabase
    /// so TypeProjectionFactory creates NativeRemappedProjection for it.
    /// </summary>
    private void RegisterNativeRemappedType(string swiftName, string csharpName, string nativeName, bool isFrozen = false)
    {
        var flags = TypeRecordFlags.None;
        if (isFrozen)
            flags |= TypeRecordFlags.Frozen;
        _typeDatabase.AddOutOfModuleTypes(new[]
        {
            (SwiftTypeName.FromModuleQualifiedName(swiftName), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(csharpName.Substring(0, csharpName.LastIndexOf('.')), csharpName.Substring(csharpName.LastIndexOf('.') + 1)),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(swiftName),
                NativeTypeName = CSharpTypeName.FromNamespaceAndName(nativeName.Substring(0, nativeName.LastIndexOf('.')), nativeName.Substring(nativeName.LastIndexOf('.') + 1)),
                MetadataAccessor = "",
                Flags = flags,
                Kind = TypeRecordKind.Struct
            })
        });
    }

    /// <summary>
    /// Registers an ObjC-bridgeable type (e.g., Foundation.URL → Foundation.NSUrl) in the TypeDatabase
    /// so TypeProjectionFactory creates ObjCBridgeableProjection for it.
    /// </summary>
    private void RegisterObjCBridgeableType(string swiftName, string nativeName)
    {
        _typeDatabase.AddOutOfModuleTypes(new[]
        {
            (SwiftTypeName.FromModuleQualifiedName(swiftName), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(nativeName.Substring(0, nativeName.LastIndexOf('.')), nativeName.Substring(nativeName.LastIndexOf('.') + 1)),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(swiftName),
                NativeTypeName = CSharpTypeName.FromNamespaceAndName(nativeName.Substring(0, nativeName.LastIndexOf('.')), nativeName.Substring(nativeName.LastIndexOf('.') + 1)),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.RequiresMemoryManagement | TypeRecordFlags.ObjCBridgeable,
                Kind = TypeRecordKind.Struct
            })
        });
    }

    #endregion

    #region Protocol AnyType Resolution in Receiver ABI Types

    [Fact]
    public void EmitProxyClass_MethodReceiver_ArrayOfExistential_UsesExistentialContainerNotAnyType()
    {
        // Root cause fix: Array<any Protocol> in receiver param must use
        // MarshalFromSwiftObject<SwiftArray<Swift.Runtime.ExistentialContainer1>> not SwiftArray<AnyType>.
        // Before fix, GetCSharpTypeName(forAbiMarshalling:true) skipped TypeProjectionFactory and
        // fell through to BoundGenericsHandler which unconditionally converts existentials to AnyType.
        var protocol = CreateSimpleProtocol("DataProtocol");
        var method = CreateMethodDecl("process");
        var arrayOfExistential = new NamedTypeSpec("Swift.Array");
        arrayOfExistential.GenericParameters.Add(
            new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.Selectable") }));
        method.CSSignature.Add(new ArgumentDecl
        {
            Name = "items",
            PrivateName = "items",
            SwiftTypeSpec = arrayOfExistential,
            IsGeneric = false, IsInOut = false,
            ParentDecl = null, ModuleDecl = null
        });
        protocol.Methods.Add(method);
        var output = EmitProxyClass(protocol);

        Assert.Contains("Receive_process_0", output);
        // Behavior, not helper name: the ABI marshal type must be the existential CONTAINER
        // (SwiftArray<ExistentialContainer1>), never SwiftArray<AnyType>. The marshalling helper
        // is MarshalFromSwiftObject (reference-type wrapper → NewFromPayload); asserting the
        // container type stays correct across that helper split.
        Assert.Contains("SwiftArray<Swift.Runtime.ExistentialContainer1>", output);
        Assert.DoesNotContain("SwiftArray<Swift.AnyType>", output);
        Assert.DoesNotContain("SwiftArray<AnyType>", output);
    }

    [Fact]
    public void EmitProxyClass_MethodReceiver_DictionaryWithExistentialValue_UsesExistentialContainer()
    {
        // Dictionary<String, any Protocol> in receiver param must use
        // MarshalFromSwiftObject<SwiftDictionary<SwiftString, Swift.Runtime.ExistentialContainer1>> not AnyType.
        RegisterSwiftString();
        var protocol = CreateSimpleProtocol("MapProtocol");
        var method = CreateMethodDecl("update");
        var dictOfExistential = new NamedTypeSpec("Swift.Dictionary");
        dictOfExistential.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        dictOfExistential.GenericParameters.Add(
            new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.Mappable") }));
        method.CSSignature.Add(new ArgumentDecl
        {
            Name = "map",
            PrivateName = "map",
            SwiftTypeSpec = dictOfExistential,
            IsGeneric = false, IsInOut = false,
            ParentDecl = null, ModuleDecl = null
        });
        protocol.Methods.Add(method);
        var output = EmitProxyClass(protocol);

        Assert.Contains("Receive_update_0", output);
        // Behavior, not helper name: ABI marshal type is the existential CONTAINER value
        // (SwiftDictionary<SwiftString, ExistentialContainer1>), never AnyType. Emitted via
        // MarshalFromSwiftObject (reference-type wrapper).
        Assert.Contains("SwiftDictionary<SwiftString, Swift.Runtime.ExistentialContainer1>", output);
        Assert.DoesNotContain("AnyType", output.Substring(output.IndexOf("Receive_update_0")));
    }

    [Fact]
    public void EmitProxyClass_MethodReceiver_BareExistential_UsesExistentialContainer()
    {
        // Bare existential (any Protocol) in receiver param must use
        // MarshalFromSwift<Swift.Runtime.ExistentialContainer1> not MarshalFromSwift<AnyType>.
        var protocol = CreateSimpleProtocol("HandlerProtocol");
        var method = CreateMethodDecl("handle");
        method.CSSignature.Add(new ArgumentDecl
        {
            Name = "item",
            PrivateName = "item",
            SwiftTypeSpec = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.Processable") }),
            IsGeneric = false, IsInOut = false,
            ParentDecl = null, ModuleDecl = null
        });
        protocol.Methods.Add(method);
        var output = EmitProxyClass(protocol);

        Assert.Contains("Receive_handle_0", output);
        Assert.Contains("MarshalFromSwift<Swift.Runtime.ExistentialContainer1>", output);
        Assert.DoesNotContain("MarshalFromSwift<Swift.AnyType>", output);
    }

    [Fact]
    public void EmitProxyClass_MethodReceiver_OptionalExistential_UsesSwiftOptionalExistentialContainer()
    {
        // Optional<any Protocol> in receiver param must use
        // MarshalFromSwift<SwiftOptional<Swift.Runtime.ExistentialContainer1>> not SwiftOptional<AnyType>.
        var protocol = CreateSimpleProtocol("OptionalExistProto");
        var method = CreateMethodDecl("check");
        var optionalExistential = new NamedTypeSpec("Swift.Optional");
        optionalExistential.GenericParameters.Add(
            new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.Checkable") }));
        method.CSSignature.Add(new ArgumentDecl
        {
            Name = "item",
            PrivateName = "item",
            SwiftTypeSpec = optionalExistential,
            IsGeneric = false, IsInOut = false,
            ParentDecl = null, ModuleDecl = null
        });
        protocol.Methods.Add(method);
        var output = EmitProxyClass(protocol);

        Assert.Contains("Receive_check_0", output);
        Assert.Contains("MarshalFromSwift<SwiftOptional<Swift.Runtime.ExistentialContainer1>>", output);
        Assert.DoesNotContain("SwiftOptional<Swift.AnyType>", output);
        Assert.DoesNotContain("SwiftOptional<AnyType>", output);
    }

    [Fact]
    public void EmitProxyClass_PropertySetter_ArrayOfExistential_UsesExistentialContainer()
    {
        // Property setter with Array<any Protocol> must use correct ABI type for MarshalFromSwift.
        var arrayOfExistential = new NamedTypeSpec("Swift.Array");
        arrayOfExistential.GenericParameters.Add(
            new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.Renderable") }));
        var protocolDecl = CreateProtocolWithProperty("RenderProto", "layers",
            hasGetter: false, hasSetter: true, arrayOfExistential);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("Receive_layers_set", output);
        // Behavior, not helper name: the ABI marshal type must be the existential CONTAINER
        // (SwiftArray<ExistentialContainer1>), never SwiftArray<AnyType>. The marshalling helper
        // is MarshalFromSwiftObject (reference-type wrapper → NewFromPayload); asserting the
        // container type stays correct across that helper split.
        Assert.Contains("SwiftArray<Swift.Runtime.ExistentialContainer1>", output);
        Assert.DoesNotContain("SwiftArray<AnyType>", output);
    }

    [Fact]
    public void EmitProxyClass_PropertyGetter_ArrayOfExistential_EmitsConversion()
    {
        // Property getter with Array<any Protocol> must convert elements via existential extraction.
        // Without protocol registered in TypeDatabase, ExistentialProjection falls back to "object".
        var arrayOfExistential = new NamedTypeSpec("Swift.Array");
        arrayOfExistential.GenericParameters.Add(
            new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.Renderable") }));
        var protocolDecl = CreateProtocolWithProperty("RenderProto", "layers",
            hasGetter: true, hasSetter: false, arrayOfExistential);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("Receive_layers_get", output);
        // Should convert elements via existential extraction
        Assert.Contains("SwiftArray<Swift.Runtime.ExistentialContainer1>.FromEnumerable", output);
        Assert.Contains("ISwiftExistentialConvertible", output);
    }

    [Fact]
    public void EmitProxyClass_SubscriptGetter_ExistentialParam_UsesExistentialContainer()
    {
        // Subscript index parameters with existential types must use ABI container type.
        var protocol = CreateSimpleProtocol("SubscriptProto");
        var subscript = new SubscriptDecl
        {
            Name = "subscript",
            MangledName = "$sSubscriptProto9subscriptP",
            ReturnTypeSpec = new NamedTypeSpec("Swift.Int"),
            IndexParameters = new List<ArgumentDecl>
            {
                new()
                {
                    Name = "key",
                    PrivateName = "key",
                    SwiftTypeSpec = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.Indexable") }),
                    IsGeneric = false, IsInOut = false,
                    ParentDecl = null, ModuleDecl = null
                }
            },
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("subscript_get") }
            },
            IsStatic = false,
            ParentDecl = null,
            ModuleDecl = null
        };
        protocol.Subscripts.Add(subscript);
        var output = EmitProxyClass(protocol);

        Assert.Contains("Receive_subscript_0_get", output);
        Assert.Contains("MarshalFromSwift<Swift.Runtime.ExistentialContainer1>", output);
        Assert.DoesNotContain("MarshalFromSwift<Swift.AnyType>", output);
    }

    [Fact]
    public void EmitProxyClass_MethodReceiver_MultiProtocolComposition_UsesExistentialContainer2()
    {
        // Two-protocol composition (any P1 & P2) uses ExistentialContainer2.
        var protocol = CreateSimpleProtocol("CompositionProto");
        var method = CreateMethodDecl("compose");
        method.CSSignature.Add(new ArgumentDecl
        {
            Name = "item",
            PrivateName = "item",
            SwiftTypeSpec = new ProtocolListTypeSpec(new[]
            {
                new NamedTypeSpec("TestModule.Encodable"),
                new NamedTypeSpec("TestModule.Decodable")
            }),
            IsGeneric = false, IsInOut = false,
            ParentDecl = null, ModuleDecl = null
        });
        protocol.Methods.Add(method);
        var output = EmitProxyClass(protocol);

        Assert.Contains("Receive_compose_0", output);
        Assert.Contains("MarshalFromSwift<Swift.Runtime.ExistentialContainer2>", output);
        Assert.DoesNotContain("AnyType", output.Substring(output.IndexOf("Receive_compose_0")));
    }

    [Fact]
    public void EmitProxyClass_MethodReceiver_ArrayOfExistential_SetterConversionCorrect()
    {
        // Array<any Protocol> in receiver: conversion side should produce
        // .AsProjected<IRenderable>(c => new RenderableProxy(c)) when protocol is registered.
        RegisterProtocol("Renderable");
        var protocol = CreateSimpleProtocol("ConversionProto");
        var method = CreateMethodDecl("render");
        var arrayOfExistential = new NamedTypeSpec("Swift.Array");
        arrayOfExistential.GenericParameters.Add(
            new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.Renderable") }));
        method.CSSignature.Add(new ArgumentDecl
        {
            Name = "items",
            PrivateName = "items",
            SwiftTypeSpec = arrayOfExistential,
            IsGeneric = false, IsInOut = false,
            ParentDecl = null, ModuleDecl = null
        });
        protocol.Methods.Add(method);
        var output = EmitProxyClass(protocol);

        // Conversion side should use the proxy pattern
        Assert.Contains("AsProjected<IRenderable>", output);
        Assert.Contains("new RenderableProxy(", output);
    }

    [Fact]
    public void EmitProxyClass_MethodReceiver_StringParam_UsesRuntimeMarshalAfterExistentialFix()
    {
        // Regression: ensure String params use runtime SwiftMarshal, not broken by existential fix.
        RegisterSwiftString();
        var protocol = CreateSimpleProtocol("StringCheckProto");
        var method = CreateMethodDecl("greet");
        method.CSSignature.Add(new ArgumentDecl
        {
            Name = "name",
            PrivateName = "name",
            SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
            IsGeneric = false, IsInOut = false,
            ParentDecl = null, ModuleDecl = null
        });
        protocol.Methods.Add(method);
        var output = EmitProxyClass(protocol);

        Assert.Contains("global::Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwiftObject<Swift.SwiftString>", output);
        Assert.DoesNotContain("MarshalFromSwift<string>", output);
    }

    [Fact]
    public void EmitProxyClass_MethodReceiver_BlittableParam_AbiTypeUnchangedByFix()
    {
        // Regression: blittable Int32 should still use System.Int32/int.
        RegisterSwiftInt32();
        var protocol = CreateSimpleProtocol("IntCheckProto");
        var method = CreateMethodDecl("count");
        method.CSSignature.Add(new ArgumentDecl
        {
            Name = "n",
            PrivateName = "n",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int32"),
            IsGeneric = false, IsInOut = false,
            ParentDecl = null, ModuleDecl = null
        });
        protocol.Methods.Add(method);
        var output = EmitProxyClass(protocol);

        Assert.Contains("MarshalFromSwift<int>", output);
    }

    [Fact]
    public void EmitProxyClass_PropertySetter_OptionalExistential_UsesCorrectAbiType()
    {
        // Optional<any Protocol> property setter ABI type should be
        // SwiftOptional<Swift.Runtime.ExistentialContainer1>, not SwiftOptional<AnyType>.
        // Previously handled by OverrideOptionalExistentialAbiType; now handled by factory path.
        var optionalExistential = new NamedTypeSpec("Swift.Optional");
        optionalExistential.GenericParameters.Add(
            new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.Configurable") }));
        var protocolDecl = CreateProtocolWithProperty("ConfigProto", "delegate",
            hasGetter: false, hasSetter: true, optionalExistential);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("Receive_delegate_set", output);
        Assert.Contains("MarshalFromSwift<SwiftOptional<Swift.Runtime.ExistentialContainer1>>", output);
        Assert.DoesNotContain("SwiftOptional<AnyType>", output);
    }

    [Fact]
    public void EmitProxyClass_MethodReceiver_DictWithExistentialKey_UsesExistentialContainer()
    {
        // Dictionary<any Protocol, String> — existential as dictionary key.
        RegisterSwiftString();
        var protocol = CreateSimpleProtocol("DictKeyProto");
        var method = CreateMethodDecl("lookup");
        var dictSpec = new NamedTypeSpec("Swift.Dictionary");
        dictSpec.GenericParameters.Add(
            new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.Hashable") }));
        dictSpec.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        method.CSSignature.Add(new ArgumentDecl
        {
            Name = "map",
            PrivateName = "map",
            SwiftTypeSpec = dictSpec,
            IsGeneric = false, IsInOut = false,
            ParentDecl = null, ModuleDecl = null
        });
        protocol.Methods.Add(method);
        var output = EmitProxyClass(protocol);

        Assert.Contains("Receive_lookup_0", output);
        // Behavior, not helper name: existential as dictionary KEY → ABI marshal type is
        // SwiftDictionary<ExistentialContainer1, SwiftString>, never AnyType. Via MarshalFromSwiftObject.
        Assert.Contains("SwiftDictionary<Swift.Runtime.ExistentialContainer1, SwiftString>", output);
        Assert.DoesNotContain("AnyType", output.Substring(output.IndexOf("Receive_lookup_0")));
    }

    [Fact]
    public void EmitProxyClass_MethodReceiver_ObjCBridgedParam_UsesIntPtrAndGetNSObject()
    {
        // ObjC bridged types in protocol proxy receivers must use IntPtr for MarshalFromSwift
        // (ObjC objects are pointer-based at ABI level) and GetNSObject for the conversion.
        // Using MarshalFromSwiftType = _csharpTypeName would produce MarshalFromSwift<NSUrlSession>
        // which crashes at runtime (ObjC classes don't have Swift metadata).
        RegisterObjCBridgedType("Foundation.NSURLSession", "Foundation.NSUrlSession");
        var protocol = CreateSimpleProtocol("SessionDelegate");
        var method = CreateMethodDecl("didReceive");
        method.CSSignature.Add(new ArgumentDecl
        {
            Name = "session",
            PrivateName = "session",
            SwiftTypeSpec = new NamedTypeSpec("Foundation.NSURLSession"),
            IsGeneric = false, IsInOut = false,
            ParentDecl = null, ModuleDecl = null
        });
        protocol.Methods.Add(method);
        var output = EmitProxyClass(protocol);

        Assert.Contains("Receive_didReceive_0", output);
        // Must use IntPtr for MarshalFromSwift (not the ObjC class name)
        Assert.Contains("MarshalFromSwift<IntPtr>", output);
        // Must apply GetNSObject conversion to wrap the IntPtr
        Assert.Contains("GetNSObject<Foundation.NSUrlSession>", output);
    }

    [Fact]
    public void EmitProxyClass_MethodReceiver_ClassParam_UsesCopyOutNotUnsafeRead()
    {
        // Regression for justinwojo/swift-dotnet-bindings#40: when Swift calls back into a
        // C# impl with a Swift-class parameter, the receiver must reconstruct it via the
        // runtime copy-out (deref the borrowed slot + ObjC-aware retain + NewFromPayload),
        // NOT the per-proxy local Unsafe.Read<T> helper. Unsafe.Read<T> reinterprets the
        // Swift heap-object pointer as a managed reference → SIGSEGV on first use.
        RegisterClass("MyPayload");
        var protocol = CreateSimpleProtocol("ClassParamProto");
        var method = CreateMethodDecl("didReceive");
        method.CSSignature.Add(new ArgumentDecl
        {
            Name = "payload", PrivateName = "payload",
            SwiftTypeSpec = new NamedTypeSpec("TestModule.MyPayload"),
            IsGeneric = false, IsInOut = false, ParentDecl = null, ModuleDecl = null
        });
        protocol.Methods.Add(method);

        var output = EmitProxyClass(protocol);
        var body = ExtractMethodBody(output, "private static void Receive_didReceive_0(");

        // The broken naive read must be gone…
        Assert.DoesNotContain("MarshalFromSwift<TestModule.MyPayload>(rawArg0)", body);
        // …replaced by the runtime copy-out from the borrowed slot.
        Assert.Contains("MarshalBorrowedClassFromSlot<TestModule.MyPayload>(rawArg0)", body);
    }

    [Fact]
    public void EmitProxyClass_MethodReceiver_ObjCRootedClassParam_UsesCopyOut()
    {
        // An @objc:NSObject class param. Same copy-out routing; the runtime helper's
        // swift_unknownObjectRetain handles the ObjC-vs-native retain dispatch
        // (native swift_retain is a no-op / over-release on an NSObject subclass).
        RegisterObjCRootedClass("AdNetworkError");
        var protocol = CreateSimpleProtocol("ObjCClassParamProto");
        var method = CreateMethodDecl("onError");
        method.CSSignature.Add(new ArgumentDecl
        {
            Name = "error", PrivateName = "error",
            SwiftTypeSpec = new NamedTypeSpec("TestModule.AdNetworkError"),
            IsGeneric = false, IsInOut = false, ParentDecl = null, ModuleDecl = null
        });
        protocol.Methods.Add(method);

        var output = EmitProxyClass(protocol);
        var body = ExtractMethodBody(output, "private static void Receive_onError_0(");

        Assert.DoesNotContain("MarshalFromSwift<TestModule.AdNetworkError>(rawArg0)", body);
        Assert.Contains("MarshalBorrowedClassFromSlot<TestModule.AdNetworkError>(rawArg0)", body);
    }

    [Fact]
    public void EmitProxyClass_MethodReceiver_OptionalClassParam_UsesOptionalCopyOut()
    {
        // Optional<class> param: the borrowed slot is a single nil-pointer-optimised word, NOT
        // a managed SwiftOptional<T>. Reading it as Unsafe.Read<SwiftOptional<T>> reinterprets
        // a heap pointer as a managed object. Must route through the optional copy-out helper.
        RegisterClass("MyPayload");
        var protocol = CreateSimpleProtocol("OptClassParamProto");
        var method = CreateMethodDecl("didReceive");
        var optionalClass = new NamedTypeSpec("Swift.Optional");
        optionalClass.GenericParameters.Add(new NamedTypeSpec("TestModule.MyPayload"));
        method.CSSignature.Add(new ArgumentDecl
        {
            Name = "payload", PrivateName = "payload",
            SwiftTypeSpec = optionalClass,
            IsGeneric = false, IsInOut = false, ParentDecl = null, ModuleDecl = null
        });
        protocol.Methods.Add(method);

        var output = EmitProxyClass(protocol);
        var body = ExtractMethodBody(output, "private static void Receive_didReceive_0(");

        Assert.DoesNotContain("MarshalFromSwift<SwiftOptional<TestModule.MyPayload>>(rawArg0)", body);
        Assert.Contains("MarshalBorrowedOptionalClassFromSlot<TestModule.MyPayload>(rawArg0)", body);
    }

    [Fact]
    public void EmitProxyClass_PropertySetterReceiver_ClassValue_UsesCopyOut()
    {
        // Issue #40, non-optional class property setter site (the method-param fix at
        // ProtocolProxyEmitter.Receivers.cs:240). A Swift-class value arrives as the address of a
        // borrowed slot holding the heap pointer; the setter receiver must copy it out (deref +
        // ObjC-aware retain) rather than Unsafe.Read-ing the slot word as a managed reference.
        // ObjC-rooted variant: swift_unknownObjectRetain dispatches the @objc:NSObject retain.
        RegisterObjCRootedClass("AdNetworkError");
        var protocol = CreateProtocolWithProperty("ErrorSinkProto", "lastError",
            hasGetter: false, hasSetter: true, new NamedTypeSpec("TestModule.AdNetworkError"));

        var output = EmitProxyClass(protocol);
        var body = ExtractMethodBody(output, "private static void Receive_lastError_set(");

        Assert.DoesNotContain("MarshalFromSwift<TestModule.AdNetworkError>(", body);
        Assert.Contains("MarshalBorrowedClassFromSlot<TestModule.AdNetworkError>(valuePtr)", body);
    }

    [Fact]
    public void EmitProxyClass_SubscriptGetterReceiver_ClassIndex_UsesCopyOut()
    {
        // Issue #40, subscript getter index site (Receivers.cs:669). A Swift-class index
        // arrives as the address of a borrowed slot; the getter receiver must copy it out, not
        // Unsafe.Read it. ObjC-rooted variant exercises the swift_unknownObjectRetain dispatch.
        RegisterObjCRootedClass("AdNetworkError");
        var protocol = CreateSimpleProtocol("ClassKeyedReadProto");
        protocol.Subscripts.Add(new SubscriptDecl
        {
            Name = "subscript",
            MangledName = "$s10TestModuleP9subscriptClassKeyGet",
            ReturnTypeSpec = new NamedTypeSpec("Swift.Int"),
            IndexParameters = new List<ArgumentDecl>
            {
                new()
                {
                    Name = "key", PrivateName = "key",
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.AdNetworkError"),
                    IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = null
                }
            },
            IsStatic = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("subscript_get") },
                new SetAccessorDecl { Method = CreateMethodDecl("subscript_set") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });

        var output = EmitProxyClass(protocol);
        var body = ExtractMethodBody(output, "private static IntPtr Receive_subscript_0_get(");

        Assert.DoesNotContain("MarshalFromSwift<TestModule.AdNetworkError>(", body);
        Assert.Contains("MarshalBorrowedClassFromSlot<TestModule.AdNetworkError>(arg0)", body);
    }

    [Fact]
    public void EmitProxyClass_SubscriptSetterReceiver_ClassValueAndIndex_UsesCopyOut()
    {
        // Issue #40, subscript setter value site (Receivers.cs:748) AND index site
        // (Receivers.cs:775) in one shape: a class element type and a class index type. Both the
        // set value (valuePtr) and the index (arg0) must copy out from the borrowed slot.
        RegisterClass("MyPayload");
        var protocol = CreateSimpleProtocol("ClassKeyedWriteProto");
        protocol.Subscripts.Add(new SubscriptDecl
        {
            Name = "subscript",
            MangledName = "$s10TestModuleP9subscriptClassKeyVal",
            ReturnTypeSpec = new NamedTypeSpec("TestModule.MyPayload"),
            IndexParameters = new List<ArgumentDecl>
            {
                new()
                {
                    Name = "key", PrivateName = "key",
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.MyPayload"),
                    IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = null
                }
            },
            IsStatic = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("subscript_get") },
                new SetAccessorDecl { Method = CreateMethodDecl("subscript_set") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });

        var output = EmitProxyClass(protocol);
        var body = ExtractMethodBody(output, "private static void Receive_subscript_0_set(");

        // Naive Unsafe.Read of either the value or the index slot must be gone…
        Assert.DoesNotContain("MarshalFromSwift<TestModule.MyPayload>(", body);
        // …and both the set value and the index copy out from their borrowed slots.
        Assert.Contains("MarshalBorrowedClassFromSlot<TestModule.MyPayload>(valuePtr)", body);
        Assert.Contains("MarshalBorrowedClassFromSlot<TestModule.MyPayload>(arg0)", body);
    }

    [Fact]
    public void EmitProxyClass_PropertySetter_ObjCBridgedType_UsesGetNSObjectConversion()
    {
        // ObjC bridged property setter: MarshalFromSwift<IntPtr> + GetNSObject conversion
        RegisterObjCBridgedType("Foundation.NSURLSession", "Foundation.NSUrlSession");
        var protocolDecl = CreateProtocolWithProperty("SessionProto", "session",
            hasGetter: false, hasSetter: true, new NamedTypeSpec("Foundation.NSURLSession"));
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("Receive_session_set", output);
        Assert.Contains("MarshalFromSwift<IntPtr>", output);
        Assert.Contains("GetNSObject<Foundation.NSUrlSession>", output);
    }

    [Fact]
    public void EmitProxyClass_PropertyGetter_ObjCBridgedType_UsesHandleConversion()
    {
        // ObjC bridged property getter: extract .Handle from the C# value to produce IntPtr
        RegisterObjCBridgedType("Foundation.NSURLSession", "Foundation.NSUrlSession");
        var protocolDecl = CreateProtocolWithProperty("SessionProto", "session",
            hasGetter: true, hasSetter: false, new NamedTypeSpec("Foundation.NSURLSession"));
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("Receive_session_get", output);
        // Getter must extract .Handle from the idiomatic type to produce IntPtr for Swift
        Assert.Contains(".Handle", output);
    }

    [Fact]
    public void EmitProxyClass_MethodReceiver_NativeRemappedParam_UsesSwiftWrapperType()
    {
        // NativeRemapped types must use the Swift wrapper type for MarshalFromSwift,
        // not SafeHandle (which was the wrong default before override).
        // This tests a generic non-frozen NativeRemapped type (URL is now ObjCBridgeable).
        RegisterNativeRemappedType("TestModule.CustomValue", "Swift.CustomValue", "Foundation.NSCustom");
        var protocol = CreateSimpleProtocol("CustomHandler");
        var method = CreateMethodDecl("open");
        method.CSSignature.Add(new ArgumentDecl
        {
            Name = "value",
            PrivateName = "value",
            SwiftTypeSpec = new NamedTypeSpec("TestModule.CustomValue"),
            IsGeneric = false, IsInOut = false,
            ParentDecl = null, ModuleDecl = null
        });
        protocol.Methods.Add(method);
        var output = EmitProxyClass(protocol);

        Assert.Contains("Receive_open_0", output);
        // Must use Swift wrapper type for MarshalFromSwift
        Assert.Contains("MarshalFromSwift<Swift.CustomValue>", output);
        // Must apply conversion method
        Assert.Contains("ToNSCustom", output);
    }

    [Fact]
    public void EmitProxyClass_PropertyGetter_NativeRemappedType_UsesFromFactoryConversion()
    {
        // NativeRemapped property getter: convert from native .NET type to Swift wrapper via factory method.
        // This tests a generic non-frozen NativeRemapped type (URL is now ObjCBridgeable).
        RegisterNativeRemappedType("TestModule.CustomValue", "Swift.CustomValue", "Foundation.NSCustom");
        var protocolDecl = CreateProtocolWithProperty("CustomProto", "endpoint",
            hasGetter: true, hasSetter: false, new NamedTypeSpec("TestModule.CustomValue"));
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("Receive_endpoint_get", output);
        // Getter must convert native type to Swift wrapper for marshalling back to Swift
        Assert.Contains("Swift.CustomValue", output);
    }

    [Fact]
    public void EmitProxyClass_MethodReceiver_OptionalObjCBridgedParam_UsesDiscriminantAndGetNSObject()
    {
        // Optional<ObjC> method param: MarshalFromSwift<SwiftOptional<IntPtr>> + discriminant check
        // + GetNSObject<T>(varName.Some) conversion. Uses the ObjCBridgedProjection branch in
        // GetReceiverOptionalSetterConversion, not the default nullable cast.
        RegisterObjCBridgedType("Foundation.NSURLSession", "Foundation.NSUrlSession");
        var protocol = CreateSimpleProtocol("OptSessionDelegate");
        var method = CreateMethodDecl("didComplete");
        var optObjC = new NamedTypeSpec("Swift.Optional");
        optObjC.GenericParameters.Add(new NamedTypeSpec("Foundation.NSURLSession"));
        method.CSSignature.Add(new ArgumentDecl
        {
            Name = "session",
            PrivateName = "session",
            SwiftTypeSpec = optObjC,
            IsGeneric = false, IsInOut = false,
            ParentDecl = null, ModuleDecl = null
        });
        protocol.Methods.Add(method);
        var output = EmitProxyClass(protocol);

        Assert.Contains("Receive_didComplete_0", output);
        // ABI type: SwiftOptional<IntPtr> (ObjC objects are pointers)
        Assert.Contains("MarshalFromSwift<SwiftOptional<IntPtr>>", output);
        // Conversion: discriminant check + GetNSObject wrapping
        Assert.Contains("GetNSObject<Foundation.NSUrlSession>", output);
        Assert.Contains("SwiftOptionalCases.None", output);
    }

    [Fact]
    public void EmitProxyClass_PropertySetter_OptionalNativeRemapped_UsesSwiftWrapperType()
    {
        // Optional<NativeRemapped> property setter: MarshalFromSwift<SwiftOptional<SwiftWrapper>> + ToNative conversion.
        // Uses the NativeRemappedProjection branch in GetReceiverOptionalSetterConversion.
        // This tests a generic non-frozen NativeRemapped type (URL is now ObjCBridgeable).
        RegisterNativeRemappedType("TestModule.CustomValue", "Swift.CustomValue", "Foundation.NSCustom");
        var optVal = new NamedTypeSpec("Swift.Optional");
        optVal.GenericParameters.Add(new NamedTypeSpec("TestModule.CustomValue"));
        var protocolDecl = CreateProtocolWithProperty("OptCustomProto", "redirect",
            hasGetter: false, hasSetter: true, optVal);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("Receive_redirect_set", output);
        // ABI type: SwiftOptional<Swift.CustomValue> (wrapper implements ISwiftObject, valid for MarshalFromSwift)
        Assert.Contains("MarshalFromSwift<SwiftOptional<Swift.CustomValue>>", output);
        // Conversion: cast to wrapper type + ToNSCustom
        Assert.Contains("ToNSCustom", output);
    }

    [Fact]
    public void EmitProxyClass_MethodReceiver_ObjCBridgeableReturn_UsesHandleConversion()
    {
        // P1 fix: Method returning ObjC-bridgeable type (e.g., Foundation.URL → NSUrl) must
        // extract .Handle from the C# return value. Before this fix, the method return path
        // only checked existential conversions and fell through to raw MarshalToSwiftBuffer(result),
        // which would write a managed reference instead of the ObjC pointer.
        RegisterObjCBridgeableType("Foundation.URL", "Foundation.NSUrl");
        var protocol = CreateSimpleProtocol("URLReturner");
        protocol.Methods.Add(new MethodDecl
        {
            Name = "getURL",
            MangledName = "$sgetURL",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty, PrivateName = string.Empty,
                    SwiftTypeSpec = new NamedTypeSpec("Foundation.URL"),
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = false, IsAsync = false,
            IsSynthesizedAccessor = false
        });
        var output = EmitProxyClass(protocol);

        Assert.Contains("Receive_getURL_0", output);
        // Must extract .Handle from the idiomatic NSUrl return value
        Assert.Contains("result.Handle", output);
        Assert.Contains("MarshalToSwiftBuffer(swiftResult)", output);
    }

    [Fact]
    public void EmitProxyClass_MethodReceiver_ObjCBridgedReturn_UsesHandleConversion()
    {
        // Same fix for ObjCBridgedProjection returns — method returns must use .Handle extraction.
        RegisterObjCBridgedType("Foundation.NSURLSession", "Foundation.NSUrlSession");
        var protocol = CreateSimpleProtocol("SessionReturner");
        protocol.Methods.Add(new MethodDecl
        {
            Name = "getSession",
            MangledName = "$sgetSession",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty, PrivateName = string.Empty,
                    SwiftTypeSpec = new NamedTypeSpec("Foundation.NSURLSession"),
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = false, IsAsync = false,
            IsSynthesizedAccessor = false
        });
        var output = EmitProxyClass(protocol);

        Assert.Contains("Receive_getSession_0", output);
        // Must extract .Handle from the idiomatic NSUrlSession return value
        Assert.Contains("result.Handle", output);
        Assert.Contains("MarshalToSwiftBuffer(swiftResult)", output);
    }

    #endregion

    #region Existential Return Dispatch Tests

    [Fact]
    public void EmitProxyClass_ExistentialReturnMethod_NonThrowing_EmitsProxyConstruction()
    {
        // Non-throwing method returning existential (any TargetProtocol) should dispatch
        // through witness table and construct proxy from existential container
        RegisterProtocol("TargetProtocol");
        var protocolDecl = CreateSimpleProtocol("SourceProtocol");
        var existentialReturn = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.TargetProtocol") });

        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "getTarget",
            MangledName = "$sgetTarget",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty, PrivateName = string.Empty,
                    SwiftTypeSpec = existentialReturn,
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = false, IsAsync = false,
            IsSynthesizedAccessor = false
        });

        var output = EmitProxyClass(protocolDecl);

        // Should construct proxy from existential container
        Assert.Contains("new TargetProtocolProxy(container, ownsContainer: true)", output);
        // Should use Unsafe.Read to recover the container (fully qualified type name)
        Assert.Contains("Unsafe.Read<Swift.Runtime.ExistentialContainer1>", output);
        // Should call accessor P/Invoke
        Assert.Contains("NativeMethods.SBW_SourceProtocol_method_getTarget_0", output);
        // Should free in finally block
        Assert.Contains("NativeMethods.SBW_SourceProtocol_free_method_getTarget_0(resultPtr)", output);
        Assert.Contains("finally", output);
    }

    [Fact]
    public void EmitProxyClass_ExistentialReturnMethod_NonThrowing_NoErrorHandling()
    {
        // Non-throwing existential return should NOT emit error handling code
        RegisterProtocol("TargetProtocol");
        var protocolDecl = CreateSimpleProtocol("SourceProtocol");
        var existentialReturn = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.TargetProtocol") });

        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "getTarget",
            MangledName = "$sgetTarget",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty, PrivateName = string.Empty,
                    SwiftTypeSpec = existentialReturn,
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = false, IsAsync = false,
            IsSynthesizedAccessor = false
        });

        var output = EmitProxyClass(protocolDecl);

        // Non-throwing should NOT have error handling
        Assert.DoesNotContain("SBW_GetErrorDescription", output);
        Assert.DoesNotContain("SBW_ReleaseError", output);
        Assert.DoesNotContain("SwiftException", output);
        Assert.DoesNotContain("errorOut", output);
    }

    [Fact]
    public void EmitProxyClass_ExistentialReturnMethod_Throwing_EmitsErrorOutParam()
    {
        // Throwing method returning existential should use error out-parameter pattern
        RegisterProtocol("TargetProtocol");
        var protocolDecl = CreateSimpleProtocol("SourceProtocol");
        var existentialReturn = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.TargetProtocol") });

        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "connect",
            MangledName = "$sconnect",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty, PrivateName = string.Empty,
                    SwiftTypeSpec = existentialReturn,
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = true, IsAsync = false,
            IsSynthesizedAccessor = false
        });

        var output = EmitProxyClass(protocolDecl);

        // Should have error out-parameter
        Assert.Contains("IntPtr errorOut = IntPtr.Zero", output);
        // Should check null result for error
        Assert.Contains("resultPtr == IntPtr.Zero", output);
        // Should still construct proxy on success
        Assert.Contains("new TargetProtocolProxy(container, ownsContainer: true)", output);
    }

    [Fact]
    public void EmitProxyClass_ExistentialReturnMethod_Throwing_FreesDescBeforeReleaseError()
    {
        // Throwing existential: must free description buffer BEFORE releasing error
        // (the description buffer may reference memory owned by the error)
        RegisterProtocol("TargetProtocol");
        var protocolDecl = CreateSimpleProtocol("SourceProtocol");
        var existentialReturn = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.TargetProtocol") });

        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "connect",
            MangledName = "$sconnect",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty, PrivateName = string.Empty,
                    SwiftTypeSpec = existentialReturn,
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = true, IsAsync = false,
            IsSynthesizedAccessor = false
        });

        var output = EmitProxyClass(protocolDecl);

        // Error description extraction
        Assert.Contains("SBW_GetErrorDescription(errorOut)", output);
        // SBW_Free must come BEFORE SBW_ReleaseError (order matters for memory safety)
        var freeIdx = output.IndexOf("SBW_Free(_descPtr)", StringComparison.Ordinal);
        var releaseIdx = output.IndexOf("SBW_ReleaseError(errorOut)", StringComparison.Ordinal);
        Assert.True(freeIdx >= 0, "Expected SBW_Free(_descPtr) in output");
        Assert.True(releaseIdx >= 0, "Expected SBW_ReleaseError(errorOut) in output");
        Assert.True(freeIdx < releaseIdx, "SBW_Free must come before SBW_ReleaseError");
        // Should throw SwiftException
        Assert.Contains("SwiftException(_errorMessage)", output);
    }

    [Fact]
    public void EmitProxyClass_ExistentialReturnMethod_Throwing_FreeInFinally()
    {
        // Both error cleanup and success result must use finally blocks
        RegisterProtocol("TargetProtocol");
        var protocolDecl = CreateSimpleProtocol("SourceProtocol");
        var existentialReturn = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.TargetProtocol") });

        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "connect",
            MangledName = "$sconnect",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty, PrivateName = string.Empty,
                    SwiftTypeSpec = existentialReturn,
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = true, IsAsync = false,
            IsSynthesizedAccessor = false
        });

        var output = EmitProxyClass(protocolDecl);

        // Success path free must be in finally block
        var freeSymbol = "SBW_SourceProtocol_free_method_connect_0(resultPtr)";
        Assert.Contains(freeSymbol, output);
        // Error cleanup must be in finally block (SBW_Free + SBW_ReleaseError)
        Assert.Contains("finally", output);
        // Must call the free function for the result on success path
        var successFreeIdx = output.IndexOf(freeSymbol, StringComparison.Ordinal);
        // Find the nearest preceding "finally" before the success free
        var precedingFinally = output.LastIndexOf("finally", successFreeIdx, StringComparison.Ordinal);
        Assert.True(precedingFinally >= 0, "Expected 'finally' before success-path free");
    }

    [Fact]
    public void EmitProxyClass_ExistentialReturnMethod_NoSB0003()
    {
        // Existential-returning methods should be dispatchable (no SB0003 diagnostic)
        RegisterProtocol("TargetProtocol");
        var protocolDecl = CreateSimpleProtocol("SourceProtocol");
        var existentialReturn = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.TargetProtocol") });

        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "getTarget",
            MangledName = "$sgetTarget",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty, PrivateName = string.Empty,
                    SwiftTypeSpec = existentialReturn,
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = false, IsAsync = false,
            IsSynthesizedAccessor = false
        });

        var output = EmitProxyClass(protocolDecl);

        // Find the method declaration and check SB0003 is NOT near it
        var methodIdx = output.IndexOf("ITargetProtocol GetTarget()", StringComparison.Ordinal);
        Assert.True(methodIdx >= 0, "Expected to find 'ITargetProtocol GetTarget()' in output");
        var preMethodText = output.Substring(Math.Max(0, methodIdx - 300), Math.Min(300, methodIdx));
        Assert.DoesNotContain("SB0003", preMethodText);
    }

    [Fact]
    public void EmitProxyClass_ExistentialReturnMethod_DelegatesToCSharpImpl()
    {
        // Existential-returning dispatch should check _csharpImpl first
        RegisterProtocol("TargetProtocol");
        var protocolDecl = CreateSimpleProtocol("SourceProtocol");
        var existentialReturn = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.TargetProtocol") });

        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "getTarget",
            MangledName = "$sgetTarget",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty, PrivateName = string.Empty,
                    SwiftTypeSpec = existentialReturn,
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = false, IsAsync = false,
            IsSynthesizedAccessor = false
        });

        var output = EmitProxyClass(protocolDecl);

        // _csharpImpl delegation must come first (before Swift dispatch)
        Assert.Contains("_csharpImpl != null", output);
        Assert.Contains("_csharpImpl.GetTarget()", output);
    }

    [Fact]
    public void EmitProxyClass_ExistentialReturnMethod_WithStringParam_EmitsPinHandle()
    {
        // Existential return with string param should marshal string via GCHandle
        RegisterProtocol("TargetProtocol");
        RegisterSwiftString();
        var protocolDecl = CreateSimpleProtocol("SourceProtocol");
        var existentialReturn = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.TargetProtocol") });

        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "connect",
            MangledName = "$sconnect",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty, PrivateName = string.Empty,
                    SwiftTypeSpec = existentialReturn,
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = null
                },
                new()
                {
                    Name = "protocol", PrivateName = "protocolString",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = false, IsAsync = false,
            IsSynthesizedAccessor = false
        });

        var output = EmitProxyClass(protocolDecl);

        // String param should be marshalled via UTF-8 encoding + GCHandle pin
        Assert.Contains("Encoding.UTF8.GetBytes", output);
        Assert.Contains("GCHandle.Alloc", output);
        Assert.Contains("Utf8Slice", output);
        // Pin handle cleanup in finally
        Assert.Contains("IsAllocated", output);
    }

    [Fact]
    public void EmitProxyClass_ThrowingExistentialReturn_PInvokeHasErrorOutParam()
    {
        // P/Invoke declaration for throwing existential method should include errorOut param
        RegisterProtocol("TargetProtocol");
        var protocolDecl = CreateSimpleProtocol("SourceProtocol");
        var existentialReturn = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.TargetProtocol") });

        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "connect",
            MangledName = "$sconnect",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty, PrivateName = string.Empty,
                    SwiftTypeSpec = existentialReturn,
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = true, IsAsync = false,
            IsSynthesizedAccessor = false
        });

        var output = EmitProxyClass(protocolDecl);

        // Scope assertions to the NativeMethods section to avoid matching
        // the method body's "IntPtr errorOut = IntPtr.Zero"
        var nativeMethodsIdx = output.IndexOf("class NativeMethods", StringComparison.Ordinal);
        Assert.True(nativeMethodsIdx >= 0, "Expected NativeMethods class in output");
        var nativeMethodsSection = output.Substring(nativeMethodsIdx);

        // P/Invoke accessor for throwing existential should have errorOut in its parameter list
        Assert.Contains("IntPtr containerPtr, IntPtr errorOut", nativeMethodsSection);
        // Should also emit the error helper P/Invokes inside NativeMethods
        Assert.Contains("SBW_GetErrorDescription", nativeMethodsSection);
        Assert.Contains("SBW_ReleaseError", nativeMethodsSection);
        Assert.Contains("SBW_Free", nativeMethodsSection);
    }

    [Fact]
    public void EmitProxyClass_NonThrowingExistentialReturn_PInvokeHasNoErrorOut()
    {
        // P/Invoke declaration for non-throwing existential method should NOT include errorOut
        RegisterProtocol("TargetProtocol");
        var protocolDecl = CreateSimpleProtocol("SourceProtocol");
        var existentialReturn = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.TargetProtocol") });

        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "getTarget",
            MangledName = "$sgetTarget",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty, PrivateName = string.Empty,
                    SwiftTypeSpec = existentialReturn,
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = false, IsAsync = false,
            IsSynthesizedAccessor = false
        });

        var output = EmitProxyClass(protocolDecl);

        // Non-throwing should not have error infrastructure
        Assert.DoesNotContain("SBW_GetErrorDescription", output);
        Assert.DoesNotContain("SBW_ReleaseError", output);
        Assert.DoesNotContain("errorOut", output);
    }

    [Fact]
    public void EmitProxyClass_ExistentialReturnMethod_PInvokeEmitsAccessorAndFree()
    {
        // P/Invoke declarations should include both accessor and free function
        RegisterProtocol("TargetProtocol");
        var protocolDecl = CreateSimpleProtocol("SourceProtocol");
        var existentialReturn = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.TargetProtocol") });

        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "getTarget",
            MangledName = "$sgetTarget",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty, PrivateName = string.Empty,
                    SwiftTypeSpec = existentialReturn,
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = false, IsAsync = false,
            IsSynthesizedAccessor = false
        });

        var output = EmitProxyClass(protocolDecl);

        // NativeMethods should contain accessor P/Invoke
        Assert.Contains("SBW_SourceProtocol_method_getTarget_0", output);
        // NativeMethods should contain free P/Invoke
        Assert.Contains("SBW_SourceProtocol_free_method_getTarget_0", output);
        // Both should be in NativeMethods section
        var nativeMethodsIdx = output.IndexOf("class NativeMethods", StringComparison.Ordinal);
        Assert.True(nativeMethodsIdx >= 0, "Expected NativeMethods class in output");
        var accessorIdx = output.IndexOf("SBW_SourceProtocol_method_getTarget_0", nativeMethodsIdx, StringComparison.Ordinal);
        var freeIdx = output.IndexOf("SBW_SourceProtocol_free_method_getTarget_0", nativeMethodsIdx, StringComparison.Ordinal);
        Assert.True(accessorIdx >= 0, "Accessor P/Invoke must be inside NativeMethods");
        Assert.True(freeIdx >= 0, "Free P/Invoke must be inside NativeMethods");
    }

    #endregion

    #region Closure Method Skip Tracking Tests

    [Fact]
    public void EmitProxyClass_ClosureSkippedMethod_StillEmitsNonClosureMethod()
    {
        // Protocol with two methods: one skipped (closure param), one kept.
        // Pattern: protocol with one skipped (closure) method and one kept method.
        RegisterSwiftString();

        var protocol = CreateSimpleProtocol("EventDelegate");

        // Non-closure method: didReceiveEvent(name:) -> Bool
        protocol.Methods.Add(CreateMethodDecl("didReceiveEvent"));

        // Closure method: onComplete(handler:) — will be passed as skipped
        protocol.Methods.Add(CreateMethodDecl("onComplete"));

        var closureSkipped = new HashSet<string> { "onComplete" };

        var output = EmitProxyClassWithSkips(protocol, closureSkippedMethodKeys: closureSkipped);

        // The proxy class should still be emitted
        Assert.Contains("EventDelegateProxy", output);
        // Non-closure method should have a receiver
        Assert.Contains("didReceiveEvent", output);
    }

    [Fact]
    public void EmitProxyClass_ClosureSkippedProperty_StillEmitsNonClosureProperty()
    {
        // Protocol with a closure-skipped property and a non-closure property.
        RegisterSwiftInt32();
        RegisterSwiftString();

        var protocol = CreateSimpleProtocol("DataDelegate");

        // Non-closure property: count (Int32 getter)
        var getterMethod = CreateMethodDecl("count_get");
        protocol.Properties.Add(new PropertyDecl
        {
            Name = "count",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int32"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = getterMethod } },
            ParentDecl = null,
            ModuleDecl = null
        });

        // Closure property: onUpdate — will be passed as skipped
        var closureGetterMethod = CreateMethodDecl("onUpdate_get");
        protocol.Properties.Add(new PropertyDecl
        {
            Name = "onUpdate",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int32"), // Placeholder type (actual is closure, but it's skipped)
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = closureGetterMethod } },
            ParentDecl = null,
            ModuleDecl = null
        });

        var closureSkippedProps = new HashSet<string> { "onUpdate" };

        var output = EmitProxyClassWithSkips(protocol, closureSkippedPropertyNames: closureSkippedProps);

        // Should still generate the proxy class with the non-closure property
        Assert.Contains("DataDelegateProxy", output);
        Assert.Contains("count", output);
    }

    [Fact]
    public void EmitProxyClass_AllMethodsClosureSkipped_StillEmitsProxyClass()
    {
        // Protocol where ALL methods are closure-skipped.
        // The proxy class should still be emitted (for the protocol interface) but
        // will have no method receivers.
        var protocol = CreateSimpleProtocol("FullClosureProtocol");
        protocol.Methods.Add(CreateMethodDecl("onComplete"));
        protocol.Methods.Add(CreateMethodDecl("onError"));

        var closureSkipped = new HashSet<string> { "onComplete", "onError" };

        var output = EmitProxyClassWithSkips(protocol, closureSkippedMethodKeys: closureSkipped);

        // Proxy class still emitted
        Assert.Contains("FullClosureProtocolProxy", output);
    }

    [Fact]
    public void EmitProxyClass_ClosureSkippedMethod_OmitsVtableSlotEntirely()
    {
        // Vtable-slot layout for a NON-DISPATCHABLE closure method.
        //
        // Swift's EveryProtocolEmitter emits a `fatalError` stub (and NO vtable field) for a method
        // whose signature carries a closure off the dispatch surface — a throwing/async method, or
        // any closure shape other than the dispatchable `() -> Void` family. The producer's
        // MethodEmitsVtableField (== ProtocolVtableMembers.IncludesMethod after the ctor/static/objc
        // pre-skip) returns false for it, so EmitProtocolVtableStruct CONSUMES the slot index then
        // drops the field. The C# proxy MUST mirror that exactly: omit the Swift-vtable field, the
        // local-vtable delegate, AND the Receive_ trampoline — while a following dispatchable method
        // still lands at the index Swift assigned it (skip-but-consume). A C# field for the skipped
        // method would shift every later slot one pointer-width past the address Swift reads.
        //
        // The omission is driven by the method's REAL shape via IncludesMethod, NOT by the C#-side
        // skip sets (those are fillability-only for the vtable now — see Vtables.cs). So the fixture
        // is a genuine non-dispatchable closure method (throwing + a `() -> Void` closure param) —
        // exactly what ProtocolHandler marks closure-skipped — not a plain method with an injected
        // skip key (that simulated a state the real pipeline never produces).
        var protocol = CreateSimpleProtocol("CallbackOwner");

        // onComplete(handler:): throwing method carrying a `() -> Void` closure param. Throwing
        // short-circuits every IsDispatchableClosure* check, so IncludesMethod == false → no slot.
        var onComplete = CreateMethodDecl("onComplete");
        onComplete.Throws = true;
        onComplete.CSSignature.Add(new ArgumentDecl
        {
            Name = "handler",
            PrivateName = "handler",
            SwiftTypeSpec = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty),
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        });
        protocol.Methods.Add(onComplete);

        // cleanup(): plain dispatchable method declared AFTER the skipped closure method.
        protocol.Methods.Add(CreateMethodDecl("cleanup"));

        var onCompleteKey = ProtocolSignatureHelper.GetMethodSignatureKey(onComplete, _typeDatabase, protocol);

        var output = EmitProxyClassWithSkips(protocol,
            closureSkippedMethodKeys: new HashSet<string> { onCompleteKey });

        // The non-dispatchable closure method gets NO vtable presence on any of the three loops.
        Assert.DoesNotContain("Receive_onComplete_0(", output);
        Assert.DoesNotContain("Func_onComplete_0", output);
        Assert.DoesNotContain("func_onComplete_0", output);

        // ...but its index IS consumed: cleanup lands at slot 1, not 0 (skip-but-consume parity with
        // EveryProtocolEmitter.EmitProtocolVtableStruct). cleanup at slot 0 would mean the C# struct
        // under-counted the skipped method and every later reverse-dispatch read shifts.
        Assert.Contains("func_cleanup_1", output);
        Assert.DoesNotContain("func_cleanup_0", output);
    }

    [Fact]
    public void EmitProxyClass_SelfRequirement_SkipsEntireProxy()
    {
        // Protocols with Self requirement can't have proxy classes at all.
        var protocol = CreateSimpleProtocol("SelfBound");
        protocol.HasSelfRequirement = true;
        protocol.Methods.Add(CreateMethodDecl("compare"));

        var output = EmitProxyClass(protocol);

        // No proxy class should be emitted
        Assert.DoesNotContain("SelfBoundProxy", output);
    }

    [Fact]
    public void EmitProxyClass_AssociatedTypes_SkipsEntireProxy()
    {
        // Protocols with associated types can't have proxy classes.
        var protocol = CreateSimpleProtocol("Container");
        protocol.AssociatedTypes = new List<AssociatedTypeDecl>
        {
            new AssociatedTypeDecl
            {
                Name = "Element",
                Constraints = new List<string>()
            }
        };
        protocol.Methods.Add(CreateMethodDecl("getElement"));

        var output = EmitProxyClass(protocol);

        // No proxy class should be emitted
        Assert.DoesNotContain("ContainerProxy", output);
    }

    #endregion

    #region Witness-Dispatch Eligibility Parity (Defect cluster D / Finding 8 forward path)

    // The Swift @_cdecl wrapper emission (WitnessDispatchEmitter.EmitWitnessDispatchFunctions)
    // and the C# proxy emission (EmitWitnessDispatchPInvokes decl walk + EmitInterfaceImplementation
    // caller walk) independently compute the per-member dispatch INDEX baked into the
    // SBW_<proto>_method_<name>_<idx> symbol. They MUST agree on which members participate.
    // Swift skips @objc-optional methods BEFORE bumping the index; if the C# walks bump the
    // index for the optional member they reference SBW_<proto>_method_<req>_<N+1> while Swift
    // only ever exported SBW_<proto>_method_<req>_<N> — a symbol that isn't in the binary, so
    // the existential caller dies with EntryPointNotFoundException at runtime. These tests pin
    // the cross-walk parity through the shared eligibility predicates.

    [Fact]
    public void EmitProxyClass_ObjCOptionalMethod_DoesNotDriftRequiredMethodDispatchIndex()
    {
        // Member order: required `alpha` (idx 0), @objc-optional `beta` (skipped both sides),
        // required `gamma`. Swift assigns gamma idx 1 (beta consumes no index). The C# proxy
        // must reference SBW_..._method_gamma_1 — NOT _gamma_2 — and must not reference the
        // optional `beta` at all (Swift never exports it).
        var protocol = CreateSimpleProtocol("WitnessMethodParity");

        var alpha = CreateMethodDecl("alpha");
        var beta = CreateMethodDecl("beta");
        beta.IsObjCOptional = true;
        var gamma = CreateMethodDecl("gamma");

        protocol.Methods.Add(alpha);
        protocol.Methods.Add(beta);
        protocol.Methods.Add(gamma);

        var output = EmitProxyClass(protocol);

        // Positive control: the harness really does emit forward-dispatch method symbols.
        Assert.Contains("SBW_WitnessMethodParity_method_alpha_0", output);

        // Core fix: gamma keeps the index Swift gave it (1), because the optional method
        // between them consumes no index on either side.
        Assert.Contains("SBW_WitnessMethodParity_method_gamma_1", output);
        Assert.DoesNotContain("SBW_WitnessMethodParity_method_gamma_2", output);

        // The @objc-optional method participates in NO forward dispatch (Swift skips it), so the
        // proxy must not declare or call a symbol the wrapper never exported.
        Assert.DoesNotContain("SBW_WitnessMethodParity_method_beta_", output);
    }

    [Fact]
    public void EmitProxyClass_ObjCOptionalAndCustomActorProperties_NotWitnessDispatched()
    {
        // The Swift wrapper skips @objc-optional properties AND custom-actor-isolated (non-main)
        // properties from witness dispatch. The C# proxy walks must mirror that exactly: emit no
        // SBW getter P/Invoke and no call site for those properties, so nothing references a
        // symbol the wrapper never exported. A normal required property is the positive control.
        RegisterSwiftInt32();
        var protocol = CreateSimpleProtocol("WitnessPropertyParity");

        protocol.Properties.Add(CreateInt32Getter("delta"));                         // required
        protocol.Properties.Add(CreateInt32Getter("epsilon", isObjCOptional: true)); // @objc optional
        protocol.Properties.Add(CreateInt32Getter("zeta", isCustomActorIsolated: true)); // custom actor

        var output = EmitProxyClass(protocol);

        // Positive control: a plain required blittable property IS witness-dispatched.
        Assert.Contains("SBW_WitnessPropertyParity_get_delta_0", output);

        // @objc-optional and custom-actor properties are skipped by the Swift wrapper, so the
        // proxy must not reference their (non-existent) getter symbols.
        Assert.DoesNotContain("SBW_WitnessPropertyParity_get_epsilon_", output);
        Assert.DoesNotContain("SBW_WitnessPropertyParity_get_zeta_", output);
    }

    private static PropertyDecl CreateInt32Getter(string name, bool isObjCOptional = false, bool isCustomActorIsolated = false)
    {
        return new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int32"),
            IsStatic = false,
            HasStorage = false,
            IsObjCOptional = isObjCOptional,
            IsActorIsolated = isCustomActorIsolated,
            IsMainActorIsolated = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl($"{name}_get") }
            },
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    #endregion

    #region Helper Methods

    private string EmitProxyClass(ProtocolDecl protocolDecl)
    {
        var stringWriter = new StringWriter();
        var writer = new CSharpWriter(stringWriter);
        _emitter.EmitProxyClass(writer, protocolDecl);
        return stringWriter.ToString();
    }

    // Returns the brace-matched body of the method whose signature begins with
    // <paramref name="signaturePrefix"/>. Boundary-matching on the next attribute is
    // unreliable because emitted comments can contain the attribute text verbatim
    // (e.g. "... propagate across the [UnmanagedCallersOnly] boundary ...").
    private static string ExtractMethodBody(string source, string signaturePrefix)
    {
        var start = source.IndexOf(signaturePrefix, StringComparison.Ordinal);
        Assert.True(start >= 0, $"expected a method matching '{signaturePrefix}'");
        var open = source.IndexOf('{', start);
        Assert.True(open >= 0, $"no method body found for '{signaturePrefix}'");
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0)
                return source.Substring(start, i - start + 1);
        }
        throw new Xunit.Sdk.XunitException($"unbalanced braces in method body for '{signaturePrefix}'");
    }

    private string EmitProxyClassWithSkips(
        ProtocolDecl protocolDecl,
        HashSet<string> closureSkippedMethodKeys = null,
        HashSet<string> closureSkippedPropertyNames = null,
        HashSet<string> skippedMethodKeys = null,
        HashSet<string> skippedPropertyNames = null)
    {
        var stringWriter = new StringWriter();
        var writer = new CSharpWriter(stringWriter);
        _emitter.EmitProxyClass(
            writer, protocolDecl,
            skippedMethodKeys: skippedMethodKeys,
            skippedPropertyNames: skippedPropertyNames,
            closureSkippedMethodKeys: closureSkippedMethodKeys,
            closureSkippedPropertyNames: closureSkippedPropertyNames);
        return stringWriter.ToString();
    }

    // ---- Sibling-fan-out receiver harness (Design B2) --------------------------------
    //
    // The sibling-fan-out path is reached after every candidate-interface lookup misses. Both it
    // and the canonical no-sibling path resolve the impl from ProxyLifetimeTracker and FailFast on
    // an all-miss (Finding 14(b) — see EmitProxyClass_SiblingFanoutAllMiss_*_FailFasts and
    // ReceiverGuardsAgainstDeadImpl); neither fabricates a carrier-sized fallback buffer anymore.
    // These helpers seed a one-entry sibling map (a distinct dummy sibling protocol) keyed exactly
    // as the receiver looks it up, so emission takes the fan-out path: the primary + sibling
    // lookup-hit blocks emit ahead of the asserted FailFast terminal.
    private string EmitProxyClassWithContext(ProtocolDecl protocolDecl, ModuleEmissionContext ctx)
    {
        var emitter = new ProtocolProxyEmitter(_typeDatabase, NullLogger.Instance, "TestModule", ctx);
        var stringWriter = new StringWriter();
        var writer = new CSharpWriter(stringWriter);
        emitter.EmitProxyClass(writer, protocolDecl);
        return stringWriter.ToString();
    }

    private string EmitProxyClassWithPropertySibling(ProtocolDecl protocolDecl, string propertyName, bool siblingHasSetter = false)
    {
        var ctx = new ModuleEmissionContext();
        ctx.SetSiblingPropertyFallbacks(
            new Dictionary<(string ProtoQName, string PropertyName), IReadOnlyList<ModuleEmissionContext.SiblingPropertyFallback>>
            {
                [(EveryProtocolEmitter.GetProtocolFallbackKey(protocolDecl), propertyName)] =
                    new[] { new ModuleEmissionContext.SiblingPropertyFallback(CreateSimpleProtocol("SiblingFallbackProto"), siblingHasSetter) }
            });
        return EmitProxyClassWithContext(protocolDecl, ctx);
    }

    private string EmitProxyClassWithMethodSibling(ProtocolDecl protocolDecl, MethodDecl method)
    {
        var ctx = new ModuleEmissionContext();
        ctx.SetSiblingMethodFallbacks(
            new Dictionary<(string ProtoQName, string MethodKey), IReadOnlyList<ModuleEmissionContext.SiblingMethodFallback>>
            {
                [(EveryProtocolEmitter.GetProtocolFallbackKey(protocolDecl), EveryProtocolEmitter.GetMethodSiblingMapKey(method))] =
                    new[] { new ModuleEmissionContext.SiblingMethodFallback(CreateSimpleProtocol("SiblingFallbackProto")) }
            });
        return EmitProxyClassWithContext(protocolDecl, ctx);
    }

    private string EmitProxyClassWithSubscriptSibling(ProtocolDecl protocolDecl, SubscriptDecl subscript, int index)
    {
        var ctx = new ModuleEmissionContext();
        // Mirror the receiver's key shape (ProtocolProxyEmitter.Receivers.cs): the literal
        // "subscript_{index}(" + comma-joined index-param Swift type strings + ")".
        var subscriptKey = $"subscript_{index}(" +
            string.Join(",", subscript.IndexParameters.Select(p => p.SwiftTypeSpec?.ToString() ?? "")) + ")";
        ctx.SetSiblingSubscriptFallbacks(
            new Dictionary<(string ProtoQName, string SubscriptKey), IReadOnlyList<ModuleEmissionContext.SiblingSubscriptFallback>>
            {
                [(EveryProtocolEmitter.GetProtocolFallbackKey(protocolDecl), subscriptKey)] =
                    new[] { new ModuleEmissionContext.SiblingSubscriptFallback(CreateSimpleProtocol("SiblingFallbackProto"), index, false) }
            });
        return EmitProxyClassWithContext(protocolDecl, ctx);
    }

    private static ProtocolDecl CreateSimpleProtocol(string name)
    {
        return new ProtocolDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}P",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            HasSelfRequirement = false,
            IsClassBound = false,
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private ProtocolDecl CreateProtocolWithProperty(string name, string propertyName, bool hasGetter, bool hasSetter)
    {
        return CreateProtocolWithProperty(name, propertyName, hasGetter, hasSetter, new NamedTypeSpec("Swift.Int"));
    }

    private ProtocolDecl CreateProtocolWithProperty(string name, string propertyName, bool hasGetter, bool hasSetter, TypeSpec typeSpec)
    {
        var protocol = CreateSimpleProtocol(name);

        var getterMethod = CreateMethodDecl($"{propertyName}_get");
        var setterMethod = CreateMethodDecl($"{propertyName}_set");

        var accessors = new List<AccessorDecl>();
        if (hasGetter)
            accessors.Add(new GetAccessorDecl { Method = getterMethod });
        if (hasSetter)
            accessors.Add(new SetAccessorDecl { Method = setterMethod });

        protocol.Properties.Add(new PropertyDecl
        {
            Name = propertyName,
            SwiftTypeSpec = typeSpec,
            IsStatic = false,
            HasStorage = false,
            Accessors = accessors,
            ParentDecl = null,
            ModuleDecl = null
        });

        return protocol;
    }

    private ProtocolDecl CreateProtocolWithMethod(string name, string methodName)
    {
        var protocol = CreateSimpleProtocol(name);

        protocol.Methods.Add(CreateMethodDecl(methodName));

        return protocol;
    }

    private static MethodDecl CreateMethodDecl(string name)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "",
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
    }

    /// <summary>
    /// Registers Swift.String → Swift.SwiftString in the test TypeDatabase so the
    /// projected C# property type is SwiftString and String dispatch is enabled.
    /// </summary>
    private void RegisterSwiftString()
    {
        _typeDatabase.AddOutOfModuleTypes(new[]
        {
            (SwiftTypeName.FromModuleQualifiedName("Swift.String"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSSWsMA",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            })
        });
    }

    /// <summary>
    /// Registers Swift.Int32 → int in the test TypeDatabase so the
    /// projected C# type is blittable (int) and dispatch is enabled.
    /// </summary>
    private void RegisterSwiftInt32()
    {
        _typeDatabase.AddOutOfModuleTypes(new[]
        {
            (SwiftTypeName.FromModuleQualifiedName("Swift.Int32"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int32"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int32"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            })
        });
    }

    /// <summary>
    /// Registers Swift.Int → nint in the test TypeDatabase.
    /// Uses CSharpTypeName.NIntType (FullyQualifiedName = "nint") to match
    /// the real Swift type database. F1 narrowing converts property type to int.
    /// </summary>
    private void RegisterSwiftInt()
    {
        _typeDatabase.AddOutOfModuleTypes(new[]
        {
            (SwiftTypeName.FromModuleQualifiedName("Swift.Int"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.NIntType,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            })
        });
    }

    /// <summary>
    /// Registers Swift.UInt → nuint in the test TypeDatabase.
    /// Uses CSharpTypeName.NUIntType (FullyQualifiedName = "nuint") to match
    /// the real Swift type database. F1 narrowing converts property type to uint.
    /// </summary>
    private void RegisterSwiftUInt()
    {
        _typeDatabase.AddOutOfModuleTypes(new[]
        {
            (SwiftTypeName.FromModuleQualifiedName("Swift.UInt"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.NUIntType,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.UInt"),
                MetadataAccessor = "$sSuMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            })
        });
    }

    private void RegisterClass(string name)
    {
        _typeDatabase.AddOutOfModuleTypes(new[]
        {
            (SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", name),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Class
            })
        });
    }

    private void RegisterObjCRootedClass(string name)
    {
        _typeDatabase.AddOutOfModuleTypes(new[]
        {
            (SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", name),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.ObjCRooted,
                Kind = TypeRecordKind.Class
            })
        });
    }

    private void RegisterNonFrozenStruct(string name)
    {
        _typeDatabase.AddOutOfModuleTypes(new[]
        {
            (SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", name),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            })
        });
    }

    private void RegisterNativeRemappedClass(string name, string nativeTypeName)
    {
        _typeDatabase.AddOutOfModuleTypes(new[]
        {
            (SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", name),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Class,
                NativeTypeName = CSharpTypeName.FromNamespaceAndName(
                    nativeTypeName.Contains('.') ? nativeTypeName[..nativeTypeName.LastIndexOf('.')] : "",
                    nativeTypeName.Contains('.') ? nativeTypeName[(nativeTypeName.LastIndexOf('.') + 1)..] : nativeTypeName)
            })
        });
    }

    private void RegisterFrozenRefFieldStruct(string name)
    {
        _typeDatabase.AddOutOfModuleTypes(new[]
        {
            (SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", name),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            })
        });
    }

    #endregion

    #region ClassReturn / StructReturn C# Emission Tests

    [Fact]
    public void EmitProxyClass_ClassReturnMethod_EmitsArcReleaseInCatch()
    {
        // ClassReturn catch block must release the retained Swift object
        RegisterClass("ResponseAPDU");
        var protocolDecl = CreateSimpleProtocol("CardChannel");
        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "transmit",
            MangledName = "$stransmit",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new() { Name = string.Empty, PrivateName = string.Empty,
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.ResponseAPDU"),
                    IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = null }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = false, IsAsync = false, IsSynthesizedAccessor = false
        });

        var output = EmitProxyClass(protocolDecl);

        // Direct MarshalFromSwift — no buffer classPayload allocation/free
        Assert.Contains("Arc.Release(resultPtr)", output);
        Assert.DoesNotContain("classPayload", output);
        Assert.Contains("MarshalFromSwift", output);
    }

    [Fact]
    public void EmitProxyClass_ClassReturnPropertyGetter_DirectMarshalWithArcReleaseCatch()
    {
        // ClassReturn: direct MarshalFromSwift — no buffer allocation needed.
        // Arc.Release in catch prevents leaks if MarshalFromSwift throws.
        RegisterClass("ResponseAPDU");
        var protocolDecl = CreateProtocolWithProperty("CardChannel", "lastResponse",
            hasGetter: true, hasSetter: false, new NamedTypeSpec("TestModule.ResponseAPDU"));

        var output = EmitProxyClass(protocolDecl);

        // No classPayload buffer — direct MarshalFromSwift
        Assert.DoesNotContain("classPayload", output);
        Assert.Contains("MarshalFromSwift", output);
        // Arc.Release in catch only (on success, retained reference consumed by SafeHandle)
        Assert.Contains("catch { Arc.Release(resultPtr); throw; }", output);
    }

    [Fact]
    public void EmitProxyClass_ClassReturnMethod_UsesFullyQualifiedSwiftMarshal()
    {
        // Must use Swift.Runtime.InteropServices.SwiftMarshal, not local MarshalFromSwift
        RegisterClass("ResponseAPDU");
        var protocolDecl = CreateSimpleProtocol("CardChannel");
        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "transmit",
            MangledName = "$stransmit",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new() { Name = string.Empty, PrivateName = string.Empty,
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.ResponseAPDU"),
                    IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = null }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = false, IsAsync = false, IsSynthesizedAccessor = false
        });

        var output = EmitProxyClass(protocolDecl);

        // Type is fully qualified in the generated code
        Assert.Contains("Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwift<TestModule.ResponseAPDU>", output);
    }

    [Fact]
    public void EmitProxyClass_StructReturnMethod_NonFrozen_UsesCatchNotFinally()
    {
        // Non-frozen struct: SafeHandle takes buffer ownership, catch-only cleanup
        RegisterNonFrozenStruct("CardStatus");
        var protocolDecl = CreateSimpleProtocol("Card");
        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "getStatus",
            MangledName = "$sgetStatus",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new() { Name = string.Empty, PrivateName = string.Empty,
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.CardStatus"),
                    IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = null }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = false, IsAsync = false, IsSynthesizedAccessor = false
        });

        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("SwiftIndirectResult", output);
        Assert.Contains("catch { NativeMemory.Free((void*)buffer); throw; }", output);
        Assert.DoesNotContain("finally { NativeMemory.Free((void*)buffer); }", output);
    }

    [Fact]
    public void EmitProxyClass_StructReturnProperty_FrozenRefFields_UsesFinallyNotCatch()
    {
        // Frozen+RefFields: NewFromPayload copies to new buffer, original must be freed on success
        RegisterFrozenRefFieldStruct("BufferedData");
        var protocolDecl = CreateProtocolWithProperty("DataSource", "data",
            hasGetter: true, hasSetter: false, new NamedTypeSpec("TestModule.BufferedData"));

        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("finally { NativeMemory.Free((void*)buffer); }", output);
        Assert.DoesNotContain("catch { NativeMemory.Free((void*)buffer); throw; }", output);
    }

    [Fact]
    public void EmitProxyClass_StructReturnMethod_EmitsSwiftIndirectResult()
    {
        // StructReturn must use SwiftIndirectResult + SwiftMarshal.MarshalFromSwift
        RegisterNonFrozenStruct("CardStatus");
        var protocolDecl = CreateSimpleProtocol("Card");
        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "getStatus",
            MangledName = "$sgetStatus",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new() { Name = string.Empty, PrivateName = string.Empty,
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.CardStatus"),
                    IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = null }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = false, IsAsync = false, IsSynthesizedAccessor = false
        });

        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("SwiftIndirectResult", output);
        Assert.Contains("SwiftObjectHelper<TestModule.CardStatus>.GetTypeMetadata()", output);
        Assert.Contains("Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwift<TestModule.CardStatus>", output);
    }

    [Fact]
    public void EmitProxyClass_ClassReturnMethod_PInvokeReturnsIntPtr()
    {
        // ClassReturn P/Invoke should return IntPtr, no free function
        RegisterClass("ResponseAPDU");
        var protocolDecl = CreateSimpleProtocol("CardChannel");
        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "transmit",
            MangledName = "$stransmit",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new() { Name = string.Empty, PrivateName = string.Empty,
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.ResponseAPDU"),
                    IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = null }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = false, IsAsync = false, IsSynthesizedAccessor = false
        });

        var output = EmitProxyClass(protocolDecl);

        // P/Invoke returns IntPtr (public static partial)
        Assert.Contains("partial IntPtr SBW_CardChannel_method_transmit_0", output);
        // No free function (SafeHandle handles ARC release)
        Assert.DoesNotContain("SBW_CardChannel_free_method_transmit_0", output);
    }

    [Fact]
    public void EmitProxyClass_StructReturnMethod_PInvokeReturnsVoidWithResultBuf()
    {
        // StructReturn P/Invoke should return void and have resultBuf param
        RegisterNonFrozenStruct("CardStatus");
        var protocolDecl = CreateSimpleProtocol("Card");
        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "getStatus",
            MangledName = "$sgetStatus",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new() { Name = string.Empty, PrivateName = string.Empty,
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.CardStatus"),
                    IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = null }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = false, IsAsync = false, IsSynthesizedAccessor = false
        });

        var output = EmitProxyClass(protocolDecl);

        // P/Invoke returns void with resultBuf param (public static partial)
        Assert.Contains("partial void SBW_Card_method_getStatus_0", output);
        Assert.Contains("IntPtr resultBuf", output);
        // No free function (SafeHandle owns buffer)
        Assert.DoesNotContain("SBW_Card_free_method_getStatus_0", output);
    }

    [Fact]
    public void EmitProxyClass_ThrowingClassReturn_ChecksResultPtrZero()
    {
        // Throwing class return: check resultPtr == IntPtr.Zero for error
        RegisterClass("ResponseAPDU");
        var protocolDecl = CreateSimpleProtocol("CardChannel");
        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "tryTransmit",
            MangledName = "$stryTransmit",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new() { Name = string.Empty, PrivateName = string.Empty,
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.ResponseAPDU"),
                    IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = null }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = true, IsAsync = false, IsSynthesizedAccessor = false
        });

        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("resultPtr == IntPtr.Zero", output);
        Assert.Contains("SwiftException", output);
        Assert.Contains("Arc.Release(resultPtr)", output);
    }

    [Fact]
    public void EmitProxyClass_ThrowingStructReturn_ChecksErrorOutNonZero()
    {
        // Throwing struct return: check errorOut != IntPtr.Zero for error
        RegisterNonFrozenStruct("CardStatus");
        var protocolDecl = CreateSimpleProtocol("Card");
        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "tryGetStatus",
            MangledName = "$stryGetStatus",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new() { Name = string.Empty, PrivateName = string.Empty,
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.CardStatus"),
                    IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = null }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = true, IsAsync = false, IsSynthesizedAccessor = false
        });

        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("errorOut != IntPtr.Zero", output);
        Assert.Contains("SwiftException", output);
        Assert.Contains("SwiftIndirectResult", output);
    }

    #endregion

    #region Class/Struct Param Dispatch Tests

    [Fact]
    public void EmitProxyClass_ClassParam_EmitsPayloadDangerousGetHandle()
    {
        RegisterClass("MPIMap");
        var protocolDecl = CreateSimpleProtocol("MapDelegate");
        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "onMapChanged",
            MangledName = "$sonMapChanged",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new() { Name = string.Empty, PrivateName = string.Empty,
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = null },
                new() { Name = "map", PrivateName = "map",
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.MPIMap"),
                    IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = null }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = false, IsAsync = false, IsSynthesizedAccessor = false
        });

        var output = EmitProxyClass(protocolDecl);

        Assert.Contains(".Payload.DangerousGetHandle()", output);
        Assert.DoesNotContain("DiagnosticId = \"SB0003\"", output);
    }

    [Fact]
    public void EmitProxyClass_StructParam_EmitsPayloadDangerousGetHandle()
    {
        RegisterNonFrozenStruct("CardStatus");
        var protocolDecl = CreateSimpleProtocol("StatusDelegate");
        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "onStatus",
            MangledName = "$sonStatus",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new() { Name = string.Empty, PrivateName = string.Empty,
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = null },
                new() { Name = "status", PrivateName = "status",
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.CardStatus"),
                    IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = null }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = false, IsAsync = false, IsSynthesizedAccessor = false
        });

        var output = EmitProxyClass(protocolDecl);

        Assert.Contains(".Payload.DangerousGetHandle()", output);
        Assert.DoesNotContain("DiagnosticId = \"SB0003\"", output);
    }

    [Fact]
    public void EmitProxyClass_MixedParams_StringClassBlittable_CorrectMarshalling()
    {
        RegisterSwiftString();
        RegisterClass("Config");
        RegisterSwiftInt32();
        var protocolDecl = CreateSimpleProtocol("Handler");
        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "configure",
            MangledName = "$sconfigure",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new() { Name = string.Empty, PrivateName = string.Empty,
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = null },
                new() { Name = "name", PrivateName = "name",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                    IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = null },
                new() { Name = "config", PrivateName = "config",
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.Config"),
                    IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = null },
                new() { Name = "count", PrivateName = "count",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int32"),
                    IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = null }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = false, IsAsync = false, IsSynthesizedAccessor = false
        });

        var output = EmitProxyClass(protocolDecl);

        // String: UTF-8 encoding
        Assert.Contains("System.Text.Encoding.UTF8.GetBytes", output);
        // Class: SafeHandle payload
        Assert.Contains("config.Payload.DangerousGetHandle()", output);
        // Blittable: simple copy
        Assert.Contains("var arg2Slice = count", output);
        // All dispatched, no SB0003 attribute
        Assert.DoesNotContain("DiagnosticId = \"SB0003\"", output);
    }

    [Fact]
    public void EmitProxyClass_ClassReturnProperty_StillUsesClassReturnGetterPath()
    {
        // Regression test: class/struct properties should still use ClassReturn/StructReturn
        // getter path, not be treated as blittable dispatch
        RegisterClass("ResponseAPDU");
        var protocolDecl = CreateProtocolWithProperty("CardChannel", "lastResponse",
            hasGetter: true, hasSetter: false, new NamedTypeSpec("TestModule.ResponseAPDU"));

        var output = EmitProxyClass(protocolDecl);

        // Should use ClassReturn getter path with SwiftMarshal
        Assert.Contains("Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwift<TestModule.ResponseAPDU>", output);
        // Should NOT be marked as SB0003
        Assert.DoesNotContain("DiagnosticId = \"SB0003\"", output);
    }

    [Fact]
    public void EmitProxyClass_ClassReturnPropertyGetter_EmitsPInvokeInNativeMethods()
    {
        // Regression test for Finding 1: class/struct property getters must have
        // matching P/Invoke declarations in NativeMethods. Previously, IsPropertyGetterDispatchable
        // returned true for class types (because IsTypeDispatchable was widened), causing the
        // property to enter the blittable P/Invoke branch where it was rejected by the
        // string/blittable filter → no P/Invoke emitted → missing NativeMethods members.
        RegisterClass("ResponseAPDU");
        var protocolDecl = CreateProtocolWithProperty("CardChannel", "lastResponse",
            hasGetter: true, hasSetter: false, new NamedTypeSpec("TestModule.ResponseAPDU"));

        var output = EmitProxyClass(protocolDecl);

        // ClassReturn getter P/Invoke must be present in NativeMethods
        Assert.Contains("SBW_CardChannel_get_lastResponse_0", output);
        // Should use ClassReturn getter path (returns IntPtr, no free function)
        Assert.Contains("Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwift<TestModule.ResponseAPDU>", output);
        // No SB0003
        Assert.DoesNotContain("DiagnosticId = \"SB0003\"", output);
    }

    [Fact]
    public void EmitProxyClass_ClassReturnMethodWithClassParam_BothDispatch()
    {
        // Method with class return AND class param should both dispatch correctly
        RegisterClass("ResponseAPDU");
        RegisterClass("CommandAPDU");
        var protocolDecl = CreateSimpleProtocol("CardChannel");
        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "transmit",
            MangledName = "$stransmit",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new() { Name = string.Empty, PrivateName = string.Empty,
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.ResponseAPDU"),
                    IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = null },
                new() { Name = "command", PrivateName = "command",
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.CommandAPDU"),
                    IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = null }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = false, IsAsync = false, IsSynthesizedAccessor = false
        });

        var output = EmitProxyClass(protocolDecl);

        // Class param marshalled via Payload
        Assert.Contains(".Payload.DangerousGetHandle()", output);
        // Class return via SwiftMarshal
        Assert.Contains("SwiftMarshal.MarshalFromSwift<TestModule.ResponseAPDU>", output);
        // No SB0003
        Assert.DoesNotContain("DiagnosticId = \"SB0003\"", output);
    }

    [Fact]
    public void EmitProxyClass_NativeRemappedClassParam_NotDispatchedAsClassParam()
    {
        // Regression test for Finding 2: native-remapped classes (e.g., Foundation.URL → NSUrl)
        // should NOT be treated as dispatchable class params because they use different
        // marshalling (FromX/ToX) and don't have .Payload.
        RegisterNativeRemappedClass("NativeUrl", "Foundation.NSUrl");
        var protocolDecl = CreateSimpleProtocol("UrlHandler");
        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "handleUrl",
            MangledName = "$shandleUrl",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new() { Name = string.Empty, PrivateName = string.Empty,
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = null },
                new() { Name = "url", PrivateName = "url",
                    SwiftTypeSpec = new NamedTypeSpec("TestModule.NativeUrl"),
                    IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = null }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = false, IsAsync = false, IsSynthesizedAccessor = false
        });

        var output = EmitProxyClass(protocolDecl);

        // Native-remapped param should NOT be dispatched via .Payload.DangerousGetHandle()
        Assert.DoesNotContain(".Payload.DangerousGetHandle()", output);
        // Should be SB0003 since native-remapped is not dispatchable
        Assert.Contains("DiagnosticId = \"SB0003\"", output);
    }

    #endregion

    #region BoundGenericReturn (F4) Tests

    [Fact]
    public void EmitProxyClass_ArrayReturnMethod_EmitsMarshalFromSwiftWithAsProjected()
    {
        RegisterSwiftString();
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        RegisterProtocol("TestProtocol");

        var arrayType = new NamedTypeSpec("Swift.Array");
        arrayType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "getItems",
            MangledName = "$sgetItems",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new() { Name = "", PrivateName = "",
                    SwiftTypeSpec = arrayType,
                    IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = null }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = false, IsAsync = false, IsSynthesizedAccessor = false
        });

        var output = EmitProxyClass(protocolDecl);

        // Should contain MarshalFromSwift with SwiftArray container type
        Assert.Contains("SwiftMarshal.MarshalFromSwift<SwiftArray<SwiftString>>(resultPtr)", output);
        // Should contain AsProjected conversion
        Assert.Contains(".AsProjected(", output);
        // Should NOT have NotSupportedException for this method
        Assert.DoesNotContain("Cannot call method 'GetItems'", output);
        // Should have free function call
        Assert.Contains("SBW_TestProtocol_free_method_getItems_0", output);
    }

    [Fact]
    public void EmitProxyClass_DictionaryReturnMethod_EmitsMarshalFromSwift()
    {
        RegisterSwiftString();
        RegisterSwiftInt();
        RegisterSwiftDictionary();
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        RegisterProtocol("TestProtocol");

        var dictType = new NamedTypeSpec("Swift.Dictionary");
        dictType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        dictType.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));

        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "getMap",
            MangledName = "$sgetMap",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new() { Name = "", PrivateName = "",
                    SwiftTypeSpec = dictType,
                    IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = null }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = false, IsAsync = false, IsSynthesizedAccessor = false
        });

        var output = EmitProxyClass(protocolDecl);

        // Should contain MarshalFromSwift with SwiftDictionary container type
        Assert.Contains("SwiftMarshal.MarshalFromSwift<SwiftDictionary<SwiftString, nint>>(resultPtr)", output);
        // Should contain free function P/Invoke
        Assert.Contains("SBW_TestProtocol_free_method_getMap_0", output);
    }

    [Fact]
    public void EmitProxyClass_SetReturnMethod_EmitsMarshalFromSwift()
    {
        RegisterSwiftInt32();
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        RegisterProtocol("TestProtocol");

        var setType = new NamedTypeSpec("Swift.Set");
        setType.GenericParameters.Add(new NamedTypeSpec("Swift.Int32"));

        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "getIds",
            MangledName = "$sgetIds",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new() { Name = "", PrivateName = "",
                    SwiftTypeSpec = setType,
                    IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = null }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = false, IsAsync = false, IsSynthesizedAccessor = false
        });

        var output = EmitProxyClass(protocolDecl);

        // Should contain MarshalFromSwift with SwiftSet container type
        Assert.Contains("SwiftMarshal.MarshalFromSwift<SwiftSet<int>>(resultPtr)", output);
        // Should contain free function P/Invoke
        Assert.Contains("SBW_TestProtocol_free_method_getIds_0", output);
    }

    [Fact]
    public void EmitProxyClass_ThrowingCollectionReturn_EmitsErrorHandling()
    {
        RegisterSwiftString();
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        RegisterProtocol("TestProtocol");

        var arrayType = new NamedTypeSpec("Swift.Array");
        arrayType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "fetchItems",
            MangledName = "$sfetchItems",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new() { Name = "", PrivateName = "",
                    SwiftTypeSpec = arrayType,
                    IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = null }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = true, IsAsync = false, IsSynthesizedAccessor = false
        });

        var output = EmitProxyClass(protocolDecl);

        // Should contain error handling pattern
        Assert.Contains("resultPtr == IntPtr.Zero", output);
        Assert.Contains("SBW_GetErrorDescription", output);
        Assert.Contains("SwiftException", output);
    }

    [Fact]
    public void EmitProxyClass_CollectionPropertyGetter_EmitsDispatch()
    {
        RegisterSwiftString();
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        RegisterProtocol("TestProtocol");

        var arrayType = new NamedTypeSpec("Swift.Array");
        arrayType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        protocolDecl.Properties.Add(new PropertyDecl
        {
            Name = "items",
            SwiftTypeSpec = arrayType,
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("items_get") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });

        var output = EmitProxyClass(protocolDecl);

        // Should dispatch to Swift, not throw NotSupportedException
        Assert.Contains("SBW_TestProtocol_get_items_0", output);
        Assert.Contains("SwiftMarshal.MarshalFromSwift<SwiftArray<SwiftString>>(resultPtr)", output);
        // Should have free function call
        Assert.Contains("SBW_TestProtocol_free_get_items_0", output);
    }

    [Fact]
    public void EmitProxyClass_CollectionReturnMethod_HasPInvokeDeclaration()
    {
        RegisterSwiftString();
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        RegisterProtocol("TestProtocol");

        var arrayType = new NamedTypeSpec("Swift.Array");
        arrayType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "getItems",
            MangledName = "$sgetItems",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new() { Name = "", PrivateName = "",
                    SwiftTypeSpec = arrayType,
                    IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = null }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = false, IsAsync = false, IsSynthesizedAccessor = false
        });

        var output = EmitProxyClass(protocolDecl);

        // P/Invoke accessor declaration
        Assert.Contains("SBW_TestProtocol_method_getItems_0", output);
        // P/Invoke free function declaration
        Assert.Contains("SBW_TestProtocol_free_method_getItems_0", output);
    }

    #endregion

    #region Proxy Lifetime Ownership Tests

    [Fact]
    public void Proxy_OwnedContainerFinalizer_ReleasesAdoptedExistentialButIsSuppressedOtherwise()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        // An owned-return proxy (constructed with ownsContainer: true) adopts a Swift-returned
        // existential at +1, so it owns the container's value-witness retains and needs a
        // finalizer to release them if the consumer never calls Dispose. Unlike the
        // C#-impl-backed proxies (whose +1 is anchored by ProxyLifetimeTracker), an adopted
        // return value has no tracker — Dispose or the finalizer is its only release path.
        Assert.Contains("~TestProtocolProxy()", output);
        Assert.Contains("ReleaseAdoptedSwiftContainer", output);
        // The release is gated on ownership: borrowed parameter/payload wraps and zeroed
        // containers are never value-witness Destroyed (doing so would crash).
        Assert.Contains("if (!_ownsContainer)", output);
        // Non-owning constructions suppress finalization in the constructor, so the finalizer
        // only ever runs for proxies that actually own a +1 — preserving the original
        // impl-anchored model's guarantee that a C#-impl-backed proxy never finalizes its
        // Swift instance out from under in-flight Swift dispatch.
        Assert.Contains("if (!ownsContainer)", output);
        Assert.Contains("GC.SuppressFinalize(this)", output);
        // Still no eager "finalized without Dispose" leak warning — a missed Dispose on an
        // owned proxy is recovered by the finalizer, not flagged.
        Assert.DoesNotContain("was finalized without Dispose()", output);
    }

    #endregion

    #region F6: SB0003 Specific Skip Reasons Tests

    [Fact]
    public void SB0003_PropertyMessage_IncludesSpecificReason()
    {
        // A property with an unsupported type should include a specific reason in SB0003
        // Use a type that is not blittable, not string, not class, not struct, not collection
        var typeSpec = new NamedTypeSpec("SomeModule.UnsupportedType");
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "weird", hasGetter: true, hasSetter: false, typeSpec);
        var output = EmitProxyClass(protocolDecl);

        // Should have the specific reason in the Obsolete message
        Assert.Contains("is not dispatchable via witness table", output);
        Assert.Contains("SB0003", output);
    }

    [Fact]
    public void SB0003_MethodMessage_IncludesSpecificReason()
    {
        // An async method should include the async-specific reason
        var protocol = CreateSimpleProtocol("AsyncProto");
        protocol.Methods.Add(new MethodDecl
        {
            Name = "fetchData",
            MangledName = "$sfetchData",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new() { Name = "", PrivateName = "",
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = null }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = false, IsAsync = true, IsSynthesizedAccessor = false
        });
        var output = EmitProxyClass(protocol);

        Assert.Contains("async methods require Swift concurrency runtime", output);
        Assert.Contains("SB0003", output);
    }

    [Fact]
    public void SB0003_SubscriptMessage_SaysNotYetImplemented()
    {
        // Subscripts should have a specific "not yet implemented" reason
        var protocol = CreateSimpleProtocol("IndexableProto");
        protocol.Subscripts.Add(new SubscriptDecl
        {
            Name = "subscript",
            ReturnTypeSpec = new NamedTypeSpec("Swift.Int32"),
            IsStatic = false,
            MangledName = "$ssubscript",
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("subscript_get") }
            },
            IndexParameters = new List<ArgumentDecl>
            {
                new() { Name = "index", PrivateName = "index",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int32"),
                    IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = null }
            },
            ParentDecl = null,
            ModuleDecl = null
        });
        RegisterSwiftInt32();
        var output = EmitProxyClass(protocol);

        Assert.Contains("subscript dispatch is not yet supported", output);
        Assert.Contains("SB0003", output);
    }

    #endregion

    #region Optional Existential Return (F4) Tests

    [Fact]
    public void EmitProxyClass_OptionalExistentialReturn_EmitsNullCheck()
    {
        RegisterProtocol("DataCaching");
        RegisterSwiftOptional();
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        RegisterProtocol("TestProtocol");

        var optionalExistentialType = new NamedTypeSpec("Swift.Optional");
        optionalExistentialType.GenericParameters.Add(
            new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.DataCaching") }));

        protocolDecl.Methods.Add(new MethodDecl
        {
            Name = "findCache",
            MangledName = "$sfindCache",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new() { Name = "", PrivateName = "",
                    SwiftTypeSpec = optionalExistentialType,
                    IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = null }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = null,
            Throws = false, IsAsync = false, IsSynthesizedAccessor = false
        });

        var output = EmitProxyClass(protocolDecl);

        // Should have null check for optional
        Assert.Contains("resultPtr == IntPtr.Zero", output);
        Assert.Contains("return null", output);
        // Should have proxy construction when non-null
        Assert.Contains("new DataCachingProxy(container, ownsContainer: true)", output);
        // Should have free function
        Assert.Contains("SBW_TestProtocol_free_method_findCache_0", output);
    }

    #endregion

    #region P14C: Nested protocol proxy qualification

    [Fact]
    public void EmitProxyClass_NestedProtocol_QualifiesInterfaceWithParentType()
    {
        // When a protocol is nested inside a class, the proxy (emitted at module level)
        // must use the parent-qualified interface name
        var parentClass = new ClassDecl
        {
            Name = "CountryCodePickerViewController",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.CountryCodePickerViewController"),
            MangledName = "$s10TestModule33CountryCodePickerViewControllerC",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            Conformances = new List<TypeConformance>(),
            IsFinal = false,
            ParentDecl = null,
            ModuleDecl = null,
        };

        var protocol = CreateProtocolWithProperty("CellProtocol", "cellValue", hasGetter: true, hasSetter: false);
        protocol.ParentDecl = parentClass;

        var output = EmitProxyClass(protocol);

        // The interface name should be qualified with the parent class name
        Assert.Contains("CountryCodePickerViewController.ICellProtocol", output);
    }

    #endregion

    #region Covariant return forwarder (CS0738)

    [Fact]
    public void IsSwiftClassAssignableTo_DirectSubclass_ReturnsTrue()
    {
        // Property : Column — direct superclass relationship in TypeDatabase.
        _typeDatabase.AddOutOfModuleTypes(new[]
        {
            (SwiftTypeName.FromModuleQualifiedName("TestModule.Column"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Column"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Column"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Class,
            }),
            (SwiftTypeName.FromModuleQualifiedName("TestModule.Property"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Property"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Property"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Class,
                SuperclassTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Column"),
            })
        });

        var assignable = _emitter.IsSwiftClassAssignableTo(
            new NamedTypeSpec("TestModule.Property"),
            new NamedTypeSpec("TestModule.Column"));

        Assert.True(assignable);
    }

    [Fact]
    public void IsSwiftClassAssignableTo_TransitiveSubclass_ReturnsTrue()
    {
        // Leaf : Mid : Root — must walk the chain, not just the direct edge.
        _typeDatabase.AddOutOfModuleTypes(new[]
        {
            (SwiftTypeName.FromModuleQualifiedName("TestModule.Root"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Root"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Root"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Class,
            }),
            (SwiftTypeName.FromModuleQualifiedName("TestModule.Mid"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Mid"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Mid"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Class,
                SuperclassTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Root"),
            }),
            (SwiftTypeName.FromModuleQualifiedName("TestModule.Leaf"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Leaf"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Leaf"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Class,
                SuperclassTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Mid"),
            })
        });

        Assert.True(_emitter.IsSwiftClassAssignableTo(
            new NamedTypeSpec("TestModule.Leaf"),
            new NamedTypeSpec("TestModule.Root")));
    }

    [Fact]
    public void IsSwiftClassAssignableTo_SameType_ReturnsTrue()
    {
        // Identical type pairs are trivially assignable — short-circuited before DB lookup
        // so this works even without a registered TypeRecord.
        Assert.True(_emitter.IsSwiftClassAssignableTo(
            new NamedTypeSpec("TestModule.Foo"),
            new NamedTypeSpec("TestModule.Foo")));
    }

    [Fact]
    public void IsSwiftClassAssignableTo_UnrelatedClasses_ReturnsFalse()
    {
        // Two root-level classes with no inheritance relationship — covariant cast
        // must NOT be considered safe.
        _typeDatabase.AddOutOfModuleTypes(new[]
        {
            (SwiftTypeName.FromModuleQualifiedName("TestModule.A"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "A"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.A"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Class,
            }),
            (SwiftTypeName.FromModuleQualifiedName("TestModule.B"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "B"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.B"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Class,
            })
        });

        Assert.False(_emitter.IsSwiftClassAssignableTo(
            new NamedTypeSpec("TestModule.A"),
            new NamedTypeSpec("TestModule.B")));
    }

    [Fact]
    public void IsSwiftClassAssignableTo_StructInsteadOfClass_ReturnsFalse()
    {
        // Struct kinds don't have C# inheritance — the forwarder cast would be invalid.
        _typeDatabase.AddOutOfModuleTypes(new[]
        {
            (SwiftTypeName.FromModuleQualifiedName("TestModule.RefinedStruct"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "RefinedStruct"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.RefinedStruct"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
            })
        });

        Assert.False(_emitter.IsSwiftClassAssignableTo(
            new NamedTypeSpec("TestModule.RefinedStruct"),
            new NamedTypeSpec("TestModule.BaseClass")));
    }

    [Fact]
    public void IsSwiftClassAssignableTo_UnregisteredRefinedType_ReturnsFalse()
    {
        // No record in TypeDatabase — the assignability check must fail closed
        // (better to surface CS0738 than to emit a forwarder that throws at runtime).
        Assert.False(_emitter.IsSwiftClassAssignableTo(
            new NamedTypeSpec("TestModule.UnknownChild"),
            new NamedTypeSpec("TestModule.UnknownParent")));
    }

    [Fact]
    public void IsSwiftClassAssignableTo_NonNamedTypeSpec_ReturnsFalse()
    {
        // Tuples, closures, and protocol compositions can't satisfy a class hierarchy cast.
        Assert.False(_emitter.IsSwiftClassAssignableTo(
            TupleTypeSpec.Empty,
            new NamedTypeSpec("TestModule.SomeClass")));
    }

    #endregion

    #region Cross-Module Parent Scaffolding (M: same-simple-name disambiguation)

    /// <summary>
    /// When a child protocol inherits two cross-module parents with the same
    /// simple name from different dependency modules, the C# scaffolding
    /// (struct names, fields, P/Invoke wrapper class, cdecl entry point) MUST
    /// disambiguate by source module. Pre-M the struct name was
    /// <c>{Name}SwiftVTable</c> with no module qualifier, so two parents named
    /// "ParentDelegate" in DepA and DepB collided on
    /// <c>ParentDelegateSwiftVTable</c> → CS0102 inside the child proxy class.
    /// </summary>
    [Fact]
    public void EmitProxyClass_CrossModuleParents_SameSimpleName_QualifiesScaffolding()
    {
        RegisterSwiftInt32();

        var depAModule = CreateCrossModuleDep("DepA");
        var depBModule = CreateCrossModuleDep("DepB");
        var parentA = CreateCrossModuleParent("ParentDelegate", "notifyA", depAModule);
        var parentB = CreateCrossModuleParent("ParentDelegate", "notifyB", depBModule);

        // The child lives in TestModule (matches _emitter._moduleName from the
        // fixture's constructor) and inherits both same-simple-name parents
        // from two different dependency modules.
        var childModule = new ModuleDecl
        {
            Name = "TestModule",
            ParentDecl = null, ModuleDecl = null,
            Properties = new(), Methods = new(), Types = new(),
            Dependencies = new(), Protocols = new(),
            DependencyProtocols = new()
            {
                ["DepA"] = new List<ProtocolDecl> { parentA },
                ["DepB"] = new List<ProtocolDecl> { parentB },
            },
        };
        var child = CreateSimpleProtocol("InheritsBothDelegate");
        child.ModuleDecl = childModule;
        child.InheritedProtocols.Add(new NamedTypeSpec("DepA.ParentDelegate"));
        child.InheritedProtocols.Add(new NamedTypeSpec("DepB.ParentDelegate"));

        var output = EmitProxyClass(child);

        // Each parent's vtable struct gets a {Module}_ prefix to avoid CS0102
        // when both parents share a simple name.
        Assert.Contains("DepA_ParentDelegateSwiftVTable", output);
        Assert.Contains("DepB_ParentDelegateSwiftVTable", output);
        Assert.Contains("DepA_ParentDelegateLocalVTable", output);
        Assert.Contains("DepB_ParentDelegateLocalVTable", output);

        // Scaffolding fields use the xm_{Module}_{Name} suffix shape.
        Assert.Contains("_swiftVTable_xm_DepA_ParentDelegate", output);
        Assert.Contains("_swiftVTable_xm_DepB_ParentDelegate", output);
        Assert.Contains("_localVTable_xm_DepA_ParentDelegate", output);
        Assert.Contains("_localVTable_xm_DepB_ParentDelegate", output);

        // Each parent's P/Invoke wrapper sits in its own per-parent nested
        // NativeMethods class so the C# method names don't collide; the cdecl
        // entry point ALSO carries the module qualifier so the wrapper-lib
        // symbol table can host both Set_vtable trampolines side-by-side.
        Assert.Contains("NativeMethods_xm_DepA_ParentDelegate", output);
        Assert.Contains("NativeMethods_xm_DepB_ParentDelegate", output);
        Assert.Contains("EntryPoint = \"SetDepA_ParentDelegate_vtable\"", output);
        Assert.Contains("EntryPoint = \"SetDepB_ParentDelegate_vtable\"", output);

        // The UNQUALIFIED forms must not appear at the cross-module emission
        // sites — both would be ambiguous.
        Assert.DoesNotContain("private struct ParentDelegateSwiftVTable", output);
        Assert.DoesNotContain("private struct ParentDelegateLocalVTable", output);
    }

    /// <summary>
    /// Same-module emission (the common case — child and parent live in the
    /// same module) must NOT pick up a module prefix on its own vtable
    /// struct names. The M fix is opt-in for cross-module sites only;
    /// regressing this would rename every protocol's vtable struct.
    /// </summary>
    [Fact]
    public void EmitProxyClass_SameModule_DoesNotPrefixVtableStructNames()
    {
        var protocolDecl = CreateProtocolWithMethod("LocalProtocol", "doSomething");

        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("private struct LocalProtocolSwiftVTable", output);
        Assert.Contains("private struct LocalProtocolLocalVTable", output);
        // No xm_ scaffolding for a protocol with no cross-module ancestors.
        Assert.DoesNotContain("_xm_", output);
    }

    private static ModuleDecl CreateCrossModuleDep(string moduleName) => new()
    {
        Name = moduleName,
        ParentDecl = null, ModuleDecl = null,
        Properties = new(), Methods = new(), Types = new(),
        Dependencies = new(), Protocols = new(),
    };

    private static ProtocolDecl CreateCrossModuleParent(string name, string methodName, ModuleDecl owningModule)
    {
        var parent = CreateSimpleProtocol(name);
        parent.ModuleDecl = owningModule;
        parent.SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{owningModule.Name}.{name}");
        parent.Methods.Add(CreateMethodDecl(methodName));
        owningModule.Protocols.Add(parent);
        return parent;
    }

    #endregion
}
