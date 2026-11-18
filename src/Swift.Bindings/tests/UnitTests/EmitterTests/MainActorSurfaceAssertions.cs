// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Reads a generated C# member back for the isolation surface every main-actor member carries: the
/// <c>[SwiftMainActor]</c> marker directly above its signature and the Debug main-thread guard as
/// its body's first statement.
/// </summary>
internal static class MainActorSurfaceAssertions
{
    internal static void AssertCSharpMainActorSurface(string csOutput, string signatureToken, bool expectMainActor)
    {
        var lines = csOutput.Replace("\r\n", "\n").Split('\n');
        var at = System.Array.FindIndex(lines, l => l.Contains(signatureToken)
            && !l.Contains(" extern ") && !l.Contains("partial IntPtr") && !l.TrimStart().StartsWith("//"));
        Assert.True(at >= 0, csOutput);
        Assert.Equal(expectMainActor, at > 0 && lines[at - 1].Trim() == "[global::Swift.Runtime.SwiftMainActor]");
        var firstStatement = lines.Skip(at + 1).Select(l => l.Trim()).First(l => l is not ("{" or "get"));
        Assert.Equal(expectMainActor, firstStatement == "global::Swift.Runtime.MainActorGuard.AssertMainThread();");
        // The remark belongs to this member: it sits in the doc and attribute block directly above it.
        var header = lines.Take(at).Reverse().Select(l => l.Trim())
            .TakeWhile(l => l.StartsWith("///") || l.StartsWith("["));
        Assert.Equal(expectMainActor, header.Any(l => l.Contains("Maps to a Swift <c>@MainActor</c>-isolated declaration")));
    }
}
