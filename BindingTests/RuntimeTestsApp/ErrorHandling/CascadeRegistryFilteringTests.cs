// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.ErrorHandling;

/// <summary>
/// Regression tests for the cascade-dispatcher registry filter
/// (<c>ErrorEnumRegistryEmitter.IsRegisterable</c>). The filter exists to keep the
/// per-module Swift→C# error cascade from referencing types the C# side never
/// emitted, or types the wrapper module's plain <c>import</c> cannot name. Three
/// shapes exercised here:
/// <list type="bullet">
///   <item><c>@_spi</c> declarations (StripeCore-style — StripeError, TimeoutError) — C# emitter skips them, registry must too.</item>
///   <item>Error types nested inside open-generic parents (Alamofire / RealityFoundation —
///   <c>DecodableWebSocketMessageDecoder&lt;TValue&gt;.Error</c>,
///   <c>FromToByAction&lt;TValue&gt;.DecodingErrors</c>) — registry must skip to avoid CS0305.</item>
///   <item><c>@usableFromInline internal</c> types (CryptoSwift <c>StreamDecryptor</c>-style)
///   — C# emitter DOES bind them (they appear in <c>@inlinable</c> signatures), but the
///   wrapper module's plain <c>import</c> only sees <c>public</c>, so a cascade arm
///   <c>as? Module.InternalType</c> fails to compile in the wrapper. Registry must skip.</item>
/// </list>
///
/// The compile-time arm of these regression checks is implicit: this file's mere
/// existence — together with the fixtures in <c>CascadeRegistryFiltering.swift</c>
/// — gates the build via <c>nuke binding-tests --compile-only</c>. The runtime arms
/// confirm filtered types fall through to the untyped <c>SwiftException</c> branch
/// (id 0) instead of matching a registry entry.
/// </summary>
public class CascadeRegistryFilteringTests : TestBase
{
    public CascadeRegistryFilteringTests(TestResults results) : base(results) { }

    public async Task TestSpiErrorFallsThroughToUntypedSwiftException()
    {
        // SpiOnlyCascadeError is annotated `@_spi(InternalCascade)`, so the C#
        // emitter skips it (HandleBaseDecl @_spi gate). The cascade-registry
        // filter must skip it too — otherwise generation emits a `case N: { }`
        // arm referencing a missing C# type. Throwing the SPI error from a public
        // throwing function therefore lands on the cascade's id-0 fallthrough,
        // and C# observes a bare `SwiftException`, not a `SwiftException<TError>`.
        // The bare-type assertion (`.GetType() == typeof(SwiftException)`) is what
        // distinguishes correct fallthrough from a "we accidentally registered a
        // type whose C# binding was never emitted" outcome — that path would
        // throw a different runtime error (typically a marshal failure surfaced
        // as `SwiftException` with a "typed marshal failed" suffix).
        try
        {
            await WithTimeout(
                TestLibFunctions.PlainThrowsAsyncSpiCascadeFallthroughAsync(),
                DefaultAsyncTimeout);
            throw new AssertionException(
                "PlainThrowsAsyncSpiCascadeFallthroughAsync should have thrown");
        }
        catch (SwiftException ex) when (ex.GetType() == typeof(SwiftException))
        {
            AssertTrue(
                ex.Message.Contains("unauthorized")
                    || ex.Message.Contains("SpiOnlyCascadeError"),
                $"Untyped fallback should preserve Swift error description, got: {ex.Message}");
            TestLogger.Info(
                $"SPI error fell through to bare SwiftException; Message={ex.Message}");
        }
    }

    public async Task TestInlinableInternalErrorFallsThroughToUntypedSwiftException()
    {
        // InlinableInternalCascadeError is `@usableFromInline internal`. The C#
        // emitter binds it (since @usableFromInline types appear in @inlinable
        // signatures the binding must reach), but the cascade dispatcher's
        // wrapper-module `import {Module}` resolves only `public` declarations.
        // Without the IsModuleInternal arm of the registry filter, the cascade
        // would emit `as? SwiftBindingsTestLib.InlinableInternalCascadeError` and
        // the wrapper swift compile would fail (the CryptoSwift StreamDecryptor
        // shape). With the filter, the throw lands on id-0 fallthrough and C#
        // observes a bare `SwiftException`.
        try
        {
            await WithTimeout(
                TestLibFunctions.PlainThrowsAsyncInlinableInternalCascadeFallthroughAsync(),
                DefaultAsyncTimeout);
            throw new AssertionException(
                "PlainThrowsAsyncInlinableInternalCascadeFallthroughAsync should have thrown");
        }
        catch (SwiftException ex) when (ex.GetType() == typeof(SwiftException))
        {
            AssertTrue(
                ex.Message.Contains("unauthorized")
                    || ex.Message.Contains("InlinableInternalCascadeError"),
                $"Untyped fallback should preserve Swift error description, got: {ex.Message}");
            TestLogger.Info(
                $"@usableFromInline internal error fell through to bare SwiftException; Message={ex.Message}");
        }
    }
}
