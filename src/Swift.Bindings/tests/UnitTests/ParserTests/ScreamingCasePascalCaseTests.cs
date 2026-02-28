// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging.Abstractions;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests that ModuleProcessor converts SCREAMING_CASE type names to PascalCase
/// in the CSharpTypeName for structs, enums, and classes.
/// </summary>
public class ScreamingCasePascalCaseTests
{
    [Fact]
    public void RegisterStructType_ScreamingCase_ConvertsToPascalCase()
    {
        var typeSpec = new NamedTypeSpec("TestModule.CAMERA_DIRECTION");
        var structDecl = CreateStructDecl("CAMERA_DIRECTION", typeSpec);

        var result = ProcessSingleType(typeSpec, structDecl);

        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.CAMERA_DIRECTION");
        Assert.True(result.ModuleDatabase.TryGetTypeRecord(swiftTypeName, out var record));
        Assert.Equal("CameraDirection", record.CSharpTypeName.Name);
    }

    [Fact]
    public void RegisterEnumType_ScreamingCase_ConvertsToPascalCase()
    {
        var typeSpec = new NamedTypeSpec("TestModule.PIXEL_FORMAT");
        var enumDecl = CreateEnumDecl("PIXEL_FORMAT", typeSpec);

        var result = ProcessSingleType(typeSpec, enumDecl);

        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.PIXEL_FORMAT");
        Assert.True(result.ModuleDatabase.TryGetTypeRecord(swiftTypeName, out var record));
        Assert.Equal("PixelFormat", record.CSharpTypeName.Name);
    }

    [Fact]
    public void RegisterClassType_ScreamingCase_ConvertsToPascalCase()
    {
        var typeSpec = new NamedTypeSpec("TestModule.HTTP_CLIENT");
        var classDecl = CreateClassDecl("HTTP_CLIENT", typeSpec);

        var result = ProcessSingleType(typeSpec, classDecl);

        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.HTTP_CLIENT");
        Assert.True(result.ModuleDatabase.TryGetTypeRecord(swiftTypeName, out var record));
        Assert.Equal("HttpClient", record.CSharpTypeName.Name);
    }

    [Fact]
    public void RegisterStructType_NestedScreamingCase_ConvertsToPascalCasePerSegment()
    {
        // Nested type: Outer.SCREAMING_NAME should become Outer.ScreamingName
        var typeSpec = new NamedTypeSpec("TestModule.Outer");
        typeSpec.InnerType = new NamedTypeSpec("SCREAMING_NAME");
        var structDecl = CreateStructDecl("SCREAMING_NAME", typeSpec);

        var result = ProcessSingleType(typeSpec, structDecl);

        var swiftTypeName = SwiftTypeName.FromTypeSpec(typeSpec);
        Assert.True(result.ModuleDatabase.TryGetTypeRecord(swiftTypeName, out var record));
        Assert.Equal("Outer.ScreamingName", record.CSharpTypeName.Name);
    }

    [Fact]
    public void RegisterStructType_AlreadyPascalCase_Unchanged()
    {
        var typeSpec = new NamedTypeSpec("TestModule.ImageRequest");
        var structDecl = CreateStructDecl("ImageRequest", typeSpec);

        var result = ProcessSingleType(typeSpec, structDecl);

        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ImageRequest");
        Assert.True(result.ModuleDatabase.TryGetTypeRecord(swiftTypeName, out var record));
        Assert.Equal("ImageRequest", record.CSharpTypeName.Name);
    }

    [Fact]
    public void RegisterEnumType_AlreadyCamelCase_ConvertsToPascalCase()
    {
        var typeSpec = new NamedTypeSpec("TestModule.pixelFormat");
        var enumDecl = CreateEnumDecl("pixelFormat", typeSpec);

        var result = ProcessSingleType(typeSpec, enumDecl);

        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.pixelFormat");
        Assert.True(result.ModuleDatabase.TryGetTypeRecord(swiftTypeName, out var record));
        Assert.Equal("PixelFormat", record.CSharpTypeName.Name);
    }

    [Fact]
    public void RegisterStructType_AllCapsAbbreviation_Unchanged()
    {
        // All-caps without underscores (abbreviation/acronym like URL, F9S1) should stay unchanged
        var typeSpec = new NamedTypeSpec("TestModule.URL");
        var structDecl = CreateStructDecl("URL", typeSpec);

        var result = ProcessSingleType(typeSpec, structDecl);

        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.URL");
        Assert.True(result.ModuleDatabase.TryGetTypeRecord(swiftTypeName, out var record));
        Assert.Equal("URL", record.CSharpTypeName.Name);
    }

    #region Helper Methods

    private static ModuleProcessingResult ProcessSingleType(NamedTypeSpec typeSpec, TypeDecl typeDecl)
    {
        var typeDecls = new Dictionary<NamedTypeSpec, TypeDecl>
        {
            { typeSpec, typeDecl },
        };

        var typeDatabase = new TypeDatabase();
        var processor = new ModuleProcessor(
            "TestModule",
            "/tmp/TestModule.dylib",
            "TestModule",
            typeDecls,
            typeDatabase,
            NullLogger.Instance);

        return processor.FinalizeTypeProcessingAndCreateModuleDatabase();
    }

    private static StructDecl CreateStructDecl(string name, NamedTypeSpec typeSpec)
    {
        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromTypeSpec(typeSpec),
            MangledName = $"$s10TestModule{name.Length}{name}VN",
            IsFrozen = true,
            MetadataAccessor = string.Empty,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static EnumDecl CreateEnumDecl(string name, NamedTypeSpec typeSpec)
    {
        return new EnumDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromTypeSpec(typeSpec),
            MangledName = $"$s10TestModule{name.Length}{name}O",
            IsFrozen = true,
            MetadataAccessor = string.Empty,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Cases = new List<EnumCaseDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static ClassDecl CreateClassDecl(string name, NamedTypeSpec typeSpec)
    {
        return new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromTypeSpec(typeSpec),
            MangledName = $"$s10TestModule{name.Length}{name}C",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    #endregion
}
