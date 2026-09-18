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
/// A parameter typed as a zero-witness existential — bare <c>Any</c>, or a composition of marker
/// protocols only such as <c>any Sendable</c> — projects to <c>object</c> and lowers to the four-word
/// <c>ExistentialContainer0</c>. On an @_cdecl method wrapper the argument travels by pointer and the
/// Swift side copies it out, so the C# side must box the value into a container it owns and destroy
/// that container after the call. No plain C# value implements
/// <c>ISwiftExistentialConvertible&lt;ExistentialContainer0&gt;</c>, so casting to it throws
/// <see cref="System.InvalidCastException"/> on every call.
/// </summary>
public class ZeroWitnessExistentialParameterEmitterTests
{
    public static TheoryData<string, ProtocolListTypeSpec> ZeroWitnessExistentials => new()
    {
        { "Any", new ProtocolListTypeSpec() },
        { "any Sendable", new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Sendable") }) },
    };

    [Theory]
    [MemberData(nameof(ZeroWitnessExistentials))]
    public void CdeclMethod_ZeroWitnessParameter_BoxesPlainValue(string shape, ProtocolListTypeSpec parameterType)
    {
        var (csOutput, _) = EmitCdeclMethod(parameterType, isAsync: false);

        Assert.Contains("(object sender)", Normalize(csOutput));
        Assert.Contains("Swift.Runtime.ExistentialContainer0.Box(sender)", csOutput);
        Assert.DoesNotContain("ISwiftExistentialConvertible", csOutput);
        // The heap buffer is sized for the container that was boxed.
        Assert.Contains("Unsafe.SizeOf<Swift.Runtime.ExistentialContainer0>()", csOutput);
        _ = shape;
    }

    [Theory]
    [MemberData(nameof(ZeroWitnessExistentials))]
    public void CdeclMethod_ZeroWitnessParameter_DestroysOwnedContainerWithZeroWitnessLayout(string shape, ProtocolListTypeSpec parameterType)
    {
        // Box yields a +1 that the borrowing wrapper copies rather than consumes, so the finally must
        // run the destroy (owns-bit set) with the layout of the container Box produced.
        var (csOutput, _) = EmitCdeclMethod(parameterType, isAsync: false);

        Assert.Matches(@"\bsenderOwns = true;", csOutput);
        Assert.Matches(@"DestroyAndFreeExistential\(senderHeap, 0, senderOwns\)", csOutput);
        _ = shape;
    }

    [Theory]
    [MemberData(nameof(ZeroWitnessExistentials))]
    public void AsyncCdeclMethod_ZeroWitnessParameter_HandsOwnedContainerToCallbackCleanup(string shape, ProtocolListTypeSpec parameterType)
    {
        // The async continuation reads the buffer after the wrapper returns, so the release rides on
        // the holder: it must carry the owns-bit and the zero-witness layout, not a borrowed flag.
        var (csOutput, _) = EmitCdeclMethod(parameterType, isAsync: true);

        Assert.Contains("Swift.Runtime.ExistentialContainer0.Box(sender)", csOutput);
        Assert.DoesNotContain("ISwiftExistentialConvertible", csOutput);
        Assert.Matches(@"new ExistentialContainerHeap\(\(IntPtr\)senderHeap, senderOwns, 0, null\)", csOutput);
        _ = shape;
    }

    [Theory]
    [MemberData(nameof(ZeroWitnessExistentials))]
    public void CdeclMethod_ZeroWitnessReturn_UnboxesAndDestroysOwnedContainer(string shape, ProtocolListTypeSpec returnType)
    {
        // The wrapper hands the result back at +1 and the public type is 'object': the value must
        // come out of the container and the container's retain must be dropped, not leaked by a
        // borrowing Unbox or returned as the container struct itself.
        var (csOutput, _) = EmitCdeclMethod(new NamedTypeSpec("Swift.Int"), isAsync: false, returnType: returnType);

        Assert.Contains("ExistentialContainer0.UnboxOwned(", csOutput);
        Assert.DoesNotContain("ExistentialContainer0.Unbox(", csOutput);
        _ = shape;
    }

    [Theory]
    [MemberData(nameof(ZeroWitnessExistentials))]
    public void CdeclClosureCallback_ZeroWitnessArgument_ReceivesPlainValue(string shape, ProtocolListTypeSpec argumentType)
    {
        // Swift lends the argument to the callback, so the callback unboxes a borrowed container
        // into the plain value the delegate's 'object' parameter expects.
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);
        var closureTypeSpec = new ClosureTypeSpec(argumentType, new NamedTypeSpec("Swift.Int"));

        var output = new StringWriter();
        ClosureEmitter.EmitEscapingClosureCallback(
            new CSharpWriter(output), "callWithAny", "body", closureTypeSpec, closureHandler,
            "$s10TestModule6LoaderC11callWithAnyyS2iypXEF", useCdecl: true);

        var result = output.ToString();
        Assert.Contains("ExistentialContainer0.Unbox(", result);
        Assert.DoesNotContain("ExistentialContainer0.UnboxOwned(", result);
        _ = shape;
    }

    [Theory]
    [MemberData(nameof(ZeroWitnessExistentials))]
    public void Closure_ReturningZeroWitnessExistential_IsRefused(string shape, ProtocolListTypeSpec returnType)
    {
        // Swift returns the four-word container indirectly while a CallConvSwift signature returns
        // it in registers, so neither a C# callback nor an invoker of a Swift closure can carry it.
        var handler = new ClosureHandler(CreateTypeDatabase());

        Assert.False(handler.IsSupportedClosure(new ClosureTypeSpec(TupleTypeSpec.Empty, returnType)));
        Assert.False(handler.IsSupportedClosure(new ClosureTypeSpec(new NamedTypeSpec("Swift.Int"), returnType)));
        _ = shape;
    }

    [Theory]
    [MemberData(nameof(ZeroWitnessExistentials))]
    public void Closure_TakingZeroWitnessExistential_IsSupported(string shape, ProtocolListTypeSpec argumentType)
    {
        // The control for the refusal above: the same existential as an argument stays bound, so
        // the refusal is attributable to the return position alone.
        var handler = new ClosureHandler(CreateTypeDatabase());

        Assert.True(handler.IsSupportedClosure(new ClosureTypeSpec(argumentType, new NamedTypeSpec("Swift.Int"))));
        _ = shape;
    }

    #region Helpers

    private static string Normalize(string s) => Regex.Replace(s, @"\(\s+", "(");

    private static (string csOutput, string swiftOutput) EmitCdeclMethod(TypeSpec parameterType, bool isAsync, TypeSpec? returnType = null)
    {
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestModuleSwiftBindings";
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var method = new MethodDecl
        {
            Name = "handle",
            MangledName = "$s10TestModule6LoaderC6handleyyypF",
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
            Throws = false,
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
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.String"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct,
                InlineSize = 16,
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
