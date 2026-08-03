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
/// Gates the reverse-dispatch honesty contract: a protocol interface is conformable in C#, which is a
/// promise that Swift will call the conformer back. That promise is kept only for requirements that get a
/// receiver trampoline wired into the proxy vtable. When NO requirement does, the binding must register no
/// vtable and mark the interface — rather than hand Swift a table of nulls and leave a C# implementation
/// silently uncalled.
///
/// The gate is strictly "zero callback slots filled", never "some requirement was dropped": a partially
/// dispatchable protocol still does real reverse dispatch through its surviving slots and must be left
/// entirely alone. Every test here pairs the hollow case with that partial case so the boundary can't drift.
/// </summary>
public class ReverseDispatchHonestyEmitterTests
{
    [Fact]
    public void HollowProtocol_InterfaceCarriesReverseDispatchMarker()
    {
        var (csOutput, _) = EmitProtocol(HollowProtocol());

        // The interface still exists and still declares the requirement — forward use (a Swift-vended
        // conformer consumed through it) is unaffected, and removing it would break working code.
        Assert.Contains("public interface IHollowDelegate", csOutput);
        Assert.Contains("DiagnosticId = \"SB0010\"", csOutput);
    }

    [Fact]
    public void HollowProtocol_RegistersNoVtableWithSwift()
    {
        var (csOutput, _) = EmitProtocol(HollowProtocol());

        // Registration is what makes the null table observable to Swift. Suppressing it is the half of
        // the fix that changes behaviour; the marker is the half that makes it visible.
        Assert.DoesNotContain("SetHollowDelegate_vtable((IntPtr)vtPtr)", csOutput);
    }

    [Fact]
    public void PartiallyDispatchableProtocol_KeepsItsVtableAndCarriesNoMarker()
    {
        var (csOutput, _) = EmitProtocol(PartialProtocol());

        // One filled slot is enough to keep the interface honest: a C# implementation genuinely IS
        // called back for that requirement, so degrading it would cost real, working reverse dispatch.
        Assert.Contains("public interface IPartialDelegate", csOutput);
        Assert.DoesNotContain("SB0010", csOutput);
        Assert.Contains("SetPartialDelegate_vtable((IntPtr)vtPtr)", csOutput);
    }

    [Fact]
    public void HollowProtocol_RecordsClassifiedSkipRow()
    {
        ReportCollector.Start(CreateModuleDecl("TestModule"));
        try
        {
            EmitProtocol(HollowProtocol());
            var report = ReportCollector.Complete();

            Assert.NotNull(report);
            var row = Assert.Single(report!.SkippedItems,
                i => i.Reason == SkipReason.ProtocolProxyVtableEmpty);
            Assert.Equal("HollowDelegate", row.Name);
            // The row has to name what could not be wired — a bare "empty vtable" tells a consumer
            // nothing about which requirement shapes to change.
            Assert.Contains("acceptCallback", row.Details);
        }
        finally
        {
            ReportCollector.Reset();
        }
    }

    [Fact]
    public void HollowChildOfAnotherProtocol_IsNeitherMarkedNorReported()
    {
        var typeDatabase = CreateTypeDatabase(withBaseDelegateProtocol: true);

        // Without this the test could pass for the wrong reason: a child whose own plan stopped being
        // hollow is unmarked by the plain rule, and the inheritance carve-out would go untested.
        Assert.True(ProtocolVtableFillPlanBuilder.Build(HollowChildProtocol(), typeDatabase).IsHollow);

        ReportCollector.Start(CreateModuleDecl("TestModule"));
        try
        {
            var (csOutput, _) = EmitProtocol(HollowChildProtocol(), typeDatabase);
            var report = ReportCollector.Complete();

            // A hollow OWN vtable is not the same fact as an inert interface. This child inherits
            // IBaseDelegate, so a C# implementation of it still gets called back through the ancestor's
            // proxy for every requirement it inherits — the interface is not inert and saying otherwise
            // would send a consumer chasing a problem they do not have.
            Assert.Contains("public interface IChildDelegate : IBaseDelegate", csOutput);
            Assert.DoesNotContain("SB0010", csOutput);

            // Marker and report must move together: gating only the source text would leave the report
            // asserting something the source contradicts.
            Assert.NotNull(report);
            Assert.DoesNotContain(report!.SkippedItems,
                i => i.Reason == SkipReason.ProtocolProxyVtableEmpty);
        }
        finally
        {
            ReportCollector.Reset();
        }
    }

    [Fact]
    public void PartiallyDispatchableProtocol_RecordsNoSkipRow()
    {
        ReportCollector.Start(CreateModuleDecl("TestModule"));
        try
        {
            EmitProtocol(PartialProtocol());
            var report = ReportCollector.Complete();

            Assert.NotNull(report);
            Assert.DoesNotContain(report!.SkippedItems,
                i => i.Reason == SkipReason.ProtocolProxyVtableEmpty);
        }
        finally
        {
            ReportCollector.Reset();
        }
    }

    [Fact]
    public void ReverseDispatchSkip_IsDeclaredButDegraded()
    {
        // The interface and its members are still emitted, so this reason must never be counted as
        // lost surface — doing so would double-count against the surface metrics.
        Assert.True(SkipDispositionClassifier.IsDeclaredButDegraded(SkipReason.ProtocolProxyVtableEmpty));
    }

    [Fact]
    public void FillPlan_CountsFilledCallbackSlots_NotVtableMembership()
    {
        var typeDatabase = CreateTypeDatabase();
        var hollow = ProtocolVtableFillPlanBuilder.Build(HollowProtocol(), typeDatabase);
        var partial = ProtocolVtableFillPlanBuilder.Build(PartialProtocol(), typeDatabase);

        Assert.True(hollow.ObligationCount > 0);
        Assert.Equal(0, hollow.FilledCallbackCount);
        Assert.True(hollow.IsHollow);

        Assert.True(partial.FilledCallbackCount > 0);
        Assert.False(partial.IsHollow);
    }

    [Fact]
    public void FillPlan_ProtocolWithNoRequirements_IsNotHollow()
    {
        // A marker protocol declares nothing, so nothing was promised and nothing is broken. The gate
        // must fire on "declared but unwireable", not on "empty" — otherwise every marker protocol in
        // a corpus picks up a marker that tells its consumer nothing actionable.
        var plan = ProtocolVtableFillPlanBuilder.Build(
            MakeProtocol("Marker", new List<MethodDecl>()), CreateTypeDatabase());

        Assert.Equal(0, plan.ObligationCount);
        Assert.False(plan.IsHollow);
    }

    [Fact]
    public void FillPlan_StaticAndConstructorRequirements_AreNotObligations()
    {
        // Neither can be reverse-dispatched by construction — Swift never calls a static requirement or
        // an initializer back through an instance vtable. Counting them as unfilled obligations would
        // mark an otherwise-fine protocol whose only real requirement dispatches perfectly.
        var moduleDecl = CreateModuleDecl("TestModule");
        var protocolDecl = MakeProtocol("StaticBearing", new List<MethodDecl>
        {
            MakeMethod("create", moduleDecl, methodType: MethodType.Static),
            MakeMethod("initialize", moduleDecl, isConstructor: true),
            MakeMethod("ping", moduleDecl),
        });

        var plan = ProtocolVtableFillPlanBuilder.Build(protocolDecl, CreateTypeDatabase());

        Assert.Equal(1, plan.ObligationCount);
        Assert.False(plan.IsHollow);
    }

    [Fact]
    public void HollowProtocolThatIsAlreadyUnconditionallyDeprecated_GetsNoSecondMarker()
    {
        // [Obsolete] is AllowMultiple=false — a second one is CS0579 and the whole binding stops
        // compiling. The availability marker already forces consumer attention, so it wins.
        var protocolDecl = HollowProtocol() with
        {
            AvailabilityAnnotations = new List<AvailabilityAnnotation>
            {
                new(Platform: "*", IntroducedVersion: null, DeprecatedVersion: null, ObsoletedVersion: null,
                    IsUnconditionallyDeprecated: true, IsUnconditionallyUnavailable: false,
                    Message: "gone", Renamed: null)
            }
        };

        var (csOutput, _) = EmitProtocol(protocolDecl);

        // The failure mode is two markers STACKED on one declaration, so assert on adjacency rather
        // than on a whole-output count (the output holds several declarations, each entitled to one).
        var lines = csOutput.Split('\n').Select(l => l.TrimStart()).ToList();
        for (int i = 1; i < lines.Count; i++)
        {
            Assert.False(
                lines[i].StartsWith("[Obsolete(", System.StringComparison.Ordinal) &&
                lines[i - 1].StartsWith("[Obsolete(", System.StringComparison.Ordinal),
                $"Two [Obsolete] attributes stacked on one declaration (CS0579) at line {i + 1}.");
        }
        Assert.DoesNotContain("SB0010", csOutput);
    }

    /// <summary>
    /// Every requirement pairs a closure parameter with a non-<c>Void</c> return. The dispatch path treats
    /// the closure as the method's only output channel, so a real return value has nowhere to go alongside
    /// it — the requirement stays on the interface but fills no callback slot.
    /// </summary>
    private static ProtocolDecl HollowProtocol()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        return MakeProtocol("HollowDelegate", new List<MethodDecl>
        {
            MakeClosureReturningMethod("acceptCallback", moduleDecl),
        });
    }

    /// <summary>
    /// Carries the identical non-dispatchable requirement plus one plain requirement that does dispatch.
    /// </summary>
    private static ProtocolDecl PartialProtocol()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        return MakeProtocol("PartialDelegate", new List<MethodDecl>
        {
            MakeClosureReturningMethod("acceptCallback", moduleDecl),
            MakeMethod("ping", moduleDecl),
        });
    }

    /// <summary>
    /// The same unwireable requirement as <see cref="HollowProtocol"/>, but declared on a protocol that
    /// inherits another. Its OWN vtable is hollow while everything it inherits still reverse-dispatches
    /// through the ancestor's proxy, so it is not an inert interface.
    /// </summary>
    private static ProtocolDecl HollowChildProtocol()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        return MakeProtocol("ChildDelegate", new List<MethodDecl>
        {
            MakeClosureReturningMethod("acceptCallback", moduleDecl),
        },
        inheritedProtocols: new List<NamedTypeSpec> { new("TestModule.BaseDelegate") });
    }

    private static ProtocolDecl MakeProtocol(
        string name, List<MethodDecl> methods, List<NamedTypeSpec>? inheritedProtocols = null)
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        return new ProtocolDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}P",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = inheritedProtocols ?? new List<NamedTypeSpec>(),
            IsClassBound = false,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = methods,
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
    }

    /// <summary>A `func name(cb: () -&gt; Void) -&gt; Int` requirement — closure param plus a real return.</summary>
    private static MethodDecl MakeClosureReturningMethod(string name, ModuleDecl moduleDecl)
    {
        var method = MakeMethod(name, moduleDecl);
        return method with
        {
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, new NamedTypeSpec("Swift.Int"), moduleDecl),
                CreateArgument("cb", ClosureTypeSpec.VoidVoid, moduleDecl),
            }
        };
    }

    private static MethodDecl MakeMethod(
        string name, ModuleDecl moduleDecl, MethodType methodType = MethodType.Instance, bool isConstructor = false)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule{name.Length}{name}yyF",
            MethodType = methodType,
            IsConstructor = isConstructor,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
    }

    private static (string csOutput, string swiftOutput) EmitProtocol(ProtocolDecl protocolDecl)
        => EmitProtocol(protocolDecl, CreateTypeDatabase());

    private static (string csOutput, string swiftOutput) EmitProtocol(
        ProtocolDecl protocolDecl, TypeDatabase typeDatabase)
    {
        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        var handler = new ProtocolHandler(new NullLogger<ProtocolHandler>());
        var env = handler.Marshal(protocolDecl, typeDatabase);
        var conductor = new Conductor(new NullLoggerFactory());
        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        return (csOutput.ToString(), swiftOutput.ToString());
    }

    private static TypeDatabase CreateTypeDatabase(bool withBaseDelegateProtocol = false)
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

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        if (withBaseDelegateProtocol)
        {
            // A parent only becomes an inherited INTERFACE if the type database can resolve it as a
            // plain protocol — an unresolvable or PAT/Self-typed parent is filtered out of the base
            // list, which is exactly the state the marker's carve-out must not confuse with "no parent".
            var baseName = SwiftTypeName.FromModuleQualifiedName("TestModule.BaseDelegate");
            testModule.RegisterType(baseName, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "IBaseDelegate"),
                SwiftTypeName = baseName,
                MetadataAccessor = string.Empty,
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        }
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

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

    private static ArgumentDecl CreateArgument(string name, TypeSpec typeSpec, ModuleDecl moduleDecl)
    {
        return new ArgumentDecl
        {
            SwiftTypeSpec = typeSpec,
            Name = name,
            PrivateName = name,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
    }
}
