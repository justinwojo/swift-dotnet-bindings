// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collisions;

/// <summary>
/// A module that declares a class named <c>Protocol</c>. Swift spells every reference to it
/// back-ticked, and a bare <c>Protocol</c> in a wrapper means a metatype, so the type's own
/// initializer and every member that takes, returns or stores it have to name the library's type.
/// </summary>
public class ReservedTypeNameTests : TestBase
{
    public ReservedTypeNameTests(TestResults results) : base(results) { }

    public void TestReservedNamedClassInitializerAndEquality()
    {
        using var first = new Protocol("https");
        using var same = new Protocol("https");
        using var other = new Protocol("mailto");

        AssertEqual("https", first.Value, "the initializer stored its argument");
        AssertTrue(first == same, "Swift equality compares the stored values");
        AssertFalse(first == other, "different values are not equal");
    }

    public void TestMembersTakingAndReturningTheReservedNamedClass()
    {
        using var whitelist = new ReservedWhitelist();
        AssertNull(whitelist.GetFirst(), "an empty whitelist has no first entry");

        using var https = new Protocol("https");
        using var mailto = new Protocol("mailto");
        using var chained = whitelist.AddProtocol(https);
        whitelist.AddProtocol(mailto).Dispose();

        AssertEqual(2, whitelist.ProtocolCount, "both entries reached Swift");
        using var registry = whitelist.Protocols;
        AssertEqual(2, registry.Count, "the stored registry holds the same entries");

        using var firstEntry = whitelist.GetFirst();
        AssertNotNull(firstEntry, "the first entry comes back");
        AssertEqual("https", firstEntry!.Value, "the first entry is the one added first");
        AssertEqual(2, chained.ProtocolCount, "the returned whitelist is the same Swift instance");
    }
}
