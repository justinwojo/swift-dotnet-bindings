// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Regression tests for third-party library binding compilation error fixes (B1-B9).
/// </summary>
public class ThirdPartyValidationFixTests
{
    #region B1+B2 — Missing using statements

    [Fact]
    public void ModuleHandler_EmitsCollectionsGenericUsing()
    {
        // Generated C# must include using System.Collections.Generic
        // for Dictionary, IReadOnlyList, IEnumerable
        var (csOutput, _) = EmitModule("TestModule");
        Assert.Contains("using System.Collections.Generic;", csOutput);
    }

    [Fact]
    public void ModuleHandler_EmitsThreadingTasksUsing()
    {
        // Generated C# must include using System.Threading.Tasks
        // for Task and Task<T>
        var (csOutput, _) = EmitModule("TestModule");
        Assert.Contains("using System.Threading.Tasks;", csOutput);
    }

    [Fact]
    public void ModuleHandler_UsingsAreAlphabetical()
    {
        // Verify using statements are in alphabetical order
        var (csOutput, _) = EmitModule("TestModule");
        var lines = csOutput.Split('\n');
        var usingLines = lines.Where(l => l.TrimStart().StartsWith("using System")).ToList();
        Assert.True(usingLines.Count >= 8, "Expected at least 8 System using statements");

        // Collections.Generic should come before Diagnostics
        int collectionsIdx = usingLines.FindIndex(l => l.Contains("Collections.Generic"));
        int diagnosticsIdx = usingLines.FindIndex(l => l.Contains("Diagnostics;"));
        Assert.True(collectionsIdx < diagnosticsIdx, "Collections.Generic should precede Diagnostics");

        // Threading.Tasks should come after Runtime.InteropServices.Swift
        int swiftIdx = usingLines.FindIndex(l => l.Contains("InteropServices.Swift"));
        int tasksIdx = usingLines.FindIndex(l => l.Contains("Threading.Tasks"));
        Assert.True(swiftIdx < tasksIdx, "Threading.Tasks should come after InteropServices.Swift");
    }

    #endregion

    #region B3 — Void as generic type parameter

    [Fact]
    public void BoundGenerics_EmptyTupleInGeneric_MapsToSwiftVoid()
    {
        // Swift: Result<Void, Error> → C# should use SwiftVoid, not void
        var typeDatabase = CreateTypeDatabaseWithResult();
        var handler = new BoundGenericsHandler(typeDatabase);

        var resultTypeSpec = new NamedTypeSpec("Swift.Result");
        resultTypeSpec.GenericParameters.Add(TupleTypeSpec.Empty); // Void
        resultTypeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Error"));

        var argDecl = new PropertyDecl
        {
            Name = "_temp",
            SwiftTypeSpec = resultTypeSpec,
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var result = handler.TranslateBoundGenericTypeToCSharp(argDecl);

        Assert.Contains("SwiftVoid", result);
        Assert.DoesNotContain("<void", result);
        Assert.DoesNotContain("<()>", result);
    }

    [Fact]
    public void BoundGenerics_NonEmptyTupleInGeneric_StaysAsTuple()
    {
        // (Int, Int) as generic arg should still be ValueTuple, not SwiftVoid
        var typeDatabase = CreateTypeDatabaseWithResult();
        var handler = new BoundGenericsHandler(typeDatabase);

        var tupleTypeSpec = new TupleTypeSpec();
        tupleTypeSpec.Elements.Add(new NamedTypeSpec("Swift.Int"));
        tupleTypeSpec.Elements.Add(new NamedTypeSpec("Swift.Int"));

        var resultTypeSpec = new NamedTypeSpec("Swift.Result");
        resultTypeSpec.GenericParameters.Add(tupleTypeSpec);
        resultTypeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Error"));

        var argDecl = new PropertyDecl
        {
            Name = "_temp",
            SwiftTypeSpec = resultTypeSpec,
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var result = handler.TranslateBoundGenericTypeToCSharp(argDecl);

        Assert.DoesNotContain("SwiftVoid", result);
    }

    #endregion

    #region B5 — Protocol proxy async return type + body

    [Fact]
    public void ProtocolProxy_AsyncMethodWithReturn_SignatureContainsTaskOfT()
    {
        // Interface declares Task<string> GetKeyAsync()
        // Proxy must match with Task<string>, not string
        var output = EmitAsyncProtocolProxy("generateKey", hasReturn: true);
        Assert.Contains("Task<string>", output);
    }

    [Fact]
    public void ProtocolProxy_AsyncVoidMethod_SignatureContainsTask()
    {
        // Interface declares Task RefreshAsync()
        // Proxy must match with Task, not void
        var output = EmitAsyncProtocolProxy("refresh", hasReturn: false);
        // Proxy class should have Task return type
        Assert.Contains("public Task", output);
    }

    [Fact]
    public void ProtocolProxy_AsyncVoidMethod_BodyReturnsTask()
    {
        // Proxy body for async void must "return _csharpImpl.Refresh()"
        // not "_csharpImpl.Refresh(); return;"
        var output = EmitAsyncProtocolProxy("refresh", hasReturn: false);
        // Should use "return _csharpImpl" delegation, not bare call + return
        Assert.Contains("return _csharpImpl.RefreshAsync(cancellationToken)", output);
    }

    [Fact]
    public void ProtocolProxy_AsyncMethodWithReturn_BodyReturnsDelegation()
    {
        var output = EmitAsyncProtocolProxy("generateKey", hasReturn: true);
        Assert.Contains("return _csharpImpl.GenerateKeyAsync(cancellationToken)", output);
    }

    #endregion

    #region B9 — Unrecognized bound generic fallback

    [Fact]
    public void ProjectTypeToCSharp_UnrecognizedBoundGeneric_ReturnsAnyType()
    {
        // SwiftDictionary<K,V> where neither K nor V is registered
        // Should return AnyType, not bare "SwiftDictionary" without type args
        var typeDatabase = CreateTypeDatabase();

        var dictTypeSpec = new NamedTypeSpec("Swift.Dictionary");
        dictTypeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        dictTypeSpec.GenericParameters.Add(new NamedTypeSpec("UnknownModule.Foo"));

        var result = ProtocolSignatureHelper.ProjectTypeToCSharp(dictTypeSpec, typeDatabase);

        Assert.Contains("AnyType", result);
    }

    [Fact]
    public void ProtocolHandler_GetCSharpTypeName_UnrecognizedBoundGeneric_ReturnsAnyType()
    {
        // Verify the ProtocolHandler fallback also returns AnyType
        var typeDatabase = CreateTypeDatabaseWithString();

        var dictTypeSpec = new NamedTypeSpec("Unknown.SomeGeneric");
        dictTypeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        // Use ProjectTypeToCSharp which mirrors ProtocolHandler.GetCSharpTypeName
        var result = ProtocolSignatureHelper.ProjectTypeToCSharp(dictTypeSpec, typeDatabase);

        Assert.Contains("AnyType", result);
    }

    #endregion

    #region B6 — DllImport in generic parent type (PInvokeHelperContext inheritance)

    [Fact]
    public void PInvokeHelperContext_NonGenericNestedInGeneric_InheritsParentContext()
    {
        // When a non-generic struct is nested inside a generic class,
        // CreateIfGeneric returns null for the nested type.
        // The handler should inherit the parent's context.
        var genericParent = CreateGenericClassDecl("AuthInterceptor", "C");
        var nonGenericNested = CreateStructDecl("RefreshWindow");

        var ownContext = PInvokeHelperContext.CreateIfGeneric(nonGenericNested);
        Assert.Null(ownContext);

        var parentContext = PInvokeHelperContext.CreateIfGeneric(genericParent);
        Assert.NotNull(parentContext);

        // The fix ensures: ownContext ?? parentContext is used as effective context
        var effectiveContext = ownContext ?? parentContext;
        Assert.NotNull(effectiveContext);
    }

    [Fact]
    public void PInvokeHelperContext_GenericNestedInGeneric_DeferredEmission()
    {
        // When a generic type is nested inside a generic parent, its helper class
        // must NOT be emitted inline (would still be inside outer generic → CS7042).
        // Instead it should be deferred and emitted at the outermost level.
        var conductor = new Conductor(new Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory());

        // Simulate outer generic creating its context
        var outerContext = new PInvokeHelperContext("Outer", new[] { "T0" });
        outerContext.AddDeclaration(new PInvokeDeclaration
        {
            LibraryPath = "/tmp/lib.dylib",
            EntryPoint = "$s_outer_method",
            MethodName = "OuterMethod",
            ReturnType = "void",
            ParametersString = ""
        });
        conductor.CurrentPInvokeHelperContext = outerContext;

        // Simulate nested generic creating its own context
        var innerContext = new PInvokeHelperContext("Inner", new[] { "U0" });
        innerContext.AddDeclaration(new PInvokeDeclaration
        {
            LibraryPath = "/tmp/lib.dylib",
            EntryPoint = "$s_inner_method",
            MethodName = "InnerMethod",
            ReturnType = "void",
            ParametersString = ""
        });

        // Inner is nested inside outer (previousContext != null) → defer
        var previousContext = conductor.CurrentPInvokeHelperContext;
        Assert.NotNull(previousContext); // We're inside a generic parent
        conductor.DeferredPInvokeHelperContexts.Add(innerContext);

        // Verify deferred list has the inner context
        Assert.Single(conductor.DeferredPInvokeHelperContexts);
        Assert.Same(innerContext, conductor.DeferredPInvokeHelperContexts[0]);
    }

    [Fact]
    public void PInvokeHelperContext_DeferredEmission_OutputShape()
    {
        // Verify the emitted code shape: outer helper emitted first, then deferred inner helpers.
        // Both should be at the same level (outside any generic type).
        var conductor = new Conductor(new Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory());

        var outerContext = new PInvokeHelperContext("Outer", new[] { "T0" });
        outerContext.AddDeclaration(new PInvokeDeclaration
        {
            LibraryPath = "/tmp/lib.dylib",
            EntryPoint = "$s_outer",
            MethodName = "OuterMethod",
            ReturnType = "void",
            ParametersString = ""
        });

        // Simulate nested generic Inner<U> inside Outer<T>
        // CreateIfGeneric would produce "Outer_Inner" via qualified name
        var innerContext = new PInvokeHelperContext("Outer_Inner", new[] { "U0" });
        innerContext.AddDeclaration(new PInvokeDeclaration
        {
            LibraryPath = "/tmp/lib.dylib",
            EntryPoint = "$s_inner",
            MethodName = "InnerMethod",
            ReturnType = "void",
            ParametersString = ""
        });
        conductor.DeferredPInvokeHelperContexts.Add(innerContext);

        // Emit as the outermost handler would: own + deferred
        var stringWriter = new StringWriter();
        var csWriter = new CSharpWriter(stringWriter);
        outerContext.EmitHelperClass(csWriter);
        foreach (var deferred in conductor.DeferredPInvokeHelperContexts)
            deferred.EmitHelperClass(csWriter);
        conductor.DeferredPInvokeHelperContexts.Clear();

        var output = stringWriter.ToString();

        // Both helper classes appear at the same level
        Assert.Contains("internal static partial class Outer_PInvoke", output);
        Assert.Contains("internal static partial class Outer_Inner_PInvoke", output);
        Assert.Contains("[LibraryImport", output);

        // Outer appears before Inner
        var outerIdx = output.IndexOf("Outer_PInvoke", StringComparison.Ordinal);
        var innerIdx = output.IndexOf("Outer_Inner_PInvoke", StringComparison.Ordinal);
        Assert.True(outerIdx < innerIdx, "Outer helper should be emitted before inner helper");

        // Deferred list was cleared
        Assert.Empty(conductor.DeferredPInvokeHelperContexts);
    }

    [Fact]
    public void PInvokeHelperContext_QualifiedName_AvoidsSiblingCollision()
    {
        // Two nested generics with the same simple name under different parents
        // should produce distinct qualified helper class names.
        var moduleDecl = CreateModuleDecl("TestModule");

        var outerA = CreateGenericClassDecl("Foo", "T");
        outerA.ParentDecl = moduleDecl;
        outerA.ModuleDecl = moduleDecl;

        var innerA = CreateGenericStructDecl("Inner", "U");
        innerA.ParentDecl = outerA;
        innerA.ModuleDecl = moduleDecl;

        var outerB = CreateGenericClassDecl("Bar", "T");
        outerB.ParentDecl = moduleDecl;
        outerB.ModuleDecl = moduleDecl;

        var innerB = CreateGenericStructDecl("Inner", "U");
        innerB.ParentDecl = outerB;
        innerB.ModuleDecl = moduleDecl;

        var contextA = PInvokeHelperContext.CreateIfGeneric(innerA);
        var contextB = PInvokeHelperContext.CreateIfGeneric(innerB);

        Assert.NotNull(contextA);
        Assert.NotNull(contextB);

        // Qualified names include parent chain → no collision
        Assert.Equal("Foo_Inner_PInvoke", contextA!.HelperClassName);
        Assert.Equal("Bar_Inner_PInvoke", contextB!.HelperClassName);
        Assert.NotEqual(contextA.HelperClassName, contextB.HelperClassName);
    }

    [Fact]
    public void PInvokeHelperContext_TopLevelGeneric_UsesSimpleName()
    {
        // A top-level generic type (parent is module, not type) should use its simple name.
        var moduleDecl = CreateModuleDecl("TestModule");
        var topLevel = CreateGenericClassDecl("Container", "T");
        topLevel.ParentDecl = moduleDecl;
        topLevel.ModuleDecl = moduleDecl;

        var context = PInvokeHelperContext.CreateIfGeneric(topLevel);
        Assert.NotNull(context);
        Assert.Equal("Container_PInvoke", context!.HelperClassName);
    }

    [Fact]
    public void PInvokeHelperContext_NonGenericNoParent_RemainsNull()
    {
        // Non-generic type with no generic parent should have no context
        var nonGenericType = CreateStructDecl("SimpleStruct");
        var ownContext = PInvokeHelperContext.CreateIfGeneric(nonGenericType);
        Assert.Null(ownContext);

        PInvokeHelperContext? parentContext = null;
        var effectiveContext = ownContext ?? parentContext;
        Assert.Null(effectiveContext);
    }

    #endregion

    #region B7 — Nullable reference type overload collision

    [Fact]
    public void GetProjectedCSharpMethodKey_NullableRefType_SameAsNonNullable()
    {
        // Method with param (Request, AnyType, AFError) and (Request, AnyType, AFError?)
        // should produce the same key since AFError is a class (reference type)
        var typeDatabase = CreateTypeDatabaseWithClassType();
        var moduleDecl = CreateModuleDecl("TestModule");

        var method1 = CreateMethodWithOptionalParam("didResume", "TestModule.AFError", optional: false, moduleDecl);
        var method2 = CreateMethodWithOptionalParam("didResume", "TestModule.AFError", optional: true, moduleDecl);

        var key1 = ProtocolSignatureHelper.GetProjectedCSharpMethodKey(method1, typeDatabase);
        var key2 = ProtocolSignatureHelper.GetProjectedCSharpMethodKey(method2, typeDatabase);

        Assert.Equal(key1, key2);
    }

    [Fact]
    public void GetProjectedCSharpMethodKey_NullableValueType_DifferentFromNonNullable()
    {
        // Method with param Int vs Int? should produce DIFFERENT keys
        // because value types have distinct Nullable<T> overloads
        var typeDatabase = CreateTypeDatabaseWithString();
        var moduleDecl = CreateModuleDecl("TestModule");

        var method1 = CreateMethodWithOptionalParam("process", "Swift.Int", optional: false, moduleDecl);
        var method2 = CreateMethodWithOptionalParam("process", "Swift.Int", optional: true, moduleDecl);

        var key1 = ProtocolSignatureHelper.GetProjectedCSharpMethodKey(method1, typeDatabase);
        var key2 = ProtocolSignatureHelper.GetProjectedCSharpMethodKey(method2, typeDatabase);

        Assert.NotEqual(key1, key2);
    }

    #endregion

    #region B7b — Enum case / nested type name collision

    [Fact]
    public void ComputeNestedTypeRenames_EnumCaseCollidesWithNestedType_Renames()
    {
        // Enum PingResponse has case "pong" (PascalCase: "Pong") and nested type "Pong"
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");

        var enumSwiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.PingResponse");
        module.RegisterType(enumSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "PingResponse"),
            SwiftTypeName = enumSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Enum
        });

        var pongSwiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.PingResponse.Pong");
        module.RegisterType(pongSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "PingResponse.Pong"),
            SwiftTypeName = pongSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        typeDatabase.AddModuleDatabase(module);

        var moduleDecl = CreateModuleDecl("TestModule");

        var nestedPong = new StructDecl
        {
            Name = "Pong",
            SwiftTypeName = pongSwiftName,
            MangledName = "$sN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = "$sMa"
        };

        var enumDecl = new EnumDecl
        {
            Name = "PingResponse",
            SwiftTypeName = enumSwiftName,
            MangledName = "$sN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { nestedPong },
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = "$sMa",
            Cases = new List<EnumCaseDecl>
            {
                new() { Name = "pong", MangledName = "$sN_pong", ParentDecl = null, ModuleDecl = moduleDecl },
                new() { Name = "timeout", MangledName = "$sN_timeout", ParentDecl = null, ModuleDecl = moduleDecl }
            }
        };
        nestedPong.ParentDecl = enumDecl;

        var renames = NameProvider.ComputeAndApplyNestedTypeRenames(enumDecl, typeDatabase);

        Assert.Single(renames);
        Assert.Equal("PongInfo", renames["Pong"]);

        // Verify TypeDatabase was updated
        Assert.True(typeDatabase.TryGetTypeRecord(pongSwiftName, out var record));
        Assert.Equal("PingResponse.PongInfo", record!.CSharpTypeName.Name);
    }

    [Fact]
    public void ComputeNestedTypeRenames_EnumCaseNoCollision_NoRename()
    {
        // Enum with case "foo" and nested type "Bar" — no collision
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");

        var enumSwiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyEnum");
        module.RegisterType(enumSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "MyEnum"),
            SwiftTypeName = enumSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Enum
        });

        var barSwiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyEnum.Bar");
        module.RegisterType(barSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "MyEnum.Bar"),
            SwiftTypeName = barSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });

        typeDatabase.AddModuleDatabase(module);

        var moduleDecl = CreateModuleDecl("TestModule");

        var nestedBar = new StructDecl
        {
            Name = "Bar",
            SwiftTypeName = barSwiftName,
            MangledName = "$sN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = "$sMa"
        };

        var enumDecl = new EnumDecl
        {
            Name = "MyEnum",
            SwiftTypeName = enumSwiftName,
            MangledName = "$sN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { nestedBar },
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = "$sMa",
            Cases = new List<EnumCaseDecl>
            {
                new() { Name = "foo", MangledName = "$sN_foo", ParentDecl = null, ModuleDecl = moduleDecl }
            }
        };
        nestedBar.ParentDecl = enumDecl;

        var renames = NameProvider.ComputeAndApplyNestedTypeRenames(enumDecl, typeDatabase);

        Assert.Empty(renames);
    }

    #endregion

    #region Helper Methods

    private static (string csOutput, string swiftOutput) EmitModule(string moduleName)
    {
        var typeDatabase = CreateTypeDatabaseWithString();
        var moduleDecl = CreateModuleDecl(moduleName);

        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);
        var loggerFactory = new Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory();
        var conductor = new Conductor(loggerFactory);

        var handler = new ModuleHandler(
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<ModuleHandler>());
        var env = handler.Marshal(moduleDecl, typeDatabase);
        handler.Emit(csWriter, swiftWriter, env, conductor);

        return (csStringWriter.ToString(), swiftStringWriter.ToString());
    }

    private static string EmitAsyncProtocolProxy(string methodName, bool hasReturn)
    {
        var typeDatabase = CreateTypeDatabaseWithString();
        var emitter = new ProtocolProxyEmitter(typeDatabase,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, "TestModule");

        var returnTypeSpec = hasReturn
            ? (TypeSpec)new NamedTypeSpec("Swift.String")
            : TupleTypeSpec.Empty;

        var protocolDecl = new ProtocolDecl
        {
            Name = "KeyGenerator",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.KeyGenerator"),
            MangledName = "$s10TestModule12KeyGeneratorMp",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>
            {
                new()
                {
                    Name = methodName,
                    MangledName = $"$s10TestModule{methodName}yyF",
                    MethodType = MethodType.Instance,
                    IsConstructor = false,
                    IsAsync = true,
                    Throws = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        new()
                        {
                            SwiftTypeSpec = returnTypeSpec,
                            Name = string.Empty, PrivateName = string.Empty,
                            IsInOut = false, IsGeneric = false,
                            ParentDecl = null, ModuleDecl = null
                        }
                    },
                    GenericParameters = new List<GenericArgumentDecl>(),
                    ParentDecl = null, ModuleDecl = null,
                    Visibility = Visibility.Public
                }
            },
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            HasSelfRequirement = false,
            ParentDecl = null,
            ModuleDecl = null
        };

        var stringWriter = new StringWriter();
        var writer = new CSharpWriter(stringWriter);
        emitter.EmitProxyClass(writer, protocolDecl);
        return stringWriter.ToString();
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
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithString()
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
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithResult()
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
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Result"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftResult"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Result"),
                MetadataAccessor = "$ss6ResultOMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Enum
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Error"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftError"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Error"),
                MetadataAccessor = "$ss5ErrorMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithClassType()
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
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.AFError"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "AFError"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.AFError"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
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

    private static MethodDecl CreateMethodWithOptionalParam(
        string name, string paramTypeName, bool optional, ModuleDecl moduleDecl)
    {
        TypeSpec paramTypeSpec;
        if (optional)
        {
            var optionalSpec = new NamedTypeSpec("Swift.Optional");
            optionalSpec.GenericParameters.Add(new NamedTypeSpec(paramTypeName));
            paramTypeSpec = optionalSpec;
        }
        else
        {
            paramTypeSpec = new NamedTypeSpec(paramTypeName);
        }

        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule{name}yyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    Name = string.Empty, PrivateName = string.Empty,
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = moduleDecl
                },
                new()
                {
                    SwiftTypeSpec = paramTypeSpec,
                    Name = "input", PrivateName = "input",
                    IsInOut = false, IsGeneric = false,
                    ParentDecl = null, ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null, ModuleDecl = moduleDecl,
            Throws = false, IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    private static ClassDecl CreateGenericClassDecl(string name, string typeParamName)
    {
        return new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = "$sN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>
            {
                new(typeParamName, typeParamName,
                    new List<GenericParameterConformance>(),
                    new List<GenericParameterConformance>())
            },
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsActor = false,
            IsFinal = false
        };
    }

    private static StructDecl CreateStructDecl(string name)
    {
        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = "$sN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = true,
            MetadataAccessor = "$sMa"
        };
    }

    private static StructDecl CreateGenericStructDecl(string name, string typeParamName)
    {
        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = "$sN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>
            {
                new(typeParamName, typeParamName,
                    new List<GenericParameterConformance>(),
                    new List<GenericParameterConformance>())
            },
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = true,
            MetadataAccessor = "$sMa"
        };
    }

    #endregion
}
