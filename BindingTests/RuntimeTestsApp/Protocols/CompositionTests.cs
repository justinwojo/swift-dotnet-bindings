// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Runtime tests for protocol composition patterns (Composition.swift).
/// Exercises ExistentialContainer2+ (multi-witness-table) existentials
/// through free functions accepting `any P & Q` parameters.
///
/// These patterns had zero runtime coverage prior to this session.
/// </summary>
public class CompositionTests : TestBase
{
    public CompositionTests(TestResults results) : base(results) { }

    #region Person Construction (Nameable & Ageable conformer)

    public void TestPersonConstruction()
    {
        var person = new Person(name: "Alice", age: 30);
        AssertNotNull(person, "Person constructed");
        TestLogger.Info("Person construction passed");
    }

    public void TestPersonNameProperty()
    {
        var person = new Person(name: "Bob", age: 25);
        var name = person.Name;
        AssertEqual("Bob", name, "Person.Name");
        TestLogger.Info($"Person.Name = \"{name}\"");
    }

    public void TestPersonAgeProperty()
    {
        var person = new Person(name: "Charlie", age: 42);
        var age = person.Age;
        AssertEqual(42, age, "Person.Age");
        TestLogger.Info($"Person.Age = {age}");
    }

    #endregion

    #region describePersonAsComposition — EC2 Existential Round-Trip

    public void TestDescribePersonAsComposition()
    {
        var result = TestLibFunctions.DescribePersonAsComposition("Diana", 35);
        AssertEqual("Diana is 35", result, "describePersonAsComposition round-trip");
        TestLogger.Info($"DescribePersonAsComposition = \"{result}\"");
    }

    public void TestDescribePersonAsCompositionZeroAge()
    {
        var result = TestLibFunctions.DescribePersonAsComposition("Eve", 0);
        AssertEqual("Eve is 0", result, "describePersonAsComposition with zero age");
        TestLogger.Info($"DescribePersonAsComposition(zero) = \"{result}\"");
    }

    #endregion

    #region MultiConformingValue — 4-Protocol Conformer

    public void TestMultiConformingValueAdd()
    {
        var val = new MultiConformingValue(value: 10);
        var result = val.Add(5);
        AssertEqual(15, result, "MultiConformingValue.Add(5)");
        TestLogger.Info($"MultiConformingValue(10).Add(5) = {result}");
    }

    public void TestMultiConformingValueSubtract()
    {
        var val = new MultiConformingValue(value: 10);
        var result = val.Subtract(3);
        AssertEqual(7, result, "MultiConformingValue.Subtract(3)");
        TestLogger.Info($"MultiConformingValue(10).Subtract(3) = {result}");
    }

    public void TestMultiConformingValueMultiply()
    {
        var val = new MultiConformingValue(value: 10);
        var result = val.Multiply(4);
        AssertEqual(40, result, "MultiConformingValue.Multiply(4)");
        TestLogger.Info($"MultiConformingValue(10).Multiply(4) = {result}");
    }

    public void TestMultiConformingValueDivide()
    {
        var val = new MultiConformingValue(value: 10);
        var result = val.Divide(2);
        AssertEqual(5, result, "MultiConformingValue.Divide(2)");
        TestLogger.Info($"MultiConformingValue(10).Divide(2) = {result}");
    }

    public void TestMultiConformingValueDivideByZero()
    {
        var val = new MultiConformingValue(value: 10);
        var result = val.Divide(0);
        AssertEqual(0, result, "MultiConformingValue.Divide(0) returns 0");
        TestLogger.Info($"MultiConformingValue(10).Divide(0) = {result}");
    }

    #endregion
}
