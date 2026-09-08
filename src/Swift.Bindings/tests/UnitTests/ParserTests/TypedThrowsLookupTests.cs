// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using BindingsGeneration.Demangling;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>Exercises facts through the actual ABI parser, including sanitized owner names.</summary>
public class TypedThrowsLookupTests
{
    [Theory]
    [InlineData("Worker")]
    [InlineData("event")]
    [InlineData("event.Inner")]
    [InlineData("Outer.event")]
    [InlineData("event.class")]
    public void TypedThrows_UsesSwiftSpellingForEveryOwnerComponent(string owner)
    {
        var result = Parse(owner, new Dictionary<string, string>
        {
            [$"{owner}.work(_:)"] = "TestModule.MemberError",
            ["work(_:)"] = "TestModule.FreeError"
        });
        Assert.Equal("TestModule.MemberError", Leaf(result).Methods.Single(m => m.Name == "work").ThrownErrorType?.ToString());
        Assert.Equal("TestModule.FreeError", result.ModuleDecl.Methods.Single().ThrownErrorType?.ToString());
    }

    [Fact]
    public void TypedThrows_OverloadsPreferSignatureThenOwnerBareKey()
    {
        var result = Parse("Worker", new Dictionary<string, string>
        {
            ["Worker.work(_:)|Int32"] = "TestModule.IntError",
            ["Worker.work(_:)|String"] = "TestModule.StringError",
            ["Worker.work(_:)"] = "TestModule.BareError"
        }, overloads: true);
        var methods = Leaf(result).Methods;
        Assert.Equal("TestModule.IntError", methods.Single(m => m.CSSignature[1].SwiftTypeSpec.ToString() == "Swift.Int32").ThrownErrorType?.ToString());
        Assert.Equal("TestModule.StringError", methods.Single(m => m.CSSignature[1].SwiftTypeSpec.ToString() == "Swift.String").ThrownErrorType?.ToString());
        // The unmatched signature uses only its owner-specific bare refinement.
        Assert.Equal("TestModule.BareError", methods.Single(m => m.CSSignature[1].SwiftTypeSpec.ToString() == "Swift.Bool").ThrownErrorType?.ToString());
    }

    [Fact]
    public void TypedThrows_MissingMemberFactNeverBorrowsSameNameFreeFunction()
    {
        var result = Parse("Owner.Inner", new Dictionary<string, string>
        {
            ["work(_:)"] = "TestModule.FreeError",
            ["Other.Inner.work(_:)"] = "TestModule.OtherError",
            ["Owner.Inner.work(_:)|String"] = "TestModule.StringError"
        });
        Assert.Null(Leaf(result).Methods.Single().ThrownErrorType);
        Assert.Equal("TestModule.FreeError", result.ModuleDecl.Methods.Single().ThrownErrorType?.ToString());
    }

    private static TypeDecl Leaf(ModuleParsingResult result)
    {
        var type = result.ModuleDecl.Types.Single();
        while (type.Types.Count != 0) type = type.Types.Single();
        return type;
    }

    private static ModuleParsingResult Parse(string owner, Dictionary<string, string> typedErrors, bool overloads = false)
    {
        var components = owner.Split('.');
        var prefix = "$s10TestModule";
        Node rootType = null;
        Node parent = null;
        foreach (var component in components)
        {
            prefix += component.Length + component + "V";
            var node = Node("TypeDecl", component, prefix, "Struct");
            if (parent != null) parent.Children = new[] { node };
            else rootType = node;
            parent = node;
        }
        parent.Children = (overloads ? new[] { "Int32", "String", "Bool" } : new[] { "Int32" })
            .Select(parameter => Function(prefix, parameter)).ToArray();
        var root = new ABIRootNode
        {
            ABIRoot = new RootNode
            {
                Kind = "Root", Name = "Root", PrintedName = "Root",
                Children = new[] { Node("Import", "TestModule", "$s"), rootType, Function("$s10TestModule", "Int32") }
            }
        };
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, JsonConvert.SerializeObject(root));
            var constructor = typeof(DemanglingResults).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null, new[] { typeof(IReduction[]), typeof(HashSet<string>) }, modifiers: null);
            var demangling = (DemanglingResults)constructor.Invoke(new object[] { Array.Empty<IReduction>(), new HashSet<string>() });
            return new SwiftABIParser(path, new TypeDatabase(), demangling, NullLogger<SwiftABIParser>.Instance,
                SwiftInterfaceFacts.Empty with { TypedThrowsErrors = typedErrors }).ParseModule();
        }
        finally { File.Delete(path); }
    }

    private static Node Function(string parentMangle, string parameter)
    {
        var parameterMangle = parameter switch { "Int32" => "s5Int32V", "String" => "SS", _ => "Sb" };
        var method = Node("Function", "work", parentMangle + "4workys5Int32V" + parameterMangle + "KF", "Func");
        method.PrintedName = "work(_:)";
        method.throwing = true;
        method.Children = new[] { Nominal("Int32"), Nominal(parameter) };
        return method;
    }

    private static Node Nominal(string name)
    {
        var node = Node("TypeNominal", name, "$s", module: "Swift");
        node.PrintedName = "Swift." + name;
        return node;
    }

    private static Node Node(string kind, string name, string mangled, string declKind = "", string module = "TestModule") => new()
    {
        Kind = kind, DeclKind = declKind, Name = name, PrintedName = name, MangledName = mangled,
        ModuleName = module, DeclAttributes = new[] { "AccessControl" }, @static = false,
        IsInternal = false, GenericSig = null, sugared_genericSig = null, throwing = false,
        AccessorKind = null, EnumRawTypeName = null, paramValueOwnership = null, hasDefaultArg = null,
        Children = Array.Empty<Node>(), Conformances = Array.Empty<Node>(), Accessors = Array.Empty<Node>()
    };
}
