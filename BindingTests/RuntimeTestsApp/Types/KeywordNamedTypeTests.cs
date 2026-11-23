// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Types;

/// <summary>
/// Public Swift types named after C# keywords (<c>event</c>, <c>`class`</c>, <c>`default`</c>,
/// <c>fixed</c>, nested <c>object</c> and <c>`struct`</c>, the protocol <c>readonly</c>), and their
/// keyword-named members and cases. The generator once read the parser's keyword-escaped name
/// (<c>_event</c>) as Swift's underscore-prefixed implementation-detail convention and dropped
/// each of these types whole. Every type, member and case here is called through its binding.
/// </summary>
public class KeywordNamedTypeTests : TestBase
{
    public KeywordNamedTypeTests(TestResults results) : base(results) { }

    public void TestStructNamedEvent()
    {
        using var value = new Event(3);
        AssertEqual(3, (int)value.Count, "event.count round-trips");
        AssertEqual(6, (int)value.GetDoubled(), "event.doubled() calls through");
        AssertTrue(value.Checked, "keyword-named property `checked` reads true for a positive count");
        value.Count = 0;
        AssertFalse(value.Checked, "`checked` follows the mutated count");
    }

    public void TestClassNamedClass()
    {
        using var host = new Class(10);
        AssertEqual(10, (int)host.Value, "class.value round-trips");
        AssertEqual(15, (int)host._default(5), "keyword-named method `default` calls through");
        using var made = host.Make();
        AssertEqual(10, (int)made.Count, "class.make() returns an event");
        using var arg = new Event(4);
        AssertEqual(14, (int)host.Take(arg), "class.take(_:) passes an event");
    }

    public void TestEnumNamedDefaultWithKeywordCases()
    {
        AssertEqual(1, (int)Default.By.GetCode(), "case by");
        AssertEqual(2, (int)Default.Switch.GetCode(), "keyword case `switch`");
        AssertEqual(3, (int)Default.Event.GetCode(), "keyword case `event`");
        AssertEqual(2, (int)TestLibFunctions.KeywordTypeCode(Default.Switch), "enum passed to a free function");
    }

    public void TestRawValueEnumNamedFixed()
    {
        AssertEqual(0, (int)TestLibFunctions.KeywordTypeRaw(Fixed.Readonly), "keyword case `readonly` reaches Swift as raw value 0");
        AssertEqual(1, (int)TestLibFunctions.KeywordTypeRaw(Fixed.Static), "keyword case `static` reaches Swift as raw value 1");
    }

    public void TestNestedKeywordNamedTypes()
    {
        using var host = new KeywordTypeHost();
        using var tagged = host.Wrap(9);
        AssertEqual(9, (int)tagged.Tag, "nested object.tag");
        AssertEqual(9, (int)TestLibFunctions.KeywordTypeHostedTag(tagged), "nested object passed to a free function");
        using var shape = host.Shape(6);
        AssertEqual(6, (int)shape.Width, "nested `struct`.width");
        AssertEqual(36, (int)host.Area(shape), "nested `struct` passed back to Swift");
        using var direct = new KeywordTypeHost.Struct(2);
        AssertEqual(4, (int)host.Area(direct), "nested `struct` constructed from C#");
    }

    public void TestFreeFunctionReturnsEvent()
    {
        using var value = TestLibFunctions.KeywordTypeMake(8);
        AssertEqual(8, (int)value.Count, "free function returns an event");
    }

    public void TestProtocolNamedReadonly()
    {
        using var conformer = new KeywordTypeConformer();
        AssertEqual(7, (int)TestLibFunctions.KeywordTypeLevel(conformer), "Swift conformer dispatches through any readonly");
        AssertEqual(11, (int)TestLibFunctions.KeywordTypeLevel(new ReadonlyImpl()), "C# implementation dispatches through any readonly");
    }

    public void TestPropertyNamedLikeItsKeywordType()
    {
        using var value = new Checked(4);
        AssertEqual(4, (int)value.CheckedValue, "`checked.checked` binds as CheckedValue");
        value.CheckedValue = 9;
        AssertEqual(9, (int)value.CheckedValue, "CheckedValue writes through to Swift");
    }

    public void TestPropertyNamedLikeANestedKeywordType()
    {
        using var host = new KeywordSiblingHost(2);
        AssertEqual(2, (int)host.GotoValue, "property `goto` beside nested type `goto` binds as GotoValue");
        using var jumped = host.Jump(5);
        AssertEqual(7, (int)jumped.Hops, "the nested `goto` type keeps its name");
        using var direct = new KeywordSiblingHost.Goto(3);
        AssertEqual(3, (int)direct.Hops, "nested `goto` constructed from C#");
    }

    public void TestPayloadEnumCasesNamedLikeTheEnum()
    {
        using var value = Implicit.ImplicitValue(6);
        AssertEqual(6, (int)value.Code, "payload case `implicit` binds as ImplicitValue");
        AssertTrue(value.TryGetImplicit(out var payload), "TryGetImplicit matches its own case");
        AssertEqual(6, (int)payload, "TryGetImplicit reads the payload");
        AssertEqual(0, (int)Implicit.None.Code, "case none");

        AssertEqual(0, (int)Stackalloc.StackallocValue.Size, "payload-free case `stackalloc` binds as StackallocValue");
        using var sized = Stackalloc.Sized(8);
        AssertEqual(8, (int)sized.Size, "payload case sized");
    }

    public void TestSimpleKeywordEnumWithSelfTypedMethods()
    {
        AssertEqual(1, (int)Unchecked.Unchecked.GetCode(), "case `unchecked` keeps the enum's name");
        AssertEqual(Unchecked.Other, Unchecked.Unchecked.Flipped(), "a method returning the enum");
        AssertTrue(Unchecked.Other.Same(Unchecked.Other), "a method taking the enum");
        AssertFalse(Unchecked.Other.Same(Unchecked.Unchecked), "a method taking the enum compares cases");
    }

    public void TestCaselessKeywordEnums()
    {
        AssertEqual(42, (int)Sealed.GetValue(), "caseless enum `sealed` with a static member");
        AssertTrue(typeof(Extern).IsEnum, "caseless enum `extern` projects as an empty C# enum");
    }

    public void TestMethodNamedLikeItsKeywordType()
    {
        using var value = new Explicit();
        AssertEqual(12, (int)value._explicit(4), "method `explicit` keeps its keyword spelling");
    }

    public void TestGenericKeywordNamedType()
    {
        using var value = TestLibFunctions.KeywordGenericMake(5);
        AssertEqual(5, (int)value.Count, "generic `foreach<Int32>` returned from Swift");
        AssertEqual(5, (int)TestLibFunctions.KeywordGenericCount(value), "generic `foreach<Int32>` passed back");
    }

    public void TestLowercaseTypeMembersNamedLikeTheType()
    {
        using var value = new Vec(3);
        AssertEqual(3, (int)value.VecValue, "property `vec` on type `vec` binds as VecValue");
        AssertEqual(12, (int)value.GetVec(4), "method `vec(scaled:)` on type `vec` binds as GetVec");
    }

    public void TestKeywordTypeNestedInASpecializedGeneric()
    {
        using var made = TestLibFunctions.KeywordNestMake(5);
        AssertEqual(5, (int)made.V, "KeywordNest<Int32>.class returned from Swift");
        AssertEqual(5, (int)TestLibFunctions.KeywordNestRead(made), "KeywordNest<Int32>.class passed back");
        AssertEqual(5, (int)TestLibFunctions.KeywordNestOptional(made), "passed as an Optional");
        AssertEqual(-1, (int)TestLibFunctions.KeywordNestOptional(null), "nil Optional");

        using var maybe = TestLibFunctions.KeywordNestMaybe(2);
        AssertNotNull(maybe, "an Optional return holding a value");
        AssertEqual(2, (int)maybe!.V, "the Optional return's value");
        AssertNull(TestLibFunctions.KeywordNestMaybe(-1), "a nil Optional return");

        using var items = new SwiftArray<KeywordNest<int>.Class>();
        items.Append(new KeywordNest<int>.Class(1));
        items.Append(new KeywordNest<int>.Class(4));
        AssertEqual(5, (int)TestLibFunctions.KeywordNestCount(items), "passed in an Array");

        using var holder = new KeywordNestHolder(made);
        using var item = holder.Item;
        AssertEqual(5, (int)item.V, "read from a stored property");
    }

    private sealed class ReadonlyImpl : Ireadonly
    {
        public nint GetLevel() => 11;
    }
}
