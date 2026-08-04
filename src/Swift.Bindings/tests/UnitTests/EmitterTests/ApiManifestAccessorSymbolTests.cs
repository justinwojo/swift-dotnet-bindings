// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="ModuleEmissionContext.BuildAccessorEntrySymbol"/> — the entry symbol the API
/// manifest records for an accessor-backed member.
///
/// <para>A property or subscript is ONE consumer-visible member but TWO native entry points, and they
/// retarget independently: a read/write property's setter can start binding a different symbol while
/// its getter is untouched. The manifest gate compares baseline↔current symbol per key, so recording
/// only the getter would let exactly that half of the ABI contract move without the gate seeing
/// it.</para>
///
/// <para>Scope: these cover the helper's own contract — pairing, ordering, and preferring a
/// promoted symbol over the silgen name. That the accessor emission paths actually RECORD their
/// promoted symbol is a production-path property, and its gate is the committed API-manifest
/// baseline: the corpus holds thunk-dispatched properties whose accessors bind a <c>…vgTj</c>
/// entry point, so an emission path that stopped recording would baseline the silgen name instead
/// and show up as a retarget.</para>
/// </summary>
public class ApiManifestAccessorSymbolTests
{
    [Fact]
    public void ReadWriteMember_CarriesBothAccessorSymbols()
    {
        var ctx = new ModuleEmissionContext();
        var property = TestDecls.Property("amount", hasGetter: true, hasSetter: true);

        var symbol = ctx.BuildAccessorEntrySymbol(property.Accessors);

        Assert.NotNull(symbol);
        var halves = symbol!.Split('|');
        Assert.Equal(2, halves.Length);
        // Get first, then set — a fixed order, so the recorded value is stable across runs rather
        // than tracking whichever accessor the parser happened to list first.
        Assert.Equal(property.Accessors.OfType<GetAccessorDecl>().Single().Method.MangledName, halves[0]);
        Assert.Equal(property.Accessors.OfType<SetAccessorDecl>().Single().Method.MangledName, halves[1]);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void SingleAccessorMember_CarriesThatSymbolBare(bool hasGetter, bool hasSetter)
    {
        var ctx = new ModuleEmissionContext();
        var property = TestDecls.Property("amount", hasGetter: hasGetter, hasSetter: hasSetter);

        var symbol = ctx.BuildAccessorEntrySymbol(property.Accessors);

        Assert.NotNull(symbol);
        Assert.DoesNotContain('|', symbol!);
        Assert.Equal(property.Accessors.Single().Method.MangledName, symbol);
    }

    [Fact]
    public void MemberWithNoAccessor_RecordsNothing()
    {
        // The caller falls back (a subscript to its own mangled name) or skips the entry entirely
        // rather than recording an empty symbol, which would baseline as a real value.
        var ctx = new ModuleEmissionContext();
        var property = TestDecls.Property("amount", hasGetter: false, hasSetter: false);

        Assert.Null(ctx.BuildAccessorEntrySymbol(property.Accessors));
    }

    [Fact]
    public void PromotedAccessorSymbol_IsPreferredOverTheSilgenName()
    {
        // The recorded symbol must be the one the P/Invoke actually binds: an accessor routed
        // through the wrapper library binds the symbol its emission promoted to, so the manifest
        // has to carry that rather than the silgen name the promotion replaced.
        var ctx = new ModuleEmissionContext();
        var property = TestDecls.Property("amount", hasGetter: true, hasSetter: true);
        var setter = property.Accessors.OfType<SetAccessorDecl>().Single().Method;
        setter.UsesWrapperLibrary = true;
        ctx.RecordMethodEmissionSymbol(setter, "SBW_amount_set");

        var symbol = ctx.BuildAccessorEntrySymbol(property.Accessors);

        Assert.Equal("SBW_amount_set", symbol!.Split('|')[1]);
    }

    [Fact]
    public void DirectlyCalledAccessor_CarriesItsDispatchThunkNotThePromotedBase()
    {
        // The mirror case: an accessor that does NOT route through the wrapper library binds
        // whatever the call-target resolver names for it — for a non-final class member, the `Tj`
        // dispatch thunk — no matter what symbol emission promoted onto it. Recording the promoted
        // base here would name a symbol the binding never mentions.
        var ctx = new ModuleEmissionContext();
        var property = TestDecls.Property("amount", hasGetter: true, hasSetter: false);
        var getter = property.Accessors.OfType<GetAccessorDecl>().Single().Method;
        ctx.RecordMethodEmissionSymbol(getter, "SBW_amount_get");

        var symbol = ctx.BuildAccessorEntrySymbol(property.Accessors);

        Assert.NotEqual("SBW_amount_get", symbol);
        Assert.StartsWith(getter.MangledName, symbol);
    }
}
