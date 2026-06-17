// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using BindingsGeneration.Demangling;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Pins <see cref="Swift5Demangler.HasAsyncMarker(string)"/>, the raw-tree async detector that
/// (Finding 17) replaced <c>SwiftABIParser.DetectAsyncFromMangledName</c>'s <c>"Ya"</c> substring
/// scan. The parser uses it as the fallback when the demangler produces no
/// <see cref="FunctionReduction"/> — notably for <c>Constructor</c>/accessor symbols, which the
/// reducer intentionally does not reduce, so an <c>async</c> initializer would otherwise be missed.
///
/// Because it walks the parsed mangling grammar (locating an <see cref="NodeKind.AsyncAnnotation"/>
/// node) rather than scanning text, it cannot false-positive on an identifier that incidentally
/// contains <c>"Ya"</c> (<c>Yak</c>, <c>Yacht</c>, …) — the case the old scan had to special-case
/// with a digit-prefix guard.
///
/// The positive/negative symbols are real mangled names from BindingTests/SwiftBindingsTestLib.
/// </summary>
public class AsyncMarkerTests
{
    [Theory]
    // Real async symbols: async void method, async method returning Int32, and — the case that
    // motivates the raw-tree walk — an async throws CONSTRUCTOR (AsyncService.init(key:) async
    // throws). The reducer has no rule for Constructor nodes, so HasAsyncMarker is the only detector.
    [InlineData("$s20SwiftBindingsTestLib11AsyncWorkerV15asyncVoidMethodyyYaF", true)]
    [InlineData("$s20SwiftBindingsTestLib11AsyncWorkerV17asyncReturnMethods5Int32VyYaF", true)]
    [InlineData("$s20SwiftBindingsTestLib12AsyncServiceC3keyACSS_tYaKcfc", true)]
    // Real sync symbols: sync void method, property getter, parameterless constructor.
    [InlineData("$s20SwiftBindingsTestLib11SharedStateC16incrementCounteryyF", false)]
    [InlineData("$s20SwiftBindingsTestLib007Caf_dmaV4nameSSvg", false)]
    [InlineData("$s20SwiftBindingsTestLib010URLRequestC6HelperCACycfc", false)]
    public void HasAsyncMarker_RealPatterns(string mangledName, bool expected)
    {
        var demangler = new Swift5Demangler();
        Assert.Equal(expected, demangler.HasAsyncMarker(mangledName));
    }

    [Theory]
    // Identifiers whose names embed "Ya" (Yak/Yacht). The grammar walk reads these as Identifier
    // text, never an AsyncAnnotation node, so they are correctly not-async — the substring scan
    // needed an explicit digit-prefix guard to reach the same answer.
    [InlineData("$s10TestModule3YakC4nameSSvg")]
    [InlineData("$s10TestModule5YachtC4nameSSvg")]
    public void HasAsyncMarker_NameContainsYa_NotAsync(string mangledName)
    {
        var demangler = new Swift5Demangler();
        Assert.False(demangler.HasAsyncMarker(mangledName),
            "an identifier containing 'Ya' must not be read as async by the grammar walk");
    }

    [Theory]
    // Opaque-return async methods — `async -> some Protocol` (the AppIntents
    // `perform() async throws -> some IntentResult` shape). The return type mangles with the `Qr`
    // opaque-return-type sigil, which the reducer cannot reduce, so async detection falls through to
    // HasAsyncMarker. Before the `Qr` archetype repair, DemangleArchetype returned null for `Qr`,
    // corrupting the function-type parse so the AsyncAnnotation never surfaced and these wrappers
    // were emitted non-async over an async body — a Swift compile failure. These are real symbols
    // from BindingTests/SwiftBindingsTestLib's AsyncOpaqueWorker.
    [InlineData("$s20SwiftBindingsTestLib17AsyncOpaqueWorkerV04makefE04textQrSS_tYaF", true)]            // makeOpaqueAsync
    [InlineData("$s20SwiftBindingsTestLib17AsyncOpaqueWorkerV04makefE8Throwing4textQrSS_tYaKF", true)]   // makeOpaqueAsyncThrowing
    [InlineData("$s20SwiftBindingsTestLib17AsyncOpaqueWorkerV021makeTrackedRenderableE03tagQrs5Int32V_tYaF", true)]          // makeTrackedRenderableAsync
    [InlineData("$s20SwiftBindingsTestLib17AsyncOpaqueWorkerV021makeTrackedRenderableE8Throwing3tagQrs5Int32V_tYaKF", true)] // makeTrackedRenderableAsyncThrowing
    // Sync opaque-return throwing method (`throws -> some Protocol`, no `Ya`) — same `Qr` return,
    // must stay not-async so the repaired archetype does not over-detect.
    [InlineData("$s20SwiftBindingsTestLib17AsyncOpaqueWorkerV04makeF8Throwing4textQrSS_tKF", false)]     // makeOpaqueThrowing
    public void HasAsyncMarker_OpaqueReturn(string mangledName, bool expected)
    {
        var demangler = new Swift5Demangler();
        Assert.Equal(expected, demangler.HasAsyncMarker(mangledName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a mangled name")]
    public void HasAsyncMarker_Malformed_ReturnsFalse(string mangledName)
    {
        var demangler = new Swift5Demangler();
        Assert.False(demangler.HasAsyncMarker(mangledName));
    }
}
