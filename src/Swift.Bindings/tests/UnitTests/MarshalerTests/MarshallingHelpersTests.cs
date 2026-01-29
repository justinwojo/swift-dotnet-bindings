// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for MarshallingHelpers utility methods.
/// </summary>
public class MarshallingHelpersTests
{
    #region MethodIsSetter Tests

    [Fact]
    public void MethodIsSetter_ReturnsTrueForSetterMethod()
    {
        var method = CreateMethodDecl("myProperty_Set");
        Assert.True(MarshallingHelpers.MethodIsSetter(method));
    }

    [Fact]
    public void MethodIsSetter_ReturnsTrueForSetterWithUnderscoreInName()
    {
        var method = CreateMethodDecl("my_Property_Set");
        Assert.True(MarshallingHelpers.MethodIsSetter(method));
    }

    [Fact]
    public void MethodIsSetter_ReturnsFalseForGetterMethod()
    {
        var method = CreateMethodDecl("myProperty_Get");
        Assert.False(MarshallingHelpers.MethodIsSetter(method));
    }

    [Fact]
    public void MethodIsSetter_ReturnsFalseForRegularMethod()
    {
        var method = CreateMethodDecl("doSomething");
        Assert.False(MarshallingHelpers.MethodIsSetter(method));
    }

    [Fact]
    public void MethodIsSetter_ReturnsFalseForMethodEndingInSet()
    {
        // "Set" without underscore is not a setter
        var method = CreateMethodDecl("resetSet");
        Assert.False(MarshallingHelpers.MethodIsSetter(method));
    }

    [Fact]
    public void MethodIsSetter_ReturnsFalseForMethodContainingSetInMiddle()
    {
        var method = CreateMethodDecl("set_something");
        Assert.False(MarshallingHelpers.MethodIsSetter(method));
    }

    [Fact]
    public void MethodIsSetter_IsCaseSensitive()
    {
        // "_set" (lowercase) should not match
        var method = CreateMethodDecl("myProperty_set");
        Assert.False(MarshallingHelpers.MethodIsSetter(method));
    }

    #endregion

    #region IsObjCBridged Tests

    [Fact]
    public void IsObjCBridged_ReturnsTrueWhenFlagIsSet()
    {
        var typeRecord = CreateTypeRecord(TypeRecordFlags.ObjCBridged);
        Assert.True(MarshallingHelpers.IsObjCBridged(typeRecord));
    }

    [Fact]
    public void IsObjCBridged_ReturnsFalseWhenFlagIsNotSet()
    {
        var typeRecord = CreateTypeRecord(TypeRecordFlags.None);
        Assert.False(MarshallingHelpers.IsObjCBridged(typeRecord));
    }

    [Fact]
    public void IsObjCBridged_ReturnsTrueWhenObjCBridgedCombinedWithOtherFlags()
    {
        var typeRecord = CreateTypeRecord(TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement);
        Assert.True(MarshallingHelpers.IsObjCBridged(typeRecord));
    }

    [Fact]
    public void IsObjCBridged_ReturnsFalseForFrozenType()
    {
        var typeRecord = CreateTypeRecord(TypeRecordFlags.Frozen);
        Assert.False(MarshallingHelpers.IsObjCBridged(typeRecord));
    }

    [Fact]
    public void IsObjCBridged_ReturnsFalseForRequiresMemoryManagement()
    {
        var typeRecord = CreateTypeRecord(TypeRecordFlags.RequiresMemoryManagement);
        Assert.False(MarshallingHelpers.IsObjCBridged(typeRecord));
    }

    #endregion

    #region Helper Methods

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
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    Name = string.Empty,
                    PrivateName = string.Empty,
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
            Visibility = Visibility.Private
        };
    }

    private static TypeRecord CreateTypeRecord(TypeRecordFlags flags)
    {
        return new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Test", "TestType"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Test.TestType"),
            MetadataAccessor = "testAccessor",
            Flags = flags,
            Kind = TypeRecordKind.Class
        };
    }

    #endregion
}
