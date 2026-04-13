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

    [Fact]
    public void AnyObjectDetection_SelfConformance_Matches()
    {
        // τ_0_0 : AnyObject — Self is class-bound
        Assert.Matches(@"τ_0_0\s*:[^,]*\bAnyObject\b", "<τ_0_0 : AnyObject>");
    }

    [Fact]
    public void AnyObjectDetection_SelfConformanceWithWhere_Matches()
    {
        // τ_0_0 : AnyObject via where clause
        Assert.Matches(@"τ_0_0\s*:[^,]*\bAnyObject\b", "<τ_0_0 where τ_0_0 : AnyObject>");
    }

    [Fact]
    public void AnyObjectDetection_SelfConformanceWithOtherProtocols_Matches()
    {
        // τ_0_0 : Foo & AnyObject — Self conforms to both
        Assert.Matches(@"τ_0_0\s*:[^,]*\bAnyObject\b", "<τ_0_0 where τ_0_0 : SomeProtocol & AnyObject>");
    }

    [Fact]
    public void AnyObjectDetection_AssociatedTypeConformance_DoesNotMatch()
    {
        // τ_0_0.Element : AnyObject — associated type is class-bound, NOT Self
        Assert.DoesNotMatch(@"τ_0_0\s*:[^,]*\bAnyObject\b", "<τ_0_0 where τ_0_0.Element : AnyObject>");
    }

    [Fact]
    public void AnyObjectDetection_MixedConstraints_OnlyMatchesSelf()
    {
        // τ_0_0 : SomeProtocol, τ_0_0.Element : AnyObject
        // Self does NOT conform to AnyObject, only the associated type does
        Assert.DoesNotMatch(@"τ_0_0\s*:[^,]*\bAnyObject\b",
            "<τ_0_0 where τ_0_0 : SomeProtocol, τ_0_0.Element : AnyObject>");
    }

    [Fact]
    public void AnyObjectDetection_SelfAndAssociatedBoth_MatchesSelf()
    {
        // τ_0_0 : AnyObject, τ_0_0.Element : SomeProtocol
        Assert.Matches(@"τ_0_0\s*:[^,]*\bAnyObject\b",
            "<τ_0_0 where τ_0_0 : AnyObject, τ_0_0.Element : SomeProtocol>");
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

    private static MethodDecl CreateMethodDecl(string name, bool isProtocolRequirement = false)
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
            Visibility = Visibility.Public,
            IsProtocolRequirement = isProtocolRequirement
        };
    }

    #endregion

    #region MissingRequirements / IsProtocolRequirement Tests

    [Fact]
    public void MethodDecl_IsProtocolRequirement_DefaultFalse()
    {
        var method = CreateMethodDecl("doWork");
        Assert.False(method.IsProtocolRequirement);
    }

    [Fact]
    public void MethodDecl_IsProtocolRequirement_CanBeSet()
    {
        var method = CreateMethodDecl("doWork", isProtocolRequirement: true);
        Assert.True(method.IsProtocolRequirement);
    }

    [Fact]
    public void Protocol_ExtensionDefaultsNotCountedAsMissing()
    {
        // TipKit.Tip pattern: protocol has properties as requirements (protocolReq=true)
        // and extension methods (protocolReq=false) that fail ABI parsing.
        // Only protocolReq=true Function/Constructor children should count as missing.
        var protocol = CreateProtocolDecl("Tip");
        // Simulate: 2 extension default methods failed to parse (not in Methods list)
        // but they are NOT requirements. No protocolReq=true Function/Constructor children.
        // HasMissingRequirements should be false.
        Assert.False(protocol.HasMissingRequirements);
        Assert.Equal(0, protocol.Methods.Count(m => m.IsProtocolRequirement));
    }

    [Fact]
    public void Protocol_AllRequiredMethodsParsed_NoMissingRequirements()
    {
        // Protocol with required methods that all parsed successfully.
        var protocol = CreateProtocolDecl("ValidProtocol");
        protocol.Methods.Add(CreateMethodDecl("doWork", isProtocolRequirement: true));
        protocol.Methods.Add(CreateMethodDecl("configure", isProtocolRequirement: true));
        // Extension default that may or may not have parsed — doesn't affect MissingRequirements
        protocol.Methods.Add(CreateMethodDecl("defaultBehavior", isProtocolRequirement: false));

        Assert.False(protocol.HasMissingRequirements);
        Assert.Equal(2, protocol.Methods.Count(m => m.IsProtocolRequirement));
    }

    #endregion
}
