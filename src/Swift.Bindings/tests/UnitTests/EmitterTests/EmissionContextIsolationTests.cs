// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Pins the isolation an emission run gets when the caller supplies no
/// <see cref="ModuleEmissionContext"/>: the fallback must belong to the emission that asked for it,
/// never to the process.
///
/// <para>A process-wide fallback makes two concurrent emissions write into one another's dedup
/// registries and per-module accumulators. Those accumulators are enumerated while emission is still
/// running (the module initializer walks the emitted-type and payload-semantics lists), so the
/// sharing surfaces as a "Collection was modified; enumeration operation may not execute" throw from
/// an unrelated test — and, when the timing misses, as silently cross-contaminated output.</para>
/// </summary>
public class EmissionContextIsolationTests
{
    [Fact]
    public void GetEmissionContext_TwoIndependentContexts_AreNotShared()
    {
        var first = TypeHandlerContext.Empty;
        var second = TypeHandlerContext.Empty;

        Assert.NotSame(first.GetEmissionContext(), second.GetEmissionContext());
    }

    [Fact]
    public void GetEmissionContext_DerivedContext_SharesTheOriginatingEmissionContext()
    {
        // Nested handlers thread state by cloning the context with `with`. The fallback has to
        // survive that clone as the SAME instance or a nested handler's dedup registrations become
        // invisible to its parent and shared Swift helpers get emitted twice.
        var root = TypeHandlerContext.Empty;
        var nested = root with { PropertyRenames = new Dictionary<string, string>() };

        Assert.Same(root.GetEmissionContext(), nested.GetEmissionContext());
    }

    [Fact]
    public void ParallelEmissions_WithoutSuppliedEmissionContext_ProduceTheSerialOutput()
    {
        var expected = EmitFixtureModule();

        var outputs = new string[8 * 16];
        Parallel.For(0, 8, worker =>
        {
            for (var i = 0; i < 16; i++)
                outputs[worker * 16 + i] = EmitFixtureModule();
        });

        Assert.All(outputs, output => Assert.Equal(expected, output));
    }

    /// <summary>
    /// Emits a one-class module through the real ModuleHandler pipeline with no emission context
    /// supplied — the shape that falls back. The class is what makes the run both a writer (it
    /// registers itself for module-initializer factory + payload-semantics emission) and a reader
    /// (the framework resolver enumerates those registrations at the end of the module).
    /// </summary>
    private static string EmitFixtureModule()
    {
        const string moduleName = "IsolationFixture";

        var moduleDecl = new ModuleDecl
        {
            Name = moduleName,
            Dependencies = new List<string>(),
            Types = new List<TypeDecl>(),
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var classDecl = new ClassDecl
        {
            Name = "Widget",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.Widget"),
            MangledName = $"$s16{moduleName}6WidgetCN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Types.Add(classDecl);

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

        var fixtureModule = new ModuleTypeDatabase(moduleName, $"/fake/{moduleName}.dylib");
        fixtureModule.RegisterType(
            classDecl.SwiftTypeName,
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(moduleName, "Widget"),
                SwiftTypeName = classDecl.SwiftTypeName,
                MetadataAccessor = $"$s16{moduleName}6WidgetCMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(fixtureModule);

        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);

        var handler = new ModuleHandler(new Microsoft.Extensions.Logging.Abstractions.NullLogger<ModuleHandler>());
        var env = handler.Marshal(moduleDecl, typeDatabase);
        var conductor = new Conductor(new Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory());

        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        return csStringWriter.ToString();
    }
}
