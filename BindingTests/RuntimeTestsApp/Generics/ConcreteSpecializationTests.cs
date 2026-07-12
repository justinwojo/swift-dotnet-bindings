// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Tests for concrete protocol specialization — methods with protocol-constrained
/// generic parameters are specialized into one concrete overload per known conformer.
/// </summary>
public class ConcreteSpecializationTests : TestBase
{
    public ConcreteSpecializationTests(TestResults results) : base(results) { }

    public void TestItemProcessor_ProcessItem_TextItem()
    {
        var processor = new ItemProcessor(prefix: "test");
        var item = new TextItem(text: "hello");
        var result = processor.ProcessItem(item);
        AssertEqual("test: text:HELLO", result, "ProcessItem<TextItem>");
    }

    public void TestItemProcessor_ProcessItem_NumberItem()
    {
        var processor = new ItemProcessor(prefix: "calc");
        var item = new NumberItem(value: 21);
        var result = processor.ProcessItem(item);
        AssertEqual("calc: number:42", result, "ProcessItem<NumberItem>");
    }

    public void TestItemProcessor_Describe_TextItem()
    {
        var item = new TextItem(text: "world");
        var result = ItemProcessor.Describe(item);
        AssertEqual("text:world", result, "Describe<TextItem>");
    }

    public void TestItemProcessor_Describe_NumberItem()
    {
        var item = new NumberItem(value: 7);
        var result = ItemProcessor.Describe(item);
        AssertEqual("number:7", result, "Describe<NumberItem>");
    }

    // MARK: - Generic Constructor Specialization (Fix 4)

    public void TestProcessedItem_FromTextItem()
    {
        var source = new TextItem(text: "hello");
        var processed = ProcessedItem.FromSwiftBindingsTestLibTextItem(source);
        AssertEqual("text:hello", processed.Title.ToString(), "ProcessedItem.FromTextItem");
    }

    public void TestProcessedItem_FromNumberItem()
    {
        var source = new NumberItem(value: 99);
        var processed = ProcessedItem.FromSwiftBindingsTestLibNumberItem(source);
        AssertEqual("number:99", processed.Title.ToString(), "ProcessedItem.FromNumberItem");
    }

    // MARK: - some Collection<String> Specialization (Fix 4)

    public void TestCollectionHost_JoinItems_ArrayOfStrings()
    {
        using var host = new CollectionHost(separator: ", ");
        using var items = new Swift.SwiftArray<Swift.SwiftString>();
        items.Append(new Swift.SwiftString("hello"));
        items.Append(new Swift.SwiftString("world"));
        var joined = host.JoinItems(items);
        AssertEqual("hello, world", joined, "CollectionHost.JoinItems<[String]>");
    }
}
