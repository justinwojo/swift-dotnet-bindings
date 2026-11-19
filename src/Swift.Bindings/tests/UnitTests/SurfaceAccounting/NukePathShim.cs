// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.IO;

namespace Nuke.Common.IO;

/// <summary>
/// Test-only stand-in for Nuke's <c>AbsolutePath</c>, the one Nuke type the link-compiled
/// validation manifest, baseline and promotion sources use. It carries only what those files
/// call: string conversions and path joining.
/// </summary>
public sealed record AbsolutePath(string Value)
{
    public static implicit operator AbsolutePath(string value) => new(value);
    public static implicit operator string(AbsolutePath value) => value.Value;
    public static AbsolutePath operator /(AbsolutePath path, string suffix) => new(Path.Combine(path.Value, suffix));
    public override string ToString() => Value;
}
