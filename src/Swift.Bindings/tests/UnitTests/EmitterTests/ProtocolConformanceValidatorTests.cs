#nullable enable
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for ProtocolConformanceValidator, specifically the A7 AnyType interface guard.
/// </summary>
public class ProtocolConformanceValidatorTests
{
    #region A7 — AnyType Interface Conformance Guard

    [Fact]
    public void CanFullyImplementProtocol_ProtocolHasAnyTypeMethod_ReturnsFalse()
    {
        // Protocol with a method whose parameter has an unresolvable type → AnyType fallback.
        // Concrete type can't implement the interface, so CanFullyImplementProtocol returns false.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = CreateProtocolWithMethod("Parser", "parse", "UnknownModule.Foo", moduleDecl);
        moduleDecl.Protocols.Add(protocolDecl);

        // Create a concrete type (doesn't matter what's on it — the protocol check fails first)
        var concreteType = CreateStructDecl("MyParser", moduleDecl);

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var result = validator.CanFullyImplementProtocol(concreteType, protocolDecl);

        Assert.False(result);
    }

    [Fact]
    public void CanFullyImplementProtocol_ProtocolHasCleanMethods_ReturnsTrue()
    {
        // Protocol with all-resolvable types → concrete type with matching members → true.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = new ProtocolDecl
        {
            Name = "Counter",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Counter"),
            MangledName = "$s10TestModule7CounterP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = false,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>
            {
                CreateVoidMethod("increment", moduleDecl)
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Protocols.Add(protocolDecl);

        // Concrete type with matching method
        var concreteType = CreateStructDecl("BasicCounter", moduleDecl);
        var concreteMethod = CreateVoidMethod("increment", moduleDecl);
        concreteMethod.ParentDecl = concreteType;
        concreteType.Methods.Add(concreteMethod);

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var result = validator.CanFullyImplementProtocol(concreteType, protocolDecl);

        Assert.True(result);
    }

    [Fact]
    public void CanFullyImplementProtocol_ProtocolHasAnyTypeProperty_ReturnsFalse()
    {
        // Protocol with a property of unresolvable type → AnyType fallback → false.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = new ProtocolDecl
        {
            Name = "DataSource",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.DataSource"),
            MangledName = "$s10TestModule10DataSourceP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = false,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>
            {
                new()
                {
                    Name = "data",
                    SwiftTypeSpec = new NamedTypeSpec("UnknownModule.Data"),
                    IsStatic = false,
                    HasStorage = false,
                    Accessors = new List<AccessorDecl>
                    {
                        new GetAccessorDecl
                        {
                            Method = new MethodDecl
                            {
                                Name = "data_Get",
                                MangledName = "$s10TestModule10DataSourceP4dataSivg",
                                MethodType = MethodType.Instance,
                                IsConstructor = false,
                                CSSignature = new List<ArgumentDecl>
                                {
                                    CreateArgument(string.Empty, new NamedTypeSpec("UnknownModule.Data"), moduleDecl)
                                },
                                GenericParameters = new List<GenericArgumentDecl>(),
                                ParentDecl = null,
                                ModuleDecl = moduleDecl,
                                Throws = false,
                                IsAsync = false,
                                Visibility = Visibility.Public
                            }
                        }
                    },
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            Methods = new List<MethodDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Protocols.Add(protocolDecl);

        var concreteType = CreateStructDecl("MyDataSource", moduleDecl);

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var result = validator.CanFullyImplementProtocol(concreteType, protocolDecl);

        Assert.False(result);
    }

    [Fact]
    public void CanFullyImplementProtocol_ProtocolHasGenericParam_ReturnsFalse()
    {
        // Protocol method with a generic type parameter (τ_0_0) that projects to AnyType → false.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var genericParamType = new NamedTypeSpec("τ_0_0");

        var protocolDecl = new ProtocolDecl
        {
            Name = "Transformer",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Transformer"),
            MangledName = "$s10TestModule11TransformerP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = false,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>
            {
                new()
                {
                    Name = "transform",
                    MangledName = "$s10TestModule11TransformerP9transformyyF",
                    MethodType = MethodType.Instance,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl),
                        CreateArgument("input", genericParamType, moduleDecl)
                    },
                    GenericParameters = new List<GenericArgumentDecl>(),
                    ParentDecl = null,
                    ModuleDecl = moduleDecl,
                    Throws = false,
                    IsAsync = false,
                    Visibility = Visibility.Public
                }
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Protocols.Add(protocolDecl);

        var concreteType = CreateStructDecl("MyTransformer", moduleDecl);

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var result = validator.CanFullyImplementProtocol(concreteType, protocolDecl);

        Assert.False(result);
    }

    #endregion

    #region P1 Fix — Nested AnyType Detection

    [Fact]
    public void CanFullyImplementProtocol_ProtocolHasClosureWithAnyTypeArg_ReturnsFalse()
    {
        // Protocol with a closure param like (UnknownModule.Foo) -> () projects to Action<AnyType>.
        // The nested AnyType must be detected and rejected.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        // Closure type: (UnknownModule.Foo) -> ()
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new NamedTypeSpec("UnknownModule.Foo")),
            TupleTypeSpec.Empty);

        var protocolDecl = new ProtocolDecl
        {
            Name = "Handler",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Handler"),
            MangledName = "$s10TestModule7HandlerP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = false,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>
            {
                new()
                {
                    Name = "onEvent",
                    MangledName = "$s10TestModule7HandlerP7onEventyyF",
                    MethodType = MethodType.Instance,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl),
                        CreateArgument("callback", closureType, moduleDecl)
                    },
                    GenericParameters = new List<GenericArgumentDecl>(),
                    ParentDecl = null,
                    ModuleDecl = moduleDecl,
                    Throws = false,
                    IsAsync = false,
                    Visibility = Visibility.Public
                }
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Protocols.Add(protocolDecl);

        var concreteType = CreateStructDecl("MyHandler", moduleDecl);

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var result = validator.CanFullyImplementProtocol(concreteType, protocolDecl);

        Assert.False(result);
    }

    [Fact]
    public void CanFullyImplementProtocol_ProtocolHasTupleWithAnyType_ReturnsFalse()
    {
        // Protocol with a tuple param containing an unresolvable type:
        // (Swift.Int, UnknownModule.Bar) → (Int64, AnyType) — nested AnyType must be caught.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var tupleType = new TupleTypeSpec(new TypeSpec[]
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("UnknownModule.Bar")
        });

        var protocolDecl = new ProtocolDecl
        {
            Name = "Processor",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Processor"),
            MangledName = "$s10TestModule9ProcessorP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = false,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>
            {
                new()
                {
                    Name = "handle",
                    MangledName = "$s10TestModule9ProcessorP6handleyyF",
                    MethodType = MethodType.Instance,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl),
                        CreateArgument("pair", tupleType, moduleDecl)
                    },
                    GenericParameters = new List<GenericArgumentDecl>(),
                    ParentDecl = null,
                    ModuleDecl = moduleDecl,
                    Throws = false,
                    IsAsync = false,
                    Visibility = Visibility.Public
                }
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Protocols.Add(protocolDecl);

        var concreteType = CreateStructDecl("MyProcessor", moduleDecl);

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var result = validator.CanFullyImplementProtocol(concreteType, protocolDecl);

        Assert.False(result);
    }

    #endregion

    #region Async CancellationToken Signature Consistency

    [Fact]
    public void CanFullyImplementProtocol_AsyncMethod_IncludesCancellationTokenInSignature()
    {
        // Async protocol methods now include CancellationToken in the interface.
        // The validator's BuildInterfaceMethodSignature must also include CT
        // so the concrete type's matching method (which also has CT) passes validation.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        // Protocol with async method
        var protocolDecl = new ProtocolDecl
        {
            Name = "Loader",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Loader"),
            MangledName = "$s10TestModule6LoaderP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = false,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>
            {
                new()
                {
                    Name = "load",
                    MangledName = "$s10TestModule6LoaderP4loadyyYaKF",
                    MethodType = MethodType.Instance,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl)
                    },
                    GenericParameters = new List<GenericArgumentDecl>(),
                    ParentDecl = null,
                    ModuleDecl = moduleDecl,
                    Throws = true,
                    IsAsync = true,
                    Visibility = Visibility.Public
                }
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Protocols.Add(protocolDecl);

        // Concrete type with matching async method
        var concreteType = CreateStructDecl("MyLoader", moduleDecl);
        var asyncMethod = new MethodDecl
        {
            Name = "load",
            MangledName = "$s10TestModule8MyLoaderV4loadyyYaKF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = concreteType,
            ModuleDecl = moduleDecl,
            Throws = true,
            IsAsync = true,
            Visibility = Visibility.Public
        };
        concreteType.Methods.Add(asyncMethod);

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var result = validator.CanFullyImplementProtocol(concreteType, protocolDecl);

        // Both protocol and concrete async methods include CancellationToken → match → true
        Assert.True(result);
    }

    #endregion

    #region Bug #1 Regression — Subscript with Bound Generic Return

    [Fact]
    public void CanFullyImplementProtocol_SubscriptWithBoundGenericReturn_DoesNotCrash()
    {
        // Bug #1 regression: subscript returning Array<UnknownType> should gracefully
        // return false (not throw NotSupportedException). The protocol must use
        // Subscripts (not Methods) to exercise the subscript matching code path.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var arrayReturn = new NamedTypeSpec("Swift.Array");
        arrayReturn.GenericParameters.Add(new NamedTypeSpec("UnknownModule.Foo"));

        var subscriptDecl = new SubscriptDecl
        {
            Name = "subscript",
            MangledName = "$s10TestModule9ContainerPySaySiGSicig",
            IsStatic = false,
            ReturnTypeSpec = arrayReturn,
            IndexParameters = new List<ArgumentDecl>
            {
                CreateArgument("index", new NamedTypeSpec("Swift.Int"), moduleDecl)
            },
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl
                {
                    Method = new MethodDecl
                    {
                        Name = "subscript_Get",
                        MangledName = "$s10TestModule9ContainerPySaySiGSicig",
                        MethodType = MethodType.Instance,
                        IsConstructor = false,
                        CSSignature = new List<ArgumentDecl>
                        {
                            CreateArgument(string.Empty, arrayReturn, moduleDecl),
                            CreateArgument("index", new NamedTypeSpec("Swift.Int"), moduleDecl)
                        },
                        GenericParameters = new List<GenericArgumentDecl>(),
                        ParentDecl = null,
                        ModuleDecl = moduleDecl,
                        Throws = false,
                        IsAsync = false,
                        Visibility = Visibility.Public
                    }
                }
            },
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };

        var protocolDecl = new ProtocolDecl
        {
            Name = "Container",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Container"),
            MangledName = "$s10TestModule9ContainerP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = false,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Subscripts = new List<SubscriptDecl> { subscriptDecl },
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Protocols.Add(protocolDecl);

        var concreteType = CreateStructDecl("MyContainer", moduleDecl);

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        // Should not throw — gracefully returns false (subscript iteration +
        // GetSubscriptSignatureKey + FindMatchingSubscript exercised)
        var result = validator.CanFullyImplementProtocol(concreteType, protocolDecl);

        Assert.False(result);
    }

    #endregion

    #region Member Matching — Property Conformance

    [Fact]
    public void CanFullyImplementProtocol_PropertyGetOnly_ConcreteHasGetSet_ReturnsTrue()
    {
        // Protocol requires get-only property, concrete has get and set → should still match
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = new ProtocolDecl
        {
            Name = "Readable",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Readable"),
            MangledName = "$s10TestModule8ReadableP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = false,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        protocolDecl.Properties.Add(
            CreatePropertyDecl("count", new NamedTypeSpec("Swift.Int"), moduleDecl, hasGetter: true, hasSetter: false, accessorParent: protocolDecl));
        moduleDecl.Protocols.Add(protocolDecl);

        var concreteType = CreateStructDecl("MyReadable", moduleDecl);
        concreteType.Properties.Add(
            CreatePropertyDecl("count", new NamedTypeSpec("Swift.Int"), moduleDecl, hasGetter: true, hasSetter: true, accessorParent: concreteType));

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var result = validator.CanFullyImplementProtocol(concreteType, protocolDecl);

        Assert.True(result);
    }

    [Fact]
    public void CanFullyImplementProtocol_PropertyGetSet_ConcreteHasGetOnly_ReturnsFalse()
    {
        // Protocol requires get/set, concrete only has get → should fail
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = new ProtocolDecl
        {
            Name = "Writable",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Writable"),
            MangledName = "$s10TestModule8WritableP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = false,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        protocolDecl.Properties.Add(
            CreatePropertyDecl("count", new NamedTypeSpec("Swift.Int"), moduleDecl, hasGetter: true, hasSetter: true, accessorParent: protocolDecl));
        moduleDecl.Protocols.Add(protocolDecl);

        var concreteType = CreateStructDecl("MyWritable", moduleDecl);
        concreteType.Properties.Add(
            CreatePropertyDecl("count", new NamedTypeSpec("Swift.Int"), moduleDecl, hasGetter: true, hasSetter: false, accessorParent: concreteType));

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var result = validator.CanFullyImplementProtocol(concreteType, protocolDecl);

        Assert.False(result);
    }

    [Fact]
    public void CanFullyImplementProtocol_InheritedProtocol_ChecksRecursively()
    {
        // Protocol B inherits from Protocol A. Concrete type must satisfy both.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        // Protocol A: has method "doA"
        var protocolA = new ProtocolDecl
        {
            Name = "ProtoA",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ProtoA"),
            MangledName = "$s10TestModule6ProtoAP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = false,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>
            {
                CreateVoidMethod("doA", moduleDecl)
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Protocols.Add(protocolA);

        // Protocol B inherits from A, adds method "doB"
        var protocolB = new ProtocolDecl
        {
            Name = "ProtoB",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ProtoB"),
            MangledName = "$s10TestModule6ProtoBP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>
            {
                new NamedTypeSpec("TestModule.ProtoA")
            },
            IsClassBound = false,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>
            {
                CreateVoidMethod("doB", moduleDecl)
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Protocols.Add(protocolB);

        // Concrete type with both doA and doB
        var concreteType = CreateStructDecl("ConcreteAB", moduleDecl);
        var methodA = CreateVoidMethod("doA", moduleDecl);
        methodA.ParentDecl = concreteType;
        concreteType.Methods.Add(methodA);
        var methodB = CreateVoidMethod("doB", moduleDecl);
        methodB.ParentDecl = concreteType;
        concreteType.Methods.Add(methodB);

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var result = validator.CanFullyImplementProtocol(concreteType, protocolB);

        Assert.True(result);
    }

    #endregion

    #region Helper Methods

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

    private static StructDecl CreateStructDecl(string name, ModuleDecl moduleDecl)
    {
        return new StructDecl
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
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = false,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa"
        };
    }

    private static ArgumentDecl CreateArgument(string name, TypeSpec typeSpec, ModuleDecl moduleDecl)
    {
        return new ArgumentDecl
        {
            SwiftTypeSpec = typeSpec,
            Name = name,
            PrivateName = name,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
    }

    private static MethodDecl CreateVoidMethod(string name, ModuleDecl moduleDecl)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule{name}yyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    private static PropertyDecl CreatePropertyDecl(string name, TypeSpec typeSpec, ModuleDecl moduleDecl, bool hasGetter, bool hasSetter, BaseDecl? accessorParent = null)
    {
        var accessors = new List<AccessorDecl>();
        if (hasGetter)
        {
            accessors.Add(new GetAccessorDecl
            {
                Method = new MethodDecl
                {
                    Name = $"{name}_Get",
                    MangledName = $"$s10TestModule{name}Sivg",
                    MethodType = MethodType.Instance,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        CreateArgument(string.Empty, typeSpec, moduleDecl)
                    },
                    GenericParameters = new List<GenericArgumentDecl>(),
                    ParentDecl = accessorParent,
                    ModuleDecl = moduleDecl,
                    Throws = false,
                    IsAsync = false,
                    Visibility = Visibility.Public
                }
            });
        }
        if (hasSetter)
        {
            accessors.Add(new SetAccessorDecl
            {
                Method = new MethodDecl
                {
                    Name = $"{name}_Set",
                    MangledName = $"$s10TestModule{name}Sivs",
                    MethodType = MethodType.Instance,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl),
                        CreateArgument("newValue", typeSpec, moduleDecl)
                    },
                    GenericParameters = new List<GenericArgumentDecl>(),
                    ParentDecl = accessorParent,
                    ModuleDecl = moduleDecl,
                    Throws = false,
                    IsAsync = false,
                    Visibility = Visibility.Public
                }
            });
        }

        return new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = typeSpec,
            IsStatic = false,
            HasStorage = false,
            Accessors = accessors,
            ParentDecl = accessorParent,
            ModuleDecl = moduleDecl
        };
    }

    private static ProtocolDecl CreateProtocolWithMethod(string protocolName, string methodName, string paramType, ModuleDecl moduleDecl)
    {
        return new ProtocolDecl
        {
            Name = protocolName,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{protocolName}"),
            MangledName = $"$s10TestModule{protocolName.Length}{protocolName}P",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = false,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>
            {
                new()
                {
                    Name = methodName,
                    MangledName = $"$s10TestModule{protocolName.Length}{protocolName}P{methodName}yyF",
                    MethodType = MethodType.Instance,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl),
                        CreateArgument("input", new NamedTypeSpec(paramType), moduleDecl)
                    },
                    GenericParameters = new List<GenericArgumentDecl>(),
                    ParentDecl = null,
                    ModuleDecl = moduleDecl,
                    Throws = false,
                    IsAsync = false,
                    Visibility = Visibility.Public
                }
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
    }

    #endregion
}
