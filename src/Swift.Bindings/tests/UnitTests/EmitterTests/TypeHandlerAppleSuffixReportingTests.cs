// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Pins the report-only "Unprojected Apple type" skip-detail enrichment on the four
/// type-handler property-skip paths that gate before PropertyHandler: frozen struct,
/// non-frozen struct, enum-with-cases (instance property), and caseless enum (static
/// property). The enrichment is deliberately report-only — generated C# must not carry
/// the suffix, so a compared artifact does not move when Apple-type naming improves.
/// </summary>
public class TypeHandlerAppleSuffixReportingTests
{
    private const string ModuleName = "AppleSuffixFixture";

    // Distinct property names so a failure names the handler path that regressed.
    private const string FrozenProp = "frozenImage";
    private const string NonFrozenProp = "nonFrozenImage";
    private const string EnumWithCasesProp = "enumImage";
    private const string CaselessStaticProp = "namespaceImage";

    [Fact]
    public void EmitModule_UnprojectedApplePropertyTypes_EnrichReportButNotGeneratedSource()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "swiftbind-apple-suffix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);

        BindingReport? report;
        try
        {
            var moduleDecl = BuildModule();
            var typeDatabase = FixtureModuleFactory.BuildTypeDatabase(moduleDecl);

            // CoreGraphics.CGImage must stay unregistered: it is a non-auto-bridge Apple
            // module type, so CanEmitProperty fails and DescribeSuffix names it.
            Assert.False(
                typeDatabase.IsTypeProcessed(new NamedTypeSpec("CoreGraphics.CGImage")),
                "Fixture must leave CoreGraphics.CGImage unresolved so the skip suffix fires.");

            ReportCollector.Reset();
            ReportCollector.Start(moduleDecl);
            try
            {
                new StringEmitter(scratch, typeDatabase, new NullLoggerFactory())
                    .EmitModule(moduleDecl, new ModuleEmissionContext());
            }
            finally
            {
                report = ReportCollector.Complete();
                ReportCollector.Reset();
            }

            Assert.NotNull(report);

            // ── assertion 1: each of the four handler paths enriches the report ──
            AssertReportHasApplePropertySkip(
                report!, FrozenProp, "FrozenStructHandler");
            AssertReportHasApplePropertySkip(
                report!, NonFrozenProp, "NonFrozenStructHandler");
            AssertReportHasApplePropertySkip(
                report!, EnumWithCasesProp, "EnumHandler (instance property on enum with cases)");
            AssertReportHasApplePropertySkip(
                report!, CaselessStaticProp, "EnumHandler (static property on caseless enum)");

            // ── assertion 2: enrichment must not leak into generated source ──
            // EmitMemberSkipped receives the unenriched skipDetails; if a path ever
            // routes DescribeSuffix into the tombstone comment, this fails.
            foreach (var path in Directory.EnumerateFiles(scratch, "*.cs"))
            {
                var content = File.ReadAllText(path);
                Assert.DoesNotContain(
                    "Unprojected Apple type",
                    content,
                    StringComparison.Ordinal);
            }
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); }
            catch (IOException) { /* best effort */ }
        }
    }

    /// <summary>
    /// Fails loudly when a property is missing from the skip report entirely (not just
    /// missing the Apple suffix). A silent no-row would make the enrichment assert vacuous.
    /// </summary>
    private static void AssertReportHasApplePropertySkip(
        BindingReport report, string propertyName, string handlerPath)
    {
        var row = report.SkippedItems.FirstOrDefault(i =>
            i.Kind == BindingItemKind.Property &&
            string.Equals(i.Name, propertyName, StringComparison.Ordinal));

        Assert.True(
            row is not null,
            $"{handlerPath}: expected a SkippedItem for property '{propertyName}', " +
            "but the report has no row for it (property was not skipped).");

        // Require the DescribeSuffix sentence, not merely the type name: the unenriched
        // CanEmitProperty detail already spells "CoreGraphics.CGImage", so grepping only
        // for that name would pass even when the report-only enrichment is absent.
        Assert.True(
            row!.Details is not null &&
            row.Details.Contains("Unprojected Apple type", StringComparison.Ordinal) &&
            row.Details.Contains("CoreGraphics.CGImage", StringComparison.Ordinal),
            $"{handlerPath}: SkippedItem for '{propertyName}' is missing the report-only " +
            $"'Unprojected Apple type' / 'CoreGraphics.CGImage' enrichment " +
            $"(got: {row.Details ?? "<null>"}).");
    }

    // ── fixture ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Four parent shapes, each with one property typed CoreGraphics.CGImage:
    /// frozen struct, non-frozen struct, enum with cases (instance), caseless enum (static).
    /// Ownership stitching mirrors FixtureModuleFactory.Reparent / OwnProperty so the
    /// emitter can resolve declaring types from property accessors.
    /// </summary>
    private static ModuleDecl BuildModule()
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

        var cgImage = new NamedTypeSpec("CoreGraphics.CGImage");

        var frozen = Struct("FrozenCarrier", moduleDecl, isFrozen: true);
        frozen.Properties.Add(TestDecls.Property(FrozenProp, cgImage, module: ModuleName));
        moduleDecl.Types.Add(frozen);

        var nonFrozen = Struct("OpaqueCarrier", moduleDecl, isFrozen: false);
        nonFrozen.Properties.Add(TestDecls.Property(NonFrozenProp, cgImage, module: ModuleName));
        moduleDecl.Types.Add(nonFrozen);

        // At least two no-payload cases so the type is not the single-case zero-size skip,
        // and so emission reaches the instance-property loop (not the caseless namespace path).
        var withCases = EnumWithCases("TaggedCarrier", moduleDecl, "alpha", "beta");
        withCases.Properties.Add(TestDecls.Property(EnumWithCasesProp, cgImage, module: ModuleName));
        moduleDecl.Types.Add(withCases);

        // Zero cases + a static member → EmitNamespaceEnum, which only walks static properties.
        var caseless = CaselessEnum("NamespaceCarrier", moduleDecl);
        caseless.Properties.Add(
            TestDecls.Property(CaselessStaticProp, cgImage, isStatic: true, module: ModuleName));
        moduleDecl.Types.Add(caseless);

        foreach (var type in moduleDecl.Types)
            Reparent(type, moduleDecl);

        return moduleDecl;
    }

    private static StructDecl Struct(string name, ModuleDecl moduleDecl, bool isFrozen) => new()
    {
        Name = name,
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
        MangledName = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{name.Length}{name}VN",
        MetadataAccessor = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{name.Length}{name}VMa",
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Operators = new List<OperatorDecl>(),
        Subscripts = new List<SubscriptDecl>(),
        GenericParameters = new List<GenericArgumentDecl>(),
        Conformances = new List<TypeConformance>(),
        IsFrozen = isFrozen,
        ParentDecl = moduleDecl,
        ModuleDecl = moduleDecl,
    };

    private static EnumDecl EnumWithCases(string name, ModuleDecl moduleDecl, params string[] caseNames)
    {
        var cases = new List<EnumCaseDecl>();
        foreach (var caseName in caseNames)
        {
            cases.Add(new EnumCaseDecl
            {
                Name = caseName,
                MangledName = "",
                ParentDecl = null,
                ModuleDecl = moduleDecl,
            });
        }

        return new EnumDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{name.Length}{name}ON",
            IsFrozen = true,
            Cases = cases,
            GenericParameters = new List<GenericArgumentDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            Conformances = new List<TypeConformance>(),
            MetadataAccessor = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{name.Length}{name}OMa",
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };
    }

    private static EnumDecl CaselessEnum(string name, ModuleDecl moduleDecl) => new()
    {
        Name = name,
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
        MangledName = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{name.Length}{name}ON",
        IsFrozen = true,
        Cases = new List<EnumCaseDecl>(),
        GenericParameters = new List<GenericArgumentDecl>(),
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Operators = new List<OperatorDecl>(),
        Subscripts = new List<SubscriptDecl>(),
        Conformances = new List<TypeConformance>(),
        MetadataAccessor = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{name.Length}{name}OMa",
        ParentDecl = moduleDecl,
        ModuleDecl = moduleDecl,
    };

    // Same ownership stitching as FixtureModuleFactory: ParentDecl/ModuleDecl on the type,
    // each member, and each property accessor's backing method (the emitter reads those).
    private static void Reparent(TypeDecl type, ModuleDecl moduleDecl)
    {
        Own(type, moduleDecl, moduleDecl);
        foreach (var method in type.Methods)
            Own(method, type, moduleDecl);
        foreach (var property in type.Properties)
            OwnProperty(property, type, moduleDecl);
        if (type is EnumDecl enumDecl)
        {
            foreach (var caseDecl in enumDecl.Cases)
                Own(caseDecl, type, moduleDecl);
        }
        foreach (var nested in type.Types)
            Reparent(nested, moduleDecl);
    }

    private static void OwnProperty(PropertyDecl property, BaseDecl parent, ModuleDecl moduleDecl)
    {
        Own(property, parent, moduleDecl);
        foreach (var accessor in property.Accessors)
            Own(accessor.Method, parent, moduleDecl);
    }

    private static void Own(BaseDecl decl, BaseDecl parent, ModuleDecl moduleDecl)
    {
        decl.ParentDecl = parent;
        decl.ModuleDecl = moduleDecl;
    }
}
