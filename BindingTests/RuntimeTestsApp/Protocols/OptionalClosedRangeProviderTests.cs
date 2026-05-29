// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;
using SwiftBindingsTestLib.SwiftInterop;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Proxy-receiver coverage for an <c>Optional&lt;ClosedRange&lt;Float&gt;&gt;</c> protocol getter.
/// A C# type implements <see cref="IRangeBoundsProvider"/>; Swift reads its <c>AllowedRange</c>
/// through the generated proxy receiver, marshalling the C# <c>SwiftClosedRange&lt;float&gt;?</c>
/// into <c>SwiftOptional&lt;SwiftClosedRange&lt;float&gt;&gt;</c>. ClosedRange is handle-backed (no
/// nested <c>.Buffer</c>), so both the receiver getter conversion and its sizing carrier must name
/// the wrapper — the path that was latently emitting a nonexistent <c>.Buffer</c>.
/// </summary>
public class OptionalClosedRangeProviderTests : TestBase
{
    public OptionalClosedRangeProviderTests(TestResults results) : base(results) { }

    public void TestProviderRangeSpanSome()
    {
        // C# proxy vends Some(2.0...7.5); Swift reads upper-lower = 5.5. Exercises the receiver
        // getter packing the wrapper value into SwiftOptional<SwiftClosedRange<float>> and Swift
        // unpacking both endpoints.
        using var range = new SwiftClosedRange<float>(2.0f, 7.5f);
        var provider = new TestRangeProvider(range);
        var span = TestLibFunctions.ReadProviderRangeSpan(provider);
        AssertEqual(5.5f, span, "Swift reads upper-lower of C# proxy's Some(ClosedRange<float>) getter");
    }

    public void TestProviderRangeNone()
    {
        var provider = new TestRangeProvider(null);
        var span = TestLibFunctions.ReadProviderRangeSpan(provider);
        AssertEqual(-1f, span, "Swift gets nil sentinel from C# proxy's null getter");
        AssertTrue(TestLibFunctions.ProviderRangeIsNil(provider), "C# proxy null getter packs as None");
    }

    public void TestProviderRangeSomeZeroIsNotNil()
    {
        // Some(0...0): payload bytes all zero, but the getter must still pack a Some tag — the None
        // tag is never inferred from a zeroed payload.
        using var range = new SwiftClosedRange<float>(0.0f, 0.0f);
        var provider = new TestRangeProvider(range);
        AssertTrue(!TestLibFunctions.ProviderRangeIsNil(provider), "C# proxy Some(0...0) getter is non-nil despite zero payload");
    }
}

/// <summary>
/// C# conformer to <see cref="IRangeBoundsProvider"/> that vends a fixed optional range from its
/// <c>AllowedRange</c> getter — the value Swift reads back through the proxy receiver.
/// </summary>
internal sealed class TestRangeProvider : IRangeBoundsProvider
{
    private readonly SwiftClosedRange<float>? _range;

    public TestRangeProvider(SwiftClosedRange<float>? range) => _range = range;

    public SwiftClosedRange<float>? AllowedRange => _range;
}
