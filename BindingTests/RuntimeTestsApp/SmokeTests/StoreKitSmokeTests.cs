// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if STOREKIT_SMOKE
extern alias StoreKitSwift;

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using RuntimeTestsApp.Infrastructure;

namespace RuntimeTestsApp.SmokeTests;

/// <summary>
/// First-party command-line StoreKit Sandbox qualification. The build harness first installs a
/// pure-Swift control under this app's bundle id on the same simulator and requires a fresh receipt
/// for all four operations. These tests independently repeat the semantic checks through generated
/// managed bindings. Xcode's .storekit backend is deliberately outside this simctl lane.
/// </summary>
public class StoreKitSmokeTests : TestBase
{
    private const string ProductIdArgument = "--storekit-sandbox-product-id";
    private static readonly TimeSpan OperationBudget = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan UnwindBudget = TimeSpan.FromSeconds(10);

    public StoreKitSmokeTests(TestResults results) : base(results) { }

    public void TestAppStoreCanMakePayments()
    {
        var canMakePayments = StoreKitSwift::StoreKit.AppStore.CanMakePayments;
        TestLogger.Info($"StoreKit.AppStore.CanMakePayments = {canMakePayments}");
        AssertTrue(true, "AppStore.CanMakePayments resolved through the StoreKit wrapper");
    }

    public async Task TestSandboxAppTransaction()
    {
        _ = RequireSandboxProductId();
        using var shared = await RunBounded(
            token => StoreKitSwift::StoreKit.AppTransaction.GetSharedAsync(token),
            "AppTransaction.shared");

        AssertTrue(shared.TryGetVerified(out var transaction) && transaction is not null,
            "AppTransaction.shared must return a verified payload after native Sandbox readiness");
        using var transactionLease = transaction;
        var expectedBundle = Foundation.NSBundle.MainBundle.BundleIdentifier;
        var environment = transaction!.Environment.RawValue;
        TestLogger.Info(
            $"AppTransaction.shared: bundle={transaction.BundleID}, expected={expectedBundle}, environment={environment}");
        AssertTrue(string.Equals(transaction.BundleID, expectedBundle, StringComparison.Ordinal),
            $"AppTransaction.bundleID '{transaction.BundleID}' must match '{expectedBundle}'");
        AssertTrue(string.Equals(environment, "Sandbox", StringComparison.OrdinalIgnoreCase),
            $"AppTransaction environment '{environment}' must be Sandbox");
    }

    public async Task TestSandboxCurrentEntitlements()
    {
        _ = RequireSandboxProductId();
        var count = await EnumerateSnapshot(
            StoreKitSwift::StoreKit.Transaction.CurrentEntitlements,
            "Transaction.currentEntitlements");
        TestLogger.Info($"Transaction.currentEntitlements completed with {count} verified Sandbox transaction(s)");
    }

    public async Task TestSandboxAllTransactions()
    {
        _ = RequireSandboxProductId();
        var count = await EnumerateSnapshot(
            StoreKitSwift::StoreKit.Transaction.All,
            "Transaction.all");
        TestLogger.Info($"Transaction.all completed with {count} verified Sandbox transaction(s)");
    }

    public async Task TestSandboxProductLookup()
    {
        var productId = RequireSandboxProductId();
        var products = await RunBounded(
            token => StoreKitSwift::StoreKit.Product.ProductsAsync([productId], token),
            "Product.products(for:)");
        AssertTrue(products is not null, "Product.products(for:) returned a non-null list");
        try
        {
            AssertTrue(products!.Count == 1,
                $"exact Sandbox product '{productId}' must return exactly one product (got {products.Count})");
            AssertTrue(string.Equals(products[0].Id, productId, StringComparison.Ordinal),
                $"Product.products(for:) returned '{products[0].Id}', expected '{productId}'");
        }
        finally
        {
            if (products is not null)
            {
                foreach (var product in products)
                    product.Dispose();
            }
        }
    }

    /// <summary>
    /// Uses already-canceled tokens so network timing cannot manufacture the cancellation event.
    /// The real generated task and iterator must both terminate, and the iterator's synchronous
    /// Dispose must complete. The downstream synthetic TimeoutTests fixture remains the deterministic
    /// late-result ownership oracle; a managed terminal state alone does not claim native cleanup.
    /// </summary>
    public async Task TestSandboxManagedCancellationUnwinds()
    {
        _ = RequireSandboxProductId();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        StoreKitSwift::StoreKit.VerificationResult<StoreKitSwift::StoreKit.AppTransaction>? raced = null;
        try
        {
            raced = await AwaitWithin(
                StoreKitSwift::StoreKit.AppTransaction.GetSharedAsync(canceled.Token),
                TimeSpan.FromSeconds(5),
                "pre-canceled AppTransaction.shared");
            throw new InvalidOperationException(
                "pre-canceled AppTransaction.shared completed successfully instead of observing cancellation");
        }
        catch (OperationCanceledException)
        {
            TestLogger.Info("pre-canceled AppTransaction.shared reached a canceled terminal state");
        }
        finally
        {
            raced?.Dispose();
        }

        using var sequence = StoreKitSwift::StoreKit.Transaction.All;
        using var iterator = sequence.MakeAsyncIterator();
        StoreKitSwift::StoreKit.VerificationResult<StoreKitSwift::StoreKit.Transaction>? current = null;
        try
        {
            current = await AwaitWithin(
                iterator.NextAsync(canceled.Token),
                TimeSpan.FromSeconds(5),
                "pre-canceled Transaction.all iterator");
            throw new InvalidOperationException(
                "pre-canceled Transaction.all iterator completed successfully instead of observing cancellation");
        }
        catch (OperationCanceledException)
        {
            TestLogger.Info("pre-canceled Transaction.all iterator reached a canceled terminal state");
        }
        finally
        {
            current?.Dispose();
        }

        AssertTrue(true, "generated StoreKit task and iterator cancellation unwound within bounds");
    }

    private static string RequireSandboxProductId()
    {
        var arguments = Foundation.NSProcessInfo.ProcessInfo.Arguments;
        for (var index = 0; index < arguments.Length - 1; index++)
        {
            if (!string.Equals(arguments[index], ProductIdArgument, StringComparison.Ordinal))
                continue;
            var value = arguments[index + 1]?.Trim();
            if (!string.IsNullOrWhiteSpace(value)
                && !value.Contains("placeholder", StringComparison.OrdinalIgnoreCase)
                && !value.Contains("nonexistent", StringComparison.OrdinalIgnoreCase))
                return value;
        }

        throw new InvalidOperationException(
            $"{ProductIdArgument} is missing or invalid. A first-party iOS/tvOS command-line smoke " +
            "must receive the exact App Store Connect product only after the same-simulator pure-Swift " +
            "control passes. A .storekit file cannot establish readiness through simctl.");
    }

    private static async Task<T> RunBounded<T>(
        Func<CancellationToken, Task<T>> start,
        string operation)
    {
        using var cts = new CancellationTokenSource();
        var task = start(cts.Token);
        var winner = await Task.WhenAny(task, Task.Delay(OperationBudget));
        if (ReferenceEquals(winner, task))
            return await task;

        cts.Cancel();
        var unwound = await Task.WhenAny(task, Task.Delay(UnwindBudget));
        if (!ReferenceEquals(unwound, task))
            throw new TimeoutException(
                $"{operation} exceeded {OperationBudget.TotalSeconds:0}s and its managed task did not " +
                $"unwind within {UnwindBudget.TotalSeconds:0}s after cancellation");

        try
        {
            var late = await task;
            if (late is IDisposable disposable)
                disposable.Dispose();
        }
        catch (OperationCanceledException)
        {
            // Expected timeout-unwind outcome.
        }
        catch
        {
            // The timeout remains the qualification verdict; the task is observed here.
        }

        throw new TimeoutException(
            $"{operation} exceeded {OperationBudget.TotalSeconds:0}s; managed unwind completed, " +
            "but native completion/cleanup is not inferred from that managed terminal state");
    }

    private static async Task<T> AwaitWithin<T>(Task<T> task, TimeSpan budget, string operation)
    {
        var winner = await Task.WhenAny(task, Task.Delay(budget));
        if (!ReferenceEquals(winner, task))
            throw new TimeoutException($"{operation} did not reach a terminal state within {budget.TotalSeconds:0}s");
        return await task;
    }

    private static async Task<int> EnumerateSnapshot(
        StoreKitSwift::StoreKit.Transaction.Transactions sequence,
        string operation)
    {
        using (sequence)
        using (var iterator = sequence.MakeAsyncIterator())
        using (var cts = new CancellationTokenSource())
        {
            var stopwatch = Stopwatch.StartNew();
            var count = 0;
            while (count <= 64)
            {
                var remaining = OperationBudget - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                    remaining = TimeSpan.FromMilliseconds(1);
                var nextTask = iterator.NextAsync(cts.Token);
                var winner = await Task.WhenAny(nextTask, Task.Delay(remaining));
                if (!ReferenceEquals(winner, nextTask))
                {
                    cts.Cancel();
                    var unwound = await Task.WhenAny(nextTask, Task.Delay(UnwindBudget));
                    if (!ReferenceEquals(unwound, nextTask))
                        throw new TimeoutException(
                            $"{operation} did not complete in {OperationBudget.TotalSeconds:0}s and " +
                            $"NextAsync did not unwind within {UnwindBudget.TotalSeconds:0}s");
                    try
                    {
                        using var late = await nextTask;
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    throw new TimeoutException(
                        $"{operation} exceeded {OperationBudget.TotalSeconds:0}s; managed iterator unwound, " +
                        "native cleanup remains unproven");
                }

                using var result = await nextTask;
                if (result is null)
                    return count;
                AssertVerifiedSandboxTransaction(result, operation);
                count++;
            }

            throw new InvalidOperationException($"{operation} exceeded the 64-element safety bound");
        }
    }

    private static void AssertVerifiedSandboxTransaction(
        StoreKitSwift::StoreKit.VerificationResult<StoreKitSwift::StoreKit.Transaction> result,
        string operation)
    {
        if (!result.TryGetVerified(out var transaction) || transaction is null)
            throw new InvalidOperationException($"{operation} yielded an unverified or null transaction");
        using var transactionLease = transaction;
        var environment = transaction.Environment.RawValue;
        if (!string.Equals(environment, "Sandbox", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"{operation} yielded environment '{environment}', expected Sandbox");
    }
}

#endif
