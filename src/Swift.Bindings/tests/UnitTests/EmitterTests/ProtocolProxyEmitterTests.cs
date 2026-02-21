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

        Assert.Contains("private readonly ITestProtocol? _csharpImpl;", output);
    }

    [Fact]
    public void EmitProxyClass_GeneratesEveryProtocolField()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("private readonly EveryProtocol? _everyProtocol;", output);
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

    #endregion

    #region Receiver Method Tests

    [Fact]
    public void EmitProxyClass_GeneratesPropertyGetterReceiver()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]", output);
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
    public void EmitProxyClass_GetterReceiver_String_ConvertsToSwiftString()
    {
        // Regression (P0 #1): Getter receiver returns idiomatic C# value (string) from
        // _csharpImpl but MarshalToSwiftBuffer expects Swift ABI type (SwiftString).
        // Without reverse conversion, Unsafe.Write writes a managed reference instead of
        // SwiftString layout → garbage across the Swift boundary.
        var typeSpec = new NamedTypeSpec("Swift.String");
        var protocolDecl = CreateProtocolWithProperty("StringProto", "name", hasGetter: true, hasSetter: false, typeSpec);
        var output = EmitProxyClass(protocolDecl);

        // Getter should convert string → SwiftString before marshalling
        Assert.Contains("Receive_name_get", output);
        Assert.Contains("new SwiftString(result)", output);
        Assert.Contains("MarshalToSwiftBuffer(swiftResult)", output);
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
        // Non-convertible (blittable) types should NOT get intermediate conversion
        var protocolDecl = CreateProtocolWithProperty("IntProto", "count", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("Receive_count_get", output);
        Assert.Contains("MarshalToSwiftBuffer(result)", output);
        Assert.DoesNotContain("swiftResult", output.Substring(output.IndexOf("Receive_count_get")));
    }

    [Fact]
    public void EmitProxyClass_ReceiverUsesSwiftObjectRegistry()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("SwiftObjectRegistry.GetProxyFromContainer<TestProtocolProxy>", output);
    }

    #endregion

    #region Constructor Tests

    [Fact]
    public void EmitProxyClass_GeneratesCSharpImplConstructor()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("public TestProtocolProxy(ITestProtocol implementation)", output);
    }

    [Fact]
    public void EmitProxyClass_GeneratesExistentialContainerConstructor()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("internal TestProtocolProxy(ExistentialContainer1 container)", output);
    }

    [Fact]
    public void EmitProxyClass_ConstructorRegistersWithSwiftObjectRegistry()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("SwiftObjectRegistry.RegisterStrong(_everyProtocol.Handle, this)", output);
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
        _typeDatabase.AsyncLibraryName = "BlinkIDSwiftBindings";
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("[LibraryImport(\"BlinkIDSwiftBindings\"", output);
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
        // Fix 8 (SnapKit): Protocols with no implementable instance members still need
        // proxy classes — return types like ILayoutConstraintItem require a proxy constructor.
        // The emission code gracefully handles zero members (loops iterate zero times).
        var protocolDecl = CreateSimpleProtocol("EmptyProtocol");

        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("public unsafe partial class EmptyProtocolProxy", output);
        Assert.Contains(": IEmptyProtocol, ISwiftObject, IDisposable", output);
        // Constructor and ISwiftObject implementation still emitted
        Assert.Contains("public EmptyProtocolProxy(IEmptyProtocol implementation)", output);
        Assert.Contains("internal EmptyProtocolProxy(ExistentialContainer1 container)", output);
        Assert.Contains("public static TypeMetadata GetTypeMetadata()", output);
    }

    [Fact]
    public void EmitProxyClass_EmptyProtocol_WithInheritedRequirements_SkipsProxy()
    {
        // A protocol with no own members but inheriting from a protocol with requirements
        // would produce a proxy class missing inherited interface members (CS0535).
        // The guard skips proxy generation for this case.
        var protocolDecl = CreateSimpleProtocol("DerivedProtocol");
        protocolDecl.InheritedProtocols.Add(new NamedTypeSpec("TestModule.BaseProtocol"));

        var output = EmitProxyClass(protocolDecl);

        Assert.DoesNotContain("DerivedProtocolProxy", output);
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
            Visibility = Visibility.Public
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
            Visibility = Visibility.Public
        });

        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("public void Apply(Action<Swift.AnyType> callback)", output);
    }

    [Fact]
    public void EmitProxyClass_WithProtocolCompositionProperty_UsesCompositionInterface()
    {
        // Protocol compositions produce a combined interface name (IP1AndP2) via
        // ExistentialHandler.GetCompositionInterfaceName. The factory routes through
        // ExistentialProjection which uses GetPublicExistentialType.
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
    public void EmitProxyClass_WithOptionalExistentialGeneric_UsesAnyTypeFallback()
    {
        var protocolDecl = CreateSimpleProtocol("OptionalExistentialProtocol");
        _typeDatabase.AddOutOfModuleTypes(new[]
        {
            (SwiftTypeName.FromModuleQualifiedName("TestModule.Box"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Box"),
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
            Visibility = Visibility.Public
        });

        var output = EmitProxyClass(protocolDecl);

        // "value" is sanitized to "_value" by GetCSharpParameterName (it's a C# contextual keyword)
        Assert.Contains("public void Update(Swift.TestModule.Box<Swift.AnyType> _value)", output);
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
            Visibility = Visibility.Public
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
            Visibility = Visibility.Public
        });

        var output = EmitProxyClass(protocolDecl);

        // Both params resolve to "Swift.AnyType" via ProtocolSignatureHelper →
        // same key "update(Swift.AnyType)" → only one proxy class method emitted
        Assert.Equal(1, EmitterTestHelpers.CountOccurrences(output, "public void Update("));

        // H2 Bug 3: Verify receiver count matches interface method count (no orphaned receivers).
        // Before H2, receivers used GetMethodKey (ToString-based) producing different keys
        // for closure vs array params, while interface used GetMethodSignatureKey (TypeDB-based)
        // collapsing both to AnyType. This mismatch caused orphaned receivers → CS1503.
        var receiverCount = EmitterTestHelpers.CountOccurrences(output, "static void Receive_update_");
        Assert.Equal(1, receiverCount);
    }

    [Fact]
    public void EmitProxyClass_ClosureAndArrayParams_ReceiverMatchesInterfaceDedup()
    {
        // H2 Bug 3: Two methods "finish(output:)" (closure param) and "finish(withBytes:)" (array param)
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
            Visibility = Visibility.Public
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
            Visibility = Visibility.Public
        });

        var output = EmitProxyClass(protocolDecl);

        // Single interface method emitted (both collapse to same key)
        Assert.Equal(1, EmitterTestHelpers.CountOccurrences(output, "public void Finish("));
        // Single receiver emitted (consistent dedup with interface)
        Assert.Equal(1, EmitterTestHelpers.CountOccurrences(output, "static void Receive_finish_"));
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
    public void EmitProxyClass_ThrowingMethod_EmitsNotSupportedException()
    {
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
            Visibility = Visibility.Public
        });
        var output = EmitProxyClass(protocolDecl);

        // Throwing methods should NOT be dispatched, even with blittable types
        Assert.Contains("Cannot call method 'TryGetValue'", output);
        Assert.DoesNotContain("SBW_TestProtocol_method_tryGetValue", output);
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
            Visibility = Visibility.Public
        });
        var output = EmitProxyClass(protocolDecl);

        // Async methods should NOT be dispatched, even with blittable types
        Assert.Contains("Cannot call method 'FetchValueAsync'", output);
        Assert.DoesNotContain("SBW_TestProtocol_method_fetchValue", output);
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
            Visibility = Visibility.Public
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
            Visibility = Visibility.Public
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
            Visibility = Visibility.Public
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
            Visibility = Visibility.Public
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
            Visibility = Visibility.Public
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
            Visibility = Visibility.Public
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
            Visibility = Visibility.Public
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

    #region Utf8Slice Struct Tests

    [Fact]
    public void EmitProxyClass_GeneratesUtf8SliceStruct()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("[StructLayout(LayoutKind.Sequential)]", output);
        Assert.Contains("private struct Utf8Slice", output);
        Assert.Contains("public IntPtr Ptr;", output);
        Assert.Contains("public nint Len;", output);
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

    #region P0 — Receiver ABI Type Marshalling (Codex review fix)

    [Fact]
    public void EmitProxyClass_SetterReceiver_String_UsesAbiType()
    {
        // P0: MarshalFromSwift<T> in setter must use Swift ABI type (Swift.SwiftString),
        // not idiomatic C# type (string). Reading Swift ABI memory as a C# string corrupts at runtime.
        RegisterSwiftString();
        var typeSpec = new NamedTypeSpec("Swift.String");
        var protocolDecl = CreateProtocolWithProperty("StringPropProto", "label", hasGetter: false, hasSetter: true, typeSpec);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("Receive_label_set", output);
        // Must use ABI type for MarshalFromSwift, not idiomatic "string"
        Assert.Contains("MarshalFromSwift<Swift.SwiftString>", output);
        Assert.DoesNotContain("MarshalFromSwift<string>", output);
    }

    [Fact]
    public void EmitProxyClass_MethodReceiver_StringParam_UsesAbiType()
    {
        // P0: Method param unmarshalling must use ABI type for MarshalFromSwift.
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
        // Must use ABI type for MarshalFromSwift
        Assert.Contains("MarshalFromSwift<Swift.SwiftString>", output);
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
            Visibility = Visibility.Public
        });

        var output = EmitProxyClass(protocolDecl);

        // The Dictionary in the closure param must have generic type arguments (not bare SwiftDictionary).
        // With factory-based projection, the raw ABI type is used when the factory can't fully resolve
        // the closure (e.g., AnyHashable not in TypeDatabase). The key requirement is generic args present.
        Assert.Contains("SwiftDictionary<", output);
        // Must NOT emit bare type without generic args
        Assert.DoesNotContain("SwiftDictionary?", output.Replace("SwiftDictionary<", ""));
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

    #endregion

    #region Helper Methods

    private string EmitProxyClass(ProtocolDecl protocolDecl)
    {
        var stringWriter = new StringWriter();
        var writer = new CSharpWriter(stringWriter);
        _emitter.EmitProxyClass(writer, protocolDecl);
        return stringWriter.ToString();
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
            Visibility = Visibility.Public
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

    #endregion
}
