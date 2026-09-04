// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BindingsGeneration.Demangling;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Parser coverage for the reference-ownership qualifier on a stored property —
/// <c>strong</c> (the default) versus the non-retaining <c>weak</c> / <c>unowned</c> /
/// <c>unowned(unsafe)</c> flavours.
///
/// <para>
/// The fact is load-bearing for marshalling: a non-retaining sink stores a value without
/// taking a strong reference, so nothing on the Swift side keeps a bridged conformer alive
/// once the setter returns. Mis-reading such a property as strong loses the managed rooting
/// the value needs, and the symptom is a delegate that silently stops firing.
/// </para>
///
/// <para>
/// Both ABI producers the repo consumes — <c>swift-frontend
/// -emit-abi-descriptor-path</c> (the BindingTests fixture producer) and
/// <c>swift-api-digester -dump-sdk</c> (the Apple-framework producer) — were dumped for the
/// same module and agree byte-for-byte on the encoding these tests pin: the raw integer in
/// <c>ownership</c> plus a <c>ReferenceOwnership</c> entry in <c>declAttributes</c>, with a
/// strong property emitting neither.
/// </para>
/// </summary>
public class ReferenceOwnershipParserTests
{
    // The two producers' spelling of a non-retaining property: the integer AND the attribute.
    // `weak var delegate: (any P)?` in a real framework dump reads exactly this way.
    [Theory]
    [InlineData(1, SwiftReferenceOwnership.Weak)]
    [InlineData(2, SwiftReferenceOwnership.Unowned)]
    [InlineData(3, SwiftReferenceOwnership.Unmanaged)]
    public void ParseModule_OwnershipIntegerWithAttribute_ReadsQualifier(
        int ownership, SwiftReferenceOwnership expected)
    {
        var property = ParseSingleProperty(
            ownership: ownership,
            declAttributes: ["HasStorage", "AccessControl", "ReferenceOwnership"]);

        Assert.Equal(expected, property.ReferenceOwnership);
    }

    [Fact]
    public void ParseModule_NoOwnershipKey_IsStrong()
    {
        // A plain `var delegate: (any P)?` — the producers emit no `ownership` key and no
        // ReferenceOwnership attribute at all. This is the overwhelmingly common shape, so
        // it must stay strong rather than degrade into a non-retaining reading.
        var property = ParseSingleProperty(
            ownership: null,
            declAttributes: ["HasInitialValue", "HasStorage", "AccessControl"]);

        Assert.Equal(SwiftReferenceOwnership.Strong, property.ReferenceOwnership);
    }

    [Fact]
    public void ParseModule_ExplicitZeroOwnership_IsStrong()
    {
        // Zero is the enum's strong encoding. A producer that starts emitting the key
        // unconditionally must not be read as non-retaining.
        var property = ParseSingleProperty(
            ownership: 0,
            declAttributes: ["HasStorage", "AccessControl"]);

        Assert.Equal(SwiftReferenceOwnership.Strong, property.ReferenceOwnership);
    }

    [Fact]
    public void ParseModule_OwnershipAttributeWithoutInteger_IsNonRetaining()
    {
        // Defensive arm: the attribute alone still says the storage does not retain. Reading
        // it as strong would drop the rooting a non-retaining sink needs, so the parser falls
        // back to the checked-unowned reading rather than to strong.
        var property = ParseSingleProperty(
            ownership: null,
            declAttributes: ["HasStorage", "AccessControl", "ReferenceOwnership"]);

        Assert.NotEqual(SwiftReferenceOwnership.Strong, property.ReferenceOwnership);
    }

    [Fact]
    public void ParseModule_UnrecognizedOwnershipValue_IsNonRetaining()
    {
        // Zero is the only strong encoding, so any value the Swift enum could grow is some
        // non-retaining flavour. Fail toward "needs rooting", never toward "strong".
        var property = ParseSingleProperty(
            ownership: 99,
            declAttributes: ["HasStorage", "AccessControl", "ReferenceOwnership"]);

        Assert.NotEqual(SwiftReferenceOwnership.Strong, property.ReferenceOwnership);
    }

    [Fact]
    public void ParseModule_NonRetainingProperty_KeepsItsOtherFacts()
    {
        // The ownership read must be additive: name and type still parse the same way, so a
        // weak property is not otherwise degraded relative to its strong sibling.
        var property = ParseSingleProperty(
            ownership: 1,
            declAttributes: ["HasStorage", "AccessControl", "ReferenceOwnership"]);

        Assert.Equal("delegate", property.Name);
        var typeSpec = Assert.IsType<NamedTypeSpec>(property.SwiftTypeSpec);
        Assert.Equal("TestModule.SomeDelegate", typeSpec.Name);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ParseModule_NonRetainingProperty_PropagatesOwnershipToAccessors(int ownership)
    {
        // Marshalling arms are handed the accessor method, not the property that owns it, so a
        // setter can only learn that its value lands in non-retaining storage if the fact is
        // mirrored onto the accessor at parse time.
        var property = ParseSingleProperty(
            ownership: ownership,
            declAttributes: ["HasStorage", "AccessControl", "ReferenceOwnership"],
            withAccessors: true);

        Assert.NotEmpty(property.Accessors);
        Assert.All(property.Accessors, accessor =>
            Assert.Equal(property.ReferenceOwnership, accessor.Method.SinkReferenceOwnership));
    }

    [Fact]
    public void ParseModule_StrongProperty_LeavesAccessorSinkOwnershipStrong()
    {
        // The default must stay the default: an ordinary `var` writes storage that retains, and
        // its accessors must say so rather than inherit whatever a sibling declaration carried.
        var property = ParseSingleProperty(
            ownership: null,
            declAttributes: ["HasStorage", "AccessControl"],
            withAccessors: true);

        Assert.NotEmpty(property.Accessors);
        Assert.All(property.Accessors, accessor =>
            Assert.Equal(SwiftReferenceOwnership.Strong, accessor.Method.SinkReferenceOwnership));
    }

    #region Test Helpers

    /// <summary>
    /// Parses a class carrying exactly one stored property with the given ownership encoding
    /// and returns that property. <paramref name="withAccessors"/> attaches a getter/setter pair
    /// so accessor-level facts can be inspected.
    /// </summary>
    private static PropertyDecl ParseSingleProperty(int? ownership, string[] declAttributes, bool withAccessors = false)
    {
        var propertyType = new Node
        {
            Kind = "TypeNominal",
            DeclKind = "",
            Name = "SomeDelegate",
            PrintedName = "TestModule.SomeDelegate",
            ModuleName = "TestModule",
            MangledName = "$s10TestModule12SomeDelegateP",
            DeclAttributes = [],
            @static = false,
            IsInternal = false,
            GenericSig = null,
            sugared_genericSig = null,
            throwing = false,
            AccessorKind = null,
            EnumRawTypeName = null,
            paramValueOwnership = null,
            hasDefaultArg = null,
            Children = [],
            Conformances = [],
            Accessors = []
        };

        var varNode = new Node
        {
            Kind = "Var",
            DeclKind = "Var",
            Name = "delegate",
            PrintedName = "delegate",
            ModuleName = "TestModule",
            MangledName = "$s10TestModule7HolderC8delegateAA04SomeC0_pSgvp",
            DeclAttributes = declAttributes,
            ownership = ownership,
            @static = false,
            IsInternal = false,
            GenericSig = null,
            sugared_genericSig = null,
            throwing = false,
            AccessorKind = null,
            EnumRawTypeName = null,
            paramValueOwnership = null,
            hasDefaultArg = null,
            Children = new[] { propertyType },
            Conformances = [],
            Accessors = withAccessors
                ? new[] { CreateAccessorNode("get", propertyType), CreateAccessorNode("set", propertyType) }
                : []
        };

        var classNode = new Node
        {
            Kind = "TypeDecl",
            DeclKind = "Class",
            Name = "Holder",
            PrintedName = "Holder",
            ModuleName = "TestModule",
            MangledName = "$s10TestModule6HolderCN",
            DeclAttributes = ["AccessControl"],
            @static = false,
            IsInternal = false,
            GenericSig = null,
            sugared_genericSig = null,
            throwing = false,
            AccessorKind = null,
            EnumRawTypeName = null,
            paramValueOwnership = null,
            hasDefaultArg = null,
            Children = new[] { varNode },
            Conformances = [],
            Accessors = []
        };

        using var fixture = CreateParserWithNodes(classNode);
        var result = fixture.Parser.ParseModule();


        var cls = Assert.IsType<ClassDecl>(Assert.Single(result.ModuleDecl.Types));
        return Assert.Single(cls.Properties, p => p.Name == "delegate");
    }

    /// <summary>
    /// A stored property's accessor node. Both accessor kinds carry the property's type as their
    /// first child (the getter's return); the setter additionally reads its second child as the
    /// value parameter, which for a stored property is that same type.
    /// </summary>
    private static Node CreateAccessorNode(string accessorKind, Node propertyType) => new Node
    {
        Kind = "Function",
        DeclKind = "Accessor",
        Name = accessorKind,
        PrintedName = $"{accessorKind}()",
        ModuleName = "TestModule",
        MangledName = $"$s10TestModule6HolderC8delegateAA04SomeC0_pSgv{accessorKind[0]}",
        DeclAttributes = ["AccessControl"],
        @static = false,
        IsInternal = false,
        GenericSig = null,
        sugared_genericSig = null,
        throwing = false,
        AccessorKind = accessorKind,
        EnumRawTypeName = null,
        paramValueOwnership = null,
        hasDefaultArg = null,
        Children = new[] { propertyType, propertyType },
        Conformances = [],
        Accessors = []
    };

    private static ParserFixture CreateParserWithNodes(params Node[] nodes)
    {
        var root = new ABIRootNode
        {
            ABIRoot = new RootNode
            {
                Kind = "Root",
                Name = "Root",
                PrintedName = "Root",
                Children = nodes
            }
        };

        var filePath = Path.GetTempFileName();
        File.WriteAllText(filePath, JsonConvert.SerializeObject(root));

        var parser = new SwiftABIParser(
            filePath,
            new TypeDatabase(),
            CreateEmptyDemanglingResults(),
            NullLogger.Instance,
            SwiftInterfaceFacts.Empty);

        return new ParserFixture(parser, filePath);
    }

    private static DemanglingResults CreateEmptyDemanglingResults()
    {
        var ctor = typeof(DemanglingResults).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            [typeof(IReduction[]), typeof(HashSet<string>)],
            modifiers: null)!;

        return (DemanglingResults)ctor.Invoke([System.Array.Empty<IReduction>(), null]);
    }

    private sealed class ParserFixture : System.IDisposable
    {
        public ParserFixture(SwiftABIParser parser, string filePath)
        {
            Parser = parser;
            _filePath = filePath;
        }

        public SwiftABIParser Parser { get; }
        private readonly string _filePath;

        public void Dispose()
        {
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
            }
        }
    }

    #endregion
}
