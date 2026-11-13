// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// The transition guard on the runtime the app actually runs on. A generated member whose only
/// route into Swift is a direct Swift-convention transition of a shape the executing runtime
/// mishandles must refuse before any native work, and the same member must work on a runtime
/// that handles the shape. Host unit tests can only exercise the policy table; whether the guard
/// identifies the engine of a real Mono, NativeAOT, CoreCLR or CoreCLR-interpreter app is only
/// observable here.
///
/// <para>The expectation is computed from the app's own view of its engine, never from the guard:
/// <see cref="SwiftRuntimeInfo.IsMonoRuntime"/> (configured by the runtime's build targets), and
/// <see cref="RuntimeFeature"/> to tell NativeAOT (no dynamic code), CoreCLR with its JIT (dynamic
/// code compiled) and the CoreCLR interpreter (dynamic code supported but interpreted) apart.</para>
/// </summary>
public class TransitionGuardTests : TestBase
{
    public TransitionGuardTests(TestResults results) : base(results) { }

    private enum ExpectedEngine { Mono, NativeAot, CoreClrJit, CoreClrInterpreter }

    private static ExpectedEngine Engine =>
        SwiftRuntimeInfo.IsMonoRuntime ? ExpectedEngine.Mono
        : !RuntimeFeature.IsDynamicCodeSupported ? ExpectedEngine.NativeAot
        : RuntimeFeature.IsDynamicCodeCompiled ? ExpectedEngine.CoreClrJit
        : ExpectedEngine.CoreClrInterpreter;

    /// <summary>The shapes each engine mishandles: the self register on Mono (it parks its own state
    /// in the callee-saved register Swift loads <c>self</c> into), the self register and a special
    /// argument ahead of ordinary ones on the arm64 interpreter, and any lowered signature on the
    /// x86_64 interpreter. NativeAOT and CoreCLR with its JIT handle all of them.</summary>
    private static SwiftTransitionFacts ExpectedRefusals(ExpectedEngine engine) => engine switch
    {
        ExpectedEngine.Mono => SwiftTransitionFacts.SelfRegister | SwiftTransitionFacts.FunctionPointerSelfRegister,
        ExpectedEngine.CoreClrInterpreter when RuntimeInformation.ProcessArchitecture == Architecture.Arm64 =>
            SwiftTransitionFacts.SelfRegister | SwiftTransitionFacts.SpecialBeforeArguments |
            SwiftTransitionFacts.FunctionPointerSpecialBeforeArguments,
        ExpectedEngine.CoreClrInterpreter when RuntimeInformation.ProcessArchitecture == Architecture.X64 =>
            SwiftTransitionFacts.LoweredSignature | SwiftTransitionFacts.FunctionPointerLoweredSignature,
        ExpectedEngine.CoreClrInterpreter =>
            SwiftTransitionFacts.SelfRegister | SwiftTransitionFacts.SpecialBeforeArguments | SwiftTransitionFacts.LoweredSignature |
            SwiftTransitionFacts.FunctionPointerSelfRegister | SwiftTransitionFacts.FunctionPointerSpecialBeforeArguments |
            SwiftTransitionFacts.FunctionPointerLoweredSignature,
        _ => SwiftTransitionFacts.None,
    };

    private static readonly SwiftTransitionFacts[] KnownShapes =
    [
        SwiftTransitionFacts.SelfRegister, SwiftTransitionFacts.SpecialBeforeArguments, SwiftTransitionFacts.LoweredSignature,
        SwiftTransitionFacts.FunctionPointerSelfRegister, SwiftTransitionFacts.FunctionPointerSpecialBeforeArguments,
        SwiftTransitionFacts.FunctionPointerLoweredSignature,
    ];

    /// <summary>
    /// The guard refuses exactly the shapes the executing engine mishandles, shape by shape, and a
    /// shape this runtime version does not know is refused everywhere.
    /// </summary>
    public void TestGuardRefusesExactlyWhatTheExecutingEngineMishandles()
    {
        var engine = Engine;
        var expected = ExpectedRefusals(engine);
        foreach (var shape in KnownShapes)
            AssertEqual((expected & shape) == 0, SwiftTransitionGuard.IsSupported(shape), $"{engine}: {shape} supported");
        AssertFalse(SwiftTransitionGuard.IsSupported((SwiftTransitionFacts)(1UL << 40)), $"{engine}: an unknown shape must be refused");
        AssertTrue(SwiftTransitionGuard.IsSupported(SwiftTransitionFacts.None), $"{engine}: no shape is always supported");
    }

    /// <summary>
    /// A member whose only route on this platform is a direct transition with <c>self</c> in the
    /// self register: <c>OriginShifter.Mutate</c> takes an inout non-frozen struct, so the
    /// <c>@_cdecl</c> wrapper declines it. The iOS and Mac Catalyst builds route it through a native
    /// bridge, which every engine runs; the other platforms' builds have no lowering evidence to route it, so it
    /// stays a direct import there and is guarded. Where the engine mishandles the self register,
    /// the call must throw before Swift runs (the point is not shifted) and name the member; where
    /// it does not, Swift shifts the point.
    /// </summary>
    public void TestDirectSelfRegisterMember_RefusesBeforeNativeOnlyWhereTheEngineMishandlesIt()
    {
        var engine = Engine;
        var routed = ImportRoutes.BridgesRouted;
        var refuses = !routed && (ExpectedRefusals(engine) & SwiftTransitionFacts.SelfRegister) != 0;

        var shifter = new OriginShifter(dx: 10.0, dy: 20.0);
        var point = new NonFrozenPoint(x: 1.0, y: 2.0);
        try
        {
            if (refuses)
            {
                SwiftTransitionUnsupportedException? refusal = null;
                try
                {
                    shifter.Mutate(ref point);
                }
                catch (SwiftTransitionUnsupportedException ex)
                {
                    refusal = ex;
                }
                AssertNotNull(refusal, $"{engine}: OriginShifter.Mutate must refuse on a direct route");
                AssertTrue(refusal!.Member.Contains("OriginShifter.Mutate", StringComparison.Ordinal), $"refusal names the member: {refusal.Member}");
                AssertTrue((refusal.RefusedFacts & SwiftTransitionFacts.SelfRegister) != 0, $"refused for the self register: {refusal.RefusedFacts}");
                AssertApproxEqual(1.0, point.X, 0.0001, "Swift did not run: x unchanged");
                AssertApproxEqual(2.0, point.Y, 0.0001, "Swift did not run: y unchanged");
            }
            else
            {
                shifter.Mutate(ref point);
                AssertApproxEqual(11.0, point.X, 0.0001, $"{engine}: x shifted by Swift");
                AssertApproxEqual(22.0, point.Y, 0.0001, $"{engine}: y shifted by Swift");
            }
        }
        finally
        {
            point.Dispose();
            shifter.Dispose();
        }
    }

    /// <summary>The shapes of <c>PendingRequestTask.Start</c>'s direct transition.</summary>
    private const SwiftTransitionFacts StartShapes = SwiftTransitionFacts.SelfRegister | SwiftTransitionFacts.LoweredSignature;

    /// <summary>
    /// A member no build routes: <c>PendingRequestTask.Start</c> returns a generic <c>Result</c>,
    /// which no native bridge carries, so every build, iOS included, keeps its direct transition
    /// with <c>self</c> in the self register and guards it. Where the engine mishandles that shape
    /// the call must throw before Swift runs and name the member. Where it does not, the guard lets
    /// the call through, but the call is not made here: the direct import declares the indirectly
    /// returned generic result as a pointer, a known mismatch with the Swift signature that is
    /// classified and left for its own fix.
    /// </summary>
#pragma warning disable SB0001
    [SupportedOSPlatform("ios16.0")]
    [SupportedOSPlatform("maccatalyst16.0")]
    [SupportedOSPlatform("macos13.0")]
    [SupportedOSPlatform("tvos16.0")]
    public void TestUnroutedMember_RefusesBeforeNativeWhereTheEngineMishandlesItsShape()
    {
        var engine = Engine;
        if ((ExpectedRefusals(engine) & StartShapes) == 0)
        {
            AssertTrue(SwiftTransitionGuard.IsSupported(StartShapes), $"{engine}: the guard admits Start's shapes");
            return;
        }

        using var task = new PendingRequestTask<nint, RequestTaskError>(42);
        SwiftTransitionUnsupportedException? refusal = null;
        try
        {
            _ = task.Start();
        }
        catch (SwiftTransitionUnsupportedException ex)
        {
            refusal = ex;
        }
        AssertNotNull(refusal, $"{engine}: PendingRequestTask.Start must refuse");
        AssertTrue(refusal!.Member.Contains("PendingRequestTask", StringComparison.Ordinal) && refusal.Member.Contains(".Start()", StringComparison.Ordinal),
            $"refusal names the member: {refusal.Member}");
        AssertEqual(StartShapes, refusal.Facts, "refusal carries the transition's shapes");
        AssertEqual(ExpectedRefusals(engine) & StartShapes, refusal.RefusedFacts, "refusal names exactly what the engine mishandles");
        AssertEqual((nint)42, TestLibFunctions.PendingRequestTaskValue(task), "the task is untouched");
    }
#pragma warning restore SB0001
}
