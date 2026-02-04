// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

public class TypeDatabaseExtensionsTests
{
    [Fact]
    public void TryGetAnyTypeFallbackInfo_MissingType_ReturnsFallbackInfo()
    {
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("KnownModule", "/tmp/KnownModule.dylib");
        typeDatabase.AddModuleDatabase(module);

        var found = typeDatabase.TryGetAnyTypeFallbackInfo(new NamedTypeSpec("UnknownModule.MissingType"), out var fallbackInfo);

        Assert.True(found);
        Assert.NotNull(fallbackInfo);
        Assert.Equal("Type is missing from the type database", fallbackInfo.Value.Reason);
        Assert.Equal("UnknownModule.MissingType", fallbackInfo.Value.SwiftType);
    }

    [Fact]
    public void TryGetAnyTypeFallbackInfo_GenericParameter_ReturnsFalse()
    {
        var typeDatabase = new TypeDatabase();

        var found = typeDatabase.TryGetAnyTypeFallbackInfo(new NamedTypeSpec("T"), out var fallbackInfo);

        Assert.False(found);
        Assert.Null(fallbackInfo);
    }

    [Fact]
    public void TryGetAnyTypeFallbackInfo_ExistentialNamedType_ReturnsFallbackInfo()
    {
        var typeDatabase = new TypeDatabase();
        var existential = new NamedTypeSpec("Swift.Encoder")
        {
            IsAny = true
        };

        var found = typeDatabase.TryGetAnyTypeFallbackInfo(existential, out var fallbackInfo);

        Assert.True(found);
        Assert.NotNull(fallbackInfo);
        Assert.Equal("Existential type fallback", fallbackInfo.Value.Reason);
        Assert.Equal("any Swift.Encoder", fallbackInfo.Value.SwiftType);
    }

    // --- Pointer type tests ---

    [Theory]
    [InlineData("Swift.OpaquePointer")]
    [InlineData("Swift.UnsafePointer")]
    [InlineData("Swift.UnsafeMutablePointer")]
    [InlineData("Swift.UnsafeRawPointer")]
    [InlineData("Swift.UnsafeMutableRawPointer")]
    [InlineData("Builtin.RawPointer")]
    public void GetTypeRecordOrAnyType_PointerType_ReturnsIntPtrType(string pointerTypeName)
    {
        var typeDatabase = new TypeDatabase();

        var record = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec(pointerTypeName));

        Assert.Equal(TypeDatabaseExtensions.IntPtrType, record);
        Assert.Equal("System.IntPtr", record.CSharpTypeName.FullyQualifiedName);
        Assert.Equal(TypeRecordFlags.Frozen, record.Flags);
        Assert.Equal(TypeRecordKind.Struct, record.Kind);
    }

    [Theory]
    [InlineData("Swift.OpaquePointer")]
    [InlineData("Swift.UnsafePointer")]
    [InlineData("Swift.UnsafeMutablePointer")]
    [InlineData("Swift.UnsafeRawPointer")]
    [InlineData("Swift.UnsafeMutableRawPointer")]
    [InlineData("Builtin.RawPointer")]
    public void GetTypeRecordOrThrow_PointerType_ReturnsIntPtrType(string pointerTypeName)
    {
        var typeDatabase = new TypeDatabase();

        var record = typeDatabase.GetTypeRecordOrThrow(new NamedTypeSpec(pointerTypeName));

        Assert.Equal(TypeDatabaseExtensions.IntPtrType, record);
    }

    [Theory]
    [InlineData("Swift.OpaquePointer")]
    [InlineData("Swift.UnsafePointer")]
    [InlineData("Swift.UnsafeMutablePointer")]
    [InlineData("Swift.UnsafeRawPointer")]
    [InlineData("Swift.UnsafeMutableRawPointer")]
    [InlineData("Builtin.RawPointer")]
    public void IsTypeProcessed_PointerType_ReturnsTrue(string pointerTypeName)
    {
        var typeDatabase = new TypeDatabase();

        var result = typeDatabase.IsTypeProcessed(new NamedTypeSpec(pointerTypeName));

        Assert.True(result);
    }

    [Theory]
    [InlineData("Swift.OpaquePointer")]
    [InlineData("Swift.UnsafePointer")]
    [InlineData("Swift.UnsafeMutablePointer")]
    [InlineData("Swift.UnsafeRawPointer")]
    [InlineData("Swift.UnsafeMutableRawPointer")]
    [InlineData("Builtin.RawPointer")]
    public void TryGetAnyTypeFallbackInfo_PointerType_ReturnsFalse(string pointerTypeName)
    {
        var typeDatabase = new TypeDatabase();

        var found = typeDatabase.TryGetAnyTypeFallbackInfo(new NamedTypeSpec(pointerTypeName), out var fallbackInfo);

        Assert.False(found);
        Assert.Null(fallbackInfo);
    }

    [Theory]
    [InlineData("Swift.OpaquePointer")]
    [InlineData("Swift.UnsafePointer")]
    [InlineData("Swift.UnsafeMutablePointer")]
    [InlineData("Swift.UnsafeRawPointer")]
    [InlineData("Swift.UnsafeMutableRawPointer")]
    [InlineData("Builtin.RawPointer")]
    public void TryGetTypeRecord_PointerType_ReturnsIntPtrType(string pointerTypeName)
    {
        var typeDatabase = new TypeDatabase();

        var found = typeDatabase.TryGetTypeRecord(new NamedTypeSpec(pointerTypeName), out var record);

        Assert.True(found);
        Assert.NotNull(record);
        Assert.Equal(TypeDatabaseExtensions.IntPtrType, record);
    }

    // --- ObjC module type tests (ObjectiveC module only) ---

    [Fact]
    public void GetTypeRecordOrAnyType_ObjCNSObject_ReturnsObjCBridgedRecord()
    {
        var typeDatabase = new TypeDatabase();

        var record = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec("ObjectiveC.NSObject"));

        Assert.Equal("Foundation.NSObject", record.CSharpTypeName.FullyQualifiedName);
        Assert.True((record.Flags & TypeRecordFlags.ObjCBridged) != 0);
        Assert.True((record.Flags & TypeRecordFlags.RequiresMemoryManagement) != 0);
        Assert.Equal(TypeRecordKind.Class, record.Kind);
    }

    [Fact]
    public void GetTypeRecordOrThrow_ObjCNSObject_ReturnsObjCBridgedRecord()
    {
        var typeDatabase = new TypeDatabase();

        var record = typeDatabase.GetTypeRecordOrThrow(new NamedTypeSpec("ObjectiveC.NSObject"));

        Assert.Equal("Foundation.NSObject", record.CSharpTypeName.FullyQualifiedName);
        Assert.True((record.Flags & TypeRecordFlags.ObjCBridged) != 0);
    }

    [Fact]
    public void TryGetAnyTypeFallbackInfo_ObjCType_ReturnsFalse()
    {
        var typeDatabase = new TypeDatabase();

        var found = typeDatabase.TryGetAnyTypeFallbackInfo(new NamedTypeSpec("ObjectiveC.NSObject"), out var fallbackInfo);

        Assert.False(found);
        Assert.Null(fallbackInfo);
    }

    // --- Foundation.NSObject (remapped from ObjectiveC.NSObject by TypeSpecParser) ---

    [Fact]
    public void GetTypeRecordOrAnyType_FoundationNSObject_ReturnsObjCBridgedRecord()
    {
        var typeDatabase = new TypeDatabase();

        // TypeSpecParser remaps ObjectiveC.NSObject → Foundation.NSObject
        var record = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec("Foundation.NSObject"));

        Assert.Equal("Foundation.NSObject", record.CSharpTypeName.FullyQualifiedName);
        Assert.True((record.Flags & TypeRecordFlags.ObjCBridged) != 0);
        Assert.True((record.Flags & TypeRecordFlags.RequiresMemoryManagement) != 0);
        Assert.Equal(TypeRecordKind.Class, record.Kind);
    }

    [Fact]
    public void GetTypeRecordOrThrow_FoundationNSObject_ReturnsObjCBridgedRecord()
    {
        var typeDatabase = new TypeDatabase();

        var record = typeDatabase.GetTypeRecordOrThrow(new NamedTypeSpec("Foundation.NSObject"));

        Assert.Equal("Foundation.NSObject", record.CSharpTypeName.FullyQualifiedName);
        Assert.True((record.Flags & TypeRecordFlags.ObjCBridged) != 0);
    }

    [Fact]
    public void TryGetAnyTypeFallbackInfo_FoundationNSObject_ReturnsFalse()
    {
        var typeDatabase = new TypeDatabase();

        var found = typeDatabase.TryGetAnyTypeFallbackInfo(new NamedTypeSpec("Foundation.NSObject"), out var fallbackInfo);

        Assert.False(found);
        Assert.Null(fallbackInfo);
    }

    [Fact]
    public void IsTypeProcessed_FoundationNSObject_ReturnsTrue()
    {
        var typeDatabase = new TypeDatabase();

        var result = typeDatabase.IsTypeProcessed(new NamedTypeSpec("Foundation.NSObject"));

        Assert.True(result);
    }

    [Fact]
    public void TryGetTypeRecord_FoundationNSObject_ReturnsObjCBridgedRecord()
    {
        var typeDatabase = new TypeDatabase();

        var found = typeDatabase.TryGetTypeRecord(new NamedTypeSpec("Foundation.NSObject"), out var record);

        Assert.True(found);
        Assert.NotNull(record);
        Assert.True((record.Flags & TypeRecordFlags.ObjCBridged) != 0);
    }

    // --- Non-class ObjectiveC module types are NOT auto-bridged ---

    [Fact]
    public void GetTypeRecordOrAnyType_ObjCSelector_ReturnsAnyType()
    {
        var typeDatabase = new TypeDatabase();

        // ObjectiveC.Selector is a struct, not an NSObject subclass.
        // TypeSpecParser remaps it to Foundation.Selector.
        var record = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec("Foundation.Selector"));

        Assert.Equal(TypeDatabaseExtensions.AnyType, record);
    }

    [Fact]
    public void GetTypeRecordOrAnyType_ObjCSelectorDirect_ReturnsAnyType()
    {
        var typeDatabase = new TypeDatabase();

        // Direct ObjectiveC.Selector (bypassing TypeSpecParser) should also not be bridged
        var record = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec("ObjectiveC.Selector"));

        Assert.Equal(TypeDatabaseExtensions.AnyType, record);
    }

    [Fact]
    public void GetTypeRecordOrThrow_ObjCSelector_Throws()
    {
        var typeDatabase = new TypeDatabase();

        // Selector is not a root class, so it should throw (not return synthetic ObjCBridged)
        Assert.Throws<Exception>(() =>
            typeDatabase.GetTypeRecordOrThrow(new NamedTypeSpec("ObjectiveC.Selector")));
    }

    // --- Non-ObjectiveC framework types are NOT auto-bridged ---

    [Fact]
    public void GetTypeRecordOrAnyType_UIKitType_ReturnsAnyType()
    {
        var typeDatabase = new TypeDatabase();

        // UIKit types are not auto-bridged — they could be value types or enums
        var record = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec("UIKit.UIViewController"));

        Assert.Equal(TypeDatabaseExtensions.AnyType, record);
    }

    [Fact]
    public void GetTypeRecordOrAnyType_FoundationType_ReturnsAnyType()
    {
        var typeDatabase = new TypeDatabase();

        // Foundation types are not auto-bridged — they could be value types or enums
        var record = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec("Foundation.NSData"));

        Assert.Equal(TypeDatabaseExtensions.AnyType, record);
    }

    [Fact]
    public void TryGetAnyTypeFallbackInfo_UIKitType_ReturnsMissing()
    {
        var typeDatabase = new TypeDatabase();

        // UIKit types not in the database should report as missing (not silently suppressed)
        var found = typeDatabase.TryGetAnyTypeFallbackInfo(new NamedTypeSpec("UIKit.UIView"), out var fallbackInfo);

        Assert.True(found);
        Assert.NotNull(fallbackInfo);
        Assert.Equal("Type is missing from the type database", fallbackInfo.Value.Reason);
    }

    // --- Negative / boundary tests ---

    [Fact]
    public void GetTypeRecordOrAnyType_UnknownModuleType_ReturnsAnyType()
    {
        var typeDatabase = new TypeDatabase();

        var record = typeDatabase.GetTypeRecordOrAnyType(new NamedTypeSpec("SomeUnknownModule.SomeType"));

        Assert.Equal(TypeDatabaseExtensions.AnyType, record);
    }

    [Fact]
    public void GetTypeRecordOrThrow_UnknownModuleType_Throws()
    {
        var typeDatabase = new TypeDatabase();

        Assert.Throws<Exception>(() =>
            typeDatabase.GetTypeRecordOrThrow(new NamedTypeSpec("SomeUnknownModule.SomeType")));
    }
}
