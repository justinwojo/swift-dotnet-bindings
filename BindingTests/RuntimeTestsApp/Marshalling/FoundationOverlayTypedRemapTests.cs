// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Pins the Foundation Swift-overlay → Foundation.NS* typed remap. Swift-side
/// `ByteCountFormatter` and `ValueTransformer` properties must bind as
/// <see cref="Foundation.NSByteCountFormatter"/> and <see cref="Foundation.NSValueTransformer"/>
/// — if FoundationDatabase.xml routed them to NSObject, the explicit-type local
/// assignments below would not compile.
/// </summary>
public class FoundationOverlayTypedRemapTests : TestBase
{
    public FoundationOverlayTypedRemapTests(TestResults results) : base(results) { }

    public void TestByteCountFormatterPropertyBindsAsTypedNS()
    {
        using var helper = new SwiftBindingsTestLib.FoundationOverlayTypedRemapHelper();
        Foundation.NSByteCountFormatter formatter = helper.ByteCountFormatter;
        AssertNotNull(formatter, "ByteCountFormatter property non-null");
    }

    public void TestValueTransformerPropertyBindsAsTypedNS()
    {
        using var helper = new SwiftBindingsTestLib.FoundationOverlayTypedRemapHelper();
        Foundation.NSValueTransformer transformer = helper.ValueTransformer;
        AssertNotNull(transformer, "ValueTransformer property non-null");
    }

    public void TestFormatBytesReturnsString()
    {
        using var helper = new SwiftBindingsTestLib.FoundationOverlayTypedRemapHelper();
        var s = helper.FormatBytes(1024);
        AssertNotNull(s, "FormatBytes returned non-null");
    }
}
