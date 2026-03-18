// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Lifetime;

/// <summary>
/// GC stress tests that trigger ForceGC() or construct MutableProps.
/// Separated from OwnershipTests for independent failure isolation.
/// </summary>
[SkipOnSimulator("GC stress triggers Mono finalizer thread Sys:Free crash (jit-info.c:918) that kills the process")]
public class OwnershipGCStressTests : TestBase
{
    public OwnershipGCStressTests(TestResults results) : base(results) { }

    #region Basic Retain/Release Balance (ForceGC)

    public void TestAnimalCreateUseRelease()
    {
        // Create object, use it, let it go out of scope, GC — no crash
        var animal = TestLibFunctions.CreateAnimal("Temp", "Woof");
        var name = animal.Name.ToString();
        AssertEqual("Temp", name, "Animal accessible after creation");

        var speak = animal.GetSpeak();
        AssertNotNull(speak, "Speak returns result");

        // Let the reference go and force GC — should not crash
        animal = null!;
        ForceGC();

        TestLogger.Info("Create-use-release cycle completed without crash");
    }

    [Skip("UniqueResource is ~Copyable: noncopyable types not yet supported by generator")]
    public void TestUniqueResourceCreateUseRelease()
    {
        // UniqueResource via factory
        var resource = TestLibFunctions.CreateUniqueResource(42);
        var id = resource.Id;
        AssertEqual(42, id, "UniqueResource.Id accessible");

        var inspected = resource.GetInspect();
        AssertEqual(42, inspected, "Inspect returns correct id");

        resource = null!;
        ForceGC();

        TestLogger.Info("UniqueResource create-use-release completed");
    }

    [Skip("UniqueResource is ~Copyable: noncopyable types not yet supported by generator")]
    public void TestUniqueResourceConstructorLifecycle()
    {
        // UniqueResource via public constructor
        var resource = new UniqueResource(99);
        AssertEqual(99, resource.Id, "Constructor-created resource has correct Id");

        resource = null!;
        ForceGC();

        TestLogger.Info("UniqueResource constructor lifecycle completed");
    }

    #endregion

    #region MutableProps (CallConvSwift constructor)

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

    public void TestMutablePropsDoubleDispose()
    {
        var props = new MutableProps(5, "DoubleDispose");
        AssertEqual(5, props.Value, "Accessible before dispose");

        props.Dispose();
        props.Dispose();

        TestLogger.Info("MutableProps double-dispose safe");
    }

    public void TestMutablePropsAccessAfterDispose()
    {
        var props = new MutableProps(10, "Test");
        props.Dispose();

        AssertThrows<ObjectDisposedException>(() =>
        {
            _ = props.Value;
        }, "MutableProps.Value after dispose throws");

        TestLogger.Info("MutableProps access after dispose correctly throws");
    }

    public void TestMutablePropsSetAfterDispose()
    {
        var props = new MutableProps(10, "Test");
        props.Dispose();

        AssertThrows<ObjectDisposedException>(() =>
        {
            props.Value = 99;
        }, "MutableProps.Value set after dispose throws");

        TestLogger.Info("MutableProps set after dispose correctly throws");
    }

    #endregion

    #region GC Stress with Ownership

    public void TestObjectSurvivesRepeatedGC()
    {
        var animal = TestLibFunctions.CreateAnimal("Survivor", "Roar");

        // Multiple GC cycles
        for (int i = 0; i < 10; i++)
        {
            ForceGC();
            var name = animal.Name.ToString();
            AssertEqual("Survivor", name, $"Survives GC cycle {i}");
        }

        TestLogger.Info("Object survives 10 GC cycles");
    }

    public void TestManyObjectsCreateAndAbandon()
    {
        // Create many objects and let them go — GC should clean up without crash
        for (int i = 0; i < 100; i++)
        {
            var animal = TestLibFunctions.CreateAnimal($"Temp{i}", "Sound");
            _ = animal.Name.ToString();
            // Intentionally not holding reference — GC will collect
        }

        ForceGC();

        // Create one more to verify the system is still healthy
        var final = TestLibFunctions.CreateAnimal("Final", "OK");
        AssertEqual("Final", final.Name.ToString(), "System healthy after mass abandonment");

        TestLogger.Info("100 objects created and abandoned without crash");
    }

    public void TestInterleavedCreateDispose()
    {
        // Interleave creation and disposal
        var animals = new List<Animal>();

        for (int i = 0; i < 20; i++)
        {
            animals.Add(TestLibFunctions.CreateAnimal($"Animal{i}", $"Sound{i}"));

            // Dispose every 5th object
            if (i % 5 == 4 && animals.Count > 0)
            {
                var toDispose = animals[0];
                toDispose.Dispose();
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

    public void TestGCPressureDuringPropertyAccess()
    {
        var animal = TestLibFunctions.CreateAnimal("Pressure", "Test");

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
