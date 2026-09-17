// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// End-to-end gate for protocols whose synthesized Swift conformance used to fail to type-check:
/// variadic requirements (and their array-spelled twins), a static property beside instance
/// requirements, a protocol extension member named <c>fatalError</c>, return-type-only overloads,
/// typed-throws requirements, a protocol extension subscript that is not a requirement, and
/// Optional twins of value types that project to CLR reference types (Data → byte[]).
/// Each case hands a plain C# conformer to a Swift helper that calls the requirement through the
/// existential, so a green result proves the conformance compiled and dispatches into C#.
/// </summary>
public class RequirementShapeConformanceTests : TestBase
{
    public RequirementShapeConformanceTests(TestResults results) : base(results) { }

    public void TestVariadicRequirementDispatches()
    {
        var sink = new TagSink();
        AssertEqual(106, TestLibFunctions.VariadicTagSinkRecordVariadic(sink, "variadic"),
            "Swift's variadic call (1, 2, 3) reached the C# conformer");
        AssertEqual("variadic", sink.LastLabel, "variadic call carried the label");
    }

    public void TestArraySpelledTwinDispatches()
    {
        var sink = new TagSink();
        AssertEqual(149, TestLibFunctions.VariadicTagSinkRecordArray(sink, "array", new[] { 40, 9 }),
            "the array-spelled twin requirement dispatched to the same C# member");
        AssertEqual("array", sink.LastLabel, "array call carried the label");
    }

    public void TestExistentialVariadicDispatches()
    {
        AssertEqual("items:2", TestLibFunctions.VariadicTagSinkDescribe(new TagSink()),
            "the variadic of existential type delivered both elements");
    }

    public void TestStaticPropertyProtocolDispatchesInstanceRequirements()
    {
        var storage = new TaggedStorage();
        AssertEqual(64, TestLibFunctions.StaticTaggedStorageCapacity(storage),
            "instance property dispatched beside a static property requirement");
        AssertEqual(10, TestLibFunctions.StaticTaggedStorageStore(storage, 5),
            "instance method dispatched beside a static property requirement");
    }

    public void TestFatalErrorExtensionMemberDoesNotShadowTrap()
    {
        AssertEqual(3, TestLibFunctions.TrapReporterCount(new Reporter()),
            "protocol with a `fatalError` extension member conforms and dispatches");
    }

    public void TestReturnTypeOverloadFirstOverloadDispatches()
    {
        AssertEqual(5, TestLibFunctions.EnrollmentClientEnrollCode(new Enrollment(), "token"),
            "the Int32-returning overload dispatched to the C# member");
    }

    public void TestTypedThrowsRequirementDispatches()
    {
        AssertEqual(12, TestLibFunctions.TypedThrowingCounterCount(new Counter(), "twelve-chars"),
            "typed-throws requirement dispatched to the C# conformer");
    }

    public void TestExtensionSubscriptIsNotARequirement()
    {
        var model = new Modeled();
        AssertEqual(42, TestLibFunctions.ExtensionSubscriptModeledRead(model, "answer"),
            "the protocol extension subscript read the C# conformer's storage");
    }

    public void TestOptionalDataInoutTwinsDispatchSeparately()
    {
        AssertEqual(13021, TestLibFunctions.OptionalBytesFieldDecoderRoundTrip(new BytesDecoder()),
            "inout Data and inout Data? reached distinct C# members and both wrote back");
    }

    public void TestOptionalDataByValueTwinsDispatchSeparately()
    {
        AssertEqual(3040, TestLibFunctions.OptionalBytesFieldDecoderMeasure(new BytesDecoder()),
            "Data and Data? parameters reached distinct C# members");
        AssertEqual(3101, TestLibFunctions.OptionalBytesFieldDecoderCollect(new BytesDecoder()),
            "[Data] and [Data?] parameters reached distinct C# members");
    }

    private sealed class TagSink : IVariadicTagSink
    {
        public string? LastLabel { get; private set; }

        public int Record(string label, IEnumerable<int> tags)
        {
            LastLabel = label;
            return 100 + tags.Sum();
        }

        public string Describe(IEnumerable<object> items) => $"items:{items.Count()}";
    }

    private sealed class TaggedStorage : IStaticTaggedStorage
    {
        public int Capacity => 64;
        public int Store(int value) => value * 2;
    }

    private sealed class Reporter : ITrapReporter
    {
        public void Report(Func<string?> message, nuint line) { }
        public int GetReportedCount() => 3;
    }

    private sealed class Enrollment : IEnrollmentClient
    {
        public int Enroll(string token) => token.Length;
        public int Enroll(string token, string email) => token.Length + email.Length;
    }

    private sealed class Counter : ITypedThrowingCounter
    {
        public int Count(string key) => key.Length;
    }

    private sealed class BytesDecoder : IOptionalBytesFieldDecoder
    {
        public nint DecodeBytesValueWithData(ref byte[] value)
        {
            value = new byte[] { 9, 9, 9 };
            return 10;
        }

        public nint DecodeBytesValueWithOptionalData(ref byte[]? value)
        {
            value = value is null ? new byte[] { 5 } : null;
            return 20;
        }

        public nint MeasureWithData(byte[] value) => value.Length;
        public nint MeasureWithOptionalData(byte[]? value) => value is null ? 40 : -1;
        public nint CollectWithArrayData(IEnumerable<byte[]> values) => values.Sum(v => v.Length);
        public nint CollectWithArrayOptionalData(IEnumerable<byte[]?> values) =>
            values.Sum(v => v is null ? 100 : v.Length);
    }

    private sealed class Modeled : IExtensionSubscriptModeled
    {
        public IReadOnlyDictionary<string, int> Storage { get; set; } =
            new Dictionary<string, int> { ["answer"] = 42 };
    }
}
