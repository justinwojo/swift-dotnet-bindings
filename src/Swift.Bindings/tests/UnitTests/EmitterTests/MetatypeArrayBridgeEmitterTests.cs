// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

public class MetatypeArrayBridgeEmitterTests
{
    [Fact]
    public void IsEligible_FreeFunctionWithMetatypeArray_ReturnsTrue()
    {
        var moduleDecl = CreateModule();
        var method = CreateFreeFunction("joinSearchableKinds",
            returnType: new NamedTypeSpec("Swift.String"),
            parameters: new[] { MetatypeArrayParam("types") },
            moduleDecl);

        Assert.True(MetatypeArrayBridgeEmitter.IsEligible(method));
    }

    [Fact]
    public void IsEligible_InstanceMethodWithMetatypeArray_ReturnsFalse()
    {
        var moduleDecl = CreateModule();
        var classDecl = CreateClass("Container", moduleDecl);
        var method = CreateInstanceMethod("accepts", classDecl, moduleDecl,
            parameters: new[] { MetatypeArrayParam("types") });

        Assert.False(MetatypeArrayBridgeEmitter.IsEligible(method));
    }

    [Fact]
    public void IsEligible_ConstructorWithMetatypeArray_ReturnsFalse()
    {
        var moduleDecl = CreateModule();
        var classDecl = CreateClass("Container", moduleDecl);
        var ctor = CreateInstanceMethod("init", classDecl, moduleDecl,
            parameters: new[] { MetatypeArrayParam("types") });
        ctor.IsConstructor = true;

        Assert.False(MetatypeArrayBridgeEmitter.IsEligible(ctor));
    }

    [Fact]
    public void IsEligible_ThrowingFreeFunction_ReturnsFalse()
    {
        var moduleDecl = CreateModule();
        var method = CreateFreeFunction("throws",
            returnType: new NamedTypeSpec("Swift.String"),
            parameters: new[] { MetatypeArrayParam("types") },
            moduleDecl);
        method.Throws = true;

        Assert.False(MetatypeArrayBridgeEmitter.IsEligible(method));
    }

    [Fact]
    public void IsEligible_NoMetatypeArrayParam_ReturnsFalse()
    {
        var moduleDecl = CreateModule();
        var method = CreateFreeFunction("plain",
            returnType: new NamedTypeSpec("Swift.Int"),
            parameters: new[] { IntParam("x") },
            moduleDecl);

        Assert.False(MetatypeArrayBridgeEmitter.IsEligible(method));
    }

    [Fact]
    public void NormalizeMethodDecl_ReplacesMetatypeArrayWithPtrAndCount()
    {
        var moduleDecl = CreateModule();
        var method = CreateFreeFunction("joinSearchableKinds",
            returnType: new NamedTypeSpec("Swift.String"),
            parameters: new[] { MetatypeArrayParam("types") },
            moduleDecl);

        var normalized = MetatypeArrayBridgeEmitter.NormalizeMethodDecl(method);

        // Return slot at [0] + 2 normalized scalar args
        Assert.Equal(3, normalized.CSSignature.Count);

        var ptrArg = normalized.CSSignature[1];
        Assert.Equal("typesPtr", ptrArg.Name);
        Assert.Equal("Swift.UnsafeRawPointer", ptrArg.SwiftTypeSpec.ToString());

        var countArg = normalized.CSSignature[2];
        Assert.Equal("typesCount", countArg.Name);
        Assert.Equal("Swift.Int", countArg.SwiftTypeSpec.ToString());
    }

    [Fact]
    public void NormalizeMethodDecl_PreservesNonMetatypeArgs()
    {
        var moduleDecl = CreateModule();
        var method = CreateFreeFunction("mixed",
            returnType: new NamedTypeSpec("Swift.Int"),
            parameters: new[] { IntParam("count"), MetatypeArrayParam("types") },
            moduleDecl);

        var normalized = MetatypeArrayBridgeEmitter.NormalizeMethodDecl(method);

        // Return + count (passthrough) + typesPtr + typesCount
        Assert.Equal(4, normalized.CSSignature.Count);
        Assert.Equal("count", normalized.CSSignature[1].Name);
        Assert.Equal("Swift.Int", normalized.CSSignature[1].SwiftTypeSpec.ToString());
        Assert.Equal("typesPtr", normalized.CSSignature[2].Name);
        Assert.Equal("typesCount", normalized.CSSignature[3].Name);
    }

    // ---- Helpers ----

    private static ModuleDecl CreateModule() => new ModuleDecl
    {
        Name = "TestModule",
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Dependencies = new List<string>(),
        Protocols = new List<ProtocolDecl>(),
        ParentDecl = null,
        ModuleDecl = null,
    };

    private static ClassDecl CreateClass(string name, ModuleDecl moduleDecl)
    {
        var decl = new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}CN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };
        moduleDecl.Types.Add(decl);
        return decl;
    }

    private static MethodDecl CreateFreeFunction(
        string name,
        TypeSpec returnType,
        IEnumerable<ArgumentDecl> parameters,
        ModuleDecl moduleDecl)
    {
        var sig = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = returnType,
                Name = "",
                PrivateName = "",
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = null,
                ModuleDecl = moduleDecl,
            }
        };
        sig.AddRange(parameters);

        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule_{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = sig,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false,
        };
    }

    private static MethodDecl CreateInstanceMethod(
        string name,
        TypeDecl parentDecl,
        ModuleDecl moduleDecl,
        IEnumerable<ArgumentDecl> parameters)
    {
        var sig = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = TupleTypeSpec.Empty,
                Name = "",
                PrivateName = "",
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = null,
                ModuleDecl = moduleDecl,
            }
        };
        sig.AddRange(parameters);

        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule_{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = sig,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false,
        };
    }

    private static ArgumentDecl MetatypeArrayParam(string name)
    {
        // "Swift.Array<SwiftBindingsTestLib.SearchableItem.Type>" with the element
        // marked as `any` (existential). BoundGenericsHandler.IsArrayOfExistentialMetatypes
        // looks for Name ending in ".Type" on an existential element, and verifies a
        // conformer hint exists — SwiftBindingsTestLib.SearchableItem is listed in
        // Data/specialization-hints.json.
        var element = new NamedTypeSpec("SwiftBindingsTestLib.SearchableItem.Type") { IsAny = true };
        var spec = new NamedTypeSpec("Swift.Array", element);
        return new ArgumentDecl
        {
            SwiftTypeSpec = spec,
            Name = name,
            PrivateName = name,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null,
        };
    }

    private static ArgumentDecl IntParam(string name) => new ArgumentDecl
    {
        SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
        Name = name,
        PrivateName = name,
        IsInOut = false,
        IsGeneric = false,
        ParentDecl = null,
        ModuleDecl = null,
    };
}
