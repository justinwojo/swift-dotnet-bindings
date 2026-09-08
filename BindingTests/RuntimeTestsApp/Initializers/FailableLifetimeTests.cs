// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Initializers;

public class FailableLifetimeTests : TestBase
{
    public FailableLifetimeTests(TestResults results) : base(results) { }

    public void TestSomeCopiesPayloadAndDestroysExactlyOnce()
    {
        using var argument = new FailableLifetimeArgument(37);
        string text = "original";
        int before = Functions.ReadFailablePayloadDeinits();
        AssertTrue(FailableLifetimeValue.TryCreate(ref text, argument, 1, out var result));
        using (result)
        {
            AssertEqual(37, result.Number);
            AssertEqual(before, Functions.ReadFailablePayloadDeinits(), "copy owns the payload after Optional cleanup");
        }
        AssertEqual(before + 1, Functions.ReadFailablePayloadDeinits(), "exactly one native payload deinit");
        AssertEqual("failable initializer entered", text);
    }

    public void TestNoneInitializesAnEmptyOptional()
    {
        using var argument = new FailableLifetimeArgument(37);
        string text = "original";
        int before = Functions.ReadFailablePayloadDeinits();
        AssertFalse(FailableLifetimeValue.TryCreate(ref text, argument, 0, out var result));
        AssertNull(result);
        AssertEqual(before, Functions.ReadFailablePayloadDeinits());
        AssertEqual("failable initializer entered", text);
    }

    public void TestSwiftThrowLeavesRawResultStorage()
    {
        using var argument = new FailableLifetimeArgument(37);
        string text = "original";
        int before = Functions.ReadFailablePayloadDeinits();
        AssertThrows<SwiftException>(() => FailableLifetimeValue.TryCreate(ref text, argument, 2, out _));
        AssertEqual(before, Functions.ReadFailablePayloadDeinits());
        AssertEqual("failable initializer entered", text);
    }

    public void TestDisposedArgumentDoesNotEnterOrDestroyAResult()
    {
        using var argument = new FailableLifetimeArgument(37);
        argument.Dispose();
        string text = "original";
        int entries = Functions.ReadFailableEntries();
        int deinits = Functions.ReadFailablePayloadDeinits();
        AssertThrows<ObjectDisposedException>(() => FailableLifetimeValue.TryCreate(ref text, argument, 1, out _));
        AssertEqual(entries, Functions.ReadFailableEntries());
        AssertEqual(deinits, Functions.ReadFailablePayloadDeinits());
        AssertEqual("original", text);
    }
}
