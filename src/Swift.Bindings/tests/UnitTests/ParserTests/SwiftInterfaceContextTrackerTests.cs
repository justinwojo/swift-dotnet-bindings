// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

public class SwiftInterfaceContextTrackerTests
{
    [Fact]
    public void QualifiedPath_NestedTypes()
    {
        var tracker = new SwiftInterfaceContextTracker();
        tracker.ProcessLine("public class Outer {", "public class Outer {");
        Assert.Equal("Outer", tracker.QualifiedTypePath);

        tracker.ProcessLine("  public class Inner {", "  public class Inner {");
        Assert.Equal("Outer.Inner", tracker.QualifiedTypePath);
    }

    [Fact]
    public void ExtensionScope_TracksIsInsideExtension()
    {
        var tracker = new SwiftInterfaceContextTracker();
        tracker.ProcessLine("extension Module.MyType {", "extension Module.MyType {");
        Assert.True(tracker.IsInsideExtension);
        Assert.Equal("MyType", tracker.QualifiedTypePath);
    }

    [Fact]
    public void PendingAnnotation_AccumulatesAndConsumes()
    {
        var tracker = new SwiftInterfaceContextTracker();
        tracker.ProcessLine("public class Outer {", "public class Outer {");
        var kind = tracker.ProcessLine("@available(iOS 16.0, *)", "@available(iOS 16.0, *)");
        Assert.Equal(SwiftInterfaceContextTracker.LineKind.AnnotationOnly, kind);
        Assert.Single(tracker.PendingAnnotationLines);
        Assert.Equal("@available(iOS 16.0, *)", tracker.PendingAnnotationLines[0]);

        tracker.ConsumePendingAnnotations();
        Assert.Empty(tracker.PendingAnnotationLines);
    }

    [Fact]
    public void ExtensionScopeAnnotations_InheritedByMembers()
    {
        var tracker = new SwiftInterfaceContextTracker();
        // @available before extension
        tracker.ProcessLine("@available(iOS 13, *)", "@available(iOS 13, *)");
        tracker.ProcessLine("extension Module.MyType {", "extension Module.MyType {");
        Assert.NotNull(tracker.ExtensionScopeAnnotations);
        Assert.Single(tracker.ExtensionScopeAnnotations!);
        Assert.Contains("@available(iOS 13, *)", tracker.ExtensionScopeAnnotations![0]);
    }

    [Fact]
    public void BuildMemberKey_ProducesQualifiedKey()
    {
        var tracker = new SwiftInterfaceContextTracker();
        tracker.ProcessLine("public class Outer {", "public class Outer {");
        tracker.ProcessLine("  public struct Inner {", "  public struct Inner {");
        Assert.Equal("Outer.Inner.foo(_:)", tracker.BuildMemberKey("foo(_:)"));
    }

    [Fact]
    public void BraceDepthTracking_PopsScopeCorrectly()
    {
        var tracker = new SwiftInterfaceContextTracker();
        tracker.ProcessLine("public class Outer {", "public class Outer {");
        Assert.Equal(1, tracker.TypeDepth);
        tracker.ProcessLine("  public func foo() -> Int", "  public func foo() -> Int");
        tracker.ProcessLine("}", "}");
        Assert.Equal(0, tracker.TypeDepth);
    }

    [Fact]
    public void TopLevel_EmptyQualifiedPath()
    {
        var tracker = new SwiftInterfaceContextTracker();
        Assert.Equal(string.Empty, tracker.QualifiedTypePath);
        Assert.Equal("foo(_:)", tracker.BuildMemberKey("foo(_:)"));
    }

    [Fact]
    public void ExtractMemberPrintedName_FuncVarInit()
    {
        Assert.Equal("foo(_:bar:)", SwiftInterfaceContextTracker.ExtractMemberPrintedName(
            "  public func foo(_ x: Int, bar y: Int) -> String"));
        Assert.Equal("myProp", SwiftInterfaceContextTracker.ExtractMemberPrintedName(
            "  public var myProp: Int { get }"));
        Assert.NotNull(SwiftInterfaceContextTracker.ExtractMemberPrintedName(
            "  public init(x: Int)"));
    }

    [Fact]
    public void MemberLine_DetectedWithinType()
    {
        var tracker = new SwiftInterfaceContextTracker();
        tracker.ProcessLine("public class Foo {", "public class Foo {");
        var kind = tracker.ProcessLine("  public func bar() -> Int", "  public func bar() -> Int");
        Assert.Equal(SwiftInterfaceContextTracker.LineKind.MemberLine, kind);
    }

    [Fact]
    public void ExtensionScopeAnnotations_ClearedWhenScopePops()
    {
        var tracker = new SwiftInterfaceContextTracker();
        tracker.ProcessLine("@available(iOS 13, *)", "@available(iOS 13, *)");
        tracker.ProcessLine("extension Module.MyType {", "extension Module.MyType {");
        Assert.NotNull(tracker.ExtensionScopeAnnotations);
        tracker.ProcessLine("}", "}");
        Assert.Null(tracker.ExtensionScopeAnnotations);
    }

    [Fact]
    public void NestedTypeExtension_PreservesFullNestedPath()
    {
        var tracker = new SwiftInterfaceContextTracker();
        tracker.ProcessLine("extension Module.Outer.Inner {", "extension Module.Outer.Inner {");
        Assert.Equal("Outer.Inner", tracker.QualifiedTypePath);
    }

    [Fact]
    public void CompletedMultiLine_SetOnContinuationCompletion()
    {
        var tracker = new SwiftInterfaceContextTracker();
        tracker.ProcessLine("public class Foo {", "public class Foo {");
        var kind1 = tracker.ProcessLine("  public func bar(_ x: Int,", "  public func bar(_ x: Int,");
        Assert.Equal(SwiftInterfaceContextTracker.LineKind.Continuation, kind1);
        Assert.Null(tracker.CompletedMultiLine);

        var kind2 = tracker.ProcessLine("    y: String) -> Bool", "    y: String) -> Bool");
        Assert.Equal(SwiftInterfaceContextTracker.LineKind.MemberLine, kind2);
        Assert.NotNull(tracker.CompletedMultiLine);
        Assert.Contains("func bar(_ x: Int,", tracker.CompletedMultiLine);
        Assert.Contains("y: String) -> Bool", tracker.CompletedMultiLine);
    }

    [Fact]
    public void CompletedMultiLine_ClearedOnNextNonContinuationLine()
    {
        var tracker = new SwiftInterfaceContextTracker();
        tracker.ProcessLine("public class Foo {", "public class Foo {");
        tracker.ProcessLine("  public func bar(_ x: Int,", "  public func bar(_ x: Int,");
        tracker.ProcessLine("    y: String) -> Bool", "    y: String) -> Bool");
        Assert.NotNull(tracker.CompletedMultiLine);

        // Next non-continuation line should clear it
        tracker.ProcessLine("  public var prop: Int { get }", "  public var prop: Int { get }");
        Assert.Null(tracker.CompletedMultiLine);
    }

    [Fact]
    public void ExtractMemberPrintedName_Subscript()
    {
        Assert.Equal("subscript(key:)", SwiftInterfaceContextTracker.ExtractMemberPrintedName(
            "  public subscript(key: String) -> Int { get }"));
        Assert.Equal("subscript(row:column:)", SwiftInterfaceContextTracker.ExtractMemberPrintedName(
            "  public subscript(row row: Int, column col: Int) -> Double { get set }"));
        Assert.Equal("subscript(_:)", SwiftInterfaceContextTracker.ExtractMemberPrintedName(
            "  public subscript(_ index: Swift.Int) -> Swift.String { get }"));
    }
}
