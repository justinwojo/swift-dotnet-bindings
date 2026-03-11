// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for AsyncStreamEmitter — Issue K: @MainActor-isolated AsyncStream properties are skipped
/// because the wrapper captures `self` as a function parameter, not the actor's implicit self.
/// Swift 6 strict concurrency won't allow accessing @MainActor-isolated properties through a
/// captured reference parameter.
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
    public void MemberEmissionValidator_SkipsMainActorIsolatedAsyncStream()
    {
        var typeDatabase = new MockTypeDatabase();

        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Camera", moduleDecl);
        classDecl.IsMainActorIsolated = true;

        var property = CreateAsyncStreamProperty("sampleBuffer", classDecl, moduleDecl);

        var skipReason = MemberEmissionValidator.CanEmitProperty(
            property, typeDatabase, out var skipDetails, out var projectedTypeName);

        Assert.Equal(SkipReason.ActorIsolatedAsyncStream, skipReason);
        Assert.Contains("@MainActor-isolated", skipDetails!);
        Assert.Null(projectedTypeName);
    }

    [Fact]
    public void MemberEmissionValidator_SkipsPropertyLevelActorIsolatedAsyncStream()
    {
        var typeDatabase = new MockTypeDatabase();

        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("DataStream", moduleDecl);
        // Parent is NOT @MainActor, but property itself is

        var property = CreateAsyncStreamProperty("events", classDecl, moduleDecl);
        property.IsActorIsolated = true;

        var skipReason = MemberEmissionValidator.CanEmitProperty(
            property, typeDatabase, out var skipDetails, out var projectedTypeName);

        Assert.Equal(SkipReason.ActorIsolatedAsyncStream, skipReason);
        Assert.Null(projectedTypeName);
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
