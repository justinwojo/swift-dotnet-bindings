// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// <c>AnyObject</c> is one class reference with no witness table, typed <c>object</c> in C#. It
/// crosses an @_cdecl wrapper like every other existential, through a pointer to a cell holding the
/// value: Swift loads an argument out of the cell and initializes a result into the caller's buffer.
/// The cell is one word, so the C# side writes and reads only the reference and boxes it in the
/// class-bound <c>SwiftExistential</c>. The wrapper used to read the argument pointer as the object
/// while C# passed the address of a five-word container, and return the object by value while C#
/// read five words out of a buffer nothing wrote.
/// </summary>
public class AnyObjectExistentialEmitterTests
{
    public static TheoryData<string, ProtocolListTypeSpec> AnyObjectShapes => new()
    {
        { "AnyObject", new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.AnyObject") }) },
        { "any AnyObject & Sendable", new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.AnyObject"), new NamedTypeSpec("Swift.Sendable") }) },
    };

    // ----- the decision ---------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AnyObjectShapes))]
    public void IsAnyObjectExistential_AcceptsAnyObjectAndMarkerCompositions(string shape, ProtocolListTypeSpec spec)
        => Assert.True(ExistentialHandler.IsAnyObjectExistential(spec, CreateTypeDatabase()), shape);

    [Fact]
    public void IsAnyObjectExistential_RejectsEveryOtherExistential()
    {
        var db = CreateTypeDatabase();
        // Bare Any and marker-only compositions are the four-word zero-witness container.
        Assert.False(ExistentialHandler.IsAnyObjectExistential(new ProtocolListTypeSpec(), db));
        Assert.False(ExistentialHandler.IsAnyObjectExistential(
            new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Sendable") }), db));
        // A protocol with witnesses carries them in its container, class-bound or not.
        Assert.False(ExistentialHandler.IsAnyObjectExistential(
            new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.Tagged") }), db));
        Assert.False(ExistentialHandler.IsAnyObjectExistential(
            new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.AnyObject"), new NamedTypeSpec("TestModule.Tagged") }), db));
    }

    [Fact]
    public void Projection_AnyObject_BorrowsAndReadsOnlyTheReference()
    {
        var projection = new TypeProjectionFactory().Project(
            new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.AnyObject") }),
            new ProjectionContext { TypeDatabase = CreateTypeDatabase(), IsParameter = true });

        var existential = Assert.IsType<ExistentialProjection>(projection);
        Assert.True(existential.IsAnyObject);
        Assert.Equal("object", existential.PublicType);

        Assert.Contains(ExistentialHandler.AnyObjectArgumentFactory + "(p, out _, out _)", existential.GetParameterPlan("p").PInvokeExpression);
        // A value Swift consumes (a reverse-dispatch result) gets a retain of its own.
        Assert.Equal(ExistentialHandler.AnyObjectOwnedHandOff("r"), existential.GetOwnedParameterElementConversion("r"));
        // Every read boxes the reference in the class-bound carrier, which releases only the reference.
        Assert.Contains("classBound: true", existential.GetReturnElementConversion("c"));
        Assert.Contains("classBound: true", existential.GetOwnedReturnElementConversion("c"));
        Assert.DoesNotContain("GetOrCreate<object>", existential.GetParameterPlan("p").PInvokeExpression);
    }

    [Fact]
    public void Projection_BareAny_IsNotTheAnyObjectLane()
    {
        // The control: the zero-witness `object` existential keeps its Box/Unbox arm.
        var projection = new TypeProjectionFactory().Project(new ProtocolListTypeSpec(),
            new ProjectionContext { TypeDatabase = CreateTypeDatabase(), IsParameter = true });

        var existential = Assert.IsType<ExistentialProjection>(projection);
        Assert.False(existential.IsAnyObject);
        Assert.DoesNotContain(ExistentialHandler.AnyObjectArgumentFactory, existential.GetParameterPlan("p").PInvokeExpression);
    }

    [Fact]
    public void ResultRead_ReadsOneWordIntoAClassBoundCarrier()
    {
        var read = ExistentialHandler.AnyObjectResultToObject("resultPtr");
        Assert.Contains("ExistentialContainerFactory.ReadObjectReference(resultPtr)", read);
        Assert.Contains("classBound: true", read);
        Assert.DoesNotContain("MarshalFromSwift", read);
    }

    // ----- emitted wrappers -----------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AnyObjectShapes))]
    public void CdeclMethod_Parameter_PassesACellBothSidesAgreeOn(string shape, ProtocolListTypeSpec parameterType)
    {
        var (csOutput, swiftOutput) = EmitCdeclMethod(parameterType);

        Assert.Contains("(object sender)", csOutput.Replace("( ", "("));
        Assert.Contains($"{ExistentialHandler.AnyObjectArgumentFactory}(sender, out senderOwns, out senderKeepAlive)", csOutput);
        Assert.DoesNotContain("GetOrCreate<object>", csOutput);
        // The Swift side copies the value out of the cell; it never treats the pointer as the object.
        Assert.Contains("sender.load(as: ", swiftOutput);
        Assert.DoesNotContain("fromOpaque(sender)", swiftOutput);
        _ = shape;
    }

    [Theory]
    [MemberData(nameof(AnyObjectShapes))]
    public void CdeclMethod_Return_WritesAndReadsTheResultBuffer(string shape, ProtocolListTypeSpec returnType)
    {
        var (csOutput, swiftOutput) = EmitCdeclMethod(new NamedTypeSpec("Swift.Int"), returnType: returnType);

        Assert.Contains("resultPtr.initializeMemory(as: (any Swift.AnyObject", swiftOutput);
        Assert.DoesNotContain("passRetained", swiftOutput);
        Assert.Contains("ExistentialContainerFactory.ReadObjectReference(resultPtr), classBound: true)", csOutput);
        Assert.DoesNotContain("MarshalFromSwift<global::Swift.Runtime.ExistentialContainer1>", csOutput);
        _ = shape;
    }

    [Fact]
    public void ThrowingCdeclMethod_Return_ReadsTheBufferOnlyAfterTheErrorCheck()
    {
        // On the error path Swift writes no result, so the read must follow the error check.
        var anyObject = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.AnyObject") });
        var (csOutput, swiftOutput) = EmitCdeclMethod(new NamedTypeSpec("Swift.Int"), returnType: anyObject, throws: true);

        Assert.Contains("resultPtr.initializeMemory(as: (any Swift.AnyObject).self", swiftOutput);
        var errorCheck = Regex.Match(csOutput, @"if \(errorPtr != (global::System\.)?IntPtr\.Zero\)");
        int read = csOutput.IndexOf("ReadObjectReference(resultPtr)", System.StringComparison.Ordinal);
        Assert.True(errorCheck.Success, "the error is checked");
        Assert.True(read > errorCheck.Index, "the result is read after the error check");
    }

    #region Helpers

    private static (string csOutput, string swiftOutput) EmitCdeclMethod(TypeSpec parameterType, TypeSpec? returnType = null,
        bool isAsync = false, bool throws = false)
    {
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestModuleSwiftBindings";
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var method = new MethodDecl
        {
            Name = "handle",
            MangledName = "$s10TestModule6LoaderC6handleyyyXlF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg(string.Empty, returnType ?? TupleTypeSpec.Empty, moduleDecl),
                CreateArg("sender", parameterType, moduleDecl),
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = throws,
            IsAsync = isAsync,
            IsSynthesizedAccessor = false,
        };
        parentDecl.Methods.Add(method);
        method.UsesCdeclMethodWrapper = true;

        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        var env = new MethodEnvironment(method, typeDatabase);
        var conductor = new Conductor(new NullLoggerFactory());
        handler.Emit(new CSharpWriter(csOutput), new SwiftWriter(swiftOutput), env, conductor, TypeHandlerContext.Empty);

        return (csOutput.ToString(), swiftOutput.ToString());
    }

    private static ArgumentDecl CreateArg(string name, TypeSpec typeSpec, ModuleDecl moduleDecl) => new()
    {
        Name = name,
        PrivateName = name,
        SwiftTypeSpec = typeSpec,
        IsInOut = false,
        IsGeneric = false,
        ParentDecl = null,
        ModuleDecl = moduleDecl,
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
            ModuleDecl = moduleDecl,
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
                CSharpTypeName = CSharpTypeName.NIntType,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
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
                Kind = TypeRecordKind.Class,
            });
        typeDatabase.AddModuleDatabase(module);

        return typeDatabase;
    }

    #endregion
}
