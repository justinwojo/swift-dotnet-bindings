// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// A generic parameter bound to classes is one object reference in Swift's lowering: a direct
/// Swift-convention call passes it, and its Optional, by value and returns it in the first result
/// register, with no buffer address and no indirect result. A parameter is class-bound through
/// <c>AnyObject</c>, a class-bound protocol of its own module, or a Swift-only Apple protocol that
/// no type database records (<c>Combine.ObservableObject</c>). An unconstrained parameter stays
/// address-only.
/// </summary>
public class ClassBoundGenericReferenceTests
{
    // ── Apple protocols no database records ────────────────────────────

    [Theory]
    [InlineData("Combine.ObservableObject", true)]
    [InlineData("_Concurrency.Actor", true)]
    [InlineData("Combine.Publisher", false)]
    [InlineData("Swift.Identifiable", false)]
    [InlineData("ObservableObject", false)]
    public void KnownClassBoundAppleProtocols(string name, bool expected)
        => Assert.Equal(expected, AppleFrameworkRegistry.IsKnownClassBoundProtocol(name));

    [Fact]
    public void ProtocolRefiningObservableObject_IsClassBound_WithNoRecordOfIt()
    {
        var observed = TestDecls.Protocol("Observed", "TestModule");
        observed.InheritedProtocols.Add(new NamedTypeSpec("Combine.ObservableObject"));

        Assert.True(SwiftProtocolBounds.IsClassBound(observed, _ => null, _ => null));
    }

    [Fact]
    public void ProtocolRefiningAPlainAppleProtocol_IsNotClassBound()
    {
        var keyed = TestDecls.Protocol("Keyed", "TestModule");
        keyed.InheritedProtocols.Add(new NamedTypeSpec("Swift.Identifiable"));

        Assert.False(SwiftProtocolBounds.IsClassBound(keyed, _ => null, _ => null));
    }

    // ── Which generic parameters are class-bound ───────────────────────

    [Theory]
    [InlineData("Swift.AnyObject", true)]
    [InlineData("TestModule.Revisioned", true)]
    [InlineData("Combine.ObservableObject", true)]
    [InlineData("TestModule.Keyed", false)]
    [InlineData(null, false)]
    public void ClassBoundGenericParameter_ByConstraint(string? constraint, bool expected)
    {
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateGenericClass(moduleDecl, constraint);
        var method = CreateMethod("echo", parent, moduleDecl, Generic(), Param("value", Generic()));

        var env = new MethodEnvironment(method, typeDb);
        Assert.Equal(expected, MarshallingHelpers.IsClassBoundGenericParameter(env, new NamedTypeSpec("τ_0_0")));
        Assert.Equal(expected, MarshallingHelpers.PassesGenericAsSwiftReference(env, method.CSSignature[1]));
        Assert.Equal(expected, MarshallingHelpers.ReturnsGenericAsSwiftReference(env));
    }

    [Fact]
    public void ACdeclWrapperKeepsTheAddress()
    {
        // The @_cdecl wrapper loads the value from the address it is given; only a Swift-convention
        // call reads the reference itself.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateGenericClass(moduleDecl, "TestModule.Revisioned");
        var method = CreateMethod("echo", parent, moduleDecl, Generic(), Param("value", Generic()));
        method.UsesCdeclMethodWrapper = true;

        var env = new MethodEnvironment(method, typeDb);
        Assert.False(MarshallingHelpers.PassesGenericAsSwiftReference(env, method.CSSignature[1]));
        Assert.False(MarshallingHelpers.ReturnsGenericAsSwiftReference(env));
    }

    // ── What the direct import declares and passes ─────────────────────

    [Theory]
    [InlineData("TestModule.Revisioned")]
    [InlineData("Combine.ObservableObject")]
    public void ClassBoundArgumentAndResult_CrossAsOneReference(string constraint)
    {
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateGenericClass(moduleDecl, constraint);
        var method = CreateMethod("exchange", parent, moduleDecl, Generic(), Param("other", Generic()));

        var (pinvoke, body) = Emit(method, typeDb);

        Assert.Matches(@"partial IntPtr PInvoke_exchange_\w+\( IntPtr otherPayloadReference,  SwiftSelf self\);", pinvoke);
        Assert.Contains("IntPtr otherPayloadReference = *(IntPtr*)otherPayload;", body);
        // The returned reference is stored in the result buffer the generic return reads from.
        Assert.Matches(@"\*\(IntPtr\*\)_cdeclBuf = PInvoke_exchange_\w+\(otherPayloadReference, self\);", body);
    }

    [Fact]
    public void OptionalOfClassBoundResult_IsOneNullableReference()
    {
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateGenericClass(moduleDecl, "TestModule.Revisioned");
        var method = CreateMethod("current", parent, moduleDecl, Optional(Generic()), Param("atLeast", new NamedTypeSpec("Swift.Int")));

        var env = new MethodEnvironment(method, typeDb);
        Assert.True(MarshallingHelpers.ReturnsGenericAsSwiftReference(env));
        var (pinvoke, body) = Emit(method, typeDb);
        Assert.Matches(@"partial IntPtr PInvoke_current_\w+\( nint atLeast,  SwiftSelf self\);", pinvoke);
        Assert.Matches(@"\*\(IntPtr\*\)_cdeclBuf = PInvoke_current_\w+\(atLeast, self\);", body);
    }

    [Fact]
    public void UnconstrainedArgumentAndResult_StayAddressOnly()
    {
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateGenericClass(moduleDecl, constraint: null);
        var method = CreateMethod("exchange", parent, moduleDecl, Generic(), Param("other", Generic()));

        var (pinvoke, body) = Emit(method, typeDb);

        Assert.DoesNotContain("otherPayloadReference", pinvoke);
        Assert.Contains("SwiftIndirectResult", pinvoke);
        Assert.DoesNotContain("*(IntPtr*)_cdeclBuf = ", body);
    }

    [Fact]
    public void InOutClassBoundArgument_StaysAnAddress()
    {
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateGenericClass(moduleDecl, "TestModule.Revisioned");
        var value = Param("value", Generic());
        value.IsInOut = true;
        var method = CreateMethod("swapIn", parent, moduleDecl, new NamedTypeSpec("()"), value);

        var env = new MethodEnvironment(method, typeDb);
        Assert.False(MarshallingHelpers.PassesGenericAsSwiftReference(env, value));
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static (string PInvoke, string Body) Emit(MethodDecl method, TypeDatabase typeDb)
    {
        var env = (MethodEnvironment)new MethodHandler(new NullLogger<MethodHandler>()).Marshal(method, typeDb);
        var pinvokeText = new StringWriter();
        var pinvokeWriter = new CSharpWriter(pinvokeText);
        PInvokeEmitter.EmitPInvoke(pinvokeWriter, env, new SignatureHandler(env));
        pinvokeWriter.Flush();

        var bodyText = new StringWriter();
        var bodyWriter = new CSharpWriter(bodyText);
        new WrapperEmitter(env, new SignatureHandler(env)).EmitMethod(bodyWriter, new SwiftWriter(new StringWriter()));
        bodyWriter.Flush();
        return (pinvokeText.ToString(), bodyText.ToString());
    }

    private static NamedTypeSpec Generic() => new("τ_0_0");

    private static NamedTypeSpec Optional(TypeSpec inner) => new("Swift.Optional", inner);

    private static ArgumentDecl Param(string name, NamedTypeSpec type) => new()
    {
        SwiftTypeSpec = type,
        Name = name,
        PrivateName = name,
        IsInOut = false,
        IsGeneric = TypeSpecHelpers.IsGenericTypeParameter(type.Name),
        ParentDecl = null,
        ModuleDecl = null,
    };

    private static ClassDecl CreateGenericClass(ModuleDecl moduleDecl, string? constraint)
    {
        var conformances = new List<GenericParameterConformance>();
        if (constraint is not null)
        {
            conformances.Add(new GenericParameterConformance(
                new[] { "τ_0_0" }, SwiftTypeName.FromModuleQualifiedName(constraint), ConformanceKind.Protocol));
        }
        var decl = new ClassDecl
        {
            Name = "Holder",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Holder"),
            MangledName = "$s10TestModule6HolderCN",
            IsFinal = true,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("τ_0_0", "V", conformances, new List<GenericParameterConformance>())
            },
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };
        moduleDecl.Types.Add(decl);
        return decl;
    }

    private static MethodDecl CreateMethod(string name, TypeDecl parent, ModuleDecl moduleDecl, NamedTypeSpec returnType, params ArgumentDecl[] parameters)
    {
        var signature = new List<ArgumentDecl> { Param("", returnType) };
        foreach (var parameter in parameters)
        {
            parameter.ModuleDecl = moduleDecl;
            signature.Add(parameter);
        }
        signature[0].ModuleDecl = moduleDecl;
        var method = new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule6HolderC{name.Length}{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            CSSignature = signature,
            // The parser records the parent's generic parameters in each member's signature.
            GenericParameters = new List<GenericArgumentDecl>(parent.GenericParameters),
            ParentDecl = parent,
            ModuleDecl = moduleDecl,
        };
        parent.Methods.Add(method);
        return method;
    }

    private static (ModuleDecl ModuleDecl, TypeDatabase TypeDb) CreateEnvironment()
    {
        var typeDb = new TypeDatabase();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.NIntType,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDb.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Holder"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Holder"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Holder"),
                MetadataAccessor = "$s10TestModule6HolderCMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Class
            });
        RegisterProtocol(testModule, "Revisioned", TypeRecordFlags.ClassBound);
        RegisterProtocol(testModule, "Keyed", TypeRecordFlags.None);
        typeDb.AddModuleDatabase(testModule);

        var moduleDecl = new ModuleDecl
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
        return (moduleDecl, typeDb);
    }

    private static void RegisterProtocol(ModuleTypeDatabase module, string name, TypeRecordFlags flags)
        => module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "I" + name),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
                MetadataAccessor = string.Empty,
                Flags = flags,
                Kind = TypeRecordKind.Protocol
            });
}
