// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for EveryProtocolEmitter Swift code generation.
/// </summary>
public class EveryProtocolEmitterTests
{
    private readonly TypeDatabase _typeDatabase;
    private readonly EveryProtocolEmitter _emitter;

    public EveryProtocolEmitterTests()
    {
        _typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/fake/path");
        _typeDatabase.AddModuleDatabase(module);
        _emitter = new EveryProtocolEmitter(_typeDatabase, NullLogger.Instance, "TestModule");
    }

    #region EveryProtocol Class Emission Tests

    [Fact]
    public void EmitEveryProtocolClass_GeneratesClassDeclaration()
    {
        var output = EmitEveryProtocolClass();

        Assert.Contains("public final class EveryProtocol", output);
    }

    [Fact]
    public void EmitEveryProtocolClass_GeneratesHandleProperty()
    {
        var output = EmitEveryProtocolClass();

        Assert.Contains("var handle: UnsafeRawPointer?", output);
    }

    [Fact]
    public void EmitEveryProtocolClass_GeneratesDefaultInit()
    {
        var output = EmitEveryProtocolClass();

        Assert.Contains("public init()", output);
    }

    [Fact]
    public void EmitEveryProtocolClass_GeneratesHandleInit()
    {
        var output = EmitEveryProtocolClass();

        Assert.Contains("public init(handle: UnsafeRawPointer)", output);
    }

    #endregion

    #region Protocol Vtable Struct Emission Tests

    [Fact]
    public void EmitProtocolVtableStruct_GeneratesStructDeclaration()
    {
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        var output = EmitVtableStruct(protocolDecl);

        Assert.Contains("fileprivate struct TestProtocol_vtable", output);
    }

    [Fact]
    public void EmitProtocolVtableStruct_GeneratesCsVTHandle()
    {
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        var output = EmitVtableStruct(protocolDecl);

        Assert.Contains("var csVTHandle: OpaquePointer?", output);
    }

    [Fact]
    public void EmitProtocolVtableStruct_GeneratesPropertyGetterField()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitVtableStruct(protocolDecl);

        Assert.Contains("var func_value_get:", output);
    }

    [Fact]
    public void EmitProtocolVtableStruct_GeneratesPropertySetterField()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: true);
        var output = EmitVtableStruct(protocolDecl);

        Assert.Contains("var func_value_set:", output);
    }

    [Fact]
    public void EmitProtocolVtableStruct_GeneratesMethodField()
    {
        var protocolDecl = CreateProtocolWithMethod("TestProtocol", "doSomething");
        var output = EmitVtableStruct(protocolDecl);

        Assert.Contains("var func_doSomething_0:", output);
    }

    [Fact]
    public void EmitProtocolVtableStruct_GeneratesVtableInstance()
    {
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        var output = EmitVtableStruct(protocolDecl);

        Assert.Contains("private var _testProtocol_vtable = TestProtocol_vtable()", output);
    }

    #endregion

    #region Protocol Extension Emission Tests

    [Fact]
    public void EmitProtocolExtension_GeneratesExtensionDeclaration()
    {
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        var output = EmitProtocolExtension(protocolDecl);

        Assert.Contains("extension EveryProtocol: TestModule.TestProtocol", output);
    }

    [Fact]
    public void EmitProtocolExtension_GeneratesPropertyGetter()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProtocolExtension(protocolDecl);

        Assert.Contains("public var value:", output);
        Assert.Contains("get {", output);
    }

    [Fact]
    public void EmitProtocolExtension_GeneratesPropertySetter()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: true);
        var output = EmitProtocolExtension(protocolDecl);

        Assert.Contains("set {", output);
    }

    [Fact]
    public void EmitProtocolExtension_GeneratesMethodImplementation()
    {
        var protocolDecl = CreateProtocolWithMethod("TestProtocol", "doSomething");
        var output = EmitProtocolExtension(protocolDecl);

        Assert.Contains("public func doSomething()", output);
    }

    #endregion

    #region SetVtable Function Emission Tests

    [Fact]
    public void EmitSetVtableFunction_GeneratesSilgenName()
    {
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        var output = EmitSetVtableFunction(protocolDecl);

        Assert.Contains("@_silgen_name(\"SetTestProtocol_vtable\")", output);
    }

    [Fact]
    public void EmitSetVtableFunction_GeneratesPublicFunction()
    {
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        var output = EmitSetVtableFunction(protocolDecl);

        Assert.Contains("public func setTestProtocol_vtable(uvt: UnsafeRawPointer)", output);
    }

    [Fact]
    public void EmitSetVtableFunction_CopiesVtable()
    {
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        var output = EmitSetVtableFunction(protocolDecl);

        Assert.Contains("_testProtocol_vtable = vt.pointee", output);
    }

    #endregion

    #region Protocol Conformance Filtering Tests

    [Fact]
    public void EmitProtocolConformance_SkipsProtocolsWithSelfRequirement()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        protocolDecl.HasSelfRequirement = true;

        var output = EmitFullConformance(protocolDecl);

        Assert.DoesNotContain("extension EveryProtocol:", output);
    }

    [Fact]
    public void EmitProtocolConformance_SkipsProtocolsWithAssociatedTypes()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        protocolDecl.AssociatedTypes.Add(new AssociatedTypeDecl { Name = "Element" });

        var output = EmitFullConformance(protocolDecl);

        Assert.DoesNotContain("extension EveryProtocol:", output);
    }

    [Fact]
    public void EmitProtocolConformance_SkipsProtocolsWithNoImplementableMembers()
    {
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        // No properties, no non-static methods, no subscripts

        var output = EmitFullConformance(protocolDecl);

        Assert.DoesNotContain("extension EveryProtocol:", output);
    }

    #endregion

    #region Helper Methods

    private string EmitEveryProtocolClass()
    {
        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        _emitter.EmitEveryProtocolClass(writer);
        return stringWriter.ToString();
    }

    private string EmitVtableStruct(ProtocolDecl protocolDecl)
    {
        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        _emitter.EmitProtocolVtableStruct(writer, protocolDecl);
        return stringWriter.ToString();
    }

    private string EmitProtocolExtension(ProtocolDecl protocolDecl)
    {
        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        _emitter.EmitProtocolExtension(writer, protocolDecl);
        return stringWriter.ToString();
    }

    private string EmitSetVtableFunction(ProtocolDecl protocolDecl)
    {
        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        _emitter.EmitSetVtableFunction(writer, protocolDecl);
        return stringWriter.ToString();
    }

    private string EmitFullConformance(ProtocolDecl protocolDecl)
    {
        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        _emitter.EmitProtocolConformance(writer, protocolDecl);
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
