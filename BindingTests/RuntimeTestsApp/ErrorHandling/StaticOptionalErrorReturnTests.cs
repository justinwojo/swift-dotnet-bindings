// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.Foundation;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.ErrorHandling;

/// <summary>
/// Regression for the PInvoke-vs-body sret-shape drift bug
/// (uninitialized sret buffer for Optional&lt;any Error&gt; return).
///
/// Shape: a direct-CallConvSwift static method returning <c>Optional&lt;any Error&gt;</c>
/// — address-only on every Apple ABI Swift currently supports, so Swift's
/// caller hands an <c>@out</c> sret buffer in the hidden <c>x8</c> register
/// and the function writes the optional's tag + payload there.
///
/// Pre-fix, the generator emitted a wrapper that allocated <c>_cdeclBuf</c>
/// + constructed <c>swiftIndirectResult</c> but the matching PInvoke
/// signature returned <c>IntPtr</c> with no sret slot. The .NET caller dropped
/// the returned IntPtr and unmarshalled the never-written buffer — fabricating
/// an <c>AnyError</c> over uninitialized memory (almost always non-None,
/// SIGSEGV on first access).
///
/// These assertions pin three round-trip properties of the post-fix shape:
///   1. None case round-trips to <c>null</c> — the sret buffer's discriminator
///      byte is read correctly.
///   2. Some(MathError) round-trips with its payload intact — the existential
///      container's witness-table dispatch survives the indirect-result
///      extraction.
///   3. Some(NSError) round-trips through the ObjC bridge — the inner
///      reference is not freed early.
/// </summary>
public class StaticOptionalErrorReturnTests : TestBase
{
    public StaticOptionalErrorReturnTests(TestResults results) : base(results) { }

    /// <summary>
    /// None branch: <c>(any Error)?.none</c> must round-trip to a C# null.
    /// Pre-fix this read whatever bits happened to live in the freshly-allocated
    /// native buffer — almost never None — and constructed a bogus AnyError.
    /// </summary>
    public void TestStaticOptionalErrorReturn_None()
    {
        using var result = StaticOptionalErrorReturn.ValidateNone();
        AssertTrue(result is null, "Expected null AnyError for validateNone()");
    }

    /// <summary>
    /// Some(MathError) branch: payload survives the sret round-trip and
    /// LocalizedDescription contains the case name.
    /// </summary>
    public void TestStaticOptionalErrorReturn_MathError()
    {
        using var result = StaticOptionalErrorReturn.ValidateMathError();
        AssertTrue(result != null, "Expected non-null AnyError for validateMathError()");

        var desc = result!.LocalizedDescription;
        TestLogger.Info($"validateMathError -> AnyError.LocalizedDescription = \"{desc}\"");
        AssertTrue(desc.Contains("divisionByZero"),
            $"Expected 'divisionByZero' in description, got: \"{desc}\"");
    }

    /// <summary>
    /// Some(ValidationError.tooLong(maxLength: 7)) branch: enum-with-associated-value
    /// payload survives the sret round-trip. Exercises witness-table dispatch
    /// inside the inner existential container.
    /// </summary>
    public void TestStaticOptionalErrorReturn_ValidationError()
    {
        using var result = StaticOptionalErrorReturn.ValidateValidationError();
        AssertTrue(result != null, "Expected non-null AnyError for validateValidationError()");

        var desc = result!.LocalizedDescription;
        TestLogger.Info($"validateValidationError -> AnyError.LocalizedDescription = \"{desc}\"");
        AssertTrue(desc.Contains("tooLong"),
            $"Expected 'tooLong' in description, got: \"{desc}\"");
    }

    /// <summary>
    /// Some(NSError) branch: ObjC-bridged error existential round-trips through
    /// the sret buffer with the inner reference still alive.
    /// </summary>
    public void TestStaticOptionalErrorReturn_NSError()
    {
        using var result = StaticOptionalErrorReturn.ValidateNSError();
        AssertTrue(result != null, "Expected non-null AnyError for validateNSError()");

        var desc = result!.LocalizedDescription;
        TestLogger.Info($"validateNSError -> AnyError.LocalizedDescription = \"{desc}\"");
        AssertTrue(
            desc.Contains("Static validation failure") || desc.Contains("StaticValidate"),
            $"Expected NSError description content, got: \"{desc}\"");
    }
}
