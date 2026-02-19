// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Reflection;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for enum case associated value type resolution (GetCSharpTypeNameForEnumCase).
/// Exercises the IsBoundGeneric guard added to prevent NotSupportedException for
/// non-bound generic types like UnsafePointer{T}.
/// </summary>
public class EnumCaseAssociatedValueTests
{
    [Fact]
    public void GetCSharpTypeNameForEnumCase_PointerType_DoesNotThrow()
    {
        // UnsafePointer<UInt8> has ContainsGenericParameters=true but is NOT a bound generic
        // (BoundGenericsHandler.IsBoundGeneric returns false for pointer types).
        // Before the fix, this threw NotSupportedException.
        var typeDatabase = CreateTypeDatabase();
        var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);

        var unsafePointer = new NamedTypeSpec("Swift.UnsafePointer");
        unsafePointer.GenericParameters.Add(new NamedTypeSpec("Swift.UInt8"));

        // Should not throw — falls through to GetTypeRecordOrAnyType
        var result = InvokeGetCSharpTypeNameForEnumCase(unsafePointer, typeDatabase, boundGenericsHandler);

        Assert.NotNull(result);
        Assert.NotEmpty(result);
    }

    [Fact]
    public void GetCSharpTypeNameForEnumCase_UnsafeMutablePointer_DoesNotThrow()
    {
        // UnsafeMutablePointer<T> — another pointer type not handled by BoundGenericsHandler.
        var typeDatabase = CreateTypeDatabase();
        var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);

        var unsafeMutablePointer = new NamedTypeSpec("Swift.UnsafeMutablePointer");
        unsafeMutablePointer.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));

        // Should not throw — falls through to GetTypeRecordOrAnyType
        var result = InvokeGetCSharpTypeNameForEnumCase(unsafeMutablePointer, typeDatabase, boundGenericsHandler);

        Assert.NotNull(result);
        Assert.NotEmpty(result);
    }

    [Fact]
    public void GetCSharpTypeNameForEnumCase_OptionalInt_DoesNotThrow()
    {
        // Optional<Int> IS a bound generic — BoundGenericsHandler handles it.
        // Exact output depends on type database context, but it must not throw.
        var typeDatabase = CreateTypeDatabase();
        var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);

        var optionalInt = new NamedTypeSpec("Swift.Optional");
        optionalInt.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));

        var result = InvokeGetCSharpTypeNameForEnumCase(optionalInt, typeDatabase, boundGenericsHandler);

        Assert.NotNull(result);
        Assert.NotEmpty(result);
    }

    /// <summary>
    /// Invokes the private static GetCSharpTypeNameForEnumCase via reflection.
    /// </summary>
    private static string InvokeGetCSharpTypeNameForEnumCase(
        TypeSpec typeSpec, ITypeDatabase typeDatabase, BoundGenericsHandler boundGenericsHandler)
    {
        var method = typeof(EnumHandler).GetMethod(
            "GetCSharpTypeNameForEnumCase",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        return (string)method!.Invoke(null, new object[] { typeSpec, typeDatabase, boundGenericsHandler })!;
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

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }
}
