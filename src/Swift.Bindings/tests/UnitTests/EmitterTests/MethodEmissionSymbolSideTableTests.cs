// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Linq;
using System.Reflection;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// AF13 (Finding 13): emission-time symbol promotion lives on an emission-scoped side table
/// (<see cref="MethodEnvironment.EmissionSymbol"/> plus the <see cref="ModuleEmissionContext"/>
/// per-method map) instead of mutating <see cref="MethodDecl.MangledName"/> in place. These tests
/// pin the side table's read-funnel, the save/restore round-trip, the no-mutation invariant, the
/// reference-identity keying, and the init-only immutability of the parser ABI fact.
/// </summary>
public class MethodEmissionSymbolSideTableTests
{
    private const string Silgen = "$s10TestModule3fooyyF";

    private static MethodEnvironment NewEnv(MethodDecl method) =>
        new(method, new TypeDatabase());

    private static MethodDecl Method(string name = "foo", string? mangled = Silgen)
    {
        var module = TestModelFactory.CreateModuleDecl("TestModule");
        return TestModelFactory.CreateMethod(name, parent: module, mangledName: mangled);
    }

    // ==================== MethodEnvironment.EmissionSymbol / PromoteSymbol ====================

    [Fact]
    public void EmissionSymbol_DefaultsToDeclMangledName()
    {
        var env = NewEnv(Method());
        Assert.Equal(Silgen, env.EmissionSymbol);
    }

    [Fact]
    public void PromoteSymbol_SetsEmissionSymbol_AndReturnsPreviousValue()
    {
        var env = NewEnv(Method());

        var previous = env.PromoteSymbol("SBW_TestModule_foo_promoted");

        Assert.Equal(Silgen, previous);
        Assert.Equal("SBW_TestModule_foo_promoted", env.EmissionSymbol);
    }

    [Fact]
    public void PromoteSymbol_DoesNotMutateDeclMangledName()
    {
        // The core AF13 invariant: promotion is emission-scoped state, never an in-place
        // rewrite of the parser's silgen symbol on the shared decl.
        var method = Method();
        var env = NewEnv(method);

        env.PromoteSymbol("SBW_TestModule_foo_promoted");

        Assert.Equal(Silgen, method.MangledName);
    }

    [Fact]
    public void PromoteSymbol_RoundTrip_RestoresViaReturnedPrevious()
    {
        // The former "save MangledName, mutate, restore on fallback" protocol becomes a local
        // value round-trip with no shared-decl mutation: promote optimistically, then restore.
        var env = NewEnv(Method());

        var saved = env.PromoteSymbol("SBW_optimistic");
        Assert.Equal("SBW_optimistic", env.EmissionSymbol);

        env.PromoteSymbol(saved);
        Assert.Equal(Silgen, env.EmissionSymbol);
    }

    [Fact]
    public void MangledName_IsInitOnly()
    {
        // Guards the AF13 immutability flip: reverting MangledName to a settable property would
        // re-open the in-place mutation channel this refactor closed. An init-only property's
        // compiler-generated setter carries the IsExternalInit required modifier.
        var setter = typeof(MethodDecl).GetProperty(nameof(MethodDecl.MangledName))!.SetMethod;
        Assert.NotNull(setter);
        var isInitOnly = setter!.ReturnParameter
            .GetRequiredCustomModifiers()
            .Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");
        Assert.True(isInitOnly,
            "MethodDecl.MangledName must stay init-only (AF13): emission must not mutate the parser ABI fact.");
    }

    // ==================== ModuleEmissionContext per-method side table ====================

    [Fact]
    public void RecordThenGet_ReturnsRecordedEmissionSymbol()
    {
        var ctx = new ModuleEmissionContext();
        var method = Method();

        ctx.RecordMethodEmissionSymbol(method, "SBW_TestModule_foo_promoted");

        Assert.Equal("SBW_TestModule_foo_promoted", ctx.GetMethodEmissionSymbolOrMangled(method));
    }

    [Fact]
    public void Get_Unrecorded_FallsBackToDeclMangledName()
    {
        var ctx = new ModuleEmissionContext();
        var method = Method(mangled: "$s10TestModule3baryyF");

        // Never recorded — a consumer that historically read an un-promoted decl's MangledName
        // must still see exactly that silgen symbol, preserving byte-stable output.
        Assert.Equal("$s10TestModule3baryyF", ctx.GetMethodEmissionSymbolOrMangled(method));
    }

    [Fact]
    public void SideTable_KeyedByReferenceIdentity_ValueEqualCloneDoesNotCollide()
    {
        // MethodDecl is a record (value equality). The side table must key on the exact decl
        // instance the base handler emitted, or a `with`-synthesized / value-equal clone would
        // wrongly resolve to a sibling's promoted symbol.
        var ctx = new ModuleEmissionContext();
        var original = Method(mangled: "$s10TestModule3bazyyF");
        var clone = original with { }; // value-equal, distinct reference

        Assert.Equal(original, clone);                 // value equality holds
        Assert.False(ReferenceEquals(original, clone)); // but distinct instances

        ctx.RecordMethodEmissionSymbol(original, "SBW_promoted_original");

        // The clone was never recorded → falls back to its own MangledName, NOT the original's
        // recorded symbol (which a value-keyed dictionary would have returned).
        Assert.Equal("$s10TestModule3bazyyF", ctx.GetMethodEmissionSymbolOrMangled(clone));
    }

    [Fact]
    public void PromotedEnvSymbol_RecordedAndRecovered_EndToEnd()
    {
        // The full path: a method's own emission promotes its env symbol; the base handler records
        // env.EmissionSymbol; a later env-less consumer (e.g. the concrete-protocol specialization
        // emitter) recovers exactly that promoted value from the side table.
        var ctx = new ModuleEmissionContext();
        var method = Method();
        var env = NewEnv(method);

        env.PromoteSymbol("SBW_TestModule_foo_ctorwrapper");
        ctx.RecordMethodEmissionSymbol(method, env.EmissionSymbol);

        Assert.Equal("SBW_TestModule_foo_ctorwrapper", ctx.GetMethodEmissionSymbolOrMangled(method));
        // The decl itself stays on its silgen symbol throughout.
        Assert.Equal(Silgen, method.MangledName);
    }
}
