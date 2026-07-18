// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Pins the assumption every regenerate-from-plan mechanism rests on: emission is a pure function
/// of (frozen TypeDatabase, decl tree). Re-running it over the same inputs must produce a
/// byte-identical output set — same file names, same bytes — or a diagnostic attributed against
/// one render would be applied to a different one, and a "discard the attempt and re-emit"
/// recovery would silently change unrelated output.
///
/// <para>These run the whole <see cref="StringEmitter.EmitModule"/> path (pre-passes, handler tree,
/// namespace qualification, the file-per-type split, the manifest/surface writers and the Swift
/// wrapper file) into a scratch directory, twice, <b>in one process</b>. In-process is the mode
/// that matters: a two-process CLI double-run cannot see leftover static or <c>AsyncLocal</c>
/// state bleeding from one emission into the next, which is exactly what a re-emission loop
/// living inside a single generator run would hit.</para>
/// </summary>
public class EmissionDeterminismTests : IDisposable
{
    private readonly List<string> _scratchDirs = new();

    public void Dispose()
    {
        foreach (var dir in _scratchDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public void EmitModule_RunTwiceInProcess_ProducesByteIdenticalOutputSet()
    {
        var first = EmitFixtureModule();
        var second = EmitFixtureModule();

        AssertOutputSetsIdentical(first, second);
    }

    /// <summary>
    /// The harsher shape: a different module is emitted between the two runs, so any static or
    /// <c>AsyncLocal</c> registry that accumulates across emissions (rather than being rebuilt per
    /// module) carries foreign state into the second run. A plain back-to-back double-emit can miss
    /// that when the leaked state happens to be a no-op for the same inputs.
    /// </summary>
    [Fact]
    public void EmitModule_RunTwiceAcrossAnInterveningModule_ProducesByteIdenticalOutputSet()
    {
        var first = EmitFixtureModule();
        EmitFixtureModule(moduleName: "InterveningModule");
        var second = EmitFixtureModule();

        AssertOutputSetsIdentical(first, second);
    }

    /// <summary>
    /// Guards the two tests above from passing vacuously. Byte-identity over an empty or trivial
    /// output set proves nothing, so assert the fixture really did drive the machinery whose
    /// ordering and name allocation is the plausible nondeterminism source: a protocol (proxy +
    /// witness dispatch), generics, closures, async, and enough members to make collision-suffix
    /// allocation and dedup registries matter.
    /// </summary>
    [Fact]
    public void FixtureModule_ExercisesTheMachineryTheDeterminismGateDependsOn()
    {
        var output = EmitFixtureModule();

        var combinedCSharp = string.Concat(output
            .Where(f => f.Key.EndsWith(".cs", StringComparison.Ordinal))
            .OrderBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => f.Value));
        var wrapper = output.Single(f => f.Key.EndsWith(".Wrapper.swift", StringComparison.Ordinal)).Value;

        Assert.True(output.Count >= 3, $"expected a multi-file output set, got {output.Count}");
        Assert.True(combinedCSharp.Length > 4000, $"C# output is too small to be meaningful ({combinedCSharp.Length} chars)");
        Assert.True(wrapper.Length > 500, $"Swift wrapper is too small to be meaningful ({wrapper.Length} chars)");

        // The fixture's shapes must have survived to emission, not been skipped wholesale.
        Assert.Contains("interface IShapeSink", combinedCSharp, StringComparison.Ordinal);      // protocol interface + proxy
        Assert.Contains("LibraryImport", combinedCSharp, StringComparison.Ordinal);             // native call surface
        Assert.Contains("Task<", combinedCSharp, StringComparison.Ordinal);                     // async bridge
        Assert.Contains("@_cdecl", wrapper, StringComparison.Ordinal);                          // Swift wrapper blocks

        // The collision-suffix allocator is the name-allocation machinery most likely to be
        // order-sensitive, so the gate is only meaningful if the fixture actually forced a rename.
        Assert.Contains("Register2", combinedCSharp, StringComparison.Ordinal);
    }

    // ── harness ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Emits the fixture module through the real <see cref="StringEmitter"/> into a fresh scratch
    /// directory and returns every file it wrote as name → content. Each call rebuilds the decl
    /// tree, the TypeDatabase, the <see cref="ModuleEmissionContext"/> and the report session —
    /// the same "fresh everything, same inputs" shape a re-emission attempt would use. Anything
    /// that survives across calls is ambient process state, which is what these tests hunt.
    /// </summary>
    private Dictionary<string, string> EmitFixtureModule(string moduleName = "DeterminismFixture")
    {
        var scratch = Path.Combine(Path.GetTempPath(), "swiftbind-determinism-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        _scratchDirs.Add(scratch);

        var moduleDecl = FixtureModuleFactory.BuildModule(moduleName);
        var typeDatabase = FixtureModuleFactory.BuildTypeDatabase(moduleDecl);

        // Mirror the module-boundary resets the CLI performs immediately around emission. Only
        // resets production already does belong here — adding one it does not would hide a real
        // leak rather than expose it.
        ReportCollector.Reset();
        ReportCollector.Start(moduleDecl);
        AppleSupplementReferences.Reset();
        try
        {
            var emitter = new StringEmitter(scratch, typeDatabase, new NullLoggerFactory());
            emitter.EmitModule(moduleDecl, new ModuleEmissionContext());
        }
        finally
        {
            ReportCollector.Complete();
            ReportCollector.Reset();
        }

        return Directory.EnumerateFiles(scratch)
            .ToDictionary(Path.GetFileName!, File.ReadAllText, StringComparer.Ordinal);
    }

    private static void AssertOutputSetsIdentical(
        Dictionary<string, string> first, Dictionary<string, string> second)
    {
        Assert.Equal(
            first.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray(),
            second.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());

        foreach (var name in first.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (string.Equals(first[name], second[name], StringComparison.Ordinal))
                continue;

            Assert.Fail(
                $"'{name}' differs between two emissions of the same inputs.{Environment.NewLine}" +
                DescribeFirstDifference(first[name], second[name]));
        }
    }

    /// <summary>
    /// Renders the first differing region of two renders. A raw full-file diff of generated output
    /// is unreadable in a test failure; the offset plus a window around it is what actually points
    /// at the nondeterministic emitter.
    /// </summary>
    private static string DescribeFirstDifference(string a, string b)
    {
        var limit = Math.Min(a.Length, b.Length);
        var offset = 0;
        while (offset < limit && a[offset] == b[offset])
            offset++;

        const int window = 160;
        var start = Math.Max(0, offset - window / 2);
        string Window(string s) => s.Substring(start, Math.Min(window, s.Length - start)).Replace("\n", "\\n");

        return $"  first difference at char {offset} (lengths {a.Length} vs {b.Length}){Environment.NewLine}" +
               $"  run 1: …{Window(a)}…{Environment.NewLine}" +
               $"  run 2: …{Window(b)}…";
    }

}
