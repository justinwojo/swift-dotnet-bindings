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
[TestTier(TestTier.Tier3)]
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
        AssertEqual("completed async", task.StatusProperty.ToString(), "AsyncTask.Status");
        TestLogger.Info($"AsyncComplexWorker.AsyncGetTask() = Task[{task.TaskId}]: {task.StatusProperty}");
    }

    public async Task TestAsyncStaticTask()
    {
        var task = await WithTimeout(AsyncComplexWorker.StaticTaskAsync(), DefaultAsyncTimeout);
        AssertNotNull(task, "AsyncStaticTask not null");
        AssertEqual("static-task", task.TaskId.ToString(), "Static AsyncTask.TaskId");
        AssertEqual("created", task.StatusProperty.ToString(), "Static AsyncTask.Status");
        TestLogger.Info($"AsyncComplexWorker.GetStaticTaskAsync() = Task[{task.TaskId}]: {task.StatusProperty}");
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

    #endregion
}
