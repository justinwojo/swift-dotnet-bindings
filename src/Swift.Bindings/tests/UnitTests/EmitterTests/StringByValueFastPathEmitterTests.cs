// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for the Finding 56(d) string by-value fast path: a transient <c>String</c> argument to an
/// <c>@_cdecl</c> constructor/method wrapper is built directly into a 16-byte STACK buffer
/// (<see cref="Swift.SwiftString.EphemeralSwiftString"/>) instead of the heap
/// <c>SwiftString</c> + <c>SafeHandle</c> + <c>PayloadBuffer</c> the general parameter path allocates.
/// The two extracted nint words (<c>{p}_w0</c>/<c>{p}_w1</c>) and their +0/+1 lifetimes are
/// byte-identical to the old heap path — this asserts the fast path is emitted for the cdecl-decompose
/// case and that the heap path's markers are no longer present, with no change to the P/Invoke shape.
/// </summary>
public class StringByValueFastPathEmitterTests
{
    [Fact]
    public void CdeclMethodWrapper_StringParam_UsesEphemeralStackBuffer()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var method = CreateMethod("greet", parentDecl, moduleDecl);
        method.CSSignature.Add(CreateArg("name", new NamedTypeSpec("Swift.String"), moduleDecl));
        method.UsesCdeclMethodWrapper = true;

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // Fast path: build the transient String into a stack buffer, extract two words.
        Assert.Contains("new SwiftString.EphemeralSwiftString(name)", csOutput);
        Assert.Contains("using var nameSwift =", csOutput);
        Assert.Contains("Unsafe.As<SwiftString.Buffer, nint>(ref nameBuf)", csOutput);
        // P/Invoke argument names are unchanged (two nint words).
        Assert.Contains("nint name_w0 =", csOutput);
        Assert.Contains("nint name_w1 =", csOutput);

        // The heap SwiftString path (SafeHandle + PayloadBuffer) is intentionally skipped for the
        // decomposed param — its markers must be gone (behavior-preserving, allocation-free).
        Assert.DoesNotContain("nameDisposable", csOutput);
        Assert.DoesNotContain("PayloadBuffer", csOutput);
        Assert.DoesNotContain("new SwiftString(name)", csOutput);
    }

    [Fact]
    public void CdeclConstructorWrapper_StringParam_UsesEphemeralStackBuffer()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Tag", moduleDecl);

        var ctor = CreateConstructor(parentDecl, moduleDecl);
        ctor.CSSignature.Add(CreateArg("label", new NamedTypeSpec("Swift.String"), moduleDecl));
        ctor.UsesCdeclConstructorWrapper = true;

        var (csOutput, _) = EmitConstructor(ctor, typeDatabase);

        Assert.Contains("new SwiftString.EphemeralSwiftString(label)", csOutput);
        Assert.Contains("using var labelSwift =", csOutput);
        Assert.Contains("Unsafe.As<SwiftString.Buffer, nint>(ref labelBuf)", csOutput);
        Assert.Contains("nint label_w0 =", csOutput);
        Assert.Contains("nint label_w1 =", csOutput);

        Assert.DoesNotContain("labelDisposable", csOutput);
        Assert.DoesNotContain("PayloadBuffer", csOutput);
        Assert.DoesNotContain("new SwiftString(label)", csOutput);
    }

    [Fact]
    public void NonCdeclMethod_StringParam_KeepsHeapPath_NoEphemeral()
    {
        // Behavior-preservation guard: the fast path is scoped to the @_cdecl decompose case.
        // A CallConvSwift (non-cdecl) method's String param must still marshal through the heap
        // SwiftString projection — EphemeralSwiftString must NOT appear.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var method = CreateMethod("greet", parentDecl, moduleDecl);
        method.CSSignature.Add(CreateArg("name", new NamedTypeSpec("Swift.String"), moduleDecl));
        // UsesCdeclMethodWrapper deliberately NOT set.

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        Assert.DoesNotContain("EphemeralSwiftString", csOutput);
        Assert.Contains("new SwiftString(name)", csOutput);
    }

    #region Helpers

    private static (string csOutput, string swiftOutput) EmitMethod(
        MethodDecl methodDecl,
        TypeDatabase typeDatabase)
    {
        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        var env = new MethodEnvironment(methodDecl, typeDatabase);
        var conductor = new Conductor(new NullLoggerFactory());
        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        return (csOutput.ToString(), swiftOutput.ToString());
    }

    private static (string csOutput, string swiftOutput) EmitConstructor(
        MethodDecl methodDecl,
        TypeDatabase typeDatabase)
    {
        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        var handler = new ConstructorHandler(new NullLogger<ConstructorHandler>(), new HashSet<string>());
        var env = new MethodEnvironment(methodDecl, typeDatabase);
        var conductor = new Conductor(new NullLoggerFactory());
        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        return (csOutput.ToString(), swiftOutput.ToString());
    }

    private static MethodDecl CreateMethod(string name, TypeDecl parentDecl, ModuleDecl moduleDecl)
    {
        var method = new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule6LoaderC{name.Length}{name}yySSF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg(string.Empty, TupleTypeSpec.Empty, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
        if (parentDecl is ClassDecl classDecl)
            classDecl.Methods.Add(method);
        else if (parentDecl is StructDecl structDecl)
            structDecl.Methods.Add(method);
        return method;
    }

    private static MethodDecl CreateConstructor(StructDecl parentDecl, ModuleDecl moduleDecl)
    {
        var ctor = new MethodDecl
        {
            Name = "init",
            MangledName = $"$s10TestModule{parentDecl.Name.Length}{parentDecl.Name}V5labelACSS_tcfc",
            MethodType = MethodType.Static,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg(string.Empty, new NamedTypeSpec($"{moduleDecl.Name}.{parentDecl.Name}"), moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
        parentDecl.Methods.Add(ctor);
        return ctor;
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

    private static ModuleDecl CreateModuleDecl(string name)
    {
        return new ModuleDecl
        {
            Name = name,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static ClassDecl CreateClassDecl(string name, ModuleDecl moduleDecl)
    {
        var classDecl = new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{name.Length}{name}CN",
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
        moduleDecl.Types.Add(classDecl);
        return classDecl;
    }

    private static StructDecl CreateFrozenStructDecl(string name, ModuleDecl moduleDecl)
    {
        var structDecl = new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{name.Length}{name}VN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa"
        };
        moduleDecl.Types.Add(structDecl);
        return structDecl;
    }

    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.String"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct,
                InlineSize = 16
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Loader"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Loader"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Loader"),
                MetadataAccessor = "$s10TestModule6LoaderCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Tag"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Tag"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Tag"),
                MetadataAccessor = "$s10TestModule3TagVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(module);

        return typeDatabase;
    }

    #endregion
}
