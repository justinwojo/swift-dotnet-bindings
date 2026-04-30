// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Best-effort Swift source position for a fact extracted from a <c>.swiftinterface</c>
/// file. Renders as the standard <c>path:line:col:</c> prefix in human diagnostics
/// (matches <c>clang</c>, <c>swiftc</c>, MSBuild). Lines and columns are 1-based.
/// <para/>
/// Emitted only where the regex parser can attribute a fact to a specific match offset.
/// Facts derived solely from ABI JSON, synthesized declarations, and dependency modules
/// without a swiftinterface input have no position — callers represent that as a
/// nullable <see cref="SourcePosition"/> rather than fabricating a fake location.
/// </summary>
public readonly record struct SourcePosition(string FilePath, int Line, int Column)
{
    /// <summary>
    /// Renders the standard <c>path:line:col:</c> prefix (with trailing space) for
    /// human-readable diagnostic messages. Returns an empty string when
    /// <paramref name="position"/> is <c>null</c>, so callers can prepend it
    /// unconditionally.
    /// </summary>
    public static string FormatPrefix(SourcePosition? position) =>
        position is { } pos ? $"{pos.FilePath}:{pos.Line}:{pos.Column}: " : string.Empty;

    public override string ToString() => $"{FilePath}:{Line}:{Column}";
}
