// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;
using SwiftBindingsTestLib;
using SwiftBindingsTestLibDependency;

namespace RuntimeTestsApp.CrossModule;

/// <summary>
/// Arrays of another module's class-bound existentials (<c>[any DependencyVendedBound]</c>, declared
/// in SwiftBindingsTestLibDependency) returned by this module's members: a method, a free function
/// and a generic builder's per-conformer specializations. Each array is read through the shared
/// class-existential carrier (a reference and one witness table, 16 bytes), whose metadata the
/// runtime resolves itself rather than waiting for some binding's module initializer to register it.
/// A wrong element stride or carrier reads the wrong value or crashes.
/// </summary>
public class CrossModuleClassBoundArrayTests : TestBase
{
    public CrossModuleClassBoundArrayTests(TestResults results) : base(results) { }

    private void AssertValues(IReadOnlyList<IDependencyVendedBound> members, int first, int count, string label)
    {
        AssertEqual(count, members.Count, $"{label}: element count");
        for (var i = 0; i < members.Count; i++)
            AssertEqual(first + i, members[i].GetVendedValue(), $"{label}: element {i} answers through its witness table");
    }

    public void TestCarrierIsAReferenceAndOneWitnessTable()
    {
        var metadata = TypeMetadata.GetTypeMetadataOrThrow<ClassExistentialContainer1>();
        AssertEqual((nuint)(2 * IntPtr.Size), metadata.Stride, "the class-bound carrier's stride");
        AssertEqual((nuint)IntPtr.Size, TypeMetadata.GetTypeMetadataOrThrow<SwiftArray<ClassExistentialContainer1>>().Size,
            "an array of the carrier is one buffer pointer");
    }

    public void TestMethodReturnsTheOtherModulesExistentials()
    {
        using var roster = new CrossModuleBoundRoster(10);
        var members = roster.Members(3);
        AssertValues(members, 10, 3, "Members(3)");
        AssertEqual(33, roster.Total(members), "the array goes back in as itself");
        AssertEqual(0, roster.Members(0).Count, "an empty array");
    }

    public void TestFreeFunctionReturnsTheOtherModulesExistentials()
    {
        AssertValues(TestLibFunctions.CrossModuleBoundMembers(5, 4), 5, 4, "CrossModuleBoundMembers(5, 4)");
    }

    public void TestEachConformerHasItsOwnSpecialization()
    {
        foreach (var conformer in new[] { typeof(LedgerCountOption), typeof(LedgerFlagOption), typeof(LedgerDurationOption) })
        {
            var overload = typeof(CrossModuleBoundBuilder).GetMethod(
                nameof(CrossModuleBoundBuilder.Vend), BindingFlags.Public | BindingFlags.Static, new[] { conformer });
            AssertNotNull(overload, $"Vend({conformer.Name}) is declared");
            AssertFalse(overload!.IsGenericMethodDefinition, $"Vend({conformer.Name}) is a concrete overload");
            AssertEqual(typeof(IReadOnlyList<IDependencyVendedBound>), overload.ReturnType, $"Vend({conformer.Name}) returns the projected list");
        }
    }

    public void TestSpecializationsReturnTheOtherModulesExistentials()
    {
        using var count = new LedgerCountOption(7);
        AssertValues(CrossModuleBoundBuilder.Vend(count), 1007, 2, "Vend(LedgerCountOption)");
        using var flag = new LedgerFlagOption(true);
        AssertValues(CrossModuleBoundBuilder.Vend(flag), 2001, 2, "Vend(LedgerFlagOption)");
        using var duration = new LedgerDurationOption(4.0);
        AssertValues(CrossModuleBoundBuilder.Vend(duration), 3004, 2, "Vend(LedgerDurationOption)");
    }

    public void TestRepeatedCallsStayIndependent()
    {
        using var roster = new CrossModuleBoundRoster(100);
        for (var i = 0; i < 50; i++)
        {
            using var option = new LedgerCountOption(i);
            AssertValues(CrossModuleBoundBuilder.Vend(option), 1000 + i, 2, $"Vend call {i}");
            AssertValues(roster.Members(2), 100, 2, $"Members call {i}");
        }
    }
}
