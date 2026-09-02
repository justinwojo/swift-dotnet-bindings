// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if VISIONKIT_SMOKE
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using RuntimeTestsApp.Infrastructure;

namespace RuntimeTestsApp.SmokeTests;

/// <summary>
/// Smoke test for the Apple-framework direct-mode pipeline on VisionKit — the
/// multi-document <c>.tbd</c> case. VisionKit's stub library re-exports the private
/// DocumentCamera framework, so its <c>.tbd</c> carries two <c>--- !tapi-tbd</c>
/// documents: the framework's own symbols in the first, DocumentCamera's in the second.
///
/// That matters to emission because the symbol set parsed out of the <c>.tbd</c> is the
/// oracle for two decisions with no other evidence source: whether an accessor is
/// <c>async</c> (a sibling <c>…Tu</c> / <c>…TjTu</c> symbol) and whether a protocol
/// requirement has a method descriptor (a sibling <c>…Tq</c>). A missing symbol reads as
/// a legitimate "no" — so losing a document does not fail the build, it silently emits a
/// synchronous, conformance-poorer binding. The shape assertions below are the runtime
/// end of that check: they only hold when VisionKit's own document was read.
///
/// Consumes the externally-built <c>VisionKit.Swift.iOS.dll</c> +
/// <c>VisionKitSwiftBindings.xcframework</c> from the gitignored in-tree snapshot at
/// <c>BindingTests/obj/VisionKitSnapshot/</c>.
///
/// Gated by <c>VISIONKIT_SMOKE</c>. Regenerate with
/// <c>nuke regenerate-apple-snapshot --framework VisionKit</c>.
///
/// <b>Deliberately excluded:</b> anything needing a camera session, a live document scan,
/// or an attached interaction. This smoke test is strictly metadata-only, and the members
/// it inspects are reached reflectively so the app's own deployment target stays put.
/// </summary>
public class VisionKitSmokeTests : TestBase
{
    public VisionKitSmokeTests(TestResults results) : base(results) { }

    /// <summary>
    /// The framework's own types load — the baseline that the first document's symbols
    /// reached the emitter at all.
    /// </summary>
    [SupportedOSPlatform("ios16.0")]
    public void TestImageAnalysisInteractionTypeLoads()
    {
        try
        {
            var type = typeof(VisionKit.ImageAnalysisInteraction);
            TestLogger.Info($"typeof(VisionKit.ImageAnalysisInteraction) = {type.FullName}");
            AssertTrue(type is not null,
                "VisionKit.ImageAnalysisInteraction must be loadable from the generated binding.");
        }
        catch (Exception ex)
        {
            LogExceptionChain(ex);
            throw;
        }
    }

    /// <summary>
    /// <c>ImageAnalysisInteraction.subjects</c> is an <c>async</c> property. Its async
    /// evidence is the <c>…vgTjTu</c> sibling symbol in the .tbd's FIRST document, so this
    /// binds as a <c>Task</c>-returning method only when that document was parsed. Read
    /// reflectively: invoking it would need a live interaction.
    /// </summary>
    [SupportedOSPlatform("ios16.0")]
    public void TestSubjectsBindsAsAsync()
    {
        try
        {
            var method = typeof(VisionKit.ImageAnalysisInteraction)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .SingleOrDefault(m => m.Name == "GetSubjectsAsync");

            AssertTrue(method is not null,
                "ImageAnalysisInteraction.GetSubjectsAsync must be emitted — an async Swift property " +
                "binds synchronously when the .tbd document carrying its async marker is dropped.");
            TestLogger.Info($"GetSubjectsAsync returns {method!.ReturnType.FullName}");
            AssertTrue(typeof(Task).IsAssignableFrom(method.ReturnType),
                $"GetSubjectsAsync must return a Task, got {method.ReturnType.FullName}.");
        }
        catch (Exception ex)
        {
            LogExceptionChain(ex);
            throw;
        }
    }

    /// <summary>
    /// The same async evidence one level down, on the nested <c>Subject</c> type's
    /// <c>image</c> property — a second, independently-mangled member from the first
    /// document.
    /// </summary>
    [SupportedOSPlatform("ios17.0")]
    public void TestSubjectImageBindsAsAsync()
    {
        try
        {
            var method = typeof(VisionKit.ImageAnalysisInteraction.Subject)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .SingleOrDefault(m => m.Name == "GetImageAsync");

            AssertTrue(method is not null,
                "ImageAnalysisInteraction.Subject.GetImageAsync must be emitted.");
            TestLogger.Info($"Subject.GetImageAsync returns {method!.ReturnType.FullName}");
            AssertTrue(typeof(Task).IsAssignableFrom(method.ReturnType),
                $"Subject.GetImageAsync must return a Task, got {method.ReturnType.FullName}.");
        }
        catch (Exception ex)
        {
            LogExceptionChain(ex);
            throw;
        }
    }

    /// <summary>
    /// The delegate protocol's proxy exists and implements the emitted interface. This is
    /// the baseline shape only — the proxy and its vtable are emitted from the ABI JSON and
    /// survive a missing method descriptor by design, so this assertion is deliberately NOT
    /// the .tbd check. <see cref="TestImageAnalysisInteractionDelegateConformanceShipped"/>
    /// is.
    /// </summary>
    [SupportedOSPlatform("ios16.0")]
    public void TestImageAnalysisInteractionDelegateProxyExists()
    {
        try
        {
            var proxy = typeof(VisionKit.SwiftInterop.ImageAnalysisInteractionDelegateProxy);
            var iface = typeof(VisionKit.IImageAnalysisInteractionDelegate);
            TestLogger.Info($"proxy = {proxy.FullName}, interface = {iface.FullName}");
            AssertTrue(iface.IsAssignableFrom(proxy),
                "ImageAnalysisInteractionDelegateProxy must implement IImageAnalysisInteractionDelegate.");

            var members = iface.GetMethods(BindingFlags.Public | BindingFlags.Instance);
            TestLogger.Info($"IImageAnalysisInteractionDelegate declares {members.Length} member(s)");
            AssertTrue(members.Length > 0,
                "IImageAnalysisInteractionDelegate must declare at least one requirement.");
        }
        catch (Exception ex)
        {
            LogExceptionChain(ex);
            throw;
        }
    }

    /// <summary>
    /// The <c>…Tq</c> half of the .tbd oracle. A protocol requirement whose method descriptor
    /// is missing from the parsed symbol set marks the protocol
    /// <c>HasMissingTbdMethodDescriptors</c>, which drops the
    /// <c>extension EveryProtocol: VisionKit.ImageAnalysisInteractionDelegate</c> conformance
    /// from the wrapper — and with it the <c>Get_EveryProtocol_…_WitnessTable</c> entry point
    /// the proxy calls to hand Swift a witness table for a C# implementation. The proxy type
    /// itself is emitted either way (the vtable/receiver path stays available on purpose), so
    /// the conformance is what must be asserted. Those descriptors live in VisionKit's own
    /// document; drop it and this getter is gone.
    ///
    /// Calls the getter rather than only reflecting on it, so dyld confirms the symbol is
    /// really in the shipped wrapper binary.
    /// </summary>
    [SupportedOSPlatform("ios16.0")]
    public void TestImageAnalysisInteractionDelegateConformanceShipped()
    {
        try
        {
            var proxy = typeof(VisionKit.SwiftInterop.ImageAnalysisInteractionDelegateProxy);
            var nativeMethods = proxy.GetNestedType("NativeMethods", BindingFlags.NonPublic);
            AssertTrue(nativeMethods is not null,
                "ImageAnalysisInteractionDelegateProxy must declare its NativeMethods P/Invoke class.");

            var getWitnessTable = nativeMethods!.GetMethod(
                "GetWitnessTable", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            AssertTrue(getWitnessTable is not null,
                "The EveryProtocol witness-table getter must be emitted — it is dropped when the " +
                "protocol's Tq method descriptors are absent from the parsed .tbd symbol set.");

            var witnessTable = (IntPtr)getWitnessTable!.Invoke(null, null)!;
            TestLogger.Info($"Get_EveryProtocol_ImageAnalysisInteractionDelegate_WitnessTable = 0x{witnessTable:x}");
            AssertTrue(witnessTable != IntPtr.Zero,
                "The EveryProtocol conformance witness table must resolve to a non-null pointer.");
        }
        catch (Exception ex)
        {
            LogExceptionChain(ex);
            throw;
        }
    }

    private static void LogExceptionChain(Exception ex)
    {
        var inner = ex;
        var depth = 0;
        while (inner != null)
        {
            TestLogger.Info($"  [ex{depth}] {inner.GetType().FullName}: {inner.Message}");
            if (inner.StackTrace != null)
                TestLogger.Info($"  [ex{depth}] stack: {inner.StackTrace}");
            inner = inner.InnerException;
            depth++;
        }
    }
}

#endif
