// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.SwiftBindingsTestLib;

namespace RuntimeTestsApp.Lifetime;

/// <summary>
/// Tests for object ownership, retain/release balance, SafeHandle lifecycle,
/// and GC interaction with Swift ARC.
/// </summary>
public class OwnershipTests : TestBase
{
    public OwnershipTests(TestResults results) : base(results) { }

    #region Basic Retain/Release Balance

    [TestTier(TestTier.Tier2)]
    public void TestAnimalCreateUseRelease()
    {
        // Create object, use it, let it go out of scope, GC — no crash
        var animal = SwiftBindingsTestLib.CreateAnimal("Temp", "Woof");
        var name = animal.Name.ToString();
        AssertEqual("Temp", name, "Animal accessible after creation");

        var speak = animal.Speak();
        AssertNotNull(speak, "Speak returns result");

        // Let the reference go and force GC — should not crash
        animal = null!;
        ForceGC();

        TestLogger.Info("Create-use-release cycle completed without crash");
    }

    [TestTier(TestTier.Tier2)]
    public void TestUniqueResourceCreateUseRelease()
    {
        // UniqueResource via factory
        var resource = SwiftBindingsTestLib.CreateUniqueResource(42);
        var id = resource.Id;
        AssertEqual(42, id, "UniqueResource.Id accessible");

        var inspected = resource.Inspect();
        AssertEqual(42, inspected, "Inspect returns correct id");

        resource = null!;
        ForceGC();

        TestLogger.Info("UniqueResource create-use-release completed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestUniqueResourceConstructorLifecycle()
    {
        // UniqueResource via public constructor
        var resource = new UniqueResource(99);
        AssertEqual(99, resource.Id, "Constructor-created resource has correct Id");

        resource = null!;
        ForceGC();

        TestLogger.Info("UniqueResource constructor lifecycle completed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestMutablePropsLifecycle()
    {
        // MutableProps struct lifecycle
        var props = new MutableProps(10, "Test");
        AssertEqual(10, props.Value, "MutableProps.Value accessible");
        AssertEqual("Test", props.Name.ToString(), "MutableProps.Name accessible");

        // Modify and verify
        props.Value = 20;
        AssertEqual(20, props.Value, "MutableProps.Value after set");

        props = null!;
        ForceGC();

        TestLogger.Info("MutableProps lifecycle completed");
    }

    #endregion

    #region Double-Dispose Safety

    [TestTier(TestTier.Tier2)]
    public void TestAnimalDoubleDispose()
    {
        // Dispose SafeHandle twice — should not crash or double-free
        var animal = SwiftBindingsTestLib.CreateAnimal("DoubleFree", "Test");
        var name = animal.Name.ToString();
        AssertEqual("DoubleFree", name, "Accessible before dispose");

        // First dispose
        animal.Payload.Dispose();

        // Second dispose should be safe (SafeHandle tracks disposed state)
        animal.Payload.Dispose();

        TestLogger.Info("Double-dispose did not crash");
    }

    [TestTier(TestTier.Tier2)]
    public void TestUniqueResourceDoubleDispose()
    {
        var resource = SwiftBindingsTestLib.CreateUniqueResource(7);
        AssertEqual(7, resource.Id, "Accessible before dispose");

        resource.Payload.Dispose();
        resource.Payload.Dispose();

        TestLogger.Info("UniqueResource double-dispose safe");
    }

    [TestTier(TestTier.Tier2)]
    public void TestMutablePropsDoubleDispose()
    {
        var props = new MutableProps(5, "DoubleDispose");
        AssertEqual(5, props.Value, "Accessible before dispose");

        props.Payload.Dispose();
        props.Payload.Dispose();

        TestLogger.Info("MutableProps double-dispose safe");
    }

    #endregion

    #region Access-After-Dispose

    [TestTier(TestTier.Tier2)]
    public void TestAnimalPropertyGetAfterDispose()
    {
        var animal = SwiftBindingsTestLib.CreateAnimal("Ghost", "Boo");
        animal.Payload.Dispose();

        AssertThrows<ObjectDisposedException>(() =>
        {
            _ = animal.Name;
        }, "Property get after dispose throws ObjectDisposedException");

        TestLogger.Info("Property get after dispose correctly throws");
    }

    [TestTier(TestTier.Tier2)]
    public void TestAnimalPropertySetAfterDispose()
    {
        var animal = SwiftBindingsTestLib.CreateAnimal("Ghost", "Boo");
        animal.Payload.Dispose();

        AssertThrows<ObjectDisposedException>(() =>
        {
            animal.Name = new SwiftString("NewName");
        }, "Property set after dispose throws ObjectDisposedException");

        TestLogger.Info("Property set after dispose correctly throws");
    }

    [TestTier(TestTier.Tier2)]
    public void TestAnimalMethodCallAfterDispose()
    {
        var animal = SwiftBindingsTestLib.CreateAnimal("Ghost", "Boo");
        animal.Payload.Dispose();

        AssertThrows<ObjectDisposedException>(() =>
        {
            _ = animal.Speak();
        }, "Method call after dispose throws ObjectDisposedException");

        TestLogger.Info("Method call after dispose correctly throws");
    }

    [TestTier(TestTier.Tier2)]
    public void TestAnimalDescribeAfterDispose()
    {
        var animal = SwiftBindingsTestLib.CreateAnimal("Ghost", "Boo");
        animal.Payload.Dispose();

        AssertThrows<ObjectDisposedException>(() =>
        {
            _ = animal.Describe();
        }, "Describe after dispose throws ObjectDisposedException");

        TestLogger.Info("Describe after dispose correctly throws");
    }

    [TestTier(TestTier.Tier2)]
    public void TestUniqueResourceAccessAfterDispose()
    {
        var resource = SwiftBindingsTestLib.CreateUniqueResource(42);
        resource.Payload.Dispose();

        AssertThrows<ObjectDisposedException>(() =>
        {
            _ = resource.Id;
        }, "UniqueResource.Id after dispose throws");

        TestLogger.Info("UniqueResource access after dispose correctly throws");
    }

    [TestTier(TestTier.Tier2)]
    public void TestUniqueResourceMethodAfterDispose()
    {
        var resource = SwiftBindingsTestLib.CreateUniqueResource(42);
        resource.Payload.Dispose();

        AssertThrows<ObjectDisposedException>(() =>
        {
            _ = resource.Inspect();
        }, "UniqueResource.Inspect after dispose throws");

        TestLogger.Info("UniqueResource method after dispose correctly throws");
    }

    [TestTier(TestTier.Tier2)]
    public void TestMutablePropsAccessAfterDispose()
    {
        var props = new MutableProps(10, "Test");
        props.Payload.Dispose();

        AssertThrows<ObjectDisposedException>(() =>
        {
            _ = props.Value;
        }, "MutableProps.Value after dispose throws");

        TestLogger.Info("MutableProps access after dispose correctly throws");
    }

    [TestTier(TestTier.Tier2)]
    public void TestMutablePropsSetAfterDispose()
    {
        var props = new MutableProps(10, "Test");
        props.Payload.Dispose();

        AssertThrows<ObjectDisposedException>(() =>
        {
            props.Value = 99;
        }, "MutableProps.Value set after dispose throws");

        TestLogger.Info("MutableProps set after dispose correctly throws");
    }

    #endregion

    #region Multiple References

    [TestTier(TestTier.Tier2)]
    public void TestMultipleReferencesIndependent()
    {
        // Two independently created objects are independent
        var animal1 = SwiftBindingsTestLib.CreateAnimal("First", "Meow");
        var animal2 = SwiftBindingsTestLib.CreateAnimal("Second", "Woof");

        animal1.Payload.Dispose();

        // animal2 should still be functional
        var name2 = animal2.Name.ToString();
        AssertEqual("Second", name2, "Second animal unaffected by first dispose");

        var speak2 = animal2.Speak();
        AssertNotNull(speak2, "Second animal methods work after first disposed");

        TestLogger.Info("Independent objects have independent lifetimes");
    }

    [TestTier(TestTier.Tier2)]
    public void TestMultipleResourcesIndependent()
    {
        var r1 = SwiftBindingsTestLib.CreateUniqueResource(1);
        var r2 = SwiftBindingsTestLib.CreateUniqueResource(2);

        r1.Payload.Dispose();

        // r2 should still work
        AssertEqual(2, r2.Id, "Second resource unaffected by first dispose");
        AssertEqual(2, r2.Inspect(), "Second resource methods work");

        TestLogger.Info("Independent UniqueResources have independent lifetimes");
    }

    [TestTier(TestTier.Tier2)]
    public void TestSharedReferenceDispose()
    {
        // Two C# variables pointing to the SAME Swift object.
        // Disposing via one reference invalidates both, because they share
        // the same SwiftSafeHandle — C# reference semantics means both
        // variables point to the same managed wrapper object.
        var animal = SwiftBindingsTestLib.CreateAnimal("Shared", "Moo");
        var alias = animal;

        // Both references see the same data
        AssertEqual("Shared", alias.Name.ToString(), "Alias sees same name");

        // Dispose via the alias
        alias.Payload.Dispose();

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
        var animal = SwiftBindingsTestLib.CreateAnimal("SharedMethod", "Woof");
        var alias = animal;

        // Verify both work before dispose
        AssertNotNull(animal.Speak(), "Original speaks before dispose");
        AssertNotNull(alias.Describe(), "Alias describes before dispose");

        // Dispose via original
        animal.Payload.Dispose();

        // Method call via alias should also throw
        AssertThrows<ObjectDisposedException>(() =>
        {
            _ = alias.Speak();
        }, "Alias method call throws after original disposed");

        TestLogger.Info("Shared references: method call via alias throws after dispose");
    }

    #endregion

    #region Ownership Transfer Patterns

    [TestTier(TestTier.Tier2)]
    public void TestBorrowResourcePreservesOwnership()
    {
        // BorrowResource should not consume the resource
        var resource = SwiftBindingsTestLib.CreateUniqueResource(42);

        var borrowed = SwiftBindingsTestLib.BorrowResource(resource);
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

        var inspected1 = resource.Inspect();
        var inspected2 = resource.Inspect();
        AssertEqual(55, inspected1, "First inspect returns correct id");
        AssertEqual(55, inspected2, "Second inspect returns correct id");

        // Still accessible after multiple inspects
        AssertEqual(55, resource.Id, "Id still accessible after inspects");

        TestLogger.Info("Inspect preserves access across multiple calls");
    }

    #endregion

    #region GC Stress with Ownership

    [TestTier(TestTier.Tier3)]
    public void TestObjectSurvivesRepeatedGC()
    {
        var animal = SwiftBindingsTestLib.CreateAnimal("Survivor", "Roar");

        // Multiple GC cycles
        for (int i = 0; i < 10; i++)
        {
            ForceGC();
            var name = animal.Name.ToString();
            AssertEqual("Survivor", name, $"Survives GC cycle {i}");
        }

        TestLogger.Info("Object survives 10 GC cycles");
    }

    [TestTier(TestTier.Tier3)]
    public void TestManyObjectsCreateAndAbandon()
    {
        // Create many objects and let them go — GC should clean up without crash
        for (int i = 0; i < 100; i++)
        {
            var animal = SwiftBindingsTestLib.CreateAnimal($"Temp{i}", "Sound");
            _ = animal.Name.ToString();
            // Intentionally not holding reference — GC will collect
        }

        ForceGC();

        // Create one more to verify the system is still healthy
        var final = SwiftBindingsTestLib.CreateAnimal("Final", "OK");
        AssertEqual("Final", final.Name.ToString(), "System healthy after mass abandonment");

        TestLogger.Info("100 objects created and abandoned without crash");
    }

    [TestTier(TestTier.Tier3)]
    public void TestInterleavedCreateDispose()
    {
        // Interleave creation and disposal
        var animals = new List<Animal>();

        for (int i = 0; i < 20; i++)
        {
            animals.Add(SwiftBindingsTestLib.CreateAnimal($"Animal{i}", $"Sound{i}"));

            // Dispose every 5th object
            if (i % 5 == 4 && animals.Count > 0)
            {
                var toDispose = animals[0];
                toDispose.Payload.Dispose();
                animals.RemoveAt(0);
            }
        }

        // Verify remaining animals are still valid
        foreach (var animal in animals)
        {
            var name = animal.Name.ToString();
            AssertNotNull(name, "Remaining animal has valid name");
        }

        TestLogger.Info("Interleaved create/dispose completed without corruption");
    }

    [TestTier(TestTier.Tier3)]
    public void TestGCPressureDuringPropertyAccess()
    {
        var animal = SwiftBindingsTestLib.CreateAnimal("Pressure", "Test");

        // Access properties while creating GC pressure
        for (int i = 0; i < 50; i++)
        {
            // Create garbage
            _ = new byte[4096];

            // Access Swift object
            var name = animal.Name.ToString();
            AssertEqual("Pressure", name, $"Property access under GC pressure {i}");
        }

        ForceGC();

        // Still works after pressure
        AssertEqual("Pressure", animal.Name.ToString(), "Survives GC pressure loop");

        TestLogger.Info("Property access stable under GC pressure");
    }

    #endregion
}
