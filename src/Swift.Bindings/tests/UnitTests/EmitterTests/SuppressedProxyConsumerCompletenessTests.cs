// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Regression net for the ingestion-hardening SwiftRichString <c>StyleProtocolProxy</c> CS0246: when a
/// protocol's proxy is suppressed because a required member reaches a type withheld from the type
/// database (an ingestion-quarantined <c>Foundation._NSRange</c>, or a SwiftUI/Combine unsupported
/// module), the proxy takes the <see cref="ProxyEmissionDecision.SkippedUnsupportedModule"/> arm. That
/// arm used to be the ONE non-emit decision the generator did NOT record in
/// <see cref="ModuleEmissionContext.SuppressedProxyClassNames"/>, on the (partly-true) assumption that
/// "references are handled elsewhere". But a RETAINED consumer of <c>any P</c> — e.g. the quarantine
/// path withdraws <c>P</c>'s offending methods yet keeps <c>P</c>, its interface, and a
/// <c>consume(base: any P)</c> declaration — projects the existential to
/// <c>new {P}Proxy(__v)</c>. With the proxy neither emitted nor recorded, every downgrade gate reads
/// "not suppressed" and the dangling <c>new {P}Proxy(</c> ships → CS0246.
///
/// <para>
/// The fix is COMPLETENESS: record EVERY non-emit proxy decision, so the already-proven consumer
/// downgrade machinery (pinned by <c>SuppressedProxyTypeSpecWalkTests</c> /
/// <c>SuppressedProxyProjectionWalkTests</c> across scalar/array/optional/dict/tuple shapes) fires for
/// the skipped-unsupported-module proxy exactly as it does for the suppressed-by-conformance one. These
/// tests lock (a) the recording at the precompute pass and (b) the absence of a dangling proxy
/// construction in the full-pipeline emitted C#.
/// </para>
/// </summary>
public class SuppressedProxyConsumerCompletenessTests : IDisposable
{
    private readonly string _dir;

    public SuppressedProxyConsumerCompletenessTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sbw-suppressed-consumer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Precompute_SkippedUnsupportedModuleProxy_IsRecordedSuppressed()
    {
        // A protocol proxy skipped because a member reaches an unsupported module MUST be recorded in
        // the suppressed set, or every consumer downgrade gate reads "not suppressed" and emits a
        // dangling `new {P}Proxy(`. Before the completeness fix the precompute pass recorded ONLY the
        // suppressed-by-conformance arm, so this protocol's proxy was silently unrecorded (RED).
        var protocolDecl = BuildProxyWithUnsupportedMember("Stylable");
        Assert.Equal(
            ProxyEmissionDecision.SkippedUnsupportedModule,
            ProtocolProxyEmissionPolicy.Decide(protocolDecl, BuildTypeDatabase("TestModule"), new ModuleEmissionContext()));

        var moduleDecl = BuildModule("TestModule", protocolDecl, consumerType: null, consumerProtocol: null);
        var ctx = new ModuleEmissionContext();
        SuppressedProxyPrecomputer.Precompute(moduleDecl, BuildTypeDatabase("TestModule"), ctx);

        Assert.Contains("StylableProxy", ctx.SuppressedProxyClassNames);
    }

    [Fact]
    public void SkippedUnsupportedModuleProxy_WithRetainedExistentialConsumer_NoDanglingProxyReference()
    {
        // The SwiftRichString shape reduced to one module: protocol P has a proxy suppressed for an
        // unsupported-module member, but a retained CLASS `StyleHost` survives with a member
        // `apply(base: any P)` that projects `any P`. A type member routes through
        // TypeProjectionFactory -> ExistentialProjection (unlike a free function, which takes the
        // ExistentialBypassEmitter `object`/ISwiftExistentialConvertible path), so its CONSUME arm
        // builds `new StylableProxy(__v)` unless the proxy is recorded suppressed. The emitted C# must
        // NOT construct the never-emitted `new StylableProxy(`.
        var protocolDecl = BuildProxyWithUnsupportedMember("Stylable");
        var (csOutput, _) = EmitModule("TestModule", protocolDecl, consumerType: "StyleHost", consumerProtocol: "Stylable");

        // The interface is still emitted (the protocol itself is retained) — so this is genuinely the
        // "retained consumer of a suppressed proxy" cell, not a "whole protocol dropped" one.
        Assert.Contains("interface IStylable", csOutput);
        // The consuming class member is still emitted (the consumer is retained).
        Assert.Contains("StyleHost", csOutput);
        // The proxy class was NOT emitted...
        Assert.DoesNotContain("class StylableProxy", csOutput);
        // ...so no consumer may construct it. This is the dangling reference the fix removes.
        Assert.DoesNotContain("new StylableProxy(", csOutput);
    }

    // --- helpers -------------------------------------------------------------------------------

    // A proxy-eligible protocol (implementable void `ping()`) plus a `decorate(view:)` requirement whose
    // parameter type lives in an unsupported module (SwiftUI) absent from the DB — the exact trigger for
    // ProtocolProxyEmissionPolicy.Decide => SkippedUnsupportedModule. Mirrors StyleProtocol, whose
    // add/remove/set reach the quarantined Foundation._NSRange (also withheld from the DB).
    private static ProtocolDecl BuildProxyWithUnsupportedMember(string name)
    {
        var protocol = new ProtocolDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}P",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            HasSelfRequirement = false,
            IsClassBound = false,
            ParentDecl = null,
            ModuleDecl = null
        };

        protocol.Methods.Add(new MethodDecl
        {
            Name = "ping",
            MangledName = "$s10TestModule8Stylable4pingyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = "",
                    SwiftTypeSpec = TupleTypeSpec.Empty,
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
            IsSynthesizedAccessor = false
        });

        protocol.Methods.Add(new MethodDecl
        {
            Name = "decorate",
            MangledName = "$s10TestModule8Stylable8decorateyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                // Return slot (void).
                new()
                {
                    Name = "",
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                },
                // A SwiftUI parameter — unsupported module, absent from the DB.
                new()
                {
                    Name = "view",
                    SwiftTypeSpec = new NamedTypeSpec("SwiftUI.AnyView"),
                    PrivateName = "view",
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
            IsSynthesizedAccessor = false
        });

        return protocol;
    }

    // A retained CLASS `StyleHost` with an instance member `apply(base: any P)` — the SwiftRichString
    // `StyleXML.init(base: any StyleProtocol)` shape. A type member (unlike a module-level free function,
    // which takes the ExistentialBypassEmitter `object`/ISwiftExistentialConvertible path) routes its
    // `any P` parameter through TypeProjectionFactory -> ExistentialProjection, whose CONSUME arm builds
    // `new {P}Proxy(__v)` unless the proxy is recorded suppressed. Must be registered in the type DB so
    // the emitter treats it as a retained bindable type.
    private static ClassDecl BuildRetainedConsumerType(string typeName, string protocolName, ModuleDecl owner)
    {
        var consumer = new ClassDecl
        {
            Name = typeName,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{owner.Name}.{typeName}"),
            MangledName = $"$s10{owner.Name}{typeName.Length}{typeName}CN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = owner,
            ModuleDecl = owner
        };

        consumer.Methods.Add(new MethodDecl
        {
            Name = "apply",
            MangledName = $"$s10{owner.Name}{typeName.Length}{typeName}C5applyyyAA{protocolName}_pF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                // Return slot (void).
                new()
                {
                    Name = "",
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                },
                // `base: any P`.
                new()
                {
                    Name = "base",
                    SwiftTypeSpec = new NamedTypeSpec($"{owner.Name}.{protocolName}") { IsAny = true },
                    PrivateName = "base",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = consumer,
            ModuleDecl = owner,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        });

        return consumer;
    }

    private static ModuleDecl BuildModule(string moduleName, ProtocolDecl protocolDecl, string? consumerType, string? consumerProtocol)
    {
        var moduleDecl = new ModuleDecl
        {
            Name = moduleName,
            Dependencies = new List<string>(),
            Types = new List<TypeDecl> { protocolDecl },
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl> { protocolDecl },
            ParentDecl = null,
            ModuleDecl = null
        };
        if (consumerType is not null && consumerProtocol is not null)
            moduleDecl.Types.Add(BuildRetainedConsumerType(consumerType, consumerProtocol, moduleDecl));
        return moduleDecl;
    }

    private static TypeDatabase BuildTypeDatabase(string moduleName, string? consumerType = null, string protocolName = "Stylable")
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.NIntType,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);
        var module = new ModuleTypeDatabase(moduleName, "/fake/path");
        // Register the protocol as a real Protocol TypeRecord (no PAT/Self flags) so
        // GetPublicExistentialType projects `any P` to `I{P}` (not `object`) and the CONSUME arm
        // reaches the `new {P}Proxy(__v)` construction — the exact SwiftRichString StyleProtocol shape.
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{protocolName}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(moduleName, $"I{protocolName}"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{protocolName}"),
                MetadataAccessor = $"$s10{moduleName}{protocolName.Length}{protocolName}Mp",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        if (consumerType is not null)
        {
            module.RegisterType(
                SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{consumerType}"),
                new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName(moduleName, consumerType),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{consumerType}"),
                    MetadataAccessor = $"$s10{moduleName}{consumerType.Length}{consumerType}CMa",
                    Flags = TypeRecordFlags.RequiresMemoryManagement,
                    Kind = TypeRecordKind.Class
                });
        }
        typeDatabase.AddModuleDatabase(module);
        return typeDatabase;
    }

    private (string csOutput, string swiftOutput) EmitModule(
        string moduleName, ProtocolDecl protocolDecl, string? consumerType = null, string? consumerProtocol = null)
    {
        var moduleDecl = BuildModule(moduleName, protocolDecl, consumerType, consumerProtocol);
        var typeDatabase = BuildTypeDatabase(moduleName, consumerType);

        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);

        var handler = new ModuleHandler(new NullLogger<ModuleHandler>());
        var env = handler.Marshal(moduleDecl, typeDatabase);

        var conductor = new Conductor(new NullLoggerFactory());
        var emissionCtx = new ModuleEmissionContext();
        var context = new TypeHandlerContext(null, new(), null, EmissionContext: emissionCtx);
        handler.Emit(csWriter, swiftWriter, env, conductor, context);

        return (csStringWriter.ToString(), swiftStringWriter.ToString());
    }
}
