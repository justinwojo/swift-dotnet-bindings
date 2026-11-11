// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Generic types nested inside a generic type: <c>SourceNamedShelf&lt;Value&gt;.Tagged&lt;Element&gt;</c>
/// (a struct) and <c>SourceNamedShelf&lt;Value&gt;.Ledger&lt;Element&gt;</c> (a class). The nested
/// type's metadata accessor takes the outer's argument and then its own; requesting the metadata
/// with the nested type's own argument alone faulted the first time a value was read, copied or
/// destroyed. Each test builds a concrete instantiation and reads every member back.
/// </summary>
public class NestedGenericOfGenericParentTests : TestBase
{
    public NestedGenericOfGenericParentTests(TestResults results) : base(results) { }

    public void TestStructFromSwift_ReadsBothLevels()
    {
        using var tagged = Functions.MakeShelfTaggedIntString(7, "seven");
        AssertEqual(7L, (long)tagged.Tag, "Tagged.tag");
        using var element = tagged.Element;
        AssertEqual("seven", element.ToString(), "Tagged.element");
        AssertEqual("7:seven", tagged.Summary, "Tagged.summary");
        AssertEqual(7L, (long)tagged.GetCurrentTag(), "Tagged.currentTag()");
        using var current = tagged.GetCurrentElement();
        AssertEqual("seven", current.ToString(), "Tagged.currentElement()");
        AssertEqual("7:seven", Functions.DescribeShelfTaggedIntString(tagged), "describe(Tagged) round-trip");
    }

    public void TestStructFromSwift_SwappedArguments()
    {
        using var tagged = Functions.MakeShelfTaggedStringInt("eight", 8);
        using var tag = tagged.Tag;
        AssertEqual("eight", tag.ToString(), "Tagged.tag");
        AssertEqual(8L, (long)tagged.Element, "Tagged.element");
        AssertEqual("eight:8", tagged.Summary, "Tagged.summary");
    }

    public void TestStructConstructorAndSetter()
    {
        using var three = new SwiftString("three");
        using var tagged = new SourceNamedShelf<nint>.Tagged<SwiftString>(3, three);
        AssertEqual("3:three", tagged.Summary, "Tagged.init(tag:element:)");

        using var drei = new SwiftString("drei");
        tagged.Element = drei;
        using var element = tagged.Element;
        AssertEqual("drei", element.ToString(), "Tagged.element after set");
        AssertEqual("3:drei", Functions.DescribeShelfTaggedIntString(tagged), "describe(Tagged) after set");
    }

    public void TestClassFromSwift_ReadsBothLevels()
    {
        using var ledger = Functions.MakeShelfLedgerIntString(11, "eleven");
        AssertEqual(11L, (long)ledger.Tag, "Ledger.tag");
        using var element = ledger.Element;
        AssertEqual("eleven", element.ToString(), "Ledger.element");
        AssertEqual("11=eleven", ledger.Summary, "Ledger.summary");
        AssertEqual(11L, (long)ledger.GetCurrentTag(), "Ledger.currentTag()");
        using var current = ledger.GetCurrentElement();
        AssertEqual("eleven", current.ToString(), "Ledger.currentElement()");
    }

    public void TestClassConstructorAndSetter()
    {
        using var twelve = new SwiftString("twelve");
        using var ledger = new SourceNamedShelf<nint>.Ledger<SwiftString>(12, twelve);
        AssertEqual("12=twelve", ledger.Summary, "Ledger.init(tag:element:)");

        using var zwoelf = new SwiftString("zwoelf");
        ledger.Element = zwoelf;
        AssertEqual("12=zwoelf", Functions.DescribeShelfLedgerIntString(ledger), "describe(Ledger) after set");
    }
}
