// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Lifetime;

/// <summary>
/// Tests for object ownership: double-dispose safety, access-after-dispose,
/// multiple references, and ownership transfer patterns.
/// These tests do NOT call ForceGC() or construct MutableProps, so they are
/// safe from the Mono JIT frame tracker assertion.
/// </summary>
public class OwnershipTests : TestBase
{
    public OwnershipTests(TestResults results) : base(results) { }

    #region Double-Dispose Safety

    [TestTier(TestTier.Tier2)]
    public void TestAnimalDoubleDispose()
    {
        // Dispose SafeHandle twice — should not crash or double-free
        var animal = TestLibFunctions.CreateAnimal("DoubleFree", "Test");
        var name = animal.Name.ToString();
        AssertEqual("DoubleFree", name, "Accessible before dispose");

        // First dispose
        animal.Dispose();

        // Second dispose should be safe (SafeHandle tracks disposed state)
        animal.Dispose();

        TestLogger.Info("Double-dispose did not crash");
    }

    [TestTier(TestTier.Tier2)]
    public void TestUniqueResourceDoubleDispose()
    {
        var resource = TestLibFunctions.CreateUniqueResource(7);
        AssertEqual(7, resource.Id, "Accessible before dispose");

        resource.Dispose();
        resource.Dispose();

        TestLogger.Info("UniqueResource double-dispose safe");
    }

    #endregion

    #region Access-After-Dispose

    [TestTier(TestTier.Tier2)]
    public void TestAnimalPropertyGetAfterDispose()
    {
        var animal = TestLibFunctions.CreateAnimal("Ghost", "Boo");
        animal.Dispose();

        AssertThrows<ObjectDisposedException>(() =>
        {
            _ = animal.Name;
        }, "Property get after dispose throws ObjectDisposedException");

        TestLogger.Info("Property get after dispose correctly throws");
    }

    [TestTier(TestTier.Tier2)]
    public void TestAnimalPropertySetAfterDispose()
    {
        var animal = TestLibFunctions.CreateAnimal("Ghost", "Boo");
        animal.Dispose();

        AssertThrows<ObjectDisposedException>(() =>
        {
            animal.Name = new SwiftString("NewName");
        }, "Property set after dispose throws ObjectDisposedException");

        TestLogger.Info("Property set after dispose correctly throws");
    }

    [TestTier(TestTier.Tier2)]
    public void TestAnimalMethodCallAfterDispose()
    {
        var animal = TestLibFunctions.CreateAnimal("Ghost", "Boo");
        animal.Dispose();

        AssertThrows<ObjectDisposedException>(() =>
        {
            _ = animal.GetSpeak();
        }, "Method call after dispose throws ObjectDisposedException");

        TestLogger.Info("Method call after dispose correctly throws");
    }

    [TestTier(TestTier.Tier2)]
    public void TestAnimalDescribeAfterDispose()
    {
        var animal = TestLibFunctions.CreateAnimal("Ghost", "Boo");
        animal.Dispose();

        AssertThrows<ObjectDisposedException>(() =>
        {
            _ = animal.GetDescribe();
        }, "Describe after dispose throws ObjectDisposedException");

        TestLogger.Info("Describe after dispose correctly throws");
    }

    [TestTier(TestTier.Tier2)]
    public void TestUniqueResourceAccessAfterDispose()
    {
        var resource = TestLibFunctions.CreateUniqueResource(42);
        resource.Dispose();

        AssertThrows<ObjectDisposedException>(() =>
        {
            _ = resource.Id;
        }, "UniqueResource.Id after dispose throws");

        TestLogger.Info("UniqueResource access after dispose correctly throws");
    }

    [TestTier(TestTier.Tier2)]
    public void TestUniqueResourceMethodAfterDispose()
    {
        var resource = TestLibFunctions.CreateUniqueResource(42);
        resource.Dispose();

        AssertThrows<ObjectDisposedException>(() =>
        {
            _ = resource.GetInspect();
        }, "UniqueResource.Inspect after dispose throws");

        TestLogger.Info("UniqueResource method after dispose correctly throws");
    }

    #endregion

    #region Multiple References

    [TestTier(TestTier.Tier2)]
    public void TestMultipleReferencesIndependent()
    {
        // Two independently created objects are independent
        var animal1 = TestLibFunctions.CreateAnimal("First", "Meow");
        var animal2 = TestLibFunctions.CreateAnimal("Second", "Woof");

        animal1.Dispose();

        // animal2 should still be functional
        var name2 = animal2.Name.ToString();
        AssertEqual("Second", name2, "Second animal unaffected by first dispose");

        var speak2 = animal2.GetSpeak();
        AssertNotNull(speak2, "Second animal methods work after first disposed");

        TestLogger.Info("Independent objects have independent lifetimes");
    }

    [TestTier(TestTier.Tier2)]
    public void TestMultipleResourcesIndependent()
    {
        var r1 = TestLibFunctions.CreateUniqueResource(1);
        var r2 = TestLibFunctions.CreateUniqueResource(2);

        r1.Dispose();

        // r2 should still work
        AssertEqual(2, r2.Id, "Second resource unaffected by first dispose");
        AssertEqual(2, r2.GetInspect(), "Second resource methods work");

        TestLogger.Info("Independent UniqueResources have independent lifetimes");
    }

    [TestTier(TestTier.Tier2)]
    public void TestSharedReferenceDispose()
    {
        // Two C# variables pointing to the SAME Swift object.
        // Disposing via one reference invalidates both, because they share
        // the same SwiftSafeHandle — C# reference semantics means both
        // variables point to the same managed wrapper object.
        var animal = TestLibFunctions.CreateAnimal("Shared", "Moo");
        var alias = animal;

        // Both references see the same data
        AssertEqual("Shared", alias.Name.ToString(), "Alias sees same name");

        // Dispose via the alias
        alias.Dispose();

        // The original reference is also invalidated (same SafeHandle)
        AssertThrows<ObjectDisposedException>(() =>
        {
            _ = animal.Name;
        }, "Original reference invalidated after alias dispose");

        AssertThrows<ObjectDisposedException>(() =>
        {
            _ = alias.Name;
        }, "Alias also invalidated after dispose");

        TestLogger.Info("Shared references: dispose via alias invalidates both");
    }

    [TestTier(TestTier.Tier2)]
    public void TestSharedReferenceMethodCallAfterDispose()
    {
        var animal = TestLibFunctions.CreateAnimal("SharedMethod", "Woof");
        var alias = animal;

        // Verify both work before dispose
        AssertNotNull(animal.GetSpeak(), "Original speaks before dispose");
        AssertNotNull(alias.GetDescribe(), "Alias describes before dispose");

        // Dispose via original
        animal.Dispose();

        // Method call via alias should also throw
        AssertThrows<ObjectDisposedException>(() =>
        {
            _ = alias.GetSpeak();
        }, "Alias method call throws after original disposed");

        TestLogger.Info("Shared references: method call via alias throws after dispose");
    }

    #endregion

    #region Ownership Transfer Patterns

    [TestTier(TestTier.Tier3)] // Mono: SafeHandle non-blittable through CallConvSwift P/Invoke
    public void TestBorrowResourcePreservesOwnership()
    {
        // BorrowResource should not consume the resource
        var resource = TestLibFunctions.CreateUniqueResource(42);

        var borrowed = TestLibFunctions.BorrowResource(resource);
        AssertEqual(42, borrowed, "BorrowResource returns correct id");

        // Resource should still be accessible after borrow
        var id = resource.Id;
        AssertEqual(42, id, "Resource still accessible after borrow");

        TestLogger.Info("BorrowResource preserves ownership");
    }

    [TestTier(TestTier.Tier2)]
    public void TestInspectPreservesAccess()
    {
        // Inspect should not consume the resource
        var resource = new UniqueResource(55);

        var inspected1 = resource.GetInspect();
        var inspected2 = resource.GetInspect();
        AssertEqual(55, inspected1, "First inspect returns correct id");
        AssertEqual(55, inspected2, "Second inspect returns correct id");

        // Still accessible after multiple inspects
        AssertEqual(55, resource.Id, "Id still accessible after inspects");

        TestLogger.Info("Inspect preserves access across multiple calls");
    }

    #endregion
}
