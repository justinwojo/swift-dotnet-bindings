// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Finding 48: <see cref="NameProvider.GetAccessModifier(bool)"/> maps the
/// <c>IsSynthesizedAccessor</c> bit — the only distinction the old, access-control-shaped
/// <c>Visibility</c> enum ever actually drew — to a C# access keyword. A synthesized accessor
/// (a stored-property/subscript getter or setter the parser produces) emits as a <c>private</c>
/// helper behind the public property/indexer; every other method emits as <c>public</c>. The
/// enum's vestigial <c>Internal</c> arm is gone (module-internal-ness lives on
/// <c>BaseDecl.IsModuleInternal</c>, never on this field), so there is no third mapping.
/// </summary>
public class AccessModifierTests
{
    [Theory]
    [InlineData(true, "private")]   // synthesized accessor → private helper
    [InlineData(false, "public")]   // ordinary method → public
    public void GetAccessModifier_MapsSynthesizedAccessorBit(bool isSynthesizedAccessor, string expected)
    {
        Assert.Equal(expected, NameProvider.GetAccessModifier(isSynthesizedAccessor));
    }

    [Fact]
    public void MethodDecl_IsSynthesizedAccessor_DefaultsToFalse()
    {
        // An ordinary (non-accessor) method is public by default — the field starts false and the
        // emitter renders it as `public` via GetAccessModifier.
        var method = new MethodDecl
        {
            Name = "doThing",
            MangledName = "$s7doThing",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new System.Collections.Generic.List<ArgumentDecl>(),
            GenericParameters = new System.Collections.Generic.List<GenericArgumentDecl>(),
            Throws = false,
            IsAsync = false,
            ParentDecl = null,
            ModuleDecl = null,
        };

        Assert.False(method.IsSynthesizedAccessor);
        Assert.Equal("public", NameProvider.GetAccessModifier(method.IsSynthesizedAccessor));
    }
}
