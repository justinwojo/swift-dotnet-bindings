// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Closures;

/// <summary>
/// Pairs with <c>BindingTests/Sources/SwiftBindingsTestLib/Closures/ZeroWitnessExistentialClosures.swift</c>.
/// An <c>Any</c> or <c>any Sendable</c> a Swift function passes into a C# callback is typed
/// <c>object</c> in C#, and the callback receives the plain value Swift passed, never the raw
/// container. A closure that returns <c>Any</c> is refused in both directions (the four-word
/// container is returned indirectly by Swift but in registers by a CallConvSwift signature), so the
/// fixture's producer and consumer members have no binding to test here.
/// </summary>
public class ZeroWitnessExistentialClosureTests : TestBase
{
    public ZeroWitnessExistentialClosureTests(TestResults results) : base(results) { }

    private static string Describe(object? value) =>
        value == null ? "null" : $"{value.GetType().Name}:{value}";

    // MARK: C# callbacks called by Swift

    public void TestCallbackReceivesSwiftInt()
    {
        using var host = new ZeroWitnessClosureHost();
        string? seen = null;
        var result = host.CallWithAnyInt(v => { seen = Describe(v); return "ok"; });
        AssertEqual("ok", result, "Swift returned the callback's string");
        AssertEqual("Int64:42", seen, "callback received Swift Int 42 as a long");
    }

    public void TestCallbackReceivesSwiftString()
    {
        using var host = new ZeroWitnessClosureHost();
        string? seen = null;
        var result = host.CallWithAnyString(v => { seen = Describe(v); return "ok"; });
        AssertEqual("ok", result, "Swift returned the callback's string");
        AssertEqual("String:swift-side", seen, "callback received the Swift string");
    }

    public void TestSendableCallbackReceivesSwiftInt()
    {
        using var host = new ZeroWitnessClosureHost();
        string? seen = null;
        host.CallWithSendable(v => { seen = Describe(v); return "ok"; });
        AssertEqual("Int64:7", seen, "callback received Swift Int 7 through any Sendable");
    }
}
