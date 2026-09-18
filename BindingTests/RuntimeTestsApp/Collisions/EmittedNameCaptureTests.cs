// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collisions;

/// <summary>
/// Round-trips members of types whose own members are named like the names the generated code
/// relies on: a nested enum with cases <c>swift</c> and <c>system</c>, properties named after the
/// namespace roots and the runtime and BCL helpers the marshalling calls, nested types named
/// <c>Task</c> and <c>Action</c>, and a protocol extension whose members and typealiases are
/// named like the standard-library functions and types the Swift wrapper calls.
///
/// Both generated layers write code inside the scope of the type they bind, where such a member
/// outranks a namespace or standard-library name of the same spelling. The compile gate proves
/// the generated C# and Swift still build; these assertions prove each marshalling path inside
/// the capturing scopes still reaches the right symbol and carries its value.
/// </summary>
public class EmittedNameCaptureTests : TestBase
{
    public EmittedNameCaptureTests(TestResults results) : base(results) { }

    /// <summary>
    /// A nested raw-value enum whose cases are named <c>swift</c> and <c>system</c> constructs,
    /// parses and describes through the enclosing struct.
    /// </summary>
    public void TestNamespaceNamedEnumCasesRoundTrip()
    {
        using var renderer = new CaptureRenderer(CaptureRenderer.FormatKind.Swift);
        AssertEqual("render:swift", renderer.GetDescribe(), "the swift case reaches Swift");
        AssertEqual("swift", renderer.Format.RawValue, "the stored case reads back");

        var system = CaptureRenderer.Parse("system");
        AssertNotNull(system, "the system raw value parses");
        AssertEqual("system", system!.RawValue, "the parsed case keeps its raw value");
        AssertNull(CaptureRenderer.Parse("nope"), "an unknown raw value parses to nil");
    }

    /// <summary>
    /// Properties named after namespace roots and runtime or BCL helpers read their own values.
    /// </summary>
    public void TestCapturingPropertiesReadTheirValues()
    {
        using var host = new CaptureMemberHost();
        AssertEqual(8, host.Swift, "swift");
        AssertEqual(9, host.System, "system");
        AssertEqual(10, host.SwiftBindingsTestLib, "swiftBindingsTestLib");
        AssertEqual(11, host.Runtime, "runtime");
        AssertEqual(12, host.IntPtr, "intPtr");
        AssertEqual(13, host.NativeMemory, "nativeMemory");
        AssertEqual(14, host.Unsafe, "unsafe");
        AssertEqual(15, host.Marshal, "marshal");
        AssertEqual(16, host.MemoryMarshal, "memoryMarshal");
        AssertEqual(17, host.Math, "math");
        AssertEqual(18, host.Gc, "gc");
        AssertEqual(19, host.TaskValue, "task");
        AssertEqual(20, host.SwiftString, "swiftString");
        AssertEqual(21, host.SwiftObjectHelper, "swiftObjectHelper");
        AssertEqual(22, host.TypeMetadata, "typeMetadata");

        host.Swift = 30;
        AssertEqual(30, host.Swift, "a setter inside the capturing class writes through");
    }

    /// <summary>
    /// String, array, optional, closure, throwing and struct-argument members of the capturing
    /// class each marshal through their own runtime path.
    /// </summary>
    public void TestMarshallingPathsInsideTheCapturingClass()
    {
        using var host = new CaptureMemberHost();
        AssertEqual("echo:hi", host.Echo("hi"), "string round trip");

        var joined = host.Joined(new[] { "a", "b" });
        AssertEqual(3, joined.Count, "array round trip keeps every element");
        AssertEqual("end", joined[2], "array round trip appends on the Swift side");

        AssertEqual("some:x", host.Maybe("x"), "optional string with a value");
        AssertNull(host.Maybe(null), "optional string without a value");

        AssertEqual(22, host.Apply(v => v * 2), "closure argument is called with runtime (11)");

        AssertEqual(15, host._checked(false), "throwing member returns on success");
        AssertThrows<SwiftException>(() => host._checked(true), "throwing member surfaces the Swift error");

        using var renderer = new CaptureRenderer(CaptureRenderer.FormatKind.Png);
        AssertEqual("render:png", host.Render(renderer), "struct argument round trip");
    }

    /// <summary>
    /// Nested types named <c>Task</c> and <c>Action</c> come back from their host as the host's own
    /// types, not the BCL ones.
    /// </summary>
    public void TestNestedTypesNamedLikeBclTypes()
    {
        using var host = new CaptureMemberHost();
        using var task = host.GetNextTask();
        AssertEqual(5, task.Ticket, "the nested Task carries its field");
        using var action = host.GetNextAction();
        AssertEqual(6, action.Code, "the nested Action carries its field");
    }

    /// <summary>
    /// An async member of the capturing class completes with its value.
    /// </summary>
    public async Task TestAsyncMemberInsideTheCapturingClass()
    {
        using var host = new CaptureMemberHost();
        var sum = await WithTimeout(host.ComputeAsync(), DefaultAsyncTimeout);
        AssertEqual(17, sum, "swift (8) + system (9)");
    }

    /// <summary>
    /// A Swift conformer of a protocol whose extension declares members and typealiases named like
    /// standard-library names binds its requirements and the extension's members.
    /// </summary>
    public void TestSwiftConformerOfStdlibNamedProtocol()
    {
        using var impl = new CaptureStdlibNamesImpl(new[] { 1, 2 });
        AssertEqual(2, impl.GetCaptureValue(), "requirement reads the stored array");
        AssertEqual("impl:x", impl.CaptureLabel("x"), "string requirement");
        AssertEqual(0, impl.WithUnsafeBytes(), "extension member named withUnsafeBytes");
        AssertEqual(0, impl.GetUnsafeBitCast(), "extension member named unsafeBitCast");
        AssertEqual(0, impl.WithExtendedLifetime(), "extension member named withExtendedLifetime");

        AssertEqual("impl:x:1,2,2", Functions.CaptureStdlibNamesSummary(impl),
            "Swift drives every requirement of the Swift conformer");
    }

    /// <summary>
    /// A C# conformer passed to Swift is driven through the wrapper's conformer shim, which sits in
    /// an extension where the protocol extension's typealiases outrank the standard-library names.
    /// </summary>
    public void TestManagedConformerOfStdlibNamedProtocol()
    {
        var managed = new ManagedCaptureStdlibNames();
        AssertEqual("cs:x:1,7", Functions.CaptureStdlibNamesSummary(managed),
            "Swift reads, appends to and writes back the managed array, and calls both methods");
        AssertEqual(2, managed.CaptureItems.Count, "the setter received the appended array");
        AssertEqual(7, managed.CaptureItems[1], "the appended element is the managed value");
    }

    /// <summary>
    /// A struct with stored members named <c>max</c>, <c>min</c> and <c>print</c> constructs,
    /// computes and passes a generic value through.
    /// </summary>
    public void TestStdlibNamedStoredMembers()
    {
        using var host = new CaptureQualifierHost(9, 4, 7);
        AssertEqual(9, host.Max, "max");
        AssertEqual(4, host.Min, "min");
        AssertEqual(7, host.Print, "print");
        AssertEqual(5, host.GetSpan(), "max - min");
        AssertEqual("q:7", host.GetLabel(), "label reads print");
        AssertEqual(3, host.Pick(3), "generic value round trip");
        AssertEqual(2.5, host.Pick(2.5), "generic double round trip");
    }

    /// <summary>
    /// A nested class that subclasses a nested class its enclosing class inherits, named like a
    /// BCL collection, round-trips as the bound class and dispatches to its own override.
    /// </summary>
    public void TestInheritedNestedClassNamedLikeBclType()
    {
        using var player = new CaptureAppQueuePlayer();
        using var baseQueue = player.MakeQueue();
        AssertEqual(1, baseQueue.GetDepth(), "the base nested class");
        using var priority = player.MakePriorityQueue();
        AssertEqual(2, priority.GetDepth(), "the subclass of the inherited nested class overrides");
        CaptureQueuePlayer.Queue asBase = priority;
        AssertEqual(2, asBase.GetDepth(), "dispatch through the inherited nested class");
    }

    private sealed class ManagedCaptureStdlibNames : ICaptureStdlibNames
    {
        public IReadOnlyList<int> CaptureItems { get; set; } = new[] { 1 };
        public int GetCaptureValue() => 7;
        public string CaptureLabel(string text) => "cs:" + text;
    }
}
