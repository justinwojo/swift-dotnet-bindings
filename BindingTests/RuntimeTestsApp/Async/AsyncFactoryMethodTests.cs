// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

#pragma warning disable SB0001 // CallConvSwift P/Invoke warning

namespace RuntimeTestsApp.Async;

/// <summary>
/// Tests the Lottie DotLottieFile / LottieAnimation async loading pattern:
/// - Static async factory methods returning optional types
/// - Loading from file paths, data, and URL strings
/// - Animation bundle with multiple animations
/// - Animation cache (store/retrieve/clear)
///
/// Exercises L5 (DotLottie format), L7 (URL-based animation loading) from the library parity roadmap.
/// </summary>
public class AsyncFactoryMethodTests : TestBase
{
    public AsyncFactoryMethodTests(TestResults results) : base(results) { }

    #region L5: AnimationAsset File Loading

    [SkipOnSimulator("Mono JIT async assertion — P/Invoke property access in async continuation (upstream Issue 1)")]
    [SkipOnDevice("NativeAOT SIGBUS on async P/Invoke with SafeHandle return")]
    public async Task TestLoadAnimationFromFile()
    {
        var result = await WithTimeout(
            AnimationAsset.LoadFromFileAsync("/path/to/animation.json"),
            DefaultAsyncTimeout);
        AssertNotNull(result, "Loaded animation should not be null");
        AssertEqual("animation.json", result!.Name, "Name extracted from path");
        AssertEqual(60, result.FrameCount, "Frame count");
        AssertApproxEqual(2.0, result.Duration, message: "Duration");
        TestLogger.Info($"LoadFromFile: {result.GetDescribe()}");
    }

    [SkipOnSimulator("Async optional return marshalling + SafeHandle cleanup in async continuation (upstream Issue 1)")]
    [SkipOnDevice("NativeAOT SIGBUS on async P/Invoke")]
    public async Task TestLoadAnimationFromEmptyPath()
    {
        var result = await WithTimeout(
            AnimationAsset.LoadFromFileAsync(""),
            DefaultAsyncTimeout);
        AssertNull(result, "Empty path should return null");
        TestLogger.Info("LoadFromFile with empty path returns null");
    }

    [SkipOnSimulator("Mono JIT async assertion — P/Invoke property access in async continuation (upstream Issue 1)")]
    [SkipOnDevice("NativeAOT SIGBUS on async P/Invoke with SafeHandle return")]
    public async Task TestLoadAnimationFromData()
    {
        var data = new byte[] { 0x7B, 0x22, 0x76, 0x22, 0x7D }; // {"v"}
        var result = await WithTimeout(
            AnimationAsset.LoadFromDataAsync(data, "test.lottie"),
            DefaultAsyncTimeout);
        AssertEqual("test.lottie", result.Name, "Filename preserved");
        AssertEqual(5, result.FrameCount, "Frame count from data length");
        TestLogger.Info($"LoadFromData: {result.GetDescribe()}");
    }

    #endregion

    #region L7: URL-based Animation Loading

    [SkipOnSimulator("Mono JIT async assertion — P/Invoke property access in async continuation (upstream Issue 1)")]
    [SkipOnDevice("NativeAOT SIGBUS on async P/Invoke with SafeHandle return")]
    public async Task TestLoadAnimationFromUrl()
    {
        var result = await WithTimeout(
            AnimationAsset.LoadFromUrlAsync("https://cdn.example.com/anim.json"),
            DefaultAsyncTimeout);
        AssertNotNull(result, "URL-loaded animation should not be null");
        AssertEqual("anim.json", result!.Name, "Name from URL path");
        AssertEqual(120, result.FrameCount, "URL animation frame count");
        AssertApproxEqual(4.0, result.Duration, message: "URL animation duration");
        TestLogger.Info($"LoadFromUrl: {result.GetDescribe()}");
    }

    [SkipOnSimulator("Async optional return marshalling + SafeHandle cleanup in async continuation (upstream Issue 1)")]
    [SkipOnDevice("NativeAOT SIGBUS on async P/Invoke")]
    public async Task TestLoadAnimationFromInvalidUrl()
    {
        var result = await WithTimeout(
            AnimationAsset.LoadFromUrlAsync(""),
            DefaultAsyncTimeout);
        AssertNull(result, "Empty URL should return null");
        TestLogger.Info("LoadFromUrl with empty URL returns null");
    }

    [SkipOnSimulator("Async optional return marshalling + SafeHandle cleanup in async continuation (upstream Issue 1)")]
    [SkipOnDevice("NativeAOT SIGBUS on async P/Invoke")]
    public async Task TestLoadAnimationFromNonHttpUrl()
    {
        var result = await WithTimeout(
            AnimationAsset.LoadFromUrlAsync("file:///local/path"),
            DefaultAsyncTimeout);
        AssertNull(result, "Non-HTTP URL should return null");
        TestLogger.Info("LoadFromUrl with non-HTTP URL returns null");
    }

    #endregion

    #region L5: AnimationBundle (DotLottieFile)

    [SkipOnSimulator("Mono JIT async assertion — P/Invoke property access in async continuation (upstream Issue 1)")]
    [SkipOnDevice("NativeAOT SIGBUS on async P/Invoke with SafeHandle return")]
    public async Task TestLoadBundleFromFile()
    {
        var result = await WithTimeout(
            AnimationBundle.LoadFromFileAsync("/path/to/bundle.lottie"),
            DefaultAsyncTimeout);
        AssertNotNull(result, "Loaded bundle should not be null");
        AssertEqual("bundle.lottie", result!.Filename, "Bundle filename");
        TestLogger.Info($"Bundle loaded: {result.Filename}");
    }

    [SkipOnSimulator("Async optional return marshalling + SafeHandle cleanup in async continuation (upstream Issue 1)")]
    [SkipOnDevice("NativeAOT SIGBUS on async P/Invoke")]
    public async Task TestLoadBundleFromEmptyPath()
    {
        var result = await WithTimeout(
            AnimationBundle.LoadFromFileAsync(""),
            DefaultAsyncTimeout);
        AssertNull(result, "Empty path should return null");
        TestLogger.Info("Bundle LoadFromFile with empty path returns null");
    }

    [SkipOnSimulator("Async LoadFromFileAsync P/Invoke triggers Mono JIT async assertion (upstream Issue 1)")]
    [SkipOnDevice("NativeAOT SIGBUS on async P/Invoke with SafeHandle return")]
    public async Task TestBundleAnimationByIndex()
    {
        var bundle = await WithTimeout(
            AnimationBundle.LoadFromFileAsync("/test/bundle.lottie"),
            DefaultAsyncTimeout);
        AssertNotNull(bundle, "Bundle loaded");
        AssertEqual(2, bundle!.GetAnimationCount(), "Bundle has 2 animations");
        var first = bundle.Animation(0);
        AssertNotNull(first, "First animation exists");
        AssertEqual("intro", first!.Name, "First animation name");
        var second = bundle.Animation(1);
        AssertNotNull(second, "Second animation exists");
        AssertEqual("loop", second!.Name, "Second animation name");
        var outOfBounds = bundle.Animation(99);
        AssertNull(outOfBounds, "Out-of-bounds index returns null");
        TestLogger.Info("Bundle animation index access works correctly");
    }

    #endregion

    #region L5: AnimationCacheStore (DotLottieCache)

    public void TestAnimationCacheStoreAndRetrieve()
    {
        using var cache = new AnimationCacheStore();
        using var anim = new AnimationAsset(name: "test", frameCount: 30, duration: 1.0);
        cache.CacheAnimation(anim, "key1");
        AssertEqual(1, cache.CacheSize, "Cache has 1 entry");
        var retrieved = cache.CachedAnimation("key1");
        AssertNotNull(retrieved, "Cached animation retrieved");
        AssertEqual("test", retrieved!.Name, "Retrieved animation name");
        TestLogger.Info("Animation cache store/retrieve works");
    }

    public void TestAnimationCacheMiss()
    {
        using var cache = new AnimationCacheStore();
        var result = cache.CachedAnimation("nonexistent");
        AssertNull(result, "Cache miss returns null");
        TestLogger.Info("Animation cache miss returns null");
    }

    public void TestAnimationCacheClear()
    {
        using var cache = new AnimationCacheStore();
        using var anim = new AnimationAsset(name: "a", frameCount: 10, duration: 0.5);
        cache.CacheAnimation(anim, "k");
        AssertEqual(1, cache.CacheSize, "Cache has 1 entry");
        cache.ClearCache();
        AssertEqual(0, cache.CacheSize, "Cache cleared");
        TestLogger.Info("Animation cache clear works");
    }

    #endregion
}
