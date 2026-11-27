// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BindingsGeneration.Demangling;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Types a module declares inside an extension of another module's nested type
/// (<c>extension String.Index { @frozen public struct Payload {} }</c>).
///
/// <para>
/// The ABI descriptor roots the receiver at its module under its bare leaf name, so its Swift name
/// comes from its mangling. Its metadata accessor is in no symbol table the generator reads, and it
/// used to be dropped with its nested types, which left every struct storing one judged without its
/// layout. It is now kept as their host only: never registered or laid out, its nested types parsed
/// as the module's own.
/// </para>
/// </summary>
public class ForeignReceiverNestedTypeTests
{
    private const string Module = "TestModule";

    [Fact]
    public void ParseModule_ReceiverWithoutAccessor_KeepsTheNestedStructsUnderAnUnresolvedHost()
    {
        var result = Parse();

        var host = Assert.Single(result.ModuleDecl.Types.OfType<StructDecl>(), t => t.Name == "Index");
        Assert.Equal("Swift.String.Index", host.SwiftTypeName.ModuleQualifiedName);
        Assert.True(host.IsUnresolvedForeignHost);
        Assert.Equal(string.Empty, host.MetadataAccessor);

        var payload = Assert.IsType<StructDecl>(Assert.Single(host.Types));
        Assert.Equal("Swift.String.Index.Payload", payload.SwiftTypeName.ModuleQualifiedName);
        Assert.Equal(Module, payload.ModuleDecl?.Name);
        Assert.True(payload.IsFrozen);
        Assert.False(payload.IsUnresolvedForeignHost);
        Assert.Equal("$sSS5IndexV10TestModuleE7PayloadVMa", payload.MetadataAccessor);
    }

    [Fact]
    public void ParseModule_OwnTopLevelStruct_IsNotAnUnresolvedHost()
    {
        var result = Parse();

        var holder = Assert.Single(result.ModuleDecl.Types.OfType<StructDecl>(), t => t.Name == "Holder");
        Assert.False(holder.IsUnresolvedForeignHost);
        Assert.Equal("TestModule.Holder", holder.SwiftTypeName.ModuleQualifiedName);
    }

    [Fact]
    public void ParseModule_NestedStructPublicInTheInterface_IsPublic()
    {
        // The interface spells the payload through the receiver's outer types.
        using var fixture = CreateParser(SwiftInterfaceFacts.Empty with
        {
            PublicTypeNames = new HashSet<string> { "Holder", "String.Index.Payload" },
        }, OwnHolder(), StringIndexWithPayload());

        var result = fixture.Parser.ParseModule();

        var host = Assert.Single(result.ModuleDecl.Types.OfType<StructDecl>(), t => t.Name == "Index");
        Assert.False(Assert.Single(host.Types).IsModuleInternal);
        Assert.True(host.IsUnresolvedForeignHost, "host");
        Assert.False(host.IsModuleInternal);
    }

    [Theory]
    [InlineData("Swift.String.Index.Payload", false)]
    [InlineData("Swift.Int", true)]
    [InlineData("CoreGraphics.CGRect", true)]
    public void IsSystemFrozenStruct_AnswersOnlyForATopLevelSystemType(string name, bool expected)
    {
        Assert.Equal(expected, CdeclParamMapper.IsSystemFrozenStruct(new NamedTypeSpec(name)));
    }

    [Theory]
    [InlineData("$sSS5IndexV", "Swift", "Index", "Swift.String.Index")]
    [InlineData("$sSS5IndexV", "Foundation", "Index", null)]
    [InlineData("$sSS5IndexV", "Swift", "Position", null)]
    [InlineData("$s10TestModule6HolderV", "TestModule", "Holder", null)]
    [InlineData("", "Swift", "Index", null)]
    [InlineData("not-a-mangling", "Swift", "Index", null)]
    public void TryNestedNameFromMangling_AnswersOnlyForANestedNameInTheGivenModule(
        string mangled, string module, string identifier, string? expected)
    {
        using var fixture = CreateParser(SwiftInterfaceFacts.Empty, OwnHolder());

        var found = fixture.Parser.TryNestedNameFromMangling(mangled, module, identifier, out var name);

        Assert.Equal(expected is not null, found);
        if (expected is not null)
            Assert.Equal(expected, name.ModuleQualifiedName);
    }

    #region Test Helpers

    private static ModuleParsingResult Parse()
    {
        using var fixture = CreateParser(SwiftInterfaceFacts.Empty, OwnHolder(), StringIndexWithPayload());
        return fixture.Parser.ParseModule();
    }

    private static Node OwnHolder() => TypeDecl(
        "Holder", Module, "$s10TestModule6HolderV", ["Frozen", "AccessControl"],
        StoredInt("tag", "$s10TestModule6HolderV3tagSivp"));

    private static Node StringIndexWithPayload()
    {
        var payload = TypeDecl(
            "Payload", Module, "$sSS5IndexV10TestModuleE7PayloadV", ["Frozen", "AccessControl"],
            StoredInt("value", "$sSS5IndexV10TestModuleE7PayloadV5valueSivp"));
        return TypeDecl("Index", "Swift", "$sSS5IndexV", ["Frozen"], payload);
    }

    private static Node TypeDecl(string name, string module, string mangled, string[] attributes, params Node[] children) => new()
    {
        Kind = "TypeDecl",
        DeclKind = "Struct",
        Name = name,
        PrintedName = name,
        ModuleName = module,
        MangledName = mangled,
        DeclAttributes = attributes,
        @static = false,
        IsInternal = false,
        GenericSig = null,
        sugared_genericSig = null,
        throwing = false,
        AccessorKind = null,
        EnumRawTypeName = null,
        paramValueOwnership = null,
        hasDefaultArg = null,
        Children = children,
        Conformances = [],
        Accessors = []
    };

    private static Node StoredInt(string name, string mangled) => new()
    {
        Kind = "Var",
        DeclKind = "Var",
        Name = name,
        PrintedName = name,
        ModuleName = Module,
        MangledName = mangled,
        DeclAttributes = ["HasStorage", "AccessControl"],
        @static = false,
        IsInternal = false,
        GenericSig = null,
        sugared_genericSig = null,
        throwing = false,
        AccessorKind = null,
        EnumRawTypeName = null,
        paramValueOwnership = null,
        hasDefaultArg = null,
        Children = new[]
        {
            new Node
            {
                Kind = "TypeNominal",
                DeclKind = "",
                Name = "Int",
                PrintedName = "Swift.Int",
                ModuleName = "Swift",
                MangledName = "$sSi",
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
            }
        },
        Conformances = [],
        Accessors = []
    };

    private static ParserFixture CreateParser(SwiftInterfaceFacts facts, params Node[] nodes)
    {
        var root = new ABIRootNode
        {
            ABIRoot = new RootNode { Kind = "Root", Name = "Root", PrintedName = "Root", Children = nodes }
        };
        var filePath = Path.GetTempFileName();
        File.WriteAllText(filePath, JsonConvert.SerializeObject(root));
        var ctor = typeof(DemanglingResults).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, binder: null,
            [typeof(IReduction[]), typeof(HashSet<string>)], modifiers: null)!;
        var demangling = (DemanglingResults)ctor.Invoke([System.Array.Empty<IReduction>(), null]);
        var parser = new SwiftABIParser(filePath, new TypeDatabase(), demangling, NullLogger.Instance, facts);
        return new ParserFixture(parser, filePath);
    }

    private sealed class ParserFixture : System.IDisposable
    {
        public ParserFixture(SwiftABIParser parser, string filePath) { Parser = parser; _filePath = filePath; }
        public SwiftABIParser Parser { get; }
        private readonly string _filePath;
        public void Dispose() { if (File.Exists(_filePath)) File.Delete(_filePath); }
    }

    #endregion
}
