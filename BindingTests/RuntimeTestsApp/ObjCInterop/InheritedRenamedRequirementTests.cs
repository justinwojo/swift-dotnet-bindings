// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.ObjCInterop;

/// <summary>
/// A subclass conforming to a protocol through a property it inherits from a base that emitted it
/// under a renamed C# name (NSObject's <c>ClassHandle</c>, or the class-name <c>Value</c> rule).
/// Before the fix the subclass had no implementation of the interface member and the whole module
/// failed to compile (CS0738 / CS0535); a requirement witnessed only by inheritance was also read
/// as an invisible Swift default and threw through the interface. Fixture:
/// ObjCInterop/InheritedRenamedRequirement.swift.
/// </summary>
public class InheritedRenamedRequirementTests : TestBase
{
    public InheritedRenamedRequirementTests(TestResults results) : base(results) { }

    public void TestNSObjectRenamedInheritedRequirementForwards()
    {
        using var child = new ClassHandleChild(7);
        AssertEqual(7, child.ClassHandleValue, "The inherited renamed property reads the Swift value");
        AssertEqual(7, ((IClassHandleCarrying)child).ClassHandle, "The interface member forwards to the inherited property");
        AssertEqual(7, TestLibFunctions.ReadClassHandleCarrying(child), "Swift reads the requirement from the subclass");
        using var own = new ClassHandleOwnCarrier(8);
        AssertEqual(8, ((IClassHandleCarrying)own).ClassHandle, "Control: a conformer declaring the property");
    }

    public void TestClassNameRenamedInheritedRequirementForwardsGetAndSet()
    {
        using var child = new RecordTagChild(3);
        var tagged = (IRecordTagged)child;
        AssertEqual(3, tagged.RecordTag, "The interface getter forwards to the inherited RecordTagValue");
        tagged.RecordTag = 10;
        AssertEqual(10, ((RecordTag)child).RecordTagValue, "The interface setter writes the inherited property");
        AssertEqual(99, child.RecordTagValue, "The subclass's own member of the same C# name is untouched");
        AssertEqual(10, TestLibFunctions.BumpRecordTagged(child), "Swift reads the value the interface wrote");
        AssertEqual(11, tagged.RecordTag, "Swift's write is visible through the interface");
    }

    public void TestInheritedRequirementForwardsPastAnIntermediateClassMember()
    {
        using var leaf = new RecordTagGrandchild(4);
        var tagged = (IRecordTagged)leaf;
        AssertEqual(4, tagged.RecordTag, "The interface getter reads the declaring ancestor's property");
        tagged.RecordTag = 20;
        AssertEqual(20, ((RecordTag)leaf).RecordTagValue, "The interface setter writes the declaring ancestor's property");
        AssertEqual(77, ((RecordTagMiddle)leaf).RecordTagValue, "The intermediate class's own member of the same C# name is untouched");
        AssertEqual(20, TestLibFunctions.BumpRecordTagged(leaf), "Swift reads the value the interface wrote");
        AssertEqual(21, tagged.RecordTag, "Swift's write is visible through the interface");
    }

    public void TestSubclassPropertyHidingARenamedAncestorPropertyStaysDistinct()
    {
        using var child = new RecordSizeChild(3);
        AssertEqual(5, child.RecordSizeValue, "The subclass's own property reads its own storage");
        AssertEqual(3, ((RecordSize)child).RecordSizeValue, "The ancestor's renamed property reads the ancestor's");
        AssertEqual((nint)5, child.RecordSizeValueNative, "The subclass's native-width companion reads its own storage");
        AssertEqual((nint)3, ((RecordSize)child).RecordSizeValueNative, "The ancestor's companion reads the ancestor's");
        child.RecordSizeValue = 6;
        AssertEqual(3, ((RecordSize)child).RecordSizeValue, "Writing the subclass's property leaves the ancestor's untouched");
    }

    public void TestSubclassCompanionWithNoAncestorCompanionIsItsOwn()
    {
        using var child = new RecordCountChild(1);
        AssertEqual(2, child.RecordCountValue, "The subclass's own property reads its own storage");
        AssertEqual((nint)2, child.RecordCountValueNative, "Its companion reads the same storage");
        AssertEqual(1, ((RecordCount)child).RecordCountValue, "The ancestor's renamed property reads the ancestor's");
    }

    public void TestRequirementWitnessedOnlyByInheritanceIsNotADefault()
    {
        using var child = new InheritedOnlyTagChild(5);
        AssertEqual(5, ((IInheritedOnlyTagged)child).InheritedOnlyTag,
            "The requirement reaches the inherited property instead of a throwing interface default");
        AssertEqual(5, TestLibFunctions.ReadInheritedOnlyTagged(child), "Swift reads the requirement from the subclass");
        AssertEqual(9, TestLibFunctions.ReadInheritedOnlyTagged(new InheritedOnlyTaggedImpl(9)),
            "A C# conformer implements the requirement and Swift reads it");
    }
}

internal sealed class InheritedOnlyTaggedImpl : IInheritedOnlyTagged
{
    public InheritedOnlyTaggedImpl(int tag) { InheritedOnlyTag = tag; }
    public int InheritedOnlyTag { get; }
}
