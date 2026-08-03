// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The wrapper-requirement fact is what a consumer reads when deciding whether
/// <c>SwiftWrapperRequired=false</c> is honest for a library, so it has to distinguish a binding that
/// would lose call routes without a wrapper from one that would lose nothing. The emitter writes a
/// <c>.Wrapper.swift</c> on every run, so the distinguishing evidence is what that source declares —
/// not that it exists.
/// </summary>
public class WrapperRequirementEvaluatorTests : IDisposable
{
    private readonly string _outputDirectory =
        Path.Combine(Path.GetTempPath(), "wrapper-requirement-" + Guid.NewGuid().ToString("N"));

    public WrapperRequirementEvaluatorTests() => Directory.CreateDirectory(_outputDirectory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_outputDirectory, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ImportsOnlyWrapperSource_IsNotRequired()
    {
        // The shape the whole fact exists to identify: a file is on disk and the wrapper compile will
        // still run, but nothing in it is a symbol the generated C# binds to.
        WriteWrapper("""
            import TestModule
            import Foundation

            @frozen
            public struct SBW_Utf8Slice {
                public var ptr: UnsafeMutablePointer<UInt8>
                public var len: Int
            }
            """);
        var report = new BindingReport { ModuleName = "TestModule" };

        WrapperRequirementEvaluator.Evaluate(report, _outputDirectory);

        Assert.False(report.WrapperRequirement!.WrapperRequired);
        Assert.Contains("nothing is lost", report.WrapperRequirement.Rationale);
    }

    [Fact]
    public void CdeclEntryPoint_IsRequired_AndRationaleCountsEntryPoints()
    {
        WriteWrapper("""
            import TestModule

            @_cdecl("SBW_TestModule_fetch")
            public func SBW_TestModule_fetch(_ self_: UnsafeRawPointer) -> Int32 { 0 }
            """);
        var report = new BindingReport { ModuleName = "TestModule" };
        report.WrappedItems.Add(WrappedItem("Fetch", "CdeclWrapper"));

        WrapperRequirementEvaluator.Evaluate(report, _outputDirectory);

        Assert.True(report.WrapperRequirement!.WrapperRequired);
        Assert.Equal(1, report.WrapperRequirement.WrapperEntryPointCount);
        Assert.Equal(1, report.WrapperRequirement.WrappedMemberCount);
        Assert.Contains("exports 1 entry point", report.WrapperRequirement.Rationale);
    }

    [Fact]
    public void EveryEntryPointIsCounted_NotJustTheFirst()
    {
        // The count is what the rationale states the cost from, so it has to be a total. It is also
        // the only measure that covers the ordinary @_cdecl method path, which records no member row
        // of its own — a module of plain wrapped methods reads as zero wrapped members but exports
        // one symbol per method, and the rationale must not understate that as "nothing recorded".
        WriteWrapper("""
            import TestModule

            @_cdecl("SBW_TestModule_a")
            public func SBW_TestModule_a() {}

            @_cdecl("SBW_TestModule_b")
            public func SBW_TestModule_b() {}

            @_silgen_name("SBW_TestModule_c")
            public func SBW_TestModule_c() async {}
            """);
        var report = new BindingReport { ModuleName = "TestModule" };

        WrapperRequirementEvaluator.Evaluate(report, _outputDirectory);

        Assert.Equal(3, report.WrapperRequirement!.WrapperEntryPointCount);
        Assert.Equal(0, report.WrapperRequirement.WrappedMemberCount);
        Assert.Contains("exports 3 entry points", report.WrapperRequirement.Rationale);
    }

    [Fact]
    public void SilgenNameEntryPoint_AlsoCountsAsRequired()
    {
        // Async wrappers export through @_silgen_name rather than @_cdecl; both are symbols the
        // generated P/Invokes target, so either one settles the question.
        WriteWrapper("""
            import TestModule

            @_silgen_name("SBW_TestModule_loadAsync")
            public func SBW_TestModule_loadAsync() async -> Int32 { 0 }
            """);
        var report = new BindingReport { ModuleName = "TestModule" };

        WrapperRequirementEvaluator.Evaluate(report, _outputDirectory);

        Assert.True(report.WrapperRequirement!.WrapperRequired);
    }

    [Fact]
    public void EntryPointsWithNoWrappedMembers_DoNotReadAsNothingLost()
    {
        // Metadata, helper and reverse-dispatch symbols belong to no member row, so a zero count here
        // is a real shape rather than a contradiction — and must not be phrased as an all-clear.
        WriteWrapper("""
            @_cdecl("SBW_TestModule_metadata")
            public func SBW_TestModule_metadata() -> UnsafeRawPointer? { nil }
            """);
        var report = new BindingReport { ModuleName = "TestModule" };

        WrapperRequirementEvaluator.Evaluate(report, _outputDirectory);

        Assert.True(report.WrapperRequirement!.WrapperRequired);
        Assert.Equal(0, report.WrapperRequirement.WrappedMemberCount);
        Assert.Equal(1, report.WrapperRequirement.WrapperEntryPointCount);
        // Phrased from what the wrapper exports, so a member-row count of zero cannot read as an
        // all-clear for a wrapper that carries a real symbol.
        Assert.DoesNotContain("0 member", report.WrapperRequirement.Rationale);
        Assert.Contains("do not exist at runtime", report.WrapperRequirement.Rationale);
    }

    [Fact]
    public void ClosureParamTombstones_DoNotCountAsWrappedMembers()
    {
        // A tombstone is recorded as wrapped but its emitted body throws — there is no Swift behind
        // it, so counting it would overstate what a missing wrapper costs.
        WriteWrapper("""
            @_cdecl("SBW_TestModule_real")
            public func SBW_TestModule_real() {}
            """);
        var report = new BindingReport { ModuleName = "TestModule" };
        report.WrappedItems.Add(WrappedItem("Real", "CdeclWrapper"));
        report.WrappedItems.Add(
            WrappedItem("Tombstoned", ReportCollector.ClosureParamTombstoneWrapperKind));

        WrapperRequirementEvaluator.Evaluate(report, _outputDirectory);

        Assert.Equal(1, report.WrapperRequirement!.WrappedMemberCount);
    }

    [Fact]
    public void MarkedMembers_AreCalledOutAsASeparateCondition()
    {
        // Members already marked because no wrapper could be generated for them are not fixed by
        // building one, so the rationale has to keep the two conditions apart.
        WriteWrapper("""
            @_cdecl("SBW_TestModule_real")
            public func SBW_TestModule_real() {}
            """);
        var report = new BindingReport { ModuleName = "TestModule" };
        report.WrappedItems.Add(WrappedItem("Real", "CdeclWrapper"));
        report.DegradedSurface = new DegradedSurfaceSummary { Total = 3 };
        report.DegradedSurface.ByDiagnosticId["SB0001"] = 2;
        report.DegradedSurface.ByDiagnosticId["SB0009"] = 1;

        WrapperRequirementEvaluator.Evaluate(report, _outputDirectory);

        Assert.Equal(3, report.WrapperRequirement!.UnwrappedMarkedMemberCount);
        Assert.Contains("3 member(s) are already marked", report.WrapperRequirement.Rationale);
    }

    [Fact]
    public void ThunkAssemblyAlone_IsRequired_WithNoExportingSwift()
    {
        // Native thunks are the other half of the signal: a module can bind entirely through emitted
        // assembly with a wrapper source that declares nothing, and losing those thunks costs the
        // same call routes an @_cdecl would have.
        WriteWrapper("""
            import TestModule
            """);
        File.WriteAllText(
            Path.Combine(_outputDirectory, "TestModule.arm64.s"), ".globl _SBT_TestModule_get\n");
        var report = new BindingReport { ModuleName = "TestModule" };

        WrapperRequirementEvaluator.Evaluate(report, _outputDirectory);

        Assert.True(report.WrapperRequirement!.WrapperRequired);
        Assert.Equal(0, report.WrapperRequirement.WrapperEntryPointCount);
        // Required with no Swift entry points is a real shape, not a contradiction — so the sentence
        // has to name the thunks rather than fall through to wording about exported symbols.
        Assert.Contains("native thunks", report.WrapperRequirement.Rationale);
        Assert.DoesNotContain("exports 0", report.WrapperRequirement.Rationale);
    }

    [Fact]
    public void ThunkAssemblyForAnotherArchitecture_DoesNotCount()
    {
        // Thunks are emitted per-slice, so an x86_64 assembly says nothing about the arm64 build the
        // caller asked about — reading it as evidence would claim dependence on a slice that has none.
        WriteWrapper("""
            import TestModule
            """);
        File.WriteAllText(
            Path.Combine(_outputDirectory, "TestModule.x86_64.s"), ".globl _SBT_TestModule_get\n");
        var report = new BindingReport { ModuleName = "TestModule" };

        WrapperRequirementEvaluator.Evaluate(report, _outputDirectory, architecture: "arm64");

        Assert.False(report.WrapperRequirement!.WrapperRequired);
    }

    [Fact]
    public void UnreadableWrapperSource_ReadsAsRequired()
    {
        // Failing to read the bytes says nothing about what they contain. This is a report fact the
        // SDK's severity default keys off, so the unknown answer has to be the one that keeps a
        // missing wrapper an error rather than quietly excusing it.
        if (OperatingSystem.IsWindows())
            return; // The generator only runs on Apple hosts; permission bits are the Unix spelling.

        var path = Path.Combine(_outputDirectory, "TestModule.Wrapper.swift");
        File.WriteAllText(path, "import TestModule\n");
        File.SetUnixFileMode(path, UnixFileMode.None);
        var report = new BindingReport { ModuleName = "TestModule" };

        try
        {
            WrapperRequirementEvaluator.Evaluate(report, _outputDirectory);
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        Assert.True(report.WrapperRequirement!.WrapperRequired);
    }

    [Fact]
    public void Restate_TakesSettledCountsAndRebuildsTheMarkedClause()
    {
        // The counts settle when the generator finishes, but the wrapper compile runs after that and
        // can strip symbols. The rationale is prose built from the marked count, so correcting one
        // without the other leaves the sentence contradicting the number beside it.
        WriteWrapper("""
            @_cdecl("SBW_TestModule_real")
            public func SBW_TestModule_real() {}
            """);
        var report = new BindingReport { ModuleName = "TestModule" };
        report.WrappedItems.Add(WrappedItem("First", "CdeclWrapper"));
        report.WrappedItems.Add(WrappedItem("Second", "CdeclWrapper"));
        report.DegradedSurface = new DegradedSurfaceSummary { Total = 3 };
        report.DegradedSurface.ByDiagnosticId["SB0001"] = 3;
        WrapperRequirementEvaluator.Evaluate(report, _outputDirectory);
        Assert.Contains("3 member(s) are already marked", report.WrapperRequirement!.Rationale);

        WrapperRequirementEvaluator.Restate(
            report.WrapperRequirement!, wrappedMemberCount: 1, unwrappedMarkedMemberCount: 1);

        Assert.Equal(1, report.WrapperRequirement.WrappedMemberCount);
        Assert.Equal(1, report.WrapperRequirement.UnwrappedMarkedMemberCount);
        Assert.Contains("1 member(s) are already marked", report.WrapperRequirement.Rationale);
        Assert.DoesNotContain("3 member(s)", report.WrapperRequirement.Rationale);
        // The artifacts still export entry points, so whether a wrapper is needed at all — and how
        // many symbols it carries — is not the question stripping member rows answers.
        Assert.True(report.WrapperRequirement.WrapperRequired);
        Assert.Equal(1, report.WrapperRequirement.WrapperEntryPointCount);
    }

    [Fact]
    public void Restate_ToZeroMarkedMembers_DropsTheClauseEntirely()
    {
        // The clause is conditional prose, so restating to zero has to remove the sentence rather
        // than leave "A further 0 member(s) are already marked".
        WriteWrapper("""
            @_cdecl("SBW_TestModule_real")
            public func SBW_TestModule_real() {}
            """);
        var report = new BindingReport { ModuleName = "TestModule" };
        report.DegradedSurface = new DegradedSurfaceSummary { Total = 2 };
        report.DegradedSurface.ByDiagnosticId["SB0009"] = 2;
        WrapperRequirementEvaluator.Evaluate(report, _outputDirectory);

        WrapperRequirementEvaluator.Restate(
            report.WrapperRequirement!, wrappedMemberCount: 0, unwrappedMarkedMemberCount: 0);

        Assert.DoesNotContain("already marked", report.WrapperRequirement!.Rationale);
    }

    private void WriteWrapper(string swift) =>
        File.WriteAllText(Path.Combine(_outputDirectory, "TestModule.Wrapper.swift"), swift);

    private static WrappedItem WrappedItem(string name, string wrapperKind) => new()
    {
        Kind = BindingItemKind.Method,
        Name = name,
        WrapperKind = wrapperKind,
    };
}
