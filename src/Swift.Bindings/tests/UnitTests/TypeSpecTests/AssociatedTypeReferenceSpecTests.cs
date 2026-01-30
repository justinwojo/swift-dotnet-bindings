// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for AssociatedTypeReferenceSpec.
/// </summary>
public class AssociatedTypeReferenceSpecTests
{
    [Fact]
    public void Constructor_ParsesSelfDotElement()
    {
        var spec = new AssociatedTypeReferenceSpec("Self.Element");

        Assert.Equal("Self", spec.BaseType);
        Assert.Equal("Element", spec.AssociatedTypeName);
    }

    [Fact]
    public void Constructor_ParsesGenericParamDotAssociatedType()
    {
        var spec = new AssociatedTypeReferenceSpec("τ_0_0.Iterator");

        Assert.Equal("τ_0_0", spec.BaseType);
        Assert.Equal("Iterator", spec.AssociatedTypeName);
    }

    [Fact]
    public void Constructor_HandlesBaseTypeOnly()
    {
        var spec = new AssociatedTypeReferenceSpec("Self");

        Assert.Equal("Self", spec.BaseType);
        Assert.Equal(string.Empty, spec.AssociatedTypeName);
    }

    [Fact]
    public void ToString_IncludesFullPath()
    {
        var spec = new AssociatedTypeReferenceSpec("Self.Element");

        Assert.Equal("Self.Element", spec.ToString());
    }

    [Fact]
    public void ToString_ReturnsBaseTypeOnly_WhenNoAssociatedType()
    {
        var spec = new AssociatedTypeReferenceSpec("Self", string.Empty);

        Assert.Equal("Self", spec.ToString());
    }

    [Fact]
    public void HasDynamicSelf_IsTrueForSelf()
    {
        var spec = new AssociatedTypeReferenceSpec("Self.Element");

        Assert.True(spec.HasDynamicSelf);
    }

    [Fact]
    public void HasDynamicSelf_IsFalseForNonSelfBase()
    {
        var spec = new AssociatedTypeReferenceSpec("τ_0_0.Element");

        Assert.False(spec.HasDynamicSelf);
    }

    [Fact]
    public void Equals_ReturnsTrueForSameSpec()
    {
        var spec1 = new AssociatedTypeReferenceSpec("Self.Element");
        var spec2 = new AssociatedTypeReferenceSpec("Self.Element");

        Assert.Equal(spec1, spec2);
    }

    [Fact]
    public void Equals_ReturnsFalseForDifferentSpec()
    {
        var spec1 = new AssociatedTypeReferenceSpec("Self.Element");
        var spec2 = new AssociatedTypeReferenceSpec("Self.Index");

        Assert.NotEqual(spec1, spec2);
    }

    [Fact]
    public void Kind_IsNamed()
    {
        var spec = new AssociatedTypeReferenceSpec("Self.Element");

        Assert.Equal(TypeSpecKind.Named, spec.Kind);
    }
}
