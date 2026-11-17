// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Swift;
using Foundation;
using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Closures;

/// <summary>
/// Runtime gate for the reverse callbacks of <c>ReverseCallbackShapes.swift</c>: Swift invokes each
/// managed delegate once while the call that handed it over runs and once after that call returned,
/// with wide values, a throwing callback and receivers in the self register.
/// </summary>
/// <remarks>
/// <para>The <c>Result</c> completions beside a <see cref="NSUrl"/> take the closure <c>@_cdecl</c>
/// adapter and must work on every engine.</para>
/// <para>The optional-wide relay and the value type's stored closures stay on the Swift-convention
/// lane. Each such member is guarded for the reverse callback and for its own forward call, which a
/// bridge carries on iOS, Mac Catalyst and tvOS. Where the engine runs both, the values must cross
/// intact; where it refuses either, the member must throw before Swift runs and name itself.</para>
/// </remarks>
public class ReverseCallbackShapesTests : TestBase
{
    public ReverseCallbackShapesTests(TestResults results) : base(results) { }

    // A Swift-lane member's own forward call: an instance member whose receiver rides the self register.
    private const SwiftTransitionFacts ForwardShapes = SwiftTransitionFacts.SelfRegister | SwiftTransitionFacts.LoweredSignature;

    /// <summary>What a Swift-lane member's guard asks of this engine: the callback, and the forward call where no bridge carries it.</summary>
    private static SwiftTransitionFacts SwiftLaneShapes =>
        SwiftTransitionFacts.ReverseCallback | (ImportRoutes.BridgesRouted ? SwiftTransitionFacts.None : ForwardShapes);

    private const string Url = "https://example.com/items/alpha.json";

    /// <summary>
    /// Runs <paramref name="call"/>, which must work where the engine runs a Swift-lane member and
    /// must throw before Swift runs, naming <paramref name="member"/>, everywhere else.
    /// </summary>
    private void RunSwiftLane(string member, Action call)
    {
        if (SwiftTransitionGuard.IsSupported(SwiftLaneShapes))
        {
            call();
            return;
        }
        SwiftTransitionUnsupportedException? refusal = null;
        try
        {
            call();
        }
        catch (SwiftTransitionUnsupportedException ex)
        {
            refusal = ex;
        }
        AssertNotNull(refusal, $"{member} must refuse on {SwiftTransitionGuard.UnsupportedFacts}");
        AssertTrue(refusal!.Member.Contains(member, StringComparison.Ordinal), $"refusal names the member: {refusal.Member}");
        AssertTrue((refusal.RefusedFacts & SwiftLaneShapes) != 0, $"refusal names a shape the member has: {refusal.RefusedFacts}");
        TestLogger.Info($"{member}: refused before Swift ran ({refusal.RefusedFacts})");
    }

    /// <summary>
    /// Asserts that the callback named <paramref name="prefix"/> on <paramref name="declaringType"/>
    /// exists once and is an <c>[UnmanagedCallersOnly]</c> method with <paramref name="convention"/>,
    /// so each test knows which lane it exercises.
    /// </summary>
    private static void AssertCallbackLane(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicMethods)] System.Type declaringType,
        string prefix,
        System.Type convention)
    {
        var callbacks = declaringType
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m.Name.StartsWith(prefix + "_", StringComparison.Ordinal) && m.Name.EndsWith("_Callback", StringComparison.Ordinal))
            .ToArray();
        if (callbacks.Length != 1)
            throw new AssertionException($"{declaringType.Name}.{prefix}: expected one callback, got {callbacks.Length}");
        var conventions = callbacks[0].GetCustomAttribute<UnmanagedCallersOnlyAttribute>()?.CallConvs ?? Array.Empty<System.Type>();
        if (conventions.Length != 1 || conventions[0] != convention)
            throw new AssertionException(
                $"{callbacks[0].Name}: expected [UnmanagedCallersOnly] with {convention.Name}, got [{string.Join(", ", conventions.Select(c => c.Name))}]");
    }

    private static ReverseCallbackWide Wide(int first, string label) => new(first, first + 1, first + 2, label);

    private void AssertWide(ReverseCallbackWide? actual, int first, string label, string context)
    {
        AssertNotNull(actual, $"{context}: a value arrives");
        AssertEqual(first, actual!.First, $"{context}: first");
        AssertEqual(first + 1, actual.Second, $"{context}: second");
        AssertEqual(first + 2, actual.Third, $"{context}: third");
        AssertEqual(label, actual.Label, $"{context}: label");
    }

    // MARK: Result completions (closure @_cdecl adapter)

    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicMethods, typeof(ReverseCallbackLoader))]
    public void TestResultCompletionBesideUrl_CompletesBeforeReturning()
    {
        ImportRoutes.AssertCdeclWrapper(typeof(ReverseCallbackLoader), "load");
        AssertCallbackLane(typeof(ReverseCallbackLoader), "load_completion", typeof(System.Runtime.CompilerServices.CallConvCdecl));
        using var loader = new ReverseCallbackLoader();
        using var url = new NSUrl(Url);

        var calls = 0;
        (int First, int Second, int Third, string Label)? success = null;
        loader.Load(url, 0, result =>
        {
            calls++;
            AssertTrue(result.IsSuccess, "code 0 succeeds");
            var value = result.Success;
            success = (value.First, value.Second, value.Third, value.Label);
        });
        AssertEqual(1, calls, "the completion ran once before Load returned");
        AssertEqual((1, 2, 3, "alpha.json"), success!.Value, "the wide success crossed intact");

        nint? rejected = null;
        loader.Load(url, 7, result =>
        {
            calls++;
            AssertTrue(result.IsFailure, "a non-zero code fails");
            AssertTrue(result.Failure.TryGetRejected(out var code), "the failure is .rejected");
            rejected = code;
        });
        AssertEqual(2, calls, "the failing completion ran once");
        AssertEqual((nint)7, rejected!.Value, "the failure's code crossed intact");
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicMethods, typeof(ReverseCallbackLoader))]
    public void TestResultCompletionBesideUrl_CompletesAfterReturning()
    {
        ImportRoutes.AssertCdeclWrapper(typeof(ReverseCallbackLoader), "loadLater");
        ImportRoutes.AssertCdeclWrapper(typeof(ReverseCallbackLoader), "loadNodeLater");
        AssertCallbackLane(typeof(ReverseCallbackLoader), "loadLater_completion", typeof(System.Runtime.CompilerServices.CallConvCdecl));
        AssertCallbackLane(typeof(ReverseCallbackLoader), "loadNodeLater_completion", typeof(System.Runtime.CompilerServices.CallConvCdecl));
        using var loader = new ReverseCallbackLoader();
        using var url = new NSUrl(Url);
        using var nodeUrl = new NSUrl("https://example.com/nodes/beta");
        using var rootUrl = new NSUrl("file:///");

        var events = new List<string>();
        loader.LoadLater(url, 0, r => events.Add(r.IsSuccess ? $"ok:{r.Success.First}:{r.Success.Label}" : "unexpected failure"));
        loader.LoadLater(url, 9, r => events.Add(r.IsFailure && r.Failure.TryGetRejected(out var c) ? $"rejected:{c}" : "unexpected success"));
        loader.LoadNodeLater(nodeUrl, r => events.Add(r.IsSuccess ? $"node:{r.Success.Name}" : "unexpected node failure"));
        loader.LoadNodeLater(rootUrl, r => events.Add(r.IsFailure ? "node failed" : "unexpected node success"));
        AssertEqual(0, events.Count, "nothing completes before finish()");

        // Every delegate is only reachable through Swift's copy of it now.
        GC.Collect();
        GC.WaitForPendingFinalizers();

        AssertEqual((nint)4, loader.Finish(), "finish() completed every held load");
        AssertEqual("ok:1:alpha.json|rejected:9|node:beta|node failed", string.Join("|", events), "each completion ran once, in order, with its values");
        AssertEqual((nint)0, loader.Finish(), "nothing is held twice");
    }

    // MARK: Optional wide argument (Swift-convention lane)

    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicMethods, typeof(ReverseCallbackRelay))]
    public void TestOptionalWideArgument_InvokedBeforeReturning()
    {
        AssertCallbackLane(typeof(ReverseCallbackRelay), "deliver_to", typeof(CallConvSwift));
        using var relay = new ReverseCallbackRelay();
        using var wide = Wide(10, "now");

        RunSwiftLane("ReverseCallbackRelay.Deliver", () =>
        {
            var seen = new List<string>();
            relay.Deliver(wide, v => { AssertWide(v, 10, "now", "a present value"); seen.Add("value"); });
            relay.Deliver(null, v => seen.Add(v is null ? "null" : "unexpected value"));
            AssertEqual("value|null", string.Join("|", seen), "each callback ran once before Deliver returned");
        });
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicMethods, typeof(ReverseCallbackRelay))]
    public void TestOptionalWideArgument_InvokedAfterReturning()
    {
        AssertCallbackLane(typeof(ReverseCallbackRelay), "deliverLater_to", typeof(CallConvSwift));
        using var relay = new ReverseCallbackRelay();
        using var first = Wide(20, "later-one");
        using var second = Wide(30, "later-two");

        RunSwiftLane("ReverseCallbackRelay.DeliverLater", () =>
        {
            var seen = new List<string>();
            relay.DeliverLater(first, v => seen.Add(v is null ? "null" : $"{v.First}/{v.Third}/{v.Label}"));
            relay.DeliverLater(null, v => seen.Add(v is null ? "null" : "unexpected"));
            relay.DeliverLater(second, v => seen.Add(v is null ? "null" : $"{v.First}/{v.Third}/{v.Label}"));
            AssertEqual(0, seen.Count, "nothing runs before flush()");

            GC.Collect();
            GC.WaitForPendingFinalizers();

            AssertEqual((nint)3, relay.Flush(), "flush() ran every held callback");
            AssertEqual("20/22/later-one|null|30/32/later-two", string.Join("|", seen), "each callback ran once, in order, with its value");
        });
    }

    // MARK: Stored closures on a value type (Swift-convention lane, receiver in the self register)

    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicMethods, typeof(ReverseCallbackSettings))]
    public void TestStoredTransform_InvokedAfterTheSetterReturned()
    {
        AssertCallbackLane(typeof(ReverseCallbackSettings), "transform_Set_value", typeof(CallConvSwift));
        using var settings = new ReverseCallbackSettings();
        AssertEqual((nint)5, settings.Apply(5), "the default transform is the identity");

        RunSwiftLane("ReverseCallbackSettings.Transform", () =>
        {
            var calls = 0;
            settings.Transform = x => { calls++; return x * 3 + 1; };
            GC.Collect();
            GC.WaitForPendingFinalizers();
            AssertEqual((nint)16, settings.Apply(5), "Swift ran the stored delegate");
            AssertEqual((nint)(-2), settings.Apply(-1), "and again with another value");
            AssertEqual(2, calls, "once per apply");
        });
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicMethods, typeof(ReverseCallbackSettings))]
    public void TestStoredProvider_FillsSwiftsIndirectResult()
    {
        AssertCallbackLane(typeof(ReverseCallbackSettings), "provider_Set_value", typeof(CallConvSwift));
        using var settings = new ReverseCallbackSettings();
        AssertTrue(settings.Provide() is null, "the default provider returns nil");

        RunSwiftLane("ReverseCallbackSettings.Provider", () =>
        {
            var next = 40;
            settings.Provider = () => Wide(next, $"provided-{next}");
            for (var i = 0; i < 3; i++)
            {
                using var provided = settings.Provide();
                AssertWide(provided, next, $"provided-{next}", $"provide #{i}");
                next += 10;
            }

            settings.Provider = () => null;
            AssertTrue(settings.Provide() is null, "a provider returning null reaches Swift as nil");
        });
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicMethods, typeof(ReverseCallbackSettings))]
    public void TestStoredFactory_ReturnsAnExistential()
    {
        AssertCallbackLane(typeof(ReverseCallbackSettings), "makeEncoder_Set_value", typeof(CallConvSwift));
        using var settings = new ReverseCallbackSettings();
        AssertEqual((nint)13, settings.Encode(10, 3), "the default factory is Swift's own offset encoding");

        RunSwiftLane("ReverseCallbackSettings.MakeEncoder", () =>
        {
            var offsets = new List<nint>();
            settings.MakeEncoder = offset => { offsets.Add(offset); return new ScalingEncoding(offset); };
            AssertEqual((nint)30, settings.Encode(10, 3), "Swift encoded through the managed conformer");
            AssertEqual((nint)(-14), settings.Encode(-7, 2), "and again with another factory argument");
            AssertEqual("3,2", string.Join(",", offsets), "the factory ran once per encode with Swift's argument");
        });
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicMethods, typeof(ReverseCallbackSettings))]
    public void TestStoredThrowingValidator_ReportsSuccessAndError()
    {
        AssertCallbackLane(typeof(ReverseCallbackSettings), "validate_Set_value", typeof(CallConvSwift));
        using var settings = new ReverseCallbackSettings();
        AssertEqual((nint)4, settings.Check(4), "the default validator passes the value through");

        RunSwiftLane("ReverseCallbackSettings.Validate", () =>
        {
            settings.Validate = x => x < 0
                ? throw new InvalidOperationException("negative")
                : Swift.SwiftResult<nint, SwiftError>.FromSuccess(x * 2);
            AssertEqual((nint)8, settings.Check(4), "a success crosses back");
            AssertEqual((nint)(-2), settings.Check(-1), "a managed exception reaches Swift as a thrown error");
            AssertEqual((nint)12, settings.Check(6), "and the callback still works after an error");
        });
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicMethods, typeof(ReverseCallbackSettings))]
    public void TestStoredThrowingLookup_FillsSwiftsIndirectResult()
    {
        // A throwing callback whose wide optional result Swift receives by address: the value, nil,
        // and an error all have to cross, and the callback must keep working after each.
        AssertCallbackLane(typeof(ReverseCallbackSettings), "lookup_Set_value", typeof(CallConvSwift));
        using var settings = new ReverseCallbackSettings();
        AssertEqual("nil", settings.Find(1), "the default lookup finds nothing");

        RunSwiftLane("ReverseCallbackSettings.Lookup", () =>
        {
            settings.Lookup = key => key < 0
                ? throw new InvalidOperationException("negative")
                : Swift.SwiftResult<ReverseCallbackWide?, SwiftError>.FromSuccess(key == 0 ? null : Wide((int)key, $"found-{key}"));
            AssertEqual("7|8|9|found-7", settings.Find(7), "a present value crosses back");
            AssertEqual("nil", settings.Find(0), "a null result reaches Swift as nil");
            AssertEqual("error", settings.Find(-1), "a managed exception reaches Swift as a thrown error");
            AssertEqual("11|12|13|found-11", settings.Find(11), "and the callback still works after an error");
        });
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicMethods, typeof(ReverseCallbackSettings))]
    public void TestStoredClassFactory_HandsSwiftAnOwnedReference()
    {
        // Swift owns a closure's class result and releases it when done. One managed node returned
        // over and over must survive every release, and so must the managed wrapper that holds it.
        AssertCallbackLane(typeof(ReverseCallbackSettings), "makeNode_Set_value", typeof(CallConvSwift));
        using var settings = new ReverseCallbackSettings();
        AssertEqual("swift-0,swift-1", settings.MakeNodes(2), "the default factory runs in Swift");

        var shared = new ReverseCallbackNode("shared");
        RunSwiftLane("ReverseCallbackSettings.MakeNode", () =>
        {
            settings.MakeNode = index => index == 1 ? new ReverseCallbackNode($"fresh-{index}") : shared;
            for (var round = 0; round < 3; round++)
            {
                AssertEqual("shared,fresh-1,shared", settings.MakeNodes(3), $"round {round}: every node reaches Swift");
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            AssertEqual("shared", shared.Name, "the managed node is still alive after Swift released its copies");
        });
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicMethods, typeof(ReverseCallbackSettings))]
    public void TestStoredThrowingClassLookup_HandsSwiftAnOwnedReference()
    {
        // The throwing twin: a node and an error both cross, and the managed node survives Swift
        // releasing every copy it was handed.
        AssertCallbackLane(typeof(ReverseCallbackSettings), "findNode_Set_value", typeof(CallConvSwift));
        using var settings = new ReverseCallbackSettings();
        AssertEqual("swift-1", settings.DescribeNode(1), "the default lookup runs in Swift");

        var shared = new ReverseCallbackNode("kept");
        RunSwiftLane("ReverseCallbackSettings.FindNode", () =>
        {
            settings.FindNode = key => key < 0
                ? throw new InvalidOperationException("negative")
                : Swift.SwiftResult<ReverseCallbackNode, SwiftError>.FromSuccess(shared);
            for (var round = 0; round < 3; round++)
            {
                AssertEqual("kept", settings.DescribeNode(5), $"round {round}: the node crosses back");
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            AssertEqual("error", settings.DescribeNode(-1), "a managed exception reaches Swift as a thrown error");
            AssertEqual("kept", settings.DescribeNode(6), "and the callback still works after an error");
            AssertEqual("kept", shared.Name, "the managed node is still alive after Swift released its copies");
        });
    }

    /// <summary>A managed conformer Swift receives from the stored factory.</summary>
    private sealed class ScalingEncoding(nint factor) : IReverseCallbackEncoding
    {
        public nint Encode(nint value) => value * factor;
    }
}
