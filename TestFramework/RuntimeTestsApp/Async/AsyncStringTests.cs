// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Async;

/// <summary>
/// Tests for AsyncStringWorker (async string returns) and AsyncArrayWorker (async array returns).
/// Validates UTF-8 round-trip, empty strings, Unicode, and array serialization.
///
/// Tier 2: Constructors take string params (SwiftString through CallConvSwift).
/// Async callbacks use Cdecl (not CallConvSwift), avoiding the Mono JIT assertion.
/// </summary>
/// Tier 3: Async P/Invoke entry points are in the SwiftBindings wrapper library but
/// DllImport targets "SwiftBindingsTestLib" → EntryPointNotFoundException at runtime.
/// Tests are ready for when the generator routes async DllImports to the wrapper library.
// Async DllImport targets wrong module — entry points are in wrapper lib, not native dylib
[TestTier(TestTier.Tier3)]
public class AsyncStringTests : TestBase
{
    public AsyncStringTests(TestResults results) : base(results) { }

    #region AsyncStringWorker Tests

    public async Task TestAsyncGetString()
    {
        var worker = new AsyncStringWorker("test");
        var result = await WithTimeout(worker.GetStringAsync(), DefaultAsyncTimeout);
        AssertEqual("test: Hello", result, "AsyncGetString");
        TestLogger.Info($"AsyncStringWorker.AsyncGetString() = {result}");
    }

    public async Task TestAsyncGetUnicodeString()
    {
        var worker = new AsyncStringWorker("utf8");
        var result = await WithTimeout(worker.GetUnicodeStringAsync(), DefaultAsyncTimeout);
        // Swift: "\(prefix): こんにちは 世界 🌍"
        AssertEqual("utf8: こんにちは 世界 🌍", result, "AsyncGetUnicodeString");
        TestLogger.Info($"AsyncStringWorker.AsyncGetUnicodeString() = {result}");
    }

    public async Task TestAsyncGetEmptyString()
    {
        var worker = new AsyncStringWorker("empty");
        var result = await WithTimeout(worker.GetEmptyStringAsync(), DefaultAsyncTimeout);
        AssertEqual("", result, "AsyncGetEmptyString should return empty");
        TestLogger.Info($"AsyncStringWorker.AsyncGetEmptyString() = (empty, length={result.Length})");
    }

    public async Task TestAsyncGetLongString()
    {
        var worker = new AsyncStringWorker("long");
        var result = await WithTimeout(worker.GetLongStringAsync(100), DefaultAsyncTimeout);
        AssertEqual(100, result.Length, "AsyncGetLongString(100) length");
        AssertEqual(new string('A', 100), result, "AsyncGetLongString(100) content");
        TestLogger.Info($"AsyncStringWorker.AsyncGetLongString(100) length = {result.Length}");
    }

    public async Task TestAsyncStaticString()
    {
        var result = await WithTimeout(AsyncStringWorker.StaticStringAsync(), DefaultAsyncTimeout);
        AssertEqual("Static async string", result, "AsyncStaticString");
        TestLogger.Info($"AsyncStringWorker.GetStaticStringAsync() = {result}");
    }

    #endregion

    #region AsyncArrayWorker Tests

    public async Task TestAsyncGetStringArray()
    {
        var worker = new AsyncArrayWorker("arr");
        var result = await WithTimeout(worker.GetStringArrayAsync(), DefaultAsyncTimeout);
        AssertEqual(3, result.Count, "AsyncGetStringArray count");
        AssertEqual("arr-first", result[0].ToString(), "Array[0]");
        AssertEqual("arr-second", result[1].ToString(), "Array[1]");
        AssertEqual("arr-third", result[2].ToString(), "Array[2]");
        TestLogger.Info($"AsyncArrayWorker.AsyncGetStringArray() = [{result.Count} items]");
    }

    public async Task TestAsyncGetEmptyArray()
    {
        var worker = new AsyncArrayWorker("empty");
        var result = await WithTimeout(worker.GetEmptyArrayAsync(), DefaultAsyncTimeout);
        AssertEqual(0, result.Count, "AsyncGetEmptyArray should be empty");
        TestLogger.Info("AsyncArrayWorker.AsyncGetEmptyArray() = []");
    }

    public async Task TestAsyncGetSingleElementArray()
    {
        var worker = new AsyncArrayWorker("single");
        var result = await WithTimeout(worker.GetSingleElementArrayAsync(), DefaultAsyncTimeout);
        AssertEqual(1, result.Count, "Single element array count");
        AssertEqual("single", result[0].ToString(), "Array[0]");
        TestLogger.Info($"AsyncArrayWorker.AsyncGetSingleElementArray() = [{result[0]}]");
    }

    public async Task TestAsyncGetUnicodeArray()
    {
        var worker = new AsyncArrayWorker("unicode");
        var result = await WithTimeout(worker.GetUnicodeArrayAsync(), DefaultAsyncTimeout);
        AssertEqual(4, result.Count, "Unicode array count");
        AssertEqual("Hello", result[0].ToString(), "Array[0]");
        AssertEqual("こんにちは", result[1].ToString(), "Array[1]");
        AssertEqual("안녕하세요", result[2].ToString(), "Array[2]");
        AssertEqual("🎉", result[3].ToString(), "Array[3]");
        TestLogger.Info($"AsyncArrayWorker.AsyncGetUnicodeArray() = [{result.Count} items]");
    }

    public async Task TestAsyncGetIntArray()
    {
        var worker = new AsyncArrayWorker("ints");
        var result = await WithTimeout(worker.GetIntArrayAsync(5), DefaultAsyncTimeout);
        AssertEqual(5, result.Count, "Int array count");
        for (int i = 0; i < 5; i++)
        {
            AssertEqual(i, result[i], $"IntArray[{i}]");
        }
        TestLogger.Info($"AsyncArrayWorker.AsyncGetIntArray(5) = [{string.Join(", ", result)}]");
    }

    public async Task TestAsyncStaticArray()
    {
        var result = await WithTimeout(AsyncArrayWorker.StaticArrayAsync(), DefaultAsyncTimeout);
        AssertEqual(3, result.Count, "Static array count");
        AssertEqual("static", result[0].ToString(), "StaticArray[0]");
        AssertEqual("array", result[1].ToString(), "StaticArray[1]");
        AssertEqual("result", result[2].ToString(), "StaticArray[2]");
        TestLogger.Info($"AsyncArrayWorker.GetStaticArrayAsync() = [{result.Count} items]");
    }

    #endregion
}
