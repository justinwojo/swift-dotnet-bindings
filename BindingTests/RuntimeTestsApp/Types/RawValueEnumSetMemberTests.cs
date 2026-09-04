// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Types;

/// <summary>
/// End-to-end coverage for a payload-less raw-value <c>Hashable</c> Swift enum used as a
/// <c>Set</c> element and as a <c>Dictionary</c> key.
///
/// Such an enum projects to a plain C# <c>enum</c>, which cannot implement
/// <c>ISwiftObject</c> — and both of the module initializer's conformance registration
/// lanes (<c>RegisterConformanceFactory</c> and <c>RegisterWitnessTable</c>) are
/// constrained to that interface. Metadata registered without a conformance is not enough:
/// resolving a witness table needs both halves, so every one of the calls below used to
/// throw "Unable to get protocol witness table" on its first element or key lookup, even
/// though Swift synthesizes and exports the conformance and uses it happily on its own
/// side. The initializer now additionally declares the enum's conformance-descriptor
/// symbol, which is the only lane a C# enum can reach.
///
/// Both nestings are exercised because the emitted <c>typeof()</c> must name the enum
/// through its enclosing type: the nested shape is what shipped bindings hit, the
/// top-level one is the unqualified control. Every assertion reads a value back through
/// Swift rather than only checking that no exception was thrown, so an element that
/// hashed through the wrong witness shows up as a wrong count or a missing key rather
/// than passing silently.
/// </summary>
public class RawValueEnumSetMemberTests : TestBase
{
    public RawValueEnumSetMemberTests(TestResults results) : base(results) { }

    #region Set<Kind> — parameter direction

    public void TestNestedRawValueEnumSetMarshalsIn()
    {
        var managed = new HashSet<EnumSetHost.Kind>
        {
            EnumSetHost.Kind.Generic,
            EnumSetHost.Kind.PhoneNumber,
            EnumSetHost.Kind.EmailAddress,
        };

        // The count comes from Swift: it can only be right if each element resolved the
        // enum's own Hashable witness on the way in.
        AssertEqual(3, TestLibFunctions.NestedKindSetCount(managed),
            "Swift-side count of a populated Set<EnumSetHost.Kind>");

        // The payloads crossed intact, not merely the cardinality.
        AssertEqual("0,1,2", TestLibFunctions.NestedKindSetSortedRawValues(managed),
            "Swift-side sorted raw values of a populated Set<EnumSetHost.Kind>");
    }

    public void TestNestedRawValueEnumSetDeduplicates()
    {
        // A managed HashSet collapses duplicates before the marshal, so drive Swift's own
        // membership instead: two of the three cases present, the third absent.
        var managed = new HashSet<EnumSetHost.Kind>
        {
            EnumSetHost.Kind.Generic,
            EnumSetHost.Kind.EmailAddress,
        };

        AssertEqual(2, TestLibFunctions.NestedKindSetCount(managed),
            "Swift-side count of a two-member Set<EnumSetHost.Kind>");
        AssertTrue(TestLibFunctions.NestedKindSetContains(managed, EnumSetHost.Kind.Generic),
            "Swift-side membership of a marshalled element");
        AssertFalse(TestLibFunctions.NestedKindSetContains(managed, EnumSetHost.Kind.PhoneNumber),
            "Swift-side non-membership of an absent case");
    }

    public void TestTopLevelRawValueEnumSetMarshalsIn()
    {
        // The unqualified control: a top-level enum registers a typeof() with no enclosing
        // type, which is a different emitted spelling than the nested case above.
        var managed = new HashSet<TopLevelEnumSetKind>
        {
            TopLevelEnumSetKind.Alpha,
            TopLevelEnumSetKind.Beta,
            TopLevelEnumSetKind.Gamma,
        };

        AssertEqual(3, TestLibFunctions.TopLevelKindSetCount(managed),
            "Swift-side count of a populated Set<TopLevelEnumSetKind>");
    }

    #endregion

    #region Set<Kind> — return direction

    public void TestNestedRawValueEnumSetMarshalsOut()
    {
        var produced = TestLibFunctions.MakeNestedKindSet();

        AssertEqual(3, produced.Count, "Swift-produced Set<EnumSetHost.Kind> reports three members");

        var enumerated = new HashSet<EnumSetHost.Kind>(produced);
        AssertEqual(3, enumerated.Count, "Enumerated member count matches the produced set's Count");
        AssertTrue(enumerated.Contains(EnumSetHost.Kind.PhoneNumber),
            "Swift-produced set contains the middle case");
    }

    public void TestTopLevelRawValueEnumSetMarshalsOut()
    {
        var produced = TestLibFunctions.MakeTopLevelKindSet();

        AssertEqual(3, produced.Count, "Swift-produced Set<TopLevelEnumSetKind> reports three members");

        var enumerated = new HashSet<TopLevelEnumSetKind>(produced);
        AssertTrue(enumerated.Contains(TopLevelEnumSetKind.Gamma),
            "Swift-produced set contains the last case");
    }

    #endregion

    #region Dictionary keyed by the enum

    public void TestNestedRawValueEnumDictionaryMarshalsIn()
    {
        var entries = new Dictionary<EnumSetHost.Kind, int>
        {
            [EnumSetHost.Kind.Generic] = 5,
            [EnumSetHost.Kind.PhoneNumber] = 7,
        };

        AssertEqual(12, TestLibFunctions.NestedKindDictionaryValueSum(entries),
            "Swift-side sum of a [EnumSetHost.Kind: Int32]");

        // Lookup by a marshalled key: the keys must hash identically on both sides for a
        // marshalled entry to be findable at all.
        AssertEqual(7, TestLibFunctions.NestedKindDictionaryLookup(entries, EnumSetHost.Kind.PhoneNumber),
            "Swift-side lookup of a present key");
        AssertEqual(-1, TestLibFunctions.NestedKindDictionaryLookup(entries, EnumSetHost.Kind.EmailAddress),
            "Swift-side lookup of an absent key returns the sentinel");
    }

    public void TestNestedRawValueEnumDictionaryMarshalsOut()
    {
        var produced = TestLibFunctions.MakeNestedKindDictionary();

        AssertEqual(3, produced.Count, "Swift-produced [EnumSetHost.Kind: Int32] reports three entries");
        AssertEqual(10, produced[EnumSetHost.Kind.PhoneNumber],
            "Swift-produced dictionary maps the middle case to rawValue * 10");
        AssertEqual(20, produced[EnumSetHost.Kind.EmailAddress],
            "Swift-produced dictionary maps the last case to rawValue * 10");
    }

    #endregion

    #region The enum still binds as an ordinary member type

    public void TestRawValueEnumStillBindsAsPlainMember()
    {
        // Discrimination guard: the conformance work must not disturb the enum's ordinary
        // projection as a stored property's type.
        var host = new EnumSetHost(EnumSetHost.Kind.EmailAddress);
        AssertEqual(EnumSetHost.Kind.EmailAddress, host.HostKind,
            "Nested raw-value enum round-trips as a stored property");
    }

    #endregion
}
