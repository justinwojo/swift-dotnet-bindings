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
/// Tests that superclass data from ABI JSON is correctly parsed into ClassDecl.
/// </summary>
public class ClassInheritanceParserTests
{
    [Fact]
    public void ParseModule_ClassWithSuperclass_ParsesSuperclassUsr()
    {
        var classNode = CreateNode(kind: "TypeDecl", declKind: "Class", name: "DataRequest",
            mangledName: "$s9Alamofire11DataRequestCN");
        classNode.superclassUsr = "s:9Alamofire7RequestC";
        classNode.superclassNames = new[] { "Alamofire.Request" };

        using var fixture = CreateParserWithNodes(classNode);
        var result = fixture.Parser.ParseModule();

        var classDecl = Assert.Single(result.ModuleDecl.Types);
        var cls = Assert.IsType<ClassDecl>(classDecl);
        Assert.Equal("s:9Alamofire7RequestC", cls.SuperclassUsr);
    }

    [Fact]
    public void ParseModule_ClassWithSuperclassChain_ParsesSuperclassNames()
    {
        var classNode = CreateNode(kind: "TypeDecl", declKind: "Class", name: "UploadRequest",
            mangledName: "$s9Alamofire13UploadRequestCN");
        classNode.superclassUsr = "s:9Alamofire11DataRequestC";
        classNode.superclassNames = new[] { "Alamofire.DataRequest", "Alamofire.Request" };

        using var fixture = CreateParserWithNodes(classNode);
        var result = fixture.Parser.ParseModule();

        var cls = (ClassDecl)result.ModuleDecl.Types.Single();
        Assert.Equal(2, cls.SuperclassNames.Count);
        Assert.Equal("Alamofire.DataRequest", cls.SuperclassNames[0]);
        Assert.Equal("Alamofire.Request", cls.SuperclassNames[1]);
    }

    [Fact]
    public void ParseModule_ClassWithSuperclass_DirectSuperclassNameReturnsFirst()
    {
        var classNode = CreateNode(kind: "TypeDecl", declKind: "Class", name: "UploadRequest",
            mangledName: "$s9Alamofire13UploadRequestCN");
        classNode.superclassUsr = "s:9Alamofire11DataRequestC";
        classNode.superclassNames = new[] { "Alamofire.DataRequest", "Alamofire.Request" };

        using var fixture = CreateParserWithNodes(classNode);
        var result = fixture.Parser.ParseModule();

        var cls = (ClassDecl)result.ModuleDecl.Types.Single();
        Assert.Equal("Alamofire.DataRequest", cls.DirectSuperclassName);
    }

    [Fact]
    public void ParseModule_RootClass_HasNullSuperclassUsr()
    {
        var classNode = CreateNode(kind: "TypeDecl", declKind: "Class", name: "Request",
            mangledName: "$s9Alamofire7RequestCN");
        // No superclass fields set

        using var fixture = CreateParserWithNodes(classNode);
        var result = fixture.Parser.ParseModule();

        var cls = (ClassDecl)result.ModuleDecl.Types.Single();
        Assert.Null(cls.SuperclassUsr);
        Assert.Empty(cls.SuperclassNames);
        Assert.Null(cls.DirectSuperclassName);
    }

    [Fact]
    public void ParseModule_ObjCDerivedClass_ParsesObjCUsr()
    {
        var classNode = CreateNode(kind: "TypeDecl", declKind: "Class", name: "SessionDelegate",
            mangledName: "$s9Alamofire15SessionDelegateCN");
        classNode.superclassUsr = "c:objc(cs)NSObject";
        classNode.superclassNames = new[] { "ObjectiveC.NSObject" };

        using var fixture = CreateParserWithNodes(classNode);
        var result = fixture.Parser.ParseModule();

        var cls = (ClassDecl)result.ModuleDecl.Types.Single();
        Assert.Equal("c:objc(cs)NSObject", cls.SuperclassUsr);
        Assert.Equal("ObjectiveC.NSObject", cls.DirectSuperclassName);
    }

    [Fact]
    public void ParseModule_ClassWithInheritsConvenienceInitializers_ParsesFlag()
    {
        var classNode = CreateNode(kind: "TypeDecl", declKind: "Class", name: "MyClass",
            mangledName: "$s10TestModule7MyClassCN");
        classNode.inheritsConvenienceInitializers = true;

        using var fixture = CreateParserWithNodes(classNode);
        var result = fixture.Parser.ParseModule();

        var cls = (ClassDecl)result.ModuleDecl.Types.Single();
        Assert.True(cls.InheritsConvenienceInitializers);
    }

    [Fact]
    public void ParseModule_ClassWithHasMissingDesignatedInitializers_ParsesFlag()
    {
        var classNode = CreateNode(kind: "TypeDecl", declKind: "Class", name: "MyClass",
            mangledName: "$s10TestModule7MyClassCN");
        classNode.hasMissingDesignatedInitializers = true;

        using var fixture = CreateParserWithNodes(classNode);
        var result = fixture.Parser.ParseModule();

        var cls = (ClassDecl)result.ModuleDecl.Types.Single();
        Assert.True(cls.HasMissingDesignatedInitializers);
    }

    [Fact]
    public void ParseModule_ClassWithoutInitializerFlags_DefaultsFalse()
    {
        var classNode = CreateNode(kind: "TypeDecl", declKind: "Class", name: "MyClass",
            mangledName: "$s10TestModule7MyClassCN");
        // No initializer flags set

        using var fixture = CreateParserWithNodes(classNode);
        var result = fixture.Parser.ParseModule();

        var cls = (ClassDecl)result.ModuleDecl.Types.Single();
        Assert.False(cls.InheritsConvenienceInitializers);
        Assert.False(cls.HasMissingDesignatedInitializers);
    }

    #region Test Helpers

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
            NullLogger.Instance);

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

    private static Node CreateNode(
        string kind,
        string declKind = "",
        string name = "",
        string moduleName = "TestModule",
        string mangledName = "$s",
        IEnumerable<Node>? children = null)
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
            Children = children ?? [],
            Conformances = [],
            Accessors = []
        };
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
