// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Shared utilities for emitter code — hashing, string manipulation.
/// </summary>
internal static class EmitterUtility
{
    /// <summary>
    /// Computes a deterministic 8-character hex hash from a string using FNV-1a 32-bit.
    /// Unlike string.GetHashCode(), this is stable across processes and platforms.
    /// </summary>
    internal static string DeterministicHash8(string input)
    {
        uint hash = 2166136261u;
        foreach (char c in input)
        {
            hash ^= (uint)c;
            hash *= 16777619u;
        }
        return hash.ToString("X8");
    }

    /// <summary>
    /// Finds the index of the last comma at top-level nesting depth (outside any &lt;&gt; pairs)
    /// within a string, searching backwards from <paramref name="endExclusive"/>.
    /// Returns -1 if no top-level comma is found.
    /// </summary>
    internal static int FindLastTopLevelComma(string s, int endExclusive)
    {
        int depth = 0;
        for (int i = endExclusive - 1; i >= 0; i--)
        {
            switch (s[i])
            {
                case '>': depth++; break;
                case '<': depth--; break;
                case ',' when depth == 0: return i;
            }
        }
        return -1;
    }
}
