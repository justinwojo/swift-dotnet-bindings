// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="NamespaceFacadeEmitter.Emit"/>.
///
/// The detector tests (<see cref="NamespaceFacadeDetectorTests"/>) lock the
/// predicate that gates whether a type takes the namespace-lift path. These
/// tests lock the *emitted output* — that a facade-shaped declaration emits
/// a `namespace {Name} { ... }` block (not `static partial class`) and that
/// nested types are passed to the supplied recurse callback. Emission-context
/// nesting-stack behavior is not asserted here.
///
/// See <c>bug-0.10.0-namespace-facade-as-static-class.md</c> and S6 in
/// <c>0.11.0-session-plan.md</c> — downstream consumers (CryptoKit, Nuke,
/// BlinkID) write `using Module.Facade;` against this shape, so a regression
/// to `static partial class` would silently break their compile.
/// </summary>
public class NamespaceFacadeEmitterTests
{
    #region Positive — emitted shape

    [Fact]
    public void Emit_StructFacade_WritesNamespaceBlock()
    {
        var nested = CreateNestedStruct("Inner");
        var facade = CreateStruct("BlinkIDSDK", nestedTypes: new[] { nested });

        var output = EmitToString(facade);

        Assert.Contains("namespace BlinkIDSDK", output);
        Assert.DoesNotContain("static partial class BlinkIDSDK", output);
        Assert.DoesNotContain("partial class BlinkIDSDK", output);
    }

    [Fact]
    public void Emit_EnumFacade_WritesNamespaceBlock()
    {
        // Caseless enum with nested types is the canonical Swift
        // "uninhabited enum as namespace" idiom (HPKE, AES, Insecure in
        // CryptoKit; ImageProcessors / ImageDecoders in Nuke). Emit as a
        // real C# namespace, never `static partial class`.
        var nested = CreateNestedStruct("Inner");
        var facade = CreateEnum("HPKE", nestedTypes: new[] { nested });

        var output = EmitToString(facade);

        Assert.Contains("namespace HPKE", output);
        Assert.DoesNotContain("static partial class HPKE", output);
        Assert.DoesNotContain("partial class HPKE", output);
    }

    [Fact]
    public void Emit_OpensAndClosesBraces()
    {
        var nested = CreateNestedStruct("Inner");
        var facade = CreateEnum("Constants", nestedTypes: new[] { nested });

        var output = EmitToString(facade);

        // One open brace, one close brace at the facade scope. The body is
        // empty because the test recurse callback is a no-op.
        Assert.Equal(1, EmitterTestHelpers.CountOccurrences(output, "{"));
        Assert.Equal(1, EmitterTestHelpers.CountOccurrences(output, "}"));
    }

    [Fact]
    public void Emit_PassesNestedTypesToRecurseCallback()
    {
        var inner1 = CreateNestedStruct("Inner1");
        var inner2 = CreateNestedStruct("Inner2");
        var facade = CreateEnum("Facade", nestedTypes: new[] { inner1, inner2 });

        var recursedDecls = new List<BaseDecl>();
        EmitToString(facade, recurseCapture: (decls, _) => recursedDecls.AddRange(decls));

        Assert.Equal(2, recursedDecls.Count);
        Assert.Contains(inner1, recursedDecls);
        Assert.Contains(inner2, recursedDecls);
    }

    #endregion

    #region Negative — facade gate honored

    [Fact]
    public void Gate_StructWithStoredProperty_IsNotFacade()
    {
        // Belt-and-suspenders on top of NamespaceFacadeDetectorTests: a
        // struct with a stored property carries runtime semantics and must
        // route through the normal struct handler (which emits `partial
        // class HasField`), never the namespace-lift path. Locks the
        // call-site gate in IHandler.cs so a future refactor that drops
        // the `IsNamespaceFacade` predicate can't accidentally namespace-ify
        // a real struct.
        var nested = CreateNestedStruct("Inner");
        var facade = CreateStruct("HasField",
            nestedTypes: new[] { nested },
            properties: new[] { CreateProperty("count", isStatic: false, hasStorage: true) });

        Assert.False(NamespaceFacadeDetector.IsNamespaceFacade(facade));
    }

    [Fact]
    public void Gate_EnumWithCase_IsNotFacade()
    {
        // An enum with even one case carries runtime semantics (the case
        // payload, the discriminator). The namespace lift is only valid for
        // the caseless `enum Foo { struct Bar { ... } }` idiom.
        var nested = CreateNestedStruct("Inner");
        var facade = CreateEnum("HasCases", nestedTypes: new[] { nested });
        facade.Cases.Add(new EnumCaseDecl
        {
            Name = "first",
            MangledName = "$sTestModule8HasCasesO5firstyA2CmF",
            ParentDecl = null,
            ModuleDecl = null,
        });

        Assert.False(NamespaceFacadeDetector.IsNamespaceFacade(facade));
    }

    #endregion

    #region Helpers

    /// <summary>Drives <see cref="NamespaceFacadeEmitter.Emit"/> against an
    /// in-memory <see cref="CSharpWriter"/> and returns the emitted text.
    /// Tests that don't care about the recurse callback can omit it (the
    /// default is a no-op).</summary>
    private static string EmitToString(
        TypeDecl facade,
        ModuleEmissionContext? emissionContext = null,
        Action<IEnumerable<BaseDecl>, TypeHandlerContext>? recurseCapture = null)
    {
        var stringWriter = new StringWriter();
        var csWriter = new CSharpWriter(stringWriter);
        var context = TypeHandlerContext.Empty with { EmissionContext = emissionContext };

        NamespaceFacadeEmitter.Emit(
            csWriter,
            swiftWriter: null!,        // unused — recurse callback is a no-op
            facade,
            conductor: null!,          // unused — recurse callback is a no-op
            typeDatabase: null!,       // GenericTypeEmitter.GetTypeNameWithGenerics accepts null
            context,
            recurse: recurseCapture ?? ((_, _) => { }));

        return stringWriter.ToString();
    }

    /// <summary>Shared module decl used as the <c>ParentDecl</c> for top-level
    /// fixtures. The detector rejects types whose parent is not a
    /// <see cref="ModuleDecl"/> — see <see cref="NamespaceFacadeDetectorTests"/>
    /// for the gate.</summary>
    private static readonly ModuleDecl TestModule = new ModuleDecl
    {
        Name = "TestModule",
        ParentDecl = null,
        ModuleDecl = null,
        Methods = new List<MethodDecl>(),
        Properties = new List<PropertyDecl>(),
        Types = new List<TypeDecl>(),
        Dependencies = new List<string>(),
        Protocols = new List<ProtocolDecl>(),
    };

    private static StructDecl CreateStruct(string name,
        TypeDecl[]? nestedTypes = null,
        PropertyDecl[]? properties = null,
        MethodDecl[]? methods = null,
        string[]? conformances = null)
    {
        var conformanceList = (conformances ?? Array.Empty<string>()).Select(p => new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            SwiftTypeName.FromModuleQualifiedName(p),
            $"${name}_{p.Replace(".", "_")}_conformance"
        )).ToList();

        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}V",
            Properties = (properties ?? Array.Empty<PropertyDecl>()).ToList(),
            Methods = (methods ?? Array.Empty<MethodDecl>()).ToList(),
            Types = (nestedTypes ?? Array.Empty<TypeDecl>()).ToList(),
            Operators = new List<OperatorDecl>(),
            Conformances = conformanceList,
            ParentDecl = TestModule,
            ModuleDecl = TestModule,
            IsFrozen = false,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa",
        };
    }

    private static EnumDecl CreateEnum(string name,
        TypeDecl[]? nestedTypes = null,
        PropertyDecl[]? properties = null,
        MethodDecl[]? methods = null,
        string[]? conformances = null)
    {
        var conformanceList = (conformances ?? Array.Empty<string>()).Select(p => new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            SwiftTypeName.FromModuleQualifiedName(p),
            $"${name}_{p.Replace(".", "_")}_conformance"
        )).ToList();

        return new EnumDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}O",
            Properties = (properties ?? Array.Empty<PropertyDecl>()).ToList(),
            Methods = (methods ?? Array.Empty<MethodDecl>()).ToList(),
            Types = (nestedTypes ?? Array.Empty<TypeDecl>()).ToList(),
            Operators = new List<OperatorDecl>(),
            Conformances = conformanceList,
            ParentDecl = TestModule,
            ModuleDecl = TestModule,
            IsFrozen = false,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}OMa",
        };
    }

    private static StructDecl CreateNestedStruct(string name)
    {
        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}V",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = true,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa",
        };
    }

    private static PropertyDecl CreateProperty(string name, bool isStatic, bool hasStorage)
    {
        return new PropertyDecl
        {
            Name = name,
            HasStorage = hasStorage,
            IsStatic = isStatic,
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            Accessors = Array.Empty<AccessorDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
        };
    }

    #endregion
}
