// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

#pragma warning disable SB0001 // CallConvSwift P/Invoke warning

namespace RuntimeTestsApp.Patterns;

/// <summary>
/// Tests the Nuke ImagePipeline.Cache pattern:
/// - Nested class with CRUD-like methods (store, retrieve, remove, contains)
/// - Optional return values from cache query methods
/// - Bool-returning containment check
/// - Pipeline singleton with cache property access
///
/// Exercises N6 (Pipeline.Cache methods verification) from the library parity roadmap.
/// </summary>
public class CachePatternTests : TestBase
{
    public CachePatternTests(TestResults results) : base(results) { }

    #region Cache Construction and Access

    public void TestDataPipelineConstruction()
    {
        using var pipeline = new DataPipeline(label: "test-pipeline");
        AssertEqual("test-pipeline", pipeline.Label, "Pipeline label");
        TestLogger.Info($"DataPipeline created with label '{pipeline.Label}'");
    }

    [SkipOnSimulator("DataPipeline.Shared uses CallConvSwift static property")]
    public void TestDataPipelineSharedSingleton()
    {
        using var shared = DataPipeline.Shared;
        AssertNotNull(shared, "Shared pipeline should not be null");
        AssertEqual("shared", shared.Label, "Shared pipeline label");
        TestLogger.Info($"DataPipeline.Shared.Label = '{shared.Label}'");
    }

    #endregion

    #region Cache Store and Retrieve

    public void TestCacheStoreAndRetrieve()
    {
        using var pipeline = new DataPipeline(label: "store-test");
        using var cache = pipeline.Cache;
        using var entry = new CachedEntry(data: "image-data", size: 1024, timestamp: 1.0);
        cache.StoreItem(entry, "key1");
        var retrieved = cache.CachedItem("key1");
        AssertNotNull(retrieved, "Retrieved entry should not be null");
        AssertEqual("image-data", retrieved!.Data, "Retrieved data matches");
        AssertEqual(1024, retrieved.Size, "Retrieved size matches");
        TestLogger.Info("Cache store + retrieve round-trip passed");
    }

    public void TestCacheRetrieveNonexistent()
    {
        using var pipeline = new DataPipeline(label: "miss-test");
        using var cache = pipeline.Cache;
        var result = cache.CachedItem("nonexistent");
        AssertNull(result, "Nonexistent key should return null");
        TestLogger.Info("Cache miss returns null correctly");
    }

    public void TestCacheMultipleEntries()
    {
        using var pipeline = new DataPipeline(label: "multi-test");
        using var cache = pipeline.Cache;
        using var e1 = new CachedEntry(data: "alpha", size: 100, timestamp: 1.0);
        using var e2 = new CachedEntry(data: "beta", size: 200, timestamp: 2.0);
        using var e3 = new CachedEntry(data: "gamma", size: 300, timestamp: 3.0);
        cache.StoreItem(e1, "a");
        cache.StoreItem(e2, "b");
        cache.StoreItem(e3, "c");
        AssertEqual("alpha", cache.CachedItem("a")!.Data, "First entry data");
        AssertEqual("gamma", cache.CachedItem("c")!.Data, "Third entry data");
        TestLogger.Info("Multiple cache entries stored and retrieved correctly");
    }

    #endregion

    #region Cache Contains

    public void TestCacheContainsExistingItem()
    {
        using var pipeline = new DataPipeline(label: "contains-test");
        using var cache = pipeline.Cache;
        using var entry = new CachedEntry(data: "test", size: 50, timestamp: 1.0);
        cache.StoreItem(entry, "exists");
        AssertTrue(cache.ContainsItem("exists"), "Contains should return true for existing key");
        TestLogger.Info("ContainsItem returns true for existing key");
    }

    public void TestCacheContainsMissingItem()
    {
        using var pipeline = new DataPipeline(label: "missing-test");
        using var cache = pipeline.Cache;
        AssertFalse(cache.ContainsItem("missing"), "Contains should return false for missing key");
        TestLogger.Info("ContainsItem returns false for missing key");
    }

    #endregion

    #region Cache Remove

    public void TestCacheRemoveItem()
    {
        using var pipeline = new DataPipeline(label: "remove-test");
        using var cache = pipeline.Cache;
        using var entry = new CachedEntry(data: "remove-me", size: 75, timestamp: 1.0);
        cache.StoreItem(entry, "key");
        AssertTrue(cache.ContainsItem("key"), "Item exists before removal");
        cache.RemoveItem("key");
        AssertFalse(cache.ContainsItem("key"), "Item removed");
        TestLogger.Info("Cache item removed successfully");
    }

    [SkipOnSimulator("RemoveAll uses CallConvSwift")]
    public void TestCacheRemoveAll()
    {
        using var pipeline = new DataPipeline(label: "clear-test");
        using var cache = pipeline.Cache;
        using var e1 = new CachedEntry(data: "a", size: 10, timestamp: 1.0);
        using var e2 = new CachedEntry(data: "b", size: 20, timestamp: 2.0);
        cache.StoreItem(e1, "x");
        cache.StoreItem(e2, "y");
        cache.RemoveAll();
        AssertFalse(cache.ContainsItem("x"), "First item removed");
        AssertFalse(cache.ContainsItem("y"), "Second item removed");
        TestLogger.Info("RemoveAll clears all cache entries");
    }

    #endregion

    #region Cache Key Generation and Overwrite

    public void TestCacheMakeKey()
    {
        using var pipeline = new DataPipeline(label: "key-test");
        using var cache = pipeline.Cache;
        var key = cache.MakeKey("https://example.com/image.jpg", "thumb");
        AssertEqual("https://example.com/image.jpg#thumb", key, "Generated cache key");
        TestLogger.Info($"MakeKey = '{key}'");
    }

    public void TestCacheOverwrite()
    {
        using var pipeline = new DataPipeline(label: "overwrite-test");
        using var cache = pipeline.Cache;
        using var e1 = new CachedEntry(data: "original", size: 100, timestamp: 1.0);
        using var e2 = new CachedEntry(data: "updated", size: 200, timestamp: 2.0);
        cache.StoreItem(e1, "key");
        cache.StoreItem(e2, "key");
        AssertEqual("updated", cache.CachedItem("key")!.Data, "Overwritten value");
        TestLogger.Info("Cache overwrite replaces existing entry");
    }

    #endregion

    #region CachedEntry Properties

    public void TestCachedEntryConstruction()
    {
        using var entry = new CachedEntry(data: "payload", size: 512, timestamp: 99.5);
        AssertEqual("payload", entry.Data, "Entry data");
        AssertEqual(512, entry.Size, "Entry size");
        AssertApproxEqual(99.5, entry.Timestamp, message: "Entry timestamp");
        TestLogger.Info($"CachedEntry: data='{entry.Data}', size={entry.Size}, ts={entry.Timestamp}");
    }

    public void TestCachedEntryDescribe()
    {
        using var entry = new CachedEntry(data: "img", size: 2048, timestamp: 1.0);
        var desc = entry.GetDescribe();
        AssertTrue(desc.Contains("img"), "Description contains data");
        AssertTrue(desc.Contains("2048"), "Description contains size");
        TestLogger.Info($"CachedEntry.Describe() = '{desc}'");
    }

    #endregion
}
