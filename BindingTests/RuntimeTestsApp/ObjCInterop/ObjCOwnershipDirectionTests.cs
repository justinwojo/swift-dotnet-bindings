// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Foundation;
using ObjCUmbrella;
using RuntimeTestsApp.Infrastructure;

namespace RuntimeTestsApp.ObjCInterop;

public class ObjCOwnershipDirectionTests : TestBase
{
    public ObjCOwnershipDirectionTests(TestResults results) : base(results) { }

    public void TestOwnedReturnsAndBorrowingReleaseExactlyOnce()
    {
        var start = OUOwnershipOracle.DeallocCount();
        using (var token = OUOwnershipOracle.OwnedFactory())
        {
            AssertEqual(42, OUOwnershipOracle.Inspect(token), "Borrow sees owned token");
            AssertEqual(42, token.Answer(), "Borrow preserves caller liveness");
            AssertEqual(start, OUOwnershipOracle.DeallocCount(), "No early native deallocation");
        }
        using (var token = OUOwnershipOracle.NewToken())
            AssertEqual(42, token.Answer(), "Clang implicit new-family ownership survives");
        using (var token = OUOwnershipOracle.FamilyFactory())
            AssertEqual(42, token.Answer(), "Explicit family override ownership survives");
        AssertEqual(start + 3, OUOwnershipOracle.DeallocCount(), "Each owned return deallocates exactly once");
    }

    public void TestBorrowedReturnsIncludingFamilyOverrideReleaseExactlyOnce()
    {
        var start = OUOwnershipOracle.DeallocCount();
        using (var pool = new NSAutoreleasePool())
        {
            using var borrowed = OUOwnershipOracle.BorrowedFactory();
            using var newBorrowed = OUOwnershipOracle.NewBorrowed();
            AssertEqual(42, borrowed.Answer(), "Ordinary borrowed return survives");
            AssertEqual(42, newBorrowed.Answer(), "Explicit borrowed annotation overrides new family");
        }
        AssertEqual(start + 2, OUOwnershipOracle.DeallocCount(), "Borrowed returns balance after pool drain");
    }

    public void TestCompilerExpandedPointerDirectionPreservesIncomingValue()
    {
        var value = 41;
        AssertEqual(41, OUOwnershipOracle.ReadValue(ref value), "Input-only native selector sees incoming value");
        AssertEqual(41, value, "Input-only selector preserves caller value");
        OUOwnershipOracle.Increment(ref value);
        AssertEqual(42, value, "Explicit inout reads then writes");
        value = 41;
        OUOwnershipOracle.IncrementMacro(ref value);
        AssertEqual(42, value, "Macro-expanded inout reads then writes");
        OUOwnershipOracle.WriteValue(out value);
        AssertEqual(42, value, "Explicit output remains output-only");
    }
}
