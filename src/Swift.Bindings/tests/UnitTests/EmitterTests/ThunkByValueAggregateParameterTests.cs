// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// A native thunk models every non-floating-point argument as exactly one integer register that
/// holds either a scalar or a pointer. A frozen struct argument wider than swiftcc's direct-passing
/// budget is passed indirectly by swiftcc (a pointer in the next integer register), so the managed
/// side must hand the thunk that pointer. Handing it the struct BY VALUE only happens to work on
/// arm64: AAPCS64 replaces a composite larger than 16 bytes with a pointer to a caller copy. System V
/// AMD64 copies the same composite onto the stack (MEMORY class) and puts nothing in the register, so
/// the thunk forwards whatever the register held and the callee dereferences garbage.
///
/// <para>These tests pin the contract on both sides: the by-value shapes that would need more than one
/// register on x86_64 (two eightbytes, homogeneous floating-point aggregates, packed floats) stay off
/// the thunk entirely, and the indirectly-passed shapes reach the thunk as an explicit pointer.</para>
/// </summary>
public class ThunkByValueAggregateParameterTests
{
    // ---- Shapes that would need two eightbytes or a split register file: never thunked ----

    [Theory]
    [InlineData("i8,i8", 16)]        // two INTEGER eightbytes (rdi+rsi on SysV, x0+x1 on arm64)
    [InlineData("i4,i4,i8", 16)]     // packed integer eightbyte + a second eightbyte
    [InlineData("f8,f8", 16)]        // HFA on arm64 (d0,d1) / two SSE eightbytes on SysV (xmm0,xmm1)
    [InlineData("f4,f4", 8)]         // two floats packed into one SSE eightbyte on SysV
    [InlineData("i4,f4", 8)]         // mixed INTEGER eightbyte on SysV, split register files in swiftcc
    [InlineData("i8,f8", 16)]        // INTEGER + SSE eightbytes
    [InlineData("f8,f8,f8,f8", 32)]  // four-member HFA on arm64, MEMORY class on SysV
    [InlineData("i8,i8,i8", 24)]     // three INTEGER slots swiftcc passes directly, MEMORY on SysV
    public void ByValueAggregate_NeedingMoreThanOneRegister_IsNotThunked(string layout, int size)
    {
        var db = CreateTypeDatabase(("TestModule.Agg", layout, size, TypeRecordFlags.Frozen));
        var env = FreeFunctionEnv(db, "TestModule.Agg");

        Assert.False(NativeThunkEmitter.ShouldEmitThunk(env));
    }

    // ---- Shapes swiftcc passes indirectly: thunked, and forwarded by address ----

    [Theory]
    [InlineData("i8,i8,i8,i8,i8", 40, false)]              // blittable, five INTEGER slots
    [InlineData("f8,f8,f8,f8,f8", 40, false)]              // five doubles: not an HFA on arm64 either
    [InlineData("p8,p8,p8,p8,p8", 40, true)]               // five references (memory-managed carrier)
    public void IndirectlyPassedFrozenStruct_IsForwardedByAddress(string layout, int size, bool managed)
    {
        var flags = TypeRecordFlags.Frozen | (managed ? TypeRecordFlags.RequiresMemoryManagement : 0);
        var db = CreateTypeDatabase(("TestModule.Big", layout, size, flags));
        var env = FreeFunctionEnv(db, "TestModule.Big");

        var lowering = TypeLowering.LowerParameterType(new NamedTypeSpec("TestModule.Big"), db);
        Assert.NotNull(lowering);
        Assert.True(lowering!.IsIndirect);

        Assert.True(NativeThunkEmitter.ShouldEmitThunk(env));
        Assert.True(NativeThunkEmitter.ForwardsParameterByAddress(env.MethodDecl.CSSignature[1], db));
    }

    [Fact]
    public void RegisterSizedFrozenStructAndNonFrozenStruct_AreNotForwardedByAddress()
    {
        var db = CreateTypeDatabase(
            ("TestModule.Small", "i4,i4", 8, TypeRecordFlags.Frozen),
            ("TestModule.Resilient", "", 0, TypeRecordFlags.None));

        // One INTEGER eightbyte: a register value on every ABI, passed as itself.
        Assert.False(NativeThunkEmitter.ForwardsParameterByAddress(Arg("s", "TestModule.Small"), db));
        // Already a pointer on the managed side (the value's own payload handle).
        Assert.False(NativeThunkEmitter.ForwardsParameterByAddress(Arg("r", "TestModule.Resilient"), db));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndirectlyPassedFrozenStruct_ThunkPInvoke_TakesAPointer(bool managed)
    {
        var flags = TypeRecordFlags.Frozen | (managed ? TypeRecordFlags.RequiresMemoryManagement : 0);
        var layout = managed ? "p8,p8,p8,p8,p8" : "i8,i8,i8,i8,i8";
        var db = CreateTypeDatabase(("TestModule.Big", layout, 40, flags));
        db.AsyncLibraryName = "TestModuleSwiftBindings";
        var module = CreateModuleDecl("TestModule");
        var method = CreateFreeFunction("consume", module, Arg("a", "TestModule.Big", module));

        var csOutput = EmitMethod(method, db);

        var pinvoke = AbiContractChecker.ExtractPInvokes(csOutput, "TestModule")
            .SingleOrDefault(p => p.EntryPoint.StartsWith("thunk_", StringComparison.Ordinal));
        Assert.True(pinvoke != null, $"No thunk P/Invoke in:\n{csOutput}");
        var declaration = csOutput.Split('\n')
            .Single(l => l.Contains($" {pinvoke!.MethodName}(", StringComparison.Ordinal)
                && l.Contains("static partial", StringComparison.Ordinal));

        // The argument crosses the C boundary as a pointer, never as the struct value.
        Assert.Contains("IntPtr a", declaration);
        Assert.DoesNotContain("Big a", declaration);
        Assert.DoesNotContain(".Buffer a", declaration);
        // And the call site hands it the address of the value's Swift-layout bytes.
        Assert.Contains($"{pinvoke.MethodName}(aPtr)", csOutput);
    }

    [Fact]
    public void IndirectlyPassedManagedFrozenStruct_ConsumedByAThunkedInitializer_IsPrepaidBeforeItsAddressIsForwarded()
    {
        // An initializer consumes its arguments. Forwarding the caller's own payload address is only
        // sound because the value-witness copy hands the callee the +1 it will release, exactly as the
        // address-only consumed lane does; the address and the transfer must travel together.
        var db = CreateTypeDatabase(
            ("TestModule.Big", "p8,p8,p8,p8,p8", 40, TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement));
        db.AsyncLibraryName = "TestModuleSwiftBindings";
        var module = CreateModuleDecl("TestModule");
        var host = CreateClass("Host", module, db);
        var init = CreateConstructor(host, module, Arg("a", "TestModule.Big", module));

        var csOutput = EmitConstructor(init, db);

        var pinvoke = AbiContractChecker.ExtractPInvokes(csOutput, "TestModule")
            .SingleOrDefault(p => p.EntryPoint.StartsWith("thunk_", StringComparison.Ordinal));
        Assert.True(pinvoke != null, $"No thunk P/Invoke in:\n{csOutput}");
        Assert.Contains($"(aPtr)", csOutput.Split('\n').Single(l => l.Contains($"{pinvoke!.MethodName}(", StringComparison.Ordinal)
            && !l.Contains("static partial", StringComparison.Ordinal)));
        Assert.Contains("OwnedArgument.BeginValueTransfer<TestModule.Big>(a.Payload)", csOutput);
    }

    // ---- The x86_64 target's register accounting for an indirect return ----

    [Fact]
    public void SysV_IndirectReturnOfUnknownSize_StillConsumesTheSretRegister()
    {
        // swiftcc returns an address-only value through a caller buffer whose size the lowering may
        // not know (TotalByteSize 0). The cdecl side still passes that buffer as the hidden first
        // integer argument, and EmitTailCall/EmitFullFrame shift every argument down for it, so the
        // encodability guard must count it: sret + six arguments does not fit in six registers.
        var descriptor = new ThunkDescriptor(
            ThunkSymbol: "thunk_t_opaque",
            SwiftSymbol: "_$sOpaque",
            ReturnLowering: new TypeLoweringResult(Array.Empty<RegisterSlot>(), IsIndirect: true, TotalByteSize: 0),
            SelfLowering: null,
            ParameterCount: 6,
            FloatParameterCount: 0,
            IsInstanceMethod: false,
            IsStaticMethod: false,
            IsConstructor: false,
            Throws: false,
            MetadataAccessorSymbol: null);

        Assert.False(ThunkTargetArch.X86_64.CanEmit(descriptor));
    }

    // ---- Harness ----

    private static MethodEnvironment FreeFunctionEnv(TypeDatabase db, string paramType)
    {
        db.AsyncLibraryName = "TestModuleSwiftBindings";
        var module = CreateModuleDecl("TestModule");
        var method = CreateFreeFunction("consume", module, Arg("a", paramType, module));
        return new MethodEnvironment(method, db);
    }

    private static string EmitMethod(MethodDecl methodDecl, TypeDatabase typeDatabase)
    {
        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        var env = new MethodEnvironment(methodDecl, typeDatabase);
        var conductor = new Conductor(new NullLoggerFactory());
        handler.Emit(new CSharpWriter(csOutput), new SwiftWriter(swiftOutput), env, conductor, TypeHandlerContext.Empty);
        return csOutput.ToString();
    }

    private static string EmitConstructor(MethodDecl methodDecl, TypeDatabase typeDatabase)
    {
        var csOutput = new StringWriter();
        var handler = new ConstructorHandler(new NullLogger<ConstructorHandler>(), new HashSet<string>());
        handler.Emit(
            new CSharpWriter(csOutput),
            new SwiftWriter(new StringWriter()),
            new MethodEnvironment(methodDecl, typeDatabase),
            new Conductor(new NullLoggerFactory()),
            TypeHandlerContext.Empty);
        return csOutput.ToString();
    }

    private static ClassDecl CreateClass(string name, ModuleDecl module, TypeDatabase typeDatabase)
    {
        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{module.Name}.{name}");
        var classDecl = new ClassDecl
        {
            Name = name,
            SwiftTypeName = swiftTypeName,
            MangledName = $"$s10TestModule{name.Length}{name}CN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = module,
            ModuleDecl = module,
        };
        module.Types.Add(classDecl);
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (identifier: swiftTypeName, record: new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", name),
                SwiftTypeName = swiftTypeName,
                MetadataAccessor = $"$s10TestModule{name.Length}{name}CMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Class,
            })
        });
        return classDecl;
    }

    private static MethodDecl CreateConstructor(ClassDecl parent, ModuleDecl module, ArgumentDecl argument)
    {
        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule4HostC1aAcA3BigV_tcfC",
            MethodType = MethodType.Static,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                Arg(string.Empty, $"{module.Name}.{parent.Name}", module),
                argument,
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parent,
            ModuleDecl = module,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false,
        };
        parent.Methods.Add(method);
        return method;
    }

    private static MethodDecl CreateFreeFunction(string name, ModuleDecl module, ArgumentDecl argument)
    {
        var method = new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule{name.Length}{name}yyAA3BigVF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new() { Name = string.Empty, PrivateName = string.Empty, SwiftTypeSpec = TupleTypeSpec.Empty, IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = module },
                argument,
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = module,
            ModuleDecl = module,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false,
        };
        module.Methods.Add(method);
        return method;
    }

    private static ArgumentDecl Arg(string name, string type, ModuleDecl? module = null) => new()
    {
        Name = name,
        PrivateName = name,
        SwiftTypeSpec = new NamedTypeSpec(type),
        IsInOut = false,
        IsGeneric = false,
        ParentDecl = null,
        ModuleDecl = module,
    };

    private static ModuleDecl CreateModuleDecl(string name) => new()
    {
        Name = name,
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Dependencies = new List<string>(),
        Protocols = new List<ProtocolDecl>(),
        ParentDecl = null,
        ModuleDecl = null,
    };

    private static TypeDatabase CreateTypeDatabase(params (string Name, string Layout, int Size, TypeRecordFlags Flags)[] structs)
    {
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        foreach (var (name, layout, size, flags) in structs)
        {
            var simple = name.Substring(name.LastIndexOf('.') + 1);
            module.RegisterType(
                SwiftTypeName.FromModuleQualifiedName(name),
                new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", simple),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(name),
                    MetadataAccessor = $"$s10TestModule{simple.Length}{simple}VMa",
                    Flags = flags,
                    Kind = TypeRecordKind.Struct,
                    AbiFieldLayout = string.IsNullOrEmpty(layout) ? null : layout,
                    InlineSize = size == 0 ? null : size,
                });
        }
        typeDatabase.AddModuleDatabase(module);
        return typeDatabase;
    }
}
