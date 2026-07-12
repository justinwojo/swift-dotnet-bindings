// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="ModuleFileSplitter"/> — the pure repackaging of a module's
/// combined C# output into one file per top-level type. These lock the properties that
/// make the split a zero-public-API-change operation: reassembly identity (every byte of
/// the combined output lands in exactly one file's distinct region), per-type isolation,
/// determinism across runs, and case-insensitive filename disambiguation (macOS/APFS).
/// The splitter is deliberately I/O-free so it can be exercised directly here.
/// </summary>
public class ModuleFileSplitterTests
{
    private const string Namespace = "TestNs";

    // A synthetic combined module output assembled from named parts so every boundary
    // offset is computed from part lengths (no hand-counted magic numbers).
    private sealed record Fixture(
        string Combined,
        int BodyStart,
        int BodyEnd,
        int CloseEnd,
        List<(string TypeName, int Start, int End)> Spans);

    private static Fixture BuildFixture()
    {
        const string header = "using System;\n\nnamespace TestNs\n{\n";
        const string spanAlpha = "    public class Alpha { public int A; }\n";
        const string interstitial = "    // module-level free functions\n    // (prelude content)\n";
        const string spanBeta = "    public struct Beta { public long B; }\n";
        const string trailer = "    // module trailer\n";
        const string close = "}\n";

        int bodyStart = header.Length;
        int alphaStart = bodyStart;
        int alphaEnd = alphaStart + spanAlpha.Length;
        int interstitialEnd = alphaEnd + interstitial.Length;
        int betaStart = interstitialEnd;
        int betaEnd = betaStart + spanBeta.Length;
        int bodyEnd = betaEnd + trailer.Length;
        int closeEnd = bodyEnd + close.Length;

        var combined = header + spanAlpha + interstitial + spanBeta + trailer + close;
        var spans = new List<(string, int, int)>
        {
            ("Alpha", alphaStart, alphaEnd),
            ("Beta", betaStart, betaEnd),
        };
        return new Fixture(combined, bodyStart, bodyEnd, closeEnd, spans);
    }

    private static IReadOnlyList<ModuleFileSplitter.SplitFile> BuildFileSet(Fixture f, Func<string, string> qualify = null)
    {
        var files = ModuleFileSplitter.BuildFileSet(
            f.Combined, Namespace, f.BodyStart, f.BodyEnd, f.CloseEnd, f.Spans, qualify ?? (s => s));
        Assert.NotNull(files);
        return files;
    }

    [Fact]
    public void BuildFileSet_ProducesPreludePlusOneFilePerType()
    {
        var files = BuildFileSet(BuildFixture());

        Assert.Equal(3, files.Count);
        Assert.Equal($"{Namespace}.cs", files[0].FileName);
        Assert.Equal($"{Namespace}.Types.Alpha.cs", files[1].FileName);
        Assert.Equal($"{Namespace}.Types.Beta.cs", files[2].FileName);
    }

    [Fact]
    public void BuildFileSet_PreludeIsCombinedWithTypeSpansCutOut()
    {
        var f = BuildFixture();
        var files = BuildFileSet(f);
        var prelude = files[0].Content;

        // The prelude is the combined output minus every type body. Rebuild that expectation
        // by removing each span's byte-range (longest-last so earlier offsets stay valid).
        var expected = f.Combined;
        foreach (var s in f.Spans.OrderByDescending(s => s.Start))
            expected = expected.Remove(s.Start, s.End - s.Start);

        Assert.Equal(expected, prelude);

        // And no type body leaked into the prelude.
        Assert.DoesNotContain("class Alpha", prelude);
        Assert.DoesNotContain("struct Beta", prelude);
    }

    [Fact]
    public void BuildFileSet_EachTypeFileIsHeaderPlusOwnSpanPlusClose()
    {
        var f = BuildFixture();
        var files = BuildFileSet(f);

        var header = f.Combined.Substring(0, f.BodyStart);
        var close = f.Combined.Substring(f.BodyEnd, f.CloseEnd - f.BodyEnd);

        var alpha = f.Spans[0];
        var beta = f.Spans[1];

        Assert.Equal(header + f.Combined.Substring(alpha.Start, alpha.End - alpha.Start) + close, files[1].Content);
        Assert.Equal(header + f.Combined.Substring(beta.Start, beta.End - beta.Start) + close, files[2].Content);
    }

    [Fact]
    public void BuildFileSet_PerTypeBodyAppearsInExactlyOneTypeFile()
    {
        var files = BuildFileSet(BuildFixture());

        // Each type's own declaration text lives only in its file, never in a sibling.
        Assert.Contains("class Alpha", files[1].Content);
        Assert.DoesNotContain("struct Beta", files[1].Content);

        Assert.Contains("struct Beta", files[2].Content);
        Assert.DoesNotContain("class Alpha", files[2].Content);
    }

    [Fact]
    public void BuildFileSet_IsDeterministicAcrossRuns()
    {
        var a = BuildFileSet(BuildFixture());
        var b = BuildFileSet(BuildFixture());

        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].FileName, b[i].FileName);
            Assert.Equal(a[i].Content, b[i].Content);
        }
    }

    [Fact]
    public void BuildFileSet_QualifyRunsOnEveryEmittedFile()
    {
        var files = BuildFileSet(BuildFixture(), qualify: s => "// Q\n" + s);

        Assert.All(files, file => Assert.StartsWith("// Q\n", file.Content));
    }

    [Fact]
    public void BuildFileSet_CaseInsensitiveLeafCollision_DisambiguatesInEmissionOrder()
    {
        // Two top-level types whose leaf names differ only by case must not collide on a
        // case-insensitive filesystem (macOS/APFS). The first keeps the plain name; the
        // second is suffixed, in deterministic emission (start-offset) order.
        const string header = "namespace TestNs\n{\n";
        const string spanFoo = "    public class Foo { }\n";
        const string spanFOO = "    public class FOO { }\n";
        const string close = "}\n";

        int bodyStart = header.Length;
        int fooStart = bodyStart;
        int fooEnd = fooStart + spanFoo.Length;
        int fooUpperStart = fooEnd;
        int fooUpperEnd = fooUpperStart + spanFOO.Length;
        int bodyEnd = fooUpperEnd;
        int closeEnd = bodyEnd + close.Length;

        var combined = header + spanFoo + spanFOO + close;
        var spans = new List<(string, int, int)>
        {
            ("Foo", fooStart, fooEnd),
            ("FOO", fooUpperStart, fooUpperEnd),
        };

        var files = ModuleFileSplitter.BuildFileSet(
            combined, Namespace, bodyStart, bodyEnd, closeEnd, spans, s => s);

        Assert.NotNull(files);
        Assert.Equal($"{Namespace}.cs", files[0].FileName);
        Assert.Equal($"{Namespace}.Types.Foo.cs", files[1].FileName);
        Assert.Equal($"{Namespace}.Types.FOO_2.cs", files[2].FileName);

        // Distinct file names even under OrdinalIgnoreCase comparison.
        var names = files.Select(x => x.FileName).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData(true)]  // no spans
    [InlineData(false)] // missing boundary offsets
    public void BuildFileSet_NotSliceable_ReturnsNull(bool emptySpans)
    {
        var f = BuildFixture();
        var spans = emptySpans ? new List<(string, int, int)>() : f.Spans;
        int? bodyStart = emptySpans ? f.BodyStart : (int?)null;

        var files = ModuleFileSplitter.BuildFileSet(
            f.Combined, Namespace, bodyStart, f.BodyEnd, f.CloseEnd, spans, s => s);

        Assert.Null(files);
    }

    [Fact]
    public void BuildFileSet_SpanOutsideNamespaceBody_ReturnsNull()
    {
        var f = BuildFixture();
        // Corrupt a span so it runs past the namespace body end — must fail closed to null
        // (caller then writes the single combined file, identical to pre-split behavior).
        var badSpans = new List<(string, int, int)>(f.Spans) { ("Rogue", f.BodyEnd, f.CloseEnd) };

        var files = ModuleFileSplitter.BuildFileSet(
            f.Combined, Namespace, f.BodyStart, f.BodyEnd, f.CloseEnd, badSpans, s => s);

        Assert.Null(files);
    }
}
