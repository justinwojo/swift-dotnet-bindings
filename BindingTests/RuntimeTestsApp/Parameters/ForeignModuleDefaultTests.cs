// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Parameters;

/// <summary>
/// A defaulted parameter whose default expression is not a C#-expressible literal is reached
/// through a trimmed overload whose Swift shim calls the declaration with that argument omitted,
/// so Swift evaluates the default itself. When the expression names a member of a module the
/// binding does not own — here a static factory call and a static property on a platform-vended
/// class — the callable the shim ends up bound to has to be one the platform module actually
/// exports. A binding that instead reached for a symbol nobody exports compiles cleanly and then
/// throws at the first call, which is why these tests call the short form and read the default's
/// effect out of the returned value rather than only checking that the overload exists.
///
/// Each test also calls the full form with an explicitly chosen queue, so a short form that
/// happened to return the expected text for an unrelated reason still separates from one that
/// really carried the caller's argument through.
/// </summary>
public class ForeignModuleDefaultTests : TestBase
{
    public ForeignModuleDefaultTests(TestResults results) : base(results) { }

    public void TestOnQueue_OmittedDefaultResolvesToSwiftsGlobalQueue()
    {
        using var box = new ForeignModuleDefaultBox();

        AssertEqual("a|true", box.OnQueue("a"),
            "Omitting the queue must let Swift evaluate `.global()` and pass its own global queue");

        using var global = Swift.DispatchQueue.Global();
        AssertEqual("b|true", box.OnQueue("b", global),
            "The full form must pass the caller's queue through unchanged");

        using var main = Swift.DispatchQueue.Main;
        AssertEqual("c|false", box.OnQueue("c", main),
            "A queue that is NOT the global queue must be visible as such on the Swift side");
    }

    public void TestOnMainQueue_OmittedDefaultResolvesToSwiftsMainQueue()
    {
        using var box = new ForeignModuleDefaultBox();

        AssertEqual("a|true", box.OnMainQueue("a"),
            "Omitting the queue must let Swift evaluate `.main` and pass its own main queue");

        using var main = Swift.DispatchQueue.Main;
        AssertEqual("b|true", box.OnMainQueue("b", main),
            "The full form must pass the caller's queue through unchanged");

        using var global = Swift.DispatchQueue.Global();
        AssertEqual("c|false", box.OnMainQueue("c", global),
            "A queue that is NOT the main queue must be visible as such on the Swift side");
    }

    public void TestDescribeDefaultQueue_FreeFunctionShimResolvesTheSameWay()
    {
        // The free-function form has no enclosing type, so its shim is a top-level Swift function
        // rather than an extension member — a different emission path to the same default.
        AssertEqual("a|true", TestLibFunctions.DescribeDefaultQueue("a"),
            "Omitting the queue on a free function must still let Swift evaluate `.global()`");

        using var main = Swift.DispatchQueue.Main;
        AssertEqual("b|false", TestLibFunctions.DescribeDefaultQueue("b", main),
            "The full form must pass the caller's queue through unchanged");
    }
}
