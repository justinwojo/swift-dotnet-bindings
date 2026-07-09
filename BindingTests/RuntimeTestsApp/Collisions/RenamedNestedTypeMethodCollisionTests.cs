// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Linq;
using System.Reflection;
using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collisions;

/// <summary>
/// Runtime coverage for the second-order nested-type/method collision: a nested
/// type that is first RENAMED by the kind-aware disambiguation pass (a sibling
/// <c>entry</c> property forces <c>Ledger.Entry</c> → <c>Ledger.EntryInfo</c>,
/// struct → "Info" suffix) and only THEN collides with a sibling method whose
/// PascalCase name is the renamed name (<c>entryInfo(scaledBy:)</c> → <c>EntryInfo</c>).
///
/// The method/nested-type collision set must reserve the nested type's EMITTED
/// leaf (<c>EntryInfo</c>), not its raw Swift leaf (<c>Entry</c>). Reserving the
/// raw leaf lets the method emit as <c>EntryInfo</c> and collide with the renamed
/// nested type → CS0102 at compile time (verified: neutering the fix makes this
/// exact fixture fail to compile). Reserving the emitted leaf disambiguates the
/// method to <c>EntryInfoMethod</c> so both compile and both are reachable.
///
/// As with the direct-collision fixture (<c>Navigator</c>), the primary signal is
/// "the binding compiles at all." The runtime calls below additionally prove the
/// renamed nested type, its stored property, and the disambiguated method all
/// round-trip. The method is invoked both directly and through a Swift free
/// function so the test does not depend on hardcoding the disambiguated name.
/// </summary>
public class RenamedNestedTypeMethodCollisionTests : TestBase
{
    public RenamedNestedTypeMethodCollisionTests(TestResults results) : base(results) { }

    /// <summary>
    /// Exercises the renamed nested type: reads the <c>entry</c> property (typed as
    /// the renamed <c>Ledger.EntryInfo</c>) off a constructed <c>Ledger</c> and
    /// verifies its <c>amount</c> field round-trips. If the rename had dropped or
    /// mis-wired the nested type, this fails to compile or returns the wrong value.
    /// </summary>
    public void TestLedgerRenamedNestedTypePropertyRoundTrip()
    {
        var ledger = TestLibFunctions.MakeLedger(42);
        var amount = ledger.Entry.Amount;
        TestLogger.Info($"Ledger.Entry.Amount = {amount}");
        AssertEqual(42, amount,
            "Ledger.entry (renamed nested type Ledger.EntryInfo) must round-trip the " +
            "amount used to build the Ledger.");
    }

    /// <summary>
    /// Constructs the renamed nested type directly via <c>new Ledger.EntryInfo(...)</c>
    /// to prove its initializer survived the rename under its post-rename C# name.
    /// </summary>
    public void TestLedgerEntryInfoNestedTypeConstructible()
    {
        var entry = new Ledger.EntryInfo(7);
        TestLogger.Info($"new Ledger.EntryInfo(7).Amount = {entry.Amount}");
        AssertEqual(7, entry.Amount,
            "The renamed nested type Ledger.EntryInfo must be constructible directly " +
            "and round-trip its amount.");
    }

    /// <summary>
    /// Exercises the disambiguated method: calls <c>Ledger.entryInfo(scaledBy:)</c>
    /// both directly (as the disambiguated <c>EntryInfoMethod</c>) and through a
    /// Swift free-function helper, and verifies both return amount * factor. If the
    /// method had been dropped or aliased onto the nested type to "resolve" the
    /// collision, the direct call fails to compile.
    /// </summary>
    public void TestLedgerCollidingMethodCallable()
    {
        var ledger = TestLibFunctions.MakeLedger(10);

        var direct = (int)ledger.EntryInfoMethod(3);
        TestLogger.Info($"Ledger.EntryInfoMethod(3) on amount=10 = {direct}");
        AssertEqual(30, direct,
            "Ledger.entryInfo(scaledBy:) (disambiguated to EntryInfoMethod) must return " +
            "amount * factor. If the collision was resolved by dropping the method this " +
            "call would not compile.");

        var viaFreeFunc = (int)TestLibFunctions.InvokeLedgerEntryInfo(ledger, 5);
        TestLogger.Info($"invokeLedgerEntryInfo(amount=10, factor=5) = {viaFreeFunc}");
        AssertEqual(50, viaFreeFunc,
            "The colliding method must also round-trip when reached through the Swift " +
            "free function, independent of its disambiguated C# name.");
    }

    /// <summary>
    /// Reflection sanity check that the collision was resolved by RENAME, not by
    /// dropping a member: the renamed nested type <c>EntryInfo</c>, the disambiguated
    /// method (<c>EntryInfo</c> + a suffix, not equal to the nested type name), and
    /// the idiomatic <c>Entry</c> property must all coexist on <c>Ledger</c>.
    /// </summary>
    public void TestLedgerRenamedTypeAndMethodBothPresent()
    {
        var ledgerType = typeof(Ledger);

        var entryInfoNested = ledgerType.GetNestedTypes(BindingFlags.Public)
            .Where(t => t.Name == "EntryInfo")
            .ToArray();
        var collidingMethods = ledgerType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => m.Name.StartsWith("EntryInfo") && m.Name != "EntryInfo")
            .ToArray();
        var entryProperty = ledgerType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.Name == "Entry")
            .ToArray();

        TestLogger.Info($"Ledger nested 'EntryInfo': [{string.Join(", ", entryInfoNested.Select(t => t.Name))}]");
        TestLogger.Info($"Ledger disambiguated methods: [{string.Join(", ", collidingMethods.Select(m => m.Name))}]");
        TestLogger.Info($"Ledger 'Entry' property: [{string.Join(", ", entryProperty.Select(p => p.Name))}]");

        AssertTrue(entryInfoNested.Length == 1,
            "Ledger must expose exactly the renamed nested type 'EntryInfo'.");
        AssertTrue(collidingMethods.Length > 0,
            "Ledger must expose the colliding method under a disambiguated name (EntryInfo + " +
            "a suffix), not dropped and not aliased onto the nested type's exact name.");
        AssertTrue(entryProperty.Length == 1,
            "Ledger must keep the idiomatic 'Entry' property name; the rename moved the " +
            "suffix onto the nested type, leaving the property clean.");
    }
}
