// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using BindingsGeneration;
using BindingsGeneration.ObjC;
using Xunit;
using static BindingsGeneration.Tests.ObjCTests.ObjCTestHelpers;

namespace BindingsGeneration.Tests.ObjCTests;

/// <summary>
/// Tests for the Gap 3 native-symbol existence guard: classes the headers declare
/// but whose <c>_OBJC_CLASS_$_&lt;Name&gt;</c> symbol is defined in no linked binary are
/// over-bindings and must be dropped (with their dependent categories), while the
/// guard fails open whenever evidence is insufficient.
/// </summary>
public class NativeSymbolGuardTests
{
    private static ObjCModule Module(
        List<ObjCClassDecl>? classes = null,
        List<ObjCProtocolDecl>? protocols = null,
        List<ObjCCategoryDecl>? categories = null) =>
        new()
        {
            ModuleName = "TestModule",
            Classes = classes ?? [],
            Protocols = protocols ?? [],
            Categories = categories ?? [],
        };

    private static NativeSymbolProbe.ObjCClassSymbolScan Scan(
        NativeSymbolProbeOutcome outcome, params string[] classNames) =>
        new(new HashSet<string>(classNames), outcome);

    // ---- FilterToNativeSymbolBackedClasses ----

    [Fact]
    public void Guard_DropsClass_WithNoNativeSymbol()
    {
        var module = Module(classes: [new() { Name = "RealClass" }, new() { Name = "OMIDAdSession" }]);
        var diag = new ObjCBindingDiagnostics();

        var filtered = ObjCPipeline.FilterToNativeSymbolBackedClasses(
            module, Scan(NativeSymbolProbeOutcome.Gathered, "RealClass"), Logger, diag);

        Assert.Single(filtered.Classes);
        Assert.Equal("RealClass", filtered.Classes[0].Name);
        Assert.Contains(diag.SkippedSymbols,
            s => s.SymbolName == "OMIDAdSession" && s.Reason == ObjCSkipReason.MissingNativeSymbol);
    }

    [Fact]
    public void Guard_KeepsClass_WithCustomRuntimeName_EvenWhenDeclaredNameAbsent()
    {
        // objc_runtime_name puts the symbol under the runtime name (not the declared name),
        // which the JSON AST does not expose — so the scan keyed on declared names cannot
        // confirm it. The guard must keep it (only drops with positive proof of absence).
        var module = Module(classes:
        [
            new() { Name = "PublicName", HasCustomRuntimeName = true },
            new() { Name = "PlainAbsent" }
        ]);
        var diag = new ObjCBindingDiagnostics();

        var filtered = ObjCPipeline.FilterToNativeSymbolBackedClasses(
            module, Scan(NativeSymbolProbeOutcome.Gathered, "SomethingElse"), Logger, diag);

        Assert.Single(filtered.Classes);
        Assert.Equal("PublicName", filtered.Classes[0].Name);
        Assert.Contains(diag.SkippedSymbols,
            s => s.SymbolName == "PlainAbsent" && s.Reason == ObjCSkipReason.MissingNativeSymbol);
        Assert.DoesNotContain(diag.SkippedSymbols, s => s.SymbolName == "PublicName");
    }

    [Fact]
    public void Guard_KeepsAllClasses_WhenEverySymbolPresent()
    {
        var module = Module(classes: [new() { Name = "A" }, new() { Name = "B" }]);
        var diag = new ObjCBindingDiagnostics();

        var filtered = ObjCPipeline.FilterToNativeSymbolBackedClasses(
            module, Scan(NativeSymbolProbeOutcome.Gathered, "A", "B"), Logger, diag);

        Assert.Equal(2, filtered.Classes.Count);
        Assert.Empty(diag.SkippedSymbols);
        Assert.Same(module, filtered); // unchanged → same instance
    }

    [Fact]
    public void Guard_FailsOpen_WhenNothingToProbe()
    {
        // No binary existed to read (header-only slice): keep everything.
        var module = Module(classes: [new() { Name = "HeaderOnly" }]);
        var diag = new ObjCBindingDiagnostics();

        var filtered = ObjCPipeline.FilterToNativeSymbolBackedClasses(
            module, Scan(NativeSymbolProbeOutcome.NothingToProbe), Logger, diag);

        Assert.Single(filtered.Classes);
        Assert.Empty(diag.SkippedSymbols);
        Assert.Same(module, filtered);
    }

    [Fact]
    public void Guard_HardErrors_WhenProbeSystemicallyFailed()
    {
        // Binaries existed but every nm invocation failed (Finding 63): a systemic probe
        // breakage must fail loud, not silently keep all classes (which would defeat the guard
        // under exactly the broken-nm condition it exists to catch).
        var module = Module(classes: [new() { Name = "Whatever" }]);
        var diag = new ObjCBindingDiagnostics();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ObjCPipeline.FilterToNativeSymbolBackedClasses(
                module, Scan(NativeSymbolProbeOutcome.AllFailed), Logger, diag));

        Assert.Contains("SWIFTBIND028", ex.Message);
    }

    [Fact]
    public void Guard_FailsOpen_WhenScanFoundNoClassSymbolsAtAll()
    {
        // Evidence gathered but zero _OBJC_CLASS_$_ symbols → insufficient proof of absence.
        var module = Module(classes: [new() { Name = "Maybe" }]);
        var diag = new ObjCBindingDiagnostics();

        var filtered = ObjCPipeline.FilterToNativeSymbolBackedClasses(
            module, Scan(NativeSymbolProbeOutcome.Gathered /* empty */), Logger, diag);

        Assert.Single(filtered.Classes);
        Assert.Empty(diag.SkippedSymbols);
        Assert.Same(module, filtered);
    }

    [Fact]
    public void Guard_DropsCategory_OnDroppedClass()
    {
        var module = Module(
            classes: [new() { Name = "Present" }, new() { Name = "Absent" }],
            categories:
            [
                new() { CategoryName = "Ext", ClassName = "Absent" },   // base dropped → drop
                new() { CategoryName = "Ext2", ClassName = "Present" }, // base kept   → keep
                new() { CategoryName = "Ext3", ClassName = "NSString" } // foreign/Apple → keep
            ]);
        var diag = new ObjCBindingDiagnostics();

        var filtered = ObjCPipeline.FilterToNativeSymbolBackedClasses(
            module, Scan(NativeSymbolProbeOutcome.Gathered, "Present"), Logger, diag);

        Assert.Single(filtered.Classes);
        Assert.Equal("Present", filtered.Classes[0].Name);
        Assert.Equal(2, filtered.Categories.Count);
        Assert.DoesNotContain(filtered.Categories, c => c.ClassName == "Absent");
        Assert.Contains(filtered.Categories, c => c.ClassName == "Present");
        Assert.Contains(filtered.Categories, c => c.ClassName == "NSString");
        Assert.Contains(diag.SkippedSymbols,
            s => s.SymbolKind == "category" && s.SymbolName == "Absent+Ext");
    }

    [Fact]
    public void Guard_NeverTouchesProtocols()
    {
        var module = Module(
            classes: [new() { Name = "Absent" }],
            protocols: [new() { Name = "SomeProto" }]);
        var diag = new ObjCBindingDiagnostics();

        var filtered = ObjCPipeline.FilterToNativeSymbolBackedClasses(
            module, Scan(NativeSymbolProbeOutcome.Gathered, "OtherClass"), Logger, diag);

        Assert.Empty(filtered.Classes);
        Assert.Single(filtered.Protocols);
        Assert.Equal("SomeProto", filtered.Protocols[0].Name);
    }

    // ---- NativeSymbolProbe.ScanObjCClassSymbols ----

    [Fact]
    public void Scan_ExtractsObjCClassNames_AndUnionsAcrossBinaries()
    {
        using var fixture = new TempBinaries("simSlice", "deviceSlice");
        var runner = new SymbolRunner();
        // Sim slice defines FBLPromise; device slice additionally defines DeviceOnly.
        runner.Set(fixture["simSlice"],
            "0000000000001000 S _OBJC_CLASS_$_FBLPromise\n0000000000002000 T _someFunc\n");
        runner.Set(fixture["deviceSlice"],
            "0000000000001000 S _OBJC_CLASS_$_FBLPromise\n0000000000003000 S _OBJC_CLASS_$_DeviceOnly\n");

        var scan = NativeSymbolProbe.ScanObjCClassSymbols(
            [fixture["simSlice"], fixture["deviceSlice"]], runner, Logger);

        Assert.Equal(NativeSymbolProbeOutcome.Gathered, scan.Outcome);
        Assert.Contains("FBLPromise", scan.DefinedClassNames);
        Assert.Contains("DeviceOnly", scan.DefinedClassNames);
        Assert.DoesNotContain("someFunc", scan.DefinedClassNames); // non-class symbol ignored
    }

    [Fact]
    public void Scan_SkipsFailingBinary_ButKeepsReadableOnes()
    {
        using var fixture = new TempBinaries("good", "bad");
        var runner = new SymbolRunner();
        runner.Set(fixture["good"], "0000000000001000 S _OBJC_CLASS_$_GoodClass\n");
        runner.SetFailure(fixture["bad"]);

        var scan = NativeSymbolProbe.ScanObjCClassSymbols(
            [fixture["good"], fixture["bad"]], runner, Logger);

        Assert.Equal(NativeSymbolProbeOutcome.Gathered, scan.Outcome); // at least one binary read
        Assert.Contains("GoodClass", scan.DefinedClassNames);
    }

    [Fact]
    public void Scan_ReportsNothingToProbe_WhenNoBinaryExists()
    {
        // Path that does not exist on disk → skipped → nothing to probe (fail-open territory).
        var scan = NativeSymbolProbe.ScanObjCClassSymbols(
            [Path.Combine(Path.GetTempPath(), $"nonexistent_{Guid.NewGuid():N}")],
            new SymbolRunner(), Logger);

        Assert.Equal(NativeSymbolProbeOutcome.NothingToProbe, scan.Outcome);
        Assert.Empty(scan.DefinedClassNames);
    }

    [Fact]
    public void Scan_ReportsAllFailed_WhenBinariesExistButEveryNmFails()
    {
        // Binaries are present on disk but every nm invocation fails → systemic (AllFailed),
        // distinct from "nothing to probe": the guard must hard-error on this, not fail open.
        using var fixture = new TempBinaries("a", "b");
        var runner = new SymbolRunner();
        runner.SetFailure(fixture["a"]);
        runner.SetFailure(fixture["b"]);

        var scan = NativeSymbolProbe.ScanObjCClassSymbols(
            [fixture["a"], fixture["b"]], runner, Logger);

        Assert.Equal(NativeSymbolProbeOutcome.AllFailed, scan.Outcome);
        Assert.Empty(scan.DefinedClassNames);
    }

    /// <summary>Command runner that returns canned nm output keyed by binary path.</summary>
    private sealed class SymbolRunner : ICommandRunner
    {
        private readonly Dictionary<string, (int, string)> _byPath = new();
        public void Set(string path, string stdout) => _byPath[path] = (0, stdout);
        public void SetFailure(string path) => _byPath[path] = (1, "");

        public (int ExitCode, string StdOut, string StdErr) Run(string command, string arguments, int timeoutMs = 30000)
        {
            foreach (var (path, response) in _byPath)
            {
                if (arguments.Contains(path))
                    return (response.Item1, response.Item2, "");
            }
            return (0, "", "");
        }
    }

    /// <summary>Creates real temp files so the scan's File.Exists check passes.</summary>
    private sealed class TempBinaries : IDisposable
    {
        private readonly string _dir;
        private readonly Dictionary<string, string> _paths = new();

        public TempBinaries(params string[] names)
        {
            _dir = Path.Combine(Path.GetTempPath(), $"nmprobe_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            foreach (var name in names)
            {
                var p = Path.Combine(_dir, name);
                File.WriteAllText(p, "stub");
                _paths[name] = p;
            }
        }

        public string this[string name] => _paths[name];

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        }
    }
}
