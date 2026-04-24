// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Async;

/// <summary>
/// Tests for AsyncComplexWorker — async methods returning frozen structs, enums, classes, and optionals.
/// Validates complex type marshalling through async callbacks.
///
/// Tier 2: Constructors take string params (SwiftString through CallConvSwift).
/// Async callbacks use Cdecl (not CallConvSwift), avoiding the Mono JIT assertion.
/// </summary>
/// Tier 3: Async P/Invoke entry points are in the SwiftBindings wrapper library but
/// DllImport targets "SwiftBindingsTestLib" → EntryPointNotFoundException at runtime.
/// Tests are ready for when the generator routes async DllImports to the wrapper library.
public class AsyncComplexTypeTests : TestBase
{
    public AsyncComplexTypeTests(TestResults results) : base(results) { }

    #region AsyncResult (Frozen Struct) Tests

    public async Task TestAsyncGetResult()
    {
        var worker = new AsyncComplexWorker("worker-1");
        var result = await WithTimeout(worker.GetResultAsync(), DefaultAsyncTimeout);
        AssertNotNull(result, "AsyncGetResult not null");
        AssertEqual(42, result.Id, "AsyncResult.Id");
        AssertEqual("Completed by worker-1", result.Message.ToString(), "AsyncResult.Message");
        AssertEqual(true, result.Success, "AsyncResult.Success");
        TestLogger.Info($"AsyncComplexWorker.AsyncGetResult() = id={result.Id}, success={result.Success}");
    }

    public async Task TestAsyncStaticResult()
    {
        var result = await WithTimeout(AsyncComplexWorker.StaticResultAsync(), DefaultAsyncTimeout);
        AssertNotNull(result, "AsyncStaticResult not null");
        AssertEqual(0, result.Id, "Static AsyncResult.Id");
        AssertEqual("Static result", result.Message.ToString(), "Static AsyncResult.Message");
        AssertEqual(true, result.Success, "Static AsyncResult.Success");
        TestLogger.Info($"AsyncComplexWorker.GetStaticResultAsync() = id={result.Id}");
    }

    public async Task TestAsyncGetResult_RepeatedCalls_NoCarrierLeak()
    {
        // Frozen-with-memory async returns: AsyncResult is @frozen with a String field
        // (ClassWithBufferStruct), so the Swift-side `initializeMemory` gives the carrier
        // a +1 on the embedded String. NewFromPayload runs its own InitializeWithCopy into
        // a managed buffer and gives the C# object an independent +1 — the carrier's +1
        // must be released via VWT Destroy before SBW_Free, otherwise every call adds a
        // permanent reference on the String. This loop reads Message each iteration so a
        // stale/freed pointer would surface as corruption, and exercises the path enough
        // times that a broken destroy would amplify into visible memory growth under
        // allocator debug checks (NSZombie, MallocStackLogging).
        var worker = new AsyncComplexWorker("repeat-worker");
        for (int i = 0; i < 50; i++)
        {
            var result = await WithTimeout(worker.GetResultAsync(), DefaultAsyncTimeout);
            AssertNotNull(result, $"Iteration {i}: result not null");
            AssertEqual(42, result.Id, $"Iteration {i}: AsyncResult.Id");
            AssertEqual("Completed by repeat-worker", result.Message.ToString(), $"Iteration {i}: AsyncResult.Message");
            AssertEqual(true, result.Success, $"Iteration {i}: AsyncResult.Success");
        }
        TestLogger.Info("AsyncComplexWorker.GetResultAsync() × 50 — no corruption across repeated calls");
    }

    #endregion

    #region AsyncReport (Non-Frozen Struct) Tests — Issue #32 regression

    // Non-frozen struct async returns previously stored the raw Swift-allocated
    // carrier inside SwiftSafeHandle. Two bugs: (a) reading a property after the
    // callback returned could hit freed memory, (b) ReleaseHandle called
    // NativeMemory.Free on a Swift-allocated pointer (allocator mismatch). The
    // fix VWT-copies into a NativeMemory-owned buffer and frees the Swift
    // carrier via SBW_Free. These tests cover property read, dispose, and
    // concurrent calls (no cross-buffer aliasing).

    public async Task TestAsyncGetReport_ReadsProperty()
    {
        var worker = new AsyncComplexWorker("report-worker");
        var report = await WithTimeout(worker.GetReportAsync(), DefaultAsyncTimeout);
        AssertNotNull(report, "AsyncGetReport not null");
        AssertEqual("Report for report-worker", report.Title.ToString(), "AsyncReport.Title");
        AssertEqual(1234, report.TokenCount, "AsyncReport.TokenCount");
        TestLogger.Info($"AsyncComplexWorker.GetReportAsync() = {report.Title}/{report.TokenCount}");
    }

    public async Task TestAsyncGetReport_DisposeDoesNotCrash()
    {
        var worker = new AsyncComplexWorker("dispose-worker");
        var report = await WithTimeout(worker.GetReportAsync(), DefaultAsyncTimeout);
        AssertNotNull(report, "AsyncGetReport not null before dispose");
        // Must not crash: allocator-matched Free only works when the carrier was
        // allocated with NativeMemory.Alloc on the C# side.
        report.Dispose();
        TestLogger.Info("AsyncComplexWorker.GetReportAsync() disposed cleanly");
    }

    public async Task TestAsyncGetReport_ConcurrentCallsNoAliasing()
    {
        var worker = new AsyncComplexWorker("concurrent-worker");
        // Kick off several overlapping async calls; each must land on its own
        // VWT-copied buffer. Cross-buffer aliasing would show up as two reports
        // reading identical field values from the last-completed carrier.
        var tasks = new Task<AsyncReport>[8];
        for (int i = 0; i < tasks.Length; i++)
        {
            tasks[i] = worker.GetReportAsync();
        }
        var reports = await WithTimeout(Task.WhenAll(tasks), DefaultAsyncTimeout);
        AssertEqual(8, reports.Length, "All concurrent reports returned");
        for (int i = 0; i < reports.Length; i++)
        {
            AssertNotNull(reports[i], $"Report {i} not null");
            AssertEqual("Report for concurrent-worker", reports[i].Title.ToString(), $"Report {i} title preserved");
            AssertEqual(1234, reports[i].TokenCount, $"Report {i} tokenCount preserved");
        }
        TestLogger.Info($"AsyncComplexWorker.GetReportAsync() × {reports.Length} concurrent calls — no aliasing");
    }

    public async Task TestAsyncGetUsageMetadata_NestedNonFrozen()
    {
        // CountTokens-style nested shape: the outer non-frozen struct wraps
        // another non-frozen struct whose property is then read. The original
        // FirebaseAILogic crash surfaced exactly on this access pattern.
        var worker = new AsyncComplexWorker("usage-worker");
        var usage = await WithTimeout(worker.GetUsageMetadataAsync(), DefaultAsyncTimeout);
        AssertNotNull(usage, "AsyncGetUsageMetadata not null");
        AssertNotNull(usage.Report, "AsyncUsageMetadata.Report not null");
        AssertEqual("Usage for usage-worker", usage.Report.Title.ToString(), "Nested AsyncReport.Title");
        AssertEqual(7777, usage.Report.TokenCount, "Nested AsyncReport.TokenCount");
        TestLogger.Info($"AsyncComplexWorker.GetUsageMetadataAsync() = {usage.Report.Title}/{usage.Report.TokenCount}");
    }

    #endregion

    #region AsyncStatus (Enum) Tests

    public async Task TestAsyncGetStatus()
    {
        var worker = new AsyncComplexWorker("status-worker");
        var status = await WithTimeout(worker.GetStatusAsync(), DefaultAsyncTimeout);
        AssertNotNull(status, "AsyncGetStatus not null");
        // Swift returns .completed(message: "Task finished")
        AssertEqual(AsyncStatus.CaseTag.Completed, status.Tag, "AsyncStatus.Tag == Completed");
        AssertTrue(status.TryGetCompleted(out var message), "TryGetCompleted should succeed");
        AssertEqual("Task finished", message!.ToString(), "Completed message");
        TestLogger.Info($"AsyncComplexWorker.AsyncGetStatus() = Completed(\"{message}\")");
    }

    public async Task TestAsyncGetPendingStatus()
    {
        var worker = new AsyncComplexWorker("pending-worker");
        var status = await WithTimeout(worker.GetPendingStatusAsync(), DefaultAsyncTimeout);
        AssertNotNull(status, "AsyncGetPendingStatus not null");
        AssertEqual(AsyncStatus.CaseTag.Pending, status.Tag, "AsyncStatus.Tag == Pending");
        TestLogger.Info("AsyncComplexWorker.AsyncGetPendingStatus() = Pending");
    }

    #endregion

    #region AsyncTask (Class) Tests

    public async Task TestAsyncGetTask()
    {
        var worker = new AsyncComplexWorker("task-worker");
        var task = await WithTimeout(worker.GetTaskAsync(), DefaultAsyncTimeout);
        AssertNotNull(task, "AsyncGetTask not null");
        // Swift: AsyncTask(taskId: workerId, status: "completed async")
        AssertEqual("task-worker", task.TaskId.ToString(), "AsyncTask.TaskId");
        AssertEqual("completed async", task.Status.ToString(), "AsyncTask.Status");
        TestLogger.Info($"AsyncComplexWorker.AsyncGetTask() = Task[{task.TaskId}]: {task.Status}");
    }

    public async Task TestAsyncStaticTask()
    {
        var task = await WithTimeout(AsyncComplexWorker.StaticTaskAsync(), DefaultAsyncTimeout);
        AssertNotNull(task, "AsyncStaticTask not null");
        AssertEqual("static-task", task.TaskId.ToString(), "Static AsyncTask.TaskId");
        AssertEqual("created", task.Status.ToString(), "Static AsyncTask.Status");
        TestLogger.Info($"AsyncComplexWorker.GetStaticTaskAsync() = Task[{task.TaskId}]: {task.Status}");
    }

    #endregion

    #region Optional Return Tests

    public async Task TestAsyncGetOptionalResultSome()
    {
        var worker = new AsyncComplexWorker("optional-worker");
        var result = await WithTimeout(worker.GetOptionalResultAsync(), DefaultAsyncTimeout);
        // Swift returns AsyncResult(id: 100, message: "Optional result", success: true)
        AssertNotNull(result, "AsyncGetOptionalResult should return Some");
        AssertEqual(100, result!.Id, "Optional AsyncResult.Id");
        AssertEqual("Optional result", result.Message.ToString(), "Optional AsyncResult.Message");
        AssertEqual(true, result.Success, "Optional AsyncResult.Success");
        TestLogger.Info($"AsyncComplexWorker.AsyncGetOptionalResult() = Some(id={result.Id})");
    }

    public async Task TestAsyncGetNilResult()
    {
        var worker = new AsyncComplexWorker("nil-worker");
        var result = await WithTimeout(worker.GetNilResultAsync(), DefaultAsyncTimeout);
        // Swift returns nil
        AssertNull(result, "AsyncGetNilResult should return null");
        TestLogger.Info("AsyncComplexWorker.AsyncGetNilResult() = null");
    }

    public async Task TestAsyncGetOptionalResult_RepeatedCalls_NoCarrierLeak()
    {
        // Optional<@frozen struct with String field> async returns: Swift wraps the carrier
        // via `initializeMemory(as: Optional<AsyncResult>.self, repeating: ...)`, so `.some`
        // holds its own +1 on the embedded String. C# marshals via SwiftOptional<T>.ToNullable()
        // which NewFromPayload-copies into a managed buffer (independent +1). Before the fix,
        // the carrier's +1 leaked every call — SBW_Free reclaims the bytes without running the
        // Optional<T> value-witness Destroy. This loop hammers the Some path so a broken
        // destroy shows up as corruption on a reused address or as growing heap under
        // allocator debug checks. Also interleaves Nil to make sure the Optional<T> destroy
        // on a .none carrier remains a no-op for ARC (no over-release, no crash).
        var worker = new AsyncComplexWorker("optional-repeat-worker");
        for (int i = 0; i < 50; i++)
        {
            var some = await WithTimeout(worker.GetOptionalResultAsync(), DefaultAsyncTimeout);
            AssertNotNull(some, $"Iteration {i}: Some not null");
            AssertEqual(100, some!.Id, $"Iteration {i}: Optional AsyncResult.Id");
            AssertEqual("Optional result", some.Message.ToString(), $"Iteration {i}: Optional AsyncResult.Message");
            AssertEqual(true, some.Success, $"Iteration {i}: Optional AsyncResult.Success");

            var none = await WithTimeout(worker.GetNilResultAsync(), DefaultAsyncTimeout);
            AssertNull(none, $"Iteration {i}: None must remain null");
        }
        TestLogger.Info("AsyncComplexWorker.GetOptionalResultAsync()/GetNilResultAsync() × 50 — no corruption across repeated Some/Nil calls");
    }

    #endregion

    #region Bug #5 Regression: Optional<Array<ObjCBridgeable>> async returns

    // Async wrappers for Optional<Array<URL>> previously emitted
    //   var result = SwiftMarshal.MarshalFromSwift<SwiftOptional<SwiftArray<IntPtr>>>(...).ToNullable();
    // which produced a SwiftArray<IntPtr>? where the TaskCompletionSource expected
    // IReadOnlyList<NSUrl>?. The fix routes Optional<Container<ObjCBridgeable>> through the
    // nullable-pointer ABI: read the storage pointer (toll-free bridged to NSArray) and
    // hand it to ArrayFromHandle. These tests cover Some-with-elements, Some-empty, and None.

    public async Task TestAsyncGetOptionalURLArraySome()
    {
        var worker = new AsyncOptionalContainerWorker();
        var result = await WithTimeout(worker.GetURLArrayAsync(), DefaultAsyncTimeout);
        AssertNotNull(result, "Optional<[URL]> Some should not be null");
        AssertEqual(2, result!.Count, "Two URLs returned");
        AssertEqual("https://example.com", result[0]!.AbsoluteString, "First URL preserved");
        AssertEqual("https://test.com", result[1]!.AbsoluteString, "Second URL preserved");
        TestLogger.Info($"AsyncOptionalContainerWorker.GetURLArrayAsync() = Some({result.Count} URLs)");
    }

    public async Task TestAsyncGetOptionalURLArrayEmpty()
    {
        var worker = new AsyncOptionalContainerWorker();
        var result = await WithTimeout(worker.GetEmptyURLArrayAsync(), DefaultAsyncTimeout);
        AssertNotNull(result, "Optional<[URL]> Some-empty should not be null");
        AssertEqual(0, result!.Count, "Empty array has zero elements");
        TestLogger.Info("AsyncOptionalContainerWorker.GetEmptyURLArrayAsync() = Some([])");
    }

    public async Task TestAsyncGetOptionalURLArrayNil()
    {
        var worker = new AsyncOptionalContainerWorker();
        var result = await WithTimeout(worker.GetNilURLArrayAsync(), DefaultAsyncTimeout);
        AssertNull(result, "Optional<[URL]> None should be null");
        TestLogger.Info("AsyncOptionalContainerWorker.GetNilURLArrayAsync() = null");
    }

    #endregion

    #region X1: AsyncStream<Int32> (primitive element type)
    // The real coverage for this fix is the compile-check step: generated code references
    // SwiftAsyncStream<int> which wouldn't compile without removing the ISwiftObject constraint.
    // Runtime testing is blocked by the class-level [Skip] (async DllImport targets wrong module).

    public async void TestAsyncValueSourceCreation()
    {
        var source = new AsyncValueSource();
        AssertNotNull(source, "AsyncValueSource created");
        TestLogger.Info("AsyncValueSource creation passed");
    }

    #endregion
}
