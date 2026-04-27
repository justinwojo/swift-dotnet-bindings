// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for the ProtocolConformanceDecisionCache in ModuleEmissionContext
/// and its integration with EveryProtocolEmitter and WitnessDispatchEmitter.
/// </summary>
public class ProtocolConformanceCacheTests
{
    [Fact]
    public void RecordConformanceDecision_EmittedTrue_WasConformanceEmittedReturnsTrue()
    {
        var ctx = new ModuleEmissionContext();
        ctx.RecordConformanceDecision("TestProtocol", true, null);
        Assert.True(ctx.WasConformanceEmitted("TestProtocol"));
    }

    [Fact]
    public void RecordConformanceDecision_EmittedFalse_WasConformanceEmittedReturnsFalse()
    {
        var ctx = new ModuleEmissionContext();
        ctx.RecordConformanceDecision("TestProtocol", false, "HasSelfRequirement");
        Assert.False(ctx.WasConformanceEmitted("TestProtocol"));
    }

    [Fact]
    public void WasConformanceEmitted_UnrecordedProtocol_ReturnsFalse()
    {
        var ctx = new ModuleEmissionContext();
        Assert.False(ctx.WasConformanceEmitted("UnknownProtocol"));
    }

    [Fact]
    public void ConformanceDecisions_TracksAllDecisions()
    {
        var ctx = new ModuleEmissionContext();
        ctx.RecordConformanceDecision("ProtoA", true, null);
        ctx.RecordConformanceDecision("ProtoB", false, "SelfTypedMembers");
        ctx.RecordConformanceDecision("ProtoC", false, "NoImplementableMembers");

        Assert.Equal(3, ctx.ConformanceDecisions.Count);
        Assert.True(ctx.ConformanceDecisions["ProtoA"].Emitted);
        Assert.Null(ctx.ConformanceDecisions["ProtoA"].SkipReason);
        Assert.False(ctx.ConformanceDecisions["ProtoB"].Emitted);
        Assert.Equal("SelfTypedMembers", ctx.ConformanceDecisions["ProtoB"].SkipReason);
    }

    [Fact]
    public void EmitProtocolConformance_SelfRequirement_RecordsSkipDecision()
    {
        var (emitter, ctx) = CreateEmitterWithContext();
        var protocolDecl = CreateProtocolWithSelfRequirement("SelfProto");

        var writer = CreateSwiftWriter();
        emitter.EmitProtocolConformance(writer, protocolDecl);

        Assert.False(ctx.WasConformanceEmitted("SelfProto"));
        Assert.Equal("HasSelfRequirement", ctx.ConformanceDecisions["SelfProto"].SkipReason);
    }

    [Fact]
    public void EmitProtocolConformance_SelfTypedMembers_EmitsConformanceWithStubs()
    {
        var (emitter, ctx) = CreateEmitterWithContext();
        // Protocol with a method returning a generic type parameter (Self-typed).
        // Self-typed members now get fatalError() stubs instead of skipping the entire protocol.
        var protocolDecl = CreateProtocolWithSelfTypedMethod("GenericProto");

        var writer = CreateSwiftWriter();
        emitter.EmitProtocolConformance(writer, protocolDecl);

        Assert.True(ctx.WasConformanceEmitted("GenericProto"));
    }

    [Fact]
    public void EmitProtocolConformance_StaticOnlyMembers_SkipsConformance()
    {
        var (emitter, ctx) = CreateEmitterWithContext();
        // Protocol with only static methods — skipped because static method
        // requirements can't be satisfied by EveryProtocol proxy
        var protocolDecl = CreateProtocolWithOnlyStaticMembers("StaticOnlyProto");

        var writer = CreateSwiftWriter();
        emitter.EmitProtocolConformance(writer, protocolDecl);

        Assert.False(ctx.WasConformanceEmitted("StaticOnlyProto"));
        Assert.Equal("StaticMethodRequirements", ctx.ConformanceDecisions["StaticOnlyProto"].SkipReason);
    }

    [Fact]
    public void EmitProtocolConformance_StaticPropertyOnly_SkipsWithStaticPropertyRequirementsReason()
    {
        // Bug #5: protocols whose only requirements are `static var` properties cannot
        // be satisfied by `fatalError()` stubs — Swift type-checks the conformance and
        // rejects it for protocols with constrained static-var requirements
        // (RealityFoundation.RealityCoordinateSpace, MaterialFunction). Skip explicitly.
        var (emitter, ctx) = CreateEmitterWithContext();
        var protocolDecl = CreateProtocolWithOnlyStaticProperty("StaticPropProto");

        var output = new System.IO.StringWriter();
        emitter.EmitProtocolConformance(new SwiftWriter(output), protocolDecl);

        Assert.False(ctx.WasConformanceEmitted("StaticPropProto"));
        Assert.Equal("StaticPropertyRequirements", ctx.ConformanceDecisions["StaticPropProto"].SkipReason);
        Assert.DoesNotContain("extension EveryProtocol", output.ToString());
        Assert.DoesNotContain("fatalError", output.ToString());
    }

    private static ProtocolDecl CreateProtocolWithOnlyStaticProperty(string name)
    {
        return new ProtocolDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}P",
            HasSelfRequirement = false,
            IsClassBound = false,
            Properties = new List<PropertyDecl>
            {
                new PropertyDecl
                {
                    Name = "defaultValue",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    HasStorage = false,
                    IsStatic = true,
                    Accessors = new List<AccessorDecl>(),
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    [Fact]
    public void EmitProtocolConformance_EmptyMarkerProtocol_EmitsTrivialConformance()
    {
        var (emitter, ctx) = CreateEmitterWithContext();
        // Empty marker protocol (no members at all) — gets trivial conformance
        // for existential container creation
        var protocolDecl = CreateEmptyMarkerProtocol("MarkerProto");

        var writer = CreateSwiftWriter();
        emitter.EmitProtocolConformance(writer, protocolDecl);

        Assert.True(ctx.WasConformanceEmitted("MarkerProto"));
    }

    [Fact]
    public void EmitProtocolConformance_ValidProtocol_RecordsEmitDecision()
    {
        var (emitter, ctx) = CreateEmitterWithContext();
        var protocolDecl = CreateValidProtocol("GoodProto");

        var writer = CreateSwiftWriter();
        emitter.EmitProtocolConformance(writer, protocolDecl);

        Assert.True(ctx.WasConformanceEmitted("GoodProto"));
        Assert.Null(ctx.ConformanceDecisions["GoodProto"].SkipReason);
    }

    #region Test Helpers

    private static (EveryProtocolEmitter emitter, ModuleEmissionContext ctx) CreateEmitterWithContext()
    {
        var typeDb = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/fake/path");
        typeDb.AddModuleDatabase(module);
        var ctx = new ModuleEmissionContext();
        var emitter = new EveryProtocolEmitter(typeDb, NullLogger.Instance, "TestModule", ctx);
        return (emitter, ctx);
    }

    private static SwiftWriter CreateSwiftWriter()
    {
        return new SwiftWriter(new System.IO.StringWriter());
    }

    private static ProtocolDecl CreateProtocolWithSelfRequirement(string name)
    {
        return new ProtocolDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}P",
            HasSelfRequirement = true,
            IsClassBound = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>
            {
                CreateInstanceMethod("doWork")
            },
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static ProtocolDecl CreateProtocolWithSelfTypedMethod(string name)
    {
        return new ProtocolDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}P",
            HasSelfRequirement = false,
            IsClassBound = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>
            {
                // Method with generic type param τ_0_0 in return type (Self-typed)
                new MethodDecl
                {
                    Name = "makeSelf",
                    MangledName = "$s_makeSelf",
                    MethodType = MethodType.Instance,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        new ArgumentDecl
                        {
                            SwiftTypeSpec = new NamedTypeSpec("\u03C4_0_0"),
                            Name = "",
                            PrivateName = "",
                            IsInOut = false,
                            IsGeneric = false,
                            ParentDecl = null,
                            ModuleDecl = null
                        }
                    },
                    GenericParameters = new List<GenericArgumentDecl>(),
                    ParentDecl = null,
                    ModuleDecl = null,
                    Throws = false,
                    IsAsync = false,
                    Visibility = Visibility.Public
                }
            },
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static ProtocolDecl CreateProtocolWithOnlyStaticMembers(string name)
    {
        return new ProtocolDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}P",
            HasSelfRequirement = false,
            IsClassBound = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>
            {
                new MethodDecl
                {
                    Name = "staticOnly",
                    MangledName = "$s_staticOnly",
                    MethodType = MethodType.Static,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        new ArgumentDecl
                        {
                            SwiftTypeSpec = TupleTypeSpec.Empty,
                            Name = "",
                            PrivateName = "",
                            IsInOut = false,
                            IsGeneric = false,
                            ParentDecl = null,
                            ModuleDecl = null
                        }
                    },
                    GenericParameters = new List<GenericArgumentDecl>(),
                    ParentDecl = null,
                    ModuleDecl = null,
                    Throws = false,
                    IsAsync = false,
                    Visibility = Visibility.Public
                }
            },
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static ProtocolDecl CreateValidProtocol(string name)
    {
        return new ProtocolDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}P",
            HasSelfRequirement = false,
            IsClassBound = false,
            Properties = new List<PropertyDecl>
            {
                new PropertyDecl
                {
                    Name = "value",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    HasStorage = false,
                    IsStatic = false,
                    Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = CreateInstanceMethod("getter:value") } },
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static MethodDecl CreateInstanceMethod(string name)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s_{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    Name = "",
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    private static ProtocolDecl CreateEmptyMarkerProtocol(string name)
    {
        return new ProtocolDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}P",
            HasSelfRequirement = false,
            IsClassBound = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    #endregion
}
