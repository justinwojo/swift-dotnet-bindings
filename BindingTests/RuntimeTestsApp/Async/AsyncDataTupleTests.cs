// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Async;

/// <summary>
/// Tests for AsyncDataWorker — async methods returning tuples containing Foundation.Data.
/// Validates the @convention(c) callback fix: Foundation.Data in tuple callbacks must be
/// passed via UnsafeMutableRawPointer (heap-allocated) to avoid ABI issues with struct
/// value parameters in C-calling-convention callbacks.
///
/// Before the fix, these crashed with SIGSEGV on Mono JIT because Foundation.Data was
/// passed by value in the @convention(c) callback, causing parameter misalignment.
/// </summary>
public class AsyncDataTupleTests : TestBase
{
    public AsyncDataTupleTests(TestResults results) : base(results) { }

    public async Task TestAsyncDataTuple_DataFirst()
    {
        var worker = new AsyncDataWorker("hello");
        var (data, size) = await WithTimeout(worker.FetchDataWithSizeAsync(), DefaultAsyncTimeout);
        AssertNotNull(data, "Data bytes not null");
        AssertEqual(5, data.Length, "Data length matches 'hello' UTF-8 bytes");
        AssertEqual(5, size, "Size element matches Data length");
        TestLogger.Info($"AsyncDataWorker.FetchDataWithSize() = {data.Length} bytes, size={size}");
    }

    public async Task TestAsyncDataTuple_DataSecond()
    {
        var worker = new AsyncDataWorker("test");
        var (size, data) = await WithTimeout(worker.FetchSizeWithDataAsync(), DefaultAsyncTimeout);
        AssertNotNull(data, "Data bytes not null");
        AssertEqual(4, data.Length, "Data length matches 'test' UTF-8 bytes");
        AssertEqual(4, size, "Size element matches Data length");
        TestLogger.Info($"AsyncDataWorker.FetchSizeWithData() = size={size}, {data.Length} bytes");
    }

    public async Task TestAsyncDataTuple_DataWithString()
    {
        var worker = new AsyncDataWorker("café");
        var (data, label) = await WithTimeout(worker.FetchDataWithLabelAsync(), DefaultAsyncTimeout);
        AssertNotNull(data, "Data bytes not null");
        // 0xCA, 0xFE, 0xBA, 0xBE = 4 bytes
        AssertEqual(4, data.Length, "Data is 4 bytes (0xCAFEBABE)");
        AssertEqual(0xCA, data[0], "First byte is 0xCA");
        AssertEqual(0xBE, data[3], "Last byte is 0xBE");
        AssertEqual("café", label, "String label preserved");
        TestLogger.Info($"AsyncDataWorker.FetchDataWithLabel() = {data.Length} bytes, label='{label}'");
    }
}
