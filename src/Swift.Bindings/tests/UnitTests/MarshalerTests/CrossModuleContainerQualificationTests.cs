// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// A cross-module existential must project to a module-qualified C# interface and proxy class:
/// a generated binding emits no <c>using</c> for a sibling binding's namespace, so a bare
/// <c>IFoo</c> / <c>FooProxy</c> written into this module's output names nothing the consuming
/// assembly can resolve.
/// <para>
/// The plain parameter/return position is qualified by the environment's existential oracle. These
/// tests cover the CONTAINER positions, each of which reaches the projection through its own oracle
/// instance: a closure's parameter/return type, a tuple element, and a bound-generic argument. Each
/// handler owns a private <see cref="ExistentialHandler"/>, so each is an independent chance to drop
/// the module segment; the constructor parameter asserted here is what keeps them in step with the
/// module they are emitting.
/// </para>
/// </summary>
public class CrossModuleContainerQualificationTests
{
    private const string OwningModule = "DependencyModule";
    private const string ConsumingModule = "ConsumerModule";
    private const string ProtocolName = "EncodingProtocol";
    private const string QualifiedProtocol = $"{OwningModule}.{ProtocolName}";

    private static ProtocolListTypeSpec ExistentialSpec() =>
        new(new[] { new NamedTypeSpec(QualifiedProtocol) });

    private static ModuleDecl CreateModuleDecl(string name) => new()
    {
        Name = name,
        Dependencies = new List<string>(),
        Types = new List<TypeDecl>(),
        Methods = new List<MethodDecl>(),
        Properties = new List<PropertyDecl>(),
        Protocols = new List<ProtocolDecl>(),
        ParentDecl = null,
        ModuleDecl = null
    };

    // ==================== Closure containers ====================

    [Fact]
    public void ClosureHandler_CrossModuleExistential_QualifiesProxyClassName()
    {
        var handler = new ClosureHandler(new ProtocolOnlyTypeDatabase(), ConsumingModule);

        var proxy = handler.GetQualifiedProxyClassName(ExistentialSpec());

        Assert.Equal($"{OwningModule}.SwiftInterop.{ProtocolName}Proxy", proxy);
    }

    [Fact]
    public void ClosureHandler_SameModuleExistential_LeavesProxyClassNameBare()
    {
        // The proxy lives in the module being emitted, so qualifying it would name a namespace
        // the file is already inside.
        var handler = new ClosureHandler(new ProtocolOnlyTypeDatabase(), OwningModule);

        var proxy = handler.GetQualifiedProxyClassName(ExistentialSpec());

        Assert.Equal($"{ProtocolName}Proxy", proxy);
    }

    [Fact]
    public void ClosureHandler_WithoutModuleName_LeavesProxyClassNameBare()
    {
        // The bare handler classifies shapes rather than rendering emitted code; it must keep its
        // pre-existing unqualified behavior so the many non-emission construction sites are unmoved.
        var handler = new ClosureHandler(new ProtocolOnlyTypeDatabase());

        var proxy = handler.GetQualifiedProxyClassName(ExistentialSpec());

        Assert.Equal($"{ProtocolName}Proxy", proxy);
    }

    // ==================== Tuple containers ====================

    [Fact]
    public void TupleHandler_CrossModuleExistentialElement_QualifiesInterfaceName()
    {
        var handler = new TupleHandler(new ProtocolOnlyTypeDatabase(), ConsumingModule);

        var element = handler.TranslateElementTypeToCSharp(ExistentialSpec());

        Assert.Equal($"{OwningModule}.I{ProtocolName}", element);
    }

    [Fact]
    public void TupleHandler_SameModuleExistentialElement_LeavesInterfaceNameBare()
    {
        var handler = new TupleHandler(new ProtocolOnlyTypeDatabase(), OwningModule);

        var element = handler.TranslateElementTypeToCSharp(ExistentialSpec());

        Assert.Equal($"I{ProtocolName}", element);
    }

    // ==================== Bound-generic containers ====================
    //
    // A bound generic's own element slot renders the ABI-level ExistentialContainer, not a public
    // interface name, so THAT slot needs no qualification. What the bound-generic handler does put
    // a public existential name into is the delegate signature of a closure nested inside the
    // container — it delegates to a ClosureHandler it constructs itself, which is exactly the
    // instance that has to inherit the module being emitted.

    private static NamedTypeSpec ArrayOfExistentialTakingClosure() =>
        new("Swift.Array", new TypeSpec[]
        {
            new ClosureTypeSpec(ExistentialSpec(), new TupleTypeSpec())
        });

    [Fact]
    public void BoundGenericsHandler_NestedClosureOverCrossModuleExistential_QualifiesDelegateArgument()
    {
        var handler = new BoundGenericsHandler(new ProtocolOnlyTypeDatabase(), conformanceGraph: null,
            currentModuleName: ConsumingModule);

        var translated = handler.TranslateBoundGenericTypeToCSharp(
            ArrayOfExistentialTakingClosure(), GenericContext.Empty);

        Assert.Contains($"{OwningModule}.I{ProtocolName}", translated);
    }

    [Fact]
    public void BoundGenericsHandler_NestedClosureOverSameModuleExistential_LeavesDelegateArgumentBare()
    {
        var handler = new BoundGenericsHandler(new ProtocolOnlyTypeDatabase(), conformanceGraph: null,
            currentModuleName: OwningModule);

        var translated = handler.TranslateBoundGenericTypeToCSharp(
            ArrayOfExistentialTakingClosure(), GenericContext.Empty);

        Assert.DoesNotContain($"{OwningModule}.I{ProtocolName}", translated);
        Assert.Contains($"I{ProtocolName}", translated);
    }

    // ==================== Factory projections ====================
    //
    // Indexer types, container-shaped property types, and the accessor bodies that convert through
    // them are resolved by the projection factory rather than by a marshaling-context handler. The
    // factory only qualifies when the projection context carries the module being emitted, so a
    // context built without it emits a bare element interface into a public signature.

    private static NamedTypeSpec ArrayOfExistential() =>
        new("Swift.Array", new TypeSpec[] { ExistentialSpec() });

    private static string ProjectPublicType(TypeSpec spec, string currentModuleName, bool isParameter) =>
        new TypeProjectionFactory().Project(spec, new ProjectionContext
        {
            TypeDatabase = new ProtocolOnlyTypeDatabase(),
            IsParameter = isParameter,
            CurrentModuleName = currentModuleName
        })?.PublicType;

    [Fact]
    public void Factory_CrossModuleExistential_QualifiesPublicType()
    {
        var publicType = ProjectPublicType(ExistentialSpec(), ConsumingModule, isParameter: false);

        Assert.Contains($"{OwningModule}.I{ProtocolName}", publicType);
    }

    [Fact]
    public void Factory_SameModuleExistential_LeavesPublicTypeBare()
    {
        var publicType = ProjectPublicType(ExistentialSpec(), OwningModule, isParameter: false);

        Assert.DoesNotContain($"{OwningModule}.I{ProtocolName}", publicType);
        Assert.Contains($"I{ProtocolName}", publicType);
    }

    [Fact]
    public void Factory_ArrayOfCrossModuleExistential_QualifiesElement()
    {
        var publicType = ProjectPublicType(ArrayOfExistential(), ConsumingModule, isParameter: false);

        Assert.Contains($"{OwningModule}.I{ProtocolName}", publicType);
    }

    [Fact]
    public void Factory_ArrayOfSameModuleExistential_LeavesElementBare()
    {
        var publicType = ProjectPublicType(ArrayOfExistential(), OwningModule, isParameter: false);

        Assert.DoesNotContain($"{OwningModule}.I{ProtocolName}", publicType);
        Assert.Contains($"I{ProtocolName}", publicType);
    }

    [Fact]
    public void Factory_ArrayOfCrossModuleExistential_QualifiesElementInParameterPosition()
    {
        // The setter direction takes the parameter-position projection; it must qualify identically
        // or a property's getter and setter disagree on the element type.
        var publicType = ProjectPublicType(ArrayOfExistential(), ConsumingModule, isParameter: true);

        Assert.Contains($"{OwningModule}.I{ProtocolName}", publicType);
    }

    // ==================== The per-module context wires all of them ====================

    [Fact]
    public void MarshalingContext_QualifiesCrossModuleExistentialInEveryContainerHandler()
    {
        // One module context owns the handler set an emission uses; a container handler that missed
        // the module name would emit an unresolvable name while the plain-position oracle beside it
        // emitted the qualified one.
        var db = new ProtocolOnlyTypeDatabase();
        var ctx = new MarshalingContext(CreateModuleDecl(ConsumingModule), db, specializationEngine: null);

        Assert.Equal($"{OwningModule}.I{ProtocolName}", ctx.Existential.GetPublicExistentialType(ExistentialSpec()));
        Assert.Equal($"{OwningModule}.SwiftInterop.{ProtocolName}Proxy", ctx.Closure.GetQualifiedProxyClassName(ExistentialSpec()));
        Assert.Equal($"{OwningModule}.I{ProtocolName}", ctx.Tuple.TranslateElementTypeToCSharp(ExistentialSpec()));
        Assert.Contains($"{OwningModule}.I{ProtocolName}",
            ctx.BoundGenerics.TranslateBoundGenericTypeToCSharp(
                ArrayOfExistentialTakingClosure(), GenericContext.Empty));
    }

    /// <summary>
    /// Minimal database carrying one protocol owned by <see cref="OwningModule"/> — all the
    /// existential oracles consult to decide qualification — plus the stdlib array, whose record
    /// the bound-generic translator needs before it will descend into the element at all.
    /// </summary>
    private sealed class ProtocolOnlyTypeDatabase : ITypeDatabase
    {
        private readonly Dictionary<string, TypeRecord> _types = new()
        {
            [QualifiedProtocol] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(OwningModule, $"I{ProtocolName}"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(QualifiedProtocol),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Protocol,
                EmittedMemberCount = 0
            },
            ["Swift.Array"] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(
                    "System.Collections.Generic", "IReadOnlyList"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Array"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
                EmittedMemberCount = 0
            }
        };

        public string AsyncLibraryName => null!;

        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) =>
            _types.ContainsKey(swiftTypeName.ModuleQualifiedName);

        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, out TypeRecord record) =>
            _types.TryGetValue(swiftTypeName.ModuleQualifiedName, out record);

        public string GetLibraryPath(string moduleName) => "";

        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
    }
}
