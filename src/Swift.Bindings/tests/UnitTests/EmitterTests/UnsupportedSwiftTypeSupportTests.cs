// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for UnsupportedSwiftTypeSupport — TryFindFallbackInfo recursive search, EscapeStringLiteral.
/// </summary>
public class UnsupportedSwiftTypeSupportTests
{
    [Fact]
    public void TryFindFallbackInfo_NamedTypeWithAnyTypeGenericParam_ReturnsTrue()
    {
        // Array<UnknownModule.Foo> — the generic parameter resolves to AnyType
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);

        var arrayType = new NamedTypeSpec("Swift.Array");
        arrayType.GenericParameters.Add(new NamedTypeSpec("UnknownModule.Foo"));

        var result = UnsupportedSwiftTypeSupport.TryFindFallbackInfo(
            typeDatabase, closureHandler, arrayType, out var fallbackInfo);

        Assert.True(result);
    }

    [Fact]
    public void TryFindFallbackInfo_TupleWithAnyTypeElement_ReturnsTrue()
    {
        // (Swift.Int, UnknownModule.Bar) — second element resolves to AnyType
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);

        var tupleType = new TupleTypeSpec(new TypeSpec[]
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("UnknownModule.Bar")
        });

        var result = UnsupportedSwiftTypeSupport.TryFindFallbackInfo(
            typeDatabase, closureHandler, tupleType, out var fallbackInfo);

        Assert.True(result);
    }

    [Fact]
    public void TryFindFallbackInfo_ClosureWithAnyTypeArg_ReturnsTrue()
    {
        // (UnknownModule.Foo) -> () — closure with unsupported arg type
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("UnknownModule.Foo") }),
            TupleTypeSpec.Empty);

        var result = UnsupportedSwiftTypeSupport.TryFindFallbackInfo(
            typeDatabase, closureHandler, closureType, out var fallbackInfo);

        Assert.True(result);
    }

    [Fact]
    public void TryFindFallbackInfo_AllSupportedTypes_ReturnsFalse()
    {
        // Swift.Int — fully supported, no fallback needed
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);

        var intType = new NamedTypeSpec("Swift.Int");

        var result = UnsupportedSwiftTypeSupport.TryFindFallbackInfo(
            typeDatabase, closureHandler, intType, out var fallbackInfo);

        Assert.False(result);
    }

    [Fact]
    public void EscapeStringLiteral_EscapesQuotesAndBackslashes()
    {
        Assert.Equal("hello", UnsupportedSwiftTypeSupport.EscapeStringLiteral("hello"));
        Assert.Equal("say \\\"hi\\\"", UnsupportedSwiftTypeSupport.EscapeStringLiteral("say \"hi\""));
        Assert.Equal("path\\\\to\\\\file", UnsupportedSwiftTypeSupport.EscapeStringLiteral("path\\to\\file"));
        Assert.Equal("mixed\\\\\\\"test", UnsupportedSwiftTypeSupport.EscapeStringLiteral("mixed\\\"test"));
    }

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
}
