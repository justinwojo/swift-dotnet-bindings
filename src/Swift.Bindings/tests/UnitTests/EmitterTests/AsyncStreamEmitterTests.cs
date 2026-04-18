// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for AsyncStreamEmitter — covers @MainActor, custom `actor`, and plain non-actor
/// parents. Custom actor AsyncStream properties are emitted as `Task { for await e in await
/// __self.prop { ... } }`; the `await` on property access hops to the actor's serial
/// executor. Parameterized-protocol element types stay rejected because the @_cdecl
/// wrapper can't spell iOS 16+ parameterized-protocol types.
/// </summary>
public class AsyncStreamEmitterTests
{
    [Fact]
    public void EmitSwiftWrapper_NonIsolatedParent_EmitsPlainTask()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Sensor", moduleDecl);

        var property = CreateAsyncStreamProperty("readings", classDecl, moduleDecl);
        var asyncStreamHandler = new AsyncStreamHandler(new MockTypeDatabase());

        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        AsyncStreamEmitter.EmitSwiftWrapper(swiftWriter, property, asyncStreamHandler,
            "Sensor_readings_AsyncStream", "TestModule.Sensor");

        var swift = swiftOutput.ToString();
        Assert.DoesNotContain("@MainActor", swift);
        Assert.Contains("Task {", swift);
        Assert.DoesNotContain("@MainActor in", swift);
    }

    [Fact]
    public void MemberEmissionValidator_AllowsMainActorIsolatedAsyncStream()
    {
        // @MainActor AsyncStream properties are now allowed — under -strict-concurrency=minimal,
        // nonisolated wrappers can access @MainActor members
        var typeDatabase = new MockTypeDatabase();

        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Camera", moduleDecl);
        classDecl.IsMainActorIsolated = true;

        var property = CreateAsyncStreamProperty("sampleBuffer", classDecl, moduleDecl);

        var skipReason = MemberEmissionValidator.CanEmitProperty(
            property, typeDatabase, out var skipDetails, out var projectedTypeName);

        Assert.Null(skipReason);
        Assert.Equal("long", projectedTypeName);
    }

    [Fact]
    public void MemberEmissionValidator_AllowsPropertyLevelActorIsolatedAsyncStream()
    {
        // Per-member @MainActor on non-actor class is now allowed
        var typeDatabase = new MockTypeDatabase();

        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("DataStream", moduleDecl);

        var property = CreateAsyncStreamProperty("events", classDecl, moduleDecl);
        property.IsActorIsolated = true;

        var skipReason = MemberEmissionValidator.CanEmitProperty(
            property, typeDatabase, out var skipDetails, out var projectedTypeName);

        Assert.Null(skipReason);
        Assert.Equal("long", projectedTypeName);
    }

    [Fact]
    public void MemberEmissionValidator_AllowsCustomActorAsyncStream()
    {
        // Custom actor AsyncStream properties are now emitted; the Swift wrapper awaits the
        // property across the actor's serial executor.
        var typeDatabase = new MockTypeDatabase();

        var moduleDecl = CreateModuleDecl("TestModule");
        var actorDecl = CreateClassDecl("DataProcessor", moduleDecl);
        actorDecl.IsActor = true;

        var property = CreateAsyncStreamProperty("results", actorDecl, moduleDecl);

        var skipReason = MemberEmissionValidator.CanEmitProperty(
            property, typeDatabase, out var skipDetails, out var projectedTypeName);

        Assert.Null(skipReason);
        Assert.Equal("long", projectedTypeName);
    }

    [Fact]
    public void MemberEmissionValidator_AllowsNonisolatedCustomActorAsyncStream()
    {
        // `nonisolated` actor property access is synchronous from any isolation; still allowed.
        var typeDatabase = new MockTypeDatabase();

        var moduleDecl = CreateModuleDecl("TestModule");
        var actorDecl = CreateClassDecl("DataProcessor", moduleDecl);
        actorDecl.IsActor = true;

        var property = CreateAsyncStreamProperty("results", actorDecl, moduleDecl);
        property.IsNonisolated = true;

        var skipReason = MemberEmissionValidator.CanEmitProperty(
            property, typeDatabase, out var skipDetails, out var projectedTypeName);

        Assert.Null(skipReason);
        Assert.Equal("long", projectedTypeName);
    }

    [Fact]
    public void EmitSwiftWrapper_CustomActorIsolated_EmitsAwaitOnPropertyAccess()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var actorDecl = CreateClassDecl("DataProcessor", moduleDecl);
        actorDecl.IsActor = true;

        var property = CreateAsyncStreamProperty("results", actorDecl, moduleDecl);
        var asyncStreamHandler = new AsyncStreamHandler(new MockTypeDatabase());

        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        AsyncStreamEmitter.EmitSwiftWrapper(swiftWriter, property, asyncStreamHandler,
            "DataProcessor_results_AsyncStream", "TestModule.DataProcessor");

        var swift = swiftOutput.ToString();
        Assert.Contains("Task {", swift);
        Assert.DoesNotContain("@MainActor", swift);
        // Actor-isolated property: hop through `await __self.results` to the actor's executor.
        Assert.Contains("await __self.results", swift);
    }

    [Fact]
    public void EmitSwiftWrapper_CustomActorNonisolated_OmitsAwaitOnPropertyAccess()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var actorDecl = CreateClassDecl("DataProcessor", moduleDecl);
        actorDecl.IsActor = true;

        var property = CreateAsyncStreamProperty("results", actorDecl, moduleDecl);
        property.IsNonisolated = true;
        var asyncStreamHandler = new AsyncStreamHandler(new MockTypeDatabase());

        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        AsyncStreamEmitter.EmitSwiftWrapper(swiftWriter, property, asyncStreamHandler,
            "DataProcessor_results_AsyncStream", "TestModule.DataProcessor");

        var swift = swiftOutput.ToString();
        Assert.DoesNotContain("@MainActor", swift);
        // Nonisolated property access is synchronous — no await prefix on the property access.
        Assert.DoesNotContain("await __self.results", swift);
        Assert.Contains("__self.results", swift);
    }

    [Fact]
    public void MemberEmissionValidator_AllowsNonIsolatedAsyncStream()
    {
        var typeDatabase = new MockTypeDatabase();

        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Sensor", moduleDecl);

        var property = CreateAsyncStreamProperty("readings", classDecl, moduleDecl);

        var skipReason = MemberEmissionValidator.CanEmitProperty(
            property, typeDatabase, out var skipDetails, out var projectedTypeName);

        Assert.Null(skipReason);
        Assert.Equal("long", projectedTypeName);
    }

    [Fact]
    public void MemberEmissionValidator_AllowsStaticMainActorAsyncStream()
    {
        var typeDatabase = new MockTypeDatabase();

        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Camera", moduleDecl);
        classDecl.IsMainActorIsolated = true;

        var property = CreateAsyncStreamProperty("globalUpdates", classDecl, moduleDecl);
        property.IsStatic = true; // Static doesn't capture self

        var skipReason = MemberEmissionValidator.CanEmitProperty(
            property, typeDatabase, out var skipDetails, out var projectedTypeName);

        Assert.Null(skipReason);
        Assert.Equal("long", projectedTypeName);
    }

    [Fact]
    public void EmitCompletionCallback_InvokesStreamCompletionCallback()
    {
        // Regression guard: the completion callback must resolve the stream
        // from context and call GetCompletionCallback()(context). A previous
        // emission left this empty, leaving the channel writer open forever
        // and hanging any C# consumer iterating via `await foreach`.
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Sensor", moduleDecl);
        var property = CreateAsyncStreamProperty("readings", classDecl, moduleDecl);
        var asyncStreamHandler = new AsyncStreamHandler(new MockTypeDatabase());

        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);

        AsyncStreamEmitter.EmitCompletionCallback(csWriter, property, asyncStreamHandler, "Sensor_readings");

        var cs = csOutput.ToString();
        Assert.Contains("stream.GetCompletionCallback()(context)", cs);
    }

    [Fact]
    public void MemberEmissionValidator_AllowsNonisolatedAsyncStreamOnMainActorType()
    {
        var typeDatabase = new MockTypeDatabase();

        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Camera", moduleDecl);
        classDecl.IsMainActorIsolated = true;

        var property = CreateAsyncStreamProperty("statusUpdates", classDecl, moduleDecl);
        property.IsNonisolated = true; // Explicitly opts out of parent's isolation

        var skipReason = MemberEmissionValidator.CanEmitProperty(
            property, typeDatabase, out var skipDetails, out var projectedTypeName);

        Assert.Null(skipReason);
        Assert.Equal("long", projectedTypeName);
    }

    #region Helpers

    private static ModuleDecl CreateModuleDecl(string name)
    {
        return new ModuleDecl
        {
            Name = name,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static ClassDecl CreateClassDecl(string name, ModuleDecl moduleDecl)
    {
        return new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{name.Length}{name}CN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };
    }

    private static PropertyDecl CreateAsyncStreamProperty(string name, TypeDecl parentDecl, ModuleDecl moduleDecl)
    {
        // AsyncStream<Element> type spec — use _Concurrency.AsyncStream as the handler expects
        var asyncStreamType = new NamedTypeSpec("_Concurrency.AsyncStream",
            new TypeSpec[] { new NamedTypeSpec("Swift.Int") });

        return new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = asyncStreamType,
            IsStatic = false,
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Accessors = new List<AccessorDecl>(),
            HasStorage = false,
        };
    }

    /// <summary>
    /// Mock type database with Swift.Int registered so AsyncStream element type resolves.
    /// </summary>
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
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                }
            };
        }

        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) => _types.ContainsKey(swiftTypeName.ModuleQualifiedName);

        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, out TypeRecord record)
        {
            return _types.TryGetValue(swiftTypeName.ModuleQualifiedName, out record);
        }

        public string GetLibraryPath(string moduleName) => "";

        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
    }

    #endregion
}
