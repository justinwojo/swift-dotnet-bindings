// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Async;

/// <summary>
/// Regression gate for Issue #34: SB0001 must NOT fire on async methods whose
/// parameter is an existential array (<c>[any Proto]</c>). Those shapes round-trip
/// cleanly through the async wrapper path — the C# P/Invoke surface is uniformly
/// blittable because <c>HasNonBlittablePInvokeTypes</c> early-returns <c>false</c>
/// for async methods. The matching sync-with-closure shape IS genuinely JIT-risky
/// and must keep its SB0001 warning so consumers have a signal to avoid it.
/// </summary>
public class AsyncExistentialArrayTests : TestBase
{
    public AsyncExistentialArrayTests(TestResults results) : base(results) { }

    private static bool HasSb0001Obsolete(MethodInfo method)
    {
        foreach (var attr in method.GetCustomAttributes<ObsoleteAttribute>(inherit: false))
        {
            if (attr.DiagnosticId == "SB0001")
                return true;
        }
        return false;
    }

    public async Task TestWorkingAsyncShape_RoundTrips()
    {
        var client = new GenerateContentClient();
        var parts = new IPartsRepresentable[]
        {
            new TextPart("alpha"),
            new TextPart("beta"),
        };
        var result = await WithTimeout(client.GenerateContentAsync(parts), DefaultAsyncTimeout);
        AssertEqual("alpha,beta", result, "GenerateContentAsync should join labels with comma");
        TestLogger.Info($"GenerateContentAsync -> {result}");
    }

    public async Task TestWorkingAsyncShape_EmptyArray()
    {
        var client = new GenerateContentClient();
        var result = await WithTimeout(client.GenerateContentAsync(Array.Empty<IPartsRepresentable>()), DefaultAsyncTimeout);
        AssertEqual("", result, "GenerateContentAsync with empty array");
    }

    public async Task TestWorkingAsyncFreeFunction_RoundTrips()
    {
        var parts = new IPartsRepresentable[]
        {
            new TextPart("a"),
            new TextPart("b"),
            new TextPart("c"),
        };
        var count = await WithTimeout(SwiftBindingsTestLib.Functions.GenerateContentFreeAsync(parts), DefaultAsyncTimeout);
        AssertEqual(3, count, "GenerateContentFreeAsync should return the parts count");
    }

    public void TestWorkingAsyncShape_NotMarkedSb0001()
    {
        var method = typeof(GenerateContentClient).GetMethod(
            nameof(GenerateContentClient.GenerateContentAsync),
            BindingFlags.Public | BindingFlags.Instance);
        AssertNotNull(method, "GenerateContentAsync method should exist on GenerateContentClient");
        AssertFalse(HasSb0001Obsolete(method!),
            "GenerateContentAsync([any Proto]) must NOT be flagged SB0001 — the async wrapper surface is uniformly blittable.");
    }

    public void TestWorkingAsyncFreeFunction_NotMarkedSb0001()
    {
        var method = typeof(SwiftBindingsTestLib.Functions).GetMethod(
            nameof(SwiftBindingsTestLib.Functions.GenerateContentFreeAsync),
            BindingFlags.Public | BindingFlags.Static);
        AssertNotNull(method, "GenerateContentFreeAsync should exist as a free function");
        AssertFalse(HasSb0001Obsolete(method!),
            "generateContentFreeAsync(parts:) must NOT be flagged SB0001 — free-function async shape is equally safe.");
    }

    public void TestBrokenStreamShape_MarkedSb0001()
    {
        var method = typeof(GenerateContentClient).GetMethod(
            nameof(GenerateContentClient.GenerateContentStream),
            BindingFlags.Public | BindingFlags.Instance);
        AssertNotNull(method, "GenerateContentStream should exist on GenerateContentClient");
        AssertTrue(HasSb0001Obsolete(method!),
            "GenerateContentStream(parts:onChunk:) IS a sync method with an existential-array closure — SB0001 is the correct flag.");
    }
}
