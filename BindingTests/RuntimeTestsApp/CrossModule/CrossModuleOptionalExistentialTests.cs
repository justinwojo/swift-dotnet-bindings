// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;
using SwiftBindingsTestLibDependency;

namespace RuntimeTestsApp.CrossModule;

/// <summary>
/// Optional existentials of protocols another module declares (<c>CrossModuleExistentialVendor</c>
/// over SwiftBindingsTestLibDependency's <c>DependencyVended*</c>). Swift returns the class-bound
/// ones in two registers, the @objc one in one and the opaque one through a 40-byte indirect
/// result, so a result read with another module's size or nil check comes back as a wrong value,
/// a non-nil for nil or a crash. Each member is called for a value and for nil, repeatedly.
/// </summary>
public class CrossModuleOptionalExistentialTests : TestBase
{
    public CrossModuleOptionalExistentialTests(TestResults results) : base(results) { }

    public void TestClassBoundOptionalExistentialRoundTrips()
    {
        using var vendor = new CrossModuleExistentialVendor(seed: 7);
        using var empty = new CrossModuleExistentialVendor(seed: 0);
        for (var i = 0; i < 3; i++)
        {
            var bound = vendor.GetBound();
            AssertNotNull(bound, "a seeded vendor vends a bound existential");
            AssertEqual(7, bound!.GetVendedValue(), "the bound existential answers through its witness table");
            AssertEqual(7, vendor.ValueOf(bound), "the bound existential goes back in as itself");
            AssertNull(empty.GetBound(), "an unseeded vendor vends nil");
            AssertEqual(-1, vendor.ValueOf(null), "nil goes back in as nil");
            (bound as IDisposable)?.Dispose();
        }
    }

    public void TestRefinedOptionalExistentialRoundTrips()
    {
        using var vendor = new CrossModuleExistentialVendor(seed: 9);
        using var empty = new CrossModuleExistentialVendor(seed: -2);
        for (var i = 0; i < 3; i++)
        {
            var refined = vendor.GetRefined();
            AssertNotNull(refined, "a seeded vendor vends a refined existential");
            // Its own requirement only: the proxy of a refined protocol does not dispatch the
            // requirements it inherits.
            AssertEqual(1009, refined!.GetVendedExtra(), "the refined protocol's own requirement");
            // Passed as its base it must carry the base protocol's witness table, not its own.
            AssertEqual(9, vendor.ValueOf(refined), "the refined existential goes back in as its base");
            AssertNull(empty.GetRefined(), "an unseeded vendor vends nil");
            (refined as IDisposable)?.Dispose();
        }
    }

    public void TestObjCOptionalExistentialRoundTrips()
    {
        using var vendor = new CrossModuleExistentialVendor(seed: 11);
        using var empty = new CrossModuleExistentialVendor(seed: 0);
        for (var i = 0; i < 3; i++)
        {
            var objc = vendor.GetObjc();
            AssertNotNull(objc, "a seeded vendor vends an @objc existential");
            AssertEqual(11, objc!.GetVendedValue(), "the @objc existential answers");
            AssertNull(empty.GetObjc(), "an unseeded vendor vends nil");
            (objc as IDisposable)?.Dispose();
        }
    }

    public void TestOpaqueRefinedPassedAsItsBase()
    {
        for (var i = 0; i < 3; i++)
        {
            var refined = TestLibFunctions.CrossModuleMakeOpaqueRefined(17);
            AssertEqual(1017, refined.GetVendedExtra(), "the refined protocol's own requirement");
            AssertEqual(17, TestLibFunctions.CrossModuleOpaqueValueOf(refined), "the refined existential goes back in as its base");
            (refined as IDisposable)?.Dispose();
        }
    }

    // The opaque member is a direct Swift-convention call with an indirect result and self.
    private const SwiftTransitionFacts OpaqueShapes = SwiftTransitionFacts.SelfRegister | SwiftTransitionFacts.SpecialBeforeArguments;

    public void TestOpaqueOptionalExistentialRoundTrips()
    {
        using var vendor = new CrossModuleExistentialVendor(seed: 13);
        using var empty = new CrossModuleExistentialVendor(seed: 0);
        if (!ImportRoutes.BridgesRouted && !SwiftTransitionGuard.IsSupported(OpaqueShapes))
        {
            SwiftTransitionUnsupportedException? refusal = null;
            try
            {
                vendor.GetOpaque();
            }
            catch (SwiftTransitionUnsupportedException ex)
            {
                refusal = ex;
            }
            AssertNotNull(refusal, "GetOpaque must refuse on an engine that mishandles its direct transition");
            return;
        }
        for (var i = 0; i < 3; i++)
        {
            var opaque = vendor.GetOpaque();
            AssertNotNull(opaque, "a seeded vendor vends an opaque existential");
            AssertEqual(13, opaque!.GetVendedValue(), "the boxed value answers through its witness table");
            AssertNull(empty.GetOpaque(), "an unseeded vendor vends nil: the box's metadata word is zero");
            (opaque as IDisposable)?.Dispose();
        }
    }
}
