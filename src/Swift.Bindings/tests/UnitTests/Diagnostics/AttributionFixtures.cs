// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

using BindingsGeneration.Diagnostics;

namespace BindingsGeneration.Tests;

/// <summary>
/// Loads the recorded wrapper-compile fixtures — the genericized wrapper source and the real swiftc
/// stderr captured from compiling it — and builds the small identity graph the attribution steps
/// need to resolve a symbol to a unit.
/// </summary>
/// <remarks>
/// The stderr files are genuine swiftc output, captured once by compiling the sibling
/// <c>*.wrapper.swift</c> file with <c>swiftc -emit-library -parse-as-library</c>. They are checked
/// in so the parser and attributor are tested against the compiler's real textual shape — the gutter
/// lines, the restated <c>`- error:</c> carets, the synthetic-location notes — not a hand-idealized
/// approximation of it.
/// </remarks>
internal static class AttributionFixtures
{
    /// <summary>Reads a fixture's captured swiftc stderr.</summary>
    public static string Stderr(string fixtureName) => File.ReadAllText(Path(fixtureName + ".stderr.txt"));

    /// <summary>Reads a fixture's wrapper source.</summary>
    public static string Source(string fixtureName) => File.ReadAllText(Path(fixtureName + ".wrapper.swift"));

    private static string Path(string leaf)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(System.IO.Path.Combine(dir.FullName, "SwiftBindings.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var path = System.IO.Path.Combine(
            dir!.FullName, "src", "Swift.Bindings", "tests", "UnitTests", "Diagnostics", "Fixtures", leaf);
        Assert.True(File.Exists(path), $"fixture not found: {path}");
        return path;
    }

    /// <summary>
    /// A deterministic identity for a wrapper symbol: one <see cref="DeclId"/> per distinct symbol,
    /// its <see cref="ArtifactRole.SwiftWrapper"/> artifact, and its <see cref="RecoveryScope.LeafApi"/>
    /// unit. Distinct symbols yield distinct decls, so distinct units — which is what lets the
    /// cascade-collapse and two-culprit assertions mean something.
    /// </summary>
    public static DeclId DeclForSymbol(string symbol) =>
        DeclId.Create("Fixture", declPath: null, BindingItemKind.Method, symbol);

    public static ArtifactId ArtifactForSymbol(string symbol) =>
        ArtifactId.Create(DeclForSymbol(symbol), ArtifactRole.SwiftWrapper);

    public static RecoveryUnitId UnitForSymbol(string symbol) =>
        RecoveryUnitId.Create(DeclForSymbol(symbol), RecoveryScope.LeafApi);

    /// <summary>
    /// The symbol→artifact and artifact→unit resolvers a symbol-anchor step needs, wired to the
    /// deterministic identity above. A symbol the fixture never declares resolves to null.
    /// </summary>
    public static SymbolAnchorProvenanceStep SymbolStep(string source)
    {
        var index = WrapperBlockIndex.Build(source);
        return new SymbolAnchorProvenanceStep(
            index,
            symbol => ArtifactForSymbol(symbol),
            SymbolUnitLookup());
    }

    /// <summary>
    /// An artifact→unit resolver that recovers the symbol from the artifact's declaration name — the
    /// inverse of <see cref="DeclForSymbol"/>. Used by both the symbol and origin-anchor paths.
    /// </summary>
    public static Func<ArtifactId, RecoveryUnitId?> SymbolUnitLookup() =>
        artifact => RecoveryUnitId.Create(artifact.Decl, RecoveryScope.LeafApi);
}
