// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if VISIONKIT_SMOKE
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;

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

    // ================================================================
    // Delegate assignment + reverse dispatch
    //
    // Everything below drives the shape a consumer actually hits: assign a C#
    // implementation to an Apple-framework delegate property, then let Swift call back
    // through it. Three independent defects live on that path, and each one produces a
    // crash inside the framework with no managed frame, so they can only be caught by
    // running the round-trip.
    //
    //  * Container shape. A class-bound protocol is carried by a TWO-word existential
    //    ([classRef][witnessTable]); an opaque one by five. Apple-direct ABI dumps spell
    //    the class-bound constraint in the sugared dialect (`<Self : AnyObject>`) rather
    //    than the desugared one (`<t_0_0 : AnyObject>`) that a compiled-from-source dump
    //    uses, so a parser that only recognises the desugared spelling picks the opaque
    //    arm, writes the witness table into a word Swift never reads, and Swift dispatches
    //    through a null witness table.
    //  * Non-retaining sink. These delegate properties are `weak var`, so Swift takes no
    //    retain on the conformer box the setter mints. Without a managed root the next
    //    collection finalizes the box and the property silently reads back nil.
    //  * Receiver peer identity. The receiver Swift hands back is the same object the
    //    consumer created; constructing a fresh managed peer per callback hands the
    //    consumer an unfamiliar instance whose managed state is empty.
    // ================================================================

    private const int GcCycles = 6;

    /// <summary>
    /// Force a collection from a worker thread so Mono's conservative stack scan on the main
    /// thread cannot pin the objects the test just dropped. Mirrors the helper the first-party
    /// lifetime suites use.
    /// </summary>
    private static void ForceGc()
    {
        var worker = new System.Threading.Thread(ForceGcWorker) { IsBackground = true };
        worker.Start();
        worker.Join();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceGcWorker()
    {
        for (int i = 0; i < GcCycles; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }

    /// <summary>
    /// Builds a scanner configured with stored state the delegate callbacks can read back.
    /// <c>recognizesMultipleItems: true</c> is deliberately non-default: a callback that sees
    /// it as <c>true</c> is holding the live Swift instance, not an empty peer.
    /// </summary>
    [SupportedOSPlatform("ios16.0")]
    private static VisionKit.DataScannerViewController CreateScanner()
    {
        var dataTypes = new HashSet<VisionKit.DataScannerViewController.RecognizedDataType>
        {
            VisionKit.DataScannerViewController.RecognizedDataType.GetText(),
        };
        return new VisionKit.DataScannerViewController(dataTypes, recognizesMultipleItems: true);
    }

    /// <summary>
    /// The container-shape tripwire for <c>DataScannerViewControllerDelegate</c>, armed against
    /// the real framework. Resolving the witness table is what runs the check: the proxy asks the
    /// Swift wrapper for <c>MemoryLayout&lt;any DataScannerViewControllerDelegate&gt;.size</c> and
    /// throws unless it matches the arm it was emitted for. A proxy emitted on the wrong arm reds
    /// here — with a readable message — instead of trapping inside VisionKit on the first callback.
    /// </summary>
    [SupportedOSPlatform("ios16.0")]
    public void TestDataScannerDelegateProxyExistentialLayoutVerified()
    {
        try
        {
            var handle = VisionKit.SwiftInterop.DataScannerViewControllerDelegateProxy.ProtocolWitnessTableHandle;
            TestLogger.Info($"DataScannerViewControllerDelegate witness table = 0x{handle:x}");
            AssertTrue(handle != IntPtr.Zero,
                "DataScannerViewControllerDelegateProxy must resolve a witness table with its " +
                "existential-layout check satisfied.");
        }
        catch (Exception ex)
        {
            LogExceptionChain(ex);
            throw;
        }
    }

    /// <summary>
    /// The same tripwire on the second class-bound protocol in the framework, so a regression that
    /// only reached one emitter path still reds this class.
    /// </summary>
    [SupportedOSPlatform("ios16.0")]
    public void TestImageAnalysisInteractionDelegateProxyExistentialLayoutVerified()
    {
        try
        {
            var handle = VisionKit.SwiftInterop.ImageAnalysisInteractionDelegateProxy.ProtocolWitnessTableHandle;
            TestLogger.Info($"ImageAnalysisInteractionDelegate witness table = 0x{handle:x}");
            AssertTrue(handle != IntPtr.Zero,
                "ImageAnalysisInteractionDelegateProxy must resolve a witness table with its " +
                "existential-layout check satisfied.");
        }
        catch (Exception ex)
        {
            LogExceptionChain(ex);
            throw;
        }
    }

    /// <summary>
    /// The controller constructs on a simulator even though scanning is unsupported there.
    /// <c>isSupported</c> / <c>isAvailable</c> gate <c>startScanning()</c>, not
    /// initialization — the delegate round-trips below depend on that, so it is asserted
    /// separately rather than folded into them.
    /// </summary>
    [SupportedOSPlatform("ios16.0")]
    public void TestDataScannerViewControllerConstructsWithoutCamera()
    {
        try
        {
            TestLogger.Info($"DataScannerViewController.IsSupported = {VisionKit.DataScannerViewController.IsSupported}, " +
                            $"IsAvailable = {VisionKit.DataScannerViewController.IsAvailable}");

            var scanner = CreateScanner();
            AssertNotNull(scanner, "DataScannerViewController must construct without a usable camera.");
            AssertTrue(scanner.RecognizesMultipleItems,
                "the constructor's stored configuration must be readable back off the instance.");
            GC.KeepAlive(scanner);
        }
        catch (Exception ex)
        {
            LogExceptionChain(ex);
            throw;
        }
    }

    /// <summary>
    /// The headline round-trip. Assign a C# implementation to <c>DataScannerViewController.delegate</c>,
    /// read the property back — which vends a carrier over the existential Swift is actually
    /// storing, not the object that was assigned — and dispatch through it. The call goes
    /// C# → Swift wrapper → witness table → conformer box → C# vtable receiver → implementation,
    /// which is the exact path that trapped inside VisionKit when the container was built on the
    /// wrong arm.
    ///
    /// <para>Three properties are asserted at once: the call arrives (the implementation's counter
    /// moves), the receiver is the consumer's own instance (<c>ReferenceEquals</c>), and that
    /// receiver is the live Swift object rather than an empty peer (its stored
    /// <c>recognizesMultipleItems</c> reads back <c>true</c>).</para>
    /// </summary>
    [SupportedOSPlatform("ios16.0")]
    public void TestDataScannerDelegateDispatchesThroughReadBackProxy()
    {
        try
        {
            var scanner = CreateScanner();
            var impl = new RecordingDataScannerDelegate();
            scanner.Delegate = impl;

            var readBack = scanner.Delegate;
            AssertNotNull(readBack, "the weak delegate property must read back the stored existential.");
            AssertTrue(!ReferenceEquals(readBack, impl),
                "the read-back value is a carrier over the Swift-held existential, not the assigned object.");

            readBack!.DataScannerDidZoom(scanner);

            AssertEqual(1, impl.DidZoomCount,
                "Swift must dispatch dataScannerDidZoom into the C# implementation.");
            AssertTrue(ReferenceEquals(scanner, impl.LastScanner),
                "the receiver handed to the callback must be the consumer's own managed peer.");
            AssertTrue(impl.LastScannerRecognizesMultipleItems,
                "the receiver must be the live Swift instance, with its constructor state intact.");

            GC.KeepAlive(impl);
            GC.KeepAlive(scanner);
        }
        catch (Exception ex)
        {
            LogExceptionChain(ex);
            throw;
        }
    }

    /// <summary>
    /// A read-back carrier owns a reference into the conformer box and releases it when it is
    /// finalized. Dropping one and running finalizers must leave the delegate Swift stores
    /// untouched: the process survives the finalizer, and a second read-back still dispatches.
    /// A carrier finalizing over a wrong-shape container releases a word that is not a class
    /// reference, which is a crash rather than a failed assertion — so surviving to the second
    /// dispatch is itself the assertion.
    /// </summary>
    [SupportedOSPlatform("ios16.0")]
    public void TestDataScannerDelegateSurvivesReadBackCarrierFinalization()
    {
        try
        {
            var scanner = CreateScanner();
            var impl = new RecordingDataScannerDelegate();
            scanner.Delegate = impl;

            DispatchThroughDroppedCarrier(scanner);
            AssertEqual(1, impl.DidZoomCount, "the first dispatch reached the implementation.");

            ForceGc();

            var second = scanner.Delegate;
            AssertNotNull(second, "the delegate must still be there after the first carrier was finalized.");
            second!.DataScannerDidZoom(scanner);

            AssertEqual(2, impl.DidZoomCount,
                "a second read-back must dispatch after the first carrier was collected.");
            AssertTrue(ReferenceEquals(scanner, impl.LastScanner),
                "receiver identity must survive a carrier finalization cycle.");

            GC.KeepAlive(impl);
            GC.KeepAlive(scanner);
        }
        catch (Exception ex)
        {
            LogExceptionChain(ex);
            throw;
        }
    }

    /// <summary>
    /// Reads the property, dispatches once, and lets the carrier fall out of scope — it is
    /// unreachable the moment this returns, which is the state the caller then collects.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    [SupportedOSPlatform("ios16.0")]
    private static void DispatchThroughDroppedCarrier(VisionKit.DataScannerViewController scanner)
    {
        var carrier = scanner.Delegate;
        carrier!.DataScannerDidZoom(scanner);
    }

    /// <summary>
    /// VisionKit declares <c>weak var delegate</c>, so Swift takes no retain on the conformer box
    /// the setter mints and the consumer never sees that box to hold it themselves. Assign, drop
    /// every managed reference the assignment created, collect — and the delegate must still be
    /// installed and still dispatch to the same implementation object the consumer holds.
    /// The carrier minted for a non-retaining sink follows the implementation's lifetime,
    /// so the consumer's own reference to their delegate is what keeps the slot populated.
    /// </summary>
    [SupportedOSPlatform("ios16.0")]
    public void TestDataScannerWeakDelegateSinkSurvivesCollection()
    {
        try
        {
            var scanner = CreateScanner();
            var impl = new RecordingDataScannerDelegate();

            AssignScannerDelegate(scanner, impl);
            ForceGc();

            var readBack = scanner.Delegate;
            AssertNotNull(readBack, "a weak delegate sink must not read back nil after a collection.");

            readBack!.DataScannerDidZoom(scanner);
            AssertEqual(1, impl.DidZoomCount,
                "dispatch after the collection must reach the same implementation the consumer holds.");

            GC.KeepAlive(impl);
            GC.KeepAlive(scanner);
        }
        catch (Exception ex)
        {
            LogExceptionChain(ex);
            throw;
        }
    }

    /// <summary>
    /// Performs the assignment in a frame that goes away, so the proxy the setter minted is
    /// unreachable from the test once this returns.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    [SupportedOSPlatform("ios16.0")]
    private static void AssignScannerDelegate(
        VisionKit.DataScannerViewController scanner, VisionKit.IDataScannerViewControllerDelegate impl)
    {
        scanner.Delegate = impl;
    }

    /// <summary>
    /// The five requirements that cannot be driven from the simulator, pinned to their designed
    /// failure. <c>didTapOn</c> takes a <c>RecognizedItem</c> and the three collection callbacks
    /// take <c>[RecognizedItem]</c>; those parameter types are not dispatchable through a
    /// protocol-typed value, so the generator emits no wrapper entry point for them and the
    /// carrier reports <see cref="NotSupportedException"/>. Swift only ever calls them from a live
    /// camera scan, which a simulator cannot run, and <c>RecognizedItem</c> has no constructible
    /// surface (its payloads come from Vision observations), so the ABI for those receiver shapes
    /// is covered by the first-party reverse-dispatch fixtures instead. What is asserted here is
    /// that the unreachable half fails honestly rather than trapping.
    /// </summary>
    [SupportedOSPlatform("ios16.0")]
    public void TestDataScannerUndispatchableRequirementsReportNotSupported()
    {
        try
        {
            var scanner = CreateScanner();
            var impl = new RecordingDataScannerDelegate();
            scanner.Delegate = impl;

            var readBack = scanner.Delegate;
            AssertNotNull(readBack, "the delegate must read back before probing its members.");

            var empty = Array.Empty<VisionKit.RecognizedItem>();
#pragma warning disable SB0003 // deliberately calling the members the generator marked non-dispatchable
            // The item argument is never dereferenced: the carrier rejects the call before it
            // reaches any marshalling, which is precisely the behaviour under test.
            AssertThrows<NotSupportedException>(() => readBack!.DataScannerDidTapOn(scanner, null!),
                "didTapOn is not dispatchable through a protocol-typed value.");
            AssertThrows<NotSupportedException>(() => readBack!.DataScannerDidAddAllItems(scanner, empty, empty),
                "didAdd/allItems is not dispatchable through a protocol-typed value.");
            AssertThrows<NotSupportedException>(() => readBack!.DataScannerDidUpdateAllItems(scanner, empty, empty),
                "didUpdate/allItems is not dispatchable through a protocol-typed value.");
            AssertThrows<NotSupportedException>(() => readBack!.DataScannerDidRemoveAllItems(scanner, empty, empty),
                "didRemove/allItems is not dispatchable through a protocol-typed value.");
            AssertThrows<NotSupportedException>(
                () => readBack!.DataScannerBecameUnavailableWithError(
                    scanner, VisionKit.DataScannerViewController.ScanningUnavailable.CameraRestricted),
                "becameUnavailableWithError is not dispatchable through a protocol-typed value.");
#pragma warning restore SB0003

            AssertEqual(0, impl.TotalCallbacks,
                "a rejected call must not reach the implementation.");

            GC.KeepAlive(impl);
            GC.KeepAlive(scanner);
        }
        catch (Exception ex)
        {
            LogExceptionChain(ex);
            throw;
        }
    }

    /// <summary>
    /// Every requirement on the protocol, driven through a proxy built directly over the C#
    /// implementation. Constructing that proxy is what mints a conformer box and resolves the
    /// witness table, so the container-shape check runs here too; the calls themselves stay on the
    /// managed side (the carrier forwards to the implementation it wraps), so this pins the full
    /// interface shape and parameter fidelity — array arity, distinct added/all collections, the
    /// error case — for the members the simulator cannot drive across the ABI.
    /// </summary>
    [SupportedOSPlatform("ios16.0")]
    public void TestDataScannerDelegateProxyForwardsEveryRequirement()
    {
        try
        {
            var scanner = CreateScanner();
            var impl = new RecordingDataScannerDelegate();
            using var proxy = new VisionKit.SwiftInterop.DataScannerViewControllerDelegateProxy(impl);

            var added = Array.Empty<VisionKit.RecognizedItem>();
            var all = Array.Empty<VisionKit.RecognizedItem>();

#pragma warning disable SB0003 // a C#-backed carrier forwards these; only the Swift-backed arm rejects them
            proxy.DataScannerDidZoom(scanner);
            proxy.DataScannerDidAddAllItems(scanner, added, all);
            proxy.DataScannerDidUpdateAllItems(scanner, added, all);
            proxy.DataScannerDidRemoveAllItems(scanner, added, all);
            proxy.DataScannerBecameUnavailableWithError(
                scanner, VisionKit.DataScannerViewController.ScanningUnavailable.CameraRestricted);
#pragma warning restore SB0003

            AssertEqual(1, impl.DidZoomCount, "didZoom reached the implementation.");
            AssertEqual(1, impl.DidAddCount, "didAdd reached the implementation.");
            AssertEqual(1, impl.DidUpdateCount, "didUpdate reached the implementation.");
            AssertEqual(1, impl.DidRemoveCount, "didRemove reached the implementation.");
            AssertEqual(1, impl.BecameUnavailableCount, "becameUnavailableWithError reached the implementation.");
            AssertEqual(VisionKit.DataScannerViewController.ScanningUnavailable.CameraRestricted,
                impl.LastError, "the error case round-tripped.");
            AssertEqual(0, impl.LastItemsCount, "the item collection round-tripped its count.");
            AssertEqual(0, impl.LastAllItemsCount, "the allItems collection round-tripped its count.");
            AssertTrue(ReferenceEquals(scanner, impl.LastScanner),
                "every requirement carries the same receiver.");

            GC.KeepAlive(scanner);
        }
        catch (Exception ex)
        {
            LogExceptionChain(ex);
            throw;
        }
    }

    /// <summary>
    /// The same assign → read back → dispatch round-trip on the framework's other class-bound
    /// delegate. All three dispatchable requirements are driven, and the two that carry a
    /// <c>Bool</c> assert the scalar round-trips with the value that was sent — the parameter
    /// coverage the DataScanner protocol cannot give (its one dispatchable requirement takes
    /// only the receiver).
    /// </summary>
    [SupportedOSPlatform("ios16.0")]
    public void TestImageAnalysisInteractionDelegateDispatchesThroughReadBackProxy()
    {
        try
        {
            var interaction = new VisionKit.ImageAnalysisInteraction();
            var impl = new RecordingImageAnalysisInteractionDelegate();
            interaction.Delegate = impl;

            var readBack = interaction.Delegate;
            AssertNotNull(readBack, "the weak delegate property must read back the stored existential.");

            readBack!.InteractionLiveTextButtonDidChangeToVisible(interaction, true);
            readBack.InteractionHighlightSelectedItemsDidChange(interaction, false);

            AssertEqual(1, impl.LiveTextButtonDidChangeCount,
                "Swift must dispatch liveTextButtonDidChange into the C# implementation.");
            AssertEqual(1, impl.HighlightSelectedItemsDidChangeCount,
                "Swift must dispatch highlightSelectedItemsDidChange into the C# implementation.");
            AssertTrue(impl.LastVisible, "the Bool parameter must round-trip as sent (true).");
            AssertFalse(impl.LastHighlightSelectedItems, "the Bool parameter must round-trip as sent (false).");
            AssertTrue(ReferenceEquals(interaction, impl.LastInteraction),
                "the receiver handed to the callback must be the consumer's own managed peer.");

            GC.KeepAlive(impl);
            GC.KeepAlive(interaction);
        }
        catch (Exception ex)
        {
            LogExceptionChain(ex);
            throw;
        }
    }

    /// <summary>
    /// <c>textSelectionDidChange</c> is the same protocol's iOS 17 requirement, so it sits in its
    /// own test rather than widening the availability of the round-trip above. Dispatch runs
    /// through our own witness table for the EveryProtocol conformer, so it does not depend on
    /// the host OS carrying the newer requirement.
    /// </summary>
    [SupportedOSPlatform("ios17.0")]
    public void TestImageAnalysisInteractionTextSelectionDispatchesThroughReadBackProxy()
    {
        try
        {
            var interaction = new VisionKit.ImageAnalysisInteraction();
            var impl = new RecordingImageAnalysisInteractionDelegate();
            interaction.Delegate = impl;

            var readBack = interaction.Delegate;
            AssertNotNull(readBack, "the weak delegate property must read back the stored existential.");

            readBack!.TextSelectionDidChange(interaction);

            AssertEqual(1, impl.TextSelectionDidChangeCount,
                "Swift must dispatch textSelectionDidChange into the C# implementation.");
            AssertTrue(ReferenceEquals(interaction, impl.LastInteraction),
                "the receiver handed to the callback must be the consumer's own managed peer.");

            GC.KeepAlive(impl);
            GC.KeepAlive(interaction);
        }
        catch (Exception ex)
        {
            LogExceptionChain(ex);
            throw;
        }
    }

    /// <summary>
    /// <c>ImageAnalysisInteraction.delegate</c> is a <c>weak var</c> too, and it is reached through
    /// a different emitter path (a plain Swift class peer rather than a view controller), so the
    /// weak-sink survival is asserted independently.
    /// </summary>
    [SupportedOSPlatform("ios16.0")]
    public void TestImageAnalysisInteractionWeakDelegateSinkSurvivesCollection()
    {
        try
        {
            var interaction = new VisionKit.ImageAnalysisInteraction();
            var impl = new RecordingImageAnalysisInteractionDelegate();

            AssignInteractionDelegate(interaction, impl);
            ForceGc();

            var readBack = interaction.Delegate;
            AssertNotNull(readBack, "a weak delegate sink must not read back nil after a collection.");

            readBack!.InteractionLiveTextButtonDidChangeToVisible(interaction, true);
            AssertEqual(1, impl.LiveTextButtonDidChangeCount,
                "dispatch after the collection must reach the same implementation the consumer holds.");
            AssertTrue(impl.LastVisible, "the parameter still round-trips after a collection.");

            GC.KeepAlive(impl);
            GC.KeepAlive(interaction);
        }
        catch (Exception ex)
        {
            LogExceptionChain(ex);
            throw;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [SupportedOSPlatform("ios16.0")]
    private static void AssignInteractionDelegate(
        VisionKit.ImageAnalysisInteraction interaction, VisionKit.IImageAnalysisInteractionDelegate impl)
    {
        interaction.Delegate = impl;
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

/// <summary>
/// Records every <c>DataScannerViewControllerDelegate</c> callback so a test can assert the call
/// arrived and its arguments survived the crossing. A consumer holds their delegate object for as
/// long as the scanner lives, and the tests do the same.
/// </summary>
[SupportedOSPlatform("ios16.0")]
internal sealed class RecordingDataScannerDelegate : VisionKit.IDataScannerViewControllerDelegate
{
    public int DidZoomCount { get; private set; }
    public int DidTapOnCount { get; private set; }
    public int DidAddCount { get; private set; }
    public int DidUpdateCount { get; private set; }
    public int DidRemoveCount { get; private set; }
    public int BecameUnavailableCount { get; private set; }

    public int TotalCallbacks =>
        DidZoomCount + DidTapOnCount + DidAddCount + DidUpdateCount + DidRemoveCount + BecameUnavailableCount;

    /// <summary>The receiver the most recent callback carried — compared by reference identity.</summary>
    public VisionKit.DataScannerViewController? LastScanner { get; private set; }

    /// <summary>
    /// Stored Swift state read off the receiver inside the callback. A freshly constructed peer
    /// over the same handle would still answer this correctly, but a peer constructed over a
    /// stale or wrong pointer would not — so it is read here rather than after the callback.
    /// </summary>
    public bool LastScannerRecognizesMultipleItems { get; private set; }

    public int LastItemsCount { get; private set; } = -1;
    public int LastAllItemsCount { get; private set; } = -1;
    public VisionKit.DataScannerViewController.ScanningUnavailable? LastError { get; private set; }

    public void DataScannerDidZoom(VisionKit.DataScannerViewController dataScanner)
    {
        DidZoomCount++;
        Record(dataScanner);
    }

    public void DataScannerDidTapOn(VisionKit.DataScannerViewController dataScanner, VisionKit.RecognizedItem item)
    {
        DidTapOnCount++;
        Record(dataScanner);
    }

    public void DataScannerDidAddAllItems(
        VisionKit.DataScannerViewController dataScanner,
        IEnumerable<VisionKit.RecognizedItem> addedItems,
        IEnumerable<VisionKit.RecognizedItem> allItems)
    {
        DidAddCount++;
        Record(dataScanner, addedItems, allItems);
    }

    public void DataScannerDidUpdateAllItems(
        VisionKit.DataScannerViewController dataScanner,
        IEnumerable<VisionKit.RecognizedItem> updatedItems,
        IEnumerable<VisionKit.RecognizedItem> allItems)
    {
        DidUpdateCount++;
        Record(dataScanner, updatedItems, allItems);
    }

    public void DataScannerDidRemoveAllItems(
        VisionKit.DataScannerViewController dataScanner,
        IEnumerable<VisionKit.RecognizedItem> removedItems,
        IEnumerable<VisionKit.RecognizedItem> allItems)
    {
        DidRemoveCount++;
        Record(dataScanner, removedItems, allItems);
    }

    public void DataScannerBecameUnavailableWithError(
        VisionKit.DataScannerViewController dataScanner,
        VisionKit.DataScannerViewController.ScanningUnavailable error)
    {
        BecameUnavailableCount++;
        LastError = error;
        Record(dataScanner);
    }

    private void Record(
        VisionKit.DataScannerViewController scanner,
        IEnumerable<VisionKit.RecognizedItem>? items = null,
        IEnumerable<VisionKit.RecognizedItem>? allItems = null)
    {
        LastScanner = scanner;
        LastScannerRecognizesMultipleItems = scanner.RecognizesMultipleItems;
        if (items is not null)
            LastItemsCount = items.Count();
        if (allItems is not null)
            LastAllItemsCount = allItems.Count();
    }
}

/// <summary>
/// Records the <c>ImageAnalysisInteractionDelegate</c> callbacks the simulator can drive. The
/// four requirements the generator marked non-dispatchable (CGPoint / CGRect / optional UIKit
/// returns) are implemented so the type is complete, but no test calls them: they are rejected on
/// a Swift-backed carrier for the same reason the DataScanner collection callbacks are.
/// </summary>
[SupportedOSPlatform("ios16.0")]
internal sealed class RecordingImageAnalysisInteractionDelegate : VisionKit.IImageAnalysisInteractionDelegate
{
    public int TextSelectionDidChangeCount { get; private set; }
    public int LiveTextButtonDidChangeCount { get; private set; }
    public int HighlightSelectedItemsDidChangeCount { get; private set; }

    public VisionKit.ImageAnalysisInteraction? LastInteraction { get; private set; }
    public bool LastVisible { get; private set; }
    public bool LastHighlightSelectedItems { get; private set; }

    [SupportedOSPlatform("ios17.0")]
    public void TextSelectionDidChange(VisionKit.ImageAnalysisInteraction interaction)
    {
        TextSelectionDidChangeCount++;
        LastInteraction = interaction;
    }

    // The four requirements below carry CoreGraphics or optional-UIKit types, which are not
    // dispatchable through a protocol-typed value; the generator emits them without a default
    // body, so a conformer must supply one. Nothing calls them — a Swift-backed carrier rejects
    // them the same way the DataScanner collection callbacks are rejected.
    public bool InteractionShouldBeginAtFor(
        VisionKit.ImageAnalysisInteraction interaction,
        Swift.CGPoint point,
        VisionKit.ImageAnalysisInteraction.InteractionTypes interactionType) => false;

    public Swift.CGRect ContentsRect(VisionKit.ImageAnalysisInteraction interaction) => default;

    public UIKit.UIView? ContentView(VisionKit.ImageAnalysisInteraction interaction) => null;

    public UIKit.UIViewController? PresentingViewController(VisionKit.ImageAnalysisInteraction interaction) => null;

    public void InteractionLiveTextButtonDidChangeToVisible(
        VisionKit.ImageAnalysisInteraction interaction, bool visible)
    {
        LiveTextButtonDidChangeCount++;
        LastInteraction = interaction;
        LastVisible = visible;
    }

    public void InteractionHighlightSelectedItemsDidChange(
        VisionKit.ImageAnalysisInteraction interaction, bool highlightSelectedItems)
    {
        HighlightSelectedItemsDidChangeCount++;
        LastInteraction = interaction;
        LastHighlightSelectedItems = highlightSelectedItems;
    }
}

#endif
