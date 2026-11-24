// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
// A member-less struct used as a namespace (`LocalFacade`) is lifted into the C#
// namespace `SwiftBindingsTestLib.LocalFacade`, so a plain `using` directive
// imports its nested types; compile success of that directive is itself a
// regression check (CS0138 if it were a type).
//
// A caseless enum used as a namespace (`LocalFacadeEnum`) is always a static
// class, members or not: a newer SDK may give the enum a static member, and a
// lifted namespace would then become a type and change every nested type's CLR
// name between a package's copies. `using static` imports a static class's
// nested types, so it is the directive for the enum (a plain `using` of it
// fails CS0138).
using SwiftBindingsTestLib.LocalFacade;
using static SwiftBindingsTestLib.LocalFacadeEnum;

namespace RuntimeTestsApp.Types;

/// <summary>
/// Coverage for types used as namespaces. A top-level <c>public struct</c> with no inits, no
/// stored properties and no members, used purely to scope nested types, emits as a C# nested
/// namespace (<c>NamespaceFacadeDetector.IsNamespaceFacade</c>, <c>NamespaceFacadeEmitter</c>).
/// A caseless enum used the same way emits as a static class, so its nested types keep one CLR
/// name (<c>LocalFacadeEnum+InnerHolder</c>) in every SDK's binding.
/// </summary>
public class NamespaceFacadeTests : TestBase
{
    public NamespaceFacadeTests(TestResults results) : base(results) { }

    public void TestStructFacade_NestedStructResolvesViaUsingDirective()
    {
        // FacadeMessage is reached without any qualifier here, which only
        // works if the `using SwiftBindingsTestLib.LocalFacade;` at the
        // top of the file resolved (i.e. LocalFacade is a namespace).
        // Pre-fix the type would have to be written as
        // `LocalFacade.FacadeMessage` and the using directive would have
        // failed to compile.
        // Property is `messageValue` (not `payload`) to avoid colliding
        // with the runtime's reserved `_payload` / `Payload` SafeHandle
        // accessor names emitted on every struct wrapper.
        using var msg = new FacadeMessage(messageValue: 42);
        AssertEqual(42, msg.MessageValue,
            "FacadeMessage.MessageValue should round-trip the constructor argument");
    }

    public void TestStructFacade_FreeFunctionReturnTypeRoundTrips()
    {
        // The free function `makeFacadeMessage` returns
        // `LocalFacade.FacadeMessage`. Verifies the type reference at the
        // P/Invoke return position resolves to the same C# type via the
        // lifted-namespace path. Round-trip the value to confirm the
        // marshalling shape didn't change with the namespace lift.
        using var msg = Functions.MakeFacadeMessage(messageValue: 7);
        AssertEqual(7, msg.MessageValue,
            "MakeFacadeMessage should return a FacadeMessage with the supplied value");
    }

    public void TestStructFacade_NestedSimpleEnumResolvesAtNamespaceLevel()
    {
        // FacadeStatus is a nested simple-enum inside the facade. The
        // namespace lift must not break the simple-enum emission path —
        // the enum should still be a C# `enum` value type. Its raw values
        // are not in the interface and it is not @frozen, so its constant is
        // keyed by case name; the raw value comes from Swift. Reaches
        // FacadeStatus via the using directive (no qualifier).
        var status = FacadeStatus.Running;
        AssertEqual(NameKeyedCase.Of("running"), (int)status,
            "FacadeStatus.Running is keyed by its case name");
        AssertEqual(1, (int)status.ToRawValue(),
            "FacadeStatus.Running's Swift raw value is 1");
    }

    public void TestEnumFacade_NestedStructResolvesViaUsingDirective()
    {
        // LocalFacadeEnum is a caseless public enum used as a namespace: a
        // static class, reached here through the `using static` directive.
        using var holder = new InnerHolder(labelValue: 99);
        AssertEqual(99, holder.LabelValue,
            "InnerHolder.LabelValue should round-trip the constructor argument");
        AssertEqual("SwiftBindingsTestLib.LocalFacadeEnum+InnerHolder", typeof(InnerHolder).FullName,
            "InnerHolder is nested in the LocalFacadeEnum static class");
        AssertTrue(typeof(LocalFacadeEnum).IsAbstract && typeof(LocalFacadeEnum).IsSealed,
            "LocalFacadeEnum is a static class");
    }

    public void TestEnumFacade_FreeFunctionReturnTypeRoundTrips()
    {
        using var holder = Functions.MakeFacadeEnumHolder(labelValue: 13);
        AssertEqual(13, holder.LabelValue,
            "MakeFacadeEnumHolder should return an InnerHolder with the supplied value");
    }
}
