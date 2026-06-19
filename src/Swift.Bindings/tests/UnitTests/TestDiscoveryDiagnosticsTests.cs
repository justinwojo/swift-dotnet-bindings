// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SwiftBindings.TestDiscovery;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Drives <see cref="TestDiscoveryGenerator"/> through a real <see cref="CSharpGeneratorDriver"/>
/// and asserts on the discovery near-miss diagnostics — the false-green footguns a convention-based
/// discovery generator must surface at build time because nothing else will:
/// <list type="bullet">
/// <item>SBTD001 — a <c>Test*</c> method declared <c>async void</c> (detaches post-await asserts).</item>
/// <item>SBTD002 — a <c>Test*</c> method on a <c>TestBase</c> class that discovery silently drops
/// (non-public / static / parameterized).</item>
/// <item>SBTD003 — a <c>*Tests</c>-named class with a discoverable-shape <c>Test*</c> method that
/// forgot <c>: TestBase</c>, so the whole class never runs.</item>
/// </list>
/// Each fixture is compiled against the live runtime's reference assemblies (sourced from
/// <c>TRUSTED_PLATFORM_ASSEMBLIES</c> so the helper stays AOT/trim-clean — no <c>Assembly.Location</c>)
/// plus an in-source <c>TestBase</c> stub, which is all the generator's semantic pass needs.
/// </summary>
public class TestDiscoveryDiagnosticsTests
{
    // Minimal scaffold every fixture is wrapped in: a TestBase the generator's inheritance walk
    // can resolve from source, in the namespace the real test app uses.
    private const string Preamble = @"
using RuntimeTestsApp.Infrastructure;
using System.Threading.Tasks;
namespace RuntimeTestsApp.Infrastructure { public abstract class TestBase { protected TestBase(object results) {} } }
";

    private static ImmutableArray<Diagnostic> RunGenerator(string fixture)
    {
        var tree = CSharpSyntaxTree.ParseText(Preamble + fixture);

        // Reference the running runtime's managed assemblies by path (no Assembly.Location ->
        // no IL3000 under this project's IsAotCompatible analyzer). A fully-resolved compilation
        // lets the generator's SemanticModel.GetDeclaredSymbol/BaseType walk behave exactly as it
        // does in the real build.
        var tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "";
        var references = tpa
            .Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrEmpty(p) && p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(p))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();

        var compilation = CSharpCompilation.Create(
            "TestDiscoveryFixture",
            new[] { tree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var driver = CSharpGeneratorDriver.Create(new TestDiscoveryGenerator().AsSourceGenerator());
        return driver.RunGenerators(compilation).GetRunResult().Diagnostics;
    }

    private static int CountById(ImmutableArray<Diagnostic> diags, string id)
        => diags.Count(d => d.Id == id);

    private static Diagnostic SingleById(ImmutableArray<Diagnostic> diags, string id)
        => Assert.Single(diags.Where(d => d.Id == id));

    // ===================================================================
    //  SBTD001 — async void
    // ===================================================================

    [Fact]
    public void AsyncVoidTestMethod_ReportsSBTD001()
    {
        var diags = RunGenerator(@"
public class FeatureTests : TestBase {
    public FeatureTests(object r) : base(r) {}
    public async void TestThing() { await Task.Yield(); }
}");
        var d = SingleById(diags, "SBTD001");
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Contains("FeatureTests.TestThing", d.GetMessage());
        // async void is SBTD001's job ONLY — it passes the discovery filter, so it is not SBTD002.
        Assert.Equal(0, CountById(diags, "SBTD002"));
    }

    // ===================================================================
    //  SBTD002 — near-miss method on a TestBase class
    // ===================================================================

    [Fact]
    public void NonPublicTestMethodOnTestBase_ReportsSBTD002_NonPublic()
    {
        var diags = RunGenerator(@"
public class FeatureTests : TestBase {
    public FeatureTests(object r) : base(r) {}
    void TestHidden() {}
}");
        var d = SingleById(diags, "SBTD002");
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Contains("FeatureTests.TestHidden", d.GetMessage());
        Assert.Contains("non-public", d.GetMessage());
    }

    [Fact]
    public void StaticTestMethodOnTestBase_ReportsSBTD002_Static()
    {
        var diags = RunGenerator(@"
public class FeatureTests : TestBase {
    public FeatureTests(object r) : base(r) {}
    public static void TestStaticOne() {}
}");
        var d = SingleById(diags, "SBTD002");
        Assert.Contains("FeatureTests.TestStaticOne", d.GetMessage());
        Assert.Contains("static", d.GetMessage());
    }

    [Fact]
    public void ParameterizedTestMethodOnTestBase_ReportsSBTD002_Parameterized()
    {
        var diags = RunGenerator(@"
public class FeatureTests : TestBase {
    public FeatureTests(object r) : base(r) {}
    public void TestWithArg(int x) {}
}");
        var d = SingleById(diags, "SBTD002");
        Assert.Contains("FeatureTests.TestWithArg", d.GetMessage());
        Assert.Contains("parameterized", d.GetMessage());
    }

    [Fact]
    public void FullyDiscoverableTestMethod_ReportsNoNearMiss()
    {
        var diags = RunGenerator(@"
public class FeatureTests : TestBase {
    public FeatureTests(object r) : base(r) {}
    public void TestRuns() {}
}");
        Assert.Equal(0, CountById(diags, "SBTD002"));
        Assert.Equal(0, CountById(diags, "SBTD003"));
        Assert.Equal(0, CountById(diags, "SBTD001"));
    }

    // ===================================================================
    //  SBTD003 — *Tests class missing : TestBase
    // ===================================================================

    [Fact]
    public void TestsNamedClassWithoutTestBase_ReportsSBTD003()
    {
        var diags = RunGenerator(@"
public class OrphanTests {
    public void TestRuns() {}
}");
        var d = SingleById(diags, "SBTD003");
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Contains("OrphanTests", d.GetMessage());
        // It never reaches the TestBase-only SBTD002 path.
        Assert.Equal(0, CountById(diags, "SBTD002"));
    }

    [Fact]
    public void TestsNamedClassWithoutTestBase_ButNoDiscoverableMethod_ReportsNothing()
    {
        // Only a non-discoverable Test* shape — the class deriving TestBase would not have run this
        // method anyway, so flagging the missing base would be a false positive.
        var diags = RunGenerator(@"
public class OrphanTests {
    void TestHidden() {}
}");
        Assert.Equal(0, CountById(diags, "SBTD003"));
    }

    [Fact]
    public void NonTestsNamedClassWithoutTestBase_IsNotACandidate()
    {
        // A plain helper class with a Test*-shaped method but neither a base list nor a *Tests name
        // is outside discovery entirely — no diagnostic.
        var diags = RunGenerator(@"
public class PlainHelper {
    public void TestRuns() {}
}");
        Assert.Equal(0, CountById(diags, "SBTD002"));
        Assert.Equal(0, CountById(diags, "SBTD003"));
    }

    // ===================================================================
    //  Dedup — a partial class must report each near-miss once
    // ===================================================================

    [Fact]
    public void PartialClassNearMiss_ReportedExactlyOnce()
    {
        // Both partial declarations are diagnostic candidates (one has the base list, the other is
        // *Tests-named), and both resolve to the same symbol, so GetNearMissDiagnostics yields the
        // SBTD002 twice. The RegisterSourceOutput dedup must collapse it to a single report.
        var diags = RunGenerator(@"
public partial class SplitTests : TestBase {
    public SplitTests(object r) : base(r) {}
    void TestHidden() {}
}
public partial class SplitTests {
}");
        Assert.Equal(1, CountById(diags, "SBTD002"));
        Assert.Contains("SplitTests.TestHidden", SingleById(diags, "SBTD002").GetMessage());
    }
}
