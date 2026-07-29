// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;
using SwiftBindingsTestLibDependency;
// Pin unqualified DependencyPoint/DependencyService to the dep-module originals.
// The cross-module emitter produces same-named partial-class wrappers in SwiftBindingsTestLib
// to host nested extension types; consumers dual-importing both modules
// disambiguate via using-aliases (mirrors the SwiftEventHandler using-alias pattern).
using DependencyPoint = SwiftBindingsTestLibDependency.DependencyPoint;
using DependencyService = SwiftBindingsTestLibDependency.DependencyService;

namespace RuntimeTestsApp.CrossModule;

/// <summary>
/// Tests for cross-module type references: types from SwiftBindingsTestLibDependency
/// used as parameters and return values in SwiftBindingsTestLib functions.
/// Also tests cross-module protocol conformance (LocalConformant).
/// </summary>
public class CrossModuleTests : TestBase
{
    public CrossModuleTests(TestResults results) : base(results) { }

    #region LocalConformant (Cross-Module Protocol Conformance)

    public void TestLocalConformantCreation()
    {
        using var lc = TestLibFunctions.MakeLocalConformant("test-id", 5);
        AssertEqual("test-id", lc.Identifier.ToString(), "Identifier preserved");
        AssertEqual(5, lc.Tag, "Tag preserved");
    }

    public void TestLocalConformantDescribe()
    {
        using var lc = TestLibFunctions.MakeLocalConformant("hello", 3);
        var desc = lc.GetDescribe();
        AssertTrue(desc.Contains("hello"), "Describe contains identifier");
        AssertTrue(desc.Contains("3"), "Describe contains tag");
    }

    #endregion

    #region Cross-Module Existential Box (conformance-descriptor / interface split)

    // A Swift type in module A (LocalConformant, SwiftBindingsTestLib) conforms to a protocol
    // in module B (DependencyProtocol, SwiftBindingsTestLibDependency). The generator must split
    // the conformance-DESCRIPTOR emission (needed for swift_getWitnessTable to box the value as
    // `any DependencyProtocol`) from the C# INTERFACE-stub emission (intentionally skipped for a
    // cross-module protocol with members — emitting `LocalConformant : IDependencyProtocol` would
    // trip CS0535 because the cross-module member bodies can't be provided). A single
    // ShouldEmitConformance gate previously dropped BOTH together, so the module-A conformer lost
    // its descriptor and could not be boxed into the existential at runtime (the real-world
    // AnchorEntity / Scene.AddAnchor(any HasAnchoring) refusal).

    public void TestLocalConformantBoxesAsCrossModuleExistential()
    {
        using var lc = TestLibFunctions.MakeLocalConformant("anchor", 7);

        // Interface stub correctly skipped; conformance descriptor still present.
        AssertFalse(lc is SwiftBindingsTestLibDependency.IDependencyProtocol,
            "LocalConformant must NOT implement the cross-module IDependencyProtocol interface (CS0535 stub skipped)");
        AssertTrue(lc is Swift.Runtime.IExistentialBoxable,
            "LocalConformant must remain IExistentialBoxable (conformance descriptor emitted despite skipped interface)");

        // C# constructs the `any DependencyProtocol` existential. This calls swift_getWitnessTable
        // with LocalConformant's cross-module conformance-descriptor symbol; pre-fix the symbol was
        // absent and this threw SwiftRuntimeException.
        var boxable = (Swift.Runtime.IExistentialBoxable)lc;
        var container = boxable.BoxAsExistential1<SwiftBindingsTestLibDependency.IDependencyProtocol>();

        AssertTrue(container[0] != System.IntPtr.Zero,
            "Witness table handle resolved (non-zero) for the cross-module conformance");
        AssertTrue(container.ObjectMetadata.Handle != System.IntPtr.Zero,
            "Existential carries LocalConformant's type metadata");
    }

    // Companion to the box test: drives Swift's describeAnyDependency(any DependencyProtocol),
    // which calls describe() on the existential. A C# implementation of the cross-module
    // IDependencyProtocol is auto-wrapped in DependencyProtocolProxy and passed in; Swift then
    // dispatches describe() back across the boundary into the C# impl. Exercises the cross-module
    // existential CONSUMPTION path and the describeAnyDependency entry point end-to-end.
    public void TestDescribeAnyDependencyDispatchesIntoCSharpConformer()
    {
        var impl = new CSharpDependencyConformer("cs-id", "tag9");
        var result = TestLibFunctions.DescribeAnyDependency(impl);
        AssertEqual("CS[tag9]: cs-id", result,
            "Swift dispatched describe() through the cross-module existential back into the C# impl");
    }

    private sealed class CSharpDependencyConformer : SwiftBindingsTestLibDependency.IDependencyProtocol
    {
        private readonly string _tag;
        public CSharpDependencyConformer(string identifier, string tag)
        {
            Identifier = identifier;
            _tag = tag;
        }
        public string Identifier { get; }
        public string GetDescribe() => $"CS[{_tag}]: {Identifier}";
    }

    // Subclass-only cross-module conformance (RealityKit AnchorEntity : Entity, HasAnchoring shape).
    // The base DependencyMarkedEntity (dependency module) conforms to DependencyBaseMarker, so it
    // emits its own IExistentialBoxable baked with Create<DependencyMarkedEntity, _>. AnchoredMarkedEntity
    // (main module) subclasses it and adds DependencyAnchorMarker — a protocol the base does NOT have.
    // Boxing the subclass as `any DependencyAnchorMarker` must dispatch the SUBCLASS's own
    // Create<AnchoredMarkedEntity, _> and resolve the subclass's conformance descriptor; the inherited
    // Create<DependencyMarkedEntity, _> would request the nonexistent DependencyMarkedEntity :
    // DependencyAnchorMarker witness and throw. Pre-fix the subclass's IExistentialBoxable was deduped
    // away (it inherited the base's), so boxing as the subclass-only protocol crashed at runtime.
    public void TestSubclassOnlyConformanceBoxesAsDerivedType()
    {
        using var entity = TestLibFunctions.MakeAnchoredMarkedEntity(7, "front");

        // The subclass keeps its own IExistentialBoxable (not collapsed to the base's).
        AssertTrue(entity is Swift.Runtime.IExistentialBoxable,
            "AnchoredMarkedEntity must remain IExistentialBoxable for its own subclass-only conformance");

        // Box as the protocol only the SUBCLASS conforms to. Dispatches Create<AnchoredMarkedEntity,_>;
        // swift_getWitnessTable resolves the subclass's DependencyAnchorMarker conformance descriptor.
        var boxable = (Swift.Runtime.IExistentialBoxable)entity;
        var container = boxable.BoxAsExistential1<SwiftBindingsTestLibDependency.IDependencyAnchorMarker>();

        AssertTrue(container[0] != System.IntPtr.Zero,
            "Witness table handle resolved (non-zero) for the subclass-only DependencyAnchorMarker conformance");
        AssertTrue(container.ObjectMetadata.Handle != System.IntPtr.Zero,
            "Existential carries AnchoredMarkedEntity's type metadata");
    }

    #endregion

    #region Cross-Module Type References (Part A)

    public void TestTransformDependencyPoint()
    {
        var point = new DependencyPoint(3.0, 4.0);
        AssertEqual(3.0, point.X, "Initial X");
        AssertEqual(4.0, point.Y, "Initial Y");

        var scaled = TestLibFunctions.TransformDependencyPoint(point, 2.0);
        AssertEqual(6.0, scaled.X, "Scaled X = 3.0 * 2.0");
        AssertEqual(8.0, scaled.Y, "Scaled Y = 4.0 * 2.0");
    }

    public void TestShiftDependencyPoint()
    {
        // Base-type-through-Bridge, end to end: a SwiftBindingsTestLib public API takes a
        // dependency Base type (DependencyPoint), dispatches the dependency's OWN instance method
        // (translated(dx:dy:)), and returns the dependency Base type. Exercises the cross-module
        // finalized struct layout in both directions plus a foreign method dispatch through the
        // bridge — the resolution the topological dependency-finalize order protects.
        var point = new DependencyPoint(3.0, 4.0);

        var shifted = TestLibFunctions.ShiftDependencyPoint(point, 1.5);
        AssertEqual(4.5, shifted.X, "Shifted X = 3.0 + 1.5");
        AssertEqual(5.5, shifted.Y, "Shifted Y = 4.0 + 1.5");

        // The returned foreign struct is fully usable: dispatch its OWN dependency-declared
        // instance method (translated(dx:dy:)) on the value that came back through the bridge.
        var again = shifted.Translated(0.5, -0.5);
        AssertEqual(5.0, again.X, "Foreign method dispatch on returned point: 4.5 + 0.5");
        AssertEqual(5.0, again.Y, "Foreign method dispatch on returned point: 5.5 - 0.5");
    }

    public void TestUpgradeDependencyConfig()
    {
        using var config = SwiftBindingsTestLibDependency.Functions.MakeDependencyConfig("TestLib", 1);
        AssertEqual("TestLib", config.Name, "Initial name");
        AssertEqual(1, config.Version, "Initial version");

        using var upgraded = TestLibFunctions.UpgradeDependencyConfig(config);
        AssertEqual("TestLib", upgraded.Name, "Name preserved after upgrade");
        AssertEqual(2, upgraded.Version, "Version incremented");
    }

    public void TestToggleDependencyService()
    {
        using var service = new DependencyService("MyService");
        AssertTrue(service.IsActive, "Initially active");

        var status = TestLibFunctions.ToggleDependencyService(service);
        AssertTrue(status.Contains("MyService"), "Status contains service name");
        AssertTrue(status.Contains("inactive"), "Status reflects toggled state");
        AssertEqual(false, service.IsActive, "Service toggled to inactive");
    }

    #endregion

    #region Cross-Module Property Type (Part B-1)

    public void TestAnnotatedLocationCreation()
    {
        using var loc = TestLibFunctions.MakeAnnotatedLocation("Origin", 0.0, 0.0);
        AssertEqual("Origin", loc.Label, "Label preserved");
        AssertEqual(0.0, loc.Point.X, "Point X preserved");
        AssertEqual(0.0, loc.Point.Y, "Point Y preserved");
    }

    public void TestAnnotatedLocationPointProperty()
    {
        using var loc = TestLibFunctions.MakeAnnotatedLocation("TestPoint", 5.0, 10.0);
        var point = loc.Point;
        AssertEqual(5.0, point.X, "Property getter returns correct X");
        AssertEqual(10.0, point.Y, "Property getter returns correct Y");
    }

    public void TestGetLocationPointRoundTrip()
    {
        using var loc = TestLibFunctions.MakeAnnotatedLocation("RoundTrip", 7.5, 2.5);
        var point = TestLibFunctions.GetLocationPoint(loc);
        AssertEqual(7.5, point.X, "Round-trip X through cross-module function");
        AssertEqual(2.5, point.Y, "Round-trip Y through cross-module function");
    }

    #endregion

    #region Cross-Module Collection (Part B-2)

    public void TestSumDependencyPoints()
    {
        var points = new[]
        {
            new DependencyPoint(1.0, 2.0),
            new DependencyPoint(3.0, 4.0),
            new DependencyPoint(5.0, 6.0)
        };

        var sum = TestLibFunctions.SumDependencyPoints(points);
        AssertEqual(9.0, sum.X, "Sum X = 1+3+5");
        AssertEqual(12.0, sum.Y, "Sum Y = 2+4+6");
    }

    public void TestMakeDependencyPointGrid()
    {
        var grid = TestLibFunctions.MakeDependencyPointGrid(2, 3);
        AssertEqual(6, grid.Count, "2x3 grid = 6 points");

        // First row: (0,0), (1,0), (2,0)
        AssertEqual(0.0, grid[0].X, "Grid[0] X");
        AssertEqual(0.0, grid[0].Y, "Grid[0] Y");
        AssertEqual(1.0, grid[1].X, "Grid[1] X");
        AssertEqual(0.0, grid[1].Y, "Grid[1] Y");
        AssertEqual(2.0, grid[2].X, "Grid[2] X");
        AssertEqual(0.0, grid[2].Y, "Grid[2] Y");

        // Second row: (0,1), (1,1), (2,1)
        AssertEqual(0.0, grid[3].X, "Grid[3] X");
        AssertEqual(1.0, grid[3].Y, "Grid[3] Y");
        AssertEqual(1.0, grid[4].X, "Grid[4] X");
        AssertEqual(1.0, grid[4].Y, "Grid[4] Y");
        AssertEqual(2.0, grid[5].X, "Grid[5] X");
        AssertEqual(1.0, grid[5].Y, "Grid[5] Y");
    }

    public void TestSumEmptyCollection()
    {
        var empty = Array.Empty<DependencyPoint>();
        var sum = TestLibFunctions.SumDependencyPoints(empty);
        AssertEqual(0.0, sum.X, "Empty sum X = 0");
        AssertEqual(0.0, sum.Y, "Empty sum Y = 0");
    }

    #endregion

    #region Cross-Module Enum Usage (Part B-3)

    public void TestPromoteDependencyStatus()
    {
        var promoted = TestLibFunctions.PromoteDependencyStatus(DependencyStatus.Unknown);
        AssertEqual(DependencyStatus.Pending, promoted, "Unknown promotes to Pending");

        promoted = TestLibFunctions.PromoteDependencyStatus(DependencyStatus.Pending);
        AssertEqual(DependencyStatus.Active, promoted, "Pending promotes to Active");

        promoted = TestLibFunctions.PromoteDependencyStatus(DependencyStatus.Active);
        AssertEqual(DependencyStatus.Active, promoted, "Active stays Active");

        promoted = TestLibFunctions.PromoteDependencyStatus(DependencyStatus.Inactive);
        AssertEqual(DependencyStatus.Pending, promoted, "Inactive promotes to Pending");
    }

    public void TestDescribeDependencyStatus()
    {
        var desc = TestLibFunctions.DescribeDependencyStatus(DependencyStatus.Active);
        AssertTrue(desc.Contains("Active"), "Description contains Active label");
    }

    public void TestDependencyStatusEnumValues()
    {
        AssertEqual(0, (int)DependencyStatus.Unknown, "Unknown = 0");
        AssertEqual(1, (int)DependencyStatus.Pending, "Pending = 1");
        AssertEqual(2, (int)DependencyStatus.Active, "Active = 2");
        AssertEqual(3, (int)DependencyStatus.Inactive, "Inactive = 3");
    }

    #endregion

    #region Cross-Module Closure (Part B-4)

    public void TestApplyToDependencyPoint()
    {
        double capturedX = 0;
        double capturedY = 0;

        TestLibFunctions.ApplyToDependencyPoint(3.0, 7.0, point =>
        {
            capturedX = point.X;
            capturedY = point.Y;
        });

        AssertEqual(3.0, capturedX, "Closure received correct X");
        AssertEqual(7.0, capturedY, "Closure received correct Y");
    }

    public void TestMapDependencyPoint()
    {
        var original = new DependencyPoint(2.0, 3.0);

        var doubled = TestLibFunctions.MapDependencyPoint(original, p =>
            new DependencyPoint(p.X * 2, p.Y * 2));

        AssertEqual(4.0, doubled.X, "Mapped X = 2*2");
        AssertEqual(6.0, doubled.Y, "Mapped Y = 3*2");
    }

    #endregion

    #region Cross-Module Extension (Part B-5)

    public void TestScaleDependencyPoint()
    {
        var point = new DependencyPoint(3.0, 4.0);

        var scaled = TestLibFunctions.ScaleDependencyPoint(point, 3.0);
        AssertEqual(9.0, scaled.X, "Scaled X = 3*3");
        AssertEqual(12.0, scaled.Y, "Scaled Y = 4*3");
    }

    public void TestScaleDependencyPointByZero()
    {
        var point = new DependencyPoint(5.0, 10.0);

        var scaled = TestLibFunctions.ScaleDependencyPoint(point, 0.0);
        AssertEqual(0.0, scaled.X, "Scaled by 0 gives X=0");
        AssertEqual(0.0, scaled.Y, "Scaled by 0 gives Y=0");
    }

    #endregion

    #region Cross-Module Class Extension (payment-SDK PaymentApiClient shape)

    public void TestDependencyServiceTaggedActivation()
    {
        using var active = new DependencyService("Worker");
        AssertEqual(7, active.TaggedActivation(7),
            "Cross-module class extension method routes through SwiftSelf register (active receiver)");

        using var idle = new DependencyService("Worker", false);
        AssertEqual(-42, idle.TaggedActivation(42),
            "Cross-module class extension reads receiver state under CallConvSwift (inactive receiver)");
    }

    public void TestDependencyServiceActivateAndReport()
    {
        using var service = new DependencyService("Worker", false);
        AssertEqual(true, service.ActivateAndReport(),
            "Cross-module class extension can mutate receiver state and report via CallConvSwift primitive return");
    }

    public void TestDependencyServiceComputeWithCompletion()
    {
        using var active = new DependencyService("Worker", true);
        int activeCaught = 0;
        active.ComputeWithCompletion(7, v => activeCaught = v);
        AssertEqual(14, activeCaught,
            "Cross-module class-extension closure parameter dispatches via @_cdecl trampoline (active receiver, value * 2)");

        using var idle = new DependencyService("Worker", false);
        int idleCaught = 0;
        idle.ComputeWithCompletion(11, v => idleCaught = v);
        AssertEqual(-11, idleCaught,
            "Cross-module class-extension closure parameter dispatches via @_cdecl trampoline (inactive receiver, -value)");
    }

    public void TestDependencyServiceProduceTokenSuccessPath()
    {
        using var active = new DependencyService("Worker", true);
        SwiftBindingsTestLib.DependencyToken? caughtToken = null;
        Foundation.NSError? caughtError = null;
        bool fired = false;

        active.ProduceToken(true, (token, error) =>
        {
            caughtToken = token;
            caughtError = error;
            fired = true;
        });

        AssertTrue(fired, "Completion fired");
        AssertTrue(caughtToken != null, "Success path delivered a non-null DependencyToken");
        AssertTrue(caughtError == null, "Success path delivered a null error");
        AssertEqual(42, caughtToken!.Value, "Optional<class> closure arg round-trips DependencyToken.value");
        caughtToken.Dispose();
    }

    public void TestDependencyServiceProduceTokenFailurePath()
    {
        using var idle = new DependencyService("Worker", false);
        SwiftBindingsTestLib.DependencyToken? caughtToken = null;
        Foundation.NSError? caughtError = null;
        bool fired = false;

        idle.ProduceToken(false, (token, error) =>
        {
            caughtToken = token;
            caughtError = error;
            fired = true;
        });

        AssertTrue(fired, "Completion fired");
        AssertTrue(caughtToken == null, "Failure path delivered a null DependencyToken");
        AssertTrue(caughtError != null, "Failure path delivered a non-null NSError");
        AssertEqual("DependencyService", caughtError!.Domain, "Optional<any Error> closure arg bridges NSError.domain");
        AssertEqual(2, (int)caughtError.Code, "Optional<any Error> closure arg bridges NSError.code");
    }

    public void TestDependencyServiceComputeAsyncSuccess()
    {
        using var active = new DependencyService("Worker", true);
        var result = active.ComputeAsync(7).GetAwaiter().GetResult();
        AssertEqual(21, result,
            "async throws cross-module class extension delivers result via TaskCompletionSource (active receiver, value * 3)");
    }

    public void TestDependencyServiceComputeAsyncFailure()
    {
        using var idle = new DependencyService("Worker", false);
        var task = idle.ComputeAsync(7);
        try
        {
            _ = task.GetAwaiter().GetResult();
            AssertTrue(false, "ComputeAsync on inactive receiver should throw");
        }
        catch (global::Swift.Runtime.SwiftException ex)
        {
            AssertTrue(ex.Message.Contains("inactive"),
                $"SwiftException carries the Swift NSError.localizedDescription (got: {ex.Message})");
        }
    }

    public void TestDependencyServiceComputeAfterDelayCompletes()
    {
        using var active = new DependencyService("Worker", true);
        // 50ms delay, no cancellation — the default CancellationToken overload must
        // behave exactly like the pre-cancellation-support binding.
        var result = active.ComputeAfterDelayAsync(9, 50_000_000).GetAwaiter().GetResult();
        AssertEqual(9, result,
            "async throws extension with a delay completes normally when the token is never cancelled");
    }

    public void TestDependencyServiceComputeAfterDelayPreCancelled()
    {
        using var active = new DependencyService("Worker", true);
        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();
        var task = active.ComputeAfterDelayAsync(9, 30_000_000_000, cts.Token);
        AssertTrue(task.IsCanceled,
            "pre-cancelled token short-circuits to an already-canceled Task without invoking Swift");
        try
        {
            _ = task.GetAwaiter().GetResult();
            AssertTrue(false, "awaiting a pre-cancelled call should throw OperationCanceledException");
        }
        catch (System.OperationCanceledException ex)
        {
            AssertEqual(cts.Token, ex.CancellationToken,
                "pre-cancel OperationCanceledException carries the caller's token");
        }
    }

    public void TestDependencyServiceComputeAfterDelayCancelInFlight()
    {
        using var active = new DependencyService("Worker", true);
        using var cts = new System.Threading.CancellationTokenSource();
        // 30s deadline: only Swift-side task cancellation (CancellationError from
        // Task.sleep) can finish this task quickly. Cancel after launch and require
        // a canceled — not faulted — Task well before the deadline.
        var task = active.ComputeAfterDelayAsync(9, 30_000_000_000, cts.Token);
        AssertTrue(!task.IsCompleted, "in-flight call is still pending before cancellation");
        cts.Cancel();
        var finished = System.Threading.Tasks.Task.WhenAny(
            task, System.Threading.Tasks.Task.Delay(10_000)).GetAwaiter().GetResult();
        AssertTrue(ReferenceEquals(finished, task),
            "cancelled in-flight call completes long before the 30s Swift-side deadline");
        AssertTrue(task.IsCanceled,
            "Swift CancellationError surfaces as a canceled (not faulted) Task");
        try
        {
            _ = task.GetAwaiter().GetResult();
            AssertTrue(false, "awaiting a cancelled in-flight call should throw OperationCanceledException");
        }
        catch (System.OperationCanceledException ex)
        {
            AssertEqual(cts.Token, ex.CancellationToken,
                "in-flight cancellation OperationCanceledException carries the caller's token");
        }
    }

    public void TestDependencyServiceMakeWithSeed()
    {
        using var positive = DependencyServiceSwiftBindingsTestLibExtensions.MakeWithSeed(7);
        AssertEqual("seed-7", positive.Name.ToString(),
            "Static class func on a class receiver returns a DependencyService with expected Name (positive seed)");
        AssertTrue(positive.IsActive,
            "Static class func wires the seed sign into IsActive (positive seed -> active)");

        using var negative = DependencyServiceSwiftBindingsTestLibExtensions.MakeWithSeed(-3);
        AssertEqual("seed--3", negative.Name.ToString(),
            "Static class func returns DependencyService with expected Name (negative seed)");
        AssertTrue(!negative.IsActive,
            "Static class func wires the seed sign into IsActive (negative seed -> inactive)");
    }

    public void TestDependencyServiceMakeWithLabel()
    {
        using var made = DependencyServiceSwiftBindingsTestLibExtensions.MakeWithLabel("payment-merchant.id");
        AssertEqual("payment-merchant.id", made.Name.ToString(),
            "Static class func with Swift.String param round-trips the label through the wrapper trampoline");
        AssertTrue(made.IsActive,
            "Static class func with String param produces an active DependencyService");

        using var unicode = DependencyServiceSwiftBindingsTestLibExtensions.MakeWithLabel("café-🍵");
        AssertEqual("café-🍵", unicode.Name.ToString(),
            "Static class func with String param round-trips multi-byte UTF-8 (combining diacritics + emoji)");
    }

    public void TestDependencyServiceNotifyLabel()
    {
        using var active = new DependencyService("svc", true);
        int caught = -1;
        bool fired = false;
        active.NotifyLabel("hello", value =>
        {
            caught = value;
            fired = true;
        });
        AssertTrue(fired, "Closure completion fired for active receiver");
        AssertEqual(6, caught,
            "Closure-bearing instance method with String param delivers (label.count + isActive ? 1 : 0) = 5 + 1");

        using var idle = new DependencyService("svc", false);
        int caughtIdle = -1;
        idle.NotifyLabel("world!", value => caughtIdle = value);
        AssertEqual(6, caughtIdle,
            "Closure-bearing instance method with String param delivers (label.count + isActive ? 1 : 0) = 6 + 0");
    }

    #endregion

    #region Cross-Module Struct Extension (frozen-struct receiver via @_cdecl trampoline)

    public void TestDependencyPointScaledExtension()
    {
        var point = new DependencyPoint(3.0, 4.0);

        var scaled = point.Scaled(2.5);
        AssertEqual(7.5, scaled.X, "Cross-module struct extension method returns frozen struct (X)");
        AssertEqual(10.0, scaled.Y, "Cross-module struct extension method returns frozen struct (Y)");
    }

    public void TestDependencyPointManhattanDistanceExtension()
    {
        var point = new DependencyPoint(-3.0, 4.0);

        AssertEqual(7.0, point.GetManhattanDistance(),
            "Cross-module struct extension property routes |x|+|y| through @_cdecl trampoline");
    }

    public void TestDependencyPointClassifyExtension_SimpleEnumLowering()
    {
        // Cross-module struct-extension method whose param AND return are a
        // SimpleEnum (DependencyStatus). Locks in the @_cdecl SimpleEnum
        // lowering on the struct-receiver trampoline path: (int)status crosses
        // the boundary as a raw scalar, the Swift trampoline reconstructs the
        // enum via DependencyStatus(rawValue:)!, and surfaces .rawValue on return.
        var origin = new DependencyPoint(0.0, 0.0);
        AssertEqual(SwiftBindingsTestLibDependency.DependencyStatus.Unknown,
            origin.Classify(SwiftBindingsTestLibDependency.DependencyStatus.Unknown),
            "Unknown @ origin -> Unknown (raw-value round-trip through @_cdecl)");

        var right = new DependencyPoint(1.0, 0.0);
        AssertEqual(SwiftBindingsTestLibDependency.DependencyStatus.Pending,
            right.Classify(SwiftBindingsTestLibDependency.DependencyStatus.Unknown),
            "Unknown @ x>0 -> Pending (receiver state observed under @_cdecl)");

        AssertEqual(SwiftBindingsTestLibDependency.DependencyStatus.Active,
            right.Classify(SwiftBindingsTestLibDependency.DependencyStatus.Pending),
            "Pending -> Active (raw-int round-trip)");

        AssertEqual(SwiftBindingsTestLibDependency.DependencyStatus.Pending,
            right.Classify(SwiftBindingsTestLibDependency.DependencyStatus.Inactive),
            "Inactive -> Pending (raw-int round-trip)");
    }

    #endregion

    #region Bug (e) sub-case e-1: Unlabeled Parameters on a Cross-Module Struct Extension

    public void TestDependencyPointOffsetByExtension_UnlabeledParameters()
    {
        // offsetBy(_ dx: Double, _ dy: Double) — two UNLABELED parameters. Pre-fix, the
        // struct-receiver trampoline reconstructed the Swift call-site label straight from
        // ArgumentDecl.Name, which for an unlabeled parameter can be an auto-generated
        // placeholder ("arg0") rather than the literal "_" sentinel it checked for —
        // emitting a spurious external label the original declaration never had, which
        // swiftc rejected at the trampoline's internal call site. The fix routes label
        // reconstruction through CdeclParamMapper.BuildSwiftCallArgLabel.
        var point = new DependencyPoint(3.0, 4.0);
        var moved = point.OffsetBy(1.0, -2.0);
        AssertEqual(4.0, moved.X, "offsetBy(_:_:) adds dx to x through the unlabeled-param trampoline");
        AssertEqual(2.0, moved.Y, "offsetBy(_:_:) adds dy to y through the unlabeled-param trampoline");
    }

    #endregion

    #region Cross-Module Synthetic-Name Collision (user `self_` vs injected receiver pointer)

    // The cross-module extension trampolines inject the receiver pointer as `self_`. A user
    // parameter also named `self_` would declare `self_` twice and the wrapper would be SILENTLY
    // dropped — leaving a missing entry point that crashes at call time. These pin the escape on
    // the struct-receiver, class-receiver, and async/throws-trampoline cross-module paths.

    public void TestDependencyPointOffsetSelfCollision()
    {
        // Struct-receiver extension returning a frozen struct: trampoline injects both the
        // receiver pointer (`self_`) and the indirect `__resultPtr`; the user `self_` escapes.
        var point = new DependencyPoint(3.0, 4.0);
        var moved = point.Offset(2.0);
        AssertEqual(5.0, moved.X, "offset(self_:) adds self_ to x through the escaped binding");
        AssertEqual(6.0, moved.Y, "offset(self_:) adds self_ to y through the escaped binding");
    }

    public void TestDependencyServiceTagWithSelfCollision()
    {
        // CONTROL (not a trampoline repro): a plain primitive sync class-extension method routes
        // through a direct CallConvSwift import of the Swift symbol, not the @_cdecl trampoline —
        // `self_` is just an ordinary Swift parameter and there is no injected-receiver collision.
        // The escape on the SYNC closure trampoline is exercised by ReportWithSelf below.
        using var active = new DependencyService("Worker", true);
        AssertEqual(9, active.TagWithSelf(9), "tagWithSelf(self_:) returns self_ when active");

        using var idle = new DependencyService("Worker", false);
        AssertEqual(-9, idle.TagWithSelf(9), "tagWithSelf(self_:) negates self_ when inactive");
    }

    public void TestDependencyServiceReportWithSelfCollision()
    {
        // SYNC closure trampoline (EmitSwiftClosureTrampoline): the closure parameter forces the
        // class @_cdecl trampoline, which injects `self_` for the receiver pointer. The user
        // `self_` must escape the injected receiver binding — otherwise `self_` is declared twice
        // and the trampoline is silently dropped. The completion firing at all proves the escaped
        // binding forwarded the user value into the synchronously-invoked block.
        using var active = new DependencyService("Worker", true);
        int? activeCaught = null;
        active.ReportWithSelf(9, v => activeCaught = v);
        AssertEqual(9, activeCaught, "reportWithSelf(self_:completion:) forwards self_ when active");

        using var idle = new DependencyService("Worker", false);
        int? idleCaught = null;
        idle.ReportWithSelf(9, v => idleCaught = v);
        AssertEqual(-9, idleCaught, "reportWithSelf(self_:completion:) negates self_ when inactive");
    }

    public void TestDependencyServiceComputeAsyncWithSelfCollision()
    {
        // Async/throws trampoline injects completionFn/completionCtx/self_; the user `self_` escapes.
        using var active = new DependencyService("Worker", true);
        var result = active.ComputeAsyncWithSelfAsync(7).GetAwaiter().GetResult();
        AssertEqual(14, result, "computeAsyncWithSelf(self_:) doubles self_ when active");

        using var idle = new DependencyService("Worker", false);
        var idleResult = idle.ComputeAsyncWithSelfAsync(7).GetAwaiter().GetResult();
        AssertEqual(-7, idleResult, "computeAsyncWithSelf(self_:) negates self_ when inactive");
    }

    #endregion

    #region Cross-Module Existentials in Container Positions

    // A sibling module's protocol used as an existential inside a CONTAINER — an enum-case
    // associated value, a bound-generic (array) argument, a closure parameter — reaches the C#
    // projection through a different oracle than the plain parameter position does. Each one must
    // still name the interface and the proxy class with the owning module's namespace, since the
    // generated file carries no `using` for a sibling binding. These tests drive each container end
    // to end: C# hands in an implementation of the cross-module interface, Swift stores/reads it
    // through the container and dispatches `describe()` back into the C# object.

    public void TestCrossModuleExistentialEnumSinglePayloadDispatches()
    {
        var impl = new CSharpDependencyConformer("single-id", "s1");
        using var payload = CrossModuleExistentialPayload.Single(impl);

        AssertEqual(CrossModuleExistentialPayload.CaseTag.Single, payload.Tag,
            "Enum built from a cross-module existential reports the 'single' case");
        AssertEqual("single:CS[s1]: single-id", payload.GetDescribePayload(),
            "Swift read the enum's existential payload and dispatched describe() into the C# impl");
    }

    public void TestCrossModuleExistentialEnumSinglePayloadReadsBack()
    {
        var impl = new CSharpDependencyConformer("read-back", "r1");
        using var payload = CrossModuleExistentialPayload.Single(impl);

        AssertTrue(payload.TryGetSingle(out var value), "TryGetSingle succeeds on the 'single' case");
        AssertEqual("CS[r1]: read-back", value!.GetDescribe(),
            "Payload read back as the cross-module interface dispatches into the original C# impl");
    }

    public void TestCrossModuleExistentialEnumArrayPayloadDispatches()
    {
        var first = new CSharpDependencyConformer("a", "t1");
        var second = new CSharpDependencyConformer("b", "t2");
        using var payload = CrossModuleExistentialPayload.Several(new[] { (IDependencyProtocol)first, second });

        AssertEqual(CrossModuleExistentialPayload.CaseTag.Several, payload.Tag,
            "Enum built from an array of cross-module existentials reports the 'several' case");
        AssertEqual("several:2", payload.GetDescribePayload(),
            "Swift sees both existentials in the array payload");

        AssertTrue(payload.TryGetSeveral(out var values), "TryGetSeveral succeeds on the 'several' case");
        AssertEqual(2, values!.Count, "Array payload reads back with both elements");
        AssertEqual("CS[t2]: b", values[1].GetDescribe(),
            "Array element read back as the cross-module interface dispatches into the C# impl");
    }

    public void TestCrossModuleExistentialEnumLabeledPayloadDispatches()
    {
        var impl = new CSharpDependencyConformer("tagged", "L");
        using var payload = CrossModuleExistentialPayload.Labeled(42, impl);

        AssertEqual("labeled:42:CS[L]: tagged", payload.GetDescribePayload(),
            "Multi-payload case carries both the scalar label and the cross-module existential");

        AssertTrue(payload.TryGetLabeled(out var tag, out var value), "TryGetLabeled succeeds on the 'labeled' case");
        AssertEqual(42, tag, "Scalar label reads back");
        AssertEqual("CS[L]: tagged", value!.GetDescribe(),
            "Labelled existential reads back and dispatches into the C# impl");
    }

    public void TestCrossModuleExistentialArrayParameter()
    {
        var first = new CSharpDependencyConformer("x", "p");
        var second = new CSharpDependencyConformer("y", "q");

        var joined = TestLibFunctions.DescribeDependencyList(new[] { (IDependencyProtocol)first, second });

        AssertEqual("CS[p]: x|CS[q]: y", joined,
            "Swift walked an array of cross-module existentials, dispatching into each C# impl");
    }

    public void TestCrossModuleExistentialClosureParameter()
    {
        var impl = new CSharpDependencyConformer("closure-id", "c1");
        string? seen = null;

        TestLibFunctions.ApplyToDependency(impl, dep => seen = dep.GetDescribe());

        AssertEqual("CS[c1]: closure-id", seen,
            "Closure parameter typed as a cross-module existential arrived projected and dispatchable");
    }

    public void TestCrossModuleExistentialScalarIndexer()
    {
        var first = new CSharpDependencyConformer("x", "p");
        var second = new CSharpDependencyConformer("y", "q");
        using var registry = new DependencyRegistry(new[] { (IDependencyProtocol)first, second });

        AssertEqual(2, registry.Count, "Registry stored both cross-module existentials");
        AssertEqual("CS[q]: y", registry[1].GetDescribe(),
            "Indexer returned the cross-module existential projected and dispatchable back into C#");
    }

    public void TestCrossModuleExistentialContainerIndexer()
    {
        var first = new CSharpDependencyConformer("x", "p");
        var second = new CSharpDependencyConformer("y", "q");
        using var registry = new DependencyRegistry(new[] { (IDependencyProtocol)first, second });

        var matched = registry["CS[q]"];

        AssertEqual(1, matched.Count, "Container-returning indexer filtered to the one matching entry");
        AssertEqual("CS[q]: y", matched[0].GetDescribe(),
            "Element of the returned container dispatches into the original C# impl");
    }

    public void TestCrossModuleExistentialContainerPropertyRoundTrips()
    {
        var first = new CSharpDependencyConformer("x", "p");
        var second = new CSharpDependencyConformer("y", "q");
        using var registry = new DependencyRegistry(new[] { (IDependencyProtocol)first });

        registry.Pinned = new[] { (IDependencyProtocol)second, first };

        var readBack = registry.Pinned;
        AssertEqual(2, readBack.Count, "Container property of cross-module existentials round-tripped both elements");
        AssertEqual("CS[q]: y", readBack[0].GetDescribe(),
            "First element read back through the getter dispatches into the C# impl");
        AssertEqual("CS[p]: x", readBack[1].GetDescribe(),
            "Second element read back through the getter dispatches into the C# impl");
    }

    public void TestCrossModuleExistentialProtocolInterfaceMembers()
    {
        // The members here are declared on a protocol, so the emitted signatures come from the
        // interface declaration — a different emission path than the concrete-type members above.
        // Calling them through the interface proves the qualified element type is the one the
        // marshalling actually uses, not just a name that happened to compile.
        var first = new CSharpDependencyConformer("x", "p");
        var second = new CSharpDependencyConformer("y", "q");
        using var aggregator = new DependencyAggregator(new[] { (IDependencyProtocol)first });

        IDependencyAggregating asInterface = aggregator;

        AssertEqual(1, asInterface.Dependencies.Count,
            "Interface-declared container property returned the stored cross-module existential");
        AssertEqual("CS[p]: x", asInterface.Dependencies[0].GetDescribe(),
            "Element of the interface-declared container dispatches into the C# impl");
        AssertEqual("CS[p]: x", asInterface.GetPrimary().GetDescribe(),
            "Interface-declared scalar existential return dispatches into the C# impl");
        AssertEqual("CS[p]: x+CS[q]: y", asInterface.Merge(new[] { (IDependencyProtocol)second }),
            "Interface-declared container parameter carried the cross-module existential into Swift");
    }

    // The members below are reached through a SECOND emitter that writes a convenience overload
    // beside the primary signature — a Task-returning form of a completion-handler method, a
    // machine-width `int` form of an `nint` method, a simplified `Func` form of a throwing-closure
    // method, and the callback body of an async container return. Each builds its own projection,
    // so each can drop the owning module's namespace independently of the primary. Calling the
    // overload (rather than only compiling it) proves the qualified interface is the type the
    // marshalling actually uses; the Task overload additionally has to EXIST — a disagreement
    // between its result type and the callback's projected type drops it silently, so the call
    // itself is the assertion.

    public void TestCrossModuleExistentialCompletionCallback()
    {
        var stored = new CSharpDependencyConformer("stored", "s");
        var fallback = new CSharpDependencyConformer("fallback", "f");
        using var loader = new DependencyLoader(new[] { (IDependencyProtocol)stored });

        string? seen = null;
        loader.LoadDependency(1, fallback, dep => seen = dep.GetDescribe());

        AssertEqual("CS[s]: stored", seen,
            "Completion callback delivered the cross-module existential projected and dispatchable");
    }

    public void TestCrossModuleExistentialCompletionTaskOverload()
    {
        var stored = new CSharpDependencyConformer("stored", "s");
        var fallback = new CSharpDependencyConformer("fallback", "f");
        using var loader = new DependencyLoader(new[] { (IDependencyProtocol)stored });

        var result = loader.LoadDependencyAsync(1, fallback).GetAwaiter().GetResult();

        AssertEqual("CS[s]: stored", result.GetDescribe(),
            "Task-returning convenience overload was emitted and its existential result dispatches into C#");
    }

    public void TestCrossModuleExistentialMachineWidthOverload()
    {
        var stored = new CSharpDependencyConformer("stored", "s");
        var fallback = new CSharpDependencyConformer("fallback", "f");
        using var loader = new DependencyLoader(new[] { (IDependencyProtocol)stored });

        AssertEqual("CS[s]: stored", loader.DescribeAt(0, fallback),
            "int-taking convenience overload forwarded the cross-module existential to the nint primary");
        AssertEqual("CS[f]: fallback", loader.DescribeAt((nint)5, fallback),
            "Out-of-range index falls back to the existential argument, dispatching back into C#");
    }

    public void TestCrossModuleExistentialThrowingClosureOverload()
    {
        var fallback = new CSharpDependencyConformer("abc", "f");
        using var loader = new DependencyLoader(new[] { (IDependencyProtocol)fallback });

        // "CS[f]: abc" is ten characters; the simplified overload's closure adds one.
        AssertEqual(11, loader.TransformDependency(fallback, v => v + 1),
            "Simplified Func-taking overload projected its sibling cross-module existential parameter");
    }

    public void TestCrossModuleExistentialAsyncContainerReturn()
    {
        var first = new CSharpDependencyConformer("x", "p");
        var second = new CSharpDependencyConformer("y", "q");
        using var loader = new DependencyLoader(new[] { (IDependencyProtocol)first, second });

        var fetched = loader.FetchDependenciesAsync().GetAwaiter().GetResult();

        AssertEqual(2, fetched.Count, "Async container return delivered both cross-module existentials");
        AssertEqual("CS[q]: y", fetched[1].GetDescribe(),
            "Element converted inside the async completion callback dispatches into the C# impl");
    }

    #endregion
}
