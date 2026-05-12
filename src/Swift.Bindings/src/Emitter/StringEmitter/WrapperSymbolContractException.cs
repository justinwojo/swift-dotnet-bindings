// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Signals that binding-emit attempted to emit a P/Invoke whose entry point matches
/// the wrapper-symbol naming convention (SBW_…) but was never registered by
/// wrapper-emit. Thrown from <see cref="PInvokeEmitHelper"/> when contract enforcement
/// is enabled. Callers convert this into a <see cref="SkipReason.MissingWrapperSymbol"/>
/// member skip; the C# member is therefore never written, and downstream cogating
/// removes any forwarder code that would have referenced the absent P/Invoke.
/// </summary>
public sealed class WrapperSymbolContractException : Exception
{
    /// <summary>The wrapper symbol the P/Invoke was about to target.</summary>
    public string EntryPoint { get; }

    /// <summary>The C# P/Invoke method name that was about to be emitted.</summary>
    public string MethodName { get; }

    public WrapperSymbolContractException(string entryPoint, string methodName)
        : base($"Wrapper-symbol contract violation: P/Invoke '{methodName}' targets entry point " +
               $"'{entryPoint}' but no wrapper-emit path registered that symbol for the current module.")
    {
        EntryPoint = entryPoint;
        MethodName = methodName;
    }
}
