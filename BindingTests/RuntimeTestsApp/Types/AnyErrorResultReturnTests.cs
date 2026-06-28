// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Types;

/// <summary>
/// Tests for <c>Result&lt;T, any Error&gt;</c> return value marshalling — the well-known stdlib
/// error existential as the failure arm. Mirrors Lottie's
/// <c>DotLottieFile.SynchronouslyBlockingCurrentThread.loadedFrom</c> / <c>.named</c>, typed
/// <c>-&gt; Result&lt;DotLottieFile, any Error&gt;</c> with a bound class success payload. The
/// failure arm surfaces as <c>SwiftResult&lt;T, ExistentialContainer1&gt;</c> (the runtime
/// reconstructs the single-protocol <c>any Error</c> existential metadata for that container).
/// Complements <see cref="ResultReturnTests"/>, which covers concrete-error Result returns.
/// </summary>
public class AnyErrorResultReturnTests : TestBase
{
    public AnyErrorResultReturnTests(TestResults results) : base(results) { }

    public void TestLoadedFromSuccess()
    {
        using var result = AssetLoader.LoadedFrom("song.json");
        AssertTrue(result.IsSuccess, "loadedFrom(non-empty) should be success");
        AssertEqual(SwiftResultCase.Success, result.Case, "Case should be Success");
        using var asset = result.Success;
        AssertEqual("song.json", asset.Name, "Success payload (bound class) carries the path");
    }

    public void TestLoadedFromFailure()
    {
        using var result = AssetLoader.LoadedFrom("");
        AssertTrue(result.IsFailure, "loadedFrom(empty) should be failure (any Error arm)");
        AssertEqual(SwiftResultCase.Failure, result.Case, "Case should be Failure");
    }

    public void TestNamedSuccess()
    {
        using var result = AssetLoader.Named("clip");
        AssertTrue(result.IsSuccess, "named(non-empty) should be success");
        using var asset = result.Success;
        AssertEqual("clip", asset.Name, "Success payload carries the name");
    }

    public void TestNamedFailure()
    {
        using var result = AssetLoader.Named("");
        AssertTrue(result.IsFailure, "named(empty) should be failure (any Error arm)");
    }
}
