// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for ProtocolExtensionClosureBridge — closure bridging in protocol extensions.
/// Tests TryEmit gate logic and output patterns for various closure shapes.
/// </summary>
public class ProtocolExtensionClosureBridgeTests
{
    #region TryEmit gate: non-protocol-extension returns false

    [Fact]
    public void TryEmit_NonProtocolExtension_ReturnsFalse()
    {
        var (csWriter, swiftWriter, output) = CreateWriters();
        var method = CreateMethodDecl("doWork", isProtocolExtension: false);
        var env = CreateMethodEnvironment(method);

        var result = ProtocolExtensionClosureBridge.TryEmit(csWriter, swiftWriter, env, null);

        Assert.False(result);
        Assert.Empty(output.ToString());
    }

    #endregion

    #region TryEmit gate: no closure parameter returns false

    [Fact]
    public void TryEmit_NoClosure_ReturnsFalse()
    {
        var (csWriter, swiftWriter, output) = CreateWriters();
        var method = CreateMethodDecl("doWork", isProtocolExtension: true);
        // CSSignature has return + non-closure arg only
        var env = CreateMethodEnvironment(method);

        var result = ProtocolExtensionClosureBridge.TryEmit(csWriter, swiftWriter, env, null);

        Assert.False(result);
    }

    #endregion

    #region TryEmit: void closure emits bridge

    [Fact]
    public void TryEmit_VoidClosure_EmitsCallback()
    {
        var (csWriter, swiftWriter, csOutput) = CreateWriters();
        var method = CreateProtocolExtMethodWithClosure(
            "forEach",
            new ClosureTypeSpec(new NamedTypeSpec("Swift.Int"), TupleTypeSpec.Empty));
        var env = CreateMethodEnvironment(method);
        var parentDecl = CreateClassDecl("MyProtocol");

        var handled = ProtocolExtensionClosureBridge.TryEmit(csWriter, swiftWriter, env, parentDecl);

        Assert.True(handled);
        var result = csOutput.ToString();

        // Should emit [UnmanagedCallersOnly] callback
        Assert.Contains("[UnmanagedCallersOnly", result);
        Assert.Contains("CallConvCdecl", result);
        Assert.Contains("PExtCB_", result);

        // Should emit function pointer field
        Assert.Contains("s_PExtCB_", result);
        Assert.Contains("delegate* unmanaged[Cdecl]", result);

        // Should emit P/Invoke
        Assert.Contains("LibraryImport", result);

        // Should emit public method with Action<> parameter
        Assert.Contains("public unsafe void ForEach", result);
        Assert.Contains("Action<", result);

        // Should set WasEmitted
        Assert.True(method.WasEmitted);
    }

    #endregion

    #region TryEmit: bool-returning closure

    [Fact]
    public void TryEmit_BoolReturnClosure_EmitsByteReturn()
    {
        var (csWriter, swiftWriter, csOutput) = CreateWriters();
        var method = CreateProtocolExtMethodWithClosure(
            "filter",
            new ClosureTypeSpec(new NamedTypeSpec("Swift.Int"), new NamedTypeSpec("Swift.Bool")));
        var env = CreateMethodEnvironment(method);
        var parentDecl = CreateClassDecl("MyProtocol");

        var handled = ProtocolExtensionClosureBridge.TryEmit(csWriter, swiftWriter, env, parentDecl);

        Assert.True(handled);
        var result = csOutput.ToString();

        // Bool return closure callback should return byte
        Assert.Contains("byte", result);
        Assert.Contains("Func<", result);
        Assert.Contains("bool", result);
    }

    #endregion

    #region TryEmit: zero-arg closure

    [Fact]
    public void TryEmit_ZeroArgVoidClosure_EmitsAction()
    {
        var (csWriter, swiftWriter, csOutput) = CreateWriters();
        var method = CreateProtocolExtMethodWithClosure(
            "onComplete",
            new ClosureTypeSpec(null, TupleTypeSpec.Empty));
        var env = CreateMethodEnvironment(method);
        var parentDecl = CreateClassDecl("MyProtocol");

        var handled = ProtocolExtensionClosureBridge.TryEmit(csWriter, swiftWriter, env, parentDecl);

        Assert.True(handled);
        var result = csOutput.ToString();

        // Zero-arg void closure → Action (not Action<>)
        Assert.Contains("Action __inner = () =>", result);
    }

    #endregion

    #region Output patterns: GCHandle allocation

    [Fact]
    public void TryEmit_EmitsGCHandleAllocation()
    {
        var (csWriter, swiftWriter, csOutput) = CreateWriters();
        var method = CreateProtocolExtMethodWithClosure(
            "process",
            new ClosureTypeSpec(new NamedTypeSpec("Swift.Int"), TupleTypeSpec.Empty));
        var env = CreateMethodEnvironment(method);
        var parentDecl = CreateClassDecl("MyProtocol");

        ProtocolExtensionClosureBridge.TryEmit(csWriter, swiftWriter, env, parentDecl);

        var result = csOutput.ToString();
        Assert.Contains("GCHandle.Alloc", result);
        Assert.Contains("GCHandle.ToIntPtr", result);
        Assert.Contains("Payload.DangerousGetHandle()", result);
    }

    #endregion

    #region Output patterns: self_ parameter in P/Invoke

    [Fact]
    public void TryEmit_PInvokeHasSelfFirst()
    {
        var (csWriter, swiftWriter, csOutput) = CreateWriters();
        var method = CreateProtocolExtMethodWithClosure(
            "run",
            new ClosureTypeSpec(null, TupleTypeSpec.Empty));
        var env = CreateMethodEnvironment(method);
        var parentDecl = CreateClassDecl("MyProtocol");

        ProtocolExtensionClosureBridge.TryEmit(csWriter, swiftWriter, env, parentDecl);

        var result = csOutput.ToString();
        // Protocol extension P/Invoke ABI: self_ comes first
        Assert.Contains("IntPtr self_", result);
    }

    #endregion

    #region Helpers

    private static (CSharpWriter csWriter, SwiftWriter swiftWriter, StringWriter csOutput) CreateWriters()
    {
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);
        return (csWriter, swiftWriter, csOutput);
    }

    private static ModuleDecl CreateModuleDecl()
    {
        return new ModuleDecl
        {
            Name = "TestModule",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static ClassDecl CreateClassDecl(string name)
    {
        var moduleDecl = CreateModuleDecl();
        return new ClassDecl
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
            ModuleDecl = moduleDecl
        };
    }

    private static MethodDecl CreateMethodDecl(string name, bool isProtocolExtension)
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("TestType");
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule{name.Length}{name}yyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            IsProtocolExtensionMethod = isProtocolExtension,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty, moduleDecl),
                CreateArg("value", new NamedTypeSpec("Swift.Int"), moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl,
            Visibility = Visibility.Public
        };
    }

    private static MethodDecl CreateProtocolExtMethodWithClosure(string name, ClosureTypeSpec closureSpec)
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("MyProtocol");
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule10MyProtocol{name.Length}{name}yyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            IsProtocolExtensionMethod = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty, moduleDecl),
                CreateArg("self", new NamedTypeSpec("TestModule.MyProtocol"), moduleDecl),
                CreateArg("block", closureSpec, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl,
            Visibility = Visibility.Public
        };
    }

    private static ArgumentDecl CreateArg(string name, TypeSpec typeSpec, ModuleDecl moduleDecl)
    {
        return new ArgumentDecl
        {
            Name = name,
            PrivateName = name,
            SwiftTypeSpec = typeSpec,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
    }

    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "nint"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Boolean"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
                MetadataAccessor = "$sSbMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyProtocol"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "IMyProtocol"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyProtocol"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static MethodEnvironment CreateMethodEnvironment(MethodDecl method)
    {
        return new MethodEnvironment(method, CreateTypeDatabase());
    }

    #endregion
}
