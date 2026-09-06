// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

#pragma warning disable SB0010 // the interface is consumed forward only; nothing here implements it for Swift to call back

/// <summary>
/// Protocol requirements taking a <c>(Result&lt;T, any Error&gt;) -> Void</c> closure, called through
/// the interface. The interface declaration and the conformer's member are spelled by different
/// translators; the binding compiling is the first gate (a disagreement is CS0535), and the calls
/// through the interface reference prove the member the interface declares is the one the class
/// implements and marshals.
/// </summary>
public class ProtocolResultClosureRequirementTests : TestBase
{
    public ProtocolResultClosureRequirementTests(TestResults results) : base(results) { }

    public void TestClassPayloadSuccessThroughInterface()
    {
        IResultCallbackSource source = new ResultCallbackFileSource(shouldFail: false);
        int calls = 0;
        string? label = null;
        int magnitude = -1;

        source.Load(result =>
        {
            calls++;
            using (result)
            {
                if (result.IsSuccess)
                {
                    var payload = result.Success;
                    label = payload.Label;
                    magnitude = payload.Magnitude;
                }
            }
        });

        AssertEqual(1, calls, $"Expected exactly one callback, got {calls}");
        AssertEqual("loaded", label, "Success payload label");
        AssertEqual(42, magnitude, "Success payload magnitude");
    }

    public void TestClassPayloadFailureThroughInterface()
    {
        IResultCallbackSource source = new ResultCallbackFileSource(shouldFail: true);
        int calls = 0;
        bool sawFailure = false;

        source.Load(result =>
        {
            calls++;
            using (result)
            {
                sawFailure = result.IsFailure;
            }
        });

        AssertEqual(1, calls, $"Expected exactly one callback, got {calls}");
        AssertTrue(sawFailure, "Expected the failure arm");
    }

    public void TestDataSuccessThroughInterface()
    {
        IResultCallbackSource source = new ResultCallbackFileSource(shouldFail: false);
        int calls = 0;
        byte[]? bytes = null;

        source.LoadData(result =>
        {
            calls++;
            using (result)
            {
                if (result.IsSuccess)
                    bytes = result.Success.ToByteArray();
            }
        });

        AssertEqual(1, calls, $"Expected exactly one callback, got {calls}");
        AssertTrue(bytes != null && bytes.Length == 4, "Expected the four-byte payload");
        AssertEqual((byte)1, bytes![0]);
        AssertEqual((byte)4, bytes[3]);
    }

    public void TestDataFailureThroughInterface()
    {
        IResultCallbackSource source = new ResultCallbackFileSource(shouldFail: true);
        int calls = 0;
        bool sawFailure = false;

        source.LoadData(result =>
        {
            calls++;
            using (result)
            {
                sawFailure = result.IsFailure;
            }
        });

        AssertEqual(1, calls, $"Expected exactly one callback, got {calls}");
        AssertTrue(sawFailure, "Expected the failure arm");
    }
}
