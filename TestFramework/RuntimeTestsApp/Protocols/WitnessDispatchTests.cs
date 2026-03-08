// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using SwiftTaskStatus = SwiftBindingsTestLib.TaskStatus;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Protocol interface projection tests — verifies that generated C# types
/// implement the correct protocol interfaces and that method/property dispatch
/// works through interface casts to concrete types.
///
/// NOTE: These tests exercise the *interface projection* path (concrete type cast
/// to C# interface, dispatching to the concrete P/Invoke symbol). They do NOT
/// exercise the existential *witness-dispatch proxy* path (C# implementing a
/// Swift protocol via existential containers and witness tables). Proxy-based
/// witness dispatch requires the SwiftBindings wrapper library to be compiled
/// into the runtime app bundle, which is not yet set up for TestFramework.
///
/// Class name sorts alphabetically BEFORE EnumMarshallingTests (the Mono JIT
/// crash point), ensuring these tests complete before the process is killed.
///
/// Tier structure:
/// - Tier 1: Blittable-only (Int32 properties/methods through interfaces)
/// - Tier 2: String methods (return idiomatic string), enum methods (Int32 raw value)
/// - Tier 3: SwiftString property access (Mono JIT risk), TaskPriority (String raw value needs wrapper lib)
/// </summary>
public class BasicProtocolDispatchTests : TestBase
{
    public BasicProtocolDispatchTests(TestResults results) : base(results) { }

    #region Protocol Conformance Checks (Tier 1)

    [TestTier(TestTier.Tier3)] // Class constructor with string params: non-blittable through CallConvSwift
    public void TestSimpleItemConformance()
    {
        var item = new SimpleItem(id: "c1", label: "Check");
        AssertTrue(item is IDescribable, "SimpleItem is IDescribable");
        AssertTrue(item is ITestIdentifiable, "SimpleItem is ITestIdentifiable");
        TestLogger.Info("SimpleItem conforms to IDescribable + ITestIdentifiable");
    }

    [TestTier(TestTier.Tier1)]
    public void TestMutableItemConformance()
    {
        var item = new MutableItem(value: 0);
        AssertTrue(item is IHasValue, "MutableItem is IHasValue");
        TestLogger.Info("MutableItem conforms to IHasValue");
    }

    [TestTier(TestTier.Tier3)] // Class constructor with string params: non-blittable through CallConvSwift
    public void TestDisplayItemConformance()
    {
        var item = new DisplayItem(text: "Hi");
        AssertTrue(item is IDisplayable, "DisplayItem is IDisplayable");
        AssertTrue(item is IDescribable, "DisplayItem is IDescribable");
        TestLogger.Info("DisplayItem conforms to IDisplayable + IDescribable");
    }

    [TestTier(TestTier.Tier1)]
    public void TestMultiConformingValueConformance()
    {
        // MultiConformingValue is a frozen struct that implements all 4 arithmetic protocols.
        // Use typeof() check since struct `is` interface is always true at compile time (CS0183).
        AssertTrue(typeof(IAddable).IsAssignableFrom(typeof(MultiConformingValue)),
            "MultiConformingValue implements IAddable");
        AssertTrue(typeof(ISubtractable).IsAssignableFrom(typeof(MultiConformingValue)),
            "MultiConformingValue implements ISubtractable");
        AssertTrue(typeof(IMultipliable).IsAssignableFrom(typeof(MultiConformingValue)),
            "MultiConformingValue implements IMultipliable");
        AssertTrue(typeof(IDividable).IsAssignableFrom(typeof(MultiConformingValue)),
            "MultiConformingValue implements IDividable");
        TestLogger.Info("MultiConformingValue conforms to 4 arithmetic protocols");
    }

    [TestTier(TestTier.Tier3)] // Class constructor with string params: non-blittable through CallConvSwift
    public void TestPersonConformance()
    {
        var person = new Person(name: "Alice", age: 30);
        AssertTrue(person is INameable, "Person is INameable");
        AssertTrue(person is IAgeable, "Person is IAgeable");
        TestLogger.Info("Person conforms to INameable + IAgeable");
    }

    #endregion

    #region Blittable Property/Method Dispatch Through Interface (Tier 1)

    [TestTier(TestTier.Tier1)]
    public void TestHasValueGetThroughInterface()
    {
        var item = new MutableItem(value: 42);
        var iface = (IHasValue)item;
        AssertEqual(42, iface.Value, "IHasValue.Value get");
        TestLogger.Info($"((IHasValue)MutableItem).Value = {iface.Value}");
    }

    [TestTier(TestTier.Tier1)]
    public void TestHasValueSetThroughInterface()
    {
        var item = new MutableItem(value: 10);
        var iface = (IHasValue)item;
        iface.Value = 99;
        AssertEqual(99, iface.Value, "IHasValue.Value after set");
        TestLogger.Info($"IHasValue.Value = 99, get => {iface.Value}");
    }

    [TestTier(TestTier.Tier1)]
    public void TestGetValueMethodThroughInterface()
    {
        var item = new MutableItem(value: 77);
        var iface = (IHasValue)item;
        AssertEqual(77, iface.GetValue(), "IHasValue.GetValue()");
        TestLogger.Info($"((IHasValue)MutableItem).GetValue() = {iface.GetValue()}");
    }

    [TestTier(TestTier.Tier1)]
    public void TestSetValueMethodThroughInterface()
    {
        var item = new MutableItem(value: 0);
        var iface = (IHasValue)item;
        iface.SetValue(55);
        AssertEqual(55, iface.GetValue(), "IHasValue after SetValue(55)");
        TestLogger.Info($"IHasValue.SetValue(55), GetValue() => {iface.GetValue()}");
    }

    [TestTier(TestTier.Tier1)]
    public void TestMultiConformingAddThroughInterface()
    {
        var val = new MultiConformingValue(value: 10);
        var iface = (IAddable)val;
        AssertEqual(15, iface.Add(5), "IAddable.Add(5) on value=10");
        TestLogger.Info($"((IAddable)MultiConformingValue(10)).Add(5) = {iface.Add(5)}");
    }

    [TestTier(TestTier.Tier1)]
    public void TestMultiConformingSubtractThroughInterface()
    {
        var val = new MultiConformingValue(value: 20);
        var iface = (ISubtractable)val;
        AssertEqual(15, iface.Subtract(5), "ISubtractable.Subtract(5) on value=20");
        TestLogger.Info($"((ISubtractable)MultiConformingValue(20)).Subtract(5) = {iface.Subtract(5)}");
    }

    [TestTier(TestTier.Tier1)]
    public void TestMultiConformingMultiplyThroughInterface()
    {
        var val = new MultiConformingValue(value: 7);
        var iface = (IMultipliable)val;
        AssertEqual(21, iface.Multiply(3), "IMultipliable.Multiply(3) on value=7");
        TestLogger.Info($"((IMultipliable)MultiConformingValue(7)).Multiply(3) = {iface.Multiply(3)}");
    }

    [TestTier(TestTier.Tier1)]
    public void TestMultiConformingDivideThroughInterface()
    {
        var val = new MultiConformingValue(value: 100);
        var iface = (IDividable)val;
        AssertEqual(25, iface.Divide(4), "IDividable.Divide(4) on value=100");
        TestLogger.Info($"((IDividable)MultiConformingValue(100)).Divide(4) = {iface.Divide(4)}");
    }

    [TestTier(TestTier.Tier1)]
    public void TestPersonAgeThroughInterface()
    {
        var person = new Person(name: "Bob", age: 25);
        var iface = (IAgeable)person;
        AssertEqual(25, iface.Age, "IAgeable.Age");
        TestLogger.Info($"((IAgeable)Person).Age = {iface.Age}");
    }

    #endregion

    #region String Method Dispatch Through Interface (Tier 2)

    [TestTier(TestTier.Tier3)] // Class constructor with string params: InvalidCastException at runtime
    public void TestDescribeMethodThroughInterface()
    {
        var item = new SimpleItem(id: "s1", label: "Widget");
        var iface = (IDescribable)item;
        var desc = iface.GetDescribe();
        AssertTrue(desc.Contains("s1"), "Describe() contains id");
        AssertTrue(desc.Contains("Widget"), "Describe() contains label");
        TestLogger.Info($"((IDescribable)SimpleItem).GetDescribe() = \"{desc}\"");
    }

    [TestTier(TestTier.Tier2)]
    public void TestDisplayMethodThroughInterface()
    {
        var item = new DisplayItem(text: "Hello");
        var iface = (IDisplayable)item;
        AssertEqual("Display: Hello", iface.GetDisplay(), "IDisplayable.GetDisplay()");
        TestLogger.Info($"((IDisplayable)DisplayItem).GetDisplay() = \"{iface.GetDisplay()}\"");
    }

    [TestTier(TestTier.Tier3)] // Class constructor with string params: InvalidCastException at runtime
    public void TestInheritedDescribeThroughDisplayable()
    {
        var item = new DisplayItem(text: "World");
        // DisplayItem implements IDisplayable which inherits IDescribable
        var iface = (IDescribable)item;
        AssertEqual("Describe: World", iface.GetDescribe(), "Inherited Describe() through Displayable");
        TestLogger.Info($"((IDescribable)DisplayItem).GetDescribe() = \"{iface.GetDescribe()}\"");
    }

    [TestTier(TestTier.Tier2)]
    public void TestEchoProcessorProcessThroughInterface()
    {
        var proc = new EchoProcessor(prefix: "Echo");
        var iface = (IStringProcessor)proc;
        var result = iface.Process(input: "test");
        AssertEqual("Echo: test", result, "IStringProcessor.Process()");
        TestLogger.Info($"((IStringProcessor)EchoProcessor).Process(\"test\") = \"{result}\"");
    }

    [TestTier(TestTier.Tier2)]
    public void TestEchoProcessorGetOutputThroughInterface()
    {
        var proc = new EchoProcessor(prefix: "Proc");
        var iface = (IStringProcessor)proc;
        var output = iface.GetOutput();
        AssertEqual("Proc: ready", output, "IStringProcessor.GetOutput()");
        TestLogger.Info($"((IStringProcessor)EchoProcessor).GetOutput() = \"{output}\"");
    }

    #endregion

    #region Enum Method Dispatch Through Interface (Tier 2)

    [TestTier(TestTier.Tier2)]
    public void TestStatusHandlerGetCurrentStatus()
    {
        var handler = new SimpleStatusHandler(initialStatus: SwiftTaskStatus.Pending);
        var iface = (IStatusHandler)handler;
        var status = iface.GetCurrentStatus();
        AssertEqual(SwiftTaskStatus.Pending, status, "Initial status should be Pending");
        TestLogger.Info($"((IStatusHandler)handler).GetCurrentStatus() = {status}");
    }

    [TestTier(TestTier.Tier2)]
    public void TestStatusHandlerTransitionStatus()
    {
        var handler = new SimpleStatusHandler(initialStatus: SwiftTaskStatus.Pending);
        var iface = (IStatusHandler)handler;
        var next = iface.TransitionStatus(from: SwiftTaskStatus.Pending);
        AssertEqual(SwiftTaskStatus.Running, next, "Transition from Pending should be Running");
        TestLogger.Info($"TransitionStatus(Pending) = {next}");
    }

    [TestTier(TestTier.Tier2)]
    public void TestStatusHandlerHandleStatus()
    {
        var handler = new SimpleStatusHandler(initialStatus: SwiftTaskStatus.Pending);
        var iface = (IStatusHandler)handler;
        iface.HandleStatus(SwiftTaskStatus.Running);
        var current = iface.GetCurrentStatus();
        AssertEqual(SwiftTaskStatus.Running, current, "After HandleStatus(Running)");
        TestLogger.Info($"After HandleStatus(Running), GetCurrentStatus() = {current}");
    }

    // Tier 3: TaskPriority has String raw value — FromRawValue("high") routes through
    // SwiftBindings wrapper library, which isn't bundled in RuntimeTestsApp.
    // TaskStatus (Int32 raw value) works because FromRawValue(int) goes directly to native lib.
    [TestTier(TestTier.Tier3)]
    public void TestPriorityHandlerGetPriority()
    {
        var handler = new SimplePriorityHandler(initialPriority: TaskPriority.High);
        var iface = (IPriorityHandler)handler;
        var priority = iface.GetPriority();
        AssertEqual(TaskPriority.CaseTag.High, priority.Tag, "Initial priority should be High");
        TestLogger.Info($"((IPriorityHandler)handler).GetPriority().Tag = {priority.Tag}");
    }

    [TestTier(TestTier.Tier3)]
    public void TestPriorityHandlerSetPriority()
    {
        var handler = new SimplePriorityHandler(initialPriority: TaskPriority.Low);
        var iface = (IPriorityHandler)handler;
        iface.SetPriority(TaskPriority.Critical);
        var priority = iface.GetPriority();
        AssertEqual(TaskPriority.CaseTag.Critical, priority.Tag, "After SetPriority(Critical)");
        TestLogger.Info($"After SetPriority(Critical), GetPriority().Tag = {priority.Tag}");
    }

    [TestTier(TestTier.Tier3)]
    public void TestPriorityHandlerIsHigherPriority()
    {
        var handler = new SimplePriorityHandler(initialPriority: TaskPriority.High);
        var iface = (IPriorityHandler)handler;
        AssertTrue(iface.IsHigherPriority(other: TaskPriority.Low), "High > Low");
        AssertFalse(iface.IsHigherPriority(other: TaskPriority.Critical), "High < Critical");
        TestLogger.Info("IsHigherPriority: High > Low = true, High < Critical = false");
    }

    #endregion

    #region Enum Property Dispatch Through Interface (Tier 3 — String raw value enum needs wrapper lib)

    [TestTier(TestTier.Tier3)]
    public void TestPrioritizedPropertyGetThroughInterface()
    {
        var item = new PrioritizedItem(priority: TaskPriority.Medium);
        var iface = (IPrioritized)item;
        AssertEqual(TaskPriority.CaseTag.Medium, iface.Priority.Tag, "IPrioritized.Priority");
        TestLogger.Info($"((IPrioritized)PrioritizedItem).Priority.Tag = {iface.Priority.Tag}");
    }

    [TestTier(TestTier.Tier3)]
    public void TestMutablePrioritizedPropertySetThroughInterface()
    {
        var item = new MutablePrioritizedItem(priority: TaskPriority.Low);
        var iface = (IMutablePrioritized)item;
        AssertEqual(TaskPriority.CaseTag.Low, iface.Priority.Tag, "Initial priority Low");
        iface.Priority = TaskPriority.High;
        AssertEqual(TaskPriority.CaseTag.High, iface.Priority.Tag, "After set to High");
        TestLogger.Info($"IMutablePrioritized: Low -> High, Tag = {iface.Priority.Tag}");
    }

    [TestTier(TestTier.Tier2)]
    public void TestTaskStatusRawValue()
    {
        // TaskStatus is a simple C# enum (Int32 raw value)
        var status = SwiftTaskStatus.Running;
        AssertEqual(SwiftTaskStatus.Running, status, "SwiftTaskStatus.Running");
        AssertTrue((int)SwiftTaskStatus.Running != (int)SwiftTaskStatus.Pending, "Running != Pending");
        TestLogger.Info($"SwiftTaskStatus.Running = {(int)status}");
    }

    #endregion

    #region SwiftString Property Access Through Interface (Tier 3 — Mono JIT risk)

    [TestTier(TestTier.Tier3)]
    public void TestDescriptionPropertyThroughInterface()
    {
        var item = new SimpleItem(id: "p1", label: "Proto");
        var iface = (IDescribable)item;
        var desc = iface.Description.ToString();
        AssertTrue(desc.Contains("p1"), "Description contains id");
        TestLogger.Info($"IDescribable.Description = \"{desc}\"");
    }

    [TestTier(TestTier.Tier3)]
    public void TestIdPropertyThroughInterface()
    {
        var item = new SimpleItem(id: "id-42", label: "Test");
        var iface = (ITestIdentifiable)item;
        var id = iface.Id.ToString();
        AssertEqual("id-42", id, "ITestIdentifiable.Id");
        TestLogger.Info($"ITestIdentifiable.Id = \"{id}\"");
    }

    [TestTier(TestTier.Tier3)]
    public void TestNameablePropertyThroughInterface()
    {
        var person = new Person(name: "Charlie", age: 40);
        var iface = (INameable)person;
        var name = iface.Name.ToString();
        AssertEqual("Charlie", name, "INameable.Name");
        TestLogger.Info($"INameable.Name = \"{name}\"");
    }

    [TestTier(TestTier.Tier3)]
    public void TestNamedPropertyGetThroughInterface()
    {
        var item = new NamedItem(name: "TestItem");
        var iface = (INamed)item;
        var name = iface.Name.ToString();
        AssertEqual("TestItem", name, "INamed.Name");
        TestLogger.Info($"INamed.Name = \"{name}\"");
    }

    [TestTier(TestTier.Tier3)]
    public void TestMutableNamedPropertySetThroughInterface()
    {
        var item = new MutableNamedItem(name: "Original");
        var iface = (IMutableNamed)item;
        var origName = iface.Name.ToString();
        AssertEqual("Original", origName, "Initial name");
        iface.Name = new Swift.SwiftString("Updated");
        var newName = iface.Name.ToString();
        AssertEqual("Updated", newName, "After name set");
        TestLogger.Info($"IMutableNamed.Name: \"{origName}\" -> \"{newName}\"");
    }

    #endregion
}
