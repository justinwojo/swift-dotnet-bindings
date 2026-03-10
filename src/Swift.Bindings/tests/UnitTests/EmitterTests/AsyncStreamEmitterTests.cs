// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for AsyncStreamEmitter — specifically the @MainActor Task isolation fix (Issue K).
/// </summary>
public class AsyncStreamEmitterTests
{
    [Fact]
    public void EmitSwiftWrapper_MainActorIsolatedParent_EmitsTaskWithMainActor()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Camera", moduleDecl);
        classDecl.IsMainActorIsolated = true;

        var property = CreateAsyncStreamProperty("sampleBuffer", classDecl, moduleDecl);
        var asyncStreamHandler = new AsyncStreamHandler(new TypeDatabase());

        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        AsyncStreamEmitter.EmitSwiftWrapper(swiftWriter, property, asyncStreamHandler,
            "Camera_sampleBuffer_AsyncStream", "TestModule.Camera");

        var swift = swiftOutput.ToString();
        Assert.Contains("@MainActor @_silgen_name(", swift);
        Assert.Contains("Task { @MainActor in", swift);
    }

    [Fact]
    public void EmitSwiftWrapper_NonIsolatedParent_EmitsPlainTask()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Sensor", moduleDecl);

        var property = CreateAsyncStreamProperty("readings", classDecl, moduleDecl);
        var asyncStreamHandler = new AsyncStreamHandler(new TypeDatabase());

        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        AsyncStreamEmitter.EmitSwiftWrapper(swiftWriter, property, asyncStreamHandler,
            "Sensor_readings_AsyncStream", "TestModule.Sensor");

        var swift = swiftOutput.ToString();
        Assert.DoesNotContain("@MainActor", swift);
        Assert.Contains("Task {", swift);
        Assert.DoesNotContain("@MainActor in", swift);
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
        // AsyncStream<Element> type spec
        var asyncStreamType = new NamedTypeSpec("Swift.AsyncStream",
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

    #endregion
}
