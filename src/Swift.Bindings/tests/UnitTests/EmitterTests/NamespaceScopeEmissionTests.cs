// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.IO;
using BindingsGeneration;
using Xunit;

namespace BindingsGeneration.Tests;

public class NamespaceScopeEmissionTests
{
    private static (ClassDecl Outer, ClassDecl Inner) NestedPair()
    {
        var module = new ModuleDecl
        {
            Name = "TestModule",
            Dependencies = new List<string>(),
            Types = new List<TypeDecl>(),
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
        ClassDecl Make(string name, BaseDecl parent) => new()
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
            ParentDecl = parent,
            ModuleDecl = module
        };
        var outer = Make("Outer", module);
        var inner = Make("Inner", outer);
        outer.Types.Add(inner);
        module.Types.Add(outer);
        return (outer, inner);
    }

    // An outermost type that leaves its handler early never reaches its own drain; the module
    // end must still write what its nested types queued, or the extension class is lost.
    [Fact]
    public void NestedEmission_QueuedPastAnOutermostTypeThatNeverDrained_IsWrittenAtModuleEnd()
    {
        var (_, inner) = NestedPair();
        var csText = new StringWriter();
        var csWriter = new CSharpWriter(csText);
        var swiftWriter = new SwiftWriter(new StringWriter());
        var context = new TypeHandlerContext(null, new(), null, EmissionContext: new ModuleEmissionContext(),
            DeferredNamespaceScopeEmissions: new());

        NamespaceScopeEmission.EmitOrDefer(csWriter, swiftWriter, inner, context,
            w => w.WriteLine("public static class InnerExtensions { }"));
        Assert.DoesNotContain("InnerExtensions", csText.ToString());

        // A nested type's own drain call leaves the queue for its outermost type.
        NamespaceScopeEmission.DrainAfterOutermost(csWriter, inner, context);
        Assert.DoesNotContain("InnerExtensions", csText.ToString());

        NamespaceScopeEmission.DrainAtModuleEnd(csWriter, context);
        Assert.Contains("public static class InnerExtensions { }", csText.ToString());
        Assert.Empty(context.DeferredNamespaceScopeEmissions!);
    }
}
