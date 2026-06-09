// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for MethodGenericBridgeEmitter — Swift 5.7+ implicit existential opening
/// bridges for methods with single protocol-constrained generic type parameters.
/// </summary>
public class MethodGenericBridgeEmitterTests
{
    #region TryEmit gates

    [Fact]
    public void TryEmit_Constructor_ReturnsFalse()
    {
        var (csWriter, swiftWriter) = CreateWriters();
        var method = CreateMethodDecl("init", isConstructor: true);
        var env = CreateMethodEnvironment(method);

        Assert.False(MethodGenericBridgeEmitter.TryEmit(csWriter, swiftWriter, env, null));
    }

    [Fact]
    public void TryEmit_Accessor_ReturnsFalse()
    {
        var (csWriter, swiftWriter) = CreateWriters();
        var method = CreateMethodDecl("getValue");
        method.IsAccessor = true;
        var env = CreateMethodEnvironment(method);

        Assert.False(MethodGenericBridgeEmitter.TryEmit(csWriter, swiftWriter, env, null));
    }

    [Fact]
    public void TryEmit_Async_ReturnsFalse()
    {
        var (csWriter, swiftWriter) = CreateWriters();
        var method = CreateMethodDecl("doWork");
        method.IsAsync = true;
        var env = CreateMethodEnvironment(method);

        Assert.False(MethodGenericBridgeEmitter.TryEmit(csWriter, swiftWriter, env, null));
    }

    [Fact]
    public void TryEmit_Throwing_ReturnsFalse()
    {
        var (csWriter, swiftWriter) = CreateWriters();
        var method = CreateMethodDecl("doWork");
        method.Throws = true;
        var env = CreateMethodEnvironment(method);

        Assert.False(MethodGenericBridgeEmitter.TryEmit(csWriter, swiftWriter, env, null));
    }

    [Fact]
    public void TryEmit_AlreadyUsesWrapperLibrary_ReturnsFalse()
    {
        var (csWriter, swiftWriter) = CreateWriters();
        var method = CreateMethodDecl("doWork");
        method.UsesWrapperLibrary = true;
        var env = CreateMethodEnvironment(method);

        Assert.False(MethodGenericBridgeEmitter.TryEmit(csWriter, swiftWriter, env, null));
    }

    [Fact]
    public void TryEmit_NoParentDecl_ReturnsFalse()
    {
        var (csWriter, swiftWriter) = CreateWriters();
        var method = CreateMethodDeclWithGenericParam();
        var env = CreateMethodEnvironment(method);

        Assert.False(MethodGenericBridgeEmitter.TryEmit(csWriter, swiftWriter, env, null));
    }

    [Fact]
    public void TryEmit_GenericParent_ReturnsFalse()
    {
        var (csWriter, swiftWriter) = CreateWriters();
        var method = CreateMethodDeclWithGenericParam();
        var parent = CreateClassDecl("Container");
        parent.GenericParameters = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl("τ_0_0", "T", new(), new())
        };
        method.ParentDecl = parent;
        var env = CreateMethodEnvironment(method);

        Assert.False(MethodGenericBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parent));
    }

    [Fact]
    public void TryEmit_NoMethodOwnGenericParams_ReturnsFalse()
    {
        var (csWriter, swiftWriter) = CreateWriters();
        var method = CreateMethodDecl("doWork"); // No generic params
        var parent = CreateClassDecl("Processor");
        method.ParentDecl = parent;
        var env = CreateMethodEnvironment(method);

        Assert.False(MethodGenericBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parent));
    }

    #endregion

    #region FindEligibleGenericParam

    [Fact]
    public void FindEligible_SingleProtocolConstraint_ReturnsInfo()
    {
        var method = CreateMethodDeclWithGenericParam();
        var typeDatabase = CreateTypeDatabase();

        var result = MethodGenericBridgeEmitter.FindEligibleGenericParam(method, typeDatabase);

        Assert.NotNull(result);
        Assert.Equal("τ_1_0", result!.Param.TypeName);
        Assert.Equal("TestModule.Describable", result.ConstraintProtocol.ModuleQualifiedName);
    }

    [Fact]
    public void FindEligible_MultipleOwnParams_ReturnsNull()
    {
        var moduleDecl = CreateModuleDecl();
        var method = new MethodDecl
        {
            Name = "doWork",
            MangledName = "$s10TestModule4doWork_yyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty, moduleDecl),
                CreateArg("a", new NamedTypeSpec("τ_1_0"), moduleDecl),
                CreateArg("b", new NamedTypeSpec("τ_1_1"), moduleDecl),
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_1_0", "T", new List<GenericParameterConformance>
                {
                    new GenericParameterConformance(Array.Empty<string>(), SwiftTypeName.FromModuleQualifiedName("TestModule.Describable"), ConformanceKind.Protocol)
                }, new()),
                new GenericArgumentDecl("τ_1_1", "U", new List<GenericParameterConformance>
                {
                    new GenericParameterConformance(Array.Empty<string>(), SwiftTypeName.FromModuleQualifiedName("TestModule.Describable"), ConformanceKind.Protocol)
                }, new()),
            },
            ParentDecl = CreateClassDecl("Processor"),
            ModuleDecl = moduleDecl,
            Visibility = Visibility.Public
        };

        var result = MethodGenericBridgeEmitter.FindEligibleGenericParam(method, CreateTypeDatabase());

        Assert.Null(result);
    }

    [Fact]
    public void FindEligible_NoProtocolConformance_ReturnsNull()
    {
        var moduleDecl = CreateModuleDecl();
        var method = new MethodDecl
        {
            Name = "doWork",
            MangledName = "$s10TestModule4doWork_yyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty, moduleDecl),
                CreateArg("value", new NamedTypeSpec("τ_1_0"), moduleDecl),
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                // No conformances at all
                new GenericArgumentDecl("τ_1_0", "T", new(), new()),
            },
            ParentDecl = CreateClassDecl("Processor"),
            ModuleDecl = moduleDecl,
            Visibility = Visibility.Public
        };

        var result = MethodGenericBridgeEmitter.FindEligibleGenericParam(method, CreateTypeDatabase());

        Assert.Null(result);
    }

    [Fact]
    public void FindEligible_NoAnyObjectBound_ReturnsNull()
    {
        var moduleDecl = CreateModuleDecl();
        var method = new MethodDecl
        {
            Name = "doWork",
            MangledName = "$s10TestModule4doWork_yyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty, moduleDecl),
                CreateArg("value", new NamedTypeSpec("τ_1_0"), moduleDecl),
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_1_0", "T", new List<GenericParameterConformance>
                {
                    // Protocol constraint but no AnyObject — struct conformers are unsound
                    new GenericParameterConformance(Array.Empty<string>(), SwiftTypeName.FromModuleQualifiedName("TestModule.Describable"), ConformanceKind.Protocol)
                }, new()),
            },
            ParentDecl = CreateClassDecl("Processor"),
            ModuleDecl = moduleDecl,
            Visibility = Visibility.Public
        };

        var result = MethodGenericBridgeEmitter.FindEligibleGenericParam(method, CreateTypeDatabase());

        Assert.Null(result);
    }

    [Fact]
    public void FindEligible_MultipleNonAnyObjectProtocols_ReturnsNull()
    {
        var moduleDecl = CreateModuleDecl();
        var method = new MethodDecl
        {
            Name = "doWork",
            MangledName = "$s10TestModule4doWork_yyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty, moduleDecl),
                CreateArg("value", new NamedTypeSpec("τ_1_0"), moduleDecl),
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_1_0", "T", new List<GenericParameterConformance>
                {
                    // Two real protocols + AnyObject — multi-protocol composition is unsound
                    new GenericParameterConformance(Array.Empty<string>(), SwiftTypeName.FromModuleQualifiedName("TestModule.Describable"), ConformanceKind.Protocol),
                    new GenericParameterConformance(Array.Empty<string>(), SwiftTypeName.FromModuleQualifiedName("TestModule.Printable"), ConformanceKind.Protocol),
                    new GenericParameterConformance(Array.Empty<string>(), SwiftTypeName.FromModuleQualifiedName("Swift.AnyObject"), ConformanceKind.Protocol)
                }, new()),
            },
            ParentDecl = CreateClassDecl("Processor"),
            ModuleDecl = moduleDecl,
            Visibility = Visibility.Public
        };

        var result = MethodGenericBridgeEmitter.FindEligibleGenericParam(method, CreateTypeDatabase());

        Assert.Null(result);
    }

    [Theory]
    [InlineData("Swift.Equatable")]
    [InlineData("Swift.Hashable")]
    [InlineData("Swift.Comparable")]
    [InlineData("Swift.Codable")]
    [InlineData("Swift.Sequence")]
    [InlineData("Swift.Collection")]
    public void FindEligible_SelfRequirementProtocol_ReturnsNull(string protocolName)
    {
        var moduleDecl = CreateModuleDecl();
        var method = new MethodDecl
        {
            Name = "doWork",
            MangledName = "$s10TestModule4doWork_yyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty, moduleDecl),
                CreateArg("value", new NamedTypeSpec("τ_1_0"), moduleDecl),
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_1_0", "T", new List<GenericParameterConformance>
                {
                    new GenericParameterConformance(Array.Empty<string>(), SwiftTypeName.FromModuleQualifiedName(protocolName), ConformanceKind.Protocol)
                }, new()),
            },
            ParentDecl = CreateClassDecl("Processor"),
            ModuleDecl = moduleDecl,
            Visibility = Visibility.Public
        };

        var result = MethodGenericBridgeEmitter.FindEligibleGenericParam(method, CreateTypeDatabase());

        Assert.Null(result);
    }

    [Fact]
    public void IsEligible_GenericParamInReturnType_ReturnsFalse()
    {
        var moduleDecl = CreateModuleDecl();
        var parent = CreateClassDecl("Processor");
        var method = new MethodDecl
        {
            Name = "transform",
            MangledName = "$s10TestModule9transform_yyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                // Return type contains the generic param
                CreateArg("", new NamedTypeSpec("τ_1_0"), moduleDecl),
                CreateArg("value", new NamedTypeSpec("τ_1_0"), moduleDecl),
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_1_0", "T", new List<GenericParameterConformance>
                {
                    new GenericParameterConformance(Array.Empty<string>(), SwiftTypeName.FromModuleQualifiedName("TestModule.Describable"), ConformanceKind.Protocol)
                }, new()),
            },
            ParentDecl = parent,
            ModuleDecl = moduleDecl,
            Visibility = Visibility.Public
        };

        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestBindings";
        Assert.False(MethodGenericBridgeEmitter.IsEligible(method, typeDatabase));
    }

    [Fact]
    public void FindEligible_GenericParamInsideContainer_ReturnsNull()
    {
        var moduleDecl = CreateModuleDecl();
        // Array<τ_1_0> — generic param is nested, not direct position
        var arraySpec = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("τ_1_0"));

        var method = new MethodDecl
        {
            Name = "process",
            MangledName = "$s10TestModule7process_yyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty, moduleDecl),
                CreateArg("items", arraySpec, moduleDecl),
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_1_0", "T", new List<GenericParameterConformance>
                {
                    new GenericParameterConformance(Array.Empty<string>(), SwiftTypeName.FromModuleQualifiedName("TestModule.Describable"), ConformanceKind.Protocol)
                }, new()),
            },
            ParentDecl = CreateClassDecl("Processor"),
            ModuleDecl = moduleDecl,
            Visibility = Visibility.Public
        };

        // GenericParamOnlyInDirectPositions rejects nested generic params
        var result = MethodGenericBridgeEmitter.FindEligibleGenericParam(method, CreateTypeDatabase());
        Assert.Null(result);
    }

    #endregion

    #region IsEligible

    [Fact]
    public void IsEligible_ValidMethod_ReturnsTrue()
    {
        var method = CreateMethodDeclWithGenericParam();
        var parent = CreateClassDecl("Processor");
        method.ParentDecl = parent;
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestBindings";

        Assert.True(MethodGenericBridgeEmitter.IsEligible(method, typeDatabase));
    }

    [Fact]
    public void IsEligible_ThrowingMethod_ReturnsFalse()
    {
        var method = CreateMethodDeclWithGenericParam();
        method.Throws = true;
        var parent = CreateClassDecl("Processor");
        method.ParentDecl = parent;
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestBindings";

        Assert.False(MethodGenericBridgeEmitter.IsEligible(method, typeDatabase));
    }

    [Fact]
    public void IsEligible_OptionalMetatypeReturn_ReturnsFalse()
    {
        // Bug-2 pin: a method-generic bridge with Optional<AnyClass.Type> return must
        // be rejected — ExistentialBypassEmitter would render the indirect buffer type
        // as a bare "Type" token in the @_cdecl wrapper. Generic param handling for
        // params already excludes this; the return side needs its own gate.
        var method = CreateMethodDeclWithGenericParam();
        var parent = CreateClassDecl("Processor");
        method.ParentDecl = parent;

        // Replace the return slot with Optional<AnyClass.Type>
        var optionalMetatype = new NamedTypeSpec("Swift.Optional");
        optionalMetatype.GenericParameters.Add(new NamedTypeSpec("AnyClass.Type"));
        method.CSSignature[0] = CreateArg("", optionalMetatype, method.ModuleDecl);

        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestBindings";

        Assert.False(MethodGenericBridgeEmitter.IsEligible(method, typeDatabase));
    }

    #endregion

    #region TryEmit: eligible method emits bridge

    [Fact]
    public void TryEmit_EligibleMethod_EmitsCdeclWrapper()
    {
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        var method = CreateMethodDeclWithGenericParam();
        var parent = CreateClassDecl("Processor");
        method.ParentDecl = parent;
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestBindings";

        var env = new MethodEnvironment(method, typeDatabase);
        var ctx = new ModuleEmissionContext();

        var handled = MethodGenericBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parent, ctx);

        Assert.True(handled);
        Assert.True(method.WasEmitted);
        Assert.True(method.UsesWrapperLibrary);

        var swiftResult = swiftOutput.ToString();
        Assert.Contains("@_cdecl", swiftResult);
        Assert.Contains("_XM", swiftResult);
        Assert.Contains("UnsafeRawPointer", swiftResult);
        Assert.Contains("Unmanaged<AnyObject>.fromOpaque(", swiftResult);
        Assert.Contains("as! any TestModule.Describable)", swiftResult);

        var csResult = csOutput.ToString();
        Assert.Contains("LibraryImport", csResult);
        Assert.Contains("ISwiftObject", csResult);
        Assert.Contains("CallConvCdecl", csResult);
    }

    [Fact]
    public void TryEmit_EligibleMethod_SwiftHandle()
    {
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        var method = CreateMethodDeclWithGenericParam();
        var parent = CreateClassDecl("Processor");
        method.ParentDecl = parent;
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestBindings";

        var env = new MethodEnvironment(method, typeDatabase);
        var ctx = new ModuleEmissionContext();

        MethodGenericBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parent, ctx);

        var csResult = csOutput.ToString();
        // Must use SwiftHandle (ISwiftObject property), not Payload
        Assert.Contains("SwiftHandle", csResult);
        Assert.DoesNotContain("Payload", csResult);
    }

    #endregion

    #region Indirect-result ownership

    // The bridge's indirect-result buffer was previously a fixed `Marshal.AllocHGlobal(256)`
    // (heap overflow for any Swift return whose stride exceeds 256 bytes) followed by an
    // undiscriminated MarshalFromSwift + finally-free: a double-free / allocator mismatch for
    // ownership-transfer returns, and a leaked +1 ARC retain for frozen-with-ref returns.
    // These tests pin the three-way ownership contract and that NO fixed 256-byte buffer
    // survives in the emitted C#.

    [Fact]
    public void TryEmit_NonFrozenStructReturn_SizesViaSwiftStrideAndTransfersOwnership()
    {
        // Non-frozen struct (ClassWithOpaquePayload): the wire buffer is adopted into the
        // returned SafeHandle, so it must be NativeMemory.Alloc'd (to match ReleaseHandle's
        // NativeMemory.Free) and NOT freed in a finally here — that would double-free.
        var (handled, csResult, _) = EmitBridgeWithStructReturn(
            "OpaqueResult", TypeRecordKind.Struct, TypeRecordFlags.RequiresMemoryManagement);

        Assert.True(handled);
        Assert.Contains("GetSwiftTypeSize<", csResult);   // sized to the Swift stride…
        Assert.DoesNotContain("256", csResult);            // …never a fixed 256-byte buffer
        Assert.Contains("NativeMemory.Alloc", csResult);   // allocator matches ReleaseHandle
        Assert.Contains("MarshalFromSwift<", csResult);
        Assert.DoesNotContain("FreeHGlobal", csResult);    // ownership transfers — no finally-free
    }

    [Fact]
    public void TryEmit_FrozenWithRefStructReturn_DestroysWireRetainsThenFreesBuffer()
    {
        // Frozen-struct-with-ref-fields (ClassWithBufferStruct): NewFromPayload copies the wire
        // into a managed buffer, but the wire still holds +1 on its ref fields — VWT-Destroy it
        // before freeing the C#-owned buffer, else every call leaks +1 ARC retain.
        var (handled, csResult, _) = EmitBridgeWithStructReturn(
            "FrozenRefResult", TypeRecordKind.Struct,
            TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement);

        Assert.True(handled);
        Assert.Contains("GetSwiftTypeSize<", csResult);
        Assert.DoesNotContain("256", csResult);
        Assert.Contains("DestroyWireBufferRetains<", csResult);
        Assert.Contains("FreeHGlobal", csResult);          // C#-owned buffer is freed (no transfer)
    }

    [Fact]
    public void TryEmit_FrozenTrivialStructReturn_IsInadmissible_NotEmitted()
    {
        // Frozen-trivial struct (no ref fields) routes to IndirectResult but has no ISwiftObject
        // binding to size via GetSwiftTypeSize<T>. The admission gate must drop it — the same
        // branch that drops inline value structs whose idiomatic projection is not an ISwiftObject
        // (e.g. Foundation.Data → byte[], where GetSwiftTypeSize<byte[]>() would not even compile).
        // Dropping lets the method fall back to the normal path instead of emitting uncompilable C#.
        var (csWriter, swiftWriter) = CreateWriters();
        var method = CreateMethodDeclWithGenericParam();
        var parent = CreateClassDecl("Processor");
        method.ParentDecl = parent;
        method.CSSignature[0] = CreateArg("", new NamedTypeSpec("TestModule.FrozenTrivialResult"), method.ModuleDecl);
        var typeDatabase = CreateTypeDatabaseWithReturn(
            "FrozenTrivialResult", TypeRecordKind.Struct, TypeRecordFlags.Frozen);

        Assert.False(MethodGenericBridgeEmitter.IsEligible(method, typeDatabase));

        var env = new MethodEnvironment(method, typeDatabase);
        Assert.False(MethodGenericBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parent, new ModuleEmissionContext()));
        Assert.False(method.WasEmitted);
    }

    #endregion

    #region Helpers

    private static (CSharpWriter csWriter, SwiftWriter swiftWriter) CreateWriters()
    {
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);
        return (csWriter, swiftWriter);
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

    private static MethodDecl CreateMethodDecl(string name, bool isConstructor = false)
    {
        var moduleDecl = CreateModuleDecl();
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule{name.Length}{name}yyF",
            MethodType = MethodType.Instance,
            IsConstructor = isConstructor,
            Throws = false,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = CreateClassDecl("TestType"),
            ModuleDecl = moduleDecl,
            Visibility = Visibility.Public
        };
    }

    /// <summary>
    /// Creates a method with a single method-own generic parameter constrained to Describable protocol.
    /// Pattern: func process&lt;T: Describable&gt;(_ value: T)
    /// </summary>
    private static MethodDecl CreateMethodDeclWithGenericParam()
    {
        var moduleDecl = CreateModuleDecl();
        var parent = CreateClassDecl("Processor");
        return new MethodDecl
        {
            Name = "process",
            MangledName = "$s10TestModule9Processor7processyyxF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty, moduleDecl), // void return
                new ArgumentDecl { Name = "_", PrivateName = "value", SwiftTypeSpec = new NamedTypeSpec("τ_1_0"), IsInOut = false, IsGeneric = true, ParentDecl = null, ModuleDecl = moduleDecl },
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_1_0", "T", new List<GenericParameterConformance>
                {
                    new GenericParameterConformance(Array.Empty<string>(), SwiftTypeName.FromModuleQualifiedName("TestModule.Describable"), ConformanceKind.Protocol),
                    new GenericParameterConformance(Array.Empty<string>(), SwiftTypeName.FromModuleQualifiedName("Swift.AnyObject"), ConformanceKind.Protocol)
                }, new()),
            },
            ParentDecl = parent,
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
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Processor"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Processor"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Processor"),
                MetadataAccessor = "$s10TestModule9ProcessorCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static MethodEnvironment CreateMethodEnvironment(MethodDecl method)
    {
        return new MethodEnvironment(method, CreateTypeDatabase());
    }

    /// <summary>
    /// Emits the sync MGB bridge for an eligible method whose return is the named struct,
    /// registered with the given kind/flags. Returns whether emission was handled plus the
    /// generated C# and Swift text. Used by the indirect-result ownership tests.
    /// </summary>
    private static (bool handled, string csResult, string swiftResult) EmitBridgeWithStructReturn(
        string returnSimpleName, TypeRecordKind returnKind, TypeRecordFlags returnFlags)
    {
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        var method = CreateMethodDeclWithGenericParam();
        var parent = CreateClassDecl("Processor");
        method.ParentDecl = parent;
        // Swap the void return for the indirect-result struct return.
        method.CSSignature[0] = CreateArg("", new NamedTypeSpec($"TestModule.{returnSimpleName}"), method.ModuleDecl);

        var typeDatabase = CreateTypeDatabaseWithReturn(returnSimpleName, returnKind, returnFlags);
        var env = new MethodEnvironment(method, typeDatabase);
        var handled = MethodGenericBridgeEmitter.TryEmit(csWriter, swiftWriter, env, parent, new ModuleEmissionContext());
        return (handled, csOutput.ToString(), swiftOutput.ToString());
    }

    /// <summary>
    /// Builds a TypeDatabase carrying Swift.Int, the non-generic class parent (Processor), and a
    /// struct return type registered with the supplied kind/flags. xcframework mode is enabled
    /// (AsyncLibraryName) so the bridge's IsXCFrameworkMode gate passes.
    /// </summary>
    private static TypeDatabase CreateTypeDatabaseWithReturn(
        string returnSimpleName, TypeRecordKind returnKind, TypeRecordFlags returnFlags)
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
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Processor"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Processor"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Processor"),
                MetadataAccessor = "$s10TestModule9ProcessorCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"TestModule.{returnSimpleName}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", returnSimpleName),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{returnSimpleName}"),
                MetadataAccessor = $"$s10TestModule{returnSimpleName.Length}{returnSimpleName}VMa",
                Flags = returnFlags,
                Kind = returnKind
            });
        typeDatabase.AddModuleDatabase(testModule);
        typeDatabase.AsyncLibraryName = "TestBindings";
        return typeDatabase;
    }

    #endregion
}
