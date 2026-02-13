// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Tests for class/struct marshalling: instantiation, property access, method calls,
/// SafeHandle lifecycle, and ownership patterns.
/// </summary>
public class ClassMarshallingTests : TestBase
{
    public ClassMarshallingTests(TestResults results) : base(results) { }

    #region Animal Class (via factory function)

    [TestTier(TestTier.Tier1)]
    public void TestAnimalCreation()
    {
        var animal = SwiftBindingsTestLib.CreateAnimal("Rex", "Bark");
        AssertNotNull(animal, "Animal created");
        TestLogger.Info("Animal creation passed");
    }

    [TestTier(TestTier.Tier1)]
    public void TestAnimalPropertyGet()
    {
        var animal = SwiftBindingsTestLib.CreateAnimal("Rex", "Bark");

        // Properties return SwiftString, need .ToString()
        var name = animal.Name.ToString();
        AssertEqual("Rex", name, "Animal.Name getter");

        var sound = animal.Sound.ToString();
        AssertEqual("Bark", sound, "Animal.Sound getter");

        TestLogger.Info($"Animal properties: Name={name}, Sound={sound}");
    }

    [TestTier(TestTier.Tier2)]
    public void TestAnimalPropertySet()
    {
        var animal = SwiftBindingsTestLib.CreateAnimal("Rex", "Bark");

        // Set new values via SwiftString
        animal.Name = new SwiftString("Buddy");
        var newName = animal.Name.ToString();
        AssertEqual("Buddy", newName, "Animal.Name setter");

        animal.Sound = new SwiftString("Woof");
        var newSound = animal.Sound.ToString();
        AssertEqual("Woof", newSound, "Animal.Sound setter");

        TestLogger.Info($"Animal property set: Name={newName}, Sound={newSound}");
    }

    [TestTier(TestTier.Tier1)]
    public void TestAnimalSpeak()
    {
        var animal = SwiftBindingsTestLib.CreateAnimal("Rex", "Bark");
        var result = animal.GetSpeak();

        AssertNotNull(result, "Speak result not null");
        AssertTrue(result.Contains("Bark"), "Speak contains sound");
        TestLogger.Info($"Animal.GetSpeak() = \"{result}\"");
    }

    [TestTier(TestTier.Tier1)]
    public void TestAnimalDescribe()
    {
        var animal = SwiftBindingsTestLib.CreateAnimal("Rex", "Bark");
        var result = animal.GetDescribe();

        AssertNotNull(result, "Describe result not null");
        // describe() returns "Animal: \(name)" — name only, no sound
        AssertTrue(result.Contains("Rex"), "Describe contains name");
        TestLogger.Info($"Animal.GetDescribe() = \"{result}\"");
    }

    [TestTier(TestTier.Tier2)]
    public void TestAnimalUnicodeProperties()
    {
        // Test unicode in class string properties
        var animal = SwiftBindingsTestLib.CreateAnimal("犬", "ワン");

        var name = animal.Name.ToString();
        AssertEqual("犬", name, "Unicode name property");

        var sound = animal.Sound.ToString();
        AssertEqual("ワン", sound, "Unicode sound property");

        TestLogger.Info("Animal unicode properties passed");
    }

    #endregion

    #region UniqueResource (via public constructor)

    [TestTier(TestTier.Tier1)]
    public void TestUniqueResourceCreation()
    {
        var resource = SwiftBindingsTestLib.CreateUniqueResource(42);
        AssertNotNull(resource, "UniqueResource created");

        var id = resource.Id;
        AssertEqual(42, id, "UniqueResource.Id");
        TestLogger.Info($"UniqueResource created with Id={id}");
    }

    [TestTier(TestTier.Tier2)]
    public void TestUniqueResourceConstructor()
    {
        // Test the public constructor directly
        var resource = new UniqueResource(99);
        var id = resource.Id;
        AssertEqual(99, id, "UniqueResource constructor Id");
        TestLogger.Info($"UniqueResource constructor with Id={id}");
    }

    [TestTier(TestTier.Tier2)]
    public void TestBorrowResource()
    {
        var resource = SwiftBindingsTestLib.CreateUniqueResource(7);
        var borrowed = SwiftBindingsTestLib.GetBorrowResource(resource);

        // BorrowResource should return the Id
        AssertEqual(7, borrowed, "BorrowResource returns Id");
        TestLogger.Info($"BorrowResource returned {borrowed}");
    }

    #endregion

    #region MutableProps Struct (property get/set)

    [TestTier(TestTier.Tier3)] // MutableProps constructor P/Invoke uses CallConvSwift → frame tracker corruption → cascading crash
    public void TestMutablePropsCreation()
    {
        var props = new MutableProps(42, "TestName");

        var value = props.Value;
        AssertEqual(42, value, "MutableProps.Value initial");

        var name = props.Name.ToString();
        AssertEqual("TestName", name, "MutableProps.Name initial");

        TestLogger.Info($"MutableProps: Value={value}, Name={name}");
    }

    [TestTier(TestTier.Tier3)] // MutableProps constructor P/Invoke uses CallConvSwift → frame tracker corruption → cascading crash
    public void TestMutablePropsSetValue()
    {
        var props = new MutableProps(10, "Original");

        // Modify the int property
        props.Value = 99;
        AssertEqual(99, props.Value, "MutableProps.Value after set");

        // Modify the string property
        props.Name = new SwiftString("Modified");
        AssertEqual("Modified", props.Name.ToString(), "MutableProps.Name after set");

        TestLogger.Info("MutableProps property set tests passed");
    }

    #endregion

    #region StaticMethods (static method calls)

    [TestTier(TestTier.Tier2)]
    public void TestStaticMethodGetSet()
    {
        // Static methods on StaticMethods type
        StaticMethods.SetStoredValue(42);
        var result = StaticMethods.GetStoredValue();
        AssertEqual(42, result, "Static GetStoredValue after SetStoredValue");

        TestLogger.Info($"StaticMethods get/set: {result}");
    }

    [TestTier(TestTier.Tier2)]
    public void TestStaticMethodIncrement()
    {
        StaticMethods.SetStoredValue(0);

        var result = StaticMethods.IncrementAndGet();
        AssertEqual(1, result, "IncrementAndGet from 0");

        result = StaticMethods.IncrementAndGet();
        AssertEqual(2, result, "IncrementAndGet from 1");

        TestLogger.Info("StaticMethods increment tests passed");
    }

    #endregion

    #region Multiple Instances

    [TestTier(TestTier.Tier2)]
    public void TestMultipleAnimalInstances()
    {
        // Create multiple independent instances
        var cat = SwiftBindingsTestLib.CreateAnimal("Cat", "Meow");
        var dog = SwiftBindingsTestLib.CreateAnimal("Dog", "Woof");

        // Verify they're independent
        AssertEqual("Cat", cat.Name.ToString(), "Cat name");
        AssertEqual("Dog", dog.Name.ToString(), "Dog name");

        // Modify one, check the other is unaffected
        cat.Name = new SwiftString("Kitty");
        AssertEqual("Kitty", cat.Name.ToString(), "Cat renamed");
        AssertEqual("Dog", dog.Name.ToString(), "Dog unaffected");

        TestLogger.Info("Multiple instances are independent");
    }

    #endregion

    #region SafeHandle Lifecycle

    [TestTier(TestTier.Tier2)]
    public void TestPayloadDisposePreventsFurtherAccess()
    {
        // Create an animal, dispose its SafeHandle, then verify access throws
        var animal = SwiftBindingsTestLib.CreateAnimal("Ephemeral", "Poof");

        // Verify it works before dispose
        var name = animal.Name.ToString();
        AssertEqual("Ephemeral", name, "Name accessible before dispose");

        // Dispose the underlying SafeHandle
        animal.Dispose();

        // Access after dispose should throw ObjectDisposedException
        AssertThrows<ObjectDisposedException>(() =>
        {
            _ = animal.Name;
        }, "Property access after dispose throws ObjectDisposedException");

        TestLogger.Info("SafeHandle use-after-dispose correctly throws");
    }

    [TestTier(TestTier.Tier2)]
    public void TestPayloadDisposePreventsMethods()
    {
        var animal = SwiftBindingsTestLib.CreateAnimal("Ghost", "Boo");

        // Verify method works before dispose
        var speak = animal.GetSpeak();
        AssertNotNull(speak, "Speak works before dispose");

        // Dispose the underlying SafeHandle
        animal.Dispose();

        // Method call after dispose should throw
        AssertThrows<ObjectDisposedException>(() =>
        {
            _ = animal.GetSpeak();
        }, "Method call after dispose throws ObjectDisposedException");

        TestLogger.Info("SafeHandle method-after-dispose correctly throws");
    }

    #endregion

    #region GC Survival

    [TestTier(TestTier.Tier3)]
    public void TestClassSurvivesGCPressure()
    {
        var animal = SwiftBindingsTestLib.CreateAnimal("Survivor", "Roar");

        // Create GC pressure
        CreateGCPressure(5000);

        // Object should still be accessible
        var name = animal.Name.ToString();
        AssertEqual("Survivor", name, "Object survives GC pressure");

        var speak = animal.GetSpeak();
        AssertNotNull(speak, "Methods work after GC pressure");

        TestLogger.Info("Class survives GC pressure");
    }

    [TestTier(TestTier.Tier3)]
    public void TestMultipleObjectsGCPressure()
    {
        // Create several objects, apply GC pressure, verify all survive
        var objects = new List<Animal>();
        for (int i = 0; i < 10; i++)
        {
            objects.Add(SwiftBindingsTestLib.CreateAnimal($"Animal{i}", $"Sound{i}"));
        }

        CreateGCPressure(5000);

        for (int i = 0; i < 10; i++)
        {
            var name = objects[i].Name.ToString();
            AssertEqual($"Animal{i}", name, $"Object {i} survives GC");
        }

        TestLogger.Info("Multiple objects survive GC pressure");
    }

    #endregion
}
