// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace Swift;

/// <summary>
/// Marks a generated C# type as an opaque Swift type wrapper — all public Swift members
/// were skipped during projection because they use types that cannot be represented in C#.
/// The type can still be used as an opaque handle when passed to or returned from other Swift APIs.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false)]
public sealed class OpaqueSwiftTypeAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OpaqueSwiftTypeAttribute"/> class.
    /// </summary>
    /// <param name="skippedMemberCount">The number of Swift members that could not be projected.</param>
    public OpaqueSwiftTypeAttribute(int skippedMemberCount)
    {
        SkippedMemberCount = skippedMemberCount;
    }

    /// <summary>
    /// Gets the number of Swift members that could not be projected to C#.
    /// </summary>
    public int SkippedMemberCount { get; }
}
