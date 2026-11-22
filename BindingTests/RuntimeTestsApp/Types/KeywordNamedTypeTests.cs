// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
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
        AssertEqual(4, (int)TestLibFunctions.KeywordTypeRaw(Fixed.Readonly), "keyword case `readonly` keeps raw value 4");
        AssertEqual(5, (int)TestLibFunctions.KeywordTypeRaw(Fixed.Static), "keyword case `static` keeps raw value 5");
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

    private sealed class ReadonlyImpl : Ireadonly
    {
        public nint GetLevel() => 11;
    }
}
