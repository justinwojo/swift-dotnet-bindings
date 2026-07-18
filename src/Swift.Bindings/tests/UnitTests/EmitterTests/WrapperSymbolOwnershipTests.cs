// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Ownership attribution on <see cref="ModuleEmissionContext"/>'s wrapper-symbol registry: which
/// declaration a registered <c>@_cdecl</c> symbol belongs to.
/// </summary>
/// <remarks>
/// The registry decides which wrapper symbols exist; the owner map answers "whose is it?", which is
/// what lets a symbol-level diagnostic name a declaration instead of a bare C symbol. Attribution
/// is passed explicitly rather than inferred from an ambient scope: an un-threaded call site must
/// leave a symbol *unowned* (a missing answer) rather than attributing it to whatever happened to
/// be emitting at the time (a wrong answer).
/// </remarks>
public class WrapperSymbolOwnershipTests
{
    private static DeclId DeclFor(string methodName)
    {
        var module = TestModelFactory.CreateModuleDecl();
        return DeclIdFactory.ForMethod(TestModelFactory.CreateMethod(methodName, module));
    }

    [Fact]
    public void ClaimedMethodWrapper_IsAttributedToItsDeclaration()
    {
        // TryClaimWrapperSymbol is the path most ordinary method wrappers take, and it used to add
        // to the symbol set directly rather than through the registration funnel — so hooking only
        // the funnel would have left the common case unowned.
        var ctx = new ModuleEmissionContext();
        var decl = DeclFor("fetch");

        Assert.True(ctx.TryClaimWrapperSymbol("Loader", "fetch", "src", "SBW_Loader_fetch", decl));

        Assert.True(ctx.TryGetWrapperSymbolOwner("SBW_Loader_fetch", out var owner));
        Assert.Equal(decl, owner.Decl);
        Assert.Equal(ArtifactRole.SwiftWrapper, owner.Role);
    }

    [Fact]
    public void PerKindRegistration_IsAttributedToItsDeclaration()
    {
        var ctx = new ModuleEmissionContext();
        var decl = DeclFor("store");

        Assert.True(ctx.TryAddMethodWrapperSymbol("SBW_Loader_store", decl));

        Assert.True(ctx.TryGetWrapperSymbolOwner("SBW_Loader_store", out var owner));
        Assert.Equal(decl, owner.Decl);
        Assert.Equal(ArtifactRole.SwiftWrapper, owner.Role);
    }

    [Fact]
    public void SharedHelperRegistration_CarriesTheMetadataHelperRole()
    {
        var ctx = new ModuleEmissionContext();
        var decl = DeclFor("meta");

        Assert.True(ctx.TryAddMetadataWrapperSymbol("SBW_Loader_metadata", decl));

        Assert.True(ctx.TryGetWrapperSymbolOwner("SBW_Loader_metadata", out var owner));
        Assert.Equal(ArtifactRole.MetadataHelper, owner.Role);
    }

    [Fact]
    public void UnthreadedCallSite_RegistersTheSymbolButLeavesItUnowned()
    {
        // Missing attribution is the designed failure mode; the symbol still has to register or the
        // duplicate-symbol protection would regress.
        var ctx = new ModuleEmissionContext();

        Assert.True(ctx.TryAddMethodWrapperSymbol("SBW_anonymous"));

        Assert.Contains("SBW_anonymous", ctx.RegisteredWrapperSymbols);
        Assert.False(ctx.TryGetWrapperSymbolOwner("SBW_anonymous", out _));
    }

    [Fact]
    public void UnregisteredSymbol_HasNoOwner()
    {
        var ctx = new ModuleEmissionContext();

        Assert.False(ctx.TryGetWrapperSymbolOwner("SBW_never_seen", out _));
        Assert.False(ctx.TryGetWrapperSymbolOwner("", out _));
    }

    [Fact]
    public void LosingASymbolCollision_LeavesTheWinnersAttributionIntact()
    {
        // Only one of the two emissions actually reaches the linker, so the loser must not
        // overwrite the surviving artifact's claim.
        var ctx = new ModuleEmissionContext();
        var winner = DeclFor("first");
        var loser = DeclFor("second");

        Assert.True(ctx.TryAddMethodWrapperSymbol("SBW_contested", winner));
        Assert.False(ctx.TryAddPropertyWrapperSymbol("SBW_contested", loser));

        Assert.True(ctx.TryGetWrapperSymbolOwner("SBW_contested", out var owner));
        Assert.Equal(winner, owner.Decl);
    }

    [Fact]
    public void RolledBackClaim_DoesNotPolluteTheOwnerMap()
    {
        // TryClaimWrapperSymbol takes the structural slot first and rolls it back when the symbol
        // is already spoken for; the owner map must roll back with it.
        var ctx = new ModuleEmissionContext();
        var winner = DeclFor("first");
        var loser = DeclFor("second");

        Assert.True(ctx.TryAddMethodWrapperSymbol("SBW_contested", winner));
        Assert.False(ctx.TryClaimWrapperSymbol("Loader", "second", "src", "SBW_contested", loser));

        Assert.True(ctx.TryGetWrapperSymbolOwner("SBW_contested", out var owner));
        Assert.Equal(winner, owner.Decl);

        // The structural slot rolled back too, so a later claim on that slot with a fresh symbol
        // still wins.
        Assert.True(ctx.TryClaimWrapperSymbol("Loader", "second", "src", "SBW_second", loser));
        Assert.True(ctx.TryGetWrapperSymbolOwner("SBW_second", out var second));
        Assert.Equal(loser, second.Decl);
    }

    [Fact]
    public void DistinctDeclarations_GetDistinctArtifactIds()
    {
        var ctx = new ModuleEmissionContext();
        var fetch = DeclFor("fetch");
        var store = DeclFor("store");

        Assert.True(ctx.TryAddMethodWrapperSymbol("SBW_fetch", fetch));
        Assert.True(ctx.TryAddMethodWrapperSymbol("SBW_store", store));

        Assert.Equal(2, ctx.WrapperSymbolOwners.Count);
        Assert.NotEqual(ctx.WrapperSymbolOwners["SBW_fetch"], ctx.WrapperSymbolOwners["SBW_store"]);
    }

    [Fact]
    public void ClaimWithAnEmptySymbol_TakesTheStructuralSlotWithoutAnOwnerEntry()
    {
        // Some wrapper shapes have no symbol to register; the structural claim still has to work
        // and must not put an empty key in the owner map.
        var ctx = new ModuleEmissionContext();
        var decl = DeclFor("fetch");

        Assert.True(ctx.TryClaimWrapperSymbol("Loader", "fetch", "src", string.Empty, decl));

        Assert.Empty(ctx.WrapperSymbolOwners);
    }
}
