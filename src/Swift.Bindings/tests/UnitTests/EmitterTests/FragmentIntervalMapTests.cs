// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Property gate on the fragment interval maps a full render publishes. These run the real
/// <see cref="StringEmitter.EmitModule"/> path — handler tree, namespace qualification, the
/// file-per-type split, the Swift wrapper — and then assert the published map against the bytes
/// that were actually written to disk.
///
/// <para>Checking against the files rather than against the emitter's own buffers is the whole
/// point: every pass between emission and disk (qualification, the split) shifts offsets, and a map
/// that agrees with the pre-pass buffer while disagreeing with the file is precisely the drift this
/// subsystem exists to make impossible.</para>
/// </summary>
public class FragmentIntervalMapTests : IDisposable
{
    private readonly List<string> _scratchDirs = new();

    public void Dispose()
    {
        foreach (var dir in _scratchDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    /// <summary>
    /// The core property: an interval's recorded text is exactly the slice of the rendered file it
    /// claims. If this holds for every interval of every file, an offset resolved through the map
    /// names the artifact that really produced those characters.
    /// </summary>
    [Fact]
    public void EveryInterval_SlicesOutExactlyItsOwnFragmentText()
    {
        var render = Render();

        foreach (var (fileName, map) in render.FragmentSet.Files)
        {
            var content = render.Files[fileName];
            foreach (var interval in map.Intervals)
            {
                Assert.True(
                    interval.Start >= 0 && interval.End <= content.Length && interval.Start < interval.End,
                    $"{fileName}: interval [{interval.Start},{interval.End}) is out of bounds for {content.Length} chars");

                var slice = content[interval.Start..interval.End];
                if (!string.Equals(slice, interval.Fragment.Text, StringComparison.Ordinal))
                {
                    Assert.Fail(
                        $"{fileName}: interval [{interval.Start},{interval.End}) owned by "
                        + $"{interval.Fragment.Owner.Artifact.Canonical} does not match the file."
                        + Environment.NewLine + $"  file : {Preview(slice)}"
                        + Environment.NewLine + $"  frag : {Preview(interval.Fragment.Text)}");
                }
            }
        }
    }

    /// <summary>
    /// The map must be a total tiling — contiguous from zero to the end of the file. A gap would
    /// leave a diagnostic in it unattributable while looking like a resolvable position, which is
    /// worse than the file being unmapped outright.
    /// </summary>
    [Fact]
    public void EveryMappedFile_IsTiledContiguouslyFromZeroToItsEnd()
    {
        var render = Render();

        foreach (var (fileName, map) in render.FragmentSet.Files)
        {
            var expectedStart = 0;
            foreach (var interval in map.Intervals)
            {
                Assert.Equal(expectedStart, interval.Start);
                expectedStart = interval.End;
            }
            Assert.Equal(render.Files[fileName].Length, expectedStart);
            Assert.Equal(render.Files[fileName].Length, map.Length);
        }
    }

    /// <summary>
    /// Guards the two properties above from passing vacuously: they are trivially true over an empty
    /// map, and a file recorded unmapped is excluded from both. A render of this fixture must map
    /// every source file it wrote, across both planes, with real artifact breadth.
    /// </summary>
    [Fact]
    public void Render_PublishesANonVacuousMapForEverySourceFileItWrote()
    {
        var render = Render();

        Assert.Empty(render.FragmentSet.UnmappedFiles);

        var sourceFiles = render.Files.Keys
            .Where(IsMappableSource)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(sourceFiles);
        Assert.Equal(sourceFiles, render.FragmentSet.Files.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());

        var fragments = render.FragmentSet.AllFragments.ToList();
        Assert.Contains(fragments, f => f.Plane == OutputPlane.CSharp);
        Assert.Contains(fragments, f => f.Plane == OutputPlane.Swift);
        Assert.Contains(fragments, f => f.IsWholeScope);

        // Breadth: the fixture's types and members must have produced distinct owned scopes, not one
        // module-sized fragment per file. Ten is well under what the fixture emits and well over
        // what a collapsed-to-the-root map would show.
        Assert.True(
            render.FragmentSet.EmittedArtifacts.Count >= 10,
            $"expected many distinct emitted artifacts, got {render.FragmentSet.EmittedArtifacts.Count}");
    }

    [Fact]
    public void TryResolve_AtEveryIntervalBoundary_ReturnsThatIntervalsFragment()
    {
        var render = Render();

        foreach (var map in render.FragmentSet.Files.Values)
        {
            foreach (var interval in map.Intervals)
            {
                Assert.True(map.TryResolve(interval.Start, out var atStart));
                Assert.Same(interval.Fragment, atStart);

                Assert.True(map.TryResolve(interval.End - 1, out var atLast));
                Assert.Same(interval.Fragment, atLast);
            }

            Assert.False(map.TryResolve(-1, out _));
            Assert.False(map.TryResolve(map.Length, out _));
        }
    }

    /// <summary>
    /// Every <c>@_cdecl</c> entry point in the finished wrapper must resolve to a fragment owned by
    /// a real declaration. This is the wrapper plane's end-to-end property: the symbols are the
    /// points a swiftc diagnostic actually lands on, so a symbol the map cannot attribute is a
    /// diagnostic that cannot be routed to the artifact that must be withdrawn.
    ///
    /// <para>Where the wrapper-symbol registry independently recorded an owner for a symbol, the two
    /// derivations are cross-checked against each other. That arm is currently latent: owner
    /// threading through the registration call sites is partial, so a render can register symbols
    /// with no owner attached. It is written to engage automatically as threading completes rather
    /// than needing to be remembered then.</para>
    /// </summary>
    [Fact]
    public void EveryWrapperEntryPoint_ResolvesToARealOwnerAndAgreesWithTheSymbolRegistry()
    {
        var render = Render();

        var wrapperFile = render.FragmentSet.Files
            .Single(kv => kv.Key.EndsWith(".Wrapper.swift", StringComparison.Ordinal));
        var content = render.Files[wrapperFile.Key];

        var entryPoints = 0;
        foreach (Match match in Regex.Matches(content, @"@_cdecl\(""(?<symbol>[^""]+)""\)"))
        {
            var symbol = match.Groups["symbol"].Value;
            entryPoints++;

            Assert.True(
                wrapperFile.Value.TryResolve(match.Index, out var fragment),
                $"no fragment covers @_cdecl(\"{symbol}\") at offset {match.Index}");
            Assert.Equal(OutputPlane.Swift, fragment.Plane);
            Assert.NotEqual(default, fragment.Owner.Artifact.Decl);
            Assert.False(
                string.IsNullOrEmpty(fragment.Owner.Artifact.Decl.Canonical),
                $"@_cdecl(\"{symbol}\") resolved to a fragment with no owning declaration");

            if (render.Context.TryGetWrapperSymbolOwner(symbol, out var registeredOwner))
            {
                Assert.True(
                    fragment.Owner.Artifact.Decl == registeredOwner.Decl,
                    $"@_cdecl(\"{symbol}\") sits in a fragment owned by "
                    + $"{fragment.Owner.Artifact.Decl.Canonical}, but the symbol registry attributes it "
                    + $"to {registeredOwner.Decl.Canonical}");
            }
        }

        Assert.True(entryPoints > 0, "the wrapper declared no @_cdecl entry points; the gate is vacuous");
    }

    /// <summary>
    /// swiftc reports columns as UTF-8 byte counts while the map is indexed in UTF-16 characters.
    /// The two agree on pure-ASCII lines, which is almost every generated line — so a test that only
    /// uses generated source cannot tell the units apart. This one builds a line where they
    /// genuinely disagree and pins that each reading resolves through its own unit.
    /// </summary>
    [Fact]
    public void TryResolveUtf8Column_OnANonAsciiLine_DisagreesWithTheCharacterColumnReading()
    {
        // "α β γ " is 6 characters but 9 UTF-8 bytes, so a position swiftc calls column 10 is
        // character 7 — squarely inside the first fragment, while character column 10 is not.
        const string content = "α β γ XYZabcdef\n";
        var first = Fragment(content[..8], "first");
        var second = Fragment(content[8..], "second");

        var set = new ModuleFragmentSet { ModuleName = "Fixture" };
        set.Add("Sample.swift", content, new List<FragmentInterval>
        {
            new(first, 0, 8),
            new(second, 8, content.Length),
        });
        var map = set.Files["Sample.swift"];

        Assert.True(map.TryResolveUtf8Column(line: 1, utf8Column: 10, out var byByte));
        Assert.Same(first, byByte);

        Assert.True(map.TryResolve(line: 1, column: 10, out var byChar));
        Assert.Same(second, byChar);
    }

    /// <summary>
    /// A file whose intervals do not tile it exactly is recorded unmapped rather than published.
    /// Downstream cannot distinguish an approximate map from an exact one, so an approximate one is
    /// strictly more dangerous than none.
    /// </summary>
    [Fact]
    public void Add_WithIntervalsThatDoNotTileTheContent_RecordsTheFileUnmapped()
    {
        const string content = "0123456789";
        var set = new ModuleFragmentSet { ModuleName = "Fixture" };

        set.Add("Gap.cs", content, new List<FragmentInterval>
        {
            new(Fragment(content[..3], "a"), 0, 3),
            new(Fragment(content[6..], "b"), 6, content.Length),   // [3,6) is uncovered
        });

        Assert.Equal(new[] { "Gap.cs" }, set.UnmappedFiles.ToArray());
        Assert.Empty(set.Files);
    }

    [Fact]
    public void Add_WithNullIntervals_RecordsTheFileUnmapped()
    {
        var set = new ModuleFragmentSet { ModuleName = "Fixture" };

        set.Add("Unknown.cs", "irrelevant", intervals: null);

        Assert.Equal(new[] { "Unknown.cs" }, set.UnmappedFiles.ToArray());
        Assert.Empty(set.Files);
    }

    // ── harness ────────────────────────────────────────────────────────────────────────────

    private sealed record RenderResult(
        Dictionary<string, string> Files, ModuleFragmentSet FragmentSet, ModuleEmissionContext Context);

    /// <summary>
    /// Emits the shared fixture module into a scratch directory and returns both the files on disk
    /// and the fragment set the render published, so assertions can compare one against the other.
    /// </summary>
    private RenderResult Render(string moduleName = "FragmentFixture")
    {
        var scratch = Path.Combine(Path.GetTempPath(), "swiftbind-fragments-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        _scratchDirs.Add(scratch);

        var moduleDecl = FixtureModuleFactory.BuildModule(moduleName);
        var typeDatabase = FixtureModuleFactory.BuildTypeDatabase(moduleDecl);
        var context = new ModuleEmissionContext();

        ReportCollector.Reset();
        ReportCollector.Start(moduleDecl);
        AppleSupplementReferences.Reset();
        try
        {
            new StringEmitter(scratch, typeDatabase, new NullLoggerFactory()).EmitModule(moduleDecl, context);
        }
        finally
        {
            ReportCollector.Complete();
            ReportCollector.Reset();
        }

        var files = Directory.EnumerateFiles(scratch)
            .ToDictionary(Path.GetFileName!, File.ReadAllText, StringComparer.Ordinal);

        Assert.NotNull(context.FragmentSet);
        return new RenderResult(files, context.FragmentSet!, context);
    }

    /// <summary>
    /// The files a fragment map is expected to cover: generated source. Project files, manifests and
    /// the API surface doc are written by separate writers that never open a fragment scope, and are
    /// deliberately outside this subsystem.
    /// </summary>
    private static bool IsMappableSource(string fileName) =>
        (fileName.EndsWith(".cs", StringComparison.Ordinal) || fileName.EndsWith(".swift", StringComparison.Ordinal))
        && !fileName.EndsWith(".csproj", StringComparison.Ordinal);

    private static OutputFragment Fragment(string text, string name) => new()
    {
        Owner = FragmentOwners.ForModule(DeclIdFactory.ForModule(name)),
        Plane = OutputPlane.Swift,
        Text = text,
        IsWholeScope = true,
        Depth = 0,
    };

    private static string Preview(string text) =>
        (text.Length <= 80 ? text : text[..80] + "…").Replace("\n", "\\n").Replace("\r", "\\r");
}
