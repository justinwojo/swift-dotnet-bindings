// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Foundation;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Lifetime;

/// <summary>
/// Objects whose bare pointer crosses into Swift while nothing else in .NET holds them: an argument
/// passed inline, and the result a C# closure hands back. Each is read after a collection has had
/// the chance to run the object's finalizer, and every Swift object C# made is accounted for after
/// its last owner lets go, so a reference the call took too few or too many of shows up as a wrong
/// value, a crash, or a node that outlives its owners.
/// </summary>
public class HandleLivenessTests : TestBase
{
    public HandleLivenessTests(TestResults results) : base(results) { }

    static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    static string Names(int count, Func<int, string>? name = null)
        => string.Join(",", Enumerable.Range(0, count).Select(i => name?.Invoke(i) ?? $"n{i}"));

    // ── Arguments passed inline ───────────────────────────────────────────────────────────

    public void TestSwiftReadsAnInlineArgumentAfterACollection()
    {
        AssertEqual("y", TestLibFunctions.LivenessDescribeObjectAfter(new NSString("y"), Collect), "an Objective-C object read after a collection");
        AssertEqual("z", TestLibFunctions.LivenessNodeNameAfter(new LivenessNode("z"), Collect), "a Swift object read after a collection");
    }

    /// <summary>
    /// Arguments of every kind are created inline on a worker while a collector runs beside it: each
    /// value Swift reads is the one C# passed.
    /// </summary>
    public void TestInlineArgumentsSurviveACollectorRunningBeside()
    {
        using var stop = new CancellationTokenSource();
        var collector = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
                Collect();
        });
        try
        {
            var worker = Task.Run(() =>
            {
                for (int i = 0; i < 200; i++)
                {
                    using (new NSAutoreleasePool())
                    {
                        var name = $"v{i}";
                        string[] read =
                        {
                            TestLibFunctions.LivenessDescribeObject(new NSString(name)),
                            TestLibFunctions.LivenessDescribeOptionalObject(new NSString(name)),
                            TestLibFunctions.LivenessNodeName(new LivenessNode(name)),
                            TestLibFunctions.LivenessOptionalNodeName(new LivenessNode(name)),
                            TestLibFunctions.LivenessURLName(new NSUrl($"https://example.com/{name}")),
                        };
                        foreach (var value in read)
                            if (value != name)
                                return $"iteration {i}: read {value}";
                        using var reader = new LivenessReader(new LivenessNode(name));
                        if (reader.First != name)
                            return $"iteration {i}: initializer read {reader.First}";
                        var both = reader.Read(new LivenessNode(name), new NSString(name));
                        if (both != $"{name}|{name}")
                            return $"iteration {i}: method read {both}";
                        var indexed = reader[new LivenessNode(name)];
                        if (indexed != name)
                            return $"iteration {i}: subscript read {indexed}";
                    }
                }
                return "ok";
            });
            AssertEqual("ok", worker.GetAwaiter().GetResult(), "every argument Swift read is the one C# passed");
        }
        finally
        {
            stop.Cancel();
            collector.GetAwaiter().GetResult();
        }
    }

    // ── Results a C# closure hands back ───────────────────────────────────────────────────

    /// <summary>
    /// Swift keeps every node a C# closure returns, though a collection runs after each and C# kept
    /// none of them; once Swift is done, no node is left behind.
    /// </summary>
    public void TestSwiftKeepsTheNodesAClosureReturns()
    {
        const int count = 20;
        Collect();
        var before = LivenessNode.Live;
        using (var first = new LivenessNode("r"))
        using (var reader = new LivenessReader(first))
        {
            AssertEqual(Names(count), reader.MakeNodes(count, name => new LivenessNode(name), Collect), "every node Swift kept");
            AssertEqual(Names(count), reader.MakeEscapingNodes(count, name => new LivenessNode(name), Collect), "every node an escaping closure returned");
        }
        for (int i = 0; i < 5 && LivenessNode.Live != before; i++)
            Collect();
        AssertEqual(before, LivenessNode.Live, "no node outlives its last owner");
    }

    /// <summary>
    /// The nodes C# keeps and disposes after Swift is done are released exactly once each: the
    /// reference the closure handed Swift is Swift's own.
    /// </summary>
    public void TestNodesAClosureReturnsAreReleasedOnce()
    {
        const int count = 20;
        foreach (var escaping in new[] { false, true })
        {
            Collect();
            var before = LivenessNode.Live;
            var made = new List<LivenessNode>();
            using (var first = new LivenessNode("r"))
            using (var reader = new LivenessReader(first))
            {
                Func<string, LivenessNode> make = name =>
                {
                    var node = new LivenessNode(name);
                    made.Add(node);
                    return node;
                };
                var names = escaping ? reader.MakeEscapingNodes(count, make, Collect) : reader.MakeNodes(count, make, Collect);
                AssertEqual(Names(count), names, $"every node Swift kept (escaping: {escaping})");
            }
            AssertEqual(before + count, LivenessNode.Live, $"C# still holds every node it made (escaping: {escaping})");
            foreach (var node in made)
                node.Dispose();
            AssertEqual(before, LivenessNode.Live, $"disposing C#'s wrappers releases the last reference (escaping: {escaping})");
        }
    }

    /// <summary>The closures' results are read on a worker while a collector runs beside it.</summary>
    public void TestClosureResultsSurviveACollectorRunningBeside()
    {
        Collect();
        var before = LivenessNode.Live;
        using var stop = new CancellationTokenSource();
        var collector = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
                Collect();
        });
        try
        {
            var worker = Task.Run(() =>
            {
                using var first = new LivenessNode("r");
                using var reader = new LivenessReader(first);
                for (int i = 0; i < 50; i++)
                {
                    using (new NSAutoreleasePool())
                    {
                        var nodes = reader.MakeNodes(4, name => new LivenessNode(name), () => { });
                        if (nodes != Names(4))
                            return $"iteration {i}: nodes {nodes}";
                        var escaping = reader.MakeEscapingNodes(4, name => new LivenessNode(name), () => { });
                        if (escaping != Names(4))
                            return $"iteration {i}: escaping nodes {escaping}";
                    }
                }
                return "ok";
            });
            AssertEqual("ok", worker.GetAwaiter().GetResult(), "every result Swift kept is the one C# returned");
        }
        finally
        {
            stop.Cancel();
            collector.GetAwaiter().GetResult();
        }
        for (int i = 0; i < 5 && LivenessNode.Live != before; i++)
            Collect();
        AssertEqual(before, LivenessNode.Live, "no node outlives its last owner");
    }
}
