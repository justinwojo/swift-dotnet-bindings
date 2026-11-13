// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Swift.Runtime;

/// <summary>
/// Refuses, on runtimes that mishandle them, the direct Swift-convention transitions a generated
/// binding keeps. A generated member whose closure reaches such a transition calls
/// <see cref="ThrowIfUnsupported"/> before any native work; on every other runtime the call costs a
/// constant test.
/// </summary>
/// <remarks>
/// <para>
/// Which shapes a runtime mishandles is measured, per runtime: Mono's managed-to-native wrappers may
/// keep their GC-transition state in the register an untyped <c>SwiftSelf</c> is loaded into; the
/// CoreCLR interpreter's arm64 stubs keep the thread pointer there and shift the ordinary arguments
/// that follow a <c>SwiftSelf</c> or <c>SwiftError*</c>, and on x86_64 it applies no Swift lowering at
/// all. NativeAOT and CoreCLR with its JIT handle every measured shape.
/// </para>
/// <para>
/// The engine is identified from the core library the process actually runs — Mono's and CoreCLR's
/// each declare a type the others lack — and never from application configuration, which can only
/// make the answer more restrictive (a CoreCLR host told it may not compile dynamic code is treated
/// as interpreting). A process whose engine cannot be identified positively refuses every shape.
/// </para>
/// </remarks>
public static class SwiftTransitionGuard
{
    /// <summary>Every shape this runtime version knows how to judge.</summary>
    internal const SwiftTransitionFacts KnownFacts =
        SwiftTransitionFacts.SelfRegister | SwiftTransitionFacts.SpecialBeforeArguments | SwiftTransitionFacts.LoweredSignature |
        SwiftTransitionFacts.FunctionPointerSelfRegister | SwiftTransitionFacts.FunctionPointerSpecialBeforeArguments |
        SwiftTransitionFacts.FunctionPointerLoweredSignature;

    /// <summary>The engine a process runs on, as far as the guard can tell.</summary>
    internal enum Engine
    {
        Unknown,
        Mono,
        CoreClr,
        NativeAot,
    }

    /// <summary>The shapes the executing runtime refuses. Unknown bits are refused too.</summary>
    public static SwiftTransitionFacts UnsupportedFacts => State.Unsupported;

    /// <summary>
    /// Whether the executing runtime executes direct transitions of every shape in
    /// <paramref name="facts"/>. This says only that the transition restriction does not prohibit
    /// them; it is not a general statement about the transition's correctness.
    /// </summary>
    public static bool IsSupported(SwiftTransitionFacts facts) => (facts & State.Unsupported) == 0;

    /// <summary>
    /// Throws <see cref="SwiftTransitionUnsupportedException"/> when the executing runtime refuses any
    /// shape in <paramref name="facts"/>. Called by generated bindings as the first statement of a
    /// guarded member.
    /// </summary>
    /// <param name="facts">The shapes of every direct transition the member reaches.</param>
    /// <param name="member">The member, as spelled in the generated binding.</param>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ThrowIfUnsupported(SwiftTransitionFacts facts, string member)
    {
        if (facts != SwiftTransitionFacts.None && (facts & State.Unsupported) != 0)
            Throw(facts, member);
    }

    /// <summary>
    /// <see cref="ThrowIfUnsupported"/> as an expression that returns true, for a guarded constructor
    /// whose initializer arguments would otherwise run before its body.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool EnsureSupported(SwiftTransitionFacts facts, string member)
    {
        ThrowIfUnsupported(facts, member);
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Throw(SwiftTransitionFacts facts, string member) =>
        throw new SwiftTransitionUnsupportedException(member, facts, facts & State.Unsupported, State.Description);

    /// <summary>
    /// The shapes <paramref name="engine"/> refuses. <paramref name="compilesDynamicCode"/> is false
    /// for a CoreCLR host without a JIT, which interprets the binding's transition stubs.
    /// </summary>
    internal static SwiftTransitionFacts UnsupportedFactsFor(Engine engine, bool compilesDynamicCode, Architecture architecture)
    {
        const SwiftTransitionFacts unknown = ~KnownFacts;
        return unknown | engine switch
        {
            Engine.NativeAot => SwiftTransitionFacts.None,
            Engine.Mono => SwiftTransitionFacts.SelfRegister | SwiftTransitionFacts.FunctionPointerSelfRegister,
            Engine.CoreClr when compilesDynamicCode => SwiftTransitionFacts.None,
            Engine.CoreClr when architecture == Architecture.Arm64 =>
                SwiftTransitionFacts.SelfRegister | SwiftTransitionFacts.SpecialBeforeArguments |
                SwiftTransitionFacts.FunctionPointerSpecialBeforeArguments,
            Engine.CoreClr when architecture == Architecture.X64 =>
                SwiftTransitionFacts.LoweredSignature | SwiftTransitionFacts.FunctionPointerLoweredSignature,
            _ => KnownFacts,
        };
    }

    /// <summary>
    /// The engine, from which runtime-specific types the core library declares, and whether the
    /// process may generate code at all: NativeAOT declares neither and never can.
    /// </summary>
    internal static Engine Identify(bool declaresMonoRuntime, bool declaresCoreClrStubs, bool dynamicCodeSupported) =>
        (declaresMonoRuntime, declaresCoreClrStubs) switch
        {
            (true, false) => Engine.Mono,
            (false, true) => Engine.CoreClr,
            (false, false) when !dynamicCodeSupported => Engine.NativeAot,
            _ => Engine.Unknown,
        };

    internal static string Describe(Engine engine, bool compilesDynamicCode, Architecture architecture) => engine switch
    {
        Engine.Mono => $"Mono ({architecture})",
        Engine.CoreClr => compilesDynamicCode ? $"CoreCLR ({architecture})" : $"the CoreCLR interpreter ({architecture})",
        Engine.NativeAot => $"NativeAOT ({architecture})",
        _ => $"an unidentified .NET runtime ({architecture})",
    };

    private static class State
    {
        internal static readonly SwiftTransitionFacts Unsupported;
        internal static readonly string Description;

#pragma warning disable CA1810 // The two fields come from one detection.
        static State()
#pragma warning restore CA1810
        {
            var engine = Engine.Unknown;
            var compiles = false;
            var architecture = RuntimeInformation.ProcessArchitecture;
            try
            {
                engine = Identify(
                    DeclaredByCoreLibrary(Type.GetType("Mono.RuntimeStructs, System.Private.CoreLib")),
                    DeclaredByCoreLibrary(Type.GetType("System.StubHelpers.StubHelpers, System.Private.CoreLib")),
                    RuntimeFeature.IsDynamicCodeSupported);
                compiles = RuntimeFeature.IsDynamicCodeCompiled;
            }
            catch (Exception)
            {
                // Identification that cannot finish identifies nothing: every shape is refused.
                engine = Engine.Unknown;
            }
            Unsupported = UnsupportedFactsFor(engine, compiles, architecture);
            Description = Describe(engine, compiles, architecture);
        }

        private static bool DeclaredByCoreLibrary(Type? type) => type is not null && type.Assembly == typeof(object).Assembly;
    }
}
