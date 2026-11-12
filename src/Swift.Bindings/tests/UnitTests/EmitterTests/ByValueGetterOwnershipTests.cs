// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.IO;
using System.Text;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// A getter that returns a value by value hands the caller the callee's +1 in the result local.
/// Building a copyable carrier from that local takes a second, independent copy, so the local has
/// to be value-witness-destroyed afterwards. Methods get this from the projection's Direct return
/// plan; accessor bodies are written by the return emitter's own arms, which once marshalled the
/// local by copy and left its references behind. The BindingTests destruction counts catch that at
/// run time; this pins the two accessor arms on the generated text, where a regression compiles.
/// </summary>
public class ByValueGetterOwnershipTests
{
    private const string Consuming = "SwiftMarshal.MarshalFromSwiftObjectConsuming<";

    [SkippableFact]
    [Trait("Category", GeneratedBindingsOutputRequirement.TraitCategory)]
    public void FrozenNonTrivialStructGetter_ConsumesTheReturnedValue()
    {
        var generated = LoadGeneratedBindingsOrSkip();

        var body = ExtractMethodBody(generated, "private global::SwiftBindingsTestLib.PassingCarrier Carrier_Get()");
        Assert.NotNull(body);
        Assert.Contains(Consuming + "global::SwiftBindingsTestLib.PassingCarrier>(&result)", body!);
    }

    [SkippableFact]
    [Trait("Category", GeneratedBindingsOutputRequirement.TraitCategory)]
    public void BoundGenericArrayGetter_ConsumesTheReturnedValue()
    {
        var generated = LoadGeneratedBindingsOrSkip();

        var classStart = generated.IndexOf("public partial class PassingTokenBox<T>", StringComparison.Ordinal);
        Assert.True(classStart >= 0, "PassingTokenBox<T> not found in the generated bindings");
        var body = ExtractMethodBody(generated.Substring(classStart), "private global::Swift.SwiftArray<T> Items_Get()");
        Assert.NotNull(body);
        Assert.Contains(Consuming + "global::Swift.SwiftArray<T>>(&result)", body!);
    }

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
        var exists = SplitModuleSource.Exists(outputDir, "SwiftBindingsTestLib");
        GeneratedBindingsOutputRequirement.SkipUnlessAvailable(exists,
            $"Generated bindings not found under {outputDir}");
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
