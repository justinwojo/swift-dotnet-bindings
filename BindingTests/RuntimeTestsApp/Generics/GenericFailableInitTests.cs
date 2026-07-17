// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Round-trips the <c>TryCreate</c> factory projected from a failable initializer on a GENERIC
/// parent, over both return conventions: a reference type (the factory wraps a nullable retained
/// pointer) and a non-frozen value type (the factory copies an opaque payload).
///
/// Every use of the parent's name inside that factory — the <c>out</c> parameter, the metadata
/// lookup, the construction expression — is a type reference, so on a generic parent it has to
/// carry the parameter list. Emitting the bare leaf name has the wrong arity and binds to
/// nothing; where a namespace shares the type's simple name it resolves to the namespace instead.
/// Either way the generated project fails to compile, so the compile gate carries that half.
/// These assertions add the runtime half — that the factory's success and failure branches both
/// behave — which only becomes observable once the type resolves at all.
/// </summary>
public class GenericFailableInitTests : TestBase
{
    public GenericFailableInitTests(TestResults results) : base(results) { }

    /// <summary>Generic reference type, success branch: the guard passes and the instance is live.</summary>
    public void TestGenericClassFailableInit_Succeeds()
    {
        var created = Vault<int>.TryCreate(8, out var vault);
        AssertTrue(created, "Expected Vault<int>.TryCreate(8) to succeed for a positive capacity");

        using (vault)
        {
            AssertNotNull(vault, "Expected a non-null Vault<int> on the success branch");
            AssertEqual(8, vault!.RemainingCapacity,
                $"Expected RemainingCapacity == 8 on a fresh vault, got {vault.RemainingCapacity}");
            AssertEqual(0, vault.StoredCount,
                $"Expected StoredCount == 0 on a fresh vault, got {vault.StoredCount}");
        }
    }

    /// <summary>Generic reference type, nil branch: the guard rejects and no instance is produced.</summary>
    public void TestGenericClassFailableInit_Fails()
    {
        var created = Vault<int>.TryCreate(0, out var vault);
        AssertFalse(created, "Expected Vault<int>.TryCreate(0) to fail for a non-positive capacity");
        AssertNull(vault, "Expected a null Vault<int> on the failure branch");
    }

    /// <summary>Generic non-frozen value type, success branch: the payload copy round-trips.</summary>
    public void TestGenericStructFailableInit_Succeeds()
    {
        var created = Journal<int>.TryCreate(10, out var journal);
        AssertTrue(created, "Expected Journal<int>.TryCreate(10) to succeed for an in-range limit");

        using (journal)
        {
            AssertNotNull(journal, "Expected a non-null Journal<int> on the success branch");
            AssertEqual(0, journal!.EntryCount,
                $"Expected EntryCount == 0 on a fresh journal, got {journal.EntryCount}");
        }
    }

    /// <summary>Generic non-frozen value type, nil branch: an out-of-range limit is rejected.</summary>
    public void TestGenericStructFailableInit_Fails()
    {
        var created = Journal<int>.TryCreate(5000, out var journal);
        AssertFalse(created, "Expected Journal<int>.TryCreate(5000) to fail for an out-of-range limit");
        AssertNull(journal, "Expected a null Journal<int> on the failure branch");
    }
}
