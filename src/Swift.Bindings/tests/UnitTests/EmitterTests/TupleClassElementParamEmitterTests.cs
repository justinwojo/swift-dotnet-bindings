// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// A @_cdecl tuple PARAMETER whose elements are a pure Swift class plus a primitive is marshalled
/// through the raw ABI buffer: the class element is written as its borrowed object handle (IntPtr)
/// into the pointer-width slot at the runtime-metadata offset, and the owning ValueTuple is pinned
/// with <c>GC.KeepAlive</c> past the native call so the wrapper's SafeHandle cannot be finalized —
/// releasing the Swift object — mid-call. Exercises the four wired emitter seams end-to-end:
/// PInvokeEmitter's CdeclTuple selection, the buffer-build per-element write, the unsafe-body flag,
/// and the finally keep-alive. The all-primitive control proves the class extension did not perturb
/// the original path (no spurious keep-alive).
/// </summary>
public class TupleClassElementParamEmitterTests
{
    [Fact]
    public void TupleParam_ClassAndPrimitive_WritesHandleAndKeepsAlive()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Holder", moduleDecl);

        // static func use(_ pair: (Widget, Int)) -> Int  — tuple param forces a @_cdecl wrapper.
        var tupleParam = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("TestModule.Widget"),
            new NamedTypeSpec("Swift.Int")
        });
        var method = CreateMethodWithTupleParam("use", parentDecl, moduleDecl, tupleParam, "pair");

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // The buffer is built from runtime tuple metadata and each element written at its offset.
        Assert.Contains("GetTupleTypeMetadataFromElements", csOutput);
        Assert.Contains("GetElementOffset(0)", csOutput);
        // The class element (Item1) is written as its borrowed object handle, NOT by value.
        Assert.Contains(".Item1.Payload.DangerousGetHandle()", csOutput);
        // The primitive element (Item2) is written by value.
        Assert.Contains("pair.Item2", csOutput);
        // The owning ValueTuple is pinned past the native call to prevent a mid-call finalize → UAF.
        Assert.Contains("global::System.GC.KeepAlive(pair);", csOutput);
        // The P/Invoke receives the buffer as a single IntPtr, not a by-value ValueTuple.
        Assert.Contains("pairPtr", csOutput);
    }

    [Fact]
    public void TupleParam_StringAndPrimitive_WritesBorrowedValueAndKeepsAlive()
    {
        // A Swift.String element occupies a 16-byte value slot written as a borrowed bit-copy of the
        // element's existing SwiftString storage (Unsafe.Read<SwiftString.Buffer> through the payload
        // handle) — NOT a fresh mint and NOT the @_cdecl utf8 ptr+len fast path. The owning ValueTuple
        // is pinned past the call (same source keep-alive as the class slot).
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Holder", moduleDecl);

        var tupleParam = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.String"),
            new NamedTypeSpec("Swift.Int")
        });
        var method = CreateMethodWithTupleParam("use", parentDecl, moduleDecl, tupleParam, "pair");

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        Assert.Contains("GetTupleTypeMetadataFromElements", csOutput);
        // The String slot is sized by the SwiftString runtime metadata, not a Swift class metadata.
        Assert.Contains("GetTypeMetadataOrThrow<global::Swift.SwiftString>()", csOutput);
        // Item1 is written as a borrowed 16-byte value read through the SwiftString payload handle.
        Assert.Contains("Unsafe.Read<global::Swift.SwiftString.Buffer>", csOutput);
        Assert.Contains(".Item1.Payload.DangerousGetHandle()", csOutput);
        // No fresh SwiftString materialization and no Dispose — it is a borrow, not a mint.
        Assert.DoesNotContain("new Swift.SwiftString", csOutput);
        // The owning ValueTuple is pinned past the native call.
        Assert.Contains("global::System.GC.KeepAlive(pair);", csOutput);
        Assert.Contains("pairPtr", csOutput);
    }

    [Fact]
    public void TupleParam_AllPrimitives_NoKeepAlive()
    {
        // Control: the original all-primitive buffer path is unchanged — every element is written by
        // value and there is NO keep-alive (no borrowed handle to protect).
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Holder", moduleDecl);

        var tupleParam = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool")
        });
        var method = CreateMethodWithTupleParam("use", parentDecl, moduleDecl, tupleParam, "pair");

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        Assert.Contains("GetTupleTypeMetadataFromElements", csOutput);
        Assert.Contains("pair.Item1", csOutput);
        Assert.DoesNotContain("DangerousGetHandle", csOutput);
        Assert.DoesNotContain("GC.KeepAlive(pair)", csOutput);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static (string csOutput, string swiftOutput) EmitMethod(MethodDecl methodDecl, TypeDatabase typeDatabase)
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

    private static MethodDecl CreateMethodWithTupleParam(
        string name, TypeDecl parentDecl, ModuleDecl moduleDecl, TupleTypeSpec tupleParam, string paramName)
    {
        var method = new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule6HolderC3use4pairSi_tFZ",
            MethodType = MethodType.Static,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                // index 0 — return slot (Swift.Int)
                new ArgumentDecl
                {
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                },
                // index 1 — the tuple parameter
                new ArgumentDecl
                {
                    SwiftTypeSpec = tupleParam,
                    Name = paramName,
                    PrivateName = paramName,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = parentDecl,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
        // Pin the @_cdecl method-wrapper strategy up front. The minimal MethodEnvironment+Emit harness
        // does not reproduce the full pipeline's wrapper-strategy decision for a tuple PARAMETER (it
        // would otherwise emit an unsupported stub), but a tuple param ALWAYS forces a @_cdecl wrapper
        // in the real pipeline (WrapperValidation.IsParamTypeCdeclRequired). Pre-setting the flags makes
        // Emit keep this strategy (its primary/thunk paths are guarded on !UsesCdeclMethodWrapper), so
        // the wrapper-buffer marshalling under test actually runs.
        method.WrapperStrategy = WrapperStrategy.CdeclMethod;
        method.UsesCdeclMethodWrapper = true;
        method.UsesWrapperLibrary = true;
        if (parentDecl is ClassDecl cd)
            cd.Methods.Add(method);
        return method;
    }

    private static ModuleDecl CreateModuleDecl(string name) => new ModuleDecl
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
            SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Boolean"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
                MetadataAccessor = "$sSbMa",
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
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Widget"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Widget"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Widget"),
                MetadataAccessor = "$s10TestModule6WidgetCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Holder"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Holder"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Holder"),
                MetadataAccessor = "$s10TestModule6HolderCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(module);

        return typeDatabase;
    }
}
