// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.IO;
using System.Text;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Regen-output parity gate for PAT-existential return projection (architecture-review Defect E).
///
/// A protocol-with-associated-type (PAT) existential with known conformers projects to the read-only
/// <c>Swift.Runtime.ExistentialUnion</c> wrapper ONLY in pure-read return positions; everywhere else it
/// degrades to <c>object</c> (ExistentialUnion has no input marshalling). The "does this return project
/// to union?" decision is made at THREE emit sites — the signature builder, the return-body wrapper, and
/// the degradation-marker suppression — that must never disagree. A disagreement compiles silently
/// (ExistentialUnion is implicitly convertible to <c>object</c>), so the C# compile gate cannot catch it;
/// only an assertion on the GENERATED text can. That is what this test is for.
///
/// Specifically guards the three holes the architecture review surfaced:
///   1. Read-write PAT property — public type stays <c>object</c>, so the backing getter must ALSO stay
///      <c>object</c> in BOTH signature and body (a desync here = a getter returning ExistentialUnion
///      under an <c>object</c> property, feeding ExistentialUnion back into the setter on round-trip).
///   2. Async PAT return — degrades to <c>Task&lt;object&gt;</c>, never <c>Task&lt;ExistentialUnion&gt;</c>.
///   3. Get-only PAT property / free function — the intended winner: projects to ExistentialUnion
///      end-to-end (guards against the centralization over-excluding and regressing the winners).
///
/// Mirrors <see cref="Issue1SkipAttributionTests"/>: reads the generated BindingTests output, skips
/// gracefully when it is absent locally, and is enforced (non-skippable) in CI via the
/// SWIFT_BINDINGS_REQUIRE_GENERATED_BINDINGTESTS_OUTPUT env gate.
/// </summary>
public class ExistentialUnionReturnProjectionParityTests
{
    private const string Union = "Swift.Runtime.ExistentialUnion";

    [SkippableFact]
    public void PatExistentialReturnProjection_SignatureAndBodyAgree_PerPosition()
    {
        var generated = LoadGeneratedBindingsOrSkip();

        // --- Finding #1: read-write PAT property MutableAttributeHolder.Current ---
        // Public property is `object` (settable: union has no input marshalling), so its backing getter
        // MUST be `object` in BOTH the signature and the body. The pre-fix bug emitted an `object`
        // signature with an `ExistentialUnion` body — caught here, missed by the compile gate.
        Assert.Contains("public object Current", generated);
        Assert.DoesNotContain($"public {Union} Current", generated);

        var currentGetBody = ExtractMethodBody(generated, "private object Current_Get()");
        Assert.False(string.IsNullOrEmpty(currentGetBody),
            "Could not find `private object Current_Get()` in the generated bindings — either the " +
            "MutableAttributeHolder fixture changed or the getter signature regressed to ExistentialUnion.");
        Assert.DoesNotContain($"new {Union}(", currentGetBody!);
        Assert.DoesNotContain($"private {Union} Current_Get()", generated);

        // --- Get-only PAT property AttributeHolder.Attribute: the intended winner ---
        // Projects to ExistentialUnion end-to-end (signature + public property). Guards against the
        // centralization over-excluding and silently degrading a legitimate union return.
        Assert.Contains($"private {Union} Attribute_Get()", generated);
        Assert.Contains($"public {Union} Attribute", generated);

        var attributeGetBody = ExtractMethodBody(generated, $"private {Union} Attribute_Get()");
        Assert.False(string.IsNullOrEmpty(attributeGetBody),
            "Could not find the get-only `Attribute_Get()` body — the winner path regressed.");
        Assert.Contains($"new {Union}(", attributeGetBody!);

        // --- Finding #2: async PAT return degrades to Task<object>, never Task<ExistentialUnion> ---
        Assert.Contains("Task<object> MakeColorAttributeAsync", generated);
        Assert.DoesNotContain($"Task<{Union}> MakeColorAttributeAsync", generated);

        // --- Free-function PAT returns: winners → ExistentialUnion ---
        Assert.Contains($"public static {Union} MakeColorAttribute(", generated);
        Assert.Contains($"public static {Union} MakeSizeAttribute(", generated);
    }

    /// <summary>
    /// Returns the source of the method whose signature line contains <paramref name="signature"/>,
    /// brace-matched from the first opening brace after the signature. Returns null if not found.
    /// </summary>
    private static string? ExtractMethodBody(string generated, string signature)
    {
        var sigIndex = generated.IndexOf(signature, StringComparison.Ordinal);
        if (sigIndex < 0) return null;

        var braceStart = generated.IndexOf('{', sigIndex);
        if (braceStart < 0) return null;

        var sb = new StringBuilder();
        int depth = 0;
        for (int i = braceStart; i < generated.Length; i++)
        {
            var c = generated[i];
            sb.Append(c);
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) break;
            }
        }
        return sb.ToString();
    }

    private static string LoadGeneratedBindingsOrSkip()
    {
        var repoRoot = LocateRepoRoot();
        var generatedBindings = Path.Combine(repoRoot, "BindingTests", "output", "SwiftBindingsTestLib.cs");

        var exists = File.Exists(generatedBindings);
        if (RequireGeneratedBindingsOutput())
            Assert.True(exists, $"Generated bindings not found at {generatedBindings}");
        Skip.IfNot(exists,
            $"Generated bindings not found at {generatedBindings}; run `nuke binding-tests --compile-only` first.");

        return File.ReadAllText(generatedBindings);
    }

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SwiftBindings.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static bool RequireGeneratedBindingsOutput()
        => string.Equals(
            Environment.GetEnvironmentVariable("SWIFT_BINDINGS_REQUIRE_GENERATED_BINDINGTESTS_OUTPUT"),
            "true",
            StringComparison.OrdinalIgnoreCase);
}
