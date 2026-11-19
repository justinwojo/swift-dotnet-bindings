// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static BindingsGeneration.Tests.ProtocolExtensionTestHelpers;

namespace BindingsGeneration.Tests;

/// <summary>
/// Every protocol-extension member the injection pass does not bind lands in the skip report with
/// the reason it was declined, on the protocol when nothing carries it and on the conformer when
/// only that conformer lost it. The emitted surface is unchanged: these tests assert the report
/// alongside the same injection results the emitter produced before.
/// </summary>
[Collection("ReportCollector")]
public class ProtocolExtensionDeclineReportingTests
{
    private static readonly ILogger Logger = NullLogger.Instance;

    private static List<SkippedItem> Inject(
        ModuleDecl moduleDecl,
        Dictionary<string, List<ProtocolExtensionMethodDecl>> extMethods,
        TypeDatabase typeDatabase,
        ModuleEmissionContext ctx,
        bool installStandardSdk = true)
    {
        // Another module's protocol is read from its interface in the run's SDK.
        using var sdk = installStandardSdk ? FakeSdkInterfaces.Standard() : null;
        ReportCollector.Start(moduleDecl);
        try
        {
            ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);
            return ReportCollector.Complete()!.SkippedItems.ToList();
        }
        finally
        {
            ReportCollector.Reset();
        }
    }

    [Fact]
    public void MemberOfAProtocolNoTypeConformsTo_IsReportedOnTheProtocol()
    {
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "Widget", "Gadget");
        var extMethods = CreateExtensionMethodDict("TestModule.Adoptable",
            CreateExtMethod("shutdown", "public func shutdown()"));

        var skipped = Inject(moduleDecl, extMethods, typeDatabase, new ModuleEmissionContext());

        Assert.Empty(conformingType.Methods);
        var row = Assert.Single(skipped);
        Assert.Equal("shutdown", row.Name);
        Assert.Equal("TestModule.Adoptable", row.ContainingType);
        Assert.Equal(SkipReason.ProtocolExtensionUncarried, row.Reason);
        Assert.Contains("no type in this module conforms", row.Details);
    }

    [Theory]
    [InlineData("static", SkipReason.ProtocolExtensionUncarried)]
    [InlineData("constrained", SkipReason.ProtocolExtensionUncarried)]
    [InlineData("settable", SkipReason.ProtocolExtensionUncarried)]
    [InlineData("async", SkipReason.ProtocolExtensionUncarried)]
    [InlineData("typedThrows", SkipReason.ProtocolExtensionUncarried)]
    public void MemberShapeTheExtensionPathDoesNotInject_IsReportedOnTheProtocol(string shape, SkipReason expected)
    {
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "Widget", "Gadget");
        var member = shape switch
        {
            "static" => CreateExtMethod("make", "public static func make()"),
            "constrained" => CreateExtMethod("reset", "public func reset()"),
            "settable" => CreateExtProperty("count", "public var count: Swift.Int32 { get set }", hasSetter: true),
            "async" => CreateExtMethod("refresh", "public func refresh() async"),
            _ => CreateExtMethod("load", "public func load() throws(TestModule.LoadError)"),
        };
        if (shape == "static")
            member.IsStatic = true;
        if (shape == "constrained")
            member.WhereConstraints.Add("Self : Swift.Equatable");
        var extMethods = CreateExtensionMethodDict("TestModule.Gadget", member);

        var ctx = new ModuleEmissionContext();
        var skipped = Inject(moduleDecl, extMethods, typeDatabase, ctx);

        // Injection itself is unchanged: the shape was never bound.
        Assert.Empty(conformingType.Methods);
        Assert.Empty(ctx.ProtocolExtSwiftWrapperLines);
        var row = Assert.Single(skipped);
        Assert.Equal(member.MethodName, row.Name);
        Assert.Equal("TestModule.Gadget", row.ContainingType);
        Assert.Equal(expected, row.Reason);
    }

    [Fact]
    public void MemberEveryConformerDeclines_IsReportedOnTheProtocolWithTheConformersReason()
    {
        // `{ get async }` cannot be read by the synchronous wrapper; the only conformer declines it.
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "WelcomeTip", "TipLike");
        var extMethods = CreateExtensionMethodDict("TestModule.TipLike",
            CreateExtProperty("liveScore", "public var liveScore: Swift.Int32 { get async }"));

        var skipped = Inject(moduleDecl, extMethods, typeDatabase, new ModuleEmissionContext());

        Assert.Empty(conformingType.Methods);
        var row = Assert.Single(skipped);
        Assert.Equal("TestModule.TipLike", row.ContainingType);
        Assert.Equal(SkipReason.UnsupportedSignature, row.Reason);
        Assert.Contains("TestModule.WelcomeTip", row.Details);
    }

    [Fact]
    public void DeclinedOverloads_KeepOneRowEach()
    {
        var (moduleDecl, _, typeDatabase) = CreateSetup("TestModule", "Widget", "Gadget");
        var extMethods = CreateExtensionMethodDict("TestModule.Adoptable",
            CreateExtMethod("tag", "public func tag(_ x: Swift.Int32) -> Swift.Int32"),
            CreateExtMethod("tag", "public func tag(_ x: Swift.Int32) -> Swift.Int64"));

        var skipped = Inject(moduleDecl, extMethods, typeDatabase, new ModuleEmissionContext());

        Assert.Equal(2, skipped.Count(s => s.Name == "tag" && s.ContainingType == "TestModule.Adoptable"));
    }

    [Fact]
    public void MemberOneConformerCarries_IsReportedOnlyOnTheConformerThatDeclines()
    {
        var (moduleDecl, carrier, typeDatabase) = CreateSetup("TestModule", "Widget", "Gadget");
        // A second conformer the type database records as a protocol: its metatype cannot be named
        // in a wrapper, so it declines the member while the first conformer carries it.
        var decliner = CreateClassDecl("Sprocket", moduleDecl);
        decliner.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Sprocket"),
            SwiftTypeName.FromModuleQualifiedName("TestModule.Gadget"),
            ""));
        typeDatabase.RegisterCrossModuleType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Sprocket"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "ISprocket"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Sprocket"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol,
            });
        var extMethods = CreateExtensionMethodDict("TestModule.Gadget",
            CreateExtMethod("shutdown", "public func shutdown()"));

        var skipped = Inject(moduleDecl, extMethods, typeDatabase, new ModuleEmissionContext());

        Assert.Single(carrier.Methods);
        Assert.Empty(decliner.Methods);
        var row = Assert.Single(skipped);
        Assert.Equal("TestModule.Sprocket", row.ContainingType);
        Assert.Equal(SkipReason.UnsupportedType, row.Reason);
    }

    [Fact]
    public void CarriedMember_HasNoRow()
    {
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "Widget", "Gadget");
        var extMethods = CreateExtensionMethodDict("TestModule.Gadget",
            CreateExtMethod("shutdown", "public func shutdown()"));

        var skipped = Inject(moduleDecl, extMethods, typeDatabase, new ModuleEmissionContext());

        Assert.Single(conformingType.Methods);
        Assert.Empty(skipped);
    }

    [Fact]
    public void DefaultOfARequirement_HasNoRow()
    {
        // The requirement is the surface a consumer sees; its extension default is not a member of
        // its own, so no row even though nothing conforms.
        var (moduleDecl, _, typeDatabase) = CreateSetup("TestModule", "Widget", "Gadget");
        var protocol = TestDecls.Protocol("Adoptable", "TestModule",
            TestDecls.Method("shutdown", module: "TestModule"),
            TestDecls.Property("level", module: "TestModule"));
        moduleDecl.Protocols.Add(protocol);
        var child = TestDecls.Protocol("Refined", "TestModule");
        child.InheritedProtocols.Add(new NamedTypeSpec("TestModule.Adoptable"));
        moduleDecl.Protocols.Add(child);
        var extMethods = CreateExtensionMethodDict("TestModule.Refined",
            CreateExtMethod("shutdown", "public func shutdown()"),
            CreateExtProperty("level", "public var level: Swift.Int { get }"),
            CreateExtMethod("restart", "public func restart()"));

        var skipped = Inject(moduleDecl, extMethods, typeDatabase, new ModuleEmissionContext());

        var row = Assert.Single(skipped);
        Assert.Equal("restart", row.Name);
    }

    [Fact]
    public void Declines_ArePublishedIntoTheSessionThatSettles()
    {
        // Injection runs once, before containment starts the session the report is read from.
        var (moduleDecl, _, typeDatabase) = CreateSetup("TestModule", "Widget", "Gadget");
        var extMethods = CreateExtensionMethodDict("TestModule.Adoptable",
            CreateExtMethod("shutdown", "public func shutdown()"));
        var ctx = new ModuleEmissionContext();
        Inject(moduleDecl, extMethods, typeDatabase, ctx);

        ReportCollector.Start(moduleDecl);
        try
        {
            ProtocolExtensionEmitter.PublishDeclines(moduleDecl, ctx);
            ProtocolExtensionEmitter.PublishDeclines(moduleDecl, ctx);
            var row = Assert.Single(ReportCollector.Complete()!.SkippedItems);
            Assert.Equal("TestModule.Adoptable", row.ContainingType);
        }
        finally
        {
            ReportCollector.Reset();
        }
    }

    [Fact]
    public void MemberTheInterfaceReaderNeverExtracts_IsReportedOnTheProtocol()
    {
        // The reader extracts plain func/var members only: an initializer, a subscript or a member
        // with another modifier reaches injection as nothing, and is reported from the ABI's list.
        var (moduleDecl, _, typeDatabase) = CreateSetup("TestModule", "Widget", "Gadget");
        var protocol = TestDecls.Protocol("Adoptable", "TestModule",
            TestDecls.Method("restart", module: "TestModule"));
        var init = TestDecls.Method("init", isConstructor: true, module: "TestModule");
        var finalMethod = TestDecls.ExtensionDefault("seal", module: "TestModule");
        var extracted = TestDecls.ExtensionDefault("shutdown", module: "TestModule");
        var requirementDefault = TestDecls.ExtensionDefault("restart", module: "TestModule");
        var internalMethod = TestDecls.ExtensionDefault("hidden", module: "TestModule");
        internalMethod.IsModuleInternal = true;
        foreach (var member in new BaseDecl[] { init, finalMethod, extracted, requirementDefault, internalMethod })
        {
            member.ParentDecl = protocol;
            protocol.ExtensionMembers.Add(member);
        }
        moduleDecl.Protocols.Add(protocol);
        var extMethods = CreateExtensionMethodDict("TestModule.Adoptable",
            CreateExtMethod("shutdown", "public func shutdown()"));

        var ctx = new ModuleEmissionContext();
        var skipped = Inject(moduleDecl, extMethods, typeDatabase, ctx);

        var unextracted = skipped.Where(s => s.Details?.Contains("does not extract") == true).ToList();
        Assert.Equal(new[] { "init", "seal" }, unextracted.Select(s => s.Name).OrderBy(n => n, System.StringComparer.Ordinal));
        Assert.All(unextracted, s =>
        {
            Assert.Equal("TestModule.Adoptable", s.ContainingType);
            Assert.Equal(SkipReason.ProtocolExtensionUncarried, s.Reason);
        });
        // The extracted member keeps the one row injection gave it (no conformer here).
        Assert.Single(skipped, s => s.Name == "shutdown");
        Assert.DoesNotContain(skipped, s => s.Name is "restart" or "hidden");
        // Parked with the injection declines, so the settled session gets them too.
        Assert.Equal(3, ctx.ProtocolExtDeclines.Count);
    }

    [Fact]
    public void MemberTheInterfaceReaderNeverExtracts_IsReportedWhenTheModuleHasNoExtractedMembers()
    {
        var (moduleDecl, _, typeDatabase) = CreateSetup("TestModule", "Widget", "Gadget");
        var protocol = TestDecls.Protocol("Adoptable", "TestModule");
        var subscript = new SubscriptDecl
        {
            Name = "subscript",
            ReturnTypeSpec = new NamedTypeSpec("Swift.Int"),
            IndexParameters = new List<ArgumentDecl>(),
            IsStatic = false,
            Accessors = new List<AccessorDecl>(),
            MangledName = "$s10TestModule9AdoptablePAAEyS2icig",
            ParentDecl = protocol,
            ModuleDecl = moduleDecl,
            IsFromExtension = true,
        };
        protocol.ExtensionMembers.Add(subscript);
        moduleDecl.Protocols.Add(protocol);

        var skipped = Inject(moduleDecl, new Dictionary<string, List<ProtocolExtensionMethodDecl>>(), typeDatabase,
            new ModuleEmissionContext());

        var row = Assert.Single(skipped);
        Assert.Equal("TestModule.Adoptable", row.ContainingType);
        Assert.Equal(SkipReason.ProtocolExtensionUncarried, row.Reason);
    }

    private static ArgumentDecl Arg(string label, string type) => TestDecls.Param(label, new NamedTypeSpec(type));

    private static void AddExtensionMember(ProtocolDecl protocol, BaseDecl member)
    {
        member.ParentDecl = protocol;
        protocol.ExtensionMembers.Add(member);
    }

    [Fact]
    public void UnextractedOverload_IsReportedBesideAnExtractedOneOfTheSameName()
    {
        // The reader extracted tag(_: Int32); the ABI also has tag(_: Bool), which it did not.
        var (moduleDecl, _, typeDatabase) = CreateSetup("TestModule", "Widget", "Gadget");
        var protocol = TestDecls.Protocol("Adoptable", "TestModule");
        AddExtensionMember(protocol, TestDecls.ExtensionDefault("tag", parameters: new[] { Arg("_", "Swift.Int32") }, module: "TestModule")
            with { MangledName = "$s10TestModule9AdoptablePAAE3tagyys5Int32VF" });
        AddExtensionMember(protocol, TestDecls.ExtensionDefault("tag", parameters: new[] { Arg("_", "Swift.Bool") }, module: "TestModule")
            with { MangledName = "$s10TestModule9AdoptablePAAE3tagyySbBoolF" });
        moduleDecl.Protocols.Add(protocol);
        var extMethods = CreateExtensionMethodDict("TestModule.Adoptable",
            CreateExtMethod("tag", "public func tag(_ x: Swift.Int32)"));

        var skipped = Inject(moduleDecl, extMethods, typeDatabase, new ModuleEmissionContext());

        var row = Assert.Single(skipped, s => s.Details?.Contains("does not extract") == true);
        Assert.Equal("tag", row.Name);
        Assert.Contains("Bool", row.DeclId ?? string.Empty);
    }

    [Fact]
    public void ExtensionSubscript_IsReportedBesideARequirementSubscriptOfAnotherType()
    {
        var (moduleDecl, _, typeDatabase) = CreateSetup("TestModule", "Widget", "Gadget");
        SubscriptDecl Subscript(string type, bool fromExtension) => new()
        {
            Name = "subscript",
            ReturnTypeSpec = new NamedTypeSpec("Swift.Int"),
            IndexParameters = new List<ArgumentDecl> { Arg("_", type) },
            IsStatic = false,
            Accessors = new List<AccessorDecl>(),
            MangledName = $"$s10TestModule9AdoptableP{type}ig",
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            IsFromExtension = fromExtension,
            IsProtocolRequirement = !fromExtension,
        };
        var protocol = TestDecls.Protocol("Adoptable", "TestModule", Subscript("Swift.Int32", fromExtension: false));
        AddExtensionMember(protocol, Subscript("Swift.Bool", fromExtension: true));
        AddExtensionMember(protocol, Subscript("Swift.Int32", fromExtension: true));
        moduleDecl.Protocols.Add(protocol);

        var skipped = Inject(moduleDecl, new Dictionary<string, List<ProtocolExtensionMethodDecl>>(), typeDatabase,
            new ModuleEmissionContext());

        // The Int32 subscript is the requirement's default; the Bool one is a member of its own.
        var row = Assert.Single(skipped);
        Assert.Equal(SkipReason.ProtocolExtensionUncarried, row.Reason);
        Assert.Contains("Bool", row.DeclId ?? string.Empty);
    }

    [Fact]
    public void DefaultOfARequirementWhoseTypeMovedModules_HasNoRow()
    {
        // A type moved between modules (`@_originallyDefinedIn`) is named by its original module in
        // the ABI and by its current one in the interface: `remove(_: OldKit.Marker)` and
        // `remove(_ x: TestModule.Marker)` are one requirement and its default. The overload beside it
        // that takes another type is still its own member.
        var (moduleDecl, _, typeDatabase) = CreateSetup("TestModule", "Widget", "Gadget");
        moduleDecl.Protocols.Add(TestDecls.Protocol("Adoptable", "TestModule",
            TestDecls.Method("remove", parameters: new[] { Arg("_", "OldKit.Marker") }, module: "TestModule")));
        var extMethods = CreateExtensionMethodDict("TestModule.Adoptable",
            CreateExtMethod("remove", "public func remove(_ x: TestModule.Marker)"),
            CreateExtMethod("remove", "public func remove(_ x: Swift.Bool)"));

        var skipped = Inject(moduleDecl, extMethods, typeDatabase, new ModuleEmissionContext());

        var row = Assert.Single(skipped);
        Assert.Contains("Swift.Bool", row.DeclId ?? string.Empty);
    }

    [Fact]
    public void ExtensionOverloadSharingARequirementsLabels_IsNotTakenForItsDefault()
    {
        var (moduleDecl, _, typeDatabase) = CreateSetup("TestModule", "Widget", "Gadget");
        moduleDecl.Protocols.Add(TestDecls.Protocol("Adoptable", "TestModule",
            TestDecls.Method("tag", parameters: new[] { Arg("_", "Swift.Int32") }, module: "TestModule")));
        var extMethods = CreateExtensionMethodDict("TestModule.Adoptable",
            CreateExtMethod("tag", "public func tag(_ x: Swift.Bool)"),
            CreateExtMethod("tag", "public func tag(_ x: Swift.Int32)"));

        var skipped = Inject(moduleDecl, extMethods, typeDatabase, new ModuleEmissionContext());

        // tag(_: Int32) is the requirement's default; tag(_: Bool) is its own member, and nothing binds it.
        var row = Assert.Single(skipped);
        Assert.Equal("tag", row.Name);
        Assert.Contains("Swift.Bool", row.DeclId ?? string.Empty);
    }

    [Fact]
    public void DefaultOfARequirementWithAnEscapedName_HasNoRow()
    {
        // The ABI parser escapes a C# keyword (event becomes _event); the reader keeps the Swift name.
        var (moduleDecl, _, typeDatabase) = CreateSetup("TestModule", "Widget", "Gadget");
        var requirement = TestDecls.Method("_event", module: "TestModule");
        requirement.OriginalSwiftName = "event";
        moduleDecl.Protocols.Add(TestDecls.Protocol("Adoptable", "TestModule", requirement));
        var extMethods = CreateExtensionMethodDict("TestModule.Adoptable",
            CreateExtMethod("event", "public func event()"));

        var skipped = Inject(moduleDecl, extMethods, typeDatabase, new ModuleEmissionContext());

        Assert.Empty(skipped);
    }

    [Fact]
    public void OverloadWhoseWrapperSymbolAnotherOverloadTook_IsReportedOnTheConformer()
    {
        // Same name and parameters, different return type: both render the same wrapper symbol.
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "Widget", "Gadget");
        var extMethods = CreateExtensionMethodDict("TestModule.Gadget",
            CreateExtMethod("level", "public func level() -> Swift.Int32"),
            CreateExtMethod("level", "public func level() -> Swift.Int64"));

        var skipped = Inject(moduleDecl, extMethods, typeDatabase, new ModuleEmissionContext());

        Assert.Single(conformingType.Methods);
        var row = Assert.Single(skipped);
        Assert.Equal("level", row.Name);
        Assert.Equal(SkipReason.DuplicateSignature, row.Reason);
    }

    [Fact]
    public void FrozenStructConformer_IsNamedWhenItIsTheOnlyConformer()
    {
        var (moduleDecl, _, typeDatabase) = CreateSetup("TestModule", "Widget", "Unrelated");
        moduleDecl.Types.Add(FrozenConformer(moduleDecl, "Pin", "Gadget"));
        var extMethods = CreateExtensionMethodDict("TestModule.Gadget",
            CreateExtMethod("shutdown", "public func shutdown()"));

        var skipped = Inject(moduleDecl, extMethods, typeDatabase, new ModuleEmissionContext());

        var row = Assert.Single(skipped);
        Assert.Equal("TestModule.Gadget", row.ContainingType);
        Assert.Contains("TestModule.Pin is a frozen struct", row.Details);
        Assert.DoesNotContain("no type in this module conforms", row.Details);
    }

    [Fact]
    public void FrozenStructConformer_IsReportedWhenAClassConformerCarriesTheMember()
    {
        var (moduleDecl, carrier, typeDatabase) = CreateSetup("TestModule", "Widget", "Gadget");
        moduleDecl.Types.Add(FrozenConformer(moduleDecl, "Pin", "Gadget"));
        var extMethods = CreateExtensionMethodDict("TestModule.Gadget",
            CreateExtMethod("shutdown", "public func shutdown()"));

        var skipped = Inject(moduleDecl, extMethods, typeDatabase, new ModuleEmissionContext());

        Assert.Single(carrier.Methods);
        var row = Assert.Single(skipped);
        Assert.Equal("TestModule.Pin", row.ContainingType);
        Assert.Equal(SkipReason.ProtocolExtensionUncarried, row.Reason);
    }

    [Fact]
    public void SameMemberInTwoConstrainedExtensions_KeepsARowEach()
    {
        var (moduleDecl, _, typeDatabase) = CreateSetup("TestModule", "Widget", "Gadget");
        var first = CreateExtProperty("value", "public var value: Swift.Int32 { get }");
        first.WhereConstraints.Add("Self.Element == Swift.Int32");
        var second = CreateExtProperty("value", "public var value: Swift.Int32 { get }");
        second.WhereConstraints.Add("Self.Element == Swift.String");
        var extMethods = CreateExtensionMethodDict("TestModule.Gadget", first, second);

        var skipped = Inject(moduleDecl, extMethods, typeDatabase, new ModuleEmissionContext());

        Assert.Equal(2, skipped.Count(s => s.Name == "value"));
    }

    private static StructDecl FrozenConformer(ModuleDecl moduleDecl, string name, string protocolName)
    {
        var structDecl = new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s10{moduleDecl.Name}{name.Length}{name}V",
            IsFrozen = true,
            Conformances = new List<TypeConformance>(),
            MetadataAccessor = "",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
        };
        structDecl.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{protocolName}"),
            ""));
        return structDecl;
    }

    private static MethodDecl AbiExtensionMethod(string name, string mangled, TypeSpec? returnType = null, params ArgumentDecl[] parameters)
        => TestDecls.ExtensionDefault(name, parameters: parameters, returnType: returnType, module: "TestModule") with { MangledName = mangled };

    private static void AddConformance(TypeDecl type, string protocolQualifiedName)
    {
        var conformance = new TypeConformance(type.SwiftTypeName, SwiftTypeName.FromModuleQualifiedName(protocolQualifiedName), "");
        switch (type)
        {
            case ClassDecl c: c.Conformances.Add(conformance); break;
            case StructDecl st: st.Conformances.Add(conformance); break;
        }
    }

    [Fact]
    public void SignatureWhoseDefaultValueHoldsAParenthesis_IsReportedWithoutFailing()
    {
        var (moduleDecl, _, typeDatabase) = CreateSetup("TestModule", "Widget", "Gadget");
        var member = CreateExtMethod("mark", "public static func mark(_ value: Swift.String = \"(\")");
        member.IsStatic = true;
        moduleDecl.Protocols.Add(TestDecls.Protocol("Gadget", "TestModule", TestDecls.Method("mark", module: "TestModule")));

        var skipped = Inject(moduleDecl, CreateExtensionMethodDict("TestModule.Gadget", member), typeDatabase,
            new ModuleEmissionContext());

        Assert.Single(skipped, s => s.Name == "mark");
    }

    [Fact]
    public void UnextractedOverloadDifferingOnlyInReturnType_IsReported()
    {
        var (moduleDecl, _, typeDatabase) = CreateSetup("TestModule", "Widget", "Gadget");
        var protocol = TestDecls.Protocol("Adoptable", "TestModule");
        AddExtensionMember(protocol, AbiExtensionMethod("level", "$s10TestModule9AdoptablePAAE5levels5Int32VyF", new NamedTypeSpec("Swift.Int32")));
        AddExtensionMember(protocol, AbiExtensionMethod("level", "$s10TestModule9AdoptablePAAE5levels5Int64VyF", new NamedTypeSpec("Swift.Int64")));
        moduleDecl.Protocols.Add(protocol);
        var extMethods = CreateExtensionMethodDict("TestModule.Adoptable",
            CreateExtMethod("level", "public func level() -> Swift.Int32"));

        var skipped = Inject(moduleDecl, extMethods, typeDatabase, new ModuleEmissionContext());

        var row = Assert.Single(skipped, s => s.Details?.Contains("does not extract") == true);
        Assert.Contains("Int64", row.DeclId ?? string.Empty);
    }

    [Fact]
    public void AsyncExtensionOverloadOfASyncRequirement_IsNotTakenForItsDefault()
    {
        var (moduleDecl, _, typeDatabase) = CreateSetup("TestModule", "Widget", "Gadget");
        moduleDecl.Protocols.Add(TestDecls.Protocol("Adoptable", "TestModule", TestDecls.Method("refresh", module: "TestModule")));
        var extMethods = CreateExtensionMethodDict("TestModule.Adoptable",
            CreateExtMethod("refresh", "public func refresh() async"));

        var skipped = Inject(moduleDecl, extMethods, typeDatabase, new ModuleEmissionContext());

        var row = Assert.Single(skipped);
        Assert.Equal("refresh", row.Name);
        Assert.Contains("async", row.Details);
    }

    [Fact]
    public void FrozenStructConformingToASameNamedProtocolOfAnotherModule_HasNoRow()
    {
        var (moduleDecl, carrier, typeDatabase) = CreateSetup("TestModule", "Widget", "Gadget");
        var pin = FrozenConformer(moduleDecl, "Pin", "Unrelated");
        pin.Conformances.Clear();
        AddConformance(pin, "OtherModule.Gadget");
        moduleDecl.Types.Add(pin);
        var extMethods = CreateExtensionMethodDict("TestModule.Gadget",
            CreateExtMethod("shutdown", "public func shutdown()"));

        var skipped = Inject(moduleDecl, extMethods, typeDatabase, new ModuleEmissionContext());

        Assert.Single(carrier.Methods);
        Assert.Empty(skipped);
    }

    [Fact]
    public void ConformerLosingTheSameMemberOfTwoProtocols_KeepsARowForEach()
    {
        // Each protocol has its own class carrier; the frozen struct conforms to both.
        var (moduleDecl, carrier, typeDatabase) = CreateSetup("TestModule", "Widget", "Gadget");
        var secondCarrier = CreateClassDecl("Gizmo", moduleDecl);
        AddConformance(secondCarrier, "TestModule.Resettable");
        typeDatabase.RegisterCrossModuleType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Gizmo"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Gizmo"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Gizmo"),
                MetadataAccessor = "$s10TestModule5GizmoCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class,
            });
        typeDatabase.RegisterCrossModuleType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Resettable"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "IResettable"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Resettable"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol,
            });
        var pin = FrozenConformer(moduleDecl, "Pin", "Gadget");
        AddConformance(pin, "TestModule.Resettable");
        moduleDecl.Types.Add(pin);
        var fromGadget = CreateExtMethod("reset", "public func reset()");
        var fromResettable = CreateExtMethod("reset", "public func reset()");
        fromResettable.ProtocolQualifiedName = "TestModule.Resettable";
        var extMethods = CreateExtensionMethodDict("TestModule.Gadget", fromGadget);
        extMethods["TestModule.Resettable"] = new List<ProtocolExtensionMethodDecl> { fromResettable };

        var skipped = Inject(moduleDecl, extMethods, typeDatabase, new ModuleEmissionContext());

        Assert.Single(carrier.Methods);
        Assert.Single(secondCarrier.Methods);
        Assert.Equal(2, skipped.Count(s => s.Name == "reset" && s.ContainingType == "TestModule.Pin"));
    }

    [Fact]
    public void BoundMemberWithAKeywordLabel_HasNoRow()
    {
        // The ABI parser stores the label `default` as `_default`; the reader keeps `default`.
        var (moduleDecl, carrier, typeDatabase) = CreateSetup("TestModule", "Widget", "Gadget");
        var protocol = TestDecls.Protocol("Gadget", "TestModule");
        var label = Arg("_default", "Swift.Int32");
        label.OriginalSwiftName = "default";
        AddExtensionMember(protocol, AbiExtensionMethod("apply", "$s10TestModule6GadgetPAAE5apply7defaultys5Int32V_tF", null, label));
        moduleDecl.Protocols.Add(protocol);
        var extMethods = CreateExtensionMethodDict("TestModule.Gadget",
            CreateExtMethod("apply", "public func apply(default value: Swift.Int32)"));

        var skipped = Inject(moduleDecl, extMethods, typeDatabase, new ModuleEmissionContext());

        Assert.Single(carrier.Methods);
        Assert.Empty(skipped);
    }

    [Fact]
    public void UnqualifiedReceiver_FindsItsFrozenConformer()
    {
        var (moduleDecl, _, typeDatabase) = CreateSetup("TestModule", "Widget", "Unrelated");
        moduleDecl.Types.Add(FrozenConformer(moduleDecl, "Pin", "Gadget"));
        var extMethods = CreateExtensionMethodDict("Gadget", CreateExtMethod("shutdown", "public func shutdown()"));

        var skipped = Inject(moduleDecl, extMethods, typeDatabase, new ModuleEmissionContext());

        var row = Assert.Single(skipped);
        Assert.Contains("TestModule.Pin is a frozen struct", row.Details);
    }

    [Fact]
    public void MemberExtractedUnderAnUnqualifiedReceiver_KeepsItsOneRow()
    {
        var (moduleDecl, _, typeDatabase) = CreateSetup("TestModule", "Widget", "Gadget");
        var protocol = TestDecls.Protocol("Adoptable", "TestModule");
        AddExtensionMember(protocol, AbiExtensionMethod("shutdown", "$s10TestModule9AdoptablePAAE8shutdownyyF"));
        moduleDecl.Protocols.Add(protocol);
        var extMethods = CreateExtensionMethodDict("Adoptable", CreateExtMethod("shutdown", "public func shutdown()"));

        var skipped = Inject(moduleDecl, extMethods, typeDatabase, new ModuleEmissionContext());

        var row = Assert.Single(skipped);
        Assert.DoesNotContain("does not extract", row.Details);
    }

    [Fact]
    public void SyncDefaultOfAnAsyncRequirement_HasNoRow()
    {
        // A sync member witnesses an async requirement, so it is that requirement's default.
        var (moduleDecl, _, typeDatabase) = CreateSetup("TestModule", "Widget", "Gadget");
        moduleDecl.Protocols.Add(TestDecls.Protocol("Adoptable", "TestModule",
            TestDecls.Method("refresh", isAsync: true, module: "TestModule")));
        var extMethods = CreateExtensionMethodDict("TestModule.Adoptable",
            CreateExtMethod("refresh", "public func refresh()"));

        var skipped = Inject(moduleDecl, extMethods, typeDatabase, new ModuleEmissionContext());

        Assert.Empty(skipped);
    }

    [Fact]
    public void StaticExtensionMethodBesideAnInstanceRequirement_IsReported()
    {
        var (moduleDecl, _, typeDatabase) = CreateSetup("TestModule", "Widget", "Gadget");
        moduleDecl.Protocols.Add(TestDecls.Protocol("Adoptable", "TestModule", TestDecls.Method("reset", module: "TestModule")));
        var member = CreateExtMethod("reset", "public static func reset()");
        member.IsStatic = true;

        var skipped = Inject(moduleDecl, CreateExtensionMethodDict("TestModule.Adoptable", member), typeDatabase,
            new ModuleEmissionContext());

        var row = Assert.Single(skipped);
        Assert.Equal("reset", row.Name);
    }

    [Fact]
    public void ExtensionSubscriptDifferingOnlyInReturnType_IsReported()
    {
        var (moduleDecl, _, typeDatabase) = CreateSetup("TestModule", "Widget", "Gadget");
        SubscriptDecl Subscript(string returnType, bool fromExtension) => new()
        {
            Name = "subscript",
            ReturnTypeSpec = new NamedTypeSpec(returnType),
            IndexParameters = new List<ArgumentDecl> { Arg("_", "Swift.Int32") },
            IsStatic = false,
            Accessors = new List<AccessorDecl>(),
            MangledName = $"$s10TestModule9AdoptableP{returnType}ig",
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            IsFromExtension = fromExtension,
            IsProtocolRequirement = !fromExtension,
        };
        var protocol = TestDecls.Protocol("Adoptable", "TestModule", Subscript("Swift.Int", fromExtension: false));
        AddExtensionMember(protocol, Subscript("Swift.Int64", fromExtension: true));
        moduleDecl.Protocols.Add(protocol);

        var skipped = Inject(moduleDecl, new Dictionary<string, List<ProtocolExtensionMethodDecl>>(), typeDatabase,
            new ModuleEmissionContext());

        var row = Assert.Single(skipped);
        Assert.Contains("Int64", row.DeclId ?? string.Empty);
    }

    [Fact]
    public void StaticAndInstancePropertyOfOneName_KeepARowEach()
    {
        var (moduleDecl, _, typeDatabase) = CreateSetup("TestModule", "Widget", "Gadget");
        var instance = CreateExtProperty("value", "public var value: Swift.Int32 { get }");
        var shared = CreateExtProperty("value", "public static var value: Swift.Int32 { get }");
        shared.IsStatic = true;

        var skipped = Inject(moduleDecl, CreateExtensionMethodDict("TestModule.Adoptable", instance, shared), typeDatabase,
            new ModuleEmissionContext());

        Assert.Equal(2, skipped.Count(s => s.Name == "value"));
    }

    [Fact]
    public void MemberOfAProtocolNestedInAType_IsReported()
    {
        // extension S.Audio { public var addToLibrary: some P { … } } with Audio declared inside S.
        var (moduleDecl, _, typeDatabase) = CreateSetup("TestModule", "Widget", "Gadget");
        var schema = FrozenConformer(moduleDecl, "Schema", "Unrelated");
        schema.Conformances.Clear();
        var audio = TestDecls.Protocol("Audio", "TestModule");
        audio.SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Schema.Audio");
        audio.ParentDecl = schema;
        var property = TestDecls.ExtensionDefaultProperty("addToLibrary", module: "TestModule");
        AddExtensionMember(audio, property);
        schema.Types.Add(audio);
        moduleDecl.Types.Add(schema);

        var skipped = Inject(moduleDecl, new Dictionary<string, List<ProtocolExtensionMethodDecl>>(), typeDatabase,
            new ModuleEmissionContext());

        var row = Assert.Single(skipped);
        Assert.Equal("addToLibrary", row.Name);
        Assert.Equal(SkipReason.ProtocolExtensionUncarried, row.Reason);
    }
    [Fact]
    public void NonisolatedMemberTheReaderExtracted_HasOneRow()
    {
        // The interface prints `nonisolated public func`, a shape the reader extracts.
        var (moduleDecl, _, typeDatabase) = CreateSetup("TestModule", "Widget", "Gadget");
        var protocol = TestDecls.Protocol("Adoptable", "TestModule");
        var member = AbiExtensionMethod("accept", "$s10TestModule9AdoptablePAAE6accept10connectionSbSo15NSXPCConnectionC_tF",
            new NamedTypeSpec("Swift.Bool"), Arg("connection", "Foundation.NSXPCConnection"));
        member.IsNonisolated = true;
        AddExtensionMember(protocol, member);
        moduleDecl.Protocols.Add(protocol);
        var extMethods = CreateExtensionMethodDict("TestModule.Adoptable",
            CreateExtMethod("accept", "nonisolated public func accept(connection: Foundation.NSXPCConnection) -> Swift.Bool"));

        var skipped = Inject(moduleDecl, extMethods, typeDatabase, new ModuleEmissionContext());

        Assert.Single(skipped, s => s.Name == "accept");
    }

    [Fact]
    public void MemberOfAProtocolRefiningAnUninspectableForeignProtocol_HasNoRow()
    {
        // The parent's requirements are not visible, so the member may be the default of one of them.
        var (moduleDecl, _, typeDatabase) = CreateSetup("TestModule", "Widget", "Gadget");
        var protocol = TestDecls.Protocol("Configuration", "TestModule");
        protocol.InheritedProtocols.Add(new NamedTypeSpec("OtherModule.AppConfiguration"));
        protocol.InheritedProtocols.Add(new NamedTypeSpec("Swift.Sendable"));
        AddExtensionMember(protocol, AbiExtensionMethod("accept", "$s10TestModule13ConfigurationPAAE6acceptSbyF", new NamedTypeSpec("Swift.Bool")));
        AddExtensionMember(protocol, TestDecls.Method("init", isConstructor: true, module: "TestModule"));
        moduleDecl.Protocols.Add(protocol);
        var extMethods = CreateExtensionMethodDict("TestModule.Configuration",
            CreateExtMethod("accept", "public func accept() -> Swift.Bool"));

        var skipped = Inject(moduleDecl, extMethods, typeDatabase, new ModuleEmissionContext());

        Assert.Empty(skipped);
    }

    [Fact]
    public void DefaultOfAForeignRequirementReadFromTheSdk_HasNoRow_AndItsSiblingIsReported()
    {
        // protocol KitchenConfiguration: AppExtensionConfiguration, whose interface declares
        // `accept(connection:)`. The extension's `accept(connection:)` supplies that requirement's default,
        // so the requirement is the surface; `describe()` is no requirement anywhere and has no C# home.
        // With the requirements unread, both used to go without a row.
        var (moduleDecl, _, typeDatabase) = CreateSetup("TestModule", "Widget", "Gadget");
        var protocol = TestDecls.Protocol("KitchenConfiguration", "TestModule");
        protocol.InheritedProtocols.Add(new NamedTypeSpec("ExtensionFoundation.AppExtensionConfiguration"));
        AddExtensionMember(protocol, AbiExtensionMethod("accept", "$s10TestModule20KitchenConfigurationPAAE6accept10connectionSbSo15NSXPCConnectionC_tF",
            new NamedTypeSpec("Swift.Bool"), Arg("connection", "Foundation.NSXPCConnection")));
        AddExtensionMember(protocol, AbiExtensionMethod("describe", "$s10TestModule20KitchenConfigurationPAAE8describeSSyF",
            new NamedTypeSpec("Swift.String")));
        moduleDecl.Protocols.Add(protocol);
        var extMethods = CreateExtensionMethodDict("TestModule.KitchenConfiguration",
            CreateExtMethod("accept", "nonisolated public func accept(connection: Foundation.NSXPCConnection) -> Swift.Bool"),
            CreateExtMethod("describe", "public func describe() -> Swift.String"));

        var skipped = Inject(moduleDecl, extMethods, typeDatabase, new ModuleEmissionContext());

        Assert.DoesNotContain(skipped, s => s.Name == "accept");
        Assert.Single(skipped, s => s.Name == "describe");
    }

    [Theory]
    [InlineData("Swift.Hashable", "hash", "public func hash(into hasher: inout Swift.Hasher)")] // inherited: Hashable : Equatable
    [InlineData("Swift.Hashable", "==", "public static func == (lhs: Self, rhs: Self) -> Swift.Bool")]
    [InlineData("Swift.Codable", "init", "public init(from decoder: any Swift.Decoder) throws")] // a typealias composition: Decodable & Encodable
    [InlineData("Swift.Codable", "encode", "public func encode(to encoder: any Swift.Encoder) throws")]
    [InlineData("Identifiable", "id", null)]            // unqualified: looked up in the standard library
    [InlineData("Swift::Identifiable", "id", null)]     // a module selector
    public void DefaultOfAStandardLibraryRequirement_HasNoRow(string parent, string requirement, string? signature)
    {
        var (moduleDecl, _, typeDatabase) = CreateSetup("TestModule", "Widget", "Gadget");
        var protocol = TestDecls.Protocol("Keyed", "TestModule");
        protocol.InheritedProtocols.Add(new NamedTypeSpec(parent));
        moduleDecl.Protocols.Add(protocol);
        var extMethods = CreateExtensionMethodDict("TestModule.Keyed",
            signature is null
                ? CreateExtProperty(requirement, $"public var {requirement}: Swift.Int {{ get }}")
                : Static(CreateExtMethod(requirement, signature)),
            CreateExtMethod("relabel", "public func relabel()"));

        var skipped = Inject(moduleDecl, extMethods, typeDatabase, new ModuleEmissionContext());

        var row = Assert.Single(skipped);
        Assert.Equal("relabel", row.Name);
    }

    private static ProtocolExtensionMethodDecl Static(ProtocolExtensionMethodDecl member)
    {
        member.IsStatic = member.RawSignature.Contains("static ", StringComparison.Ordinal);
        return member;
    }

    [Theory]
    [InlineData("public static func hash(into hasher: inout Swift.Hasher)")]  // a static method beside an instance requirement
    [InlineData("public func encode(to encoder: any Swift.Encoder) async")]   // an async method beside a sync requirement
    public void MemberOfAForeignRequirementsNameAndLabelsWithOtherEffects_IsReported(string signature)
    {
        var (moduleDecl, _, typeDatabase) = CreateSetup("TestModule", "Widget", "Gadget");
        var protocol = TestDecls.Protocol("Keyed", "TestModule");
        protocol.InheritedProtocols.Add(new NamedTypeSpec("Swift.Hashable"));
        protocol.InheritedProtocols.Add(new NamedTypeSpec("Swift.Codable"));
        moduleDecl.Protocols.Add(protocol);
        var start = signature.IndexOf("func ", StringComparison.Ordinal) + "func ".Length;
        var name = signature[start..signature.IndexOf('(')];
        var extMethods = CreateExtensionMethodDict("TestModule.Keyed", Static(CreateExtMethod(name, signature)));

        var skipped = Inject(moduleDecl, extMethods, typeDatabase, new ModuleEmissionContext());

        Assert.Single(skipped, s => s.Name == name);
    }

    [Fact]
    public void ExtensionSubscriptOfAProtocolRefiningAForeignProtocolWithSubscripts_IsReported()
    {
        // Collection declares subscripts; an extension subscript is not taken for any of them.
        using var sdk = FakeSdkInterfaces.Create(("Swift", false, FakeSdkInterfaces.Swift + """

            public protocol Collection {
              subscript(position: Swift.Int) -> Swift.Int { get }
            }
            """));
        var (moduleDecl, _, typeDatabase) = CreateSetup("TestModule", "Widget", "Gadget");
        var protocol = TestDecls.Protocol("Shelf", "TestModule");
        protocol.InheritedProtocols.Add(new NamedTypeSpec("Swift.Collection"));
        AddExtensionMember(protocol, new SubscriptDecl
        {
            Name = "subscript",
            ReturnTypeSpec = new NamedTypeSpec("Swift.Int"),
            IndexParameters = new List<ArgumentDecl> { Arg("_", "Swift.Int") },
            IsStatic = false,
            Accessors = new List<AccessorDecl>(),
            MangledName = "$s10TestModule5ShelfPAAEyS2icig",
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            IsFromExtension = true,
            IsProtocolRequirement = false,
        });
        moduleDecl.Protocols.Add(protocol);

        var skipped = Inject(moduleDecl, new Dictionary<string, List<ProtocolExtensionMethodDecl>>(), typeDatabase,
            new ModuleEmissionContext(), installStandardSdk: false);

        Assert.Single(skipped, s => s.Kind == BindingItemKind.Subscript);
    }

    [Theory]
    [InlineData("public func hash(seed: Swift.Int)")]                 // a method of a requirement's name, other labels
    [InlineData("public func encode(into encoder: any Swift.Encoder)")]
    public void OverloadOfAForeignRequirementsNameWithOtherLabels_IsReported(string signature)
    {
        var (moduleDecl, _, typeDatabase) = CreateSetup("TestModule", "Widget", "Gadget");
        var protocol = TestDecls.Protocol("Keyed", "TestModule");
        protocol.InheritedProtocols.Add(new NamedTypeSpec("Swift.Hashable"));
        protocol.InheritedProtocols.Add(new NamedTypeSpec("Swift.Codable"));
        moduleDecl.Protocols.Add(protocol);
        var name = signature["public func ".Length..signature.IndexOf('(')];
        var extMethods = CreateExtensionMethodDict("TestModule.Keyed", CreateExtMethod(name, signature));

        var skipped = Inject(moduleDecl, extMethods, typeDatabase, new ModuleEmissionContext());

        Assert.Single(skipped, s => s.Name == name);
    }

    [Fact]
    public void MethodOfAForeignRequirementPropertysName_IsReported()
    {
        // `id` is a property requirement of Identifiable; a method `id()` supplies nothing.
        var (moduleDecl, _, typeDatabase) = CreateSetup("TestModule", "Widget", "Gadget");
        var protocol = TestDecls.Protocol("Keyed", "TestModule");
        protocol.InheritedProtocols.Add(new NamedTypeSpec("Swift.Identifiable"));
        moduleDecl.Protocols.Add(protocol);
        var extMethods = CreateExtensionMethodDict("TestModule.Keyed", CreateExtMethod("id", "public func id() -> Swift.Int"));

        var skipped = Inject(moduleDecl, extMethods, typeDatabase, new ModuleEmissionContext());

        Assert.Single(skipped, s => s.Name == "id");
    }

    [Fact]
    public void UnextractedOverloadOfAForeignRequirementsName_IsReported()
    {
        // The ABI holds `accept(connection:)`, the requirement's default, and `accept(other:)`, which the
        // reader did not extract; only the second is an addition with no C# home.
        var (moduleDecl, _, typeDatabase) = CreateSetup("TestModule", "Widget", "Gadget");
        var protocol = TestDecls.Protocol("KitchenConfiguration", "TestModule");
        protocol.InheritedProtocols.Add(new NamedTypeSpec("ExtensionFoundation.AppExtensionConfiguration"));
        AddExtensionMember(protocol, AbiExtensionMethod("accept", "$s10TestModule20KitchenConfigurationPAAE6accept10connectionSbSo15NSXPCConnectionC_tF",
            new NamedTypeSpec("Swift.Bool"), Arg("connection", "Foundation.NSXPCConnection")));
        AddExtensionMember(protocol, AbiExtensionMethod("accept", "$s10TestModule20KitchenConfigurationPAAE6accept5otherSbSi_tF",
            new NamedTypeSpec("Swift.Bool"), Arg("other", "Swift.Int")));
        moduleDecl.Protocols.Add(protocol);
        var extMethods = CreateExtensionMethodDict("TestModule.KitchenConfiguration",
            CreateExtMethod("accept", "nonisolated public func accept(connection: Foundation.NSXPCConnection) -> Swift.Bool"));

        var skipped = Inject(moduleDecl, extMethods, typeDatabase, new ModuleEmissionContext());

        var row = Assert.Single(skipped);
        Assert.Contains("other", row.DeclId ?? string.Empty);
    }

    [Fact]
    public void DefaultOfARequirementOfAProtocolAModuleReexportsPublicly_HasNoRow()
    {
        // Xcode 27.2 prints a re-export as `@_exported public import M`; the protocol is read from M.
        using var sdk = FakeSdkInterfaces.Create(
            ("Swift", false, FakeSdkInterfaces.Swift),
            ("Umbrella", true, "@_exported public import ExtensionFoundation\nimport Swift"),
            ("ExtensionFoundation", true, FakeSdkInterfaces.ExtensionFoundation));
        var (moduleDecl, _, typeDatabase) = CreateSetup("TestModule", "Widget", "Gadget");
        var protocol = TestDecls.Protocol("KitchenConfiguration", "TestModule");
        protocol.InheritedProtocols.Add(new NamedTypeSpec("Umbrella.AppExtensionConfiguration"));
        moduleDecl.Protocols.Add(protocol);
        var extMethods = CreateExtensionMethodDict("TestModule.KitchenConfiguration",
            CreateExtMethod("accept", "nonisolated public func accept(connection: Foundation.NSXPCConnection) -> Swift.Bool"),
            CreateExtMethod("describe", "public func describe() -> Swift.String"));

        var skipped = Inject(moduleDecl, extMethods, typeDatabase, new ModuleEmissionContext(), installStandardSdk: false);

        var row = Assert.Single(skipped);
        Assert.Equal("describe", row.Name);
    }

    [Fact]
    public void SelectiveReexportOfAnotherName_IsNotFollowedForTheProtocol()
    {
        // `@_exported import protocol Decoy.Other` brings in `Other` alone; Decoy's own
        // AppExtensionConfiguration (no requirements) must not stand in for the real one.
        using var sdk = FakeSdkInterfaces.Create(
            ("Swift", false, FakeSdkInterfaces.Swift),
            ("Umbrella", true, "@_exported import protocol Decoy.Other\n@_exported public import ExtensionFoundation"),
            ("Decoy", true, "public protocol Other {\n}\npublic protocol AppExtensionConfiguration {\n}"),
            ("ExtensionFoundation", true, FakeSdkInterfaces.ExtensionFoundation));
        var (moduleDecl, _, typeDatabase) = CreateSetup("TestModule", "Widget", "Gadget");
        var protocol = TestDecls.Protocol("KitchenConfiguration", "TestModule");
        protocol.InheritedProtocols.Add(new NamedTypeSpec("Umbrella.AppExtensionConfiguration"));
        moduleDecl.Protocols.Add(protocol);
        var extMethods = CreateExtensionMethodDict("TestModule.KitchenConfiguration",
            CreateExtMethod("accept", "nonisolated public func accept(connection: Foundation.NSXPCConnection) -> Swift.Bool"));

        var skipped = Inject(moduleDecl, extMethods, typeDatabase, new ModuleEmissionContext(), installStandardSdk: false);

        Assert.Empty(skipped);
    }

    [Theory]
    [InlineData("take(_:Kit.OuterA.Token) -> ()", "take(_:Kit.OuterB.Token) -> ()", false)] // nested types of different parents
    [InlineData("remove(_:OldKit.Marker) -> ()", "remove(_:NewKit.Marker) -> ()", true)]    // a type moved between modules
    [InlineData("map(_:Swift.Array<OldKit.Outer.Item>) -> ()", "map(_:Swift.Array<NewKit.Outer.Item>) -> ()", true)]
    public void SignatureKeysCompareWithoutTheirModules(string left, string right, bool equal)
        => Assert.Equal(equal,
            ProtocolExtensionEmitter.WithoutModuleQualifiers(left) == ProtocolExtensionEmitter.WithoutModuleQualifiers(right));

    [Fact]
    public void MemberOfAProtocolRefiningOnlyAMarkerProtocol_IsStillReported()
    {
        var (moduleDecl, _, typeDatabase) = CreateSetup("TestModule", "Widget", "Gadget");
        var protocol = TestDecls.Protocol("Configuration", "TestModule");
        protocol.InheritedProtocols.Add(new NamedTypeSpec("Swift.Sendable"));
        moduleDecl.Protocols.Add(protocol);
        var extMethods = CreateExtensionMethodDict("TestModule.Configuration",
            CreateExtMethod("accept", "public func accept() -> Swift.Bool"));

        var skipped = Inject(moduleDecl, extMethods, typeDatabase, new ModuleEmissionContext());

        Assert.Single(skipped, s => s.Name == "accept");
    }

    [Theory]
    [InlineData("Swift.CustomStringConvertible", "description", "Swift.String")]
    [InlineData("Foundation.CustomLocalizedStringResourceConvertible", "localizedStringResource", "Foundation.LocalizedStringResource")]
    public void MemberOfAProtocolRefiningAKnownForeignProtocol_IsReportedUnlessItIsThatProtocolsRequirement(
        string parent, string requirement, string requirementType)
    {
        var (moduleDecl, _, typeDatabase) = CreateSetup("TestModule", "Widget", "Gadget");
        var protocol = TestDecls.Protocol("Labeled", "TestModule");
        protocol.InheritedProtocols.Add(new NamedTypeSpec(parent));
        moduleDecl.Protocols.Add(protocol);
        var extMethods = CreateExtensionMethodDict("TestModule.Labeled",
            CreateExtProperty(requirement, $"public var {requirement}: {requirementType} {{ get }}"),
            CreateExtMethod("relabel", "public func relabel()"));

        var skipped = Inject(moduleDecl, extMethods, typeDatabase, new ModuleEmissionContext());

        var row = Assert.Single(skipped);
        Assert.Equal("relabel", row.Name);
    }
}
