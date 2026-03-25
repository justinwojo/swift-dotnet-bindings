// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for CrossModuleExtensionEmitter — cross-module extension type dispatch.
/// When module B extends a type from module A, this emitter generates static extension classes.
/// </summary>
public class CrossModuleExtensionEmitterTests
{
    private static readonly ILogger Logger = NullLogger.Instance;

    #region Emit: no members from current module → skips

    [Fact]
    public void Emit_NoMembersFromCurrentModule_NoOutput()
    {
        var (csWriter, swiftWriter, csOutput, moduleDecl, classDecl, conductor, env) = CreateSetup();

        // All methods belong to the original module, not the current module
        classDecl.Methods.Add(CreateMethodDecl("doWork", "OrigModule", classDecl));

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger);

        Assert.DoesNotContain("class", csOutput.ToString());
    }

    #endregion

    #region Emit: extension method from current module → emits class

    [Fact]
    public void Emit_ExtensionMethodFromCurrentModule_EmitsExtensionClass()
    {
        var (csWriter, swiftWriter, csOutput, moduleDecl, classDecl, conductor, env) = CreateSetup();

        // Method from current module (extension method)
        classDecl.Methods.Add(CreateMethodDecl("customAction", "TestModule", classDecl));

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger);

        var result = csOutput.ToString();
        Assert.Contains("public static partial class", result);
        Assert.Contains("TestModuleExtensions", result);
        Assert.Contains("Extension methods for", result);
    }

    #endregion

    #region Emit: gates — generic, async, throwing, mutating methods skipped

    [Fact]
    public void Emit_GenericMethod_Skipped()
    {
        var (csWriter, swiftWriter, csOutput, moduleDecl, classDecl, conductor, env) = CreateSetup();

        var method = CreateMethodDecl("doWork", "TestModule", classDecl);
        // Make method generic by adding a generic parameter
        method.GenericParameters.Add(new GenericArgumentDecl("τ_0_0", "T", new(), new()));
        classDecl.Methods.Add(method);

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger);

        var result = csOutput.ToString();
        Assert.DoesNotContain("DoWork", result);
    }

    [Fact]
    public void Emit_AsyncMethod_Skipped()
    {
        var (csWriter, swiftWriter, csOutput, moduleDecl, classDecl, conductor, env) = CreateSetup();

        var method = CreateMethodDecl("doWork", "TestModule", classDecl);
        method.IsAsync = true;
        classDecl.Methods.Add(method);

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger);

        var result = csOutput.ToString();
        Assert.DoesNotContain("DoWork", result);
    }

    [Fact]
    public void Emit_ThrowingMethod_Skipped()
    {
        var (csWriter, swiftWriter, csOutput, moduleDecl, classDecl, conductor, env) = CreateSetup();

        var method = CreateMethodDecl("doWork", "TestModule", classDecl);
        method.Throws = true;
        classDecl.Methods.Add(method);

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger);

        var result = csOutput.ToString();
        Assert.DoesNotContain("DoWork", result);
    }

    [Fact]
    public void Emit_MutatingMethod_Skipped()
    {
        var (csWriter, swiftWriter, csOutput, moduleDecl, classDecl, conductor, env) = CreateSetup();

        var method = CreateMethodDecl("doWork", "TestModule", classDecl);
        method.IsMutating = true;
        classDecl.Methods.Add(method);

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger);

        var result = csOutput.ToString();
        Assert.DoesNotContain("DoWork", result);
    }

    #endregion

    #region Emit: property extension

    [Fact]
    public void Emit_PropertyExtension_EmitsGetMethod()
    {
        var (csWriter, swiftWriter, csOutput, moduleDecl, classDecl, conductor, env) = CreateSetup();

        var ownerModuleDecl = CreateFullModuleDecl("TestModule");
        var getterMethod = CreateMethodDecl("get_count", "TestModule", classDecl);
        getterMethod.IsAccessor = true;
        getterMethod.MangledName = "$s10TestModule5count_getter";
        var getter = new GetAccessorDecl { Method = getterMethod };

        var property = new PropertyDecl
        {
            Name = "count",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl> { getter },
            ParentDecl = classDecl,
            ModuleDecl = ownerModuleDecl
        };
        classDecl.Properties.Add(property);

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger);

        var result = csOutput.ToString();
        Assert.Contains("GetCount", result);
    }

    [Fact]
    public void Emit_StaticProperty_Skipped()
    {
        var (csWriter, swiftWriter, csOutput, moduleDecl, classDecl, conductor, env) = CreateSetup();

        var ownerModuleDecl = CreateFullModuleDecl("TestModule");
        var property = new PropertyDecl
        {
            Name = "shared",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsStatic = true,
            HasStorage = false,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = classDecl,
            ModuleDecl = ownerModuleDecl
        };
        classDecl.Properties.Add(property);

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger);

        var result = csOutput.ToString();
        Assert.DoesNotContain("GetShared", result);
    }

    #endregion

    #region Emit: NativeMethods nested class

    [Fact]
    public void Emit_WithMembers_EmitsNativeMethodsClass()
    {
        var (csWriter, swiftWriter, csOutput, moduleDecl, classDecl, conductor, env) = CreateSetup();

        classDecl.Methods.Add(CreateMethodDecl("customAction", "TestModule", classDecl));

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger);

        var result = csOutput.ToString();
        Assert.Contains("NativeMethods", result);
        Assert.Contains("LibraryImport", result);
    }

    #endregion

    #region Emit: extension method with this parameter

    [Fact]
    public void Emit_InstanceMethod_HasThisParameter()
    {
        var (csWriter, swiftWriter, csOutput, moduleDecl, classDecl, conductor, env) = CreateSetup();

        classDecl.Methods.Add(CreateMethodDecl("doAction", "TestModule", classDecl));

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger);

        var result = csOutput.ToString();
        Assert.Contains("this", result);
    }

    #endregion

    #region Emit: calling convention — all cross-module P/Invokes use CallConvSwift

    [Fact]
    public void Emit_MethodPInvoke_UsesCallConvSwift()
    {
        var (csWriter, swiftWriter, csOutput, moduleDecl, classDecl, conductor, env) = CreateSetup();

        classDecl.Methods.Add(CreateMethodDecl("doAction", "TestModule", classDecl));

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger);

        var result = csOutput.ToString();
        // Cross-module extension P/Invokes always use CallConvSwift because both direct
        // symbols and @_silgen_name wrappers use swiftcc. SwiftSelf (x20) and
        // SwiftIndirectResult (x8) only map to correct registers under swiftcc.
        Assert.Contains("CallConvSwift", result);
        Assert.DoesNotContain("CallConvCdecl", result);
    }

    [Fact]
    public void Emit_PropertyGetterPInvoke_UsesCallConvSwift()
    {
        var (csWriter, swiftWriter, csOutput, moduleDecl, classDecl, conductor, env) = CreateSetup();

        var ownerModuleDecl = CreateFullModuleDecl("TestModule");
        var getterMethod = CreateMethodDecl("get_count", "TestModule", classDecl);
        getterMethod.IsAccessor = true;
        getterMethod.MangledName = "$s10TestModule5count_getter";
        var getter = new GetAccessorDecl { Method = getterMethod };

        var property = new PropertyDecl
        {
            Name = "count",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl> { getter },
            ParentDecl = classDecl,
            ModuleDecl = ownerModuleDecl
        };
        classDecl.Properties.Add(property);

        CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, Logger);

        var result = csOutput.ToString();
        // Property getter P/Invoke must use CallConvSwift (see method test comment)
        Assert.Contains("CallConvSwift", result);
        Assert.DoesNotContain("CallConvCdecl", result);
    }

    #endregion

    #region Helpers

    private static (CSharpWriter csWriter, SwiftWriter swiftWriter, StringWriter csOutput,
        ModuleDecl moduleDecl, ClassDecl classDecl, Conductor conductor, MethodEnvironment env) CreateSetup()
    {
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        // ClassDecl from a different module (OrigModule) — simulates cross-module extension
        var classDecl = new ClassDecl
        {
            Name = "OrigType",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("OrigModule.OrigType"),
            MangledName = "$s10OrigModule8OrigTypeCN",
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

        var conductor = new Conductor(NullLoggerFactory.Instance);
        // Create a dummy method for MethodEnvironment
        var dummyMethod = CreateMethodDecl("_dummy", "TestModule", classDecl);
        var env = new MethodEnvironment(dummyMethod, typeDatabase);

        return (csWriter, swiftWriter, csOutput, moduleDecl, classDecl, conductor, env);
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

    private static ModuleDecl CreateFullModuleDecl(string name)
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

    private static MethodDecl CreateMethodDecl(string name, string ownerModule, ClassDecl parentDecl)
    {
        var ownerModuleDecl = new ModuleDecl
        {
            Name = ownerModule,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10{ownerModule}{name.Length}{name}yyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = ownerModuleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = ownerModuleDecl,
            Visibility = Visibility.Public
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
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        typeDatabase.AddModuleDatabase(testModule);

        var origModule = new ModuleTypeDatabase("OrigModule", "/tmp/OrigModule.dylib");
        origModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("OrigModule.OrigType"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("OrigModule", "OrigType"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("OrigModule.OrigType"),
                MetadataAccessor = "$s10OrigModule8OrigTypeCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(origModule);
        return typeDatabase;
    }

    #endregion
}
