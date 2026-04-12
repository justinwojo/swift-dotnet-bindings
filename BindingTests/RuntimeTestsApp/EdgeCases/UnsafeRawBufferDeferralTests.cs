// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

#pragma warning disable SB0001 // CallConvSwift P/Invoke warning

namespace RuntimeTestsApp.EdgeCases;

/// <summary>
/// Runtime coverage for fix #11 (commit 26f764f1): the
/// <c>UnsafeRawBufferPointer</c> parameter deferral path. The fix has two
/// layers and this test is the second of them:
///
/// <list type="number">
///   <item>
///     <b>Build-side layer</b>: the Nuke target
///     <c>AssertBindingReportConstraints</c> in
///     build/Build.BindingTests.cs reads
///     <c>BindingTests/output/binding-report.json</c> and asserts that
///     <c>UnsafeRawBufferHolder.readBuffer</c> appears with
///     <c>Reason=UnsupportedSignature</c>. That assertion runs on the build
///     host before the iOS test launches and does NOT execute inside this
///     test process (the report is not bundled into the app's resources).
///   </item>
///   <item>
///     <b>Runtime layer (this test)</b>: instantiate
///     <see cref="UnsafeRawBufferHolder"/> and call the unrelated
///     <c>Multiplier</c> method. The point is NOT to exercise the deferred
///     method — it's to prove the enclosing type survived the deferral.
///     Fix #11 protects against an emitter failure mode where a single
///     unsupported parameter type propagates up and drops the entire type;
///     if that regresses, this test fails to compile because the
///     <see cref="UnsafeRawBufferHolder"/> class is missing.
///   </item>
/// </list>
///
/// <para>
/// Two layers catch either direction of regression: the build-side layer
/// catches "skip reason silently changed" and the runtime layer catches
/// "entire type was dropped". A one-layer test would let one of these slip.
/// </para>
/// </summary>
public class UnsafeRawBufferDeferralTests : TestBase
{
    public UnsafeRawBufferDeferralTests(TestResults results) : base(results) { }

    /// <summary>
    /// Call the unrelated <c>Multiplier</c> method on
    /// <see cref="UnsafeRawBufferHolder"/> and assert it round-trips. Proves
    /// the enclosing type is still reachable and usable even though one of
    /// its members was deferred.
    /// </summary>
    public void TestUnsafeRawBufferHolderMultiplierRoundTrip()
    {
        using var holder = new UnsafeRawBufferHolder(scale: 7);
        AssertEqual(7, holder.Scale,
            "UnsafeRawBufferHolder.Scale must expose the constructor-supplied value. " +
            "If this fails, fix #11 regressed and a constructor-level accessor was " +
            "dropped along with the deferred readBuffer method.");

        var result = holder.Multiplier(6);
        TestLogger.Info($"UnsafeRawBufferHolder(scale=7).Multiplier(6) = {result}");
        AssertEqual(42, result,
            "UnsafeRawBufferHolder.Multiplier(6) must return 42 (6 * 7). The value is " +
            "irrelevant in isolation — the assertion exists to prove the type survived " +
            "the deferral of readBuffer(UnsafeRawBufferPointer). If the emitter's " +
            "unsupported-signature path regresses to dropping the entire enclosing type, " +
            "this test stops compiling, which is the signal fix #11 is meant to surface.");
    }

    /// <summary>
    /// Reflectively confirm that <c>readBuffer</c> is absent from the
    /// generated binding. This mirrors the build-side assertion (which lives
    /// in the Nuke target) but from the runtime side: the deferred method
    /// must not leak into the compiled C# surface. If it appears, a
    /// soft-gate regression let it through into the binding report AND the
    /// emitted class.
    /// </summary>
    public void TestReadBufferIsAbsentFromGeneratedClass()
    {
        var holderType = typeof(UnsafeRawBufferHolder);
        var readBufferMethods = holderType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => string.Equals(m.Name, "ReadBuffer", System.StringComparison.Ordinal))
            .ToArray();
        TestLogger.Info(
            $"UnsafeRawBufferHolder.ReadBuffer method count = {readBufferMethods.Length}");
        AssertEqual(0, readBufferMethods.Length,
            "UnsafeRawBufferHolder must NOT expose a ReadBuffer method — fix #11 defers " +
            "it with Reason=UnsupportedSignature. If this assertion fails the emitter " +
            "regressed and started emitting a wrapper for an UnsafeRawBufferPointer " +
            "parameter. The build-side assertion in " +
            "build/Build.BindingTests.cs::AssertBindingReportConstraints is the " +
            "complementary half and would fail simultaneously.");
    }
}
