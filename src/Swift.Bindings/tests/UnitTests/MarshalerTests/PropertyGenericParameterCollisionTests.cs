// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// A property named like a type parameter of its declaring type: a stored <c>var t: T</c> on
/// <c>Box&lt;T&gt;</c> PascalCases to <c>T</c>, which C# rejects beside the type parameter (CS0102).
/// The property takes the member-value suffix, as a property named like its enclosing type does.
/// </summary>
public class PropertyGenericParameterCollisionTests
{
    private static StructDecl GenericStruct(string name, params string[] typeParameters)
    {
        var parameters = new List<GenericArgumentDecl>();
        for (int i = 0; i < typeParameters.Length; i++)
            parameters.Add(new GenericArgumentDecl($"τ_0_{i}", typeParameters[i], new List<GenericParameterConformance>(), new List<GenericParameterConformance>()));
        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}VN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = parameters,
            Conformances = new List<TypeConformance>(),
            IsFrozen = false,
            MetadataAccessor = string.Empty,
            ParentDecl = null,
            ModuleDecl = null,
        };
    }

    private static PropertyDecl PropertyOn(TypeDecl parent, string name)
    {
        var property = TestDecls.Property(name, new NamedTypeSpec("τ_0_0"));
        property.ParentDecl = parent;
        return property;
    }

    [Theory]
    [InlineData("t", "T", "TValue")]
    [InlineData("u", "U", "UValue")]
    public void PropertyNamedLikeItsTypesParameter_TakesTheMemberValueSuffix(string swiftName, string typeParameter, string expected)
    {
        var parent = GenericStruct("WideGeneric", typeParameter);

        Assert.Equal(expected, NameProvider.GetPropertyName(PropertyOn(parent, swiftName), parent.Name));
    }

    // A sibling already spelled like the suffixed name keeps it; the renamed property moves past
    // it instead of colliding, whichever is declared first.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SuffixedNameTakenByASibling_MovesPastIt(bool collidingFirst)
    {
        var parent = GenericStruct("WideGeneric", "T");
        var colliding = PropertyOn(parent, "t");
        var sibling = PropertyOn(parent, "tValue");
        parent.Properties.AddRange(collidingFirst ? new[] { colliding, sibling } : new[] { sibling, colliding });

        Assert.Equal("TValue2", NameProvider.GetPropertyName(colliding, parent.Name));
        Assert.Equal("TValue", NameProvider.GetPropertyName(sibling, parent.Name));
    }

    [Fact]
    public void PropertyNotNamedLikeAnyTypeParameter_KeepsItsName()
    {
        var parent = GenericStruct("WideGeneric", "T", "U");

        Assert.Equal("Item", NameProvider.GetPropertyName(PropertyOn(parent, "item"), parent.Name));
    }

    [Fact]
    public void SameNameOnANonGenericType_KeepsItsName()
    {
        var parent = GenericStruct("Plain");

        Assert.Equal("T", NameProvider.GetPropertyName(PropertyOn(parent, "t"), parent.Name));
    }
}
