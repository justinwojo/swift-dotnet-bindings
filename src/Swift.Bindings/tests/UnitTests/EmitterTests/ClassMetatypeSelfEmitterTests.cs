// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// A <c>static</c> or <c>class</c> member of a Swift class is lowered over the class's thick metatype,
/// which the callee reads from the Swift self register. A struct or enum static has a thin metatype and
/// no self. These tests pin which direct Swift-convention calls fill that register, and with what.
/// </summary>
public class ClassMetatypeSelfEmitterTests
{
    private const string MetatypeArgument = "new SwiftSelf((void*)SwiftObjectHelper<Counter>.GetTypeMetadata().Handle)";

    [Fact]
    public void ClassStaticMethod_PassesTheClassMetatypeInTheSelfRegister()
    {
        var (typeDatabase, module) = CreateModule();
        var counter = CreateClass("Counter", module, typeDatabase);
        AddMethod(counter, module, "make", "$s10TestModule7CounterC4makeSiyFZ", MethodType.Static, isFinal: true);

        var output = EmitClass(counter, typeDatabase);

        var pinvoke = PInvokeDeclaration(output, "$s10TestModule7CounterC4makeSiyFZ");
        Assert.Contains("CallConvSwift", pinvoke);
        Assert.Contains("SwiftSelf _metatypeSelf)", pinvoke);
        Assert.Contains(MetatypeArgument, output);
    }

    [Fact]
    public void StructStaticMethod_PassesNoSelf()
    {
        // The control: a struct static's metatype is thin, so the callee has no self register to read
        // and a SwiftSelf here would shift nothing into place but still be wrong.
        var (typeDatabase, module) = CreateModule();
        var point = CreateStruct("Point", module, typeDatabase);
        AddMethod(point, module, "make", "$s10TestModule5PointV4makeSiyFZ", MethodType.Static, isFinal: true);

        var output = EmitStruct(point, typeDatabase);

        var pinvoke = PInvokeDeclaration(output, "$s10TestModule5PointV4makeSiyFZ");
        Assert.DoesNotContain("SwiftSelf", pinvoke);
    }

    [Fact]
    public void OverridableClassFunc_BindsItsDispatchThunkAndPassesTheMetatype()
    {
        var (typeDatabase, module) = CreateModule();
        var counter = CreateClass("Counter", module, typeDatabase);
        AddMethod(counter, module, "kind", "$s10TestModule7CounterC4kindSiyFZ", MethodType.Static, isFinal: false);

        var output = EmitClass(counter, typeDatabase);

        var pinvoke = PInvokeDeclaration(output, "$s10TestModule7CounterC4kindSiyFZTj");
        Assert.Contains("SwiftSelf _metatypeSelf)", pinvoke);
        Assert.Contains(MetatypeArgument, output);
    }

    [Fact]
    public void OverriddenClassFunc_HidesTheAncestorsStaticAndDispatchesThroughItsThunk()
    {
        var (typeDatabase, module) = CreateModule();
        module.ExportedSymbols = new HashSet<string> { "$s10TestModule7CounterC4kindSiyFZTj" };
        var counter = CreateClass("Counter", module, typeDatabase);
        AddMethod(counter, module, "kind", "$s10TestModule7CounterC4kindSiyFZ", MethodType.Static, isFinal: false);
        var tally = CreateClass("Tally", module, typeDatabase);
        tally.ResolvedSuperclass = counter;
        var overrideDecl = AddMethod(tally, module, "kind", "$s10TestModule5TallyC4kindSiyFZ", MethodType.Static, isFinal: false);
        overrideDecl.IsOverride = true;

        EmitClass(counter, typeDatabase);
        var output = EmitClass(tally, typeDatabase);

        // Swift gives an override with the same signature no thunk of its own: the ancestor's thunk,
        // called with Tally's metatype, reaches the override through Tally's vtable.
        var pinvoke = PInvokeDeclaration(output, "$s10TestModule7CounterC4kindSiyFZTj");
        Assert.Contains("SwiftSelf _metatypeSelf)", pinvoke);
        Assert.Contains("new SwiftSelf((void*)SwiftObjectHelper<Tally>.GetTypeMetadata().Handle)", output);
        Assert.DoesNotContain("$s10TestModule5TallyC4kindSiyFZ", output);
        Assert.Contains("public static new ", output);
    }

    [Fact]
    public void ClassOperator_WithAWrapperLibrary_GoesThroughTheThunkThatLoadsTheMetatype()
    {
        // Emitted through the class handler, the production caller. An operator is a static member,
        // and its thunk loads the class metatype into the self register itself, so the managed side
        // makes a plain C call that carries no self.
        var (typeDatabase, module) = CreateModule();
        typeDatabase.AsyncLibraryName = "TestModuleSwiftBindings";
        var counter = CreateClass("Counter", module, typeDatabase);
        const string operatorSymbol = "$s10TestModule7CounterC1poiySiAC_ACtFZ";
        AddOperator(counter, module, "+", operatorSymbol);

        var output = EmitClass(counter, typeDatabase);

        var pinvoke = PInvokeDeclaration(output, ThunkAssemblyEmitter.GenerateThunkSymbol("TestModule", operatorSymbol));
        PInvokeConventionAssert.CConvention(pinvoke);
        Assert.Contains("\"TestModuleSwiftBindings\"", pinvoke);
        Assert.DoesNotContain("SwiftSelf", pinvoke);
        Assert.DoesNotContain(operatorSymbol, output);
    }

    [Fact]
    public void ClassOperator_WithAnIndirectlyPassedFrozenOperand_StaysOnTheDirectRoute()
    {
        // A frozen operand wider than swiftcc's register budget must reach a thunk as an explicit
        // address, which the operator body does not stage. Handing the thunk the value instead puts
        // it on the stack under System V, so the operator keeps the direct route, where the runtime's
        // own Swift lowering passes the operand by reference.
        var (typeDatabase, module) = CreateModule();
        typeDatabase.AsyncLibraryName = "TestModuleSwiftBindings";
        var big = SwiftTypeName.FromModuleQualifiedName("TestModule.Big");
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (identifier: big, record: new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Big"),
                SwiftTypeName = big,
                MetadataAccessor = "$s10TestModule3BigVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
                AbiFieldLayout = "i8,i8,i8,i8,i8",
                InlineSize = 40,
            }),
        });
        var counter = CreateClass("Counter", module, typeDatabase);
        const string operatorSymbol = "$s10TestModule7CounterC1poiySiAC_AA3BigVtFZ";
        AddOperator(counter, module, "+", operatorSymbol, rhsType: "TestModule.Big");

        var output = EmitClass(counter, typeDatabase);

        Assert.DoesNotContain(ThunkAssemblyEmitter.GenerateThunkSymbol("TestModule", operatorSymbol), output);
        Assert.Contains($"EntryPoint = \"{operatorSymbol}\"", output);
    }

    [Theory]
    [InlineData("static", true)]
    [InlineData("constructor", true)]
    [InlineData("instance", false)]
    [InlineData("async-static", false)]
    [InlineData("free-function-wrapper", false)]
    [InlineData("top-level-function-target", false)]
    public void RequiresClassMetatypeSelf_FollowsWhatTheCalleeReads(string shape, bool expected)
    {
        // A wrapper that is itself a free function, or a reduced decl that targets a top-level
        // function, has no self register; an async member reaches its callee through a wrapper; an
        // instance member's self is the instance, passed separately.
        var (typeDatabase, module) = CreateModule();
        var counter = CreateClass("Counter", module, typeDatabase);
        var method = AddMethod(counter, module, "make", "$s10TestModule7CounterC4makeSiyFZ",
            shape == "instance" ? MethodType.Instance : MethodType.Static, isFinal: true);
        method.IsConstructor = shape == "constructor";
        method.IsAsync = shape == "async-static";
        method.UsesFreeFunctionWrapper = shape == "free-function-wrapper";
        if (shape == "top-level-function-target")
            method = method with { TargetsTopLevelFunction = true };

        Assert.Equal(expected, MarshallingHelpers.RequiresClassMetatypeSelf(new MethodEnvironment(method, typeDatabase)));
    }

    [Fact]
    public void RequiresClassMetatypeSelf_IsFalseForAStructStatic()
    {
        var (typeDatabase, module) = CreateModule();
        var point = CreateStruct("Point", module, typeDatabase);
        var method = AddMethod(point, module, "make", "$s10TestModule5PointV4makeSiyFZ", MethodType.Static, isFinal: true);

        Assert.False(MarshallingHelpers.RequiresClassMetatypeSelf(new MethodEnvironment(method, typeDatabase)));
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private static string PInvokeDeclaration(string output, string entryPoint)
    {
        var lines = output.Split('\n');
        var index = System.Array.FindIndex(lines, l => l.Contains($"EntryPoint = \"{entryPoint}\""));
        Assert.True(index >= 0, $"no P/Invoke binds {entryPoint}:\n{output}");
        // [UnmanagedCallConv] precedes [LibraryImport]; the declaration follows it.
        return string.Join("\n", lines.Skip(System.Math.Max(0, index - 1)).Take(3));
    }

    private static (TypeDatabase, ModuleDecl) CreateModule()
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.NIntType,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
            });
        typeDatabase.AddModuleDatabase(swiftModule);
        typeDatabase.AddModuleDatabase(new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib"));

        var module = new ModuleDecl
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
        return (typeDatabase, module);
    }

    private static ClassDecl CreateClass(string name, ModuleDecl module, TypeDatabase typeDatabase)
    {
        var classDecl = new ClassDecl
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
            ParentDecl = module,
            ModuleDecl = module,
        };
        module.Types.Add(classDecl);
        Register(typeDatabase, classDecl.SwiftTypeName, name, $"$s10TestModule{name.Length}{name}CMa", TypeRecordKind.Class, TypeRecordFlags.None);
        return classDecl;
    }

    private static StructDecl CreateStruct(string name, ModuleDecl module, TypeDatabase typeDatabase)
    {
        var structDecl = new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}VN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = module,
            ModuleDecl = module,
            IsFrozen = true,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa",
        };
        module.Types.Add(structDecl);
        Register(typeDatabase, structDecl.SwiftTypeName, name, structDecl.MetadataAccessor, TypeRecordKind.Struct, TypeRecordFlags.Frozen);
        return structDecl;
    }

    private static void Register(TypeDatabase typeDatabase, SwiftTypeName swiftName, string name, string accessor,
        TypeRecordKind kind, TypeRecordFlags flags)
    {
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (identifier: swiftName, record: new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", name),
                SwiftTypeName = swiftName,
                MetadataAccessor = accessor,
                Flags = flags,
                Kind = kind,
            }),
        });
    }

    private static MethodDecl AddMethod(TypeDecl parent, ModuleDecl module, string name, string mangledName,
        MethodType methodType, bool isFinal)
    {
        var method = new MethodDecl
        {
            Name = name,
            MangledName = mangledName,
            MethodType = methodType,
            IsConstructor = false,
            IsFinal = isFinal,
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = module,
                },
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

    private static void AddOperator(ClassDecl parent, ModuleDecl module, string symbol, string mangledName,
        string rhsType = null)
    {
        ArgumentDecl Argument(string swiftType, string name) => new()
        {
            SwiftTypeSpec = new NamedTypeSpec(swiftType),
            Name = name,
            PrivateName = name,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = parent,
            ModuleDecl = module,
        };
        var method = new MethodDecl
        {
            Name = symbol,
            MangledName = mangledName,
            MethodType = MethodType.Static,
            IsConstructor = false,
            IsFinal = true,
            CSSignature = new List<ArgumentDecl>
            {
                Argument("Swift.Int", string.Empty),
                Argument($"TestModule.{parent.Name}", "lhs"),
                Argument(rhsType ?? $"TestModule.{parent.Name}", "rhs"),
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parent,
            ModuleDecl = module,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false,
        };
        parent.Operators.Add(new OperatorDecl
        {
            Name = symbol,
            OperatorSymbol = symbol,
            Kind = OperatorKind.Binary,
            IsPrefix = false,
            UnderlyingMethod = method,
            ParentDecl = parent,
            ModuleDecl = module,
        });
    }

    private static string EmitClass(ClassDecl classDecl, TypeDatabase typeDatabase)
    {
        var csOutput = new StringWriter();
        var handler = new ClassHandler(new NullLogger<ClassHandler>());
        var env = handler.Marshal(classDecl, typeDatabase);
        handler.Emit(new CSharpWriter(csOutput), new SwiftWriter(new StringWriter()), env,
            new Conductor(new NullLoggerFactory()), TypeHandlerContext.Empty);
        return csOutput.ToString();
    }

    private static string EmitStruct(StructDecl structDecl, TypeDatabase typeDatabase)
    {
        var csOutput = new StringWriter();
        var handler = new FrozenStructHandler(new NullLogger<FrozenStructHandler>());
        var env = handler.Marshal(structDecl, typeDatabase);
        handler.Emit(new CSharpWriter(csOutput), new SwiftWriter(new StringWriter()), env,
            new Conductor(new NullLoggerFactory()), TypeHandlerContext.Empty);
        return csOutput.ToString();
    }
}
