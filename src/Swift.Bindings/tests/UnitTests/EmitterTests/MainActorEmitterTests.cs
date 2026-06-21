// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// F41: tests that the Swift <c>@MainActor</c> isolation bit is surfaced to the C# binding —
/// the type-level <c>[SwiftMainActor]</c> attribute, the per-member attribute + isolation
/// <c>&lt;remarks&gt;</c>, and the Debug-only <c>MainActorGuard.AssertMainThread()</c> guard in the
/// wrapper body.
/// </summary>
public class MainActorEmitterTests
{
    private const string GuardCall = "global::Swift.Runtime.MainActorGuard.AssertMainThread();";
    private const string Attribute = "[global::Swift.Runtime.SwiftMainActor]";

    [Fact]
    public void Method_OnMainActorParent_EmitsGuardAttributeAndRemarks()
    {
        var cs = GenerateSyncInstanceMethodCSharp(parentIsolated: true, memberIsolated: false, memberNonisolated: false);
        Assert.Contains(GuardCall, cs);
        Assert.Contains(Attribute, cs);
        Assert.Contains("@MainActor", cs);
        Assert.Contains("main thread", cs);
    }

    [Fact]
    public void Method_WithPerMemberMainActor_EmitsGuardAttributeAndRemarks()
    {
        var cs = GenerateSyncInstanceMethodCSharp(parentIsolated: false, memberIsolated: true, memberNonisolated: false);
        Assert.Contains(GuardCall, cs);
        Assert.Contains(Attribute, cs);
    }

    [Fact]
    public void Method_NonisolatedOnMainActorParent_OmitsGuard()
    {
        // A nonisolated member opts out of its parent's isolation — no guard, no attribute.
        var cs = GenerateSyncInstanceMethodCSharp(parentIsolated: true, memberIsolated: false, memberNonisolated: true);
        Assert.DoesNotContain(GuardCall, cs);
        Assert.DoesNotContain(Attribute, cs);
    }

    [Fact]
    public void Method_NoIsolation_OmitsGuardAndAttribute()
    {
        var cs = GenerateSyncInstanceMethodCSharp(parentIsolated: false, memberIsolated: false, memberNonisolated: false);
        Assert.DoesNotContain(GuardCall, cs);
        Assert.DoesNotContain(Attribute, cs);
    }

    [Fact]
    public void TypeAnnotation_MainActorType_EmitsAttributeAndRemarks()
    {
        var sw = new StringWriter();
        TypeAnnotationHelper.EmitSwiftMainActorAnnotation(new CSharpWriter(sw), MakeStruct("Iso", isMainActor: true));
        var output = sw.ToString();
        Assert.Contains(Attribute, output);
        Assert.Contains("@MainActor", output);
        Assert.Contains("main thread", output);
    }

    [Fact]
    public void TypeAnnotation_NonIsolatedType_EmitsNothing()
    {
        var sw = new StringWriter();
        TypeAnnotationHelper.EmitSwiftMainActorAnnotation(new CSharpWriter(sw), MakeStruct("Plain", isMainActor: false));
        Assert.Equal(string.Empty, sw.ToString());
    }

    private static StructDecl MakeStruct(string name, bool isMainActor)
        => new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            IsFrozen = true,
            IsMainActorIsolated = isMainActor,
            MetadataAccessor = $"$s10TestModule{name}VMa",
            MangledName = $"$s10TestModule{name}V",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Conformances = new List<TypeConformance>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

    /// <summary>
    /// Generates the C# wrapper for a sync, no-argument, Int-returning instance method on a frozen
    /// struct receiver, with the parent / member isolation flags set per the arguments. Mirrors the
    /// decl-construction in <c>AsyncSwiftWrapperTests.GenerateAsyncStructInstanceMethodCSharp</c>.
    /// </summary>
    private static string GenerateSyncInstanceMethodCSharp(bool parentIsolated, bool memberIsolated, bool memberNonisolated)
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

        var parentDecl = new StructDecl
        {
            Name = "TestStruct",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.TestStruct"),
            IsFrozen = true,
            IsMainActorIsolated = parentIsolated,
            MetadataAccessor = "$s10TestModule0A6StructVMa",
            MangledName = "$s10TestModule0A6StructV",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Conformances = new List<TypeConformance>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Types.Add(parentDecl);

        var csSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                Name = string.Empty,
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl
            }
        };

        var methodDecl = new MethodDecl
        {
            Name = "increment",
            MangledName = "$s10TestModule0A6StructV9incrementSiyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsMainActorIsolated = memberIsolated,
            IsNonisolated = memberNonisolated,
            IsSynthesizedAccessor = false
        };

        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/fake/path");
        module.RegisterType(parentDecl.SwiftTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "TestStruct"),
            SwiftTypeName = parentDecl.SwiftTypeName,
            MetadataAccessor = parentDecl.MetadataAccessor,
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(SwiftTypeName.FromModuleQualifiedName("Swift.Int"), new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.NIntType,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            MetadataAccessor = "$sSiMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });
        typeDatabase.AddModuleDatabase(swiftModule);
        typeDatabase.AddModuleDatabase(module);

        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);

        var loggerFactory = new NullLoggerFactory();
        var conductor = new Conductor(loggerFactory);

        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        var env = handler.Marshal(methodDecl, typeDatabase);
        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        return csStringWriter.ToString();
    }
}
