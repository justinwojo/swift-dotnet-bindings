// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for ProtocolDecl and protocol-related parsing functionality.
/// </summary>
public class ProtocolParserTests
{
    #region ProtocolDecl Tests

    [Fact]
    public void ProtocolDecl_DefaultValues_AreCorrect()
    {
        var protocolDecl = CreateProtocolDecl("TestProtocol");

        Assert.Equal("TestProtocol", protocolDecl.Name);
        Assert.Empty(protocolDecl.AssociatedTypes);
        Assert.False(protocolDecl.HasSelfRequirement);
        Assert.Empty(protocolDecl.InheritedProtocols);
        Assert.Null(protocolDecl.GenericSignature);
        Assert.False(protocolDecl.IsClassBound);
    }

    [Fact]
    public void ProtocolDecl_CanBeExistential_TrueForSimpleProtocol()
    {
        var protocolDecl = CreateProtocolDecl("SimpleProtocol");

        Assert.True(protocolDecl.CanBeExistential);
    }

    [Fact]
    public void ProtocolDecl_CanBeExistential_FalseWithSelfRequirement()
    {
        var protocolDecl = CreateProtocolDecl("SelfRequiringProtocol");
        protocolDecl.HasSelfRequirement = true;

        Assert.False(protocolDecl.CanBeExistential);
    }

    [Fact]
    public void ProtocolDecl_CanBeExistential_FalseWithAssociatedTypes()
    {
        var protocolDecl = CreateProtocolDecl("PATProtocol");
        protocolDecl.AssociatedTypes.Add(new AssociatedTypeDecl { Name = "Element" });

        Assert.False(protocolDecl.CanBeExistential);
    }

    [Fact]
    public void ProtocolDecl_CanBeExistential_FalseWithBothSelfAndAssociatedTypes()
    {
        var protocolDecl = CreateProtocolDecl("ComplexProtocol");
        protocolDecl.HasSelfRequirement = true;
        protocolDecl.AssociatedTypes.Add(new AssociatedTypeDecl { Name = "Element" });

        Assert.False(protocolDecl.CanBeExistential);
    }

    [Fact]
    public void ProtocolDecl_WithInheritedProtocols()
    {
        var protocolDecl = CreateProtocolDecl("DerivedProtocol");
        protocolDecl.InheritedProtocols.Add(new NamedTypeSpec("Swift.Equatable"));
        protocolDecl.InheritedProtocols.Add(new NamedTypeSpec("Swift.Hashable"));

        Assert.Equal(2, protocolDecl.InheritedProtocols.Count);
    }

    [Fact]
    public void ProtocolDecl_IsClassBound_True()
    {
        var protocolDecl = CreateProtocolDecl("ClassOnlyProtocol");
        protocolDecl.IsClassBound = true;

        Assert.True(protocolDecl.IsClassBound);
    }

    [Fact]
    public void ProtocolDecl_WithGenericSignature()
    {
        var protocolDecl = CreateProtocolDecl("GenericProtocol");
        protocolDecl.GenericSignature = "<Self where Self: Equatable>";
        protocolDecl.HasSelfRequirement = true;

        Assert.NotNull(protocolDecl.GenericSignature);
        Assert.Contains("Self", protocolDecl.GenericSignature);
    }

    #endregion

    #region AssociatedTypeDecl Tests

    [Fact]
    public void AssociatedTypeDecl_CanBeCreated()
    {
        var associatedType = new AssociatedTypeDecl
        {
            Name = "Element"
        };

        Assert.Equal("Element", associatedType.Name);
        Assert.Null(associatedType.DefaultType);
        Assert.Empty(associatedType.Constraints);
    }

    [Fact]
    public void AssociatedTypeDecl_WithDefaultType()
    {
        var associatedType = new AssociatedTypeDecl
        {
            Name = "Element",
            DefaultType = new NamedTypeSpec("Swift.Int")
        };

        Assert.Equal("Element", associatedType.Name);
        Assert.NotNull(associatedType.DefaultType);
        Assert.IsType<NamedTypeSpec>(associatedType.DefaultType);
    }

    [Fact]
    public void AssociatedTypeDecl_WithConstraints()
    {
        var associatedType = new AssociatedTypeDecl
        {
            Name = "Element",
            Constraints = new List<string> { "Equatable", "Hashable" }
        };

        Assert.Equal(2, associatedType.Constraints.Count);
        Assert.Contains("Equatable", associatedType.Constraints);
        Assert.Contains("Hashable", associatedType.Constraints);
    }

    #endregion

    #region Protocol Properties and Methods Tests

    [Fact]
    public void ProtocolDecl_CanHaveProperties()
    {
        var protocolDecl = CreateProtocolDecl("PropertyProtocol");
        protocolDecl.Properties.Add(CreatePropertyDecl("value", "Swift.Int"));

        Assert.Single(protocolDecl.Properties);
        Assert.Equal("value", protocolDecl.Properties[0].Name);
    }

    [Fact]
    public void ProtocolDecl_CanHaveMethods()
    {
        var protocolDecl = CreateProtocolDecl("MethodProtocol");
        protocolDecl.Methods.Add(CreateMethodDecl("doSomething"));

        Assert.Single(protocolDecl.Methods);
        Assert.Equal("doSomething", protocolDecl.Methods[0].Name);
    }

    [Fact]
    public void ProtocolDecl_CanHaveMultipleMembers()
    {
        var protocolDecl = CreateProtocolDecl("RichProtocol");
        protocolDecl.Properties.Add(CreatePropertyDecl("count", "Swift.Int"));
        protocolDecl.Properties.Add(CreatePropertyDecl("isEmpty", "Swift.Bool"));
        protocolDecl.Methods.Add(CreateMethodDecl("reset"));
        protocolDecl.Methods.Add(CreateMethodDecl("validate"));

        Assert.Equal(2, protocolDecl.Properties.Count);
        Assert.Equal(2, protocolDecl.Methods.Count);
    }

    #endregion

    #region Integration Tests

    [Fact]
    public void ProtocolDecl_CompleteProtocol_AllPropertiesSet()
    {
        var protocolDecl = CreateProtocolDecl("CompleteProtocol");
        protocolDecl.AssociatedTypes.Add(new AssociatedTypeDecl { Name = "Element" });
        protocolDecl.AssociatedTypes.Add(new AssociatedTypeDecl { Name = "Index" });
        protocolDecl.HasSelfRequirement = true;
        protocolDecl.InheritedProtocols.Add(new NamedTypeSpec("Swift.Collection"));
        protocolDecl.GenericSignature = "<Self, Element, Index>";
        protocolDecl.IsClassBound = false;
        protocolDecl.Properties.Add(CreatePropertyDecl("startIndex", "Index"));
        protocolDecl.Methods.Add(CreateMethodDecl("subscript"));

        Assert.Equal("CompleteProtocol", protocolDecl.Name);
        Assert.Equal(2, protocolDecl.AssociatedTypes.Count);
        Assert.True(protocolDecl.HasSelfRequirement);
        Assert.Single(protocolDecl.InheritedProtocols);
        Assert.NotNull(protocolDecl.GenericSignature);
        Assert.False(protocolDecl.IsClassBound);
        Assert.Single(protocolDecl.Properties);
        Assert.Single(protocolDecl.Methods);
        Assert.False(protocolDecl.CanBeExistential);
    }

    #endregion

    #region Helper Methods

    private static ProtocolDecl CreateProtocolDecl(string name)
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
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static PropertyDecl CreatePropertyDecl(string name, string typeName)
    {
        return new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = new NamedTypeSpec(typeName),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl
                {
                    Method = new MethodDecl
                    {
                        Name = $"{name}_Get",
                        MangledName = "",
                        MethodType = MethodType.Instance,
                        IsConstructor = false,
                        CSSignature = new List<ArgumentDecl>(),
                        GenericParameters = new List<GenericArgumentDecl>(),
                        ParentDecl = null,
                        ModuleDecl = null,
                        Throws = false,
                        IsAsync = false,
                        Visibility = Visibility.Public
                    }
                }
            },
            ParentDecl = null,
            ModuleDecl = null
        };
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
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    Name = "",
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
