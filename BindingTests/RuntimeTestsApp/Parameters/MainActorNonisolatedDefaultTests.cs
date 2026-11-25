// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Parameters;

/// <summary>
/// Trimmed default-argument overloads of <c>nonisolated</c> members of a <c>@MainActor</c> class.
/// Each trimmed overload calls a Swift shim in an extension of the class, which inherits the
/// class's main-actor isolation unless it opts out with the member; a shim that kept it made
/// swiftc refuse the nonisolated wrapper's call, and verify-recover withdrew every such overload.
/// Every test calls the short form and checks that Swift evaluated its own default.
/// </summary>
public class MainActorNonisolatedDefaultTests : TestBase
{
    public MainActorNonisolatedDefaultTests(TestResults results) : base(results) { }

    public void TestNonisolatedInit_TrimmedOverloadUsesTheSwiftDefault()
    {
        using var host = new MainActorNonisolatedDefaults("n");
        AssertEqual("n", host.Name, "The kept argument must reach Swift");
        AssertEqual("autoauto", host.Token, "Swift must evaluate the trimmed initializer argument's default");

        using var explicitToken = new MainActorNonisolatedDefaults("n", "t");
        AssertEqual("t", explicitToken.Token, "The full initializer must pass the caller's value through");
    }

    public void TestNonisolatedMethod_TrimmedOverloadUsesTheSwiftDefault()
    {
        using var host = new MainActorNonisolatedDefaults("n", "t");
        AssertEqual("p:n:t:end", host.Label("p"), "Swift must evaluate the trimmed method argument's default");
        AssertEqual("p:n:t:s", host.Label("p", "s"), "The full method must pass the caller's value through");
    }

    public void TestNonisolatedStaticMethod_TrimmedOverloadUsesTheSwiftDefault()
    {
        AssertEqual("made:tag", MainActorNonisolatedDefaults.Make(), "Swift must evaluate the static method's default");
        AssertEqual("made:x", MainActorNonisolatedDefaults.Make("x"), "The full static method must pass the caller's value through");
    }

    public void TestIsolatedMethod_TrimmedOverloadKeepsTheClassIsolation()
    {
        // The harness runs tests on the main thread, which a main-actor member requires.
        using var host = new MainActorNonisolatedDefaults("n", "t");
        AssertEqual("p@n:main", host.IsolatedLabel("p"), "An isolated member's trimmed overload must still bind");
        AssertEqual("p@n:s", host.IsolatedLabel("p", "s"), "The full isolated method must pass the caller's value through");
    }
}
