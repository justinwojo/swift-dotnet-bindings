// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

using BindingsGeneration;

namespace BindingsGeneration.Tests;

/// <summary>
/// Pins the strip-to-withdrawal classifier's disposition rules: a stripped wrapper symbol whose owner
/// is a droppable leaf/accessor is convertible to an iteration-0 withdrawal; a symbol-less strip or a
/// fan-in owner (helper/conformance/vtable/module) fails closed. This mirrors what the verify-recover
/// controller actually accepts, so the (still fixtures-only) conversion cannot drift from the loop's
/// own droppability judgement — and the production loop path keeps SWIFTBIND115 fail-closed regardless.
/// </summary>
public class StripToWithdrawalClassifierTests
{
    private static ArtifactId Method(string name = "foo", ArtifactRole role = ArtifactRole.SwiftWrapper) =>
        ArtifactId.Create(DeclId.Create("Mod", "T", BindingItemKind.Method, name), role);

    private static ArtifactId PropertyAccessor(string name = "value", ArtifactRole role = ArtifactRole.SwiftWrapper) =>
        ArtifactId.Create(
            DeclId.Create("Mod", "T", BindingItemKind.Property, name, accessor: AccessorKind.Getter), role);

    private static ArtifactId MetadataHelper() =>
        ArtifactId.Create(DeclId.Create("Mod", "T", BindingItemKind.Type, "T"), ArtifactRole.MetadataHelper);

    private static ArtifactId ModuleInitializer() =>
        ArtifactId.Create(DeclId.Create("Mod", "", BindingItemKind.Type, "Mod"), ArtifactRole.ModuleInitializer);

    [Fact]
    public void Classify_OwnedDroppableLeafMethod_Withdraws()
    {
        var result = StripToWithdrawalClassifier.Classify("$sSymbolLeaf", Method());
        Assert.Equal(StripWithdrawalDisposition.Withdraw, result.Disposition);
        Assert.NotNull(result.Unit);
        Assert.Equal(RecoveryScope.LeafApi, result.Unit!.Value.Scope);
    }

    [Theory]
    [InlineData(ArtifactRole.CSharpPublic)]
    [InlineData(ArtifactRole.PInvoke)]
    [InlineData(ArtifactRole.SwiftWrapper)]
    [InlineData(ArtifactRole.Callback)]
    public void Classify_LeafRoleVariants_AllWithdrawAsLeafApi(ArtifactRole role)
    {
        // Every role the loop treats as a droppable leaf must classify identically — the strip side
        // must not accept a narrower set than the controller does.
        var result = StripToWithdrawalClassifier.Classify("$sSymbol", Method(role: role));
        Assert.Equal(StripWithdrawalDisposition.Withdraw, result.Disposition);
        Assert.Equal(RecoveryScope.LeafApi, result.Unit!.Value.Scope);
    }

    [Fact]
    public void Classify_OwnedDroppableAccessor_WithdrawsAsAccessorGroup()
    {
        var result = StripToWithdrawalClassifier.Classify("$sSymbolAccessor", PropertyAccessor());
        Assert.Equal(StripWithdrawalDisposition.Withdraw, result.Disposition);
        Assert.NotNull(result.Unit);
        // The accessor id is normalized to the property-level group, exactly as the loop withdraws a
        // property (getter and setter name one unit).
        Assert.Equal(RecoveryScope.AccessorGroup, result.Unit!.Value.Scope);
    }

    [Fact]
    public void Classify_NullOwner_FailsClosed()
    {
        // A symbol-less strip (EveryProtocol conformance, plain extension, dispatch protocol) or an
        // owner the emission side never threaded — nothing to attribute a withdrawal to.
        var result = StripToWithdrawalClassifier.Classify("$sSymbolNoOwner", owner: null);
        Assert.Equal(StripWithdrawalDisposition.FailClosed, result.Disposition);
        Assert.Null(result.Unit);
        Assert.Contains("no recorded wrapper owner", result.Reason);
    }

    [Fact]
    public void Classify_MetadataHelperOwner_FailsClosed()
    {
        // A type-metadata accessor is a TypeSurface unit — coarser than leaf/accessor-group, so the
        // wave-1 loop does not withdraw it; it needs graph closure.
        var result = StripToWithdrawalClassifier.Classify("$sSymbolMeta", MetadataHelper());
        Assert.Equal(StripWithdrawalDisposition.FailClosed, result.Disposition);
        Assert.Contains("graph closure", result.Reason);
    }

    [Fact]
    public void Classify_ModuleInitializerOwner_FailsClosed()
    {
        // The module-init unit is the coarsest scope — withdrawing it needs graph closure that the
        // iteration-0 conversion does not have.
        var result = StripToWithdrawalClassifier.Classify("$sSymbolModule", ModuleInitializer());
        Assert.Equal(StripWithdrawalDisposition.FailClosed, result.Disposition);
    }

    [Fact]
    public void Classify_UnmodelledKind_LeafScopedButNotDroppable_FailsClosed()
    {
        // A role/declaration pairing the classifier does not model resolves to the conservative
        // Unclassified sink: LeafApi scope (so it passes the wave-1 scope gate) but layout-contributing
        // (not droppable alone). This is the forward-safety backstop — without the droppable check a
        // future unmodelled artifact would be silently withdrawn instead of escalated. An out-of-range
        // BindingItemKind stands in for that not-yet-modelled kind.
        var unmodelled = ArtifactId.Create(
            DeclId.Create("Mod", "T", (BindingItemKind)0x7EE7, "mystery"), ArtifactRole.CSharpPublic);
        var result = StripToWithdrawalClassifier.Classify("$sSymbolUnmodelled", unmodelled);
        Assert.Equal(StripWithdrawalDisposition.FailClosed, result.Disposition);
        Assert.Equal(RecoveryScope.LeafApi, result.Unit!.Value.Scope);
        Assert.Contains("unclassified sink", result.Reason);
    }

    [Fact]
    public void Classify_PreservesTheSymbolItWasGiven()
    {
        Assert.Equal("$sSpecificSymbol", StripToWithdrawalClassifier.Classify("$sSpecificSymbol", Method()).Symbol);
        Assert.Equal("$sOtherSymbol", StripToWithdrawalClassifier.Classify("$sOtherSymbol", owner: null).Symbol);
    }
}
