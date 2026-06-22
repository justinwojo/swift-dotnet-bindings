// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// End-to-end ABI gate for the value-layout oracle's Optional sizing inside a <c>@frozen</c> struct —
/// the two opposite outcomes the oracle decides per field:
///
/// <para>
/// <b>Width path</b> (<see cref="FrozenScalarOptionalPair"/>): fields are ONLY tag-adding scalar
/// Optionals (<c>Int32?</c>, <c>Float?</c>), whose payloads have no spare bits, so each adds a
/// discriminator tag byte AFTER its payload. The oracle must size that appended tag and place the
/// second field at the correct offset; a wrong width silently corrupts a round-tripped field. The
/// generated getter reads the <c>Int32?</c> tag at byte offset 4 (just past the 4-byte payload), so a
/// mis-sized layout surfaces as a changed value, not just a crash.
/// </para>
///
/// <para>
/// <b>Decline path</b> (<see cref="FrozenOptionalBoolHolder"/>): an <c>Optional&lt;Bool&gt;</c> reuses
/// Bool's spare bit, which the oracle declines to size inline; one declining field forces the WHOLE
/// struct to the indirect <c>@_cdecl</c> (whole-struct-by-pointer) path. This asserts the decline
/// routes to a correct indirect round-trip rather than fabricating a bad inline width — the
/// accompanying non-optional <c>marker</c> rides the indirect buffer and confirms no corruption.
/// </para>
///
/// Both shapes project to the <c>ClassWithBufferStruct</c> path; the fence is on the <c>Buffer</c>
/// inner-struct layout the oracle computes. Gated on simulator (Mono JIT) and device (NativeAOT), whose
/// struct register-classification differs.
/// </summary>
public class FrozenOptionalAbiWidthTests : TestBase
{
    public FrozenOptionalAbiWidthTests(TestResults results) : base(results) { }

    /// <summary>
    /// Width fence: a frozen struct of only tag-adding scalar Optionals round-trips every Some/None
    /// combination by value. Each combination drives the appended Optional tag both set and clear, so a
    /// wrong tag offset or struct width corrupts <c>First</c> or <c>Second</c>.
    /// </summary>
    public void TestFrozenScalarOptionalPairRoundTripsAllInhabitants()
    {
        AssertScalarPairRoundTrips(7, 3.5f);
        AssertScalarPairRoundTrips(int.MinValue, float.MaxValue);
        AssertScalarPairRoundTrips(int.MaxValue, float.MinValue);
        AssertScalarPairRoundTrips(null, 1.25f);
        AssertScalarPairRoundTrips(42, null);
        AssertScalarPairRoundTrips(null, null);
        TestLogger.Info("FrozenScalarOptionalPair: all 6 Some/None inhabitants round-tripped with intact fields");
    }

    private void AssertScalarPairRoundTrips(int? first, float? second)
    {
        using var input = new FrozenScalarOptionalPair(first, second);
        using var output = TestLibFunctions.RoundTripFrozenScalarOptionalPair(input);
        AssertEqual(first, output.First, $"FrozenScalarOptionalPair.First ({first?.ToString() ?? "nil"}, {second?.ToString() ?? "nil"})");
        AssertEqual(second, output.Second, $"FrozenScalarOptionalPair.Second ({first?.ToString() ?? "nil"}, {second?.ToString() ?? "nil"})");
    }

    /// <summary>
    /// Decline fence: a frozen struct with an <c>Optional&lt;Bool&gt;</c> field round-trips by value
    /// through the indirect path. Asserts the spare-bit Bool flag (true / false / nil) and the
    /// non-optional marker both survive the whole-struct-by-pointer round-trip.
    /// </summary>
    public void TestFrozenOptionalBoolHolderRoundTripsViaIndirectPath()
    {
        AssertBoolHolderRoundTrips(true, 11);
        AssertBoolHolderRoundTrips(false, -22);
        AssertBoolHolderRoundTrips(null, 33);
        TestLogger.Info("FrozenOptionalBoolHolder: true/false/nil + marker round-tripped via @_cdecl indirect path");
    }

    private void AssertBoolHolderRoundTrips(bool? flag, int marker)
    {
        using var input = new FrozenOptionalBoolHolder(flag, marker);
        using var output = TestLibFunctions.RoundTripFrozenOptionalBoolHolder(input);
        AssertEqual(flag, output.Flag, $"FrozenOptionalBoolHolder.Flag ({flag?.ToString() ?? "nil"})");
        AssertEqual(marker, output.Marker, $"FrozenOptionalBoolHolder.Marker ({flag?.ToString() ?? "nil"})");
    }
}
