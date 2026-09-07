// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BindingsGeneration.Diagnostics;

/// <summary>
/// Joins compiler locations to captured compile inputs. Only producer-registered paths are aliases;
/// sharing a basename never establishes identity. Conflicting aliases resolve to nothing.
/// </summary>
public sealed class CompileInputIdentity
{
    private readonly Dictionary<string, string?> _files = new(StringComparer.Ordinal);
    private readonly string _workingDirectory = Directory.GetCurrentDirectory();

    public CompileInputIdentity(IReadOnlyDictionary<string, IReadOnlyList<string>> pathsByFile)
    {
        ArgumentNullException.ThrowIfNull(pathsByFile);
        foreach (var (file, paths) in pathsByFile)
        {
            foreach (var path in paths)
            {
                var normalized = Normalize(path);
                if (normalized == null)
                    continue;
                if (_files.TryGetValue(normalized, out var existing) && existing != file)
                    _files[normalized] = null;
                else
                    _files[normalized] = file;
            }
        }
    }

    /// <summary>
    /// Registers published inputs, their absolute locations, and explicit project-relative spellings.
    /// A supplied directory is the producer's actual output location, including when it is relative
    /// to the current working directory. Without a directory, names remain literal input aliases.
    /// </summary>
    public static CompileInputIdentity ForFiles(IEnumerable<string> files, string? directory = null)
    {
        var publishedDirectory = directory == null ? null : Path.GetFullPath(directory);
        return new(files.ToDictionary(file => file,
            file => (IReadOnlyList<string>)(directory == null
                ? new[] { file }
                : new[] { file, Path.Combine(directory, file), Path.Combine(publishedDirectory!, file) }),
            StringComparer.Ordinal));
    }

    public bool TryResolve(string? path, out string file)
    {
        file = null!;
        var normalized = Normalize(path);
        if (normalized == null || !_files.TryGetValue(normalized, out var found) || found == null)
            return false;
        file = found;
        return true;
    }

    private string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile)
            path = uri.LocalPath;
        try
        {
            // A project-relative spelling is an explicit alias, not an absolute path in the
            // generator's working directory. Keep those identity domains separate.
            return Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : "relative:" + Path.GetRelativePath(_workingDirectory, Path.GetFullPath(path, _workingDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
