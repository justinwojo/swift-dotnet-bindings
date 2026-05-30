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

public class SwiftABIParserRuntimeTests
{
    [Fact]
    public void GetModuleName_ReturnsFirstChildModuleName()
    {
        using var fixture = CreateParserWithNodes(
            CreateNode(kind: "Import", moduleName: "StoreKit", name: "StoreKit"));
        var parser = fixture.Parser;

        Assert.Equal("StoreKit", parser.GetModuleName());
    }

    [Fact]
    public void ParseModule_WithUnsupportedNodeKind_DoesNotThrowAndSkipsNode()
    {
        using var fixture = CreateParserWithNodes(
            CreateNode(kind: "UnknownKind", moduleName: "TestModule", name: "Mystery"));
        var parser = fixture.Parser;

        var result = parser.ParseModule();

        Assert.Equal("TestModule", result.ModuleDecl.Name);
        Assert.Empty(result.ModuleDecl.Types);
        Assert.Empty(result.ModuleDecl.Methods);
        Assert.Empty(result.ModuleDecl.Properties);
    }

    [Fact]
    public void ParseModule_WithKeywordModuleName_EscapesModuleName()
    {
        using var fixture = CreateParserWithNodes(
            CreateNode(kind: "Import", moduleName: "class", name: "class"));
        var parser = fixture.Parser;

        var result = parser.ParseModule();

        Assert.Equal("_class", result.ModuleDecl.Name);
    }

    [Fact]
    public void ParseModule_TypeDeclWithoutMangledName_SkipsType()
    {
        using var fixture = CreateParserWithNodes(
            CreateNode(
                kind: "TypeDecl",
                declKind: "Struct",
                moduleName: "TestModule",
                name: "NoMangle",
                mangledName: string.Empty));
        var parser = fixture.Parser;

        var result = parser.ParseModule();

        Assert.Empty(result.ModuleDecl.Types);
        Assert.Empty(result.TypeDecls);
    }

    #region NO_MODULE Guard Tests

    [Fact]
    public void GetModuleName_NoModule_ThrowsDescriptiveError()
    {
        using var fixture = CreateParserWithNodes(
            CreateNode(kind: "Import", moduleName: "NO_MODULE", name: "NO_MODULE"));
        var parser = fixture.Parser;

        var ex = Assert.Throws<InvalidOperationException>(() => parser.GetModuleName());
        Assert.Contains("NO_MODULE", ex.Message);
        Assert.Contains("BUILD_LIBRARY_FOR_DISTRIBUTION", ex.Message);
    }

    [Fact]
    public void GetModuleName_EmptyModule_ThrowsDescriptiveError()
    {
        using var fixture = CreateParserWithNodes(
            CreateNode(kind: "Import", moduleName: "", name: ""));
        var parser = fixture.Parser;

        var ex = Assert.Throws<InvalidOperationException>(() => parser.GetModuleName());
        Assert.Contains("BUILD_LIBRARY_FOR_DISTRIBUTION", ex.Message);
    }

    #endregion

    #region SPI Suppression Tests

    [Fact]
    public void ParseModule_TypeWithSPIAccessControl_IsMarkedInternal()
    {
        // E15: @_spi types have SPIAccessControl in DeclAttributes and should be treated as internal
        var classNode = CreateNode(
            kind: "TypeDecl",
            declKind: "Class",
            name: "InternalSPIType",
            mangledName: "$s10TestModule15InternalSPITypeCN");
        classNode.DeclAttributes = new[] { "SPIAccessControl", "AccessControl" };

        using var fixture = CreateParserWithNodes(classNode);
        var result = fixture.Parser.ParseModule();

        var spiType = Assert.Single(result.ModuleDecl.Types);
        Assert.True(spiType.IsModuleInternal, "@_spi type should be marked as module internal");
    }

    [Fact]
    public void ParseModule_TypeWithoutSPIAccessControl_IsNotInternal()
    {
        var classNode = CreateNode(
            kind: "TypeDecl",
            declKind: "Class",
            name: "PublicType",
            mangledName: "$s10TestModule10PublicTypeCN");
        classNode.DeclAttributes = new[] { "AccessControl" };

        using var fixture = CreateParserWithNodes(classNode);
        var result = fixture.Parser.ParseModule();

        var pubType = Assert.Single(result.ModuleDecl.Types);
        Assert.False(pubType.IsModuleInternal, "Public type should not be marked as internal");
    }

    [Fact]
    public void ParseModule_TypeWithSPIAccessControl_IsSpiProtected()
    {
        var classNode = CreateNode(
            kind: "TypeDecl",
            declKind: "Class",
            name: "SPIProtectedType",
            mangledName: "$s10TestModule16SPIProtectedTypeCN");
        classNode.DeclAttributes = new[] { "SPIAccessControl", "AccessControl" };

        using var fixture = CreateParserWithNodes(classNode);
        var result = fixture.Parser.ParseModule();

        var spiType = Assert.Single(result.ModuleDecl.Types);
        Assert.True(spiType.IsSpiProtected, "@_spi type should have IsSpiProtected = true");
    }

    [Fact]
    public void ParseModule_TypeWithUsableFromInline_IsNotSpiProtected()
    {
        var classNode = CreateNode(
            kind: "TypeDecl",
            declKind: "Class",
            name: "InlineableType",
            mangledName: "$s10TestModule14InlineableTypeCN");
        classNode.DeclAttributes = new[] { "UsableFromInline" };

        using var fixture = CreateParserWithNodes(classNode);
        var result = fixture.Parser.ParseModule();

        var inlineType = Assert.Single(result.ModuleDecl.Types);
        Assert.True(inlineType.IsModuleInternal, "@usableFromInline should be module internal");
        Assert.False(inlineType.IsSpiProtected, "@usableFromInline should NOT be SPI protected");
    }

    #endregion

    #region funcSelfKind → IsMutating Tests

    [Fact]
    public void ParseModule_FuncWithMutatingFuncSelfKind_SetsIsMutatingTrue()
    {
        // A function node with funcSelfKind = "Mutating"
        var returnTypeNode = CreateNode(kind: "TypeNominal", name: "Void", mangledName: "$s");
        returnTypeNode.PrintedName = "()";

        var paramNode = CreateNode(kind: "TypeNominal", name: "Int", mangledName: "$s");
        paramNode.PrintedName = "Int";

        var funcNode = CreateFunctionNode(
            name: "update",
            printedName: "update(_:)",
            funcSelfKind: "Mutating",
            children: new[] { returnTypeNode, paramNode });

        using var fixture = CreateParserWithNodes(funcNode);
        var result = fixture.Parser.ParseModule();

        var method = Assert.Single(result.ModuleDecl.Methods);
        Assert.True(method.IsMutating);
    }

    [Fact]
    public void ParseModule_FuncWithNonMutatingFuncSelfKind_SetsIsMutatingFalse()
    {
        var returnTypeNode = CreateNode(kind: "TypeNominal", name: "Void", mangledName: "$s");
        returnTypeNode.PrintedName = "()";

        var funcNode = CreateFunctionNode(
            name: "read",
            printedName: "read()",
            funcSelfKind: "NonMutating",
            children: new[] { returnTypeNode });

        using var fixture = CreateParserWithNodes(funcNode);
        var result = fixture.Parser.ParseModule();

        var method = Assert.Single(result.ModuleDecl.Methods);
        Assert.False(method.IsMutating);
    }

    [Fact]
    public void ParseModule_FuncWithNullFuncSelfKind_SetsIsMutatingFalse()
    {
        var returnTypeNode = CreateNode(kind: "TypeNominal", name: "Void", mangledName: "$s");
        returnTypeNode.PrintedName = "()";

        var funcNode = CreateFunctionNode(
            name: "process",
            printedName: "process()",
            funcSelfKind: null,
            children: new[] { returnTypeNode });

        using var fixture = CreateParserWithNodes(funcNode);
        var result = fixture.Parser.ParseModule();

        var method = Assert.Single(result.ModuleDecl.Methods);
        Assert.False(method.IsMutating);
    }

    #endregion

    #region IsModuleInternal Tests (Bug #17)

    [Fact]
    public void ParseModule_FuncWithIsInternalTrue_SetsIsModuleInternalTrue()
    {
        var returnTypeNode = CreateNode(kind: "TypeNominal", name: "Void", mangledName: "$s");
        returnTypeNode.PrintedName = "()";

        var funcNode = CreateFunctionNode(
            name: "process64",
            printedName: "process64()",
            funcSelfKind: null,
            children: new[] { returnTypeNode });
        funcNode.IsInternal = true;

        using var fixture = CreateParserWithNodes(funcNode);
        var result = fixture.Parser.ParseModule();

        var method = Assert.Single(result.ModuleDecl.Methods);
        Assert.True(method.IsModuleInternal);
        Assert.Equal(Visibility.Public, method.Visibility); // C# access stays public
    }

    [Fact]
    public void ParseModule_FuncWithIsInternalFalse_SetsIsModuleInternalFalse()
    {
        var returnTypeNode = CreateNode(kind: "TypeNominal", name: "Void", mangledName: "$s");
        returnTypeNode.PrintedName = "()";

        var funcNode = CreateFunctionNode(
            name: "encrypt",
            printedName: "encrypt()",
            funcSelfKind: null,
            children: new[] { returnTypeNode });
        funcNode.IsInternal = false;

        using var fixture = CreateParserWithNodes(funcNode);
        var result = fixture.Parser.ParseModule();

        var method = Assert.Single(result.ModuleDecl.Methods);
        Assert.False(method.IsModuleInternal);
    }

    [Fact]
    public void ParseModule_FuncWithIsInternalNull_SetsIsModuleInternalFalse()
    {
        var returnTypeNode = CreateNode(kind: "TypeNominal", name: "Void", mangledName: "$s");
        returnTypeNode.PrintedName = "()";

        var funcNode = CreateFunctionNode(
            name: "doWork",
            printedName: "doWork()",
            funcSelfKind: null,
            children: new[] { returnTypeNode });
        funcNode.IsInternal = null;

        using var fixture = CreateParserWithNodes(funcNode);
        var result = fixture.Parser.ParseModule();

        var method = Assert.Single(result.ModuleDecl.Methods);
        Assert.False(method.IsModuleInternal);
    }

    [Fact]
    public void ParseModule_FuncWithUsableFromInlineWithoutAccessControl_SetsIsModuleInternalTrue()
    {
        // @usableFromInline internal methods have "UsableFromInline" but NOT "AccessControl"
        var returnTypeNode = CreateNode(kind: "TypeNominal", name: "Void", mangledName: "$s");
        returnTypeNode.PrintedName = "()";

        var funcNode = CreateFunctionNode(
            name: "process64",
            printedName: "process64()",
            funcSelfKind: null,
            children: new[] { returnTypeNode });
        funcNode.IsInternal = null;
        funcNode.DeclAttributes = new[] { "Final", "UsableFromInline" };

        using var fixture = CreateParserWithNodes(funcNode);
        var result = fixture.Parser.ParseModule();

        var method = Assert.Single(result.ModuleDecl.Methods);
        Assert.True(method.IsModuleInternal);
        Assert.Equal(Visibility.Public, method.Visibility); // C# access stays public
    }

    [Fact]
    public void ParseModule_FuncWithAccessControlAndUsableFromInline_SetsIsModuleInternalTrue()
    {
        // @usableFromInline is exclusively used on internal declarations, even when
        // AccessControl is also present (explicit 'internal' keyword).
        var returnTypeNode = CreateNode(kind: "TypeNominal", name: "Void", mangledName: "$s");
        returnTypeNode.PrintedName = "()";

        var funcNode = CreateFunctionNode(
            name: "encrypt",
            printedName: "encrypt()",
            funcSelfKind: null,
            children: new[] { returnTypeNode });
        funcNode.IsInternal = null;
        funcNode.DeclAttributes = new[] { "Final", "AccessControl", "Inlinable", "UsableFromInline" };

        using var fixture = CreateParserWithNodes(funcNode);
        var result = fixture.Parser.ParseModule();

        var method = Assert.Single(result.ModuleDecl.Methods);
        Assert.True(method.IsModuleInternal);
        Assert.Equal(Visibility.Public, method.Visibility); // C# access stays public
    }

    [Fact]
    public void ParseModule_FuncWithInlinableWithoutAccessControl_SetsIsModuleInternalTrue()
    {
        // @inlinable internal methods without explicit access control have
        // "Inlinable" but NOT "AccessControl" in declAttributes
        var returnTypeNode = CreateNode(kind: "TypeNominal", name: "Void", mangledName: "$s");
        returnTypeNode.PrintedName = "()";

        var funcNode = CreateFunctionNode(
            name: "xor",
            printedName: "xor()",
            funcSelfKind: null,
            children: new[] { returnTypeNode });
        funcNode.IsInternal = null;
        funcNode.DeclAttributes = new[] { "Final", "Inlinable" };

        using var fixture = CreateParserWithNodes(funcNode);
        var result = fixture.Parser.ParseModule();

        var method = Assert.Single(result.ModuleDecl.Methods);
        Assert.True(method.IsModuleInternal);
    }

    [Fact]
    public void ParseModule_FuncWithInlinableWithoutAccessControl_PublicInSwiftInterface_SetsIsModuleInternalFalse()
    {
        // Some toolchains emit ONLY ["Inlinable"] (no "AccessControl") for an @inlinable
        // PUBLIC member, so the "Inlinable without AccessControl" guess mis-flags it internal.
        // When a public swiftinterface is available, it authoritatively resolves the access
        // level — the guess must NOT pre-empt the negative-space detection that keeps a
        // swiftinterface-public member public. (Real-world repro: RealityFoundation's
        // @inlinable public Transform.init(scale:rotation:translation:).)
        var returnTypeNode = CreateNode(kind: "TypeNominal", name: "Void", mangledName: "$s");
        returnTypeNode.PrintedName = "()";

        var funcNode = CreateFunctionNode(
            name: "encrypt",
            printedName: "encrypt()",
            funcSelfKind: null,
            children: new[] { returnTypeNode });
        funcNode.IsInternal = null;
        funcNode.DeclAttributes = new[] { "Final", "Inlinable" };

        // Public swiftinterface lists this free function as public (bare printedName key).
        var facts = SwiftInterfaceFacts.Empty with
        {
            PublicMemberNames = new HashSet<string> { "encrypt()" }
        };

        using var fixture = CreateParserWithFacts(facts, funcNode);
        var result = fixture.Parser.ParseModule();

        var method = Assert.Single(result.ModuleDecl.Methods);
        Assert.False(method.IsModuleInternal);
    }

    [Fact]
    public void ParseModule_FuncWithInlinableAndAccessControl_SetsIsModuleInternalFalseWithoutSwiftInterface()
    {
        // @inlinable public methods have both "Inlinable" and "AccessControl" —
        // indistinguishable from @inlinable internal without swiftinterface data
        var returnTypeNode = CreateNode(kind: "TypeNominal", name: "Void", mangledName: "$s");
        returnTypeNode.PrintedName = "()";

        var funcNode = CreateFunctionNode(
            name: "encrypt",
            printedName: "encrypt()",
            funcSelfKind: null,
            children: new[] { returnTypeNode });
        funcNode.IsInternal = null;
        funcNode.DeclAttributes = new[] { "Final", "AccessControl", "Inlinable" };

        using var fixture = CreateParserWithNodes(funcNode);
        var result = fixture.Parser.ParseModule();

        var method = Assert.Single(result.ModuleDecl.Methods);
        // Without swiftinterface, we can't tell if it's public or internal
        Assert.False(method.IsModuleInternal);
    }

    [Fact]
    public void ParseModule_ProtocolComposition_ParsesFromPrintedName()
    {
        // ProtocolComposition nodes in ABI JSON have no children — protocols are in printedName
        var compositionNode = CreateNode(
            kind: "TypeNominal",
            name: "ProtocolComposition",
            mangledName: "$s");
        compositionNode.PrintedName = "any TestModule.Cryptor & TestModule.Updatable";

        var funcNode = CreateFunctionNode(
            name: "makeEncryptor",
            printedName: "makeEncryptor()",
            funcSelfKind: null,
            children: new[] { compositionNode });

        using var fixture = CreateParserWithNodes(funcNode);
        var result = fixture.Parser.ParseModule();

        var method = Assert.Single(result.ModuleDecl.Methods);
        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        Assert.NotNull(returnType);
        Assert.IsType<ProtocolListTypeSpec>(returnType);
        var protocolList = (ProtocolListTypeSpec)returnType;
        Assert.Equal(2, protocolList.Protocols.Count);
    }

    [Fact]
    public void ParseModule_ProtocolCompositionAny_ReturnsEmptyProtocolList()
    {
        // "Any" printed name should produce empty protocol list
        var compositionNode = CreateNode(
            kind: "TypeNominal",
            name: "ProtocolComposition",
            mangledName: "$s");
        compositionNode.PrintedName = "Any";

        var funcNode = CreateFunctionNode(
            name: "getValue",
            printedName: "getValue()",
            funcSelfKind: null,
            children: new[] { compositionNode });

        using var fixture = CreateParserWithNodes(funcNode);
        var result = fixture.Parser.ParseModule();

        var method = Assert.Single(result.ModuleDecl.Methods);
        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        Assert.NotNull(returnType);
        Assert.IsType<ProtocolListTypeSpec>(returnType);
        var protocolList = (ProtocolListTypeSpec)returnType;
        Assert.Empty(protocolList.Protocols);
    }

    #endregion

    #region IsFinal Parsing Tests

    [Fact]
    public void ParseModule_ClassWithFinalAttribute_SetsIsFinalTrue()
    {
        var classNode = CreateNode(
            kind: "TypeDecl",
            declKind: "Class",
            name: "FinalHandler",
            mangledName: "$s10TestModule12FinalHandlerCN");
        classNode.DeclAttributes = new[] { "Final", "AccessControl" };

        using var fixture = CreateParserWithNodes(classNode);
        var result = fixture.Parser.ParseModule();

        var classDecl = Assert.Single(result.ModuleDecl.Types);
        Assert.IsType<ClassDecl>(classDecl);
        Assert.True(((ClassDecl)classDecl).IsFinal);
    }

    [Fact]
    public void ParseModule_ClassWithoutFinalAttribute_SetsIsFinalFalse()
    {
        var classNode = CreateNode(
            kind: "TypeDecl",
            declKind: "Class",
            name: "Animal",
            mangledName: "$s10TestModule6AnimalCN");
        classNode.DeclAttributes = new[] { "AccessControl" };

        using var fixture = CreateParserWithNodes(classNode);
        var result = fixture.Parser.ParseModule();

        var classDecl = Assert.Single(result.ModuleDecl.Types);
        Assert.IsType<ClassDecl>(classDecl);
        Assert.False(((ClassDecl)classDecl).IsFinal);
    }

    [Fact]
    public void ParseModule_MethodWithFinalAttribute_SetsIsFinalTrue()
    {
        var returnTypeNode = CreateNode(kind: "TypeNominal", name: "Void", mangledName: "$s");
        returnTypeNode.PrintedName = "()";

        var funcNode = CreateFunctionNode(
            name: "getKey",
            printedName: "getKey()",
            funcSelfKind: null,
            children: new[] { returnTypeNode });
        funcNode.DeclAttributes = new[] { "Final", "AccessControl" };

        using var fixture = CreateParserWithNodes(funcNode);
        var result = fixture.Parser.ParseModule();

        var method = Assert.Single(result.ModuleDecl.Methods);
        Assert.True(method.IsFinal);
    }

    [Fact]
    public void ParseModule_MethodWithoutFinalAttribute_SetsIsFinalFalse()
    {
        var returnTypeNode = CreateNode(kind: "TypeNominal", name: "Void", mangledName: "$s");
        returnTypeNode.PrintedName = "()";

        var funcNode = CreateFunctionNode(
            name: "speak",
            printedName: "speak()",
            funcSelfKind: null,
            children: new[] { returnTypeNode });
        funcNode.DeclAttributes = new[] { "AccessControl" };

        using var fixture = CreateParserWithNodes(funcNode);
        var result = fixture.Parser.ParseModule();

        var method = Assert.Single(result.ModuleDecl.Methods);
        Assert.False(method.IsFinal);
    }

    [Fact]
    public void ParseModule_PropertyGetAccessorWithFinalAttribute_SetsIsFinalTrue()
    {
        // A stored let property has Final on its getter accessor
        var typeNode = CreateNode(kind: "TypeNominal", name: "Int", mangledName: "$sSi");
        typeNode.PrintedName = "Int";

        var getterAccessor = CreateNode(
            kind: "Accessor",
            name: "key",
            mangledName: "$s10TestModule12AsyncServiceC3keySSvg");
        getterAccessor.AccessorKind = "get";
        getterAccessor.DeclAttributes = new[] { "Final" };
        getterAccessor.Children = new[] { typeNode };

        var varNode = CreateNode(
            kind: "Var",
            name: "key",
            mangledName: "$s10TestModule12AsyncServiceC3keySSvp");
        varNode.DeclAttributes = new[] { "HasStorage", "Final", "AccessControl" };
        varNode.Children = new[] { typeNode };
        varNode.Accessors = new[] { getterAccessor };

        // Place inside a non-final class
        var classNode = CreateNode(
            kind: "TypeDecl",
            declKind: "Class",
            name: "AsyncService",
            mangledName: "$s10TestModule12AsyncServiceCN");
        classNode.DeclAttributes = new[] { "AccessControl" };
        classNode.Children = new[] { varNode };

        using var fixture = CreateParserWithNodes(classNode);
        var result = fixture.Parser.ParseModule();

        var classDecl = Assert.Single(result.ModuleDecl.Types);
        Assert.False(((ClassDecl)classDecl).IsFinal); // class is not final
        var prop = Assert.Single(classDecl.Properties);
        var getter = prop.Accessors.OfType<GetAccessorDecl>().Single();
        Assert.True(getter.Method.IsFinal); // but accessor is final
    }

    [Fact]
    public void ParseModule_PropertyGetAccessorWithoutFinalAttribute_SetsIsFinalFalse()
    {
        // A computed/open property has no Final on its getter accessor
        var typeNode = CreateNode(kind: "TypeNominal", name: "String", mangledName: "$sSS");
        typeNode.PrintedName = "String";

        var getterAccessor = CreateNode(
            kind: "Accessor",
            name: "name",
            mangledName: "$s10TestModule6AnimalC4nameSSvg");
        getterAccessor.AccessorKind = "get";
        getterAccessor.DeclAttributes = Array.Empty<string>();
        getterAccessor.Children = new[] { typeNode };

        var varNode = CreateNode(
            kind: "Var",
            name: "name",
            mangledName: "$s10TestModule6AnimalC4nameSSvp");
        varNode.DeclAttributes = new[] { "AccessControl" };
        varNode.Children = new[] { typeNode };
        varNode.Accessors = new[] { getterAccessor };

        var classNode = CreateNode(
            kind: "TypeDecl",
            declKind: "Class",
            name: "Animal",
            mangledName: "$s10TestModule6AnimalCN");
        classNode.DeclAttributes = new[] { "AccessControl" };
        classNode.Children = new[] { varNode };

        using var fixture = CreateParserWithNodes(classNode);
        var result = fixture.Parser.ParseModule();

        var classDecl = Assert.Single(result.ModuleDecl.Types);
        var prop = Assert.Single(classDecl.Properties);
        var getter = prop.Accessors.OfType<GetAccessorDecl>().Single();
        Assert.False(getter.Method.IsFinal);
    }

    [Fact]
    public void ParseModule_PropertyAvailability_PropagatesToGetSetAccessors()
    {
        // Property-level @available annotations must flow to the accessor MethodDecls so
        // the private *_Get/*_Set backing methods emit matching [SupportedOSPlatform]
        // attributes. Without this, backing methods that reference newer-SDK return/value
        // types trigger CA1416 inside wider class-level surfaces (e.g. WeatherKit
        // DayWeather.precipitationAmountByType: iOS 18+ payload on an iOS 16+ parent type).
        var typeNode = CreateNode(kind: "TypeNominal", name: "Int", mangledName: "$sSi");
        typeNode.PrintedName = "Int";

        var getterAccessor = CreateNode(
            kind: "Accessor",
            name: "newProp",
            mangledName: "$s10TestModule5ThingC7newPropSivg");
        getterAccessor.AccessorKind = "get";
        getterAccessor.Children = new[] { typeNode };

        var setterAccessor = CreateNode(
            kind: "Accessor",
            name: "newProp",
            mangledName: "$s10TestModule5ThingC7newPropSivs");
        setterAccessor.AccessorKind = "set";
        var voidNode = CreateNode(kind: "TypeNominal", name: "Void", mangledName: "$s");
        voidNode.PrintedName = "()";
        setterAccessor.Children = new[] { voidNode, typeNode };

        var varNode = CreateNode(
            kind: "Var",
            name: "newProp",
            mangledName: "$s10TestModule5ThingC7newPropSivp");
        varNode.DeclAttributes = new[] { "HasStorage", "AccessControl" };
        varNode.Children = new[] { typeNode };
        varNode.Accessors = new[] { getterAccessor, setterAccessor };

        var structNode = CreateNode(
            kind: "TypeDecl",
            declKind: "Class",
            name: "Thing",
            mangledName: "$s10TestModule5ThingCN");
        structNode.DeclAttributes = new[] { "AccessControl" };
        structNode.Children = new[] { varNode };

        var annotations = new List<AvailabilityAnnotation>
        {
            new("iOS",   "18.0", null, null, false, false, null, null),
            new("macOS", "15.0", null, null, false, false, null, null),
        };
        var availability = new Dictionary<string, List<AvailabilityAnnotation>>
        {
            ["Thing.newProp"] = annotations,
        };

        using var fixture = CreateParserWithNodes(availability, structNode);
        var result = fixture.Parser.ParseModule();

        var typeDecl = Assert.Single(result.ModuleDecl.Types);
        var prop = Assert.Single(typeDecl.Properties);
        Assert.NotNull(prop.AvailabilityAnnotations);
        Assert.Equal(2, prop.AvailabilityAnnotations!.Count);

        var getter = prop.Accessors.OfType<GetAccessorDecl>().Single();
        Assert.NotNull(getter.Method.AvailabilityAnnotations);
        Assert.Equal(2, getter.Method.AvailabilityAnnotations!.Count);
        Assert.Equal("iOS",   getter.Method.AvailabilityAnnotations[0].Platform);
        Assert.Equal("18.0",  getter.Method.AvailabilityAnnotations[0].IntroducedVersion);
        Assert.Equal("macOS", getter.Method.AvailabilityAnnotations[1].Platform);

        var setter = prop.Accessors.OfType<SetAccessorDecl>().Single();
        Assert.NotNull(setter.Method.AvailabilityAnnotations);
        Assert.Equal(2, setter.Method.AvailabilityAnnotations!.Count);
        Assert.Equal("iOS",   setter.Method.AvailabilityAnnotations[0].Platform);
        Assert.Equal("macOS", setter.Method.AvailabilityAnnotations[1].Platform);

        // Each accessor must own a distinct list so downstream mutation (e.g.
        // PropertyHandler's async-property path) cannot feed back into the parent
        // PropertyDecl or its siblings.
        Assert.NotSame(prop.AvailabilityAnnotations, getter.Method.AvailabilityAnnotations);
        Assert.NotSame(prop.AvailabilityAnnotations, setter.Method.AvailabilityAnnotations);
        Assert.NotSame(getter.Method.AvailabilityAnnotations, setter.Method.AvailabilityAnnotations);

        getter.Method.AvailabilityAnnotations!.Add(
            new AvailabilityAnnotation("tvOS", "18.0", null, null, false, false, null, null));
        Assert.Equal(2, prop.AvailabilityAnnotations!.Count);
        Assert.Equal(2, setter.Method.AvailabilityAnnotations!.Count);
    }

    [Fact]
    public void ParseModule_PropertyWithoutAvailability_LeavesAccessorsUnannotated()
    {
        // Unannotated properties must not leave stale annotations on their accessors —
        // the pipeline only propagates when the property itself has availability data.
        var typeNode = CreateNode(kind: "TypeNominal", name: "Int", mangledName: "$sSi");
        typeNode.PrintedName = "Int";

        var getterAccessor = CreateNode(
            kind: "Accessor",
            name: "bareProp",
            mangledName: "$s10TestModule5ThingC8barePropSivg");
        getterAccessor.AccessorKind = "get";
        getterAccessor.Children = new[] { typeNode };

        var varNode = CreateNode(
            kind: "Var",
            name: "bareProp",
            mangledName: "$s10TestModule5ThingC8barePropSivp");
        varNode.DeclAttributes = new[] { "HasStorage", "AccessControl" };
        varNode.Children = new[] { typeNode };
        varNode.Accessors = new[] { getterAccessor };

        var structNode = CreateNode(
            kind: "TypeDecl",
            declKind: "Class",
            name: "Thing",
            mangledName: "$s10TestModule5ThingCN");
        structNode.DeclAttributes = new[] { "AccessControl" };
        structNode.Children = new[] { varNode };

        using var fixture = CreateParserWithNodes(structNode);
        var result = fixture.Parser.ParseModule();

        var typeDecl = Assert.Single(result.ModuleDecl.Types);
        var prop = Assert.Single(typeDecl.Properties);
        Assert.Null(prop.AvailabilityAnnotations);

        var getter = prop.Accessors.OfType<GetAccessorDecl>().Single();
        Assert.Null(getter.Method.AvailabilityAnnotations);
    }

    [Fact]
    public void ParseModule_FinalClassMethodInheritsClassDispatch()
    {
        // In a final class, individual methods don't need Final attribute —
        // the class-level Final is sufficient for direct dispatch
        var returnTypeNode = CreateNode(kind: "TypeNominal", name: "Void", mangledName: "$s");
        returnTypeNode.PrintedName = "()";

        var funcNode = new Node
        {
            Kind = "Function",
            DeclKind = "Func",
            Name = "fire",
            MangledName = "$s10TestModule12EventHandlerC4fireyyF",
            PrintedName = "fire()",
            ModuleName = "TestModule",
            DeclAttributes = new[] { "AccessControl" }, // no Final on method
            @static = false,
            IsInternal = false,
            GenericSig = null,
            sugared_genericSig = null,
            throwing = false,
            AccessorKind = null,
            EnumRawTypeName = null,
            paramValueOwnership = null,
            hasDefaultArg = null,
            funcSelfKind = null,
            Children = new[] { returnTypeNode },
            Conformances = Array.Empty<Node>(),
            Accessors = Array.Empty<Node>()
        };

        var classNode = CreateNode(
            kind: "TypeDecl",
            declKind: "Class",
            name: "EventHandler",
            mangledName: "$s10TestModule12EventHandlerCN");
        classNode.DeclAttributes = new[] { "Final", "AccessControl" };
        classNode.Children = new[] { funcNode };

        using var fixture = CreateParserWithNodes(classNode);
        var result = fixture.Parser.ParseModule();

        var classDecl = Assert.Single(result.ModuleDecl.Types);
        Assert.True(((ClassDecl)classDecl).IsFinal);
        var method = Assert.Single(classDecl.Methods);
        Assert.False(method.IsFinal); // method itself isn't marked final
        // But the class IS final, so PInvokeEmitter skips Tj
    }

    #endregion

    #region spi_group_names Detection Tests

    [Fact]
    public void ParseModule_TypeWithSpiGroupNames_IsMarkedSpiProtected()
    {
        // Some Swift compiler versions emit spi_group_names instead of SPIAccessControl
        // in declAttributes. The parser must detect both paths.
        var classNode = CreateNode(
            kind: "TypeDecl",
            declKind: "Class",
            name: "STPAPIClient",
            mangledName: "$s10TestModule12STPAPIClientCN");
        classNode.DeclAttributes = new[] { "AccessControl" }; // NO SPIAccessControl
        classNode.spi_group_names = new[] { "STP" };

        using var fixture = CreateParserWithNodes(classNode);
        var result = fixture.Parser.ParseModule();

        var spiType = Assert.Single(result.ModuleDecl.Types);
        Assert.True(spiType.IsSpiProtected, "Type with spi_group_names should have IsSpiProtected = true");
        Assert.True(spiType.IsModuleInternal, "Type with spi_group_names should be marked as module internal");
    }

    [Fact]
    public void ParseModule_TypeWithSpiGroupNames_ButNoSPIAccessControl_IsInternal()
    {
        // Regression test: spi_group_names alone (without SPIAccessControl in declAttributes)
        // must be sufficient to mark a type as SPI-protected
        var classNode = CreateNode(
            kind: "TypeDecl",
            declKind: "Class",
            name: "InternalConfig",
            mangledName: "$s10TestModule14InternalConfigCN");
        classNode.DeclAttributes = new[] { "AccessControl" };
        classNode.spi_group_names = new[] { "Internal" };

        using var fixture = CreateParserWithNodes(classNode);
        var result = fixture.Parser.ParseModule();

        var classType = Assert.Single(result.ModuleDecl.Types);
        Assert.True(classType.IsSpiProtected);
        Assert.True(classType.IsModuleInternal);
    }

    [Fact]
    public void ParseModule_TypeWithEmptySpiGroupNames_IsNotSpiProtected()
    {
        // Empty spi_group_names array should NOT trigger SPI protection
        var classNode = CreateNode(
            kind: "TypeDecl",
            declKind: "Class",
            name: "PublicClient",
            mangledName: "$s10TestModule12PublicClientCN");
        classNode.DeclAttributes = new[] { "AccessControl" };
        classNode.spi_group_names = Array.Empty<string>();

        using var fixture = CreateParserWithNodes(classNode);
        var result = fixture.Parser.ParseModule();

        var pubType = Assert.Single(result.ModuleDecl.Types);
        Assert.False(pubType.IsSpiProtected, "Type with empty spi_group_names should not be SPI protected");
        Assert.False(pubType.IsModuleInternal, "Type with empty spi_group_names should not be internal");
    }

    [Fact]
    public void ParseModule_TypeWithNullSpiGroupNames_IsNotSpiProtected()
    {
        // Null spi_group_names (not present in JSON) should NOT trigger SPI protection
        var classNode = CreateNode(
            kind: "TypeDecl",
            declKind: "Class",
            name: "RegularType",
            mangledName: "$s10TestModule11RegularTypeCN");
        classNode.DeclAttributes = new[] { "AccessControl" };
        // spi_group_names is null by default (not set)

        using var fixture = CreateParserWithNodes(classNode);
        var result = fixture.Parser.ParseModule();

        var regType = Assert.Single(result.ModuleDecl.Types);
        Assert.False(regType.IsSpiProtected);
        Assert.False(regType.IsModuleInternal);
    }

    [Fact]
    public void ParseModule_MethodWithSpiGroupNames_IsMarkedSpiProtected()
    {
        // Methods can also have spi_group_names (e.g., from @_spi extensions)
        var returnTypeNode = CreateNode(kind: "TypeNominal", name: "Void", mangledName: "$s");
        returnTypeNode.PrintedName = "()";

        var funcNode = CreateFunctionNode(
            name: "configure",
            printedName: "configure()",
            funcSelfKind: null,
            children: new[] { returnTypeNode });
        funcNode.DeclAttributes = new[] { "AccessControl" };
        funcNode.spi_group_names = new[] { "STP" };

        using var fixture = CreateParserWithNodes(funcNode);
        var result = fixture.Parser.ParseModule();

        var method = Assert.Single(result.ModuleDecl.Methods);
        Assert.True(method.IsSpiProtected, "Method with spi_group_names should have IsSpiProtected = true");
    }

    #endregion

    #region Underscore-Prefix Internal Suppression Tests

    [Fact]
    public void ParseModule_UnderscorePrefixedMethodWithoutAccessControl_IsModuleInternal()
    {
        // Swift convention: _-prefixed methods without explicit AccessControl are internal.
        // The ABI JSON includes them for binary compat but they're not callable externally.
        var returnTypeNode = CreateNode(kind: "TypeNominal", name: "Void", mangledName: "$s");
        returnTypeNode.PrintedName = "()";

        var funcNode = CreateFunctionNode(
            name: "_convertFromCapitalized",
            printedName: "_convertFromCapitalized()",
            funcSelfKind: null,
            children: new[] { returnTypeNode });
        funcNode.DeclAttributes = Array.Empty<string>(); // no AccessControl

        using var fixture = CreateParserWithNodes(funcNode);
        var result = fixture.Parser.ParseModule();

        var method = Assert.Single(result.ModuleDecl.Methods);
        Assert.True(method.IsModuleInternal,
            "_-prefixed method without AccessControl should be marked as module internal");
    }

    [Fact]
    public void ParseModule_UnderscorePrefixedMethodWithAccessControl_IsNotModuleInternal()
    {
        // Explicitly public _-prefixed APIs (like _NIOFileSystem) have AccessControl
        // and should be preserved — they are intentionally public.
        var returnTypeNode = CreateNode(kind: "TypeNominal", name: "Void", mangledName: "$s");
        returnTypeNode.PrintedName = "()";

        var funcNode = CreateFunctionNode(
            name: "_publicHelper",
            printedName: "_publicHelper()",
            funcSelfKind: null,
            children: new[] { returnTypeNode });
        funcNode.DeclAttributes = new[] { "AccessControl" };

        using var fixture = CreateParserWithNodes(funcNode);
        var result = fixture.Parser.ParseModule();

        var method = Assert.Single(result.ModuleDecl.Methods);
        Assert.False(method.IsModuleInternal,
            "_-prefixed method WITH AccessControl should NOT be marked as internal");
    }

    [Fact]
    public void ParseModule_NonUnderscoreMethodWithoutAccessControl_IsNotModuleInternal()
    {
        // Non-underscore methods should not be affected by the underscore suppression,
        // even if they lack AccessControl (they might be implicitly public).
        var returnTypeNode = CreateNode(kind: "TypeNominal", name: "Void", mangledName: "$s");
        returnTypeNode.PrintedName = "()";

        var funcNode = CreateFunctionNode(
            name: "processData",
            printedName: "processData()",
            funcSelfKind: null,
            children: new[] { returnTypeNode });
        funcNode.DeclAttributes = Array.Empty<string>();

        using var fixture = CreateParserWithNodes(funcNode);
        var result = fixture.Parser.ParseModule();

        var method = Assert.Single(result.ModuleDecl.Methods);
        Assert.False(method.IsModuleInternal,
            "Non-underscore method should not be marked as internal regardless of AccessControl");
    }

    #endregion

    #region Protocol IsClassBound Detection Tests

    [Fact]
    public void ParseModule_ProtocolWithAnyObjectGenericSig_SetsIsClassBound()
    {
        // Mirrors real ABI JSON where ": AnyObject" appears in genericSig,
        // not in conformances (e.g. ExistentialParamDelegate).
        var protocolNode = CreateNode(
            kind: "TypeDecl",
            declKind: "Protocol",
            name: "MyClassBoundProtocol",
            mangledName: "$s10TestModule21MyClassBoundProtocolP",
            genericSig: "<\u03c4_0_0 : AnyObject>");

        using var fixture = CreateParserWithNodes(protocolNode);
        var result = fixture.Parser.ParseModule();

        var protocols = result.ModuleDecl.Types
            .OfType<ProtocolDecl>()
            .ToList();
        var protocol = Assert.Single(protocols);
        Assert.True(protocol.IsClassBound,
            "Protocol with AnyObject in genericSig should be class-bound");
    }

    [Fact]
    public void ParseModule_ProtocolWithoutAnyObject_IsNotClassBound()
    {
        var protocolNode = CreateNode(
            kind: "TypeDecl",
            declKind: "Protocol",
            name: "MyValueProtocol",
            mangledName: "$s10TestModule15MyValueProtocolP",
            genericSig: "<\u03c4_0_0>");

        using var fixture = CreateParserWithNodes(protocolNode);
        var result = fixture.Parser.ParseModule();

        var protocols = result.ModuleDecl.Types
            .OfType<ProtocolDecl>()
            .ToList();
        var protocol = Assert.Single(protocols);
        Assert.False(protocol.IsClassBound,
            "Protocol without AnyObject constraint should not be class-bound");
    }

    [Fact]
    public void ParseModule_ProtocolWithAnyObjectOnAssociatedType_IsNotClassBound()
    {
        // AnyObject constraining an associated type (τ_0_0.Element : AnyObject) does NOT
        // make the protocol itself class-bound. Only τ_0_0 : AnyObject does.
        var protocolNode = CreateNode(
            kind: "TypeDecl",
            declKind: "Protocol",
            name: "CollectionOfClassesProtocol",
            mangledName: "$s10TestModule28CollectionOfClassesProtocolP",
            genericSig: "<\u03c4_0_0 where \u03c4_0_0.Element : AnyObject>");

        using var fixture = CreateParserWithNodes(protocolNode);
        var result = fixture.Parser.ParseModule();

        var protocols = result.ModuleDecl.Types
            .OfType<ProtocolDecl>()
            .ToList();
        var protocol = Assert.Single(protocols);
        Assert.False(protocol.IsClassBound,
            "Protocol with AnyObject on associated type (not Self) should not be class-bound");
    }

    [Fact]
    public void ParseModule_ProtocolWithSelfAndAssociatedAnyObject_IsClassBound()
    {
        // τ_0_0 : AnyObject, τ_0_0.Element : SomeProtocol — Self IS class-bound
        var protocolNode = CreateNode(
            kind: "TypeDecl",
            declKind: "Protocol",
            name: "ClassBoundWithAssocProtocol",
            mangledName: "$s10TestModule28ClassBoundWithAssocProtocolP",
            genericSig: "<\u03c4_0_0 where \u03c4_0_0 : AnyObject, \u03c4_0_0.Element : Swift.Equatable>");

        using var fixture = CreateParserWithNodes(protocolNode);
        var result = fixture.Parser.ParseModule();

        var protocols = result.ModuleDecl.Types
            .OfType<ProtocolDecl>()
            .ToList();
        var protocol = Assert.Single(protocols);
        Assert.True(protocol.IsClassBound,
            "Protocol where Self conforms to AnyObject should be class-bound even with other constraints");
    }

    #endregion

    #region Cross-Module Re-Export Detection Tests

    [Fact]
    public void ParseModule_ThirdPartyReExport_SkipsType()
    {
        // When a type's ModuleName differs from the module being parsed and the
        // source is a third-party module, it should be skipped (e.g., StripeCryptoOnramp
        // re-exporting StripeCore.STPAPIClient).
        var importNode = CreateNode(kind: "Import", moduleName: "TestModule", name: "TestModule");
        var reExportedNode = CreateNode(
            kind: "TypeDecl",
            declKind: "Class",
            name: "STPAPIClient",
            moduleName: "StripePayments",
            mangledName: "$s15StripePayments12STPAPIClientCN");

        using var fixture = CreateParserWithNodes(importNode, reExportedNode);
        var result = fixture.Parser.ParseModule();

        Assert.Empty(result.ModuleDecl.Types);
    }

    [Fact]
    public void ParseModule_SystemModuleReExport_IsKept()
    {
        // Re-exports from Apple/system modules (Swift, Foundation, etc.) should be
        // kept because the generated code legitimately extends or conforms to them.
        var importNode = CreateNode(kind: "Import", moduleName: "TestModule", name: "TestModule");
        var swiftErrorNode = CreateNode(
            kind: "TypeDecl",
            declKind: "Protocol",
            name: "Error",
            moduleName: "Swift",
            mangledName: "$ss5ErrorP");

        using var fixture = CreateParserWithNodes(importNode, swiftErrorNode);
        var result = fixture.Parser.ParseModule();

        Assert.Single(result.ModuleDecl.Protocols);
    }

    [Fact]
    public void ParseModule_TypeWithMatchingModuleName_IsNotSkipped()
    {
        // Types where ModuleName matches the module being parsed should be kept
        var importNode = CreateNode(kind: "Import", moduleName: "TestModule", name: "TestModule");
        var ownNode = CreateNode(
            kind: "TypeDecl",
            declKind: "Class",
            name: "OnrampConfig",
            moduleName: "TestModule",
            mangledName: "$s10TestModule12OnrampConfigCN");

        using var fixture = CreateParserWithNodes(importNode, ownNode);
        var result = fixture.Parser.ParseModule();

        var type = Assert.Single(result.ModuleDecl.Types);
        Assert.Equal("OnrampConfig", type.Name);
    }

    [Fact]
    public void ParseModule_MixOfOwnAndThirdPartyReExports_OnlyKeepsOwnTypes()
    {
        // Module with both own types and third-party re-exports should only bind its own types
        var importNode = CreateNode(kind: "Import", moduleName: "TestModule", name: "TestModule");
        var ownType = CreateNode(
            kind: "TypeDecl",
            declKind: "Class",
            name: "CryptoOnrampView",
            moduleName: "TestModule",
            mangledName: "$s10TestModule16CryptoOnrampViewCN");

        var reExported1 = CreateNode(
            kind: "TypeDecl",
            declKind: "Class",
            name: "STPAPIClient",
            moduleName: "StripePayments",
            mangledName: "$s15StripePayments12STPAPIClientCN");

        var reExported2 = CreateNode(
            kind: "TypeDecl",
            declKind: "Class",
            name: "PaymentIntent",
            moduleName: "StripeCore",
            mangledName: "$s10StripeCore13PaymentIntentCN");

        using var fixture = CreateParserWithNodes(importNode, ownType, reExported1, reExported2);
        var result = fixture.Parser.ParseModule();

        var type = Assert.Single(result.ModuleDecl.Types);
        Assert.Equal("CryptoOnrampView", type.Name);
    }

    [Fact]
    public void ParseModule_SystemModuleReExport_UsesCorrectModuleQualification()
    {
        // When a Swift stdlib type (e.g., KeyPath) appears in another module's ABI,
        // its SwiftTypeName should use the type's actual module (Swift), not the
        // containing module (e.g., RichTextKit). This prevents emitting
        // "extension RichTextKit.KeyPath" which doesn't exist.
        var importNode = CreateNode(kind: "Import", moduleName: "TestModule", name: "TestModule");
        var swiftKeyPathNode = CreateNode(
            kind: "TypeDecl",
            declKind: "Class",
            name: "KeyPath",
            moduleName: "Swift",
            mangledName: "$ss7KeyPathCyxq_G");

        using var fixture = CreateParserWithNodes(importNode, swiftKeyPathNode);
        var result = fixture.Parser.ParseModule();

        var type = Assert.Single(result.ModuleDecl.Types);
        Assert.Equal("Swift.KeyPath", type.SwiftTypeName.ModuleQualifiedName);
    }

    [Fact]
    public void ParseModule_ForeignClass_WithExtensionMembersFromCurrentModule_IsRouted()
    {
        // Stripe STPAPIClient pattern: module B (TestModule) declares
        // `extension ForeignLib.ForeignAPIClient { ... }` — the foreign class
        // re-export now carries extension members. The parser must keep the
        // type (instead of skipping it as a third-party re-export) so the
        // CrossModuleExtensionEmitter has a ClassDecl to dispatch on.
        var importNode = CreateNode(kind: "Import", moduleName: "TestModule", name: "TestModule");

        var extensionMethod = CreateNode(
            kind: "Function",
            declKind: "Func",
            name: "tagged",
            moduleName: "TestModule",
            mangledName: "$s10TestModuleE6taggedSiyF");

        var foreignClass = CreateNode(
            kind: "TypeDecl",
            declKind: "Class",
            name: "ForeignAPIClient",
            moduleName: "ForeignLib",
            mangledName: "$s10ForeignLib16ForeignAPIClientCN",
            children: new[] { extensionMethod });

        using var fixture = CreateParserWithNodes(importNode, foreignClass);
        var result = fixture.Parser.ParseModule();

        var type = Assert.Single(result.ModuleDecl.Types);
        Assert.Equal("ForeignAPIClient", type.Name);
        Assert.Equal("ForeignLib", type.SwiftTypeName.Module);
    }

    [Fact]
    public void ParseModule_ForeignStruct_WithExtensionMembersFromCurrentModule_IsRouted()
    {
        // Phase 2 of the cross-module-extension fix routes Class AND Struct receivers
        // through CrossModuleExtensionEmitter. Struct receivers use @_cdecl trampolines
        // that read self via `assumingMemoryBound(to: T.self).pointee`, so the parser
        // keeps the foreign struct in the current module's Types list when the foreign
        // module's database registered it as a frozen value struct (the only safe shape
        // for the pointee path).
        var typeDatabase = new TypeDatabase();
        var foreignModule = new ModuleTypeDatabase("ForeignLib", "/fake/path");
        var foreignSwiftName = SwiftTypeName.FromModuleQualifiedName("ForeignLib.ForeignValue");
        foreignModule.RegisterType(foreignSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("BindingsGeneration.Tests", "ForeignValue"),
            SwiftTypeName = foreignSwiftName,
            MetadataAccessor = "$s10ForeignLib12ForeignValueVMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct,
        });
        typeDatabase.AddModuleDatabase(foreignModule);

        var importNode = CreateNode(kind: "Import", moduleName: "TestModule", name: "TestModule");

        var extensionMethod = CreateNode(
            kind: "Function",
            declKind: "Func",
            name: "tagged",
            moduleName: "TestModule",
            mangledName: "$s10TestModuleE6taggedSiyF");

        var foreignStruct = CreateNode(
            kind: "TypeDecl",
            declKind: "Struct",
            name: "ForeignValue",
            moduleName: "ForeignLib",
            mangledName: "$s10ForeignLib12ForeignValueV",
            children: new[] { extensionMethod });

        using var fixture = CreateParserWithNodes(typeDatabase, importNode, foreignStruct);
        var result = fixture.Parser.ParseModule();

        var type = Assert.Single(result.ModuleDecl.Types);
        Assert.Equal("ForeignValue", type.Name);
        Assert.Equal("ForeignLib", type.SwiftTypeName.Module);
    }

    [Fact]
    public void ParseModule_ForeignStruct_NonFrozen_IsSkippedAtParser()
    {
        // Non-frozen foreign struct receivers are not yet supported by the cross-module
        // struct trampoline path: `assumingMemoryBound(to: T.self).pointee` is only ABI-safe
        // for frozen value structs. The parser must consult the dependency type database
        // and skip foreign structs that lack the Frozen flag, so the foreign type doesn't
        // get registered in the current module's database with a stub metadata accessor
        // and zero usable members (database pollution).
        //
        // NOTE on unknown-at-probe-time foreign structs: when a non-frozen foreign type
        // has not been registered in the dependency database at parse time, the probe
        // here cannot reject it. That case is tolerated by design — the emitter's own
        // fallback-receiver guard runs later and simply produces zero members for the
        // unsupported shape. This split keeps the parser fast (no eager dependency
        // loading) without giving up correctness.
        var typeDatabase = new TypeDatabase();
        var foreignSwiftName = SwiftTypeName.FromModuleQualifiedName("ForeignLib.ForeignValue");
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (foreignSwiftName, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("BindingsGeneration.Tests", "ForeignValue"),
                SwiftTypeName = foreignSwiftName,
                MetadataAccessor = "$s10ForeignLib12ForeignValueVMa",
                // No Frozen flag — the foreign struct is resilient and must be skipped.
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct,
            }),
        });

        var importNode = CreateNode(kind: "Import", moduleName: "TestModule", name: "TestModule");

        var extensionMethod = CreateNode(
            kind: "Function",
            declKind: "Func",
            name: "tagged",
            moduleName: "TestModule",
            mangledName: "$s10TestModuleE6taggedSiyF");

        var foreignStruct = CreateNode(
            kind: "TypeDecl",
            declKind: "Struct",
            name: "ForeignValue",
            moduleName: "ForeignLib",
            mangledName: "$s10ForeignLib12ForeignValueVN",
            children: new[] { extensionMethod });

        using var fixture = CreateParserWithNodes(typeDatabase, importNode, foreignStruct);
        var result = fixture.Parser.ParseModule();

        Assert.Empty(result.ModuleDecl.Types);
    }

    [Fact]
    public void ParseModule_ForeignStruct_NonTrivial_IsSkippedAtParser()
    {
        // RequiresMemoryManagement is the marker for non-trivial (ARC-bearing) value types.
        // Even a Frozen struct with RequiresMemoryManagement is unsafe for the
        // `.pointee` trampoline path because copying the value across the C ABI without
        // proper retain/release would corrupt the reference count of its stored class
        // payloads. The parser must skip this shape too.
        var typeDatabase = new TypeDatabase();
        var foreignSwiftName = SwiftTypeName.FromModuleQualifiedName("ForeignLib.ForeignValue");
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (foreignSwiftName, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("BindingsGeneration.Tests", "ForeignValue"),
                SwiftTypeName = foreignSwiftName,
                MetadataAccessor = "$s10ForeignLib12ForeignValueVMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct,
            }),
        });

        var importNode = CreateNode(kind: "Import", moduleName: "TestModule", name: "TestModule");

        var extensionMethod = CreateNode(
            kind: "Function",
            declKind: "Func",
            name: "tagged",
            moduleName: "TestModule",
            mangledName: "$s10TestModuleE6taggedSiyF");

        var foreignStruct = CreateNode(
            kind: "TypeDecl",
            declKind: "Struct",
            name: "ForeignValue",
            moduleName: "ForeignLib",
            mangledName: "$s10ForeignLib12ForeignValueVN",
            children: new[] { extensionMethod });

        using var fixture = CreateParserWithNodes(typeDatabase, importNode, foreignStruct);
        var result = fixture.Parser.ParseModule();

        Assert.Empty(result.ModuleDecl.Types);
    }

    [Fact]
    public void ParseModule_ForeignClass_WithoutExtensionMembers_IsStillSkipped()
    {
        // Pure third-party re-export with no extension members: the original
        // skip path still applies (no point binding a foreign type the
        // current module doesn't extend).
        var importNode = CreateNode(kind: "Import", moduleName: "TestModule", name: "TestModule");

        var foreignOnlyMethod = CreateNode(
            kind: "Function",
            declKind: "Func",
            name: "nativeMethod",
            moduleName: "ForeignLib",
            mangledName: "$s10ForeignLib11nativeMethodSiyF");

        var foreignClass = CreateNode(
            kind: "TypeDecl",
            declKind: "Class",
            name: "ForeignAPIClient",
            moduleName: "ForeignLib",
            mangledName: "$s10ForeignLib16ForeignAPIClientCN",
            children: new[] { foreignOnlyMethod });

        using var fixture = CreateParserWithNodes(importNode, foreignClass);
        var result = fixture.Parser.ParseModule();

        Assert.Empty(result.ModuleDecl.Types);
    }

    [Fact]
    public void ParseModule_AppleFrameworkReceiver_WithExtensionMembers_IsRouted()
    {
        // RealityKit → RealityFoundation pattern. RealityFoundation is registered in
        // apple-frameworks.json with `concreteClassFallback: true` (and no other set
        // membership) which makes it `ShouldSuppressDeclaredWrapperImport == true`
        // (wrapper gate) but `IsSystemReexportAllowedModule == false` (parser gate).
        //
        // The parser's children-first restructure must keep this Apple-framework
        // receiver and route it through CrossModuleExtensionEmitter when the foreign
        // node carries current-module extension children — independent of its
        // apple-frameworks.json taxonomy. Without this, RealityKit's extension on
        // RealityFoundation.AccessibilityComponent ends up mis-qualified as
        // `RealityKit.AccessibilityComponent.RotorType` and the generated C# fails
        // to compile (the type does not exist under RealityKit).
        var typeDatabase = new TypeDatabase();
        var foreignSwiftName = SwiftTypeName.FromModuleQualifiedName("RealityFoundation.AccessibilityComponent");
        var foreignModule = new ModuleTypeDatabase("RealityFoundation", "/fake/path");
        foreignModule.RegisterType(foreignSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("RealityFoundation", "AccessibilityComponent"),
            SwiftTypeName = foreignSwiftName,
            MetadataAccessor = "$s17RealityFoundation22AccessibilityComponentVMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct,
        });
        typeDatabase.AddModuleDatabase(foreignModule);

        var importNode = CreateNode(kind: "Import", moduleName: "RealityKit", name: "RealityKit");

        var nestedRotorType = CreateNode(
            kind: "TypeDecl",
            declKind: "Enum",
            name: "RotorType",
            moduleName: "RealityKit",
            mangledName: "$s17RealityFoundation22AccessibilityComponentV0A3KitE9RotorTypeON");

        var foreignReceiver = CreateNode(
            kind: "TypeDecl",
            declKind: "Struct",
            name: "AccessibilityComponent",
            moduleName: "RealityFoundation",
            mangledName: "$s17RealityFoundation22AccessibilityComponentVN",
            children: new[] { nestedRotorType });

        using var fixture = CreateParserWithNodes(typeDatabase, importNode, foreignReceiver);
        var result = fixture.Parser.ParseModule();

        var receiver = Assert.Single(result.ModuleDecl.Types);
        Assert.Equal("AccessibilityComponent", receiver.Name);
        Assert.Equal("RealityFoundation", receiver.SwiftTypeName.Module);
        Assert.Equal("RealityFoundation.AccessibilityComponent", receiver.SwiftTypeName.ModuleQualifiedName);
        // The nested RotorType is attached as a child TypeDecl on the foreign receiver
        // and must carry the canonical RealityFoundation.AccessibilityComponent qualifier.
        // Pre-fix, the receiver's module was incorrectly stamped as RealityKit, which
        // pulled the nested type's qualified name to "RealityKit.AccessibilityComponent.RotorType"
        // and broke the generated C# with two CS0234 references.
        var nested = Assert.Single(receiver.Types);
        Assert.Equal("RotorType", nested.Name);
        Assert.Equal("RealityFoundation.AccessibilityComponent.RotorType", nested.SwiftTypeName.ModuleQualifiedName);
    }

    [Fact]
    public void ParseModule_AppleFrameworkReceiver_WithoutExtensionMembers_IsSkipped()
    {
        // RealityFoundation-shaped Apple framework (concreteClassFallback only,
        // NOT in IsSystemReexportAllowedModule) appearing as a pure re-export with
        // no current-module children. Falls through both branches of the foreign
        // handler: not a cross-module extension (no children) and not in the
        // parser keep-list (not a system re-export). Should be skipped, matching
        // pre-7c38c3e2 behavior — the dep DB still resolves any references.
        var importNode = CreateNode(kind: "Import", moduleName: "RealityKit", name: "RealityKit");

        var foreignOnlyMethod = CreateNode(
            kind: "Function",
            declKind: "Func",
            name: "nativeMethod",
            moduleName: "RealityFoundation",
            mangledName: "$s17RealityFoundation12nativeMethodSiyF");

        var foreignReceiver = CreateNode(
            kind: "TypeDecl",
            declKind: "Struct",
            name: "AccessibilityComponent",
            moduleName: "RealityFoundation",
            mangledName: "$s17RealityFoundation22AccessibilityComponentVN",
            children: new[] { foreignOnlyMethod });

        using var fixture = CreateParserWithNodes(importNode, foreignReceiver);
        var result = fixture.Parser.ParseModule();

        Assert.Empty(result.ModuleDecl.Types);
    }

    [Fact]
    public void ParseModule_DuplicateCrossModuleReExport_SkipsGracefully()
    {
        // When the same system type appears twice in a module's ABI (e.g., two extension
        // blocks on Swift.KeyPath), the second occurrence should be skipped instead of
        // throwing "Type already processed".
        var importNode = CreateNode(kind: "Import", moduleName: "TestModule", name: "TestModule");
        var firstKeyPath = CreateNode(
            kind: "TypeDecl",
            declKind: "Class",
            name: "KeyPath",
            moduleName: "Swift",
            mangledName: "$ss7KeyPathCyxq_G");
        var secondKeyPath = CreateNode(
            kind: "TypeDecl",
            declKind: "Class",
            name: "KeyPath",
            moduleName: "Swift",
            mangledName: "$ss7KeyPathCyxq_G_dup");

        using var fixture = CreateParserWithNodes(importNode, firstKeyPath, secondKeyPath);
        var result = fixture.Parser.ParseModule();

        // Only the first occurrence should be kept
        var type = Assert.Single(result.ModuleDecl.Types);
        Assert.Equal("Swift.KeyPath", type.SwiftTypeName.ModuleQualifiedName);
    }

    #endregion

    #region Variadic detection (per-overload, not name-keyed)

    // Two PageBuilder.buildBlock overloads share the printedName "buildBlock(_:)" but
    // differ only in variadic-ness:
    //     buildBlock(_ components: [Page])      // not variadic
    //     buildBlock(_ components: [Page]...)   // variadic
    // The variadic flag must come from the per-overload ABI signature, not a name-keyed
    // VariadicMembers fact — keying on the shared name marks BOTH variadic and emits an
    // invalid "[Page] as variadic" cast on the non-variadic sibling.

    [Fact]
    public void ParseModule_VariadicArrayParam_FlaggedFromSignature()
    {
        var func = CreateBuildBlockNode(variadic: true);

        using var fixture = CreateParserWithNodes(func);
        var result = fixture.Parser.ParseModule();

        var method = Assert.Single(result.ModuleDecl.Methods);
        Assert.True(method.HasVariadicParameter);
    }

    [Fact]
    public void ParseModule_NonVariadicArraySibling_NotFlaggedEvenWhenNameFactPresent()
    {
        // Regression guard: the name fact names the shared printedName, but the
        // non-variadic overload has an inspectable (non-variadic) array param, so the
        // per-overload signature wins and the name fact must NOT over-fire.
        var func = CreateBuildBlockNode(variadic: false);
        var facts = SwiftInterfaceFacts.Empty with
        {
            VariadicMembers = new HashSet<string> { "buildBlock(_:)" }
        };

        using var fixture = CreateParserWithFacts(facts, func);
        var result = fixture.Parser.ParseModule();

        var method = Assert.Single(result.ModuleDecl.Methods);
        Assert.False(method.HasVariadicParameter);
    }

    [Fact]
    public void ParseModule_VariadicArrayParam_FlaggedWhenNameFactAlsoPresent()
    {
        var func = CreateBuildBlockNode(variadic: true);
        var facts = SwiftInterfaceFacts.Empty with
        {
            VariadicMembers = new HashSet<string> { "buildBlock(_:)" }
        };

        using var fixture = CreateParserWithFacts(facts, func);
        var result = fixture.Parser.ParseModule();

        var method = Assert.Single(result.ModuleDecl.Methods);
        Assert.True(method.HasVariadicParameter);
    }

    // Builds a free function `buildBlock(_:)` returning Void whose single parameter is
    // either `[Page]` (variadic:false) or the variadic `[Page]...` (variadic:true), as
    // swift-api-digester renders them: an Array TypeNominal whose printedName carries a
    // trailing "..." for the variadic case.
    private static Node CreateBuildBlockNode(bool variadic)
    {
        var returnTypeNode = CreateNode(kind: "TypeNominal", name: "Void", mangledName: "$s");
        returnTypeNode.PrintedName = "()";

        var element = CreateNode(kind: "TypeNominal", name: "Page", moduleName: "TestModule", mangledName: "$s");
        element.PrintedName = "TestModule.Page";

        var arrayParam = CreateNode(kind: "TypeNominal", name: "Array", mangledName: "$s", children: new[] { element });
        arrayParam.PrintedName = "[TestModule.Page]";

        Node paramNode = arrayParam;
        if (variadic)
        {
            // Variadic of arrays: `[Page]...` lowers to Array<Array<Page>> with the
            // outer Array's printedName ending in "...".
            var variadicArray = CreateNode(kind: "TypeNominal", name: "Array", mangledName: "$s", children: new[] { arrayParam });
            variadicArray.PrintedName = "[TestModule.Page]...";
            paramNode = variadicArray;
        }

        return CreateFunctionNode(
            name: "buildBlock",
            printedName: "buildBlock(_:)",
            funcSelfKind: null,
            children: new[] { returnTypeNode, paramNode });
    }

    #endregion

    private static ParserFixture CreateParserWithFacts(SwiftInterfaceFacts facts, params Node[] nodes)
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
            facts);

        return new ParserFixture(parser, filePath);
    }

    private static ParserFixture CreateParserWithNodes(params Node[] nodes)
    {
        return CreateParserWithNodes(availabilityAnnotations: null, typeDatabase: null, nodes);
    }

    private static ParserFixture CreateParserWithNodes(
        TypeDatabase typeDatabase,
        params Node[] nodes)
    {
        return CreateParserWithNodes(availabilityAnnotations: null, typeDatabase, nodes);
    }

    private static ParserFixture CreateParserWithNodes(
        Dictionary<string, List<AvailabilityAnnotation>>? availabilityAnnotations,
        params Node[] nodes)
    {
        return CreateParserWithNodes(availabilityAnnotations, typeDatabase: null, nodes);
    }

    private static ParserFixture CreateParserWithNodes(
        Dictionary<string, List<AvailabilityAnnotation>>? availabilityAnnotations,
        TypeDatabase? typeDatabase,
        params Node[] nodes)
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

        var facts = availabilityAnnotations is null
            ? SwiftInterfaceFacts.Empty
            : SwiftInterfaceFacts.Empty with { AvailabilityAnnotations = availabilityAnnotations };

        var parser = new SwiftABIParser(
            filePath,
            typeDatabase ?? new TypeDatabase(),
            CreateEmptyDemanglingResults(),
            NullLogger.Instance,
            facts);

        return new ParserFixture(parser, filePath);
    }

    private sealed class ParserFixture : IDisposable
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

    private static DemanglingResults CreateEmptyDemanglingResults()
    {
        var ctor = typeof(DemanglingResults).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            [typeof(IReduction[]), typeof(HashSet<string>)],
            modifiers: null)!;

        return (DemanglingResults)ctor.Invoke([Array.Empty<IReduction>(), null]);
    }

    private static Node CreateNode(
        string kind,
        string declKind = "",
        string name = "",
        string moduleName = "TestModule",
        string mangledName = "$s",
        IEnumerable<Node>? children = null,
        string[]? declAttributes = null,
        string? genericSig = null)
    {
        return new Node
        {
            Kind = kind,
            DeclKind = declKind,
            Name = name,
            MangledName = mangledName,
            PrintedName = name,
            ModuleName = moduleName,
            DeclAttributes = declAttributes ?? [],
            @static = false,
            IsInternal = false,
            GenericSig = genericSig,
            sugared_genericSig = null,
            throwing = false,
            AccessorKind = null,
            EnumRawTypeName = null,
            paramValueOwnership = null,
            hasDefaultArg = null,
            Children = children ?? [],
            Conformances = [],
            Accessors = []
        };
    }

    private static Node CreateFunctionNode(
        string name,
        string printedName,
        string? funcSelfKind,
        IEnumerable<Node>? children = null)
    {
        return new Node
        {
            Kind = "Function",
            DeclKind = "Func",
            Name = name,
            MangledName = $"$s10TestModule{name.Length}{name}yyF",
            PrintedName = printedName,
            ModuleName = "TestModule",
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
            funcSelfKind = funcSelfKind,
            Children = children ?? [],
            Conformances = [],
            Accessors = []
        };
    }
}
