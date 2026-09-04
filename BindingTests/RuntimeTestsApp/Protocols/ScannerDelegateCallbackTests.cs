// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// The reported scanner-delegate callback shape, end to end. A framework host holds its delegate
/// WEAKLY and calls back with two arrays of an associated-value enum whose cases carry a UUID
/// identity, a String, a frozen rect-like struct and a tracked class.
///
/// <para>
/// Three mechanisms have to hold at once for this to work, and each fails differently. The weak
/// slot means the C# conformer is reachable from Swift only through a non-retaining reference, so
/// it must stay rooted — this fixture CONSUMES that rooting rather than testing it. The array
/// parameters arrive through the object-marshalling arm. Each element then has to be projected out
/// of the enum payload, which is where a bitwise read of a borrowed slot reinterprets a String's
/// storage pointer or a class reference. And three requirements share the base name <c>host</c>,
/// distinguished only by argument labels, so the emitted C# names have to come from the labels.
/// </para>
///
/// <para>
/// Assertions are element-by-element against the fixture's OWN oracle
/// (<c>expectedScanItemDescription</c> / <c>deterministicScanIdentifier</c>) rather than
/// hand-transcribed literals, so a change to the fixture cannot leave the test asserting a stale
/// shape, and a truncated or reordered array cannot accidentally match.
/// </para>
/// </summary>
public class ScannerDelegateCallbackTests : TestBase
{
    public ScannerDelegateCallbackTests(TestResults results) : base(results) { }

    private const int AddSeed = 7;

    /// <summary>
    /// The core round trip: assign a C# delegate, hold it, fire <c>didAdd</c> with two non-empty
    /// arrays, and check every field of every element.
    /// </summary>
    public void TestDidAddDeliversEveryElementIntact()
    {
        var host = new ScannerHost();
        var del = new ScannerDelegateImpl();
        host.Delegate = del;

        AssertTrue(host.HasDelegate, "the weak delegate slot still resolves after assignment");

        var hostItemsAfter = host.EmitAdded(count: 2, seed: AddSeed);

        AssertTrue(del.DidAddCalled, "host(_:didAdd:allItems:) fired into the C# delegate");
        AssertEqual(2, del.LastAdded.Count, "both added elements arrived");
        AssertEqual(2, del.LastAllItems.Count, "allItems carried the full list, not just the added slice");

        AssertElementMatchesOracle(del.LastAdded[0], index: 0, seed: AddSeed, expectedKind: "text");
        AssertElementMatchesOracle(del.LastAdded[1], index: 1, seed: AddSeed, expectedKind: "barcode");

        AssertEqual(hostItemsAfter, string.Join(";", del.LastAllItems.Select(e => e.Description)),
            "the allItems array the delegate saw matches what the host still holds afterwards");

        GC.KeepAlive(del);
    }

    /// <summary>
    /// <c>didRemove</c>: the removed slice and the remaining list are genuinely different arrays,
    /// so a receiver that read the wrong slot would surface here rather than in <c>didAdd</c>,
    /// where both arrays happen to be equal.
    /// </summary>
    public void TestDidRemoveDeliversRemovedSliceAndRemainder()
    {
        var host = new ScannerHost();
        var del = new ScannerDelegateImpl();
        host.Delegate = del;

        host.EmitAdded(count: 3, seed: AddSeed);
        var remainingAfter = host.EmitRemovedFirst(count: 2);

        AssertTrue(del.DidRemoveCalled, "host(_:didRemove:allItems:) fired into the C# delegate");
        AssertEqual(2, del.LastRemoved.Count, "both removed elements arrived");
        AssertEqual(1, del.LastAllItems.Count, "allItems carried only the remainder");

        AssertElementMatchesOracle(del.LastRemoved[0], index: 0, seed: AddSeed, expectedKind: "text");
        AssertElementMatchesOracle(del.LastRemoved[1], index: 1, seed: AddSeed, expectedKind: "barcode");
        AssertElementMatchesOracle(del.LastAllItems[0], index: 2, seed: AddSeed, expectedKind: "text");

        AssertEqual(remainingAfter, string.Join(";", del.LastAllItems.Select(e => e.Description)),
            "the remainder the delegate saw matches what the host still holds");

        GC.KeepAlive(del);
    }

    /// <summary>
    /// <c>didTapOn</c>: ONE enum by value rather than an array, so the single-element borrowed-slot
    /// copy-out is covered beside the array path. Both enum cases are tapped, because a corrupted
    /// discriminator that always reports the first case would pass a one-case test.
    /// </summary>
    public void TestDidTapOnDeliversSingleElement()
    {
        var host = new ScannerHost();
        var del = new ScannerDelegateImpl();
        host.Delegate = del;

        host.EmitAdded(count: 2, seed: AddSeed);

        var tappedText = host.EmitTap(index: 0);
        AssertTrue(del.DidTapCalled, "host(_:didTapOn:) fired into the C# delegate");
        AssertNotNull(del.LastTapped, "the tapped element arrived");
        AssertEqual(tappedText, del.LastTapped!.Description,
            "the tapped element the delegate saw matches the host's own description of it");
        AssertElementMatchesOracle(del.LastTapped!, index: 0, seed: AddSeed, expectedKind: "text");

        var tappedBarcode = host.EmitTap(index: 1);
        AssertEqual(tappedBarcode, del.LastTapped!.Description,
            "the second tapped element also round-trips");
        AssertElementMatchesOracle(del.LastTapped!, index: 1, seed: AddSeed, expectedKind: "barcode");

        GC.KeepAlive(del);
    }

    /// <summary>
    /// The weak slot must not be what keeps the delegate alive, and it must not go stale while the
    /// test still holds the conformer: a GC in the middle of a delegate's life must leave the host
    /// able to call it.
    /// </summary>
    public void TestWeakDelegateSurvivesCollectionWhileHeld()
    {
        var host = new ScannerHost();
        var del = new ScannerDelegateImpl();
        host.Delegate = del;

        ComplexSlotParamDispatchTests.DrainFinalizers();

        AssertTrue(host.HasDelegate, "the weak delegate slot still resolves after a full GC");
        host.EmitAdded(count: 1, seed: AddSeed);
        AssertEqual(1, del.LastAdded.Count, "the host could still call the delegate after a full GC");

        GC.KeepAlive(del);
    }

    /// <summary>
    /// Repeated dispatch of the SAME two elements. Swift builds the batch once and hands the same
    /// two items to every callback, so the tracker must see exactly two allocations no matter how
    /// many dispatches ran — a receiver that rebuilt the elements per callback shows up as growth
    /// there rather than as a leak.
    ///
    /// <para>
    /// The retain half is what the release assertion measures. The conformer disposes every wrapper
    /// the callback hands it, so each copy-out's <c>+1</c> has exactly one matching release; an
    /// over-retaining copy-out leaves the marker alive past the drive even though every wrapper was
    /// disposed, and an under-retaining one corrupts the descriptions the loop re-reads and trips the
    /// mismatch check inside the driver instead.
    /// </para>
    /// </summary>
    public void TestRepeatedDelegateCallbacksDoNotLeak()
    {
        ComplexSlotParamDispatchTests.DrainFinalizers();
        LifetimeTracker.Reset();

        DriveDelegateCallbacks(100);

        var (allocations, _, _) = LifetimeTracker.GetStats();
        AssertTrue(allocations >= 2,
            "positive control: the tracked markers inside the payloads really did reach the tracker");
        AssertEqual(2, allocations,
            "100 dispatches of one two-element batch allocate the two tracked markers ONCE — a "
            + "per-callback re-materialization would scale with the loop");

        ComplexSlotParamDispatchTests.DrainFinalizers();
        LifetimeTracker.AssertNoLeaks(
            "repeated scanner-delegate callbacks must not strand the copied-out array elements");
        TestLogger.Info("scanner delegate: 100 didAdd callbacks over 2 payload elements, balanced");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DriveDelegateCallbacks(int n)
    {
        var host = new ScannerHost();
        var del = new ScannerDelegateImpl();
        host.Delegate = del;

        var last = host.EmitAddedRepeatedly(iterations: n, seed: AddSeed);
        if (last != string.Join(";", del.LastAdded.Select(e => e.Description)))
        {
            throw new InvalidOperationException(
                $"scanner payload corrupted after {n} callbacks: host says '{last}', "
                + $"delegate saw '{string.Join(";", del.LastAdded.Select(e => e.Description))}'");
        }

        GC.KeepAlive(del);
        host.Delegate = null;
    }

    /// <summary>
    /// Checks one received element against the fixture's own oracle, field by field: case,
    /// identity, String payload, all four doubles of the frozen rect, and the tracked marker.
    /// </summary>
    private void AssertElementMatchesOracle(ScannerDelegateImpl.Element element, int index, int seed, string expectedKind)
    {
        int serial = seed * 100 + index;

        AssertEqual(TestLibFunctions.ExpectedScanItemDescription(index, seed), element.Description,
            $"element {index} matches the fixture's own description oracle");
        AssertEqual(expectedKind, element.Kind, $"element {index} arrived as the '{expectedKind}' case");
        AssertEqual(TestLibFunctions.DeterministicScanIdentifier(serial), element.Id,
            $"element {index} UUID round-tripped as a System.Guid");
        AssertEqual(TestLibFunctions.DeterministicScanIdentifierString(serial), element.IdentifierString,
            $"element {index} UUID still renders canonically Swift-side");
        AssertEqual(expectedKind == "text" ? $"text-{serial}" : $"code-{serial}", element.Label,
            $"element {index} String payload round-tripped");
        AssertApproxEqual(serial + 0.25, element.X, 1e-9, $"element {index} bounds.x");
        AssertApproxEqual(serial + 0.5, element.Y, 1e-9, $"element {index} bounds.y");
        AssertApproxEqual(serial + 0.75, element.Width, 1e-9, $"element {index} bounds.width");
        AssertApproxEqual(serial + 1.5, element.Height, 1e-9, $"element {index} bounds.height");
        AssertEqual(serial, element.MarkerSerial, $"element {index} tracked marker survived the copy-out");
    }
}

/// <summary>
/// C# implementation of the generated <c>IScannerHostDelegate</c>. Each callback flattens every
/// received element into plain managed data immediately, while the borrowed arrays are still
/// alive, and keeps no reference to the Swift wrappers afterwards — so the leak probe can watch
/// them drain, and a later assertion cannot accidentally read a value the copy-out only appeared
/// to produce.
/// </summary>
internal sealed class ScannerDelegateImpl : IScannerHostDelegate
{
    /// <summary>One received element, fully projected into managed values.</summary>
    internal sealed record Element(
        string Description,
        string Kind,
        Guid Id,
        string IdentifierString,
        string Label,
        double X,
        double Y,
        double Width,
        double Height,
        int MarkerSerial);

    public bool DidAddCalled { get; private set; }
    public bool DidRemoveCalled { get; private set; }
    public bool DidTapCalled { get; private set; }

    public IReadOnlyList<Element> LastAdded { get; private set; } = Array.Empty<Element>();
    public IReadOnlyList<Element> LastRemoved { get; private set; } = Array.Empty<Element>();
    public IReadOnlyList<Element> LastAllItems { get; private set; } = Array.Empty<Element>();
    public Element? LastTapped { get; private set; }

    public void HostDidAddAllItems(ScannerHost host, IEnumerable<ScanItem> added, IEnumerable<ScanItem> allItems)
    {
        DidAddCalled = true;
        LastAdded = Project(added);
        LastAllItems = Project(allItems);
    }

    public void HostDidRemoveAllItems(ScannerHost host, IEnumerable<ScanItem> removed, IEnumerable<ScanItem> allItems)
    {
        DidRemoveCalled = true;
        LastRemoved = Project(removed);
        LastAllItems = Project(allItems);
    }

    public void HostDidTapOn(ScannerHost host, ScanItem item)
    {
        DidTapCalled = true;
        LastTapped = Project(item);

        // The single-element requirement hands over a copy the receiver made out of the borrowed
        // slot, so this side owns it — the array path disposes its elements for the same reason.
        item.Dispose();
    }

    /// <summary>
    /// Projects every element, disposing each one. Enumerating a Swift array hands the consumer an
    /// INDEPENDENT wrapper per element (the subscript copies the element out at <c>+1</c>), and the
    /// array wrapper the receiver built for this parameter owns a <c>+1</c> of its own — so this side
    /// owns both and has to release them. Dropping them on the floor is not wrong, but it defers
    /// every release to finalization, which would make the leak probe a test of GC timing instead of
    /// a test of the copy-out's retain balance.
    /// </summary>
    private static IReadOnlyList<Element> Project(IEnumerable<ScanItem> items)
    {
        var result = new List<Element>();
        foreach (var item in items)
        {
            result.Add(Project(item));
            item.Dispose();
        }

        if (items is IDisposable disposableArray)
            disposableArray.Dispose();

        return result;
    }

    /// <summary>
    /// Reads the element through BOTH routes: the Swift free-function readers (which prove Swift
    /// can still make sense of the value the receiver copied out) and the generated case
    /// accessors, which give the frozen rect and the UUID as managed values.
    /// </summary>
    private static Element Project(ScanItem item)
    {
        string description = TestLibFunctions.DescribeScanItem(item);
        string kind = TestLibFunctions.ScanItemKind(item);
        string identifierString = TestLibFunctions.ScanItemIdentifier(item);
        string label = TestLibFunctions.ScanItemLabel(item);
        int markerSerial = TestLibFunctions.ScanItemMarkerSerial(item);

        Guid id;
        ScanBounds bounds;
        // The case accessors extract the payload into a wrapper this side owns, so each one is
        // disposed as soon as its fields have been read out into managed values.
        if (item.TryGetText(out var text))
        {
            using (text)
            {
                id = text.Id;
                bounds = text.Bounds;
            }
        }
        else if (item.TryGetBarcode(out var barcode))
        {
            using (barcode)
            {
                id = barcode.Id;
                bounds = barcode.Bounds;
            }
        }
        else
        {
            throw new InvalidOperationException($"ScanItem arrived as neither case: '{description}'");
        }

        return new Element(
            description,
            kind,
            id,
            identifierString,
            label,
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            markerSerial);
    }
}
