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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InitializersWhoseParameterTypesProjectAlike_TheNonDeprecatedOneKeepsTheConstructor(bool deprecatedDeclaredFirst)
    {
        // Same labels, different Swift types that resolve to one C# type (as a remapped value type
        // resolves to its primitive): the primary signature already collides, before the projected one.
        const string currentSymbol = "$s8Survivor5ScoreV11selectedKey5valueACSS_SdtcfC";
        const string legacySymbol = "$s8Survivor5ScoreV11selectedKey5valueACSS_5Remap6WeightVtcfC";
        var current = Init("value", deprecated: false, new NamedTypeSpec("Swift.Double"), currentSymbol);
        var legacy = Init("value", deprecated: true, new NamedTypeSpec("Remap.Weight"), legacySymbol);

        var (report, csharp) = Emit(
            deprecatedDeclaredFirst ? new[] { legacy, current } : new[] { current, legacy },
            typeDatabase =>
            {
                var remap = new ModuleTypeDatabase("Remap", "/fake/Remap.dylib");
                var weight = SwiftTypeName.FromModuleQualifiedName("Remap.Weight");
                remap.RegisterType(weight, new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Double"),
                    SwiftTypeName = weight,
                    MetadataAccessor = "$s5Remap6WeightVMa",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct,
                });
                typeDatabase.AddModuleDatabase(remap);
            });

        var dropped = Assert.Single(report.SkippedItems, i => i.Reason == SkipReason.DuplicateSignature);
        Assert.Contains("Remap.Weight", dropped.DeclId);
        Assert.Contains($"EntryPoint = \"{currentSymbol}\"", csharp);
        Assert.DoesNotContain(legacySymbol, csharp);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASurvivorReorder_NeverDropsAConstructorOutsideTheFamily(bool outsiderHasTrailingParameter)
    {
        // init(old:) and the second init(value:) project alike (an Action<nint> parameter), so they
        // form a family whose preferred member is the second init(value:). The primary signature reads
        // both closures as one type, though, so moving that member ahead of init(value: () -> ()) would
        // drop the latter, a constructor with a C# signature of its own. The reorder is refused and
        // the declared order decides, as it did before any sort. The outsider can also carry a trailing
        // defaulted parameter this validation rejects, which the loop can drop to emit the constructor
        // without it, so a label prefix of a constructor outside the family blocks the move as well. (A
        // required trailing parameter cannot be dropped and blocks nothing; see the next test.)
        const string oldSymbol = "$s8Survivor5ScoreV11selectedKey3oldACSS_ySictcfC";
        const string plainSymbol = "$s8Survivor5ScoreV11selectedKey5valueACSS_yyctcfC";
        const string intSymbol = "$s8Survivor5ScoreV11selectedKey5valueACSS_ySictcfC";
        var legacy = Init("old", deprecated: true, Closure("(Swift.Int)->()"), oldSymbol);
        var plain = Init("value", deprecated: false, Closure("()->()"), plainSymbol);
        if (outsiderHasTrailingParameter)
            plain.CSSignature.Add(TestDecls.Param("edges", new NamedTypeSpec("Remap.Unbound"), hasDefault: true));
        var withInt = Init("value", deprecated: false, Closure("(Swift.Int)->()"), intSymbol);

        var (_, csharp) = Emit(new[] { legacy, plain, withInt });

        Assert.Contains($"EntryPoint = \"{oldSymbol}\"", csharp);
        Assert.DoesNotContain(intSymbol, csharp);
        if (!outsiderHasTrailingParameter)
            Assert.Contains($"EntryPoint = \"{plainSymbol}\"", csharp);
    }

    [Fact]
    public void ARequiredTrailingParameterOutsideTheFamilyDoesNotBlockTheReorder()
    {
        // init(selectedKey:value:required:) shares the preferred member's labels only as a prefix, and
        // its trailing parameter has no default, so the loop can never emit it as init(selectedKey:value:)
        // and it cannot take that member's primary key. The family still reorders: the current
        // init(value:) keeps the constructor and the deprecated init(old:) is dropped.
        const string oldSymbol = "$s8Survivor5ScoreV11selectedKey3oldACSS_ySictcfC";
        const string intSymbol = "$s8Survivor5ScoreV11selectedKey5valueACSS_ySictcfC";
        const string requiredSymbol = "$s8Survivor5ScoreV11selectedKey5value8requiredACSS_SiSbtcfC";
        var legacy = Init("old", deprecated: true, Closure("(Swift.Int)->()"), oldSymbol);
        var withRequired = Init("value", deprecated: false, symbol: requiredSymbol);
        withRequired.CSSignature.Add(TestDecls.Param("required", new NamedTypeSpec("Swift.Bool")));
        var withInt = Init("value", deprecated: false, Closure("(Swift.Int)->()"), intSymbol);

        var (report, csharp) = Emit(new[] { legacy, withRequired, withInt });

        Assert.Contains($"EntryPoint = \"{intSymbol}\"", csharp);
        Assert.Contains($"EntryPoint = \"{requiredSymbol}\"", csharp);
        Assert.DoesNotContain(oldSymbol, csharp);
        var dropped = Assert.Single(report.SkippedItems, i => i.Reason == SkipReason.DuplicateSignature);
        Assert.Contains("old", dropped.DeclId);
    }

    [Fact]
    public void OnlyThePreferredInitializerAndThoseBeforeItMove()
    {
        // The preferred init(modern:) moves ahead of the deprecated init(old:) and init(stale:). The
        // losing init(value:) of the same family stays behind init(value: () -> ()), whose primary
        // signature it shares, so that constructor still emits.
        const string valueIntSymbol = "$s8Survivor5ScoreV11selectedKey5valueACSS_ySictcfC";
        const string valuePlainSymbol = "$s8Survivor5ScoreV11selectedKey5valueACSS_yyctcfC";
        const string modernSymbol = "$s8Survivor5ScoreV11selectedKey6modernACSS_ySictcfC";
        var old = Init("old", deprecated: true, Closure("(Swift.Int)->()"), "$s8Survivor5ScoreV11selectedKey3oldACSS_ySictcfC");
        var stale = Init("stale", deprecated: true, Closure("(Swift.Int)->()"), "$s8Survivor5ScoreV11selectedKey5staleACSS_ySictcfC");
        var valuePlain = Init("value", deprecated: false, Closure("()->()"), valuePlainSymbol);
        var modern = Init("modern", deprecated: false, Closure("(Swift.Int)->()"), modernSymbol);
        var valueInt = Init("value", deprecated: false, Closure("(Swift.Int)->()"), valueIntSymbol);

        var (_, csharp) = Emit(new[] { old, stale, valuePlain, modern, valueInt });

        Assert.Contains($"EntryPoint = \"{modernSymbol}\"", csharp);
        Assert.Contains($"EntryPoint = \"{valuePlainSymbol}\"", csharp);
        Assert.DoesNotContain(valueIntSymbol, csharp);
        Assert.DoesNotContain("selectedKey3old", csharp);
        Assert.DoesNotContain("selectedKey5stale", csharp);
    }

    private static TypeSpec Closure(string signature) => TypeSpecParser.Parse("@escaping[] " + signature)!;

    private static MethodDecl Init(string secondLabel, bool deprecated, TypeSpec? secondType = null, string? symbol = null)
    {
        var init = TestDecls.Method(
            "init",
            methodType: MethodType.Static,
            isConstructor: true,
            parameters: new[]
            {
                TestDecls.Param("selectedKey", new NamedTypeSpec("Swift.String")),
                TestDecls.Param(secondLabel, secondType ?? new NamedTypeSpec("Swift.Int")),
            },
            returnType: new NamedTypeSpec($"{ModuleName}.Score"),
            module: ModuleName) with
        {
            MangledName = symbol ?? Symbol(secondLabel),
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

    private (BindingReport Report, string CSharp) Emit(IEnumerable<MethodDecl> inits, Action<TypeDatabase>? extendTypeDatabase = null)
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
        extendTypeDatabase?.Invoke(typeDatabase);

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
