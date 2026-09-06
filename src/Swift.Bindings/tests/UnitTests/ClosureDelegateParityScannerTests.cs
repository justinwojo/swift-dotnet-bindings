// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// The model under test carries its own `#nullable enable`, so the annotations below are opted into
// locally rather than inherited from this project's Nullable=disable.
#nullable enable

using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="ClosureDelegateParityScanner"/> — the decision model behind
/// <c>nuke binding-tests --compile-only</c>'s closure delegate-type parity gate.
///
/// <para>The gate needs freshly-generated bindings on disk, but the scan itself is a pure function
/// over C# text: pull every <c>GetDelegateFrom(Boxed)Context&lt;T&gt;</c> cast, pull the file's
/// remaining <c>Action</c>/<c>Func</c> types, and report any cast the public surface never declares.
/// Three things in that must not drift and are testable here — the bracket matching has to survive
/// nested generic arguments (a naive scan stops at the first <c>&gt;</c> and compares a truncated
/// type), the normalization has to collapse the qualification and whitespace spellings the emitter
/// uses interchangeably at the two sites, and the cast's own type argument has to be excised from
/// the surface it is compared against or every cast trivially matches itself.</para>
/// </summary>
public class ClosureDelegateParityScannerTests
{
    /// <summary>
    /// The shape the gate exists for: an emitted delegate stored under one type and recovered under
    /// another. Deeply nested so a scan that mismatched brackets would compare truncated strings.
    /// </summary>
    [Fact]
    public void MismatchedCastTarget_IsReported()
    {
        const string source = """
            public virtual void Deliver( global::System.Action<Swift.SwiftResult<Swift.SwiftOptional<Swift.Runtime.ExistentialContainer0>, Swift.Runtime.ExistentialContainer1>> completion) { }
            var d = SwiftClosureMarshaller.GetDelegateFromContext<global::System.Action<Swift.SwiftResult<Swift.SwiftOptional<Swift.Runtime.ExistentialContainer0>, Swift.Foundation.AnyError>>>(ctx);
            """;

        var verdict = ClosureDelegateParityScanner.ScanFile("Sample.cs", source);

        Assert.False(verdict.Passed);
        var mismatch = Assert.Single(verdict.Mismatches);
        Assert.Equal("Sample.cs", mismatch.File);
        Assert.Contains("AnyError", mismatch.CastType);
    }

    /// <summary>
    /// The same nesting depth, agreeing. Without this the test above would pass for a scanner that
    /// reported everything.
    /// </summary>
    [Fact]
    public void MatchingCastTarget_Passes()
    {
        const string source = """
            public virtual void Deliver( global::System.Action<Swift.SwiftResult<Swift.SwiftOptional<Swift.Runtime.ExistentialContainer0>, Swift.Foundation.AnyError>> completion) { }
            var d = SwiftClosureMarshaller.GetDelegateFromContext<global::System.Action<Swift.SwiftResult<Swift.SwiftOptional<Swift.Runtime.ExistentialContainer0>, Swift.Foundation.AnyError>>>(ctx);
            """;

        var verdict = ClosureDelegateParityScanner.ScanFile("Sample.cs", source);

        Assert.True(verdict.Passed);
        Assert.Equal(1, verdict.CastCount);
    }

    /// <summary>The boxed recovery is the same unchecked cast and is judged identically.</summary>
    [Fact]
    public void BoxedContextCast_IsScannedToo()
    {
        const string source = """
            public virtual void Emit( global::System.Action<Swift.SwiftArray<double>> sink) { }
            var d = SwiftClosureMarshaller.GetDelegateFromBoxedContext<global::System.Action<IReadOnlyList<double>>>(ctx);
            """;

        var verdict = ClosureDelegateParityScanner.ScanFile("Sample.cs", source);

        Assert.False(verdict.Passed);
        Assert.Equal(1, verdict.CastCount);
    }

    /// <summary>
    /// The cast's own type argument must not count as public surface. If it did, every cast would
    /// match itself and the gate would be permanently green.
    /// </summary>
    [Fact]
    public void CastTypeArgument_DoesNotSatisfyItself()
    {
        const string source =
            "var d = SwiftClosureMarshaller.GetDelegateFromContext<global::System.Action<Swift.SwiftArray<double>>>(ctx);";

        var verdict = ClosureDelegateParityScanner.ScanFile("Sample.cs", source);

        Assert.False(verdict.Passed);
    }

    /// <summary>
    /// The two sites spell the same type differently — `global::`, `System.`/`Swift.` qualification,
    /// and whitespace inside the type arguments — and those differences are not divergences.
    /// </summary>
    [Fact]
    public void QualificationAndWhitespaceDifferences_AreNotMismatches()
    {
        const string source = """
            public virtual void Emit( Action<SwiftDictionary<string, int>> sink) { }
            var d = SwiftClosureMarshaller.GetDelegateFromContext<global::System.Action<Swift.SwiftDictionary<string,int>>>(ctx);
            """;

        var verdict = ClosureDelegateParityScanner.ScanFile("Sample.cs", source);

        Assert.True(verdict.Passed);
    }

    /// <summary>
    /// A nullable delegate parameter is stored and recovered as the same delegate; the `?` is a
    /// declaration-site annotation, not part of the type identity being compared.
    /// </summary>
    [Fact]
    public void NullableDelegateParameter_MatchesNonNullableCast()
    {
        const string source = """
            public virtual void Emit( global::System.Action<int>? sink) { }
            var d = SwiftClosureMarshaller.GetDelegateFromContext<global::System.Action<int>>(ctx);
            """;

        var verdict = ClosureDelegateParityScanner.ScanFile("Sample.cs", source);

        Assert.True(verdict.Passed);
    }

    /// <summary>A file with no trampoline at all contributes nothing rather than failing.</summary>
    [Fact]
    public void FileWithoutCasts_ContributesNothing()
    {
        var verdict = ClosureDelegateParityScanner.ScanFile(
            "Sample.cs",
            "public virtual void Plain( global::System.Action<int> cb) { }");

        Assert.True(verdict.Passed);
        Assert.Equal(0, verdict.CastCount);
        Assert.Equal(0, verdict.FilesWithCasts);
    }

    /// <summary>
    /// Judgement is per-file. A delegate type that exists only in some OTHER generated file does not
    /// excuse a cast here — the two halves of one closure are always emitted into the same file.
    /// </summary>
    [Fact]
    public void SurfaceFromAnotherFile_DoesNotExcuseACast()
    {
        var verdict = ClosureDelegateParityScanner.Scan(new[]
        {
            ("Other.cs", "public virtual void Emit( global::System.Action<Swift.SwiftArray<double>> sink) { }"),
            ("Sample.cs", "var d = SwiftClosureMarshaller.GetDelegateFromContext<global::System.Action<Swift.SwiftArray<double>>>(ctx);"),
        });

        Assert.False(verdict.Passed);
        Assert.Equal("Sample.cs", Assert.Single(verdict.Mismatches).File);
    }

    /// <summary>
    /// Every cast in a file is judged, not just the first — a file typically carries one trampoline
    /// per closure-bearing member.
    /// </summary>
    [Fact]
    public void MultipleCastsInOneFile_AreAllJudged()
    {
        const string source = """
            public virtual void Good( global::System.Action<int> a) { }
            public virtual void Bad( global::System.Action<Swift.SwiftArray<double>> b) { }
            var g = SwiftClosureMarshaller.GetDelegateFromContext<global::System.Action<int>>(ctx);
            var b = SwiftClosureMarshaller.GetDelegateFromContext<global::System.Action<IReadOnlyList<double>>>(ctx);
            """;

        var verdict = ClosureDelegateParityScanner.ScanFile("Sample.cs", source);

        Assert.Equal(2, verdict.CastCount);
        Assert.Equal(1, verdict.FilesWithCasts);
        Assert.Contains("IReadOnlyList<double>", Assert.Single(verdict.Mismatches).CastType);
    }

    /// <summary>
    /// A bare `Action` (no type arguments) is a delegate type in its own right and must round-trip,
    /// so a void closure is not reported as a divergence.
    /// </summary>
    [Fact]
    public void BareActionDelegate_RoundTrips()
    {
        const string source = """
            public virtual void Run( global::System.Action cb) { }
            var d = SwiftClosureMarshaller.GetDelegateFromContext<global::System.Action>(ctx);
            """;

        var verdict = ClosureDelegateParityScanner.ScanFile("Sample.cs", source);

        Assert.True(verdict.Passed);
        Assert.Equal(1, verdict.CastCount);
    }
}
