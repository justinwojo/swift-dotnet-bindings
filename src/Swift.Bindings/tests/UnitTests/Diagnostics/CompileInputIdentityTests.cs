// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.IO;
using BindingsGeneration.Diagnostics;
using Xunit;

namespace BindingsGeneration.Tests;

public class CompileInputIdentityTests
{
    [Theory]
    [InlineData("/generated/Owned.swift", true)]
    [InlineData("/generated/.wrapper-build/Owned.swift", true)]
    [InlineData("/generated/.wrapper-build/../Owned.swift", true)]
    [InlineData("file:///generated/Owned.swift", true)]
    [InlineData("/foreign/Owned.swift", false)]
    [InlineData("/foreign/.wrapper-build/Owned.swift", false)]
    [InlineData("/generated/Owned.swiftinterface", false)]
    [InlineData("Owned.swift", false)]
    public void ExplicitAliasesPreserveInputIdentity(string diagnosticPath, bool expected)
    {
        var inputs = Inputs();
        Assert.Equal(expected, inputs.TryResolve(diagnosticPath, out var file));
        if (expected)
            Assert.Equal("Owned.swift", file);
    }

    [Fact]
    public void ProjectRelativeAliasDoesNotAuthorizeAnAbsoluteFileInTheGeneratorsDirectory()
    {
        var inputs = CompileInputIdentity.ForFiles(new[] { "Owned.cs" }, "/generated");
        Assert.True(inputs.TryResolve("./Owned.cs", out _));
        Assert.True(inputs.TryResolve("/generated/Owned.cs", out _));
        Assert.False(inputs.TryResolve(Path.GetFullPath("Owned.cs"), out _));
    }

    [Theory]
    [InlineData("out/generated")]
    [InlineData("obj/Debug/net10.0-ios/swift-binding/")]
    [InlineData("out/staging/../generated")]
    public void PublishedRelativeDirectoryRegistersItsAbsoluteCompilerLocation(string directory)
    {
        var inputs = CompileInputIdentity.ForFiles(new[] { "Owned.cs" }, directory);
        var published = Path.GetFullPath(Path.Combine(directory, "Owned.cs"));
        Assert.True(inputs.TryResolve(published, out var file));
        Assert.Equal("Owned.cs", file);
        Assert.True(inputs.TryResolve(new System.Uri(published).AbsoluteUri, out _));
        Assert.True(inputs.TryResolve(Path.Combine(directory, "Owned.cs"), out _));
        Assert.True(inputs.TryResolve("./Owned.cs", out _));
        Assert.False(inputs.TryResolve(Path.GetFullPath("Owned.cs"), out _));
        Assert.False(inputs.TryResolve(Path.GetFullPath("foreign/Owned.cs"), out _));
    }

    [Fact]
    public void LiteralRelativeAliasWithoutPublishedDirectoryRemainsSeparateFromAbsolutePaths()
    {
        var inputs = CompileInputIdentity.ForFiles(new[] { "Owned.cs" });
        Assert.True(inputs.TryResolve("Owned.cs", out _));
        Assert.False(inputs.TryResolve(Path.GetFullPath("Owned.cs"), out _));
    }

    [Fact]
    public void AmbiguousAliasCannotSelectEitherLocalFile()
    {
        var inputs = new CompileInputIdentity(new Dictionary<string, IReadOnlyList<string>>
        {
            ["One.swift"] = new[] { "/staging/shared.swift" },
            ["Two.swift"] = new[] { "/staging/shared.swift", "/staging/shared.swift" },
        });
        Assert.False(inputs.TryResolve("/staging/shared.swift", out _));
    }

    [Theory]
    [InlineData(false, "/generated/Owned.swift", true)]
    [InlineData(false, "/generated/.wrapper-build/Owned.swift", true)]
    [InlineData(false, "/foreign/Owned.swift", false)]
    [InlineData(true, "/generated/Owned.swift", true)]
    [InlineData(true, "/generated/.wrapper-build/Owned.swift", true)]
    [InlineData(true, "/foreign/Owned.swift", false)]
    public void BothIntervalPlanesRequireInputIdentity(bool csharp, string path, bool expected)
    {
        var owner = FragmentOwners.ForDeclId(
            DeclId.Create("Fixture", "T", BindingItemKind.Method, "member"),
            csharp ? ArtifactRole.CSharpPublic : ArtifactRole.SwiftWrapper);
        const string text = "broken\n";
        var fragment = new OutputFragment
        {
            Owner = owner, Plane = csharp ? OutputPlane.CSharp : OutputPlane.Swift,
            Text = text, IsWholeScope = true, Depth = 0,
        };
        var set = new ModuleFragmentSet { ModuleName = "Fixture" };
        set.Add("Owned.swift", text, new[] { new FragmentInterval(fragment, 0, text.Length) });
        IProvenanceStep step = csharp
            ? new CSharpIntervalMapProvenanceStep(set, Inputs())
            : new IntervalMapProvenanceStep(set, Inputs());
        Assert.Equal(expected, step.TryResolve(At(path), out var hit));
        if (expected)
            Assert.Equal(owner.Unit, hit.Unit);
    }

    [Theory]
    [InlineData("/generated/Owned.swift", true)]
    [InlineData("/generated/.wrapper-build/Owned.swift", true)]
    [InlineData("/foreign/Owned.swift", false)]
    [InlineData("/sdk/Module.swiftinterface", false)]
    public void AnchorFallbackRequiresTheSameInputIdentity(string path, bool expected)
    {
        const string text = "@_cdecl(\"SBW_owned\")\nfunc owned() { broken() }\n";
        var step = new SymbolAnchorProvenanceStep(WrapperBlockIndex.Build(text), "Owned.swift", Inputs(),
            symbol => AttributionFixtures.ArtifactForSymbol(symbol), AttributionFixtures.SymbolUnitLookup());
        Assert.Equal(expected, step.TryResolve(At(path, 2), out var hit));
        if (expected)
            Assert.Equal(AttributionFixtures.UnitForSymbol("SBW_owned"), hit.Unit);
    }

    private static CompileInputIdentity Inputs() => new(new Dictionary<string, IReadOnlyList<string>>
    {
        ["Owned.swift"] = new[] { "/generated/Owned.swift", "/generated/.wrapper-build/Owned.swift" },
    });

    private static CompilerDiagnostic At(string file, int line = 1) => new()
    {
        File = file, Line = line, Column = 1, Severity = DiagnosticSeverity.Error, Message = "broken",
    };
}
