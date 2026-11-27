// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// <c>ClassConstrainedSlot&lt;T&gt;</c> takes any <c>T</c>, but <c>init(object:tag:)</c> adds
/// <c>where T: AnyObject</c>. C# cannot constrain a constructor, and a
/// <c>class</c> constraint would not match Swift's rule anyway (a Swift struct can be projected
/// as a C# class), so the binding checks the type argument's Swift metadata before the call: a
/// class goes through, and a value type is refused with an <see cref="ArgumentException"/> instead
/// of reaching a Swift cast that stops the process. The free functions bound by <c>AnyObject</c>
/// alone check that the object crosses as the reference Swift expects. Fixture:
/// Generics/ClassConstrainedInit.swift.
/// </summary>
public class ClassConstrainedInitTests : TestBase
{
    public ClassConstrainedInitTests(TestResults results) : base(results) { }

    public void TestAClassTypeArgumentBuildsTheValue()
    {
        using var token = new ClassConstrainedToken(7);
        using var other = new ClassConstrainedToken(8);
        using var slot = new ClassConstrainedSlot<ClassConstrainedToken>(token, 5);
        AssertEqual(5, (int)slot.Tag, "the class-only initializer stored the tag");
        using var stored = slot.Value;
        AssertTrue(TestLibFunctions.ClassConstrainedSame(stored, token), "Swift stored the very object C# passed");
        AssertTrue(!TestLibFunctions.ClassConstrainedSame(stored, other), "and only that object");
    }

    public void TestAValueTypeArgumentIsRefusedBeforeSwift()
    {
        ArgumentException? refusal = null;
        try
        {
            using var slot = new ClassConstrainedSlot<int>(3, 5);
        }
        catch (ArgumentException e)
        {
            refusal = e;
        }
        AssertTrue(refusal is not null, "Int is not a class to Swift");
        AssertEqual("T", refusal?.ParamName, "the refusal names the type parameter");

        AssertThrows<ArgumentException>(() =>
        {
            using var slot = new ClassConstrainedSlot<string>("text", 1);
        }, "String is a struct to Swift");
    }

    public void TestAnAnyObjectBoundGenericCrossesAsTheReference()
    {
        using var token = new ClassConstrainedToken(11);
        using var other = new ClassConstrainedToken(12);
        AssertEqual(11, (int)TestLibFunctions.ClassConstrainedTokenId(token), "Swift reads the object it was passed");
        AssertTrue(TestLibFunctions.ClassConstrainedSame(token, token), "the same object arrives as the same reference twice");
        AssertTrue(!TestLibFunctions.ClassConstrainedSame(token, other), "two objects stay distinct");

        using var echoed = TestLibFunctions.ClassConstrainedEcho(token);
        AssertEqual(11, (int)echoed.Id, "the returned reference is the object passed in");
        AssertTrue(TestLibFunctions.ClassConstrainedSame(echoed, token), "and Swift sees it as that same object");

        AssertThrows<ArgumentException>(() => TestLibFunctions.ClassConstrainedSame(1, 1), "a value type is refused before Swift");
    }

    public void TestTheUnconstrainedMembersStillTakeAValueType()
    {
        using var slot = new ClassConstrainedSlot<int>(42);
        AssertEqual(42, slot.Value, "the unconstrained initializer is unaffected");
    }
}
