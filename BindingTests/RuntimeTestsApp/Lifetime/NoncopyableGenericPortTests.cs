// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Lifetime;

/// <summary>
/// Members of a <c>~Copyable</c> generic struct, both on the type itself and in same-type
/// constrained extensions (<c>where Right == …</c>). A wrapper must borrow the value for a read,
/// mutate it in place, and take it for a <c>consuming</c> member; a mutating member on a copyable
/// sibling must write back.
/// </summary>
public class NoncopyableGenericPortTests : TestBase
{
    public NoncopyableGenericPortTests(TestResults results) : base(results) { }

    public void TestGenericInitializerAndUnconstrainedMembersUseTheValueInPlace()
    {
        var port = new NcPort<NcReceiveRight>(7);

        AssertEqual(7u, port.Name, "the generic initializer stored the name");
        AssertEqual(7u, port.Peek(), "a borrowing read leaves the value in place");
        AssertEqual(8u, port.Advance(), "the mutating member returns the new name");
        AssertEqual(8u, port.Name, "the mutation reached the caller's value");

        port.Tag = 40;
        AssertEqual(20u, port.Name, "the setter wrote through to the stored value");
        AssertEqual(40u, port.Tag, "the getter reads the value the setter stored");

        AssertEqual(1020u, port.Close(), "the consuming member returns from the moved value");
        AssertThrows<ObjectDisposedException>(() => port.Peek(),
            "a read after the consuming member must throw ObjectDisposedException");
        AssertThrows<ObjectDisposedException>(() => port.Close(),
            "a second consume must throw ObjectDisposedException");
    }

    public void TestUnconstrainedConsumeOnAnotherRight()
    {
        var port = NcPortSwiftBindingsTestLib_NcSendRightCsmExtensions.FromSwiftBindingsTestLibNcSendRight(3);

        AssertEqual(3u, port.Peek(), "the borrowing read sees the stored name");
        AssertEqual(1003u, port.Close(), "the consuming member returns from the moved value");
        AssertThrows<ObjectDisposedException>(() => _ = port.Name,
            "a property read after the consuming member must throw ObjectDisposedException");
    }

    public void TestBorrowingReadThenConsumeOnReceiveSpecialization()
    {
        var port = NcPortSwiftBindingsTestLib_NcReceiveRightCsmExtensions.FromSwiftBindingsTestLibNcReceiveRight(5);

        AssertEqual(105u, port.GetSendName(), "the borrowing read sees the stored name");
        AssertEqual(105u, port.GetSendName(), "a borrow leaves the value usable");
        AssertEqual(5u, port.Relinquish(), "the consuming member returns the stored name");

        AssertThrows<ObjectDisposedException>(() => port.GetSendName(),
            "a read after the consuming member must throw ObjectDisposedException");
        AssertThrows<ObjectDisposedException>(() => port.Relinquish(),
            "a second consume must throw ObjectDisposedException");
    }

    public void TestConsumeDispatchesToTheSendSpecialization()
    {
        var port = NcPortSwiftBindingsTestLib_NcSendRightCsmExtensions.FromSwiftBindingsTestLibNcSendRight(5);

        AssertEqual(6u, port.Relinquish(), "the send-right extension's own body runs");
        AssertThrows<ObjectDisposedException>(() => port.Relinquish(),
            "a second consume must throw ObjectDisposedException");
    }

    public void TestMutatingConstrainedMemberWritesBack()
    {
        using var counter = new NcCounter<NcReceiveRight>(1);

        AssertEqual((nint)2, counter.Bump(), "the mutating member returns the new value");
        AssertEqual((nint)3, counter.Bump(), "the second call sees the first call's write");
        AssertEqual(3, counter.Value, "the stored value reflects both writes");
    }

    public void TestOpenGenericMembersDispatchThroughTheParentMetadata()
    {
        var slot = new NcSlot<int>(4);

        AssertEqual(4, slot.Count, "the generic initializer stored the count");
        AssertEqual((nint)4, slot.GetPeek(), "a borrowing read leaves the value in place");
        AssertEqual((nint)5, slot.Bump(), "the mutating member returns the new count");
        AssertEqual(5, slot.Count, "the mutation reached the caller's value");
        AssertEqual(42, slot.Echo(42), "a member taking the generic parameter round-trips it");

        AssertEqual((nint)5, slot.GetDrain(), "the consuming member returns from the moved value");
        AssertThrows<ObjectDisposedException>(() => slot.GetPeek(),
            "a read after the consuming member must throw ObjectDisposedException");
    }
}
