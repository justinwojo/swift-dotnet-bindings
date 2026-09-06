// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// End-to-end ABI gate for the inline SIZE and ALIGNMENT a <c>@frozen</c> struct's Buffer mirror
/// reserves for a stored field whose own type is another reference-bearing frozen struct.
///
/// <para>
/// Such a field is not one pointer wide — <see cref="NestedLayoutRefLeaf"/> holds a single
/// <c>String</c> and is 16 bytes inline. A mirror that reserved one 8-byte word for it is short by
/// half a field, and the Buffer is exactly what gets <c>NativeMemory.Alloc</c>'d and handed to Swift,
/// so every copy in or out wrote past the end of the allocation over a live refcounted reference.
/// </para>
///
/// <para>
/// The host mixes the three shapes whose widths a mirror has to get right — the nested reference
/// struct, the same nested struct under <c>Optional</c> (spare-inhabitant payload, so exactly as wide
/// as the payload with no appended tag), and a trivial nested struct that is 8 bytes at 4-byte
/// alignment. They are in one struct on purpose: a wrong width on any of them shifts every field
/// after it, and <c>SentinelValue</c> — the highest-offset field — is the neighbour probe that reads
/// back wrong when that happens. Swift measures the host at 44 bytes (fields at 0, 16, 32, 40).
/// </para>
///
/// <para>
/// Construction goes through the Swift static factories rather than an initializer: a Swift
/// <c>init</c> takes its parameters <c>@owned</c> while the direct-P/Invoke path hands them over
/// borrowed, and these tests are meant to fence Buffer layout rather than parameter ownership. The
/// labels are longer than 15 UTF-8 bytes so the <c>String</c> really carries a heap reference — the
/// case where a short mirror corrupts a refcounted object rather than only scratch bytes.
/// </para>
///
/// Gated on simulator (Mono JIT) and device (NativeAOT), whose allocators react differently to a
/// short buffer.
/// </summary>
public class FrozenNestedFieldLayoutTests : TestBase
{
    private const string Label = "nested-frozen-field-layout-probe";

    public FrozenNestedFieldLayoutTests(TestResults results) : base(results) { }

    /// <summary>
    /// Reads every field of a freshly constructed host. With the populated Optional present, all four
    /// fields occupy their real widths, so an under-reserved mirror puts the trailing fields at the
    /// wrong offsets — the trivial pair and the sentinel read back wrong (or the process dies).
    /// </summary>
    public void TestNestedFieldLayoutHostReadsEveryFieldAtItsRealOffset()
    {
        using var host = NestedFieldLayoutHost.Make(Label, true, 111, 222, 3939);

        AssertEqual(Label, host.LeadingText, "NestedFieldLayoutHost.LeadingText");
        AssertEqual(true, host.HasOptional, "NestedFieldLayoutHost.HasOptional");
        AssertEqual(Label + "-optional", host.OptionalText, "NestedFieldLayoutHost.OptionalText");
        AssertEqual(111, host.TrivialFirst, "NestedFieldLayoutHost.TrivialFirst");
        AssertEqual(222, host.TrivialSecond, "NestedFieldLayoutHost.TrivialSecond");
        AssertEqual(3939, host.SentinelValue, "NestedFieldLayoutHost.SentinelValue (neighbour probe)");
    }

    /// <summary>
    /// The full copy fence: pass the host into Swift and back out. Each direction blits the whole
    /// Buffer, so a mirror that is short by a field's worth of bytes both truncates the value and
    /// writes past its allocation. Reading every field back afterwards proves the copy moved the right
    /// number of bytes, and the sentinel proves the neighbouring field was not overwritten.
    /// </summary>
    public void TestNestedFieldLayoutHostSurvivesSwiftRoundTrip()
    {
        using var input = NestedFieldLayoutHost.Make(Label, true, -7, 65000, int.MaxValue);
        using var output = NestedFieldLayoutHost.RoundTrip(input);

        AssertEqual(Label, output.LeadingText, "round-tripped LeadingText");
        AssertEqual(true, output.HasOptional, "round-tripped HasOptional");
        AssertEqual(Label + "-optional", output.OptionalText, "round-tripped OptionalText");
        AssertEqual(-7, output.TrivialFirst, "round-tripped TrivialFirst");
        AssertEqual(65000, output.TrivialSecond, "round-tripped TrivialSecond");
        AssertEqual(int.MaxValue, output.SentinelValue, "round-tripped SentinelValue (neighbour probe)");

        // The source must be untouched by the copy — a short mirror can corrupt either end.
        AssertEqual(Label, input.LeadingText, "source LeadingText after round-trip");
        AssertEqual(int.MaxValue, input.SentinelValue, "source SentinelValue after round-trip");
    }

    /// <summary>
    /// The nil arm of the Optional field. The field keeps the SAME width when empty (nil folds into the
    /// payload's spare bits), so the fields after it must not move — an oracle that appended a
    /// discriminator tag only for the populated case would shift them here.
    /// </summary>
    public void TestNestedFieldLayoutHostWithEmptyOptionalKeepsLaterFieldOffsets()
    {
        using var host = NestedFieldLayoutHost.Make(Label, false, 5, 6, -12345);
        using var output = NestedFieldLayoutHost.RoundTrip(host);

        AssertEqual(false, output.HasOptional, "empty-optional HasOptional");
        AssertEqual("<none>", output.OptionalText, "empty-optional OptionalText");
        AssertEqual(Label, output.LeadingText, "empty-optional LeadingText");
        AssertEqual(5, output.TrivialFirst, "empty-optional TrivialFirst");
        AssertEqual(6, output.TrivialSecond, "empty-optional TrivialSecond");
        AssertEqual(-12345, output.SentinelValue, "empty-optional SentinelValue (neighbour probe)");
    }

    /// <summary>
    /// Reads the nested reference-bearing fields back out as their own projected values. This is the
    /// direct assertion that the bytes the host reserved for a nested struct really do hold that whole
    /// nested struct: the returned leaf's own <c>String</c> must still be readable, which it is not if
    /// only the first word of the leaf was ever copied.
    /// </summary>
    public void TestNestedFieldLayoutHostReturnsIntactNestedValues()
    {
        using var host = NestedFieldLayoutHost.Make(Label, true, 1, 2, 99);

        using var leading = host.Leading;
        AssertEqual(Label, leading.Text, "NestedFieldLayoutHost.Leading.Text");

        using var viaAccessor = host.LeadingLeaf;
        AssertEqual(Label, viaAccessor.Text, "NestedFieldLayoutHost.LeadingLeaf.Text");

        using var optional = host.Optional;
        AssertNotNull(optional, "NestedFieldLayoutHost.Optional");
        AssertEqual(Label + "-optional", optional!.Text, "NestedFieldLayoutHost.Optional.Text");

        using var trivial = host.Trivial;
        AssertEqual(1, trivial.First, "NestedFieldLayoutHost.Trivial.First");
        AssertEqual(2, trivial.Second, "NestedFieldLayoutHost.Trivial.Second");
        AssertEqual(3, trivial.Sum, "NestedFieldLayoutHost.Trivial.Sum");

        AssertEqual(99, host.SentinelValue, "SentinelValue after nested reads (neighbour probe)");
    }

    /// <summary>
    /// Repeats the round-trip so the short-mirror overflow has many chances to land on live allocator
    /// metadata rather than on padding. A single copy through an under-sized Buffer can silently
    /// survive; a loop of them reliably does not.
    /// </summary>
    public void TestNestedFieldLayoutHostRepeatedRoundTripsDoNotCorruptTheHeap()
    {
        for (int i = 0; i < 200; i++)
        {
            using var input = NestedFieldLayoutHost.Make(Label + i, true, i, -i, i * 3);
            using var output = NestedFieldLayoutHost.RoundTrip(input);
            AssertEqual(Label + i, output.LeadingText, $"repeat[{i}] LeadingText");
            AssertEqual(i * 3, output.SentinelValue, $"repeat[{i}] SentinelValue (neighbour probe)");
        }

        TestLogger.Info("NestedFieldLayoutHost: 200 Buffer round-trips with intact fields and no heap damage");
    }
}
