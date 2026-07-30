// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.IO;
using System.Text;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Regen-output parity gate for PAT-existential return projection (the PAT-existential
/// return-degradation defect).
///
/// A protocol-with-associated-type (PAT) existential with known conformers projects to the read-only
/// <c>Swift.Runtime.ExistentialUnion</c> wrapper ONLY in pure-read return positions; everywhere else it
/// degrades to <c>object</c> (ExistentialUnion has no input marshalling). The "does this return project
/// to union?" decision is made at THREE emit sites — the signature builder, the return-body wrapper, and
/// the degradation-marker suppression — that must never disagree. A disagreement compiles silently
/// (ExistentialUnion is implicitly convertible to <c>object</c>), so the C# compile gate cannot catch it;
/// only an assertion on the GENERATED text can. That is what this test is for.
///
/// Specifically guards the three holes that desynced in practice:
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
    [Trait("Category", GeneratedBindingsOutputRequirement.TraitCategory)]
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

        // --- A union-projected return is NOT a degradation, so it must NOT carry the
        // `[return: OriginalSwiftType(...)]` marker (the degradation
        // oracle is direction-blind). The marker and the signature type are driven by the SAME predicate
        // (MethodEnvironment.ReturnProjectsToExistentialUnion), so a winner that projects to union in its
        // signature must also drop the marker — otherwise the wrapper claims a degradation that did not
        // happen. The marker legitimately remains on the degraded positions (settable getter `object`,
        // async `Task<object>`), so this is asserted per-winner, not globally.
        AssertNoReturnDegradationMarkerBefore(generated, $"private {Union} Attribute_Get()");
        AssertNoReturnDegradationMarkerBefore(generated, $"public static {Union} MakeColorAttribute(");
        AssertNoReturnDegradationMarkerBefore(generated, $"public static {Union} MakeSizeAttribute(");
    }

    /// <summary>
    /// Asserts that the C# member whose signature line contains <paramref name="signature"/> is NOT
    /// preceded by a <c>[return: ...OriginalSwiftType(...)]</c> degradation marker. Walks back over the
    /// contiguous run of attribute/blank lines attached to the member (the only place the marker can sit)
    /// and stops at the first line that is not an attribute, so a marker on an unrelated earlier member
    /// does not produce a false positive.
    /// </summary>
    private static void AssertNoReturnDegradationMarkerBefore(string generated, string signature)
    {
        var sigIndex = generated.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(sigIndex >= 0, $"Union-projecting signature not found in generated bindings: {signature}");

        var lines = generated.Substring(0, sigIndex).Split('\n');
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0) continue;          // skip blank lines between attributes
            if (!trimmed.StartsWith("[")) break;        // reached non-attribute code — done walking back
            Assert.DoesNotContain("OriginalSwiftType", trimmed);
        }
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
        var outputDir = Path.Combine(repoRoot, "BindingTests", "output");
        var preludePath = Path.Combine(outputDir, "SwiftBindingsTestLib.cs");

        // The module is emitted file-per-top-level-type: read the prelude plus every
        // {module}.Types.*.cs file so the projected members that moved into their own files
        // are still visible to this parity scan.
        var exists = SplitModuleSource.Exists(outputDir, "SwiftBindingsTestLib");
        GeneratedBindingsOutputRequirement.SkipUnlessAvailable(exists,
            $"Generated bindings not found at {preludePath}");

        return SplitModuleSource.ReadAll(outputDir, "SwiftBindingsTestLib");
    }

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SwiftBindings.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
