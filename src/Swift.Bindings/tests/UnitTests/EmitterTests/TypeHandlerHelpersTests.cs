// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for TypeHandlerHelpers — GetImplementedInterfaces, EqualityMethodsWriter.
/// </summary>
public class TypeHandlerHelpersTests
{
    #region GetImplementedInterfaces Tests

    [Fact]
    public void GetImplementedInterfaces_MinimalType_IncludesISwiftObjectAndIDisposable()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Loader", moduleDecl);

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            classDecl, "Loader", "TestModule", typeDatabase);

        Assert.Contains("ISwiftObject", interfaces);
        Assert.Contains("IDisposable", interfaces);
    }

    [Fact]
    public void GetImplementedInterfaces_EquatableType_IncludesIEquatable()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDeclWithConformances("Point", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$s10TestModule5PointVSQAAMc"));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            structDecl, "Point", "TestModule", typeDatabase);

        Assert.Contains("IEquatable<Point>", interfaces);
    }

    [Fact]
    public void GetImplementedInterfaces_HashableType_SkipsHashableInterface()
    {
        // Hashable is a marker — not emitted as a C# interface
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDeclWithConformances("Point", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Hashable"),
                "$s10TestModule5PointVSHAAMc"));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            structDecl, "Point", "TestModule", typeDatabase);

        Assert.DoesNotContain(interfaces, i => i.Contains("Hashable"));
    }

    [Fact]
    public void GetImplementedInterfaces_CrossModuleProtocol_NotInSupportedList_Excluded()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDeclWithConformances("Point", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("OtherModule.SomeProtocol"),
                "$s10TestModule5PointVOtherModuleSomeProtocolMc"));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            structDecl, "Point", "TestModule", typeDatabase);

        // Cross-module protocol not in supported list should be excluded
        Assert.DoesNotContain(interfaces, i => i.Contains("SomeProtocol"));
    }

    [Fact]
    public void GetImplementedInterfaces_ProtocolWithAssociatedType_Excluded()
    {
        var typeDatabase = CreateTypeDatabaseWithPAT();
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDeclWithConformances("MyIterator", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.MyIterator"),
                SwiftTypeName.FromModuleQualifiedName("TestModule.Iterable"),
                "$s10TestModule10MyIteratorVIterableMc"));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            structDecl, "MyIterator", "TestModule", typeDatabase);

        // Protocols with associated types should be excluded
        Assert.DoesNotContain(interfaces, i => i.Contains("Iterable"));
    }

    [Fact]
    public void GetImplementedInterfaces_SameModuleProtocol_Included()
    {
        var typeDatabase = CreateTypeDatabaseWithProtocol("TestModule", "Describable");
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDeclWithConformances("Point", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("TestModule.Describable"),
                "$s10TestModule5PointVDescribableMc"));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            structDecl, "Point", "TestModule", typeDatabase);

        Assert.Contains(interfaces, i => i.Contains("Describable"));
    }

    #endregion

    #region EqualityMethodsWriter Tests

    [Fact]
    public void WriteSwiftEquatable_Equatable_EmitsEquals()
    {
        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);
        var structDecl = CreateStructDeclWithConformances("Point", CreateModuleDecl("TestModule"),
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$sMc"));

        var writer = new EqualityMethodsWriter(csWriter, structDecl, true, "Point");
        writer.WriteSwiftEquatableImplementation();

        var result = output.ToString();
        Assert.Contains("SwiftEquatable.Equals", result);
    }

    [Fact]
    public void WriteSwiftEquatable_EquatableAndHashable_EmitsSwiftHashableGetHashCode()
    {
        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);
        var structDecl = CreateStructDeclWithConformances("Point", CreateModuleDecl("TestModule"),
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$sMc1"),
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Hashable"),
                "$sMc2"));

        var writer = new EqualityMethodsWriter(csWriter, structDecl, true, "Point");
        writer.WriteSwiftEquatableImplementation();

        var result = output.ToString();
        Assert.Contains("SwiftHashable.GetHashCode(this)", result);
    }

    [Fact]
    public void WriteSwiftEquatable_EquatableNotHashable_EmitsReturnZero()
    {
        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);
        var structDecl = CreateStructDeclWithConformances("Point", CreateModuleDecl("TestModule"),
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$sMc"));

        var writer = new EqualityMethodsWriter(csWriter, structDecl, true, "Point");
        writer.WriteSwiftEquatableImplementation();

        var result = output.ToString();
        Assert.Contains("return 0;", result);
        Assert.DoesNotContain("SwiftHashable", result);
    }

    [Fact]
    public void WriteSwiftEquatable_ExplicitEqualityOperator_SkipsOperator()
    {
        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);
        var structDecl = CreateStructDeclWithConformances("Point", CreateModuleDecl("TestModule"),
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$sMc"));

        var writer = new EqualityMethodsWriter(csWriter, structDecl, true, "Point",
            hasExplicitEqualityOperator: true, hasExplicitInequalityOperator: false);
        writer.WriteSwiftEquatableImplementation();

        var result = output.ToString();
        Assert.DoesNotContain("operator ==(", result);
        // != should still be emitted since only == is explicit
        Assert.Contains("operator !=(", result);
    }

    [Fact]
    public void WriteSwiftEquatable_ExplicitInequalityOperator_SkipsOperator()
    {
        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);
        var structDecl = CreateStructDeclWithConformances("Point", CreateModuleDecl("TestModule"),
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$sMc"));

        var writer = new EqualityMethodsWriter(csWriter, structDecl, true, "Point",
            hasExplicitEqualityOperator: false, hasExplicitInequalityOperator: true);
        writer.WriteSwiftEquatableImplementation();

        var result = output.ToString();
        Assert.Contains("operator ==(", result);
        Assert.DoesNotContain("operator !=(", result);
    }

    #endregion

    #region Helper Methods

    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);
        typeDatabase.AddModuleDatabase(new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib"));
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithPAT()
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Iterable"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "IIterable"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Iterable"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.HasAssociatedTypes,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithProtocol(string module, string name)
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);
        var testModule = new ModuleTypeDatabase(module, $"/tmp/{module}.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"{module}.{name}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName($"Swift.{module}", $"I{name}"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{module}.{name}"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static ModuleDecl CreateModuleDecl(string name)
    {
        return new ModuleDecl
        {
            Name = name,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static ClassDecl CreateClassDecl(string name, ModuleDecl moduleDecl)
    {
        return new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}CN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
    }

    private static StructDecl CreateStructDeclWithConformances(string name, ModuleDecl moduleDecl, params TypeConformance[] conformances)
    {
        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}VN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(conformances),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa"
        };
    }

    #endregion
}
