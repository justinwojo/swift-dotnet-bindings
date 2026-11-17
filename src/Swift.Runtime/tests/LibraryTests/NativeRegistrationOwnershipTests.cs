// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using Swift.Runtime;
using Xunit;

namespace Swift.Runtime.Tests;

public class NativeRegistrationOwnershipTests
{
    private static string UniqueModule() => "OwnershipTestModule_" + Guid.NewGuid().ToString("N");

    [Fact]
    public void FirstClaimOwnsTheModuleAndRepeatedClaimsByTheOwnerAreAccepted()
    {
        var module = UniqueModule();
        var owner = typeof(NativeRegistrationOwnershipTests).Assembly;

        Assert.Null(NativeRegistrationOwnership.GetModuleOwner(module));
        Assert.True(NativeRegistrationOwnership.ClaimModule(module, owner));
        Assert.False(NativeRegistrationOwnership.ClaimModule(module, owner));
        Assert.Same(owner, NativeRegistrationOwnership.GetModuleOwner(module));
    }

    [Fact]
    public void ADifferentAssemblyIsRefusedAndTheOwnerIsUnchanged()
    {
        var module = UniqueModule();
        var owner = typeof(NativeRegistrationOwnershipTests).Assembly;
        var other = typeof(object).Assembly;
        NativeRegistrationOwnership.ClaimModule(module, owner);

        var ex = Assert.Throws<SwiftLoadContextConflictException>(
            () => NativeRegistrationOwnership.ClaimModule(module, other));

        Assert.Same(owner, NativeRegistrationOwnership.GetModuleOwner(module));
        Assert.Contains(module, ex.Registration);
        Assert.Contains(owner.FullName!, ex.Message);
        Assert.Contains(other.FullName!, ex.Message);
        Assert.Contains("\"Default\"", ex.OwnerLoadContext);
        Assert.Contains("\"Default\"", ex.RefusedLoadContext);
        Assert.IsAssignableFrom<SwiftRuntimeException>(ex);
    }

    [Fact]
    public void TheOwnerTableIsProcessWideAppContextData()
    {
        var module = UniqueModule();
        NativeRegistrationOwnership.ClaimModule(module, typeof(NativeRegistrationOwnershipTests).Assembly);

        // Every copy of Swift.Runtime in every load context reads the same CoreLib-owned table.
        var table = Assert.IsType<Dictionary<string, Assembly>>(
            AppContext.GetData(NativeRegistrationOwnership.OwnerTableKey));
        lock (table)
            Assert.Same(typeof(NativeRegistrationOwnershipTests).Assembly, table["module:" + module]);
    }

    [Fact]
    public void ThisRuntimeOwnsTheRuntimeRegistrations()
    {
        // The module initializer claimed them before its first native call.
        _ = NativeRegistrationOwnership.GetModuleOwner(UniqueModule());
        var table = (Dictionary<string, Assembly>)AppContext.GetData(NativeRegistrationOwnership.OwnerTableKey)!;
        lock (table)
            Assert.Same(typeof(NativeRegistrationOwnership).Assembly, table[NativeRegistrationOwnership.RuntimeKey]);
    }

    [Fact]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test host loads an untrimmed runtime copy.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Test host loads an untrimmed runtime copy.")]
    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "The test host is never single-file.")]
    public void APrivateCopyOfTheRuntimeInAnotherLoadContextIsRefusedAtModuleInitialization()
    {
        _ = NativeRegistrationOwnership.GetModuleOwner(UniqueModule());
        var context = new AssemblyLoadContext("private-runtime-copy", isCollectible: true);
        try
        {
            var copy = context.LoadFromAssemblyPath(typeof(NativeRegistrationOwnership).Assembly.Location);
            Assert.NotSame(typeof(NativeRegistrationOwnership).Assembly, copy);

            var getOwner = copy.GetType("Swift.Runtime.NativeRegistrationOwnership", throwOnError: true)!
                .GetMethod(nameof(NativeRegistrationOwnership.GetModuleOwner), BindingFlags.NonPublic | BindingFlags.Static)!;
            var invocation = Assert.Throws<TargetInvocationException>(
                () => getOwner.Invoke(null, [UniqueModule()]));

            var refusal = FindConflict(invocation);
            Assert.NotNull(refusal);
            Assert.Contains("SwiftBindings.Runtime", refusal!.Message);
            Assert.Contains("private-runtime-copy", refusal.Message);
            Assert.Contains("\"Default\"", refusal.Message);

            // The owner is still this context's runtime.
            var table = (Dictionary<string, Assembly>)AppContext.GetData(NativeRegistrationOwnership.OwnerTableKey)!;
            lock (table)
                Assert.Same(typeof(NativeRegistrationOwnership).Assembly, table[NativeRegistrationOwnership.RuntimeKey]);
        }
        finally
        {
            context.Unload();
        }
    }

    // The refusal is the private copy's own exception type, so match it by name.
    private static Exception? FindConflict(Exception? ex)
    {
        for (; ex != null; ex = ex.InnerException)
        {
            if (ex.GetType().FullName == typeof(SwiftLoadContextConflictException).FullName)
                return ex;
        }
        return null;
    }
}
