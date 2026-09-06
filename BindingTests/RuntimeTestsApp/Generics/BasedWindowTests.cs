// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// <c>BasedWindow&lt;Element&gt;</c> takes both an element array and an <c>Int</c> through the
/// same initializer. The <c>Int</c> is what earns the type a native-int convenience overload,
/// and the array is what that overload has to name: it forwards to the pointer-width member, so
/// its declaration repeats every parameter it does not narrow. Deriving those types on its own —
/// without the enclosing type's generic context in scope — spells a placeholder instead of the
/// element container, and the binding stops compiling over a member that is only sugar.
///
/// Compilation is therefore half of what these tests assert: the file they live in would not
/// build if the overload named the wrong type. The rest checks that both entry points carry a
/// value to the same place, and that the base still lands where the collection projection
/// expects to find it.
/// </summary>
public class BasedWindowTests : TestBase
{
    public BasedWindowTests(TestResults results) : base(results) { }

    public void TestBasedWindow_ConstructedFromManagedCode_CarriesTheBaseItWasGiven()
    {
        using var first = new CollectibleCoin(collectibleId: "alpha");
        using var second = new CollectibleCoin(collectibleId: "beta");

        // An idiomatic 32-bit literal for the base — the case the convenience overload exists
        // for — alongside a managed sequence for the elements.
        using var window = new BasedWindow<CollectibleCoin>(
            new List<CollectibleCoin> { first, second }, 40);

        AssertEqual(40, window.WindowBase, "BasedWindow base through the managed constructor");
        AssertEqual(2, window.Count, "BasedWindow element count");
    }

    public void TestBasedWindow_ManagedAndSwiftConstructionAgree()
    {
        // The same window built on either side of the boundary. Nothing about the base should
        // depend on which entry point it entered through.
        using var first = new CollectibleCoin(collectibleId: "alpha");
        using var second = new CollectibleCoin(collectibleId: "beta");
        using var managed = new BasedWindow<CollectibleCoin>(
            new List<CollectibleCoin> { first, second }, 40);
        using var fromSwift = Functions.MakeBasedWindow(
            firstId: "alpha", secondId: "beta", @base: 40);

        AssertEqual(managed.WindowBase, fromSwift.WindowBase, "BasedWindow base, both directions");

        IReadOnlyList<CollectibleCoin> managedView = managed;
        IReadOnlyList<CollectibleCoin> swiftView = fromSwift;
        AssertEqual(managedView.Count, swiftView.Count, "BasedWindow count, both directions");

        // Position 0 is the first element on either one: the base is a native index, and the
        // projection owes the consumer a zero-based view regardless of where it starts.
        AssertEqual("alpha", managedView[0].CollectibleId, "BasedWindow first element, managed");
        AssertEqual("alpha", swiftView[0].CollectibleId, "BasedWindow first element, from Swift");
    }

    public void TestBasedWindow_BaseAboveIntRange_ReadsBackAtNativeWidth()
    {
        // A base that does not fit in an int. The narrow property cannot represent it and says
        // so; the native-width sibling returns it exactly. Both halves matter — a silent wrap
        // here would report a plausible small base and misplace every index built on it.
        long wideBase = 4294967296L; // 2^32, past both int and uint
        using var window = Functions.MakeBasedWindow(
            firstId: "alpha", secondId: "beta", @base: (nint)wideBase);

        AssertEqual((nint)wideBase, window.WindowBaseNative, "BasedWindow base at native width");
        AssertThrows<System.OverflowException>(
            () => { _ = window.WindowBase; },
            "BasedWindow narrowed base above int range");
    }
}
