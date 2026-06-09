// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Structural "exactly one live emitter per family" tests. Each emission responsibility must have
/// a single owning emitter. These guard against re-introducing dead duplicates, and pin the resolved
/// partition so a future refactor can't silently re-split a family across two emitters (one of which
/// would then be dead code that patches land in).
///
/// Live/dead map:
///   • Async C# callback plumbing (TCS / GCHandle / UnmanagedCallersOnly callbacks)
///       LIVE  → AsyncHarnessEmitter.EmitAsyncWrapper (+ EmitAsyncWrapperFor* family)
///       DEAD  → duplicate copy deleted from WrapperEmitter.Async.cs
///   • Async Swift @_cdecl/@_silgen_name wrapper body
///       LIVE  → WrapperEmitter.EmitAsync → BuildSwiftAsyncWrapperCode / BuildSwiftCatchBody
///       DEAD  → duplicate copies deleted from AsyncHarnessEmitter.cs
///   • Method-generic bridge: sync (MethodGenericBridgeAdapter, conditionally live) and async
///       (AsyncMethodGenericBridgeAdapter) are distinct siblings, each registered exactly once.
/// </summary>
public class EmitterFamilyLivenessTests
{
    private static readonly Assembly GeneratorAssembly = typeof(MethodHandler).Assembly;

    private const BindingFlags AnyDeclared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
        BindingFlags.Static | BindingFlags.DeclaredOnly;

    // This test deliberately reflects over the generator's own assembly to assert a structural
    // invariant. The trim/AOT analyzer (IsAotCompatible) can't see that the reflected members are
    // the very emitters under test, so its warnings are not actionable here — suppress them locally.
#pragma warning disable IL2026 // Assembly.GetTypes() has RequiresUnreferencedCode
#pragma warning disable IL2070 // GetMethods() on a Type without DynamicallyAccessedMembers

    /// <summary>
    /// All loadable types in the generator assembly. Tolerates a partial load failure so an
    /// unrelated missing dependency can't turn this into a flaky test.
    /// </summary>
    private static IEnumerable<Type> AllGeneratorTypes()
    {
        try
        {
            return GeneratorAssembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t != null)!;
        }
    }

    /// <summary>Types that declare (DeclaredOnly) an instance/static method with the given name.</summary>
    private static List<Type> TypesDeclaringMethod(string methodName) =>
        AllGeneratorTypes()
            .Where(t => t.GetMethods(AnyDeclared).Any(m => m.Name == methodName))
            .ToList();

#pragma warning restore IL2070
#pragma warning restore IL2026

    // ---------------------------------------------------------------------
    // Async wrapper family — C# callback plumbing
    // ---------------------------------------------------------------------

    [Fact]
    public void CSharpAsyncPlumbing_HasExactlyOneOwner_AsyncHarnessEmitter()
    {
        var owners = TypesDeclaringMethod("EmitAsyncWrapper");
        var owner = Assert.Single(owners);
        Assert.Equal(typeof(AsyncHarnessEmitter), owner);
    }

    [Fact]
    public void WrapperEmitter_DoesNotReintroduce_CSharpAsyncPlumbingDuplicate()
    {
        // The dead C# duplicate (EmitAsyncWrapper + EmitAsyncWrapperFor*) was deleted from
        // WrapperEmitter.Async.cs. If a refactor re-grows it here, this names the regressed file.
        var emitAsyncWrapperMethods = typeof(WrapperEmitter)
            .GetMethods(AnyDeclared)
            .Where(m => m.Name.StartsWith("EmitAsyncWrapper", StringComparison.Ordinal))
            .Select(m => m.Name)
            .ToList();
        Assert.Empty(emitAsyncWrapperMethods);
    }

    // ---------------------------------------------------------------------
    // Async wrapper family — Swift @_cdecl wrapper body
    // ---------------------------------------------------------------------

    [Fact]
    public void SwiftAsyncBody_HasExactlyOneOwner_WrapperEmitter()
    {
        // BuildSwiftAsyncWrapperCode and BuildSwiftCatchBody are the distinctive Swift-emission
        // helpers; each must be declared by exactly one type — WrapperEmitter.
        foreach (var methodName in new[] { "BuildSwiftAsyncWrapperCode", "BuildSwiftCatchBody" })
        {
            var owners = TypesDeclaringMethod(methodName);
            var owner = Assert.Single(owners);
            Assert.Equal(typeof(WrapperEmitter), owner);
        }
    }

    [Fact]
    public void AsyncHarnessEmitter_DoesNotReintroduce_SwiftEmissionDuplicate()
    {
        // The dead Swift duplicate (BuildSwift*) was deleted from AsyncHarnessEmitter.cs. The class
        // emits ONLY C# plumbing now; it must own no Swift-emission method.
        var buildSwiftMethods = typeof(AsyncHarnessEmitter)
            .GetMethods(AnyDeclared)
            .Where(m => m.Name.StartsWith("BuildSwift", StringComparison.Ordinal))
            .Select(m => m.Name)
            .ToList();
        Assert.Empty(buildSwiftMethods);
    }

    // ---------------------------------------------------------------------
    // Method-generic bridge family — dispatch table partition
    // ---------------------------------------------------------------------

    [Fact]
    public void BridgeDispatchTable_RegistersEachAdapterExactlyOnce()
    {
        var duplicateAdapters = MethodHandler.BridgeEmitters
            .GroupBy(a => a.GetType())
            .Where(g => g.Count() > 1)
            .Select(g => g.Key.Name)
            .ToList();
        Assert.Empty(duplicateAdapters);
    }

    [Fact]
    public void MethodGenericBridge_SyncAndAsync_AreBothWiredAsDistinctSiblings()
    {
        // Both siblings are live-and-wired exactly once. The sync MethodGenericBridge is
        // conditionally live (reachable for a sync method-own class-bound generic on a non-generic
        // parent in XCFramework mode) — it is the sync counterpart to the async adapter, NOT a dead
        // duplicate.
        Assert.Single(MethodHandler.BridgeEmitters, a => a is MethodGenericBridgeAdapter);
        Assert.Single(MethodHandler.BridgeEmitters, a => a is AsyncMethodGenericBridgeAdapter);
    }
}
