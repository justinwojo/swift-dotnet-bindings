// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Runtime regression for the property-drop bug on PAT-constrained generic
/// parents. Plain stored properties on <c>Bag&lt;Item: BagItem&gt;</c> —
/// where <c>BagItem</c> is a PAT protocol — must:
/// <list type="bullet">
///   <item>Emit per closed conformer (the open generic's accessor preflight
///   used to drop them silently with no tombstone).</item>
///   <item>Round-trip values via getter/setter for blittable types
///   (<c>Int</c>) and for <c>Optional&lt;Bool&gt;</c>.</item>
/// </list>
/// The negative case (<c>selectedFilter: Item.Filter?</c>) cannot be
/// round-tripped at runtime — its type reaches the parent's PAT associated
/// type, which is out of scope for this work. The generator must instead
/// suppress it with a visible <c>// Unsupported:</c> tombstone comment in the
/// generated output (see <c>BindingTests/output/SwiftBindingsTestLib.cs</c>
/// — grep for <c>// Unsupported: property 'selectedFilter'</c>). That
/// tombstone is verified by inspection of the generated file; there is no
/// runtime assertion for it here.
/// </summary>
public class PatParentPlainPropertiesTests : TestBase
{
    public PatParentPlainPropertiesTests(TestResults results) : base(results) { }

    public void TestBagPlainStringItem_DefaultValues()
    {
        using var bag = Functions.MakeBagPlainStringItem();
        AssertEqual(25, (int)bag.Limit, "Limit default value is 25");
        AssertEqual(0, (int)bag.Offset, "Offset default value is 0");
        AssertTrue(bag.IncludeArchived is null, "IncludeArchived default value is null");
    }

    public void TestBagPlainStringItem_LimitRoundTrips()
    {
        using var bag = Functions.MakeBagPlainStringItem();
        bag.Limit = 100;
        AssertEqual(100, (int)bag.Limit, "Limit setter persists 100");
        bag.Limit = 0;
        AssertEqual(0, (int)bag.Limit, "Limit setter persists 0");
        bag.Limit = -7;
        AssertEqual(-7, (int)bag.Limit, "Limit setter persists negative value");
    }

    public void TestBagPlainStringItem_OffsetRoundTrips()
    {
        using var bag = Functions.MakeBagPlainStringItem();
        bag.Offset = 50;
        AssertEqual(50, (int)bag.Offset, "Offset setter persists 50");
        bag.Offset = 12345;
        AssertEqual(12345, (int)bag.Offset, "Offset setter persists 12345");
    }

    public void TestBagPlainStringItem_IncludeArchivedRoundTrips()
    {
        using var bag = Functions.MakeBagPlainStringItem();
        bag.IncludeArchived = true;
        AssertTrue(bag.IncludeArchived == true, "IncludeArchived setter persists true");
        bag.IncludeArchived = false;
        AssertTrue(bag.IncludeArchived == false, "IncludeArchived setter persists false");
        bag.IncludeArchived = null;
        AssertTrue(bag.IncludeArchived is null, "IncludeArchived setter persists null");
    }

    public void TestBagPlainIntItem_SecondConformerEmitsIndependently()
    {
        // Per-closed-conformer CSM emission: the same property surface must
        // emit independently for each conformer's specialization.
        using var bag = Functions.MakeBagPlainIntItem();
        AssertEqual(25, (int)bag.Limit, "Second conformer's Limit default value is 25");
        bag.Limit = 7;
        AssertEqual(7, (int)bag.Limit, "Second conformer's Limit setter persists 7");
        bag.Offset = 3;
        AssertEqual(3, (int)bag.Offset, "Second conformer's Offset setter persists 3");
    }

    public void TestBagPlainStringItem_Mutations_AreIndependent()
    {
        // Two instances must not share state — confirms each Bag<PlainStringItem>
        // carries its own backing storage even after writes through the per-
        // closed-conformer property setters.
        using var a = Functions.MakeBagPlainStringItem();
        using var b = Functions.MakeBagPlainStringItem();
        a.Limit = 11;
        b.Limit = 22;
        AssertEqual(11, (int)a.Limit, "Instance A retains its own Limit (11)");
        AssertEqual(22, (int)b.Limit, "Instance B retains its own Limit (22)");
    }
}
