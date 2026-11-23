// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Two initializers of a type with no static-factory recovery (a non-frozen struct) that project to one
/// C# constructor signature: one keeps the constructor and the other is reported as a duplicate. Which
/// one survives must not follow declaration order. A deprecated initializer yields to one that is not,
/// and among equals the first declared keeps the constructor, so `new T(…)` reaches the initializer the
/// library recommends wherever the .swiftinterface happens to list it.
/// </summary>
public sealed class ConstructorCollisionSurvivorTests : IDisposable
{
    private const string ModuleName = "Survivor";
    private readonly List<string> _scratchDirs = new();

    public void Dispose()
    {
        foreach (var dir in _scratchDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CollidingInitializers_TheNonDeprecatedOneKeepsTheConstructor(bool deprecatedDeclaredFirst)
    {
        var current = Init("probabilities", deprecated: false);
        var legacy = Init("levelProbabilities", deprecated: true);

        var (report, csharp) = Emit(deprecatedDeclaredFirst ? new[] { legacy, current } : new[] { current, legacy });

        var dropped = Assert.Single(report.SkippedItems, i => i.Reason == SkipReason.DuplicateSignature);
        Assert.Equal($"{ModuleName}.Score", dropped.ContainingType);
        Assert.Contains("levelProbabilities:", dropped.DeclId);
        Assert.Contains(EntryPoint("probabilities"), csharp);
        Assert.DoesNotContain(Symbol("levelProbabilities"), csharp);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CollidingInitializers_OfEqualStanding_TheFirstDeclaredKeepsTheConstructor(bool bothDeprecated)
    {
        var first = Init("alpha", deprecated: bothDeprecated);
        var second = Init("beta", deprecated: bothDeprecated);

        var (report, csharp) = Emit(new[] { first, second });

        var dropped = Assert.Single(report.SkippedItems, i => i.Reason == SkipReason.DuplicateSignature);
        Assert.Contains("beta:", dropped.DeclId);
        Assert.Contains(EntryPoint("alpha"), csharp);
        Assert.DoesNotContain(Symbol("beta"), csharp);
    }

    private static MethodDecl Init(string secondLabel, bool deprecated)
    {
        var init = TestDecls.Method(
            "init",
            methodType: MethodType.Static,
            isConstructor: true,
            parameters: new[]
            {
                TestDecls.Param("selectedKey", new NamedTypeSpec("Swift.String")),
                TestDecls.Param(secondLabel, new NamedTypeSpec("Swift.Int")),
            },
            returnType: new NamedTypeSpec($"{ModuleName}.Score"),
            module: ModuleName) with
        {
            MangledName = Symbol(secondLabel),
        };
        if (deprecated)
        {
            init.AvailabilityAnnotations = new List<AvailabilityAnnotation>
            {
                new(Platform: "*", IntroducedVersion: null, DeprecatedVersion: null, ObsoletedVersion: null,
                    IsUnconditionallyDeprecated: true, IsUnconditionallyUnavailable: false,
                    Message: null, Renamed: null),
            };
        }
        return init;
    }

    // `init(selectedKey: String, <label>: Int)` on Survivor.Score.
    private static string Symbol(string secondLabel)
        => $"$s8Survivor5ScoreV11selectedKey{secondLabel.Length}{secondLabel}ACSS_SitcfC";

    // The constructor's P/Invoke binds the initializer's own symbol, so the entry point names the survivor.
    private static string EntryPoint(string secondLabel) => $"EntryPoint = \"{Symbol(secondLabel)}\"";

    private (BindingReport Report, string CSharp) Emit(IEnumerable<MethodDecl> inits)
    {
        var moduleDecl = new ModuleDecl
        {
            Name = ModuleName,
            Dependencies = new List<string>(),
            Types = new List<TypeDecl>(),
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
        };
        var score = new StructDecl
        {
            Name = "Score",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{ModuleName}.Score"),
            MangledName = "$s8Survivor5ScoreVN",
            MetadataAccessor = "$s8Survivor5ScoreVMa",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            IsFrozen = false,
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };
        foreach (var init in inits)
        {
            init.ParentDecl = score;
            init.ModuleDecl = moduleDecl;
            score.Methods.Add(init);
        }
        moduleDecl.Types.Add(score);
        var typeDatabase = FixtureModuleFactory.BuildTypeDatabase(moduleDecl);

        var scratch = Path.Combine(Path.GetTempPath(), "swiftbind-ctor-survivor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        _scratchDirs.Add(scratch);

        ReportCollector.Reset();
        ReportCollector.Start(moduleDecl);
        BindingReport? report;
        try
        {
            new StringEmitter(scratch, typeDatabase, new NullLoggerFactory())
                .EmitModule(moduleDecl, new ModuleEmissionContext());
            report = ReportCollector.Complete();
        }
        finally
        {
            ReportCollector.Reset();
        }
        Assert.NotNull(report);

        var csharp = string.Concat(Directory.EnumerateFiles(scratch, "*.cs").Select(File.ReadAllText));
        return (report!, csharp);
    }
}
