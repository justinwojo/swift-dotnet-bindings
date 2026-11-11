// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// An XML doc comment only documents a declaration when it precedes the declaration's whole
/// attribute list. A <c>///</c> block written after any attribute is not attached to anything: the
/// compiler drops it from the documentation file and reports CS1587, which a consumer who builds a
/// binding with <c>GenerateDocumentationFile</c> and <c>TreatWarningsAsErrors</c> sees as a hard
/// error.
///
/// <para>These run the whole <see cref="StringEmitter.EmitModule"/> path over a module whose every
/// declaration carries documentation together with the attributes that travel with it (platform
/// availability, deprecation, <c>@MainActor</c>, <c>Sendable</c>), then ask the C# compiler, in
/// documentation-diagnose mode, whether any doc comment went unattached.</para>
/// </summary>
public class DocCommentPlacementTests : IDisposable
{
    private readonly List<string> _scratchDirs = new();

    public void Dispose()
    {
        foreach (var dir in _scratchDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public void EmitModule_DocumentedAndAttributedDeclarations_EveryDocCommentIsAttached()
    {
        var output = EmitDocumentedFixture();

        var unattached = UnattachedDocComments(output);

        Assert.True(unattached.Count == 0,
            $"{unattached.Count} doc comment(s) follow an attribute and are dropped (CS1587):{Environment.NewLine}" +
            string.Join(Environment.NewLine, unattached));
    }

    [Fact]
    public void EmitModule_DocCommentsAreWellFormedAndNameTheirParameters()
    {
        var output = EmitDocumentedFixture();

        var malformed = MalformedDocComments(output);

        Assert.True(malformed.Count == 0,
            $"{malformed.Count} doc comment(s) are malformed, misname their parameters or cite an ambiguous cref (CS0419, CS1570, CS1572, CS1573):{Environment.NewLine}" +
            string.Join(Environment.NewLine, malformed));
    }

    /// <summary>
    /// Guards the tests above from passing vacuously: the fixture must really put attributes and
    /// documentation on the same declarations, on both type and member declarations.
    /// </summary>
    [Fact]
    public void DocumentedFixture_PairsDocumentationWithAttributesOnTypesAndMembers()
    {
        var combined = string.Concat(EmitDocumentedFixture()
            .Where(f => f.Key.EndsWith(".cs", StringComparison.Ordinal))
            .Select(f => f.Value));

        Assert.Contains("/// Documented type Registry.", combined);
        Assert.Contains("/// Documented method fetch.", combined);
        Assert.Contains("/// Documented property name.", combined);
        Assert.Contains("SupportedOSPlatform(\"ios16.0\")", combined);
        Assert.Contains("[global::Swift.Runtime.SwiftMainActor]", combined);
        Assert.Contains("[global::Swift.SwiftSendable]", combined);
        Assert.Contains("Use reset instead", combined);
        Assert.Contains("case of Holder&lt;T&gt;", combined);
        Assert.Contains("double @base", combined);
        Assert.Contains("<param name=\"base\">The base value.</param>", combined);
        Assert.Contains("LoadAsync(", combined);
        Assert.Contains("StoreAsync(", combined);
        Assert.Contains("<param name=\"key\">The key value.</param>", combined);
    }

    /// <summary>
    /// The partially documented members reach the output in the shape the diagnostics test
    /// relies on: an undescribed parameter beside a described one, and a failable factory's
    /// <c>out</c> result, including the renamed one, each with a tag.
    /// </summary>
    [Fact]
    public void DocumentedFixture_PartialDocsTagEveryEmittedParameter()
    {
        var combined = string.Concat(EmitDocumentedFixture()
            .Where(f => f.Key.EndsWith(".cs", StringComparison.Ordinal))
            .Select(f => f.Value));

        Assert.Contains("/// Partially documented method shift.", combined);
        Assert.Contains("/// <param name=\"dx\">The horizontal offset.</param>", combined);
        Assert.Contains("/// <param name=\"dy\"></param>", combined);

        Assert.Contains("/// Partially documented failable init seed.", combined);
        Assert.Contains("/// <param name=\"seed\">The seed value.</param>", combined);
        Assert.Contains($"{XmlDocCommentEmitter.FailableResultParamText}</param>", combined);

        Assert.Contains("/// Documented failable init result.", combined);
        Assert.Contains("/// <param name=\"result\">The Swift parameter named result.</param>", combined);
        Assert.Matches("/// <param name=\"__resultOut\\d*\">The registry when result is valid.</param>", combined);

        Assert.Contains("ObserveAsync(", combined);
        Assert.Contains("/// <param name=\"key\">The observed key.</param>", combined);
    }

    /// <summary>
    /// The compiler's own verdict, so the assertion is about what a consumer's build reports rather
    /// than about the generated text's shape. Unresolved types are irrelevant here: CS1587 is a
    /// syntactic placement diagnostic and is reported regardless of binding errors.
    /// </summary>
    private static List<string> DocDiagnostics(IReadOnlyDictionary<string, string> output, string id)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview, DocumentationMode.Diagnose);
        var trees = output
            .Where(f => f.Key.EndsWith(".cs", StringComparison.Ordinal))
            .OrderBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => CSharpSyntaxTree.ParseText(f.Value, parseOptions, path: f.Key))
            .ToList();
        var compilation = CSharpCompilation.Create(
            "DocPlacement" + Guid.NewGuid().ToString("N"),
            trees,
            Array.Empty<MetadataReference>(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        return compilation.GetDiagnostics()
            .Where(d => d.Id == id)
            .Select(d =>
            {
                var span = d.Location.GetLineSpan();
                var lines = d.Location.SourceTree!.GetText().Lines;
                var line = span.StartLinePosition.Line;
                var previous = line > 0 ? lines[line - 1].ToString().Trim() : "";
                return $"  {span.Path}:{line + 1}: after `{previous}`: {d.GetMessage()}";
            })
            .ToList();
    }

    internal static List<string> UnattachedDocComments(IReadOnlyDictionary<string, string> output)
        => DocDiagnostics(output, "CS1587");

    /// <summary>
    /// CS1570, a doc comment that is not well-formed XML (an unescaped generic); CS1572, a
    /// <c>&lt;param&gt;</c> naming no parameter; CS1573, a parameter left without one while its
    /// siblings have one; CS0419, a <c>cref</c> naming a method group with more than one overload.
    /// </summary>
    internal static List<string> MalformedDocComments(IReadOnlyDictionary<string, string> output)
        => new[] { "CS0419", "CS1570", "CS1572", "CS1573" }.SelectMany(id => DocDiagnostics(output, id)).ToList();

    private Dictionary<string, string> EmitDocumentedFixture()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "swiftbind-docplacement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        _scratchDirs.Add(scratch);

        const string moduleName = "DocPlacementFixture";
        var moduleDecl = FixtureModuleFactory.BuildModule(moduleName);
        Decorate(moduleDecl);
        var typeDatabase = FixtureModuleFactory.BuildTypeDatabase(moduleDecl);

        ReportCollector.Reset();
        ReportCollector.Start(moduleDecl);
        AppleSupplementReferences.Reset();
        try
        {
            var emitter = new StringEmitter(scratch, typeDatabase, new NullLoggerFactory());
            emitter.EmitModule(moduleDecl, new ModuleEmissionContext());
        }
        finally
        {
            ReportCollector.Complete();
            ReportCollector.Reset();
        }

        return Directory.EnumerateFiles(scratch)
            .ToDictionary(Path.GetFileName!, File.ReadAllText, StringComparer.Ordinal);
    }

    /// <summary>
    /// Documents every declaration and gives it an availability floor, then layers on the
    /// attributes that carry their own remarks: <c>@MainActor</c> on the class, <c>Sendable</c> on
    /// the frozen struct, and a deprecation on one method.
    /// </summary>
    private static void Decorate(ModuleDecl moduleDecl)
    {
        // `func offset(base: Double) -> Double`: the C# parameter is the verbatim `@base`, while
        // its <param> tag must name `base`.
        var pointType = moduleDecl.Types.OfType<StructDecl>().Single(t => t.Name == "Point");
        var doubleSpec = new NamedTypeSpec("Swift.Double");
        pointType.Methods.Add(TestDecls.Method("offset", parameters: new[] { TestDecls.Param("base", doubleSpec) },
            returnType: doubleSpec, module: moduleDecl.Name) with { ParentDecl = pointType, ModuleDecl = moduleDecl });

        // `func load(key: Int) async -> Int`: the projection appends a CancellationToken the Swift
        // comment cannot describe, beside a documented `key` (CS1573 unless it gets its own tag).
        var registryType = moduleDecl.Types.OfType<ClassDecl>().Single(t => t.Name == "Registry");
        var intSpec = new NamedTypeSpec("Swift.Int");
        registryType.Methods.Add(TestDecls.Method("load", isAsync: true, parameters: new[] { TestDecls.Param("key", intSpec) },
            returnType: intSpec, module: moduleDecl.Name) with { ParentDecl = registryType, ModuleDecl = moduleDecl });
        // `func store(_ value: Int) async`: documented, but no parameter is described. Tagging the
        // token alone would leave `value` untagged beside a tagged sibling (CS1573).
        registryType.Methods.Add(TestDecls.Method("store", isAsync: true,
            parameters: new[] { TestDecls.Param("_", intSpec) with { PrivateName = "value" } },
            module: moduleDecl.Name) with { ParentDecl = registryType, ModuleDecl = moduleDecl });

        foreach (var type in moduleDecl.Types)
        {
            Document(type, "type");
            Gate(type, ios: "15.0", macos: "12.0");
            foreach (var method in type.Methods)
            {
                DocumentMethod(method);
                Gate(method);
            }
            foreach (var property in type.Properties)
            {
                Document(property, "property");
                Gate(property);
            }

            if (type is ClassDecl registry && registry.Name == "Registry")
            {
                registry.IsMainActorIsolated = true;
                var register = registry.Methods.First(m => m.Name == "register");
                register.AvailabilityAnnotations!.Add(new AvailabilityAnnotation(
                    "*", null, null, null, true, false, "Use reset instead", null));
            }

            if (type is StructDecl { Name: "Point" } point)
            {
                point.Conformances.Add(new TypeConformance(
                    point.SwiftTypeName, SwiftTypeName.FromModuleQualifiedName("Swift.Sendable"), string.Empty));
            }
        }

        foreach (var method in moduleDecl.Methods)
        {
            DocumentMethod(method);
            Gate(method);
        }

        moduleDecl.Types.Add(GenericEnum(moduleDecl));
        AddPartiallyDocumentedMembers(moduleDecl);
    }

    /// <summary>
    /// Members whose Swift comment describes only some of what the C# declaration writes, added
    /// after the blanket documentation so they keep their own:
    /// <list type="bullet">
    /// <item><c>func shift(dx: Double, dy: Double) -&gt; Double</c> describes <c>dx</c> only, so
    /// <c>dy</c> needs an empty tag of its own (CS1573).</item>
    /// <item><c>init?(seed: Int)</c> on the struct describes <c>seed</c> and has no
    /// <c>Returns:</c>, yet its <c>TryCreate</c> appends an <c>out</c> result that needs a tag.</item>
    /// <item><c>init?(result: Int)</c> on the class, a cdecl factory: the Swift parameter is
    /// already called <c>result</c>, so the <c>out</c> parameter is renamed, and the
    /// <c>Returns:</c> text must describe the renamed one rather than the Swift parameter.</item>
    /// </list>
    /// </summary>
    private static void AddPartiallyDocumentedMembers(ModuleDecl moduleDecl)
    {
        var doubleSpec = new NamedTypeSpec("Swift.Double");
        var intSpec = new NamedTypeSpec("Swift.Int");

        var point = moduleDecl.Types.OfType<StructDecl>().Single(t => t.Name == "Point");
        var shift = TestDecls.Method("shift",
            parameters: new[] { TestDecls.Param("dx", doubleSpec), TestDecls.Param("dy", doubleSpec) },
            returnType: doubleSpec, module: moduleDecl.Name) with { ParentDecl = point, ModuleDecl = moduleDecl };
        shift.Documentation = new DocComment
        {
            Summary = "Partially documented method shift.",
            Parameters = new Dictionary<string, string> { { "dx", "The horizontal offset." } },
        };
        point.Methods.Add(shift);

        var seeded = FailableInit(point, moduleDecl, "seed", intSpec, cdecl: false);
        seeded.Documentation = new DocComment
        {
            Summary = "Partially documented failable init seed.",
            Parameters = new Dictionary<string, string> { { "seed", "The seed value." } },
        };
        point.Methods.Add(seeded);

        var registry = moduleDecl.Types.OfType<ClassDecl>().Single(t => t.Name == "Registry");
        var named = FailableInit(registry, moduleDecl, "result", intSpec, cdecl: true);
        named.Documentation = new DocComment
        {
            Summary = "Documented failable init result.",
            Parameters = new Dictionary<string, string> { { "result", "The Swift parameter named result." } },
            Returns = "The registry when result is valid.",
        };
        registry.Methods.Add(named);

        // `func observe(key: Int, completion: @escaping (Int) -> Void)` beside `func observe(name: String)`:
        // the completion handler earns an `ObserveAsync` whose summary points back at its primary,
        // and `Observe` names two methods, so a bare method-group cref is ambiguous (CS0419).
        var completion = new ClosureTypeSpec(intSpec, TupleTypeSpec.Empty);
        completion.Attributes.Add(new TypeSpecAttribute("escaping"));
        var observe = TestDecls.Method("observe",
            parameters: new[] { TestDecls.Param("key", intSpec), TestDecls.Param("completion", completion) },
            module: moduleDecl.Name) with { ParentDecl = registry, ModuleDecl = moduleDecl };
        observe.Documentation = new DocComment
        {
            Summary = "Documented completion-handler method observe.",
            Parameters = new Dictionary<string, string> { { "key", "The observed key." } },
        };
        registry.Methods.Add(observe);
        var observeByName = TestDecls.Method("observe",
            parameters: new[] { TestDecls.Param("name", new NamedTypeSpec("Swift.String")) },
            module: moduleDecl.Name) with { ParentDecl = registry, ModuleDecl = moduleDecl };
        observeByName.Documentation = new DocComment { Summary = "Documented overload observe by name." };
        registry.Methods.Add(observeByName);
    }

    private static MethodDecl FailableInit(TypeDecl parent, ModuleDecl moduleDecl, string label, TypeSpec type, bool cdecl)
    {
        var init = TestDecls.Method("init", methodType: MethodType.Static, isConstructor: true,
            parameters: new[] { TestDecls.Param(label, type) },
            returnType: new NamedTypeSpec(parent.SwiftTypeName.ToString()), module: moduleDecl.Name)
            with { ParentDecl = parent, ModuleDecl = moduleDecl, IsFailable = true };
        init.UsesCdeclConstructorWrapper = cdecl;
        foreach (var arg in init.CSSignature)
            arg.ModuleDecl = moduleDecl;
        return init;
    }

    /// <summary>
    /// <c>enum Holder&lt;T&gt; { case empty; case value(T) }</c>: the emitter writes its own summary for
    /// each case and names the enum by its C# spelling, <c>Holder&lt;T&gt;</c>, so that text must be
    /// escaped to stay well-formed XML.
    /// </summary>
    private static EnumDecl GenericEnum(ModuleDecl moduleDecl)
    {
        const string name = "Holder";
        var moduleName = moduleDecl.Name;
        var holder = new EnumDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{name}"),
            MangledName = $"$s{moduleName.Length}{moduleName}{name.Length}{name}ON",
            MetadataAccessor = $"$s{moduleName.Length}{moduleName}{name.Length}{name}OMa",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>()),
            },
            Conformances = new List<TypeConformance>(),
            Cases = new List<EnumCaseDecl>(),
            IsFrozen = false,
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };
        foreach (var (caseName, payload) in new[] { ("empty", new List<TypeSpec>()), ("value", new List<TypeSpec> { new NamedTypeSpec("τ_0_0") }) })
        {
            holder.Cases.Add(new EnumCaseDecl
            {
                Name = caseName,
                MangledName = $"$s{moduleName.Length}{moduleName}{name.Length}{name}O{caseName.Length}{caseName}yxcAEmlF",
                AssociatedValues = payload,
                ParentDecl = holder,
                ModuleDecl = moduleDecl,
            });
        }
        Document(holder, "type");
        return holder;
    }

    private static void Document(BaseDecl decl, string kind)
        => decl.Documentation = new DocComment { Summary = $"Documented {kind} {decl.Name}." };

    private static void DocumentMethod(MethodDecl method)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in method.CSSignature.Skip(1))
        {
            if (!string.IsNullOrEmpty(parameter.Name) && parameter.Name != "_")
                parameters.TryAdd(parameter.Name, $"The {parameter.Name} value.");
        }
        method.Documentation = new DocComment
        {
            Summary = $"Documented method {method.Name}.",
            Parameters = parameters,
        };
    }

    /// <summary>
    /// Members get a floor above their type's, so the availability-delta emitter writes their
    /// attributes too rather than leaving them implied by the type.
    /// </summary>
    private static void Gate(BaseDecl decl, string ios = "16.0", string macos = "13.0")
        => decl.AvailabilityAnnotations = new List<AvailabilityAnnotation>
        {
            new("iOS", ios, null, null, false, false, null, null),
            new("macOS", macos, null, null, false, false, null, null),
        };
}
