// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Async;

/// <summary>
/// Existential-bypass wrappers on a <c>@MainActor</c> type. The wrapper must be main-actor
/// isolated to compile at all; at run time the attribute changes no ABI, so each call has to reach
/// the member with Swift's default for the omitted existential and leave the receiver's state
/// where Swift put it. The runner calls these on the main thread.
/// </summary>
public class MainActorExistentialBypassTests : TestBase
{
    public MainActorExistentialBypassTests(TestResults results) : base(results) { }

    public void TestVoidMethodBypassReachesTheMemberWithItsDefault()
    {
        using var sink = new MainActorBypassSink();
        sink.Take();
        sink.Take();
        AssertEqual(2, sink.Takes, "Each Take() ran once with an empty filter list");
    }

    public void TestExistentialReturnBypassReturnsTheMembersValue()
    {
        using var sink = new MainActorBypassSink();
        var item = sink.MakeItem();
        AssertEqual("[mab] sink", item.GetDescribe(), "MakeItem() returned the Swift item");
        AssertEqual(1, sink.Takes, "MakeItem() ran once with an empty filter list");
    }

    public void TestInitializerBypassUsesTheDefault()
    {
        using var config = MainActorBypassConfig.Create("cfg");
        AssertEqual(0, config.FilterCount, "The omitted filter list defaulted to []");
        AssertEqual("cfg", config.Label.ToString(), "The label passed through");
    }
}
