// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Layer A coverage for the constrained-existential projection
/// (gap-0.10.0-everyprotocol-and-existentials.md, Cases 1 + 2).
///
/// Verifies that a Swift function which accepts or returns
/// <c>any LabelledContainer&lt;String&gt;</c> projects as the strongly-typed
/// generic interface <c>ILabelledContainer&lt;SwiftString&gt;</c> rather than
/// degrading to <c>object</c> / <c>Swift.AnyType</c> with an
/// <c>[UnsupportedSwiftType("Existential type fallback", …)]</c> annotation.
///
/// Runtime invocation is intentionally NOT exercised here: that requires
/// proxy-dispatch for protocols with associated types, which is carved out
/// to Bundle 11b. The MakeStringLabel body still throws
/// <c>NotSupportedException("Protocol proxy not available")</c> by design.
/// </summary>
public class ConstrainedExistentialTests : TestBase
{
    public ConstrainedExistentialTests(TestResults results) : base(results) { }

    private static MethodInfo GetFunction(string name)
    {
        var method = typeof(Functions).GetMethod(
            name,
            BindingFlags.Public | BindingFlags.Static);
        if (method == null)
        {
            throw new AssertionException(
                $"SwiftBindingsTestLib.Functions.{name} was not emitted");
        }
        return method;
    }

    public void TestDescribeStringLabel_ParameterIsStronglyTypedExistential()
    {
        var method = GetFunction(nameof(Functions.DescribeStringLabel));
        var parameters = method.GetParameters();
        AssertEqual(1, parameters.Length, "DescribeStringLabel should take exactly one parameter");

        var paramType = parameters[0].ParameterType;
        var expected = typeof(ILabelledContainer<SwiftString>);
        AssertTrue(
            paramType == expected,
            $"DescribeStringLabel parameter projected as {paramType.FullName}, expected {expected.FullName}");
    }

    public void TestDescribeStringLabel_ParameterIsNotAnyTypeOrObject()
    {
        var method = GetFunction(nameof(Functions.DescribeStringLabel));
        var paramType = method.GetParameters()[0].ParameterType;
        AssertFalse(
            paramType == typeof(object),
            "DescribeStringLabel parameter must not degrade to object");
        AssertFalse(
            paramType == typeof(AnyType),
            "DescribeStringLabel parameter must not degrade to Swift.AnyType");
    }

    public void TestDescribeStringLabel_NoExistentialFallbackAnnotation()
    {
        var method = GetFunction(nameof(Functions.DescribeStringLabel));
        var attrs = method.GetCustomAttributes(typeof(UnsupportedSwiftTypeAttribute), inherit: false);
        AssertEqual(
            0,
            attrs.Length,
            "DescribeStringLabel must not carry [UnsupportedSwiftType] when projection is strongly typed");
    }

    public void TestMakeStringLabel_ReturnIsStronglyTypedExistential()
    {
        var method = GetFunction(nameof(Functions.MakeStringLabel));
        var expected = typeof(ILabelledContainer<SwiftString>);
        AssertTrue(
            method.ReturnType == expected,
            $"MakeStringLabel return projected as {method.ReturnType.FullName}, expected {expected.FullName}");
    }

    public void TestMakeStringLabel_ReturnIsNotAnyTypeOrObject()
    {
        var method = GetFunction(nameof(Functions.MakeStringLabel));
        AssertFalse(
            method.ReturnType == typeof(object),
            "MakeStringLabel return must not degrade to object");
        AssertFalse(
            method.ReturnType == typeof(AnyType),
            "MakeStringLabel return must not degrade to Swift.AnyType");
    }

    public void TestMakeStringLabel_NoExistentialFallbackAnnotation()
    {
        var method = GetFunction(nameof(Functions.MakeStringLabel));
        var attrs = method.GetCustomAttributes(typeof(UnsupportedSwiftTypeAttribute), inherit: false);
        AssertEqual(
            0,
            attrs.Length,
            "MakeStringLabel must not carry [UnsupportedSwiftType] when projection is strongly typed");
    }

    public void TestProjectedInterfaceIsGeneric()
    {
        var iface = typeof(ILabelledContainer<SwiftString>);
        AssertTrue(iface.IsInterface, "ILabelledContainer<TLabel> must be an interface");
        AssertTrue(iface.IsGenericType, "ILabelledContainer<SwiftString> must be a closed generic interface");
        var args = iface.GetGenericArguments();
        AssertEqual(1, args.Length, "ILabelledContainer must have exactly one type argument");
        AssertTrue(
            args[0] == typeof(SwiftString),
            $"ILabelledContainer type argument was {args[0].FullName}, expected Swift.SwiftString");
    }

    /// <summary>
    /// Runtime-level coverage for the typed-PAT conformance lookup. The Swift
    /// existential <c>any LabelledContainer&lt;String&gt;</c> projects as the
    /// closed generic interface <c>ILabelledContainer&lt;SwiftString&gt;</c>,
    /// but a single-PAT conformer's <c>_protocolConformanceSymbols</c> dict
    /// is keyed on <c>typeof(object)</c>. The generated
    /// <c>GetProtocolConformanceDescriptor&lt;TProtocol&gt;()</c> body MUST
    /// fall back to the object key when the typed key misses — otherwise
    /// every existential boxing through this path throws
    /// SwiftRuntimeException at runtime.
    ///
    /// This test calls <see cref="ProtocolWitnessTable.GetOrThrowAuto{TType,TProtocol}"/>
    /// against the concrete <c>StringLabel</c> conformer with the typed
    /// existential interface. Both Mono (reflection through
    /// <c>ProtocolConformanceDescriptorHelper</c>) and NativeAOT (static
    /// abstract dispatch through <c>TryGetDirect</c>) land in the same
    /// generated method body, so a single test covers both runtimes.
    /// </summary>
    public void TestStringLabel_TypedPATConformanceLookupSucceeds()
    {
        // Touch StringLabel's static initializer so _protocolConformanceSymbols
        // is populated before the dispatch — paranoia, since the constructor
        // would do this anyway.
        using var label = new StringLabel("hello");
        AssertEqual("hello", label.Label, "StringLabel.label round-trip");

        // The typed lookup path. If the fallback in
        // GetProtocolConformanceDescriptor<TProtocol>() is missing, this
        // throws SwiftRuntimeException("…no conformance was found").
        var witnessTable = ProtocolWitnessTable.GetOrThrowAuto<StringLabel, ILabelledContainer<SwiftString>>();

        // Witness table must wrap a non-zero native handle.
        AssertTrue(
            witnessTable.IsValid,
            "Witness table for StringLabel : ILabelledContainer<SwiftString> must be valid (non-zero handle)");

        TestLogger.Info("Typed-PAT lookup resolved StringLabel : ILabelledContainer<SwiftString> via typeof(object) fallback");
    }

    /// <summary>
    /// Round-trip assignability + Swift-side dispatch through the typed
    /// existential parameter (Codex r2 High closure for #5a). Without
    /// StringLabel implementing <c>ILabelledContainer&lt;SwiftString&gt;</c>
    /// this call would fail CS0029 at compile time; without the typed-PAT
    /// runtime fallback in <c>GetProtocolConformanceDescriptor</c> it would
    /// throw SwiftRuntimeException at the boxing site. Together they make
    /// the typed-existential surface actually usable from C#.
    /// </summary>
    public void TestDescribeStringLabel_RoundTripsThroughTypedExistential()
    {
        // `any P<X>` parameterized existentials require iOS 16 / macOS 13 /
        // tvOS 16 runtime support. The Swift fixture is gated with
        // @available, so the C# call site picks up CA1416 against our iOS 15
        // deployment target. Guard at runtime — older simulators just skip.
        if (!OperatingSystem.IsIOSVersionAtLeast(16))
        {
            TestLogger.Info("any LabelledContainer<String> requires iOS 16+; skipping on this OS.");
            return;
        }

        // Compile-time gate: the typed parameter must accept a StringLabel
        // directly. If the closed-PAT interface is missing from StringLabel's
        // implements list this assignment fails CS0029 and the test never
        // reaches runtime.
        using var label = new StringLabel("hello");
        ILabelledContainer<SwiftString> typed = label;
        AssertTrue(typed is not null, "StringLabel must be assignable to ILabelledContainer<SwiftString>");

        // End-to-end Swift round-trip. Functions.DescribeStringLabel boxes the
        // parameter through ExistentialContainerFactory.GetOrCreate, which
        // resolves the conformance via the typed-PAT runtime fallback.
        // The flow analyzer doesn't pick up the AssertTrue contract, so suppress
        // the nullable-reference warning explicitly — the assertion above already
        // guarantees non-null.
        var description = Functions.DescribeStringLabel(typed!);
        AssertEqual(
            "label=hello",
            description,
            "Swift-side describeLabel(_:) must dispatch via the conformer's witness table");

        TestLogger.Info($"Round-trip via typed existential -> \"{description}\"");
    }

    /// <summary>
    /// Dispatch-correctness check: the same StringLabel instance, called both
    /// directly and through the typed existential, must produce the same
    /// string. Catches the regression where the typed boxing path resolves
    /// the wrong witness (e.g., a multi-PAT key collision returning a sibling
    /// conformance's table) — Swift would still execute SOMETHING and return
    /// SOME string, but the string would differ from the direct call.
    /// </summary>
    public void TestStringLabel_TypedAndDirectDispatchAgree()
    {
        // Same iOS 16+ guard as TestDescribeStringLabel_RoundTripsThroughTypedExistential —
        // Functions.DescribeStringLabel is annotated @available(iOS 16+) at the
        // Swift level, which propagates as CA1416 in C#.
        if (!OperatingSystem.IsIOSVersionAtLeast(16))
        {
            TestLogger.Info("any LabelledContainer<String> requires iOS 16+; skipping on this OS.");
            return;
        }

        using var label = new StringLabel("dispatch-check");

        // Direct dispatch — no boxing, calls StringLabel.GetDescribeLabel directly.
        var direct = label.GetDescribeLabel();
        AssertEqual("label=dispatch-check", direct, "direct dispatch baseline");

        // Typed-existential dispatch — boxes through ExistentialContainerFactory,
        // resolves witness via typed-PAT fallback, dispatches via Swift PWT.
        ILabelledContainer<SwiftString> typed = label;
        var viaTypedExistential = Functions.DescribeStringLabel(typed);

        AssertEqual(
            direct,
            viaTypedExistential,
            "Same StringLabel instance must yield the same string via direct C# call and via typed-existential boxing");

        TestLogger.Info($"Direct={direct}, viaTypedExistential={viaTypedExistential}");
    }

    /// <summary>
    /// End-to-end ABI coverage for a VARIADIC constrained existential —
    /// <c>LabelledContainerBuilder.buildBlock(_ items: (any LabelledContainer&lt;String&gt;)...)</c>.
    /// This is the intersection of parameterized-protocol existentials and variadic packs, and the
    /// deepest case of the demangle-based variadic-detection path: <c>any LabelledContainer&lt;String&gt;</c>
    /// mangles through the <c>ConstrainedExistential</c> (<c>XP</c>) node, which the demangler must
    /// reduce for the per-overload "d" variadic marker to be read. swift-api-digester renders the
    /// parameter as a plain <c>[any LabelledContainer&lt;String&gt;]</c> (no "..."), so the marker is the
    /// only variadic signal. If detection fails, the generated wrapper emits a direct splat call that
    /// fails to compile — i.e. this test reaching runtime at all already proves the wrapper bridged
    /// the variadic-to-array ABI via <c>unsafeBitCast</c>; the count assertion proves the round-trip.
    ///
    /// It is also the runtime exerciser of the SUPPRESSED-PROXY one-arg owned-carrier overload
    /// (<c>ExistentialContainerFactory.CreateOwnedExistential1&lt;T&gt;(value)</c>, P1-08 opaque sibling):
    /// because the closed-constrained PAT proxy is suppressed, the co-gater strips the wrap-fallback and
    /// the generated <c>buildBlock</c> body emits the one-arg form
    /// <c>CreateOwnedExistential1&lt;ILabelledContainer&lt;SwiftString&gt;&gt;(e)</c>. The boxable
    /// <c>StringLabel</c> inputs drive the donate branch; a regression in co-gating fallback removal
    /// would emit the two-arg form referencing the suppressed proxy and fail to compile.
    /// </summary>
    public void TestLabelledContainerBuilder_VariadicExistentialSplat()
    {
        // Same iOS 16+ guard as the other LabelledContainer round-trip tests — the
        // @available(iOS 16+) struct propagates as CA1416 against our iOS 15 floor.
        if (!OperatingSystem.IsIOSVersionAtLeast(16))
        {
            TestLogger.Info("any LabelledContainer<String> requires iOS 16+; skipping on this OS.");
            return;
        }

        using var alpha = new StringLabel("alpha");
        using var beta = new StringLabel("beta");
        using var gamma = new StringLabel("gamma");

        var count = LabelledContainerBuilder.BuildBlock(
            new ILabelledContainer<SwiftString>[] { alpha, beta, gamma });

        AssertEqual(3, (int)count, "buildBlock((any LabelledContainer<String>)...) returns the element count");
    }

    /// <summary>
    /// The zero-children overload must remain callable alongside the variadic one — the variadic
    /// flag is per-overload, so the empty <c>buildBlock()</c> must NOT inherit the variadic bridge.
    /// Mirrors the plain-existential <c>ExistentialVariadicBuilder</c> empty-overload guard.
    /// </summary>
    public void TestLabelledContainerBuilder_EmptyOverload()
    {
        if (!OperatingSystem.IsIOSVersionAtLeast(16))
        {
            TestLogger.Info("LabelledContainerBuilder requires iOS 16+; skipping on this OS.");
            return;
        }

        var count = LabelledContainerBuilder.BuildBlock();

        AssertEqual(0, (int)count, "Zero-children buildBlock() returns 0");
    }
}
