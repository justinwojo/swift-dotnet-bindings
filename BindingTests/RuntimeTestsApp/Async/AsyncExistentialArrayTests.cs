// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using SwiftBindingsTestLib.SwiftInterop;

namespace RuntimeTestsApp.Async;

/// <summary>
/// Regression gate for Issue #34: SB0001 must NOT fire on async methods whose
/// parameter is an existential array (<c>[any Proto]</c>). Those shapes round-trip
/// cleanly through the async wrapper path — the C# P/Invoke surface is uniformly
/// blittable because <c>HasNonBlittablePInvokeTypes</c> early-returns <c>false</c>
/// for async methods. The matching sync-with-closure shape now takes the typed-address
/// collection adapter and must remain callable without SB0001.
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

    public void TestStreamShape_RoundTripsBothExistentialArrayEdges()
    {
        using var client = new GenerateContentClient();
        var parts = new IPartsRepresentable[]
        {
            new TextPart("first"),
            new TextPart("second"),
        };
        int calls = 0;
        var labels = new List<string>();

        client.GenerateContentStream(parts, containers =>
        {
            calls++;
            foreach (var container in containers)
            {
                using var part = new PartsRepresentableProxy(container);
                labels.Add(part.Label);
            }
        });

        AssertEqual(1, calls, "GenerateContentStream invokes its callback exactly once");
        AssertEqual(2, labels.Count, "callback preserves the existential array count");
        AssertEqual("first", labels[0], "callback preserves first label and order");
        AssertEqual("second", labels[1], "callback preserves second label and order");
    }

    public void TestStreamShape_EmptyArray()
    {
        using var client = new GenerateContentClient();
        int count = -1;
        client.GenerateContentStream(Array.Empty<IPartsRepresentable>(), values => count = values.Count);
        AssertEqual(0, count, "empty existential array survives both edges");
    }

    public void TestStreamShape_NotMarkedSb0001()
    {
        var method = typeof(GenerateContentClient).GetMethod(
            nameof(GenerateContentClient.GenerateContentStream),
            BindingFlags.Public | BindingFlags.Instance);
        AssertNotNull(method, "GenerateContentStream should exist on GenerateContentClient");
        AssertFalse(HasSb0001Obsolete(method!),
            "GenerateContentStream(parts:onChunk:) uses a Cdecl outer wrapper and typed-address callback adapter.");
    }
}
