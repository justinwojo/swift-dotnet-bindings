// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collisions;

/// <summary>
/// Round-trips members of a module that declares public types whose projected names shadow BCL
/// names the emitted interop itself references (<c>Type</c>, and the interop attribute names).
///
/// The emitted P/Invoke boilerplate lands inside <c>namespace SwiftBindingsTestLib</c>, so an
/// unqualified BCL reference binds to the shadowing Swift type rather than the BCL one. The
/// sharpest case is <c>Type</c>: the calling-convention attribute's <c>new Type[] { ... }</c>
/// reads as an array of the Swift enum, which is both a hard error and a bail-out for the
/// LibraryImport source generator — so every P/Invoke in the module fails, not just the
/// colliding file. The compile gate is what proves that (the module would not build at all),
/// and these assertions add the runtime half: the calls still bind to the right symbols and
/// round-trip their values once the references are qualified.
/// </summary>
public class BclNameShadowingTests : TestBase
{
    public BclNameShadowingTests(TestResults results) : base(results) { }

    /// <summary>
    /// A method whose return type is a shadowing enum still marshals its value correctly.
    /// </summary>
    public void TestShadowedEnumReturn()
    {
        using var probe = new BclShadowProbe();

        var marshalling = probe.GetMarshalling();
        AssertEqual(StringMarshalling.Utf8, marshalling,
            $"Expected StringMarshalling.Utf8 from marshalling(), got {marshalling}");
    }

    /// <summary>
    /// A string-returning method on the same type: exercises the string-marshalling P/Invoke
    /// whose attribute carries the qualified StringMarshalling reference.
    /// </summary>
    public void TestShadowedModuleStringRoundTrip()
    {
        using var probe = new BclShadowProbe();

        var described = probe.Describe("payload");
        AssertEqual("shadowed:payload", described,
            $"Expected \"shadowed:payload\" from describe(_:), got \"{described}\"");
    }

    /// <summary>
    /// A struct parameter whose projected name shadows an interop attribute name still passes
    /// its field through by value.
    /// </summary>
    public void TestShadowedStructParameter()
    {
        using var probe = new BclShadowProbe();
        using var value = new LibraryImport(41);

        var slot = probe.SlotOf(value);
        AssertEqual(42, slot, $"Expected slotOf(LibraryImport(41)) == 42, got {slot}");
    }

    /// <summary>
    /// The module declares an error type named <c>Exception</c>. A throwing member still surfaces
    /// its error as a Swift exception, and its success path returns the value.
    /// </summary>
    public void TestModuleExceptionTypeThrowingMember()
    {
        using var probe = new BclShadowProbe();

        AssertEqual(14, probe.CheckedSlot(7), "a non-negative slot returns its double");
        AssertThrows<SwiftException>(() => probe.CheckedSlot(-1),
            "a negative slot throws the module's Exception error");
    }

    /// <summary>
    /// Generic types in the same module, whose emitted guards catch the BCL exception type,
    /// still construct and round-trip values.
    /// </summary>
    public void TestGenericTypesBesideModuleExceptionType()
    {
        using var stack = new ShadowStack<int>();
        stack.Push(1);
        stack.Push(2);
        AssertEqual(2, stack.Count, "both pushes reached Swift");

        using var probe = new BclShadowProbe();
        using var something = probe.Choose(5);
        AssertTrue(something.IsSomething, "the payload case reports itself");
        AssertTrue(something.TryGetSomething(out var payload) && payload == 5, "the payload survives the round trip");
        var nothing = ShadowChoice<int>.Nothing;
        AssertFalse(nothing.IsSomething, "the empty case reports itself");
        using var rejected = probe.Choose(-1);
        AssertFalse(rejected.IsSomething, "a returned empty case reports itself");
    }
}
