// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="MarshalingContext"/> — the constructed-once-per-module holder of the
/// fully-configured marshalling handler instances. These assert the two properties the context
/// exists to guarantee: every handler it owns is born with the module's qualification
/// (<c>CurrentModuleName</c>) and conformer-discovery engine (<c>SpecializationEngine</c>) already
/// set, and a <see cref="ProjectionContext"/> minted from it inherits the same configuration — so
/// the env path and the projection path can never diverge into the "configured vs. bare" fork that
/// was the structural root of Defect E.
/// </summary>
public class MarshalingContextTests
{
    private static ModuleDecl CreateModuleDecl(string name) => new ModuleDecl
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

    [Fact]
    public void Constructor_ThreadsModuleNameToContext()
    {
        var ctx = new MarshalingContext(CreateModuleDecl("MyModule"), new MockTypeDatabase(), specializationEngine: null);

        Assert.Equal("MyModule", ctx.CurrentModuleName);
    }

    [Fact]
    public void Constructor_ThreadsModuleNameToExistentialHandler()
    {
        // The existential handler's CurrentModuleName drives cross-module qualification; the context
        // must set it at construction, not leave it for a later late-bind.
        var ctx = new MarshalingContext(CreateModuleDecl("MyModule"), new MockTypeDatabase(), specializationEngine: null);

        Assert.Equal("MyModule", ctx.Existential.CurrentModuleName);
    }

    [Fact]
    public void Constructor_ThreadsSpecializationEngineToContextAndExistentialHandler()
    {
        var db = new MockTypeDatabase();
        var engine = new ConcreteSpecializationEngine(db, currentModuleName: "MyModule");

        var ctx = new MarshalingContext(CreateModuleDecl("MyModule"), db, engine);

        // Same instance threaded to both the context surface and the handler it owns — no second
        // engine, no bare handler.
        Assert.Same(engine, ctx.SpecializationEngine);
        Assert.Same(engine, ctx.Existential.SpecializationEngine);
    }

    [Fact]
    public void Constructor_AllHandlersAreNonNull()
    {
        var ctx = new MarshalingContext(CreateModuleDecl("MyModule"), new MockTypeDatabase(), specializationEngine: null);

        Assert.NotNull(ctx.BoundGenerics);
        Assert.NotNull(ctx.Closure);
        Assert.NotNull(ctx.Tuple);
        Assert.NotNull(ctx.TypeConversion);
        Assert.NotNull(ctx.Existential);
        Assert.NotNull(ctx.AsyncStream);
    }

    [Fact]
    public void NewProjectionContext_InheritsModuleNameAndEngine()
    {
        var db = new MockTypeDatabase();
        var engine = new ConcreteSpecializationEngine(db, currentModuleName: "MyModule");
        var ctx = new MarshalingContext(CreateModuleDecl("MyModule"), db, engine);

        var projection = ctx.NewProjectionContext(isParameter: false);

        Assert.Equal("MyModule", projection.CurrentModuleName);
        Assert.Same(engine, projection.SpecializationEngine);
        Assert.Same(db, projection.TypeDatabase);
    }

    [Fact]
    public void NewProjectionContext_DefaultsCompositionCollectorToNull()
    {
        // Overload-key / validation contexts must not register composition interfaces as a
        // projection side effect — the collector is opt-in, defaulting to null.
        var ctx = new MarshalingContext(CreateModuleDecl("MyModule"), new MockTypeDatabase(), specializationEngine: null);

        var projection = ctx.NewProjectionContext(isParameter: true);

        Assert.Null(projection.CompositionCollector);
    }

    [Fact]
    public void NewProjectionContext_ThreadsCompositionCollectorWhenProvided()
    {
        var ctx = new MarshalingContext(CreateModuleDecl("MyModule"), new MockTypeDatabase(), specializationEngine: null);
        var collector = new SortedDictionary<string, List<string>>();

        var projection = ctx.NewProjectionContext(isParameter: true, compositionCollector: collector);

        Assert.Same(collector, projection.CompositionCollector);
    }

    [Fact]
    public void NewProjectionContext_PropagatesIsParameterAsyncThrowsAndPrefix()
    {
        var ctx = new MarshalingContext(CreateModuleDecl("MyModule"), new MockTypeDatabase(), specializationEngine: null);

        var projection = ctx.NewProjectionContext(
            isParameter: true,
            isAsync: true,
            throws: true,
            callbackNamePrefix: "cb_");

        Assert.True(projection.IsParameter);
        Assert.True(projection.IsAsync);
        Assert.True(projection.Throws);
        Assert.Equal("cb_", projection.CallbackNamePrefix);
    }

    #region MockTypeDatabase

    private class MockTypeDatabase : ITypeDatabase
    {
        private readonly Dictionary<string, TypeRecord> _types;

        public string AsyncLibraryName => null!;

        public MockTypeDatabase()
        {
            _types = new Dictionary<string, TypeRecord>
            {
                ["Swift.Int"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.NIntType,
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                }
            };
        }

        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) =>
            _types.ContainsKey(swiftTypeName.ModuleQualifiedName);

        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, out TypeRecord record)
        {
            return _types.TryGetValue(swiftTypeName.ModuleQualifiedName, out record!);
        }

        public string GetLibraryPath(string moduleName) => "";

        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
    }

    #endregion
}
