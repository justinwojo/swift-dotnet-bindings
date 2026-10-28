// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="ThunkWeakLinkAudit"/> — the verdict behind <c>nuke binding-tests
/// --compile-only</c>'s thunk weak-link gate. The gate checks two levels: every undefined symbol of
/// an assembled thunk object is weak (the directive, where it is the only reference), and every
/// import of a future-floored fixture in the linked wrapper is weak (what dyld acts on), with the
/// thunk-bound fixtures still arriving through a thunk. These pin
/// the three parsers, both rules, the deliberate tolerance of a strong thunk target in the linked
/// wrapper, and the positive controls that turn a vacuous pass into a failure.
/// </summary>
public class ThunkWeakLinkAuditTests
{
    private const string Future = "_$s3Lib4MineV6futurey5Int32VyF";
    private const string Accessor = "_$s3Lib4MineVMa";
    private const string Static = "_$s3Lib4MineV6staticy5Int32VyFZ";

    private const string Thunks = """
        .text
        .align 4
        .globl _thunk_Lib_00000001
        .p2align 2
        _thunk_Lib_00000001:
            .weak_reference _$s3Lib4MineV6futurey5Int32VyF
            b       _$s3Lib4MineV6futurey5Int32VyF

        .globl _thunk_Lib_00000002
        .p2align 2
        _thunk_Lib_00000002:
            .weak_reference _$s3Lib4MineVMa
            .weak_reference _$s3Lib4MineV6staticy5Int32VyFZ
            stp     x29, x30, [sp, #-16]!
            bl      _$s3Lib4MineVMa
            mov     x20, x0
            bl      _$s3Lib4MineV6staticy5Int32VyFZ
            ldp     x29, x30, [sp], #16
            ret
        """;

    private const string ObjectSymbols = """
                         (undefined) weak external _$s3Lib4MineV6futurey5Int32VyF
                         (undefined) weak external _$s3Lib4MineVMa
                         (undefined) external _$s3Lib4MineV6staticy5Int32VyFZ
        0000000000000000 (__TEXT,__text) external _thunk_Lib_00000001
        0000000000000004 (__TEXT,__text) non-external ltmp0
        """;

    private const string FatImports = """
        /tmp/Wrapper.framework/Wrapper [arm64]:
            -imports:
              0x0000  _$s3Lib4MineV6futurey5Int32VyF [weak-import] (from Lib)
              0x0001  _$s3Lib4MineVMa  (from Lib)
              0x0002  _swift_retain  (from libswiftCore)
        /tmp/Wrapper.framework/Wrapper [x86_64]:
            -imports:
              0x0000  _$s3Lib4MineV6futurey5Int32VyF [weak-import] (from Lib)
        """;

    private static IReadOnlySet<string> Targets => ThunkWeakLinkAudit.ParseBranchTargets(Thunks);

    private static List<ObjectReference> AllWeak(IEnumerable<string> symbols) =>
        symbols.Select(s => new ObjectReference(s, true)).ToList();

    [Fact]
    public void ParseBranchTargets_ReadsEveryBranchAndCall_NotDirectivesOrLabels() =>
        Assert.Equal(new[] { Future, Static, Accessor }, Targets.OrderBy(s => s, System.StringComparer.Ordinal));

    [Theory]
    [InlineData("    jmp     _$s3Lib1fyyF", "_$s3Lib1fyyF")]
    [InlineData("    callq   _$s3Lib1fyyF", "_$s3Lib1fyyF")]
    public void ParseBranchTargets_ReadsTheX86Mnemonics(string line, string symbol) =>
        Assert.Equal(new[] { symbol }, ThunkWeakLinkAudit.ParseBranchTargets(line + "\n"));

    [Fact]
    public void ParseUndefinedSymbols_ReadsOnlyUndefined_AndTheWeakFlag() =>
        Assert.Equal(
            new[] { new ObjectReference(Future, true), new ObjectReference(Accessor, true), new ObjectReference(Static, false) },
            ThunkWeakLinkAudit.ParseUndefinedSymbols(ObjectSymbols));

    [Fact]
    public void ParseImports_SplitsSlicesAndReadsTheWeakFlag()
    {
        var imports = ThunkWeakLinkAudit.ParseImports(FatImports);

        Assert.Equal(4, imports.Count);
        Assert.Equal(new DyldImport("arm64", Future, "Lib", true), imports[0]);
        Assert.Equal(new DyldImport("arm64", Accessor, "Lib", false), imports[1]);
        Assert.Equal(new DyldImport("arm64", "_swift_retain", "libswiftCore", false), imports[2]);
        Assert.Equal(new DyldImport("x86_64", Future, "Lib", true), imports[3]);
    }

    [Fact]
    public void Evaluate_StrongReferenceInTheThunkObject_Fails()
    {
        var verdict = ThunkWeakLinkAudit.Evaluate("W", Targets,
            ThunkWeakLinkAudit.ParseUndefinedSymbols(ObjectSymbols), ThunkWeakLinkAudit.ParseImports(FatImports), Array.Empty<FutureFlooredFixture>());

        var failure = Assert.Single(verdict.Failures);
        Assert.Contains(Static, failure);
        Assert.Contains("STRONGLY", failure);
    }

    [Fact]
    public void Evaluate_StrongThunkTargetInTheLinkedWrapper_IsTolerated_AndCounted()
    {
        // The accessor is strong in the linked wrapper because (in this model) Swift code also uses it
        // unconditionally — the linker's correct merge, not a defect.
        var verdict = ThunkWeakLinkAudit.Evaluate("W", Targets, AllWeak(Targets), ThunkWeakLinkAudit.ParseImports(FatImports),
            new[] { Thunked("4MineV6future") });

        Assert.True(verdict.Passed, string.Join("\n", verdict.Failures));
        Assert.Equal(2, verdict.ImportedThunkTargetCount);
        Assert.Equal(1, verdict.StrongImportedThunkTargetCount);
        Assert.Equal(3, verdict.WeakObjectReferenceCount);
    }

    [Fact]
    public void Evaluate_ThunkTargetMissingFromTheObjects_Fails()
    {
        var verdict = ThunkWeakLinkAudit.Evaluate("W", Targets, AllWeak(new[] { Future, Accessor }),
            ThunkWeakLinkAudit.ParseImports(FatImports), Array.Empty<FutureFlooredFixture>());

        var failure = Assert.Single(verdict.Failures);
        Assert.Contains(Static, failure);
        Assert.Contains("stale or incomplete", failure);
    }

    [Fact]
    public void Evaluate_NothingParsed_FailsInsteadOfPassingVacuously()
    {
        var verdict = ThunkWeakLinkAudit.Evaluate("W",
            new HashSet<string>(), new List<ObjectReference>(), new List<DyldImport>(), Array.Empty<FutureFlooredFixture>());

        Assert.Equal(3, verdict.Failures.Count);
    }

    [Fact]
    public void Evaluate_NoThunkTargetImported_FailsInsteadOfPassingVacuously()
    {
        var verdict = ThunkWeakLinkAudit.Evaluate("W", Targets, AllWeak(Targets),
            new[] { new DyldImport("arm64", "_swift_retain", "libswiftCore", false) }, Array.Empty<FutureFlooredFixture>());

        Assert.Contains(verdict.Failures, f => f.Contains("vacuously"));
    }

    [Fact]
    public void Evaluate_FutureFlooredFixture_MustBeImported_AndWeakEverywhere()
    {
        var weakThunkImport = new DyldImport("arm64", Future, "Lib", true);

        // Missing entirely — thunk-bound or not, the fixture no longer exercises anything.
        foreach (var fixture in new[] { Thunked("5OtherV"), SwiftOnly("5OtherV") })
            Assert.Contains(
                ThunkWeakLinkAudit.Evaluate("W", Targets, AllWeak(Targets), new[] { weakThunkImport }, new[] { fixture }).Failures,
                f => f.Contains("no longer reaches the wrapper"));

        // A strong import of a future-floored symbol fails whether it is a thunk target or not.
        var strongThunkTarget = weakThunkImport with { IsWeak = false };
        var strongSibling = new DyldImport("arm64", "_$s3Lib4MineV6futureSivg", "Lib", false);
        var verdict = ThunkWeakLinkAudit.Evaluate("W", Targets, AllWeak(Targets),
            new[] { strongThunkTarget, strongSibling }, new[] { Thunked("4MineV6future") });
        Assert.Equal(2, verdict.Failures.Count);
        Assert.All(verdict.Failures, f => Assert.Contains("dyld would refuse to load", f));

        // ...including a fixture that only Swift code references.
        var strongSwiftOnly = new DyldImport("arm64", "_$s3Lib5OtherV1xSivg", "Lib", false);
        var swiftOnlyFailure = Assert.Single(ThunkWeakLinkAudit.Evaluate("W", Targets, AllWeak(Targets),
            new[] { weakThunkImport, strongSwiftOnly }, new[] { SwiftOnly("5OtherV") }).Failures);
        Assert.Contains("dyld would refuse to load", swiftOnlyFailure);
    }

    [Fact]
    public void Evaluate_ThunkBoundFixture_ReachedOnlyFromSwift_Fails_WhileASwiftOnlyFixturePasses()
    {
        var weakThunkImport = new DyldImport("arm64", Future, "Lib", true);
        var weakSwiftOnly = new DyldImport("arm64", "_$s3Lib5OtherV1xSivg", "Lib", true);
        var imports = new[] { weakThunkImport, weakSwiftOnly };

        Assert.Contains(
            ThunkWeakLinkAudit.Evaluate("W", Targets, AllWeak(Targets), imports, new[] { Thunked("5OtherV") }).Failures,
            f => f.Contains("no longer reached through a native thunk"));

        var verdict = ThunkWeakLinkAudit.Evaluate("W", Targets, AllWeak(Targets), imports, new[] { SwiftOnly("5OtherV") });
        Assert.True(verdict.Passed, string.Join("\n", verdict.Failures));
    }

    private static FutureFlooredFixture Thunked(string fragment) => new(fragment, ReachedThroughThunk: true);

    private static FutureFlooredFixture SwiftOnly(string fragment) => new(fragment, ReachedThroughThunk: false);
}
