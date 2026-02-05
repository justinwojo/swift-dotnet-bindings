// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Shared utility for emitting the SBW_Utf8Slice struct in Swift.
/// This struct is used for UTF-8 string marshalling between C# and Swift.
/// Both WitnessDispatchEmitter and EnumHandler may need to emit this,
/// so this class ensures it's only emitted once per module.
/// </summary>
public static class Utf8SliceEmitter
{
    /// <summary>
    /// Tracks whether the SBW_Utf8Slice struct has been emitted for this module.
    /// </summary>
    private static bool _emittedForModule = false;

    /// <summary>
    /// Resets the tracking for a new module. Call at the start of each module emission.
    /// </summary>
    public static void ResetForModule()
    {
        _emittedForModule = false;
    }

    /// <summary>
    /// Emits the SBW_Utf8Slice struct if not already emitted for this module.
    /// </summary>
    /// <param name="swiftWriter">The Swift writer to emit to.</param>
    /// <returns>True if the struct was emitted, false if it was already emitted.</returns>
    public static bool EmitIfNeeded(SwiftWriter swiftWriter)
    {
        if (_emittedForModule)
            return false;

        swiftWriter.WriteLines("""
            @frozen
            public struct SBW_Utf8Slice {
                public var ptr: UnsafeMutablePointer<UInt8>
                public var len: Int
            }

            """);
        _emittedForModule = true;
        return true;
    }

    /// <summary>
    /// Checks if the SBW_Utf8Slice struct has already been emitted for this module.
    /// </summary>
    public static bool IsEmitted => _emittedForModule;
}
