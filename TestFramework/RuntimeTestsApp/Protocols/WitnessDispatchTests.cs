// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.SwiftBindingsTestLib;
using SwiftTaskStatus = Swift.SwiftBindingsTestLib.TaskStatus;

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

    [TestTier(TestTier.Tier1)]
    public void TestSimpleItemConformance()
    {
        var item = new SimpleItem(id: "c1", label: "Check");
        AssertTrue(item is ISwiftDescribable, "SimpleItem is ISwiftDescribable");
        AssertTrue(item is ISwiftTestIdentifiable, "SimpleItem is ISwiftTestIdentifiable");
        TestLogger.Info("SimpleItem conforms to ISwiftDescribable + ISwiftTestIdentifiable");
    }

    [TestTier(TestTier.Tier1)]
    public void TestMutableItemConformance()
    {
        var item = new MutableItem(value: 0);
        AssertTrue(item is ISwiftHasValue, "MutableItem is ISwiftHasValue");
        TestLogger.Info("MutableItem conforms to ISwiftHasValue");
    }

    [TestTier(TestTier.Tier1)]
    public void TestDisplayItemConformance()
    {
        var item = new DisplayItem(text: "Hi");
        AssertTrue(item is ISwiftDisplayable, "DisplayItem is ISwiftDisplayable");
        AssertTrue(item is ISwiftDescribable, "DisplayItem is ISwiftDescribable");
        TestLogger.Info("DisplayItem conforms to ISwiftDisplayable + ISwiftDescribable");
    }

    [TestTier(TestTier.Tier1)]
    public void TestMultiConformingValueConformance()
    {
        // MultiConformingValue is a frozen struct that implements all 4 arithmetic protocols.
        // Use typeof() check since struct `is` interface is always true at compile time (CS0183).
        AssertTrue(typeof(ISwiftAddable).IsAssignableFrom(typeof(MultiConformingValue)),
            "MultiConformingValue implements ISwiftAddable");
        AssertTrue(typeof(ISwiftSubtractable).IsAssignableFrom(typeof(MultiConformingValue)),
            "MultiConformingValue implements ISwiftSubtractable");
        AssertTrue(typeof(ISwiftMultipliable).IsAssignableFrom(typeof(MultiConformingValue)),
            "MultiConformingValue implements ISwiftMultipliable");
        AssertTrue(typeof(ISwiftDividable).IsAssignableFrom(typeof(MultiConformingValue)),
            "MultiConformingValue implements ISwiftDividable");
        TestLogger.Info("MultiConformingValue conforms to 4 arithmetic protocols");
    }

    [TestTier(TestTier.Tier1)]
    public void TestPersonConformance()
    {
        var person = new Person(name: "Alice", age: 30);
        AssertTrue(person is ISwiftNameable, "Person is ISwiftNameable");
        AssertTrue(person is ISwiftAgeable, "Person is ISwiftAgeable");
        TestLogger.Info("Person conforms to ISwiftNameable + ISwiftAgeable");
    }

    #endregion

    #region Blittable Property/Method Dispatch Through Interface (Tier 1)

    [TestTier(TestTier.Tier1)]
    public void TestHasValueGetThroughInterface()
    {
        var item = new MutableItem(value: 42);
        var iface = (ISwiftHasValue)item;
        AssertEqual(42, iface.Value, "ISwiftHasValue.Value get");
        TestLogger.Info($"((ISwiftHasValue)MutableItem).Value = {iface.Value}");
    }

    [TestTier(TestTier.Tier1)]
    public void TestHasValueSetThroughInterface()
    {
        var item = new MutableItem(value: 10);
        var iface = (ISwiftHasValue)item;
        iface.Value = 99;
        AssertEqual(99, iface.Value, "ISwiftHasValue.Value after set");
        TestLogger.Info($"ISwiftHasValue.Value = 99, get => {iface.Value}");
    }

    [TestTier(TestTier.Tier1)]
    public void TestGetValueMethodThroughInterface()
    {
        var item = new MutableItem(value: 77);
        var iface = (ISwiftHasValue)item;
        AssertEqual(77, iface.GetValue(), "ISwiftHasValue.GetValue()");
        TestLogger.Info($"((ISwiftHasValue)MutableItem).GetValue() = {iface.GetValue()}");
    }

    [TestTier(TestTier.Tier1)]
    public void TestSetValueMethodThroughInterface()
    {
        var item = new MutableItem(value: 0);
        var iface = (ISwiftHasValue)item;
        iface.SetValue(55);
        AssertEqual(55, iface.GetValue(), "ISwiftHasValue after SetValue(55)");
        TestLogger.Info($"ISwiftHasValue.SetValue(55), GetValue() => {iface.GetValue()}");
    }

    [TestTier(TestTier.Tier1)]
    public void TestMultiConformingAddThroughInterface()
    {
        var val = new MultiConformingValue(value: 10);
        var iface = (ISwiftAddable)val;
        AssertEqual(15, iface.Add(5), "ISwiftAddable.Add(5) on value=10");
        TestLogger.Info($"((ISwiftAddable)MultiConformingValue(10)).Add(5) = {iface.Add(5)}");
    }

    [TestTier(TestTier.Tier1)]
    public void TestMultiConformingSubtractThroughInterface()
    {
        var val = new MultiConformingValue(value: 20);
        var iface = (ISwiftSubtractable)val;
        AssertEqual(15, iface.Subtract(5), "ISwiftSubtractable.Subtract(5) on value=20");
        TestLogger.Info($"((ISwiftSubtractable)MultiConformingValue(20)).Subtract(5) = {iface.Subtract(5)}");
    }

    [TestTier(TestTier.Tier1)]
    public void TestMultiConformingMultiplyThroughInterface()
    {
        var val = new MultiConformingValue(value: 7);
        var iface = (ISwiftMultipliable)val;
        AssertEqual(21, iface.Multiply(3), "ISwiftMultipliable.Multiply(3) on value=7");
        TestLogger.Info($"((ISwiftMultipliable)MultiConformingValue(7)).Multiply(3) = {iface.Multiply(3)}");
    }

    [TestTier(TestTier.Tier1)]
    public void TestMultiConformingDivideThroughInterface()
    {
        var val = new MultiConformingValue(value: 100);
        var iface = (ISwiftDividable)val;
        AssertEqual(25, iface.Divide(4), "ISwiftDividable.Divide(4) on value=100");
        TestLogger.Info($"((ISwiftDividable)MultiConformingValue(100)).Divide(4) = {iface.Divide(4)}");
    }

    [TestTier(TestTier.Tier1)]
    public void TestPersonAgeThroughInterface()
    {
        var person = new Person(name: "Bob", age: 25);
        var iface = (ISwiftAgeable)person;
        AssertEqual(25, iface.Age, "ISwiftAgeable.Age");
        TestLogger.Info($"((ISwiftAgeable)Person).Age = {iface.Age}");
    }

    #endregion

    #region String Method Dispatch Through Interface (Tier 2)

    [TestTier(TestTier.Tier2)]
    public void TestDescribeMethodThroughInterface()
    {
        var item = new SimpleItem(id: "s1", label: "Widget");
        var iface = (ISwiftDescribable)item;
        var desc = iface.Describe();
        AssertTrue(desc.Contains("s1"), "Describe() contains id");
        AssertTrue(desc.Contains("Widget"), "Describe() contains label");
        TestLogger.Info($"((ISwiftDescribable)SimpleItem).Describe() = \"{desc}\"");
    }

    [TestTier(TestTier.Tier2)]
    public void TestDisplayMethodThroughInterface()
    {
        var item = new DisplayItem(text: "Hello");
        var iface = (ISwiftDisplayable)item;
        AssertEqual("Display: Hello", iface.Display(), "ISwiftDisplayable.Display()");
        TestLogger.Info($"((ISwiftDisplayable)DisplayItem).Display() = \"{iface.Display()}\"");
    }

    [TestTier(TestTier.Tier2)]
    public void TestInheritedDescribeThroughDisplayable()
    {
        var item = new DisplayItem(text: "World");
        // DisplayItem implements ISwiftDisplayable which inherits ISwiftDescribable
        var iface = (ISwiftDescribable)item;
        AssertEqual("Describe: World", iface.Describe(), "Inherited Describe() through Displayable");
        TestLogger.Info($"((ISwiftDescribable)DisplayItem).Describe() = \"{iface.Describe()}\"");
    }

    [TestTier(TestTier.Tier2)]
    public void TestEchoProcessorProcessThroughInterface()
    {
        var proc = new EchoProcessor(prefix: "Echo");
        var iface = (ISwiftStringProcessor)proc;
        var result = iface.Process(input: "test");
        AssertEqual("Echo: test", result, "ISwiftStringProcessor.Process()");
        TestLogger.Info($"((ISwiftStringProcessor)EchoProcessor).Process(\"test\") = \"{result}\"");
    }

    [TestTier(TestTier.Tier2)]
    public void TestEchoProcessorGetOutputThroughInterface()
    {
        var proc = new EchoProcessor(prefix: "Proc");
        var iface = (ISwiftStringProcessor)proc;
        var output = iface.GetOutput();
        AssertEqual("Proc: ready", output, "ISwiftStringProcessor.GetOutput()");
        TestLogger.Info($"((ISwiftStringProcessor)EchoProcessor).GetOutput() = \"{output}\"");
    }

    #endregion

    #region Enum Method Dispatch Through Interface (Tier 2)

    [TestTier(TestTier.Tier2)]
    public void TestStatusHandlerGetCurrentStatus()
    {
        var handler = new SimpleStatusHandler(initialStatus: SwiftTaskStatus.Pending);
        var iface = (ISwiftStatusHandler)handler;
        var status = iface.GetCurrentStatus();
        AssertEqual(SwiftTaskStatus.CaseTag.Pending, status.Tag, "Initial status should be Pending");
        TestLogger.Info($"((ISwiftStatusHandler)handler).GetCurrentStatus().Tag = {status.Tag}");
    }

    [TestTier(TestTier.Tier2)]
    public void TestStatusHandlerTransitionStatus()
    {
        var handler = new SimpleStatusHandler(initialStatus: SwiftTaskStatus.Pending);
        var iface = (ISwiftStatusHandler)handler;
        var next = iface.TransitionStatus(from: SwiftTaskStatus.Pending);
        AssertEqual(SwiftTaskStatus.CaseTag.Running, next.Tag, "Transition from Pending should be Running");
        TestLogger.Info($"TransitionStatus(Pending).Tag = {next.Tag}");
    }

    [TestTier(TestTier.Tier2)]
    public void TestStatusHandlerHandleStatus()
    {
        var handler = new SimpleStatusHandler(initialStatus: SwiftTaskStatus.Pending);
        var iface = (ISwiftStatusHandler)handler;
        iface.HandleStatus(SwiftTaskStatus.Running);
        var current = iface.GetCurrentStatus();
        AssertEqual(SwiftTaskStatus.CaseTag.Running, current.Tag, "After HandleStatus(Running)");
        TestLogger.Info($"After HandleStatus(Running), GetCurrentStatus().Tag = {current.Tag}");
    }

    // Tier 3: TaskPriority has String raw value — FromRawValue("high") routes through
    // SwiftBindings wrapper library, which isn't bundled in RuntimeTestsApp.
    // TaskStatus (Int32 raw value) works because FromRawValue(int) goes directly to native lib.
    [TestTier(TestTier.Tier3)]
    public void TestPriorityHandlerGetPriority()
    {
        var handler = new SimplePriorityHandler(initialPriority: TaskPriority.High);
        var iface = (ISwiftPriorityHandler)handler;
        var priority = iface.GetPriority();
        AssertEqual(TaskPriority.CaseTag.High, priority.Tag, "Initial priority should be High");
        TestLogger.Info($"((ISwiftPriorityHandler)handler).GetPriority().Tag = {priority.Tag}");
    }

    [TestTier(TestTier.Tier3)]
    public void TestPriorityHandlerSetPriority()
    {
        var handler = new SimplePriorityHandler(initialPriority: TaskPriority.Low);
        var iface = (ISwiftPriorityHandler)handler;
        iface.SetPriority(TaskPriority.Critical);
        var priority = iface.GetPriority();
        AssertEqual(TaskPriority.CaseTag.Critical, priority.Tag, "After SetPriority(Critical)");
        TestLogger.Info($"After SetPriority(Critical), GetPriority().Tag = {priority.Tag}");
    }

    [TestTier(TestTier.Tier3)]
    public void TestPriorityHandlerIsHigherPriority()
    {
        var handler = new SimplePriorityHandler(initialPriority: TaskPriority.High);
        var iface = (ISwiftPriorityHandler)handler;
        AssertTrue(iface.IsHigherPriority(than: TaskPriority.Low), "High > Low");
        AssertFalse(iface.IsHigherPriority(than: TaskPriority.Critical), "High < Critical");
        TestLogger.Info("IsHigherPriority: High > Low = true, High < Critical = false");
    }

    #endregion

    #region Enum Property Dispatch Through Interface (Tier 3 — String raw value enum needs wrapper lib)

    [TestTier(TestTier.Tier3)]
    public void TestPrioritizedPropertyGetThroughInterface()
    {
        var item = new PrioritizedItem(priority: TaskPriority.Medium);
        var iface = (ISwiftPrioritized)item;
        AssertEqual(TaskPriority.CaseTag.Medium, iface.Priority.Tag, "ISwiftPrioritized.Priority");
        TestLogger.Info($"((ISwiftPrioritized)PrioritizedItem).Priority.Tag = {iface.Priority.Tag}");
    }

    [TestTier(TestTier.Tier3)]
    public void TestMutablePrioritizedPropertySetThroughInterface()
    {
        var item = new MutablePrioritizedItem(priority: TaskPriority.Low);
        var iface = (ISwiftMutablePrioritized)item;
        AssertEqual(TaskPriority.CaseTag.Low, iface.Priority.Tag, "Initial priority Low");
        iface.Priority = TaskPriority.High;
        AssertEqual(TaskPriority.CaseTag.High, iface.Priority.Tag, "After set to High");
        TestLogger.Info($"ISwiftMutablePrioritized: Low -> High, Tag = {iface.Priority.Tag}");
    }

    [TestTier(TestTier.Tier2)]
    public void TestTaskStatusRawValue()
    {
        var status = SwiftTaskStatus.Running;
        AssertEqual(1, status.RawValue, "SwiftTaskStatus.Running.RawValue");
        TestLogger.Info($"SwiftTaskStatus.Running.RawValue = {status.RawValue}");
    }

    #endregion

    #region SwiftString Property Access Through Interface (Tier 3 — Mono JIT risk)

    [TestTier(TestTier.Tier3)]
    public void TestDescriptionPropertyThroughInterface()
    {
        var item = new SimpleItem(id: "p1", label: "Proto");
        var iface = (ISwiftDescribable)item;
        var desc = iface.Description.ToString();
        AssertTrue(desc.Contains("p1"), "Description contains id");
        TestLogger.Info($"ISwiftDescribable.Description = \"{desc}\"");
    }

    [TestTier(TestTier.Tier3)]
    public void TestIdPropertyThroughInterface()
    {
        var item = new SimpleItem(id: "id-42", label: "Test");
        var iface = (ISwiftTestIdentifiable)item;
        var id = iface.Id.ToString();
        AssertEqual("id-42", id, "ISwiftTestIdentifiable.Id");
        TestLogger.Info($"ISwiftTestIdentifiable.Id = \"{id}\"");
    }

    [TestTier(TestTier.Tier3)]
    public void TestNameablePropertyThroughInterface()
    {
        var person = new Person(name: "Charlie", age: 40);
        var iface = (ISwiftNameable)person;
        var name = iface.Name.ToString();
        AssertEqual("Charlie", name, "ISwiftNameable.Name");
        TestLogger.Info($"ISwiftNameable.Name = \"{name}\"");
    }

    [TestTier(TestTier.Tier3)]
    public void TestNamedPropertyGetThroughInterface()
    {
        var item = new NamedItem(name: "TestItem");
        var iface = (ISwiftNamed)item;
        var name = iface.Name.ToString();
        AssertEqual("TestItem", name, "ISwiftNamed.Name");
        TestLogger.Info($"ISwiftNamed.Name = \"{name}\"");
    }

    [TestTier(TestTier.Tier3)]
    public void TestMutableNamedPropertySetThroughInterface()
    {
        var item = new MutableNamedItem(name: "Original");
        var iface = (ISwiftMutableNamed)item;
        var origName = iface.Name.ToString();
        AssertEqual("Original", origName, "Initial name");
        iface.Name = new Swift.SwiftString("Updated");
        var newName = iface.Name.ToString();
        AssertEqual("Updated", newName, "After name set");
        TestLogger.Info($"ISwiftMutableNamed.Name: \"{origName}\" -> \"{newName}\"");
    }

    #endregion
}
