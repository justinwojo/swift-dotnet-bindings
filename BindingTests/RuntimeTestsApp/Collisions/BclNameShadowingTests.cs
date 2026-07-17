// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
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
}
