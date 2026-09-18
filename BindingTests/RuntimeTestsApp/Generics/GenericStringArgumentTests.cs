// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Unconstrained generics instantiated with a C# <c>string</c>, which projects onto Swift.String.
/// The value crosses as a Swift String in both directions: through a generic method parameter and
/// return, a stored generic property and its setter, and a generic class's methods.
/// </summary>
public class GenericStringArgumentTests : TestBase
{
    public GenericStringArgumentTests(TestResults results) : base(results) { }

    public void TestGenericMethodEchoesString()
    {
        using var host = new StringGenericHost();
        AssertEqual("hello", host.Echo("hello"), "a small string round-trips");
        AssertEqual("", host.Echo(""), "the empty string round-trips");
        var large = new string('x', 200) + "é🙂";
        AssertEqual(large, host.Echo(large), "a heap-backed non-ASCII string round-trips");
    }

    public void TestGenericParameterReachesSwiftAsString()
    {
        using var host = new StringGenericHost();
        AssertEqual("<naïve>", host.Describe("naïve"), "Swift interpolates the value as a String");
        AssertEqual("<7>", host.Describe(7), "an Int32 argument still takes the primitive path");
    }

    public void TestGenericClassStoresString()
    {
        using var cell = new StringGenericCell<string>("first");
        AssertEqual("first", cell.Value, "the stored String reads back");
        AssertEqual("cell:first", cell.GetDescribe(), "Swift sees the stored String");

        cell.Value = "second";
        AssertEqual("second", cell.Value, "the setter stores a new String");

        AssertEqual("second", cell.Replace("third"), "replace returns the previous String");
        AssertEqual("third", cell.Value, "replace stores the new String");
        AssertEqual("cell:third", cell.GetDescribe(), "Swift sees the replaced String");
    }

    public void TestRepeatedStringRoundTripsKeepValues()
    {
        using var host = new StringGenericHost();
        using var cell = new StringGenericCell<string>("start");
        for (int i = 0; i < 200; i++)
        {
            var text = "value-" + i + new string('y', i);
            AssertEqual(text, host.Echo(text), "echo");
            cell.Value = text;
            AssertEqual(text, cell.Value, "stored");
        }
    }
}
