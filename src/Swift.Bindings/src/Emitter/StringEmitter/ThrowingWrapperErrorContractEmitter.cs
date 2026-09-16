// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Emits the success-side initialization shared by every synchronous throwing
/// C-callable Swift wrapper. The caller-owned slot may contain bytes from an earlier
/// invocation, so clearing it before the Swift call is part of the wrapper ABI contract.
/// </summary>
internal static class ThrowingWrapperErrorContractEmitter
{
    internal static void EmitInitialization(
        SwiftWriter swiftWriter,
        string errorOutName = "errorOut",
        string indent = "")
    {
        swiftWriter.WriteLine($"{indent}{errorOutName}.pointee = nil");
    }
}
