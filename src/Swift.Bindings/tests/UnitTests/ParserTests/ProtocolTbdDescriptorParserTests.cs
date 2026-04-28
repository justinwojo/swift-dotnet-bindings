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
/// Coverage for the parser-side TBD method-descriptor gate that drives
/// <see cref="ProtocolDecl.HasMissingTbdMethodDescriptors"/>.
///
/// The gate fires when the swiftinterface declares a protocol requirement whose
/// method-descriptor symbol (mangled name + <c>Tq</c>) is absent from the
/// framework's TBD on this slice — Apple's macCatalyst <c>ConversationManagerDelegate</c>
/// is the canonical example. Setting the flag tells the EveryProtocol emitter to
/// skip the conformance so the wrapper links instead of referencing an undefined
/// descriptor symbol.
///
/// Gate scope, all asserted below:
///   • methods only — properties and subscripts also have <c>Tq</c> descriptors,
///     but no Apple SDK in the validation corpus exhibits the missing-property
///     case, so they're explicitly out of scope until one surfaces;
///   • <c>@objc</c> protocols are exempt because their selector-based dispatch
///     never emits a Swift <c>Tq</c> descriptor in the first place — flagging
///     them would suppress every working ObjC proxy;
///   • <c>@objc optional</c> methods inside non-<c>@objc</c> protocols are
///     skipped per-method for the same reason;
///   • methods with no MangledName at all are skipped (no symbol to look up).
/// </summary>
public class ProtocolTbdDescriptorParserTests
{
    [Fact]
    public void Protocol_AllMethodDescriptorsPresentInTbd_FlagStaysFalse()
    {
        // Baseline: every required method's `{mangled}Tq` symbol is present in the
        // TBD's AllSymbols set, so the gate must not fire and the EveryProtocol
        // emitter sees `HasMissingTbdMethodDescriptors == false`.
        var method = CreateMethodNode(
            name: "didActivate",
            mangledName: "$s10TestModule5DelegateP11didActivateyyF",
            protocolReq: true);

        var protocolNode = CreateProtocolNode(
            name: "Delegate",
            mangledName: "$s10TestModule5DelegateP",
            children: new[] { method });

        var symbols = new HashSet<string>
        {
            "$s10TestModule5DelegateP11didActivateyyFTq",
        };

        using var fixture = CreateParserWithNodes(symbols, protocolNode);
        var result = fixture.Parser.ParseModule();

        var protocolDecl = (ProtocolDecl)result.ModuleDecl.Types.Single();
        Assert.False(protocolDecl.HasMissingTbdMethodDescriptors,
            "All required-method Tq descriptors are present in TBD; the gate must not fire.");
    }

    [Fact]
    public void Protocol_RequiredMethodDescriptorMissingFromTbd_FlagSetTrue()
    {
        // Mirror of the LiveCommunicationKit.ConversationManagerDelegate.didActivate
        // pattern under macCatalyst: the swiftinterface declares the requirement but
        // its `Tq` descriptor is not exported from the framework's TBD on this slice.
        // The gate must flip HasMissingTbdMethodDescriptors so the EveryProtocol
        // emitter skips synthesizing a witness table that would reference the
        // missing descriptor symbol.
        var method = CreateMethodNode(
            name: "didActivate",
            mangledName: "$s10TestModule5DelegateP11didActivateyyF",
            protocolReq: true);

        var protocolNode = CreateProtocolNode(
            name: "Delegate",
            mangledName: "$s10TestModule5DelegateP",
            children: new[] { method });

        // AllSymbols deliberately empty — the descriptor is missing from the TBD.
        var symbols = new HashSet<string>();

        using var fixture = CreateParserWithNodes(symbols, protocolNode);
        var result = fixture.Parser.ParseModule();

        var protocolDecl = (ProtocolDecl)result.ModuleDecl.Types.Single();
        Assert.True(protocolDecl.HasMissingTbdMethodDescriptors,
            "When a required method's Tq descriptor is absent from the TBD, the " +
            "gate must flip HasMissingTbdMethodDescriptors so the conformance is " +
            "skipped (otherwise the wrapper link fails with an undefined symbol).");
    }

    [Fact]
    public void Protocol_AtObjC_DescriptorMissing_GateDoesNotFire()
    {
        // `@objc protocol` requirements dispatch through the ObjC selector table,
        // not a Swift witness table — they never emit a Tq descriptor at all, so
        // a missing one is meaningless. Without this scope guard the gate would
        // suppress proxy classes for every @objc protocol on every slice.
        var method = CreateMethodNode(
            name: "ping",
            mangledName: "$s10TestModule6PingerP4pingyyF",
            protocolReq: true);

        var protocolNode = CreateProtocolNode(
            name: "Pinger",
            mangledName: "$s10TestModule6PingerP",
            children: new[] { method });
        protocolNode.DeclAttributes = new[] { "ObjC" };

        var symbols = new HashSet<string>(); // No descriptors anywhere.

        using var fixture = CreateParserWithNodes(symbols, protocolNode);
        var result = fixture.Parser.ParseModule();

        var protocolDecl = (ProtocolDecl)result.ModuleDecl.Types.Single();
        Assert.False(protocolDecl.HasMissingTbdMethodDescriptors,
            "@objc protocols must be exempt from the TBD descriptor gate — they " +
            "use selector dispatch and never produce Tq descriptors, so a missing " +
            "one is not a real signal.");
    }

    [Fact]
    public void Protocol_AtObjCOptionalMethod_DescriptorMissing_MethodSkipped()
    {
        // A method marked `@objc optional` inside an otherwise-non-@objc protocol
        // (which can show up in mixed Apple frameworks) should be skipped from the
        // gate per-method. Same reasoning as the protocol-level skip: optional
        // members dispatch via selector, not Swift witness, so a missing Tq is not
        // a real failure mode for them.
        var optionalMethod = CreateMethodNode(
            name: "didActivate",
            mangledName: "$s10TestModule5DelegateP11didActivateyyF",
            protocolReq: true);
        optionalMethod.DeclAttributes = new[] { "Optional" };

        var protocolNode = CreateProtocolNode(
            name: "Delegate",
            mangledName: "$s10TestModule5DelegateP",
            children: new[] { optionalMethod });

        var symbols = new HashSet<string>();

        using var fixture = CreateParserWithNodes(symbols, protocolNode);
        var result = fixture.Parser.ParseModule();

        var protocolDecl = (ProtocolDecl)result.ModuleDecl.Types.Single();
        Assert.False(protocolDecl.HasMissingTbdMethodDescriptors,
            "A `@objc optional` method inside a non-@objc protocol must be skipped " +
            "by the TBD descriptor gate — it dispatches via selector, not witness.");
    }

    [Fact]
    public void Protocol_NonRequirementMethod_DescriptorMissing_GateDoesNotFire()
    {
        // Default-implementation methods (protocolReq=false) live on the protocol
        // declaration but are not part of the conformance witness table — Swift
        // provides their implementation automatically. The gate must not fire on
        // their absent Tq descriptor: the descriptor genuinely doesn't exist for
        // a default implementation, and treating it as a "missing requirement"
        // would suppress useful conformances.
        var defaultImpl = CreateMethodNode(
            name: "describe",
            mangledName: "$s10TestModule5DelegateP8describeyyF",
            protocolReq: false);

        var protocolNode = CreateProtocolNode(
            name: "Delegate",
            mangledName: "$s10TestModule5DelegateP",
            children: new[] { defaultImpl });

        var symbols = new HashSet<string>();

        using var fixture = CreateParserWithNodes(symbols, protocolNode);
        var result = fixture.Parser.ParseModule();

        var protocolDecl = (ProtocolDecl)result.ModuleDecl.Types.Single();
        Assert.False(protocolDecl.HasMissingTbdMethodDescriptors,
            "Non-requirement (default-impl) methods don't carry conformance witness " +
            "obligations; their missing Tq descriptors must not flag the protocol.");
    }

    [Fact]
    public void Protocol_PropertyDescriptorMissing_GateDoesNotFire_DocumentsCurrentScope()
    {
        // Documents the explicit scope guard called out in SwiftABIParser:
        //   "Scope: methods only. Protocol property and subscript requirements
        //    also have `Tq` descriptors in Swift, but no observed Apple SDK has
        //    missing-property-descriptor cases against the validation corpus."
        // A missing property Tq descriptor must NOT flag the protocol — there's
        // no observed need yet, and flagging would be a false positive against
        // every framework that ships properties without the descriptor noise.
        // If a real Apple-SDK case ever surfaces, swiftc stderr (now always
        // captured by Build.Validation) will fail loud; this test should be
        // updated alongside extending the cross-check to walk Properties.
        var propertyNode = new Node
        {
            Kind = "Var",
            DeclKind = "Var",
            Name = "isEnabled",
            PrintedName = "isEnabled",
            ModuleName = "TestModule",
            MangledName = "$s10TestModule5DelegateP9isEnabledSbvp",
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
            protocolReq = true,
            Children = new[] { CreateTypeNominalNode("Bool") },
            Conformances = [],
            Accessors = [],
        };

        var protocolNode = CreateProtocolNode(
            name: "Delegate",
            mangledName: "$s10TestModule5DelegateP",
            children: new[] { propertyNode });

        var symbols = new HashSet<string>(); // Property Tq descriptor absent.

        using var fixture = CreateParserWithNodes(symbols, protocolNode);
        var result = fixture.Parser.ParseModule();

        var protocolDecl = (ProtocolDecl)result.ModuleDecl.Types.Single();
        Assert.False(protocolDecl.HasMissingTbdMethodDescriptors,
            "Property requirements are deliberately out of the gate's scope; a " +
            "missing property Tq descriptor must not flag the protocol until a " +
            "real Apple-SDK case requires extending the cross-check.");
    }

    [Fact]
    public void Protocol_RequiredMethodWithoutMangledName_GateSkipsIt()
    {
        // Methods that fail to acquire a MangledName from the ABI JSON (rare but
        // possible for obscure type encodings) have no symbol to look up. The
        // gate's `string.IsNullOrEmpty(MangledName)` short-circuit must skip
        // them rather than treat them as "missing" — otherwise a single
        // unparseable signature would suppress the whole protocol's conformance.
        var unmangledMethod = CreateMethodNode(
            name: "broken",
            mangledName: string.Empty,
            protocolReq: true);

        var protocolNode = CreateProtocolNode(
            name: "Delegate",
            mangledName: "$s10TestModule5DelegateP",
            children: new[] { unmangledMethod });

        var symbols = new HashSet<string>();

        using var fixture = CreateParserWithNodes(symbols, protocolNode);
        var result = fixture.Parser.ParseModule();

        var protocolDecl = (ProtocolDecl)result.ModuleDecl.Types.Single();
        Assert.False(protocolDecl.HasMissingTbdMethodDescriptors,
            "Methods without a MangledName must be skipped by the gate — there " +
            "is no symbol to look up, so they cannot be 'missing'.");
    }

    #region Test Helpers

    /// <summary>
    /// Creates a Function node configured as a protocol requirement with the
    /// minimum fields needed for parser dispatch (PrintedName carries the empty
    /// argument list, a Void return-type child).
    /// </summary>
    private static Node CreateMethodNode(string name, string mangledName, bool protocolReq)
    {
        var node = CreateNode(kind: "Function", declKind: "Func", name: name, mangledName: mangledName);
        node.PrintedName = $"{name}()";
        node.protocolReq = protocolReq;
        node.Children = new[] { CreateTypeNominalNode("Void") };
        return node;
    }

    private static Node CreateProtocolNode(string name, string mangledName, IEnumerable<Node> children)
    {
        var node = CreateNode(kind: "TypeDecl", declKind: "Protocol", name: name, mangledName: mangledName);
        node.Children = children;
        return node;
    }

    private static Node CreateTypeNominalNode(string name)
        => CreateNode(kind: "TypeNominal", name: name);

    private static Node CreateNode(
        string kind,
        string declKind = "",
        string name = "",
        string moduleName = "TestModule",
        string mangledName = "$s")
    {
        return new Node
        {
            Kind = kind,
            DeclKind = declKind,
            Name = name,
            MangledName = mangledName,
            PrintedName = name,
            ModuleName = moduleName,
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
            Accessors = [],
        };
    }

    private static ParserFixture CreateParserWithNodes(HashSet<string> tbdSymbols, params Node[] nodes)
    {
        var importNode = CreateNode(kind: "Import", moduleName: "TestModule", name: "TestModule");
        var allNodes = new[] { importNode }.Concat(nodes).ToArray();

        var root = new ABIRootNode
        {
            ABIRoot = new RootNode
            {
                Kind = "Root",
                Name = "Root",
                PrintedName = "Root",
                Children = allNodes,
            },
        };

        var filePath = Path.GetTempFileName();
        File.WriteAllText(filePath, JsonConvert.SerializeObject(root));

        var parser = new SwiftABIParser(
            filePath,
            new TypeDatabase(),
            CreateDemanglingResults(tbdSymbols),
            NullLogger<SwiftABIParser>.Instance);

        return new ParserFixture(parser, filePath);
    }

    private static DemanglingResults CreateDemanglingResults(HashSet<string> allSymbols)
    {
        var ctor = typeof(DemanglingResults).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            [typeof(IReduction[]), typeof(HashSet<string>)],
            modifiers: null)!;

        return (DemanglingResults)ctor.Invoke([System.Array.Empty<IReduction>(), allSymbols]);
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
                File.Delete(_filePath);
        }
    }

    #endregion
}
