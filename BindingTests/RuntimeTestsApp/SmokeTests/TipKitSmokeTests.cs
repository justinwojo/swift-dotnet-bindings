// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if TIPKIT_SMOKE
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using TipKit;

namespace RuntimeTestsApp.SmokeTests;

/// <summary>
/// Session 6 end-to-end smoke test for the Apple-framework direct-mode pipeline
/// on TipKit. Consumes the externally-built <c>TipKit.Swift.iOS.dll</c> +
/// <c>TipKitSwiftBindings.xcframework</c> from the gitignored in-tree snapshot
/// at <c>BindingTests/obj/TipKitSnapshot/</c> AND the
/// <c>TipKitSmokeTip.swift</c> fixture under
/// <c>BindingTests/Sources/SwiftBindingsTestLib/SmokeFixtures/</c>.
///
/// <para>
/// <b>Three orthogonal things this smoke test pins:</b>
/// </para>
/// <list type="number">
///   <item>
///     <b>Session 1 build-infra <c>-D</c> threading (end-to-end, both sides).</b>
///     <see cref="TestTipKitSmokeFixtureWasCompiled"/> reflectively asserts that
///     the generator emitted <c>ReadTipKitSmokeIdentifier</c> on
///     <see cref="TestLibFunctions"/>. For that method to exist at runtime the
///     following chain must have held end-to-end:
///     <list type="bullet">
///       <item>Nuke threaded <c>-D TIPKIT_SMOKE</c> into <c>swiftc</c> when
///         compiling <c>SwiftBindingsTestLib</c>, so the Swift fixture's
///         <c>#if TIPKIT_SMOKE</c> block lands in the dylib.</item>
///       <item>Nuke threaded <c>-D TIPKIT_SMOKE</c> into <c>swift-frontend</c>
///         when dumping the ABI JSON, so the generator's view of the module
///         matches the dylib — without this the fixture exists in the binary
///         but is invisible to the generator and the method never gets
///         emitted.</item>
///       <item>The snapshot csproj was regenerated and the TipKit
///         ProjectReference resolved, so the <c>using TipKit;</c> directive at
///         the top of this file compiles at all.</item>
///     </list>
///     If this test ever stops finding the method, Session 1's
///     <c>CompileModuleSlice</c> plumbing in <c>Build.BindingTests.cs</c> has
///     regressed — half of the reason Session 6 exists.
///   </item>
///   <item>
///     <b>Fix #7 (PAT fallback to <c>object</c>) compile-time half pinned on a
///     real Apple framework surface.</b>
///     <see cref="TestTipUICollectionReusableViewViewStylePropertyIsObject"/>
///     reflectively checks that the generator-emitted
///     <c>TipKit.TipUICollectionReusableView.ViewStyle</c> property lowers
///     <c>any TipKit.TipViewStyle</c> (a protocol with <c>Self</c> requirements
///     → <c>ITipViewStyle&lt;TSelf&gt;</c>, which has no TSelf in scope at a
///     property boundary) to the literal <see cref="object"/> type. Regressing
///     fix #7 here means third-party consumer projects that use TipKit lose
///     the ability to read or write the <c>viewStyle</c> property.
///   </item>
///   <item>
///     <b>Fix #7 runtime-dispatch half (latent bug) pinned on the TipKit
///     surface.</b> <see cref="TestReadTipKitSmokeIdentifierDispatchLatentBug"/>
///     actually invokes the generator-emitted
///     <c>TestLibFunctions.ReadTipKitSmokeIdentifier(object)</c> on a live
///     <c>TipKitSmokeMinimalTip</c> and asserts it throws
///     <see cref="InvalidCastException"/> today, because the generator does
///     not emit <c>IExistentialBoxable</c> on synthetic PAT conformers —
///     the <c>_protocolConformanceSymbols</c> dictionary on
///     <c>TipKitSmokeMinimalTip</c> is empty (verified in the generated
///     <c>SwiftBindingsTestLib.cs</c>). This is the real-framework pin of the
///     same latent bug that Session 5's
///     <c>PATFallbackBoundaryTests.TestReadTaggedAssociatorDispatchLatentBug</c>
///     pins on the synthetic <c>TaggedAssociator</c> fixture — when the
///     generator starts emitting <c>IExistentialBoxable</c> for PAT conformers,
///     follow the flip checklist on that test to re-point both.
///   </item>
/// </list>
///
/// <para>
/// <b>Note on the <c>any Tip</c> parameter type:</b> Empirically, the
/// generator lowers the <c>any TipKit.Tip</c> parameter on
/// <c>readTipKitSmokeIdentifier</c> to the literal <see cref="object"/> type
/// (verified by running this test with a logging-only observer first). This
/// file pins the observed shape, not the specific generator branch taken:
/// there are several plausible causes (cross-module protocol reference,
/// Self/PAT requirement fallback, or a non-generic <c>ITip</c> interface that
/// still falls back to <c>object</c> at parameter position), and the exact
/// branch is implementation detail that may shift without the observed shape
/// changing. Either way, the net effect is the same as the Session 5 latent
/// bug pin: an <c>object</c> parameter that the runtime cannot box into an
/// <c>ExistentialContainer1</c> for a PAT conformer whose
/// <c>_protocolConformanceSymbols</c> dictionary is empty.
/// </para>
///
/// <para>
/// <b>Deliberately excluded:</b> Anything touching <c>Tips.configure(...)</c>,
/// any call that needs the Tips datastore, any entitlement-gated surface.
/// The smoke test is strictly metadata-only (reflection + constructor calls on
/// the synthetic fixture) so it runs in any environment where the TipKit
/// dylib and SwiftBindingsTestLib dylib are reachable by dyld.
/// </para>
/// </summary>
public class TipKitSmokeTests : TestBase
{
    public TipKitSmokeTests(TestResults results) : base(results) { }

    /// <summary>
    /// Session 1 plumbing validator: reflectively confirms the generator
    /// emitted <c>TestLibFunctions.ReadTipKitSmokeIdentifier</c> from the
    /// <c>#if TIPKIT_SMOKE</c>-gated Swift fixture. See the class-level
    /// comment for the full chain this pins.
    /// </summary>
    public void TestTipKitSmokeFixtureWasCompiled()
    {
        var method = typeof(TestLibFunctions).GetMethod(
            "ReadTipKitSmokeIdentifier",
            BindingFlags.Public | BindingFlags.Static);
        if (method is null)
        {
            TestLogger.Info("TestLibFunctions.ReadTipKitSmokeIdentifier = <missing>");
        }
        else
        {
            var parameters = method.GetParameters();
            var paramSummary = parameters.Length == 0
                ? "no parameters"
                : $"param[0]={parameters[0].ParameterType.FullName}";
            TestLogger.Info($"TestLibFunctions.ReadTipKitSmokeIdentifier = {method} " +
                            $"({paramSummary}, ret={method.ReturnType.FullName})");
        }
        AssertTrue(method is not null,
            "TestLibFunctions.ReadTipKitSmokeIdentifier must exist on the generated " +
            "SwiftBindingsTestLib bindings. If this assertion fails, Session 1's " +
            "`-D TIPKIT_SMOKE` threading through CompileModuleSlice has regressed — " +
            "either the dylib compile dropped the define (fixture not in binary) or " +
            "the ABI JSON dump dropped it (fixture not visible to the generator). " +
            "Both legs of the plumbing in Build.BindingTests.cs are required; this " +
            "is exactly the end-to-end regression pin that Session 1 exists to prevent.");
    }

    /// <summary>
    /// Fix #7 compile-time half pinned on a real Apple framework surface:
    /// reflectively confirms that <c>TipKit.TipUICollectionReusableView.ViewStyle</c>
    /// lowers <c>any TipKit.TipViewStyle</c> to the literal <see cref="object"/>
    /// C# type. <c>TipViewStyle</c> is a Self-requiring protocol so the generic
    /// interface name <c>ITipViewStyle&lt;TSelf&gt;</c> has no type argument in
    /// scope at the property boundary — the PAT fallback in
    /// <c>ExistentialHandler.GetPublicExistentialType()</c> must rewrite it to
    /// <c>object</c>. A regression here means third-party consumer projects
    /// that reference TipKit fail to compile with CS0305 or similar.
    /// </summary>
    [SupportedOSPlatform("ios17.0")]
    public void TestTipUICollectionReusableViewViewStylePropertyIsObject()
    {
        var cellType = typeof(TipKit.TipUICollectionReusableView);
        var prop = cellType.GetProperty(
            "ViewStyle",
            BindingFlags.Instance | BindingFlags.Public);
        AssertTrue(prop is not null,
            "TipKit.TipUICollectionReusableView.ViewStyle must exist on the generated " +
            "TipKit binding. If this fails, the generator dropped the property or " +
            "renamed it; rerun `nuke regenerate-apple-snapshot --framework TipKit` " +
            "and check TipKitSnapshot/TipKit.cs.");

        TestLogger.Info($"TipKit.TipUICollectionReusableView.ViewStyle type = {prop!.PropertyType.FullName}");
        AssertEqual(typeof(object), prop.PropertyType,
            "TipKit.TipUICollectionReusableView.ViewStyle must lower `any TipKit.TipViewStyle` " +
            "to the literal `object` C# type. Fix #7 (commit 4235d568) rewrites " +
            "Self-requiring existentials to object because ITipViewStyle<TSelf> has no type " +
            "argument in scope at a property boundary. If this assertion fails, the PAT " +
            "fallback in ExistentialHandler has regressed and consumer projects that " +
            "reference TipKit will lose the ability to read or write ViewStyle.");
    }

    /// <summary>
    /// Fix #7 compile-time half on the SwiftBindingsTestLib side: pins that
    /// <c>TestLibFunctions.ReadTipKitSmokeIdentifier</c> lowers its
    /// <c>any TipKit.Tip</c> parameter to the literal <see cref="object"/>
    /// type. This is the TipKit analogue of
    /// <c>PATFallbackBoundaryTests.TestReadTaggedAssociatorIsLoweredToObjectParameter</c>,
    /// but on a real Apple-framework protocol crossed through a second
    /// module's ABI JSON — a subtler regression surface than the synthetic
    /// in-module case.
    /// </summary>
    public void TestReadTipKitSmokeIdentifierLowersToObjectParameter()
    {
        var method = typeof(TestLibFunctions).GetMethod(
            "ReadTipKitSmokeIdentifier",
            BindingFlags.Public | BindingFlags.Static);
        AssertTrue(method is not null,
            "Precondition: ReadTipKitSmokeIdentifier must exist. If this fails, " +
            "see TestTipKitSmokeFixtureWasCompiled for the Session 1 plumbing.");

        var parameters = method!.GetParameters();
        AssertEqual(1, parameters.Length,
            "ReadTipKitSmokeIdentifier must have exactly one parameter.");
        TestLogger.Info($"ReadTipKitSmokeIdentifier parameter[0] type = {parameters[0].ParameterType.FullName}");
        TestLogger.Info($"ReadTipKitSmokeIdentifier return type = {method.ReturnType.FullName}");

        AssertEqual(typeof(object), parameters[0].ParameterType,
            "ReadTipKitSmokeIdentifier's `any TipKit.Tip` parameter must lower to the " +
            "literal `object` C# type. Fix #7 (commit 4235d568) rewrites PAT / " +
            "Self-requirement / cross-module existential parameters to `object` at the " +
            "call site. A regression here means the emitted signature references a " +
            "generic or cross-module interface that consumer projects cannot resolve.");
        AssertEqual(typeof(string), method.ReturnType,
            "ReadTipKitSmokeIdentifier must return string (the dispatched `.id`).");
    }

    /// <summary>
    /// Fix #7 runtime-dispatch half pinned on the real TipKit surface: invokes
    /// <c>TestLibFunctions.ReadTipKitSmokeIdentifier(new TipKitSmokeMinimalTip())</c>
    /// and confirms it throws <see cref="InvalidCastException"/> today. Same
    /// latent bug as
    /// <c>PATFallbackBoundaryTests.TestReadTaggedAssociatorDispatchLatentBug</c>:
    /// the generator does not emit <c>IExistentialBoxable</c> on PAT conformers,
    /// so <c>TipKitSmokeMinimalTip</c>'s <c>_protocolConformanceSymbols</c>
    /// dictionary is empty (verified against generated
    /// <c>output/SwiftBindingsTestLib.cs</c>). The factory cascade in
    /// <c>Swift.Runtime.ExistentialContainerFactory.GetOrCreate&lt;object&gt;</c>
    /// cannot find a conformance witness and lands on the throw branch.
    /// </summary>
    [SupportedOSPlatform("ios17.0")]
    public void TestReadTipKitSmokeIdentifierDispatchLatentBug()
    {
        using var tip = new TipKitSmokeMinimalTip();

        Exception? thrown = null;
        string? dispatched = null;
        try
        {
            dispatched = TestLibFunctions.ReadTipKitSmokeIdentifier(tip);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        TestLogger.Info(thrown is null
            ? $"ReadTipKitSmokeIdentifier(TipKitSmokeMinimalTip) returned \"{dispatched}\""
            : $"ReadTipKitSmokeIdentifier(TipKitSmokeMinimalTip) threw {thrown.GetType().Name}: {thrown.Message}");

        // LATENT-BUG PIN: flip to an AssertTrue on a concrete `.id` string when
        // the generator starts emitting IExistentialBoxable on PAT conformers
        // AND populating _protocolConformanceSymbols for cross-module protocol
        // conformances (TipKit.Tip → TipKitSmokeMinimalTip's conformance witness).
        // Until then, ExistentialContainerFactory.GetOrCreate<object> in
        // src/Swift.Runtime/src/Swift/Runtime/ExistentialContainer.cs cannot box
        // the value because TipKitSmokeMinimalTip implements neither
        // ISwiftExistentialConvertible<ExistentialContainer1> nor
        // IExistentialBoxable.
        //
        // Flip checklist — when the runtime half lands:
        //   1. Replace the InvalidCastException assertion below with
        //      AssertTrue(!string.IsNullOrEmpty(dispatched), ...). The Swift
        //      `Tip.id` default comes from a protocol extension (usually the
        //      fully-qualified type name); the exact string is implementation-
        //      defined, so assert non-empty rather than an exact value.
        //   2. Mirror the flip in Session 5's
        //      PATFallbackBoundaryTests.TestReadTaggedAssociatorDispatchLatentBug
        //      — both pins must move together since they share the same
        //      generator code path.
        AssertTrue(thrown is InvalidCastException,
            "Documents current broken dispatch for PAT fallback on real TipKit surface: " +
            "passing a TipKitSmokeMinimalTip value through ReadTipKitSmokeIdentifier(object) " +
            "must throw InvalidCastException today because the factory cannot box a " +
            "PAT-conformer into an ExistentialContainer1. When the generator starts emitting " +
            "IExistentialBoxable on PAT-conformer structs and populating " +
            "_protocolConformanceSymbols for cross-module protocol witnesses, this assertion " +
            "will start failing and the fixer must follow the flip checklist above AND the " +
            "matching checklist in PATFallbackBoundaryTests.");
    }
}

#endif
