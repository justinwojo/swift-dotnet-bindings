// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The one coarse withdrawal the production driver authorizes: a module-local protocol's managed
/// conformance, named by the Swift wrapper compile. Everything else a round blames above the leaf
/// scopes stays fail-closed (SWIFTBIND111).
/// </summary>
public class ConformanceWithdrawalAuthorizationTests
{
    private const string Module = "Kitchen";

    private static RecoveryUnitId Unit(string module, BindingItemKind kind, string name, RecoveryScope scope) =>
        RecoveryUnitId.Create(DeclId.Create(module, declPath: null, kind, name), scope);

    private static RecoveryUnitId Conformance(string name, string module = Module) =>
        Unit(module, BindingItemKind.Type, name, RecoveryScope.ManagedProtocolConformance);

    private static EmitterFaultOrigin SwiftWrapper(RecoveryUnitId _) => EmitterFaultOrigin.RecoveryWithdrawal;

    [Fact]
    public void ModuleLocalConformancesFromTheSwiftWrapper_Authorized()
    {
        var blocking = new[] { Conformance("KitchenConfiguration"), Conformance("OtherConfiguration") };

        Assert.True(InEmissionDriver.AuthorizesConformanceWithdrawal(blocking, Module, SwiftWrapper));
    }

    [Fact]
    public void EmptyRound_NotAuthorized()
    {
        Assert.False(InEmissionDriver.AuthorizesConformanceWithdrawal(
            Array.Empty<RecoveryUnitId>(), Module, SwiftWrapper));
    }

    [Theory]
    [InlineData(RecoveryScope.TypeSurface)]
    [InlineData(RecoveryScope.ForwardProtocolView)]
    [InlineData(RecoveryScope.ConformanceEdge)]
    [InlineData(RecoveryScope.SharedHelperBundle)]
    [InlineData(RecoveryScope.TypeRepresentation)]
    [InlineData(RecoveryScope.Module)]
    public void AnyOtherCoarseScopeInTheRound_NotAuthorized(RecoveryScope scope)
    {
        var blocking = new List<RecoveryUnitId>
        {
            Conformance("KitchenConfiguration"),
            Unit(Module, scope == RecoveryScope.Module ? BindingItemKind.Module : BindingItemKind.Type,
                scope == RecoveryScope.Module ? Module : "KitchenBox", scope),
        };

        Assert.False(InEmissionDriver.AuthorizesConformanceWithdrawal(blocking, Module, SwiftWrapper));
    }

    [Fact]
    public void ConformanceOfAnotherModule_NotAuthorized()
    {
        var blocking = new[] { Conformance("DepConfiguration", module: "DepModule") };

        Assert.False(InEmissionDriver.AuthorizesConformanceWithdrawal(blocking, Module, SwiftWrapper));
    }

    // EmitterFaultOrigin is internal, so the planes are enumerated here instead of as theory data.
    [Fact]
    public void ConformanceBlamedByAnotherPlane_NotAuthorized()
    {
        var blocking = new[] { Conformance("KitchenConfiguration") };

        foreach (var origin in new[]
                 {
                     EmitterFaultOrigin.EmitterException,
                     EmitterFaultOrigin.CSharpRecoveryWithdrawal,
                     EmitterFaultOrigin.AbiRecoveryWithdrawal,
                     EmitterFaultOrigin.BisectionIsolatedWithdrawal,
                 })
        {
            Assert.False(InEmissionDriver.AuthorizesConformanceWithdrawal(blocking, Module, _ => origin));
        }
    }
}
