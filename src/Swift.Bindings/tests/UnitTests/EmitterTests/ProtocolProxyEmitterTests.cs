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

        Assert.Contains("public unsafe class TestProtocolProxy", output);
    }

    [Fact]
    public void EmitProxyClass_ImplementsInterface()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains(": ISwiftTestProtocol, ISwiftObject", output);
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

        Assert.Contains("private readonly ISwiftTestProtocol? _csharpImpl;", output);
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

        Assert.Contains("private readonly ExistentialContainer1 _swiftContainer;", output);
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

        Assert.Contains("public TestProtocolProxy(ISwiftTestProtocol implementation)", output);
    }

    [Fact]
    public void EmitProxyClass_GeneratesExistentialContainerConstructor()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("public TestProtocolProxy(ExistentialContainer1 container)", output);
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
        Assert.Contains("public Swift.AnyType value", output);
        Assert.Contains("if (_csharpImpl != null)", output);
        Assert.Contains("return _csharpImpl.value;", output);
    }

    [Fact]
    public void EmitProxyClass_ImplementsPropertySetter()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: true);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("_csharpImpl.value = value;", output);
    }

    [Fact]
    public void EmitProxyClass_ImplementsMethod()
    {
        var protocolDecl = CreateProtocolWithMethod("TestProtocol", "doSomething");
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("public void doSomething()", output);
        Assert.Contains("_csharpImpl.doSomething();", output);
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

        Assert.Contains("private static class NativeMethods", output);
    }

    [Fact]
    public void EmitProxyClass_GeneratesSetVtablePInvoke()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProxyClass(protocolDecl);

        Assert.Contains("[DllImport(\"TestModule\"", output);
        Assert.Contains("EntryPoint = \"SetTestProtocol_vtable\"", output);
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
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        protocolDecl.AssociatedTypes.Add(new AssociatedTypeDecl { Name = "Element" });

        var output = EmitProxyClass(protocolDecl);

        Assert.DoesNotContain("public unsafe class TestProtocolProxy", output);
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
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
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

    #endregion
}
