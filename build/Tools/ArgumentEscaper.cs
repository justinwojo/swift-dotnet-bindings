// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Shared argument escaping for shell process invocations.
/// Used by all tool settings classes to safely quote arguments containing spaces or quotes.
/// </summary>
public static class ArgumentEscaper
{
    /// <summary>
    /// Escapes a single argument for shell invocation.
    /// Wraps in double quotes if the argument contains spaces or quotes.
    /// </summary>
    public static string Escape(string arg)
    {
        if (arg.Contains(' ') || arg.Contains('"'))
            return $"\"{arg.Replace("\"", "\\\"")}\"";
        return arg;
    }

    /// <summary>
    /// Joins a list of arguments into a single escaped string.
    /// </summary>
    public static string Join(IEnumerable<string> args) =>
        string.Join(" ", args.Select(Escape));
}
