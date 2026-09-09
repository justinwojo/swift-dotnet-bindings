// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// End-of-pipeline checks for the collision shapes: a member whose parameter is spelled exactly
/// like a local the marshalling code mints into the same scope.
///
/// <para>The property asserted is the one the C# compiler cares about — every identifier the
/// emitted member DECLARES is distinct, and the parameter keeps the name it projects to — rather
/// than any particular escaped spelling. That keeps the tests indifferent to which local moved and
/// what it moved to, while still failing on the shape that used to emit uncompilable C#.</para>
/// </summary>
public class GeneratedLocalNameCollisionEmitterTests
{
    // The fixed-spelling locals this member shape actually mints into its body. A parameter
    // projected onto any of them lands in the same scope as the local.
    [Theory]
    [InlineData("returnMetadata")]
    [InlineData("swiftIndirectResult")]
    [InlineData("success")]
    public void ParameterSpelledLikeAFixedBodyLocal_EmitsDistinctDeclarations(string parameterName)
    {
        var cs = EmitInstanceMethod(parameterName, isAsync: false);

        AssertNoIdentifierDeclaredTwice(cs);

        // The local was moved aside rather than dropped: the colliding member still declares
        // exactly as many locals as the member that collides with nothing.
        var control = EmitInstanceMethod("count", isAsync: false);
        Assert.Equal(DeclaredLocals(control).Count, DeclaredLocals(cs).Count);
    }

    [Theory]
    [InlineData("returnMetadata")]
    [InlineData("swiftIndirectResult")]
    [InlineData("success")]
    public void ParameterSpelledLikeAFixedBodyLocal_ParameterIsNotTheOneThatMoves(string parameterName)
    {
        // The user-facing name is part of the public surface, so it must never be the identifier
        // that gets escaped — the generated local is.
        var cs = EmitInstanceMethod(parameterName, isAsync: false);

        Assert.Contains(parameterName, PublicSignatureParameterNames(cs, "Compute"));
        Assert.DoesNotContain(parameterName, DeclaredLocals(cs));
    }

    [Fact]
    public void AsyncMemberWithParameterSpellingTheToken_EmitsTwoDistinctParameters()
    {
        var cs = EmitInstanceMethod("cancellationToken", isAsync: true);

        var parameters = PublicSignatureParameterNames(cs, "Async");
        Assert.Equal(2, parameters.Count);
        Assert.Contains("cancellationToken", parameters);
        Assert.Equal(parameters.Count, parameters.Distinct().Count());
    }

    [Fact]
    public void AsyncMemberWithoutACollision_KeepsThePlainTokenName()
    {
        // The control: nothing collides, so the appended parameter is spelled exactly as before and
        // the emitted signature is unchanged.
        var cs = EmitInstanceMethod("count", isAsync: true);

        var parameters = PublicSignatureParameterNames(cs, "Async");
        Assert.Contains("cancellationToken", parameters);
        Assert.Contains("count", parameters);
    }

    [Fact]
    public void NonCollidingMember_DeclaresItsLocalsUnderThePreferredSpellings()
    {
        // Guards the "no collision changes nothing" half of the contract at the emitter layer: an
        // ordinary member still declares the plain spellings the corpus has always carried, and
        // nothing is escaped when nothing collides.
        var cs = EmitInstanceMethod("count", isAsync: false);

        AssertNoIdentifierDeclaredTwice(cs);

        var locals = DeclaredLocals(cs);
        Assert.Contains("returnMetadata", locals);
        Assert.Contains("swiftIndirectResult", locals);
        Assert.Contains("success", locals);
        Assert.DoesNotContain(locals, n => n.StartsWith("__", StringComparison.Ordinal));
    }

    [Fact]
    public void TheDuplicateCheckSeesDeclarationsOfEveryTypeShape()
    {
        // A self-check on the detector the assertions above rely on: if it only recognised a fixed
        // list of type keywords, a redeclaration under any other type would pass unnoticed. The
        // nullable and verbatim shapes are here because generated locals do take both.
        const string snippet = """
            public void M(int p)
            {
                var a = 1;
                global::Swift.Runtime.TypeMetadata b = default;
                System.Collections.Generic.List<string> c = null;
                byte* d = stackalloc byte[4];
                TryGet(out var e);
                TryGet(out SomeType f);
                object? g = null;
                byte* @h = stackalloc byte[4];
            }
            """;

        var locals = DeclaredLocals(snippet);

        Assert.Equal(new[] { "a", "b", "c", "d", "e", "f", "g", "h" }, locals.OrderBy(n => n, StringComparer.Ordinal));
        Assert.Contains("p", DeclaredParameters(snippet));
    }

    // =====================================================================================

    /// <summary>
    /// Every local the emitted member declares, in order. Matches any declaration of the form
    /// <c>&lt;type&gt; &lt;name&gt; =</c> — inferred (<c>var</c>), explicitly typed with a
    /// namespace-qualified, generic or pointer type, and <c>out var x</c> / <c>out T x</c>
    /// declarations alike. The earlier version listed a handful of type keywords by name, so a
    /// local declared with any other type was invisible to the duplicate check.
    /// </summary>
    private static List<string> DeclaredLocals(string cs)
    {
        // A type is an identifier possibly carrying ::, ., generic arguments, [], ? and *.
        const string TypePattern = @"(?:var|[A-Za-z_][\w.:]*(?:<[^;=\r\n]*?>)?(?:\s*\[\s*\])?\??(?:\s*\*)*)";

        var declarations = Regex.Matches(cs, @"(?<![\w.])" + TypePattern + @"\s+(@?[A-Za-z_]\w*)\s*(?==[^=]|=$)")
            .Select(m => m.Groups[1].Value);

        // `out var x` / `out T x` introduce a local without an initializer.
        var outDeclarations = Regex.Matches(cs, @"\bout\s+" + TypePattern + @"\s+(@?[A-Za-z_]\w*)")
            .Select(m => m.Groups[1].Value);

        // `fixed (byte* p = ...)` and `stackalloc`-initialized pointers are covered by the first
        // pattern; `foreach (var x in ...)` is not a redeclaration hazard the emitters produce.
        // The verbatim marker is stripped: `@event` and `event` are one C# identifier, so a local
        // that carries it still redeclares a parameter that does not.
        return declarations.Concat(outDeclarations).Select(StripVerbatim).ToList();
    }

    /// <summary>
    /// Parameter names of the emitted PUBLIC member signatures — the identifiers the emitted body
    /// actually shares a scope with. A local that redeclares one of these is exactly as broken as
    /// one that redeclares another local, so the duplicate check has to see both.
    /// <para>The <c>private static partial</c> P/Invoke declarations are deliberately excluded:
    /// their parameter list is its own scope with no body, binding is positional, and a name
    /// shared with a wrapper-body local there is not a compile error.</para>
    /// </summary>
    private static List<string> DeclaredParameters(string cs)
        => Regex.Matches(cs, @"\bpublic\s+(?!.*\bpartial\b)[^\r\n;{}]*?\(([^)]*)\)")
            .SelectMany(m => m.Groups[1].Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(param => param.Split('=', 2)[0].Trim())
                .Where(param => param.Contains(' '))
                .Select(param => StripVerbatim(param.Split(' ', StringSplitOptions.RemoveEmptyEntries).Last())))
            .ToList();

    /// <summary>
    /// The identifier without its verbatim <c>@</c> marker. The marker is spelling, not identity:
    /// <c>@event</c> and <c>event</c> name the same thing to the compiler.
    /// </summary>
    private static string StripVerbatim(string name)
        => name.StartsWith("@", StringComparison.Ordinal) ? name.Substring(1) : name;

    /// <summary>
    /// One member is emitted per test, so a repeated declared identifier here is exactly what the
    /// compiler reports as a redeclaration — whether the two declarations are two locals (CS0128)
    /// or a local and a parameter of the member it sits in (CS0136).
    /// </summary>
    private static void AssertNoIdentifierDeclaredTwice(string cs)
    {
        var locals = DeclaredLocals(cs);

        var duplicateLocals = locals.GroupBy(n => n, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.Empty(duplicateLocals);

        var shadowedParameters = locals.Intersect(DeclaredParameters(cs), StringComparer.Ordinal).ToList();

        Assert.Empty(shadowedParameters);
    }

    /// <summary>Parameter names of the first public method whose name ends with <paramref name="suffix"/>.</summary>
    private static List<string> PublicSignatureParameterNames(string cs, string suffix)
    {
        var match = Regex.Match(cs, @"public\s+[^\r\n(]*" + Regex.Escape(suffix) + @"\(([^)]*)\)");
        Assert.True(match.Success, $"expected a public '...{suffix}' member in the emitted output");

        return match.Groups[1].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split('=', 2)[0].Trim())
            .Select(p => p.Split(' ', StringSplitOptions.RemoveEmptyEntries).Last())
            .ToList();
    }

    /// <summary>
    /// One instance method on a class, taking a single <c>Int</c> parameter with the requested name
    /// and returning a non-frozen struct — the shape that mints the indirect-result body locals.
    /// </summary>
    private static string EmitInstanceMethod(string parameterName, bool isAsync)
    {
        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Dependencies = new List<string>(),
            Types = new List<TypeDecl>(),
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var parentDecl = new ClassDecl
        {
            Name = "Pipeline",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Pipeline"),
            MangledName = "$s10TestModule8PipelineCN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Types.Add(parentDecl);

        const string returnTypeName = "TestModule.WideResult";
        var csSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec(returnTypeName),
                Name = string.Empty,
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl
            },
            new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec("Swift.Int32"),
                Name = parameterName,
                PrivateName = parameterName,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl
            }
        };

        var methodDecl = new MethodDecl
        {
            Name = "compute",
            MangledName = "$s10TestModule8PipelineC7computeyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = isAsync,
            IsSynthesizedAccessor = false
        };

        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/fake/path");
        module.RegisterType(parentDecl.SwiftTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Pipeline"),
            SwiftTypeName = parentDecl.SwiftTypeName,
            MetadataAccessor = "$s10TestModule8PipelineCMa",
            Flags = TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class
        });

        var returnSwiftTypeName = SwiftTypeName.FromModuleQualifiedName(returnTypeName);
        module.RegisterType(returnSwiftTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "WideResult"),
            SwiftTypeName = returnSwiftTypeName,
            MetadataAccessor = "$s10TestModule11WideResultVMa",
            Flags = TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Struct
        });

        typeDatabase.AddModuleDatabase(module);

        // The parameter's scalar type lives in the Swift module, so it resolves out of the real
        // Swift database rather than the fixture's own module database.
        typeDatabase.LoadModuleDatabaseFromFile(
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Swift", "SwiftDatabase.xml")).Wait();

        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        var env = handler.Marshal(methodDecl, typeDatabase);
        handler.Emit(
            new CSharpWriter(csStringWriter),
            new SwiftWriter(swiftStringWriter),
            env,
            new Conductor(new NullLoggerFactory()),
            new TypeHandlerContext(null, new(), null, EmissionContext: new ModuleEmissionContext()));

        return csStringWriter.ToString();
    }
}
