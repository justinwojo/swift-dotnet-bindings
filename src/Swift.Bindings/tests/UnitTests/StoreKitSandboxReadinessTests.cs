// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using Xunit;

namespace Swift.Bindings.UnitTests;

public class StoreKitSandboxReadinessTests
{
    private const string BundleId = "com.swiftbindings.runtimetestsapp";
    private const string ProductId = "com.swiftbindings.runtimetestsapp.nonconsumable";
    private const string Token = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void MissingProductId_FailsBeforeLaunchAndProhibitsCliStoreKitFile()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => StoreKitSandboxReadiness.RequireConfiguration(null, "ios", BundleId));

        Assert.Contains("failed before app launch", error.Message);
        Assert.Contains(StoreKitSandboxReadiness.ProductIdEnvironmentVariable, error.Message);
        Assert.Contains(".storekit", error.Message);
        Assert.Contains("simctl", error.Message);
    }

    [Theory]
    [InlineData("com.example.nonexistent")]
    [InlineData("placeholder")]
    [InlineData("spaces are invalid")]
    public void PlaceholderOrMalformedProductId_FailsClosed(string productId)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => StoreKitSandboxReadiness.RequireConfiguration(productId, "tvos", BundleId));
        Assert.Contains("rejected product id", error.Message);
    }

    [Fact]
    public void FreshSameBackendReceipt_PassesAndPreservesEmptySequenceCounts()
    {
        var expected = StoreKitSandboxReadiness.RequireConfiguration(ProductId, "ios", BundleId);
        var output = Receipt(Token, ProductId, "ios", BundleId, products: 1, current: 0, all: 0)
            + "\n[SK-CONTROL] TEST SUCCESS\n";

        var receipt = StoreKitSandboxReadiness.RequireFreshNativeReceipt(output, expected, Token);

        Assert.Equal(1, receipt.ProductCount);
        Assert.Equal(0, receipt.CurrentEntitlementsCount);
        Assert.Equal(0, receipt.AllTransactionsCount);
    }

    [Fact]
    public void StaleToken_FailsClosed()
    {
        var expected = StoreKitSandboxReadiness.RequireConfiguration(ProductId, "ios", BundleId);
        var stale = "fedcba9876543210fedcba9876543210";
        var output = Receipt(stale, ProductId, "ios", BundleId, 1, 0, 0) + "\n[SK-CONTROL] TEST SUCCESS\n";

        var error = Assert.Throws<InvalidOperationException>(
            () => StoreKitSandboxReadiness.RequireFreshNativeReceipt(output, expected, Token));
        Assert.Contains("field 'token'", error.Message);
    }

    [Theory]
    [InlineData("appTransaction=production", "field 'appTransaction'")]
    [InlineData("products=0", "exactly one configured product")]
    [InlineData("products=2", "exactly one configured product")]
    [InlineData("currentEntitlements=pending", "not a non-negative integer")]
    [InlineData("all=Infinity", "not a non-negative integer")]
    public void WrongBackendOrIncompleteOperation_FailsClosed(string replacement, string expectedMessage)
    {
        var expected = StoreKitSandboxReadiness.RequireConfiguration(ProductId, "tvos", BundleId);
        var output = Receipt(Token, ProductId, "tvos", BundleId, 1, 0, 0)
            .Replace(replacement.Split('=')[0] + "=" + OriginalValue(replacement.Split('=')[0]), replacement,
                StringComparison.Ordinal)
            + "\n[SK-CONTROL] TEST SUCCESS\n";

        var error = Assert.Throws<InvalidOperationException>(
            () => StoreKitSandboxReadiness.RequireFreshNativeReceipt(output, expected, Token));
        Assert.Contains(expectedMessage, error.Message);
    }

    [Fact]
    public void MissingSuccessMarker_FailsClosed()
    {
        var expected = StoreKitSandboxReadiness.RequireConfiguration(ProductId, "ios", BundleId);
        var error = Assert.Throws<InvalidOperationException>(() =>
            StoreKitSandboxReadiness.RequireFreshNativeReceipt(
                Receipt(Token, ProductId, "ios", BundleId, 1, 0, 0), expected, Token));
        Assert.Contains("TEST SUCCESS", error.Message);
    }

    [Theory]
    [InlineData("tvos", BundleId)]
    [InlineData("ios", "com.example.other-app")]
    public void WrongPlatformOrBundle_FailsClosed(string platform, string bundleId)
    {
        var expected = StoreKitSandboxReadiness.RequireConfiguration(ProductId, "ios", BundleId);
        var output = Receipt(Token, ProductId, platform, bundleId, 1, 0, 0)
            + "\n[SK-CONTROL] TEST SUCCESS\n";

        Assert.Throws<InvalidOperationException>(() =>
            StoreKitSandboxReadiness.RequireFreshNativeReceipt(output, expected, Token));
    }

    [Fact]
    public void DuplicateReadyReceipt_FailsClosed()
    {
        var expected = StoreKitSandboxReadiness.RequireConfiguration(ProductId, "ios", BundleId);
        var receipt = Receipt(Token, ProductId, "ios", BundleId, 1, 0, 0);

        var error = Assert.Throws<InvalidOperationException>(() =>
            StoreKitSandboxReadiness.RequireFreshNativeReceipt(
                receipt + "\n" + receipt + "\n[SK-CONTROL] TEST SUCCESS\n", expected, Token));
        Assert.Contains("exactly one fresh receipt", error.Message);
    }

    [Fact]
    public void NonHexRunToken_FailsClosed()
    {
        var expected = StoreKitSandboxReadiness.RequireConfiguration(ProductId, "ios", BundleId);
        var malformed = "zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz";

        var error = Assert.Throws<InvalidOperationException>(() =>
            StoreKitSandboxReadiness.RequireFreshNativeReceipt(
                Receipt(malformed, ProductId, "ios", BundleId, 1, 0, 0)
                    + "\n[SK-CONTROL] TEST SUCCESS\n",
                expected,
                malformed));
        Assert.Contains("run token is malformed", error.Message);
    }

    private static string OriginalValue(string field) => field switch
    {
        "appTransaction" => "sandbox",
        "products" => "1",
        "currentEntitlements" => "0",
        "all" => "0",
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    private static string Receipt(string token, string product, string platform, string bundle,
        int products, int current, int all) =>
        $"{StoreKitSandboxReadiness.ReceiptPrefix}schema=1 platform={platform} bundle={bundle} " +
        $"product={product} token={token} appTransaction=sandbox products={products} " +
        $"currentEntitlements={current} all={all}";
}
