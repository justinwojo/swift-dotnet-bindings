// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using CoreMedia;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Types;

/// <summary>
/// CMTime, CMTimeRange and CMTimeMapping are imported C structs projected as the .NET
/// <c>CoreMedia</c> structs of the same 24/48/96-byte layout. Every shape crosses here — by-value
/// parameter and return, stored property of a struct and a class, array element, optional — and
/// each field is checked separately, so a buffer sized for the wrong type, a shifted argument slot
/// or a misaligned element read fails on the field it corrupts.
/// </summary>
public class CoreMediaTimeTests : TestBase
{
    public CoreMediaTimeTests(TestResults results) : base(results) { }

    private static CMTime Time(long value, int timescale, long epoch, CMTime.Flags flags = CMTime.Flags.Valid)
        => new CMTime { Value = value, TimeScale = timescale, TimeFlags = flags, TimeEpoch = epoch };

    private void AssertTime(long value, int timescale, CMTime.Flags flags, long epoch, CMTime actual, string what)
    {
        AssertEqual(value, actual.Value, $"{what}.Value");
        AssertEqual(timescale, actual.TimeScale, $"{what}.TimeScale");
        AssertEqual(flags, actual.TimeFlags, $"{what}.TimeFlags");
        AssertEqual(epoch, actual.TimeEpoch, $"{what}.TimeEpoch");
    }

    #region By-value parameters and returns

    public void TestMakeCMTimeReturnsEveryField()
    {
        var time = TestLibFunctions.MakeCMTime(1234567890123L, 600, 1 | 2, 77);
        AssertTime(1234567890123L, 600, CMTime.Flags.Valid | CMTime.Flags.HasBeenRounded, 77, time, "MakeCMTime");
    }

    public void TestDescribeCMTimeSeesEveryField()
    {
        AssertEqual("42/30000/1/9", TestLibFunctions.DescribeCMTime(Time(42, 30000, 9)), "DescribeCMTime");
    }

    public void TestEchoCMTimeRoundTrips()
    {
        var echoed = TestLibFunctions.EchoCMTime(Time(-5, 44100, 3, CMTime.Flags.Valid | CMTime.Flags.Indefinite));
        AssertTime(-5, 44100, CMTime.Flags.Valid | CMTime.Flags.Indefinite, 3, echoed, "EchoCMTime");
    }

    public void TestCMTimeBetweenScalarsKeepsArgumentSlots()
    {
        AssertEqual("-7|11/600/1/2|9000000000",
            TestLibFunctions.DescribeCMTimeBetween(-7, Time(11, 600, 2), 9000000000L),
            "DescribeCMTimeBetween");
    }

    public void TestShiftCMTimeKeepsTimescaleFlagsAndEpoch()
    {
        AssertTime(150, 90000, CMTime.Flags.Valid, 4, TestLibFunctions.ShiftCMTime(Time(100, 90000, 4), 50), "ShiftCMTime");
    }

    public void TestMakeCMTimeRangeReturnsBothTimes()
    {
        var range = TestLibFunctions.MakeCMTimeRange(Time(10, 600, 1), Time(20, 1200, 2));
        AssertTime(10, 600, CMTime.Flags.Valid, 1, range.Start, "Range.Start");
        AssertTime(20, 1200, CMTime.Flags.Valid, 2, range.Duration, "Range.Duration");
    }

    public void TestDescribeAndEchoCMTimeRange()
    {
        var range = new CMTimeRange { Start = Time(3, 24, 5), Duration = Time(48, 24, 6) };
        AssertEqual("[3/24/1/5 + 48/24/1/6]", TestLibFunctions.DescribeCMTimeRange(range), "DescribeCMTimeRange");
        var echoed = TestLibFunctions.EchoCMTimeRange(range);
        AssertTime(3, 24, CMTime.Flags.Valid, 5, echoed.Start, "EchoCMTimeRange.Start");
        AssertTime(48, 24, CMTime.Flags.Valid, 6, echoed.Duration, "EchoCMTimeRange.Duration");
    }

    public void TestDescribeAndEchoCMTimeMapping()
    {
        var mapping = new CMTimeMapping
        {
            Source = new CMTimeRange { Start = Time(1, 10, 1), Duration = Time(2, 20, 2) },
            Target = new CMTimeRange { Start = Time(3, 30, 3), Duration = Time(4, 40, 4) },
        };
        AssertEqual("[1/10/1/1 + 2/20/1/2] -> [3/30/1/3 + 4/40/1/4]",
            TestLibFunctions.DescribeCMTimeMapping(mapping), "DescribeCMTimeMapping");
        var echoed = TestLibFunctions.EchoCMTimeMapping(mapping);
        AssertTime(1, 10, CMTime.Flags.Valid, 1, echoed.Source.Start, "Mapping.Source.Start");
        AssertTime(2, 20, CMTime.Flags.Valid, 2, echoed.Source.Duration, "Mapping.Source.Duration");
        AssertTime(3, 30, CMTime.Flags.Valid, 3, echoed.Target.Start, "Mapping.Target.Start");
        AssertTime(4, 40, CMTime.Flags.Valid, 4, echoed.Target.Duration, "Mapping.Target.Duration");
    }

    #endregion

    #region Stored properties

    public void TestStructStoredCMTime()
    {
        using var sample = TestLibFunctions.MakeTimedSample(987654321L, 48000, 12, 0.25);
        AssertTime(987654321L, 48000, CMTime.Flags.Valid, 12, sample.Time, "TimedSample.Time");
        AssertEqual(0.25, sample.Value, "TimedSample.Value");
    }

    public void TestStructStoredCMTimeRangeBesideString()
    {
        using var span = TestLibFunctions.MakeTimedSpan(100, 250, 600, "chorus");
        AssertTime(100, 600, CMTime.Flags.Valid, 1, span.Range.Start, "TimedSpan.Range.Start");
        AssertTime(250, 600, CMTime.Flags.Valid, 2, span.Range.Duration, "TimedSpan.Range.Duration");
        AssertEqual("chorus", span.Label, "TimedSpan.Label");
    }

    public void TestStructInitTakesCMTime()
    {
        using var sample = new TimedSample(Time(7, 25, 8), 1.5);
        AssertTime(7, 25, CMTime.Flags.Valid, 8, sample.Time, "TimedSample(init).Time");
        AssertEqual(1.5, sample.Value, "TimedSample(init).Value");
    }

    public void TestClassCMTimePropertiesGetAndSet()
    {
        using var box = new CMTimeBox(Time(1, 2, 3), new CMTimeRange { Start = Time(4, 5, 6), Duration = Time(7, 8, 9) });
        AssertTime(1, 2, CMTime.Flags.Valid, 3, box.Time, "CMTimeBox.Time");
        AssertTime(4, 5, CMTime.Flags.Valid, 6, box.Range.Start, "CMTimeBox.Range.Start");
        AssertTime(7, 8, CMTime.Flags.Valid, 9, box.Range.Duration, "CMTimeBox.Range.Duration");

        box.Time = Time(-100, 1000, 11, CMTime.Flags.Valid | CMTime.Flags.PositiveInfinity);
        box.Range = new CMTimeRange { Start = Time(12, 13, 14), Duration = Time(15, 16, 17) };
        AssertTime(-100, 1000, CMTime.Flags.Valid | CMTime.Flags.PositiveInfinity, 11, box.Time, "CMTimeBox.Time after set");
        AssertTime(12, 13, CMTime.Flags.Valid, 14, box.Range.Start, "CMTimeBox.Range.Start after set");
        AssertTime(15, 16, CMTime.Flags.Valid, 17, box.Range.Duration, "CMTimeBox.Range.Duration after set");
    }

    public void TestFrozenStructWithCMTimeAfterInt32()
    {
        // Swift lays `time` out at offset 4 (CMTime is 4-byte aligned); the field must still read back whole.
        using var tagged = TestLibFunctions.MakeTaggedTime(-3, 5555, 600, 21);
        AssertEqual(-3, tagged.Tag, "TaggedTime.Tag");
        AssertTime(5555, 600, CMTime.Flags.Valid, 21, tagged.Time, "TaggedTime.Time");
        AssertEqual("-3@5555/600/1/21", TestLibFunctions.DescribeTaggedTime(tagged), "DescribeTaggedTime");
    }

    #endregion

    #region Arrays and optionals

    public void TestCMTimeArrayReturn()
    {
        var times = TestLibFunctions.MakeCMTimes(4);
        AssertEqual(4, times.Count, "MakeCMTimes count");
        for (int i = 0; i < times.Count; i++)
            AssertTime(i * 10 + 1, 600, CMTime.Flags.Valid, i, times[i], $"MakeCMTimes[{i}]");
    }

    public void TestCMTimeArrayParameter()
    {
        var times = new[] { Time(1, 600, 0), Time(2, 1200, 5), Time(3, 90000, 9) };
        AssertEqual("1/600/1/0,2/1200/1/5,3/90000/1/9", TestLibFunctions.DescribeCMTimes(times), "DescribeCMTimes");
    }

    public void TestStoredCMTimeAndCMTimeRangeArrays()
    {
        using var track = TestLibFunctions.MakeTimeTrack(3, 2);
        AssertEqual(3, track.Beats.Count, "TimeTrack.Beats count");
        for (int i = 0; i < track.Beats.Count; i++)
            AssertTime(i * 100 + 7, 44100, CMTime.Flags.Valid, 0, track.Beats[i], $"TimeTrack.Beats[{i}]");
        AssertEqual(2, track.Sections.Count, "TimeTrack.Sections count");
        for (int i = 0; i < track.Sections.Count; i++)
        {
            AssertTime(i * 1000, 48000, CMTime.Flags.Valid, 0, track.Sections[i].Start, $"TimeTrack.Sections[{i}].Start");
            AssertTime(500 + i, 48000, CMTime.Flags.Valid, 0, track.Sections[i].Duration, $"TimeTrack.Sections[{i}].Duration");
        }
    }

    public void TestStructInitTakesCMTimeArrays()
    {
        using var track = new TimeTrack(
            new[] { Time(5, 10, 1), Time(6, 10, 2) },
            new[] { new CMTimeRange { Start = Time(7, 10, 3), Duration = Time(8, 10, 4) } });
        AssertEqual(2, track.Beats.Count, "TimeTrack(init).Beats count");
        AssertTime(5, 10, CMTime.Flags.Valid, 1, track.Beats[0], "TimeTrack(init).Beats[0]");
        AssertTime(6, 10, CMTime.Flags.Valid, 2, track.Beats[1], "TimeTrack(init).Beats[1]");
        AssertEqual(1, track.Sections.Count, "TimeTrack(init).Sections count");
        AssertTime(7, 10, CMTime.Flags.Valid, 3, track.Sections[0].Start, "TimeTrack(init).Sections[0].Start");
        AssertTime(8, 10, CMTime.Flags.Valid, 4, track.Sections[0].Duration, "TimeTrack(init).Sections[0].Duration");
    }

    public void TestFrozenStructWithCMTimeArrays()
    {
        var items = TestLibFunctions.MakeTaggedTimes(3);
        AssertEqual(3, items.Count, "MakeTaggedTimes count");
        for (int i = 0; i < items.Count; i++)
        {
            AssertEqual(-i, items[i].Tag, $"MakeTaggedTimes[{i}].Tag");
            AssertTime(i * 7 + 1, 600, CMTime.Flags.Valid, i, items[i].Time, $"MakeTaggedTimes[{i}].Time");
        }
        AssertEqual("0@1/600/1/0,-1@8/600/1/1,-2@15/600/1/2", TestLibFunctions.DescribeTaggedTimes(items),
            "DescribeTaggedTimes(returned array)");

        var built = new[] { new TaggedTime(5, Time(9, 10, 11)), new TaggedTime(6, Time(12, 13, 14)), new TaggedTime(7, Time(15, 16, 17)) };
        AssertEqual("5@9/10/1/11,6@12/13/1/14,7@15/16/1/17", TestLibFunctions.DescribeTaggedTimes(built),
            "DescribeTaggedTimes(managed array)");
    }

    public void TestOptionalCMTimeReturn()
    {
        var present = TestLibFunctions.MaybeCMTime(true, 360);
        AssertTrue(present.HasValue, "MaybeCMTime(true) has a value");
        AssertTime(360, 90000, CMTime.Flags.Valid, 3, present!.Value, "MaybeCMTime(true)");
        AssertTrue(!TestLibFunctions.MaybeCMTime(false, 360).HasValue, "MaybeCMTime(false) is null");
    }

    public void TestOptionalCMTimeParameter()
    {
        AssertEqual("8/16/1/32", TestLibFunctions.DescribeOptionalCMTime(Time(8, 16, 32)), "DescribeOptionalCMTime(value)");
        AssertEqual("nil", TestLibFunctions.DescribeOptionalCMTime(null), "DescribeOptionalCMTime(null)");
    }

    #endregion
}
