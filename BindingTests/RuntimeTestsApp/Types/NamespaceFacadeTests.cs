// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
// The namespace-facade emission lifts `LocalFacade.FacadeMessage` /
// `LocalFacade.FacadeStatus` into the C# namespace
// `SwiftBindingsTestLib.LocalFacade`. The using directives below succeed
// ONLY when `LocalFacade` is emitted as a real C# namespace — pre-fix
// (when it was a `partial class`) `using SwiftBindingsTestLib.LocalFacade;`
// produces CS0138 because a 'using namespace' directive can only be applied
// to namespaces, not types. Compile success of these usings is itself the
// regression gate.
//
// Negative consumer pattern (does NOT compile against the post-fix shape —
// kept as a comment because "must fail to compile" can't be expressed in a
// runtime test):
//
//     using static SwiftBindingsTestLib.LocalFacade;
//     using static SwiftBindingsTestLib.LocalFacadeEnum;
//
// `using static` only resolves type members; once the facade is a real
// namespace those lines fail CS7007 ("a 'using static' directive can only
// be applied to types"). Downstream consumers (CryptoKit, Nuke, BlinkID)
// historically wrote `using static Module.Facade;` against the old
// `partial class` shape and broke at the 0.11.0 cutover. The replacement
// is the plain `using SwiftBindingsTestLib.LocalFacade;` directives below
// — see S6 in `0.11.0-session-plan.md`.
using SwiftBindingsTestLib.LocalFacade;
using SwiftBindingsTestLib.LocalFacadeEnum;

namespace RuntimeTestsApp.Types;

/// <summary>
/// Regression coverage for
/// <c>bug-0.10.0-namespace-facade-as-static-class.md</c> (Bundle 04 #3).
/// Swift modules that use the canonical "uninhabited type as namespace"
/// idiom — a top-level <c>public struct</c>/<c>enum</c> with no inits,
/// no stored properties, and no instance/static members, used purely to
/// scope a family of nested types — now emit as a real C# nested
/// namespace under the parent module's namespace, instead of a
/// <c>partial class</c> (struct) or <c>static partial class</c>
/// (caseless enum) container.
///
/// Post-fix:
///   - <c>NamespaceFacadeDetector.IsNamespaceFacade</c> matches the
///     strict shape (zero properties, methods, inits, operators,
///     subscripts, conformances, generic parameters; at least one
///     nested type; for enum: zero cases).
///   - <c>NamespaceFacadeEmitter.Emit</c> writes a
///     <c>namespace {Name} { … }</c> block at the current indent
///     instead of the per-handler class declaration.
///   - <c>IHandler.HandleBaseDecl</c> intercepts the matched decl
///     before per-handler dispatch (StructDecl + EnumDecl branches)
///     and routes through the facade emitter.
///
/// Compile success of the <c>using SwiftBindingsTestLib.LocalFacade</c>
/// directive at the top of this file is the primary regression check —
/// pre-fix that line would fail with CS0138 because <c>LocalFacade</c>
/// was a type, not a namespace.
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
        // the enum should still be a C# `enum` value type with the
        // declared raw-value cases. Reaches FacadeStatus via the using
        // directive (no qualifier).
        var status = FacadeStatus.Running;
        AssertEqual(1, (int)status,
            "FacadeStatus.Running should map to its declared raw value (1)");
    }

    public void TestEnumFacade_NestedStructResolvesViaUsingDirective()
    {
        // LocalFacadeEnum is a caseless public enum used as a namespace.
        // Pre-fix this emitted as `public static partial class LocalFacadeEnum`,
        // which still allowed `using static` access. Post-fix it's a real
        // namespace, and the `using SwiftBindingsTestLib.LocalFacadeEnum;`
        // at the top of the file resolves to it. Reaching InnerHolder
        // without a qualifier proves the namespace path works.
        using var holder = new InnerHolder(labelValue: 99);
        AssertEqual(99, holder.LabelValue,
            "InnerHolder.LabelValue should round-trip the constructor argument");
    }

    public void TestEnumFacade_FreeFunctionReturnTypeRoundTrips()
    {
        using var holder = Functions.MakeFacadeEnumHolder(labelValue: 13);
        AssertEqual(13, holder.LabelValue,
            "MakeFacadeEnumHolder should return an InnerHolder with the supplied value");
    }
}
