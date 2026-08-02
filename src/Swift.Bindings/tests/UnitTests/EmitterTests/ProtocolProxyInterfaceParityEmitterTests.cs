// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Full-pipeline gate on the invariant that a protocol proxy's member set equals the emitted C#
/// interface's member set.
///
/// <para>
/// The two are decided by different planes at different granularities, which is the whole hazard.
/// Interface membership is decided PER MEMBER by <c>ProtocolHandler</c>'s requirement gates.
/// Witness-dispatch eligibility is decided for the protocol as a WHOLE — a mixed-generic protocol
/// exports no SBW_ accessor for ANY member, so every member falls to the proxy's NotSupported-stub
/// path. A member the requirement gate already dropped for its own unrelated reason is absent from
/// the interface, yet that stub path would still emit it: the stub body delegates through
/// <c>_csharpImpl.{Name}(...)</c>, and <c>_csharpImpl</c> is typed as the interface, so the
/// delegation names a member the interface never declared — CS1061, and the binding does not
/// compile. Both decisions must read the one "did this requirement survive into the interface?"
/// fact.
/// </para>
/// </summary>
public class ProtocolProxyInterfaceParityEmitterTests
{
    [Fact]
    public void MixedGenericProtocol_RequirementDroppedFromInterface_EmitsNoProxyStub()
    {
        // `probe` carries a leaked associated-type reference, so the requirement gate drops it from
        // the interface. It is ALSO method-level generic, which (alongside the plain `ping`) makes
        // the protocol mixed-generic and routes every member to the stub path. Stubbing `probe`
        // there would emit `_csharpImpl.Probe(...)` against an interface that never declared Probe.
        var protocolDecl = BuildMixedGenericProtocol("Resolver");
        var csOutput = EmitModule("TestModule", protocolDecl);

        // Setup really is the mixed-generic shape — otherwise the stub path never runs and every
        // assertion below passes vacuously.
        Assert.True(EveryProtocolEmitter.IsMixedGenericProtocol(protocolDecl),
            "Test setup invalid: the protocol is not mixed-generic, so no member reaches the stub path.");

        // The gate dropped `probe` from the interface...
        Assert.DoesNotContain("void Probe(", csOutput);
        // ...so the proxy must not declare it, and above all must not delegate to it.
        Assert.DoesNotContain("_csharpImpl.Probe(", csOutput);

        // Discrimination: `ping` DID survive into the interface, so the proxy still owes it a stub.
        // Without this a regression that suppressed EVERY stub would satisfy the assertions above.
        Assert.Contains("_csharpImpl.Ping(", csOutput);
    }

    [Fact]
    public void MixedGenericProtocol_StubbedRequirement_IsReportedAsADegradedWitness()
    {
        // The stub carries an SB0003 [Obsolete] — the member is declared but every protocol-typed
        // call throws. That is the same degradation the non-dispatchable path records, so it must
        // produce the same report row; otherwise binding-report.json under-counts the exact surface
        // the SB0003 diagnostic is warning about.
        var protocolDecl = BuildMixedGenericProtocol("Resolver");

        BindingReport report;
        ReportCollector.Start(new ModuleDecl
        {
            Name = "TestModule",
            Dependencies = new List<string>(),
            Types = new List<TypeDecl>(),
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        });
        try
        {
            EmitModule("TestModule", protocolDecl);
            var completed = ReportCollector.Complete();
            Assert.NotNull(completed);
            report = completed!;
        }
        finally
        {
            ReportCollector.Reset();
        }

        Assert.True(EveryProtocolEmitter.IsMixedGenericProtocol(protocolDecl),
            "Test setup invalid: the protocol is not mixed-generic, so no member reaches the stub path.");

        var degraded = report.SkippedItems
            .Where(i => i.Reason == SkipReason.ProtocolWitnessNotDispatchable)
            .ToList();

        // `ping` survived into the interface and got a stub, so it owes a row.
        Assert.Contains(degraded, i => i.Name == "ping");
        // `probe` never reached the interface, so it got no stub and must not be reported as a
        // degraded witness — it is a plain drop, already covered by its own gate.
        Assert.DoesNotContain(degraded, i => i.Name == "probe");
    }

    // A proxy-eligible, non-class, non-Self protocol that is mixed-generic:
    //   ping()                       — plain instance method; survives the requirement gate.
    //   probe(item: τ_1_0.Element)   — method-level generic AND gate-dropped (leaked associated
    //                                  type reference), i.e. absent from the emitted interface.
    // The pair satisfies IsMixedGenericProtocol: one method-level-generic instance method plus one
    // non-generic instance method.
    private static ProtocolDecl BuildMixedGenericProtocol(string name)
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

        protocol.Methods.Add(CreateVoidMethod("ping"));

        var probe = CreateVoidMethod("probe");
        probe.CSSignature.Add(new ArgumentDecl
        {
            Name = "item",
            SwiftTypeSpec = new AssociatedTypeReferenceSpec("τ_1_0.Element"),
            PrivateName = "item",
            IsInOut = false,
            IsGeneric = true,
            ParentDecl = null,
            ModuleDecl = null
        });
        protocol.Methods.Add(probe);

        return protocol;
    }

    // An instance method returning Void. CSSignature[0] is the return slot.
    private static MethodDecl CreateVoidMethod(string name)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s{name}",
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
        };
    }

    // Runs the real ModuleHandler.Marshal + Emit over a single-protocol module and returns the
    // emitted C#. The protocol goes into BOTH Types and Protocols: the parser keeps those as
    // independent lists and ProtocolHandler — which emits the interface and the proxy — walks only
    // Types, so a Protocols-only entry would never exercise the path under test.
    private static string EmitModule(string moduleName, ProtocolDecl protocolDecl)
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
        typeDatabase.AddModuleDatabase(new ModuleTypeDatabase(moduleName, "/fake/path"));

        var csStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(new StringWriter());

        var handler = new ModuleHandler(new NullLogger<ModuleHandler>());
        var env = handler.Marshal(moduleDecl, typeDatabase);

        var conductor = new Conductor(new NullLoggerFactory());
        var context = new TypeHandlerContext(null, new(), null, EmissionContext: new ModuleEmissionContext());
        handler.Emit(csWriter, swiftWriter, env, conductor, context);

        return csStringWriter.ToString();
    }
}
