// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// A protocol-extension member injected onto a conformer reaches the extension member the ABI lists
/// under the protocol, so the injected member and its Swift wrapper carry that member's floor (its
/// own <c>@available</c>, or one inferred because an older OS lacks it) merged with the protocol's;
/// and the injected member also takes the floor inferred for the conformance its wrapper uses.
/// </summary>
public class ProtocolExtensionFloorTests
{
    private const string Module = "TestModule";

    private static AvailabilityAnnotation InferredFloor(string version) =>
        new("iOS", version, null, null, false, false, null, null) { IsRuntimeSupportFloor = true, IsInferredOsFloor = true };

    private static (ModuleDecl Module, StructDecl Conformer, ProtocolDecl Protocol, TypeDatabase Database) Setup()
    {
        var database = new TypeDatabase();
        database.AddModuleDatabase(new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib"));
        var moduleDatabase = new ModuleTypeDatabase(Module, $"/tmp/{Module}.dylib");
        moduleDatabase.RegisterType(SwiftTypeName.FromModuleQualifiedName($"{Module}.Drawable"), new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName($"Swift.{Module}", "IDrawable"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{Module}.Drawable"),
            MetadataAccessor = "",
            Flags = TypeRecordFlags.None,
            Kind = TypeRecordKind.Protocol,
        });
        moduleDatabase.RegisterType(SwiftTypeName.FromModuleQualifiedName($"{Module}.Canvas"), new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName($"Swift.{Module}", "Canvas"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{Module}.Canvas"),
            MetadataAccessor = "$s10TestModule6CanvasVMa",
            Flags = TypeRecordFlags.None,
            Kind = TypeRecordKind.Struct,
        });
        database.AddModuleDatabase(moduleDatabase);

        var module = new ModuleDecl
        {
            Name = Module,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
        };
        var protocol = TestDecls.Protocol("Drawable", Module);
        protocol.ParentDecl = module;
        protocol.ModuleDecl = module;
        module.Types.Add(protocol);
        var conformer = new StructDecl
        {
            Name = "Canvas",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{Module}.Canvas"),
            MangledName = "$s10TestModule6CanvasV",
            MetadataAccessor = "$s10TestModule6CanvasVMa",
            IsFrozen = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>
            {
                new(SwiftTypeName.FromModuleQualifiedName($"{Module}.Canvas"), SwiftTypeName.FromModuleQualifiedName($"{Module}.Drawable"), ""),
            },
            ParentDecl = module,
            ModuleDecl = module,
        };
        module.Types.Add(conformer);
        return (module, conformer, protocol, database);
    }

    private static MethodDecl AbiExtensionMember(ProtocolDecl protocol, string name, AvailabilityAnnotation? floor)
    {
        var member = TestDecls.ExtensionDefault(name, module: Module);
        member.ParentDecl = protocol;
        member.AvailabilityAnnotations = floor is null ? null : new List<AvailabilityAnnotation> { floor };
        protocol.ExtensionMembers.Add(member);
        return member;
    }

    private static Dictionary<string, List<ProtocolExtensionMethodDecl>> Extracted(params string[] names) => new()
    {
        [$"{Module}.Drawable"] = names.Select(name => new ProtocolExtensionMethodDecl
        {
            ProtocolQualifiedName = $"{Module}.Drawable",
            MethodName = name,
            RawSignature = $"public func {name}()",
            PrintedName = $"{name}()",
            WhereConstraints = new List<string>(),
        }).ToList(),
    };

    private static string WrapperPreamble(ModuleEmissionContext ctx, string name)
    {
        var lines = ctx.ProtocolExtSwiftWrapperLines.ToList();
        var declaration = lines.FindIndex(line => line.Contains($"_{name}") && (line.Contains("@_silgen_name") || line.Contains("@_cdecl")));
        Assert.True(declaration >= 0, $"no wrapper for {name}:\n{string.Join("\n", lines)}");
        var start = declaration;
        while (start > 0 && lines[start - 1].TrimStart().StartsWith('@'))
            start--;
        return string.Join("\n", lines.Skip(start).Take(declaration - start + 1));
    }

    [Fact]
    public void AFlooredExtensionMember_FloorsTheInjectedMember_AndItsWrapper()
    {
        var (module, conformer, protocol, database) = Setup();
        AbiExtensionMember(protocol, "outline", InferredFloor("27.2"));
        var ctx = new ModuleEmissionContext();

        ProtocolExtensionEmitter.InjectExtensionMethods(module, Extracted("outline"), database, NullLogger.Instance, ctx);

        var injected = Assert.Single(conformer.Methods);
        var floor = Assert.Single(injected.AvailabilityAnnotations!, a => a.Platform == "iOS");
        Assert.Equal("27.2", floor.IntroducedVersion);
        Assert.True(floor.IsInferredOsFloor);
        Assert.Contains("@available(iOS 27.2, *)", WrapperPreamble(ctx, "outline"));
    }

    [Fact]
    public void TheProtocolsOwnFloor_StillReachesTheWrapper_BesideTheMembers()
    {
        var (module, conformer, protocol, database) = Setup();
        protocol.AvailabilityAnnotations = new List<AvailabilityAnnotation> { new("macOS", "15.0", null, null, false, false, null, null) };
        AbiExtensionMember(protocol, "outline", InferredFloor("27.2"));
        var ctx = new ModuleEmissionContext();

        ProtocolExtensionEmitter.InjectExtensionMethods(module, Extracted("outline"), database, NullLogger.Instance, ctx);

        var preamble = WrapperPreamble(ctx, "outline");
        Assert.Contains("@available(iOS 27.2, *)", preamble);
        Assert.Contains("@available(macOS 15.0, *)", preamble);
        Assert.Contains(Assert.Single(conformer.Methods).AvailabilityAnnotations!, a => a.Platform == "macOS");
    }

    [Fact]
    public void AnUnflooredExtensionMember_LeavesTheInjectedMemberAtTheConformersFloor()
    {
        var (module, conformer, protocol, database) = Setup();
        AbiExtensionMember(protocol, "outline", floor: null);
        AbiExtensionMember(protocol, "fill", InferredFloor("27.2"));
        var ctx = new ModuleEmissionContext();

        ProtocolExtensionEmitter.InjectExtensionMethods(module, Extracted("outline"), database, NullLogger.Instance, ctx);

        Assert.Null(Assert.Single(conformer.Methods).AvailabilityAnnotations);
        Assert.DoesNotContain("@available", WrapperPreamble(ctx, "outline"));
    }

    /// <summary>
    /// A protocol the inventory's OS lacks floors every member copied from its extension, though the
    /// ABI lists no floor on the extension member itself: the wrapper reaches the member through the
    /// protocol.
    /// </summary>
    [Fact]
    public void AFlooredProtocol_FloorsTheInjectedMember_ThoughItsExtensionMemberAddsNone()
    {
        var (module, conformer, protocol, database) = Setup();
        protocol.AvailabilityAnnotations = new List<AvailabilityAnnotation> { InferredFloor("27.2") };
        AbiExtensionMember(protocol, "outline", floor: null);
        var ctx = new ModuleEmissionContext();

        ProtocolExtensionEmitter.InjectExtensionMethods(module, Extracted("outline"), database, NullLogger.Instance, ctx);

        var floor = Assert.Single(Assert.Single(conformer.Methods).AvailabilityAnnotations!, a => a.Platform == "iOS");
        Assert.Equal("27.2", floor.IntroducedVersion);
    }

    [Fact]
    public void AConformanceTheOsLacks_FloorsTheInjectedMember()
    {
        var (module, conformer, protocol, database) = Setup();
        const string descriptor = "$s10TestModule6CanvasVAA8DrawableAAMc";
        conformer.Conformances[0] = conformer.Conformances[0] with { ProtocolConformanceDescriptor = descriptor };
        module.InferredConformanceFloors[descriptor] = new List<AvailabilityAnnotation> { InferredFloor("27.2") };
        AbiExtensionMember(protocol, "outline", floor: null);
        var ctx = new ModuleEmissionContext();

        ProtocolExtensionEmitter.InjectExtensionMethods(module, Extracted("outline"), database, NullLogger.Instance, ctx);

        var floor = Assert.Single(Assert.Single(conformer.Methods).AvailabilityAnnotations!, a => a.Platform == "iOS");
        Assert.Equal("27.2", floor.IntroducedVersion);
        // Swift knows nothing of an inferred conformance floor, so the wrapper declares none.
        Assert.DoesNotContain("@available", WrapperPreamble(ctx, "outline"));
    }

    /// <summary>
    /// A nested protocol sharing the extended protocol's name must not stand in for it: the floor comes
    /// from the protocol the ABI qualifies, wherever the other one is declared, and an unqualified
    /// <c>extension Drawable</c> names the top-level protocol, as Swift's lookup at file scope does.
    /// </summary>
    [Theory]
    [InlineData(Module + ".Drawable")]
    [InlineData("Drawable")]
    public void TheExtendedProtocol_IsFoundByItsQualifiedName_BeforeANestedNamesake(string extendedName)
    {
        var (module, conformer, protocol, database) = Setup();
        var outer = TestDecls.Protocol("Shapes", Module);
        var namesake = TestDecls.Protocol("Drawable", Module);
        namesake.SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{Module}.Shapes.Drawable");
        namesake.ParentDecl = outer;
        outer.Types.Add(namesake);
        module.Types.Insert(0, outer);
        AbiExtensionMember(protocol, "outline", InferredFloor("27.2"));
        var ctx = new ModuleEmissionContext();

        var extracted = Extracted("outline");
        foreach (var member in extracted[$"{Module}.Drawable"])
            member.ProtocolQualifiedName = extendedName;

        ProtocolExtensionEmitter.InjectExtensionMethods(module, extracted, database, NullLogger.Instance, ctx);

        Assert.Equal("27.2", Assert.Single(Assert.Single(conformer.Methods).AvailabilityAnnotations!, a => a.Platform == "iOS").IntroducedVersion);
    }
}
