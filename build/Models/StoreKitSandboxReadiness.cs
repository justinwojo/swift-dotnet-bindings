// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>
/// Pure fail-closed policy for the command-line StoreKit Sandbox lane. A configured product id is
/// only declared intent; a fresh same-device native-control receipt is the readiness observation
/// that permits the managed app to launch.
/// </summary>
internal static class StoreKitSandboxReadiness
{
    internal const string ProductIdEnvironmentVariable = "STOREKIT_SANDBOX_PRODUCT_ID";
    internal const string ProductIdArgument = "--storekit-sandbox-product-id";
    internal const string RunTokenArgument = "--storekit-sandbox-run-token";
    internal const string ReceiptPrefix = "[SK-CONTROL] READY ";
    internal const string SuccessMarker = "[SK-CONTROL] TEST SUCCESS";

    private static readonly Regex ProductIdPattern =
        new("^[A-Za-z0-9][A-Za-z0-9._-]{2,254}$", RegexOptions.CultureInvariant);
    private static readonly Regex RunTokenPattern =
        new("^[a-f0-9]{32}$", RegexOptions.CultureInvariant);

    internal sealed record Configuration(string Platform, string BundleId, string ProductId);

    internal sealed record Receipt(
        string Platform,
        string BundleId,
        string ProductId,
        string RunToken,
        int ProductCount,
        int CurrentEntitlementsCount,
        int AllTransactionsCount);

    internal static Configuration RequireConfiguration(
        string? productId,
        string platform,
        string bundleId)
    {
        if (platform is not ("ios" or "tvos"))
            throw new InvalidOperationException(
                $"StoreKit Sandbox readiness is supported only for command-line iOS/tvOS runners; got '{platform}'.");
        if (string.IsNullOrWhiteSpace(bundleId))
            throw new InvalidOperationException("StoreKit Sandbox readiness requires the managed app bundle id.");
        if (string.IsNullOrWhiteSpace(productId))
            throw new InvalidOperationException(
                $"StoreKit Sandbox readiness preflight failed before app launch: {ProductIdEnvironmentVariable} " +
                "(or --storekit-sandbox-product-id) is missing. Configure an App Store Connect product for " +
                $"bundle '{bundleId}' and sign the target {platform} simulator into a Sandbox tester account. " +
                "A .storekit path cannot be activated through simctl, mlaunch, dotnet, or this command-line lane.");

        productId = productId.Trim();
        if (!ProductIdPattern.IsMatch(productId)
            || productId.Contains("nonexistent", StringComparison.OrdinalIgnoreCase)
            || productId.Contains("placeholder", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"StoreKit Sandbox readiness preflight rejected product id '{productId}'. Supply the exact " +
                "App Store Connect Sandbox product id; placeholder/nonexistent probes cannot establish backend readiness.");
        }

        return new Configuration(platform, bundleId, productId);
    }

    internal static Receipt RequireFreshNativeReceipt(
        string output,
        Configuration expected,
        string runToken)
    {
        if (!RunTokenPattern.IsMatch(runToken))
            throw new InvalidOperationException("StoreKit Sandbox native-control run token is malformed.");

        var receiptLines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith(ReceiptPrefix, StringComparison.Ordinal))
            .ToArray();
        if (receiptLines.Length != 1)
            throw new InvalidOperationException(
                $"StoreKit Sandbox native control emitted {receiptLines.Length} READY receipts; exactly one fresh receipt is required.");
        if (!output.Split('\n').Any(line => string.Equals(line.Trim(), SuccessMarker, StringComparison.Ordinal)))
            throw new InvalidOperationException(
                "StoreKit Sandbox native control did not emit TEST SUCCESS; backend readiness is not established.");

        var fields = ParseFields(receiptLines[0][ReceiptPrefix.Length..]);
        RequireField(fields, "schema", "1");
        RequireField(fields, "platform", expected.Platform);
        RequireField(fields, "bundle", expected.BundleId);
        RequireField(fields, "product", expected.ProductId);
        RequireField(fields, "token", runToken);
        RequireField(fields, "appTransaction", "sandbox");

        var products = RequireNonNegativeInt(fields, "products");
        if (products != 1)
            throw new InvalidOperationException(
                $"StoreKit Sandbox native receipt reported {products} products; exactly one configured product is required.");
        var current = RequireNonNegativeInt(fields, "currentEntitlements");
        var all = RequireNonNegativeInt(fields, "all");

        return new Receipt(expected.Platform, expected.BundleId, expected.ProductId, runToken,
            products, current, all);
    }

    private static Dictionary<string, string> ParseFields(string body)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var token in body.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = token.IndexOf('=');
            if (separator <= 0 || separator == token.Length - 1)
                throw new InvalidOperationException($"StoreKit Sandbox native receipt contains malformed token '{token}'.");
            if (!fields.TryAdd(token[..separator], token[(separator + 1)..]))
                throw new InvalidOperationException(
                    $"StoreKit Sandbox native receipt repeats field '{token[..separator]}'.");
        }
        return fields;
    }

    private static void RequireField(IReadOnlyDictionary<string, string> fields, string name, string expected)
    {
        if (!fields.TryGetValue(name, out var actual) || !string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"StoreKit Sandbox native receipt field '{name}' was '{actual ?? "<missing>"}', expected '{expected}'.");
    }

    private static int RequireNonNegativeInt(IReadOnlyDictionary<string, string> fields, string name)
    {
        if (!fields.TryGetValue(name, out var text) || !int.TryParse(text, out var value) || value < 0)
            throw new InvalidOperationException(
                $"StoreKit Sandbox native receipt field '{name}' is missing or not a non-negative integer.");
        return value;
    }
}
