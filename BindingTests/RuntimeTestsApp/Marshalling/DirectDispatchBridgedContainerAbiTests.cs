// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Pins the ABI floor for ObjC-bridged containers on the DIRECT CallConvSwift arm — the
/// container-level sibling of the bridged-value-type refusal in
/// <see cref="OptionalMarshallingTests"/>.
///
/// <para>A <c>[URL]</c>, <c>[String: URL]</c> or <c>Set&lt;URL&gt;</c> crosses to Swift as an
/// NSArray / NSDictionary / NSSet handle, which is the right value only at a <c>@_cdecl</c>
/// boundary that bridges it back. On Swift's own entry point the slot wants native container
/// storage, so the member is refused: the body throws instead of handing Swift a foreign object
/// (or, on the return side, reading Swift's storage back as one). Optionality of the container
/// changes nothing about that, so the bare and optional spellings are asserted alike, and the
/// wrapper-arm control at the end asserts the refusal does not reach past the direct arm.</para>
///
/// <para>Accessors are refused without a declaration marker — one on the private synthesized
/// accessor would stop the public indexer compiling — so the indexer reads below need no
/// pragma while the method and initializer calls do.</para>
/// </summary>
public class DirectDispatchBridgedContainerAbiTests : TestBase
{
    public DirectDispatchBridgedContainerAbiTests(TestResults results) : base(results) { }

    private static DirectBridgedContainerHost.BridgedMarker Stamp(int value)
        => new DirectBridgedContainerHost.BridgedMarker(value);

    private static Foundation.NSUrl Url(string s) => Foundation.NSUrl.FromString(s)!;

#pragma warning disable SB0009 // Tombstoned by the ABI floor — throwing is the behavior under test.

    public void TestDirectPathOptionalBridgedArrayInitializerIsRefused()
    {
        // Both arms of the optional are refused: the present case would hand Swift an NSArray
        // handle, and the absent case is the same member with the same missing boundary.
        var urls = new[] { Url("https://first.example.com/path"), Url("https://second.example.com/path") };
        AssertThrows<NotSupportedException>(
            () => { using var host = new DirectBridgedContainerHost(urls, Stamp(1)); },
            "[URL]? initializer on the direct path throws instead of passing an NSArray to Swift");
        AssertThrows<NotSupportedException>(
            () => { using var host = new DirectBridgedContainerHost((IReadOnlyList<Foundation.NSUrl>?)null, Stamp(2)); },
            "[URL]? initializer on the direct path throws for the nil arm too");
        TestLogger.Info("DirectBridgedContainerHost(urls:) threw NotSupportedException on both arms");
    }

    public void TestDirectPathOptionalBridgedDictionaryInitializerIsRefused()
    {
        var lookup = new Dictionary<string, Foundation.NSUrl>
        {
            ["alpha"] = Url("https://alpha.example.com/path"),
        };
        AssertThrows<NotSupportedException>(
            () => { using var host = new DirectBridgedContainerHost(lookup, Stamp(3)); },
            "[String: URL]? initializer on the direct path throws instead of passing an NSDictionary to Swift");
        AssertThrows<NotSupportedException>(
            () => { using var host = new DirectBridgedContainerHost((IReadOnlyDictionary<string, Foundation.NSUrl>?)null, Stamp(4)); },
            "[String: URL]? initializer on the direct path throws for the nil arm too");
        TestLogger.Info("DirectBridgedContainerHost(lookup:) threw NotSupportedException on both arms");
    }

    public void TestDirectPathOptionalBridgedSetInitializerIsRefused()
    {
        var unique = new HashSet<Foundation.NSUrl> { Url("https://one.example.com/path") };
        AssertThrows<NotSupportedException>(
            () => { using var host = new DirectBridgedContainerHost(unique, Stamp(5)); },
            "Set<URL>? initializer on the direct path throws instead of passing an NSSet to Swift");
        AssertThrows<NotSupportedException>(
            () => { using var host = new DirectBridgedContainerHost((IReadOnlySet<Foundation.NSUrl>?)null, Stamp(6)); },
            "Set<URL>? initializer on the direct path throws for the nil arm too");
        TestLogger.Info("DirectBridgedContainerHost(unique:) threw NotSupportedException on both arms");
    }

    public void TestDirectPathBareBridgedArrayMethodIsRefused()
    {
        // The bare spelling is the ordinary one, and the one a floor keyed on Optional would
        // miss. Its slot is exactly one word wide; the value in it is what is wrong.
        var urls = new[] { Url("https://bare.example.com/path") };
        AssertThrows<NotSupportedException>(
            () => { _ = DirectBridgedContainerHost.BorrowedCount(urls, Stamp(7)); },
            "bare [URL] parameter on the direct path throws instead of passing an NSArray to Swift");
        TestLogger.Info("DirectBridgedContainerHost.BorrowedCount threw NotSupportedException");
    }

    public void TestDirectPathBareBridgedSetMethodIsRefused()
    {
        var unique = new HashSet<Foundation.NSUrl> { Url("https://bare-set.example.com/path") };
        AssertThrows<NotSupportedException>(
            () => { _ = DirectBridgedContainerHost.BorrowedUnique(unique, Stamp(8)); },
            "bare Set<URL> parameter on the direct path throws instead of passing an NSSet to Swift");
        TestLogger.Info("DirectBridgedContainerHost.BorrowedUnique threw NotSupportedException");
    }

#pragma warning restore SB0009

    public void TestDirectPathBareBridgedArraySubscriptIsRefusedOnBothAccessors()
    {
        // The host itself constructs — its initializer takes only the frozen marker — so the
        // refusal is observable on the accessors alone. No pragma: accessor-side tombstones carry
        // no declaration marker, and this asserts the indexer still refuses without one.
        using var slot = new DirectBridgedSlotHost(Stamp(9));
        AssertThrows<NotSupportedException>(
            () => { _ = slot[Stamp(9)]; },
            "[URL] subscript getter on the direct path throws instead of reading Swift storage as an NSArray");
        AssertThrows<NotSupportedException>(
            () => { slot[Stamp(9)] = new[] { Url("https://slot.example.com/path") }; },
            "[URL] subscript setter on the direct path throws instead of passing an NSArray to Swift");
        TestLogger.Info("DirectBridgedSlotHost indexer threw NotSupportedException on get and set");
    }

    public void TestDirectPathBareBridgedDictionarySubscriptIsRefusedOnBothAccessors()
    {
        using var lookup = new DirectBridgedLookupHost(Stamp(10));
        AssertThrows<NotSupportedException>(
            () => { _ = lookup[Stamp(10)]; },
            "[String: URL] subscript getter on the direct path throws instead of reading Swift storage as an NSDictionary");
        AssertThrows<NotSupportedException>(
            () => { lookup[Stamp(10)] = new Dictionary<string, Foundation.NSUrl> { ["k"] = Url("https://lookup.example.com/path") }; },
            "[String: URL] subscript setter on the direct path throws instead of passing an NSDictionary to Swift");
        TestLogger.Info("DirectBridgedLookupHost indexer threw NotSupportedException on get and set");
    }

    public void TestWrapperPathBareBridgedArrayStillBinds()
    {
        // Over-breadth control. The same bare [URL] parameter with no frozen-struct sibling is
        // wrapper-eligible, so it crosses the @_cdecl boundary the NSArray rendering is correct
        // at. If the floor ever fires on "is a bridged container" without asking whether a
        // wrapper is present, this is the call that starts throwing.
        var urls = new[] { Url("https://live.example.com/a"), Url("https://live.example.com/b"), Url("https://live.example.com/c") };
        AssertEqual(3, DirectBridgedContainerHost.LiveCount(urls),
            "bare [URL] parameter through the @_cdecl wrapper binds and counts its elements");
        TestLogger.Info("DirectBridgedContainerHost.LiveCount answered through the wrapper arm");
    }
}
