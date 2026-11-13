// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Asserts the calling convention emitted C# P/Invokes are called with, read the way the
/// generator's own ABI contract checker reads it. The generator writes
/// <c>[UnmanagedCallConv]</c> only for the Swift convention, so a C-convention import carries
/// no attribute; these assertions hold whichever way the C convention is spelled.
/// </summary>
internal static class PInvokeConventionAssert
{
    /// <summary>
    /// Every P/Invoke in <paramref name="csOutput"/> whose entry point starts with
    /// <paramref name="entryPointPrefix"/> (every P/Invoke, when it is empty) is called with the
    /// C convention, and there is at least one.
    /// </summary>
    public static void CConvention(string csOutput, string entryPointPrefix = "")
    {
        var matches = AbiContractChecker.ExtractPInvokes(csOutput, "TestModule")
            .Where(p => p.EntryPoint.StartsWith(entryPointPrefix, StringComparison.Ordinal))
            .ToList();
        Assert.True(matches.Count > 0, $"No P/Invoke whose entry point starts with '{entryPointPrefix}' in:\n{csOutput}");
        Assert.All(matches, p => Assert.True(p.CallingConvention == "CallConvCdecl",
            $"{p.MethodName} ({p.EntryPoint}) is called with {p.CallingConvention}, not the C convention"));
    }

    /// <summary>At least one P/Invoke in <paramref name="csOutput"/> is called with the C convention.</summary>
    public static void AnyCConvention(string csOutput) =>
        Assert.True(
            AbiContractChecker.ExtractPInvokes(csOutput, "TestModule").Any(p => p.CallingConvention == "CallConvCdecl"),
            $"No C-convention P/Invoke in:\n{csOutput}");
}
