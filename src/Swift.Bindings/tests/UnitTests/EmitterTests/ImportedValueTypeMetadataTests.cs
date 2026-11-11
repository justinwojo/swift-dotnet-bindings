// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Which type-database structs get a wrapper metadata accessor registered by the module
/// initializer: imported C structs projected as an existing .NET value type, whose Swift metadata
/// nothing else can supply to a generic over them (<c>[CMTime]</c>, <c>CMTime?</c>).
/// </summary>
public class ImportedValueTypeMetadataTests
{
    private static readonly string s_dbDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Swift");

    private static async Task<TypeRecord> LoadRecordAsync(string xmlFileName, string moduleQualifiedName)
    {
        var typeDatabase = new TypeDatabase();
        await typeDatabase.LoadModuleDatabaseFromFile(Path.Combine(s_dbDir, xmlFileName));
        Assert.True(typeDatabase.TryGetTypeRecord(SwiftTypeName.FromModuleQualifiedName(moduleQualifiedName), out var record));
        return record!;
    }

    [Theory]
    [InlineData("CoreMediaDatabase.xml", "CoreMedia.CMTime")]
    [InlineData("CoreMediaDatabase.xml", "CoreMedia.CMTimeRange")]
    [InlineData("CoreMediaDatabase.xml", "CoreMedia.CMTimeMapping")]
    [InlineData("UIKitDatabase.xml", "UIKit.UIEdgeInsets")]
    public async Task ImportedCStructProjectedAsDotNetValueType_IsRegistered(string xml, string name)
    {
        var record = await LoadRecordAsync(xml, name);
        Assert.True(ModuleHandler.IsImportedValueTypeProjection(record, boundModuleName: "SomeLibrary"));
    }

    [Theory]
    // The runtime resolves the CoreGraphics structs (projected into the Swift namespace) itself.
    [InlineData("CoreGraphicsDatabase.xml", "CoreGraphics.CGPoint")]
    // A Swift-native Foundation struct, not a C import.
    [InlineData("FoundationDatabase.xml", "Foundation.UUID")]
    // A pointer-sized keyword stand-in is no distinct type to register.
    [InlineData("FoundationDatabase.xml", "Foundation.Selector")]
    // An ObjC-bridged CF type.
    [InlineData("CoreMediaDatabase.xml", "CoreMedia.CMSampleBuffer")]
    public async Task OtherDatabaseTypes_AreNotRegistered(string xml, string name)
    {
        var record = await LoadRecordAsync(xml, name);
        Assert.False(ModuleHandler.IsImportedValueTypeProjection(record, boundModuleName: "SomeLibrary"));
    }

    [Fact]
    public async Task BindingTheDeclaringModuleItself_IsNotRegistered()
    {
        var record = await LoadRecordAsync("CoreMediaDatabase.xml", "CoreMedia.CMTime");
        Assert.False(ModuleHandler.IsImportedValueTypeProjection(record, boundModuleName: "CoreMedia"));
    }
}
