// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Value types an Apple framework declares in Swift under an Objective-C class prefix. The SDK's
/// Swift has no class by these names, only a struct or enum (most of them resilient, so Swift reads
/// them through an address), but the prefix fallback that bridges an unresolved <c>UI…</c> or
/// <c>NW…</c> name as an Objective-C object would pass the .NET object's handle where Swift expects
/// the value. The registry lists them as value types so every reader of the fallback (the
/// synthetic class record, the Optional projection and <see cref="MarshallingHelpers.IsOptionalObjCBridged"/>)
/// refuses them alike.
/// </summary>
public class SwiftOnlyAppleValueTypeTests
{
    [Theory]
    [InlineData("UIKit.UIListContentConfiguration")]
    [InlineData("UIKit.UIBackgroundConfiguration")]
    [InlineData("UIKit.UICellAccessory")]
    [InlineData("UIKit.NSDiffableDataSourceSnapshot")]
    [InlineData("AppKit.NSDiffableDataSourceSnapshot")]
    [InlineData("Network.NWPath")]
    [InlineData("Network.NWEndpoint")]
    [InlineData("CoreML.MLShapedArray")]
    [InlineData("PhotosUI.PHPickerConfiguration")]
    [InlineData("Foundation.NSKeyValueObservedChange")]
    public void SwiftDeclaredPrefixedValueType_NeverTakesTheObjCClassBridge(string swiftName)
    {
        var named = new NamedTypeSpec(swiftName);
        Assert.True(AppleFrameworkRegistry.IsKnownValueType(swiftName));
        Assert.False(MarshallingHelpers.IsObjCPrefixBridgeCandidate(named));
        Assert.False(TypeDatabaseExtensions.IsObjCModuleType(named));

        var database = new TypeDatabase();
        Assert.False(database.TryGetTypeRecord(SwiftTypeName.FromModuleQualifiedName(swiftName), out _));

        var optional = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec(swiftName));
        Assert.False(MarshallingHelpers.IsOptionalObjCBridged(optional, database));
        var projection = new TypeProjectionFactory().Project(optional, new ProjectionContext { TypeDatabase = database, IsParameter = true });
        Assert.False(projection is OptionalProjection { InnerProjection: ObjCBridgedProjection });
    }

    [Theory]
    [InlineData("UIKit.UIView")]
    [InlineData("UIKit.UIListContentView")]
    [InlineData("PhotosUI.PHPickerViewController")]
    public void PrefixedObjCClass_StillTakesTheBridge(string swiftName)
    {
        // Positive control: the registry entries above must not swallow real classes that share a prefix.
        Assert.False(AppleFrameworkRegistry.IsKnownValueType(swiftName));
        Assert.True(MarshallingHelpers.IsObjCPrefixBridgeCandidate(new NamedTypeSpec(swiftName)));
    }
}
