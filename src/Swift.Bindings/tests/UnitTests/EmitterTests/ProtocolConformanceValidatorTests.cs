#nullable enable
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for ProtocolConformanceValidator, specifically the A7 AnyType interface guard.
/// </summary>
public class ProtocolConformanceValidatorTests
{
    #region A7 — AnyType Interface Conformance Guard

    [Fact]
    public void CanFullyImplementProtocol_StaticSelfTypedProperty_ConcreteHasOwnTypedStatic_ReturnsTrue()
    {
        // `static var shared: Self` on a protocol that is NOT modeled as Self-requirement
        // (no associated-type path / same-type pin) cannot spell TSelf in its interface —
        // the member degrades to a `static virtual Swift.AnyType` throw-stub there. The
        // concrete type's own `static MyContainer Shared` then mismatches that AnyType,
        // but the mismatch is compile-benign (the static virtual default body means the
        // concrete member simply doesn't override). Rejecting the WHOLE conformance here
        // instead breaks every generic constraint on the protocol (CS0311).
        // Build the database from scratch so the concrete type is registered and its
        // static property projects to its real C# type (TestModule.MyContainer).
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        typeDatabase.AddModuleDatabase(swiftModule);
        var targetDb = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        targetDb.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyContainer"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "MyContainer"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyContainer"),
                MetadataAccessor = "$s10TestModule11MyContainerVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(targetDb);
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = new ProtocolDecl
        {
            Name = "SharedContainer",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.SharedContainer"),
            MangledName = "$s10TestModule15SharedContainerP",
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
                    Name = "shared",
                    SwiftTypeSpec = new NamedTypeSpec("τ_0_0"),
                    IsStatic = true,
                    HasStorage = false,
                    Accessors = new List<AccessorDecl>
                    {
                        new GetAccessorDecl
                        {
                            Method = new MethodDecl
                            {
                                Name = "shared_Get",
                                MangledName = "$s10TestModule15SharedContainerP6sharedxvgZ",
                                MethodType = MethodType.Static,
                                IsConstructor = false,
                                CSSignature = new List<ArgumentDecl>
                                {
                                    CreateArgument(string.Empty, new NamedTypeSpec("τ_0_0"), moduleDecl)
                                },
                                GenericParameters = new List<GenericArgumentDecl>(),
                                ParentDecl = null,
                                ModuleDecl = moduleDecl,
                                Throws = false,
                                IsAsync = false,
                                IsSynthesizedAccessor = false
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

        var concreteType = CreateStructDecl("MyContainer", moduleDecl);
        concreteType.Properties.Add(new PropertyDecl
        {
            Name = "shared",
            SwiftTypeSpec = new NamedTypeSpec("TestModule.MyContainer"),
            IsStatic = true,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl
                {
                    Method = new MethodDecl
                    {
                        Name = "shared_Get",
                        MangledName = "$s10TestModule11MyContainerV6sharedACvgZ",
                        MethodType = MethodType.Static,
                        IsConstructor = false,
                        CSSignature = new List<ArgumentDecl>
                        {
                            CreateArgument(string.Empty, new NamedTypeSpec("TestModule.MyContainer"), moduleDecl)
                        },
                        GenericParameters = new List<GenericArgumentDecl>(),
                        ParentDecl = concreteType,
                        ModuleDecl = moduleDecl,
                        Throws = false,
                        IsAsync = false,
                        IsSynthesizedAccessor = false
                    }
                }
            },
            ParentDecl = concreteType,
            ModuleDecl = moduleDecl
        });

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var result = validator.CanFullyImplementProtocol(concreteType, protocolDecl);

        Assert.True(result);
    }

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
                                IsSynthesizedAccessor = false
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

    #region Dropped-conformance reporting

    [Fact]
    public void CanFullyImplementProtocol_Gap_NamesTheFirstUnmetMethodRequirement()
    {
        // Same shape as CanFullyImplementProtocol_ProtocolHasAnyTypeMethod_ReturnsFalse: the drop is
        // real, and the gap is what makes it explainable — which requirement, of which kind.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var protocolDecl = CreateProtocolWithMethod("Parser", "parse", "UnknownModule.Foo", moduleDecl);
        moduleDecl.Protocols.Add(protocolDecl);
        var concreteType = CreateStructDecl("MyParser", moduleDecl);

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var satisfied = validator.CanFullyImplementProtocol(concreteType, protocolDecl, out var gap);

        Assert.False(satisfied);
        Assert.NotNull(gap);
        Assert.Equal(BindingItemKind.Method, gap!.Value.Kind);
        Assert.Equal("parse", gap.Value.RequirementName);
        Assert.False(string.IsNullOrWhiteSpace(gap.Value.Explanation));
        // The rendered form is what reaches the report, so it has to carry both facts.
        Assert.Contains("parse", gap.Value.ToString(), StringComparison.Ordinal);
        Assert.Contains("method", gap.Value.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CanFullyImplementProtocol_Gap_NamesTheUnmetPropertyRequirementWithPropertyKind()
    {
        // The kind travels with the name: "data" alone doesn't say whether to look for a property or
        // a method on the conforming type.
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
                CreatePropertyDecl("data", new NamedTypeSpec("UnknownModule.Data"), moduleDecl, hasGetter: true, hasSetter: false)
            },
            Methods = new List<MethodDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Protocols.Add(protocolDecl);
        var concreteType = CreateStructDecl("MyDataSource", moduleDecl);

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var satisfied = validator.CanFullyImplementProtocol(concreteType, protocolDecl, out var gap);

        Assert.False(satisfied);
        Assert.NotNull(gap);
        Assert.Equal(BindingItemKind.Property, gap!.Value.Kind);
        Assert.Equal("data", gap.Value.RequirementName);
    }

    [Fact]
    public void CanFullyImplementProtocol_Gap_SatisfiedConformanceReportsNoGap()
    {
        // The negative control the reporting sites depend on: a conformance that IS emitted must not
        // hand back a gap, or every bound type would grow a phantom dropped-conformance row.
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
            Methods = new List<MethodDecl> { CreateVoidMethod("increment", moduleDecl) },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Protocols.Add(protocolDecl);

        var concreteType = CreateStructDecl("BasicCounter", moduleDecl);
        var concreteMethod = CreateVoidMethod("increment", moduleDecl);
        concreteMethod.ParentDecl = concreteType;
        concreteType.Methods.Add(concreteMethod);

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var satisfied = validator.CanFullyImplementProtocol(concreteType, protocolDecl, out var gap);

        Assert.True(satisfied);
        Assert.Null(gap);
    }

    [Fact]
    public void CanFullyImplementProtocol_Gap_RecordsNothingInAnActiveReportSession()
    {
        // The overload is consulted speculatively (the closed-PAT projection loop asks about
        // conformances the main loop already handled). Recording from inside the validator would make
        // those speculative asks visible as drops; the recording decision belongs to the call site.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var protocolDecl = CreateProtocolWithMethod("Parser", "parse", "UnknownModule.Foo", moduleDecl);
        moduleDecl.Protocols.Add(protocolDecl);
        var concreteType = CreateStructDecl("MyParser", moduleDecl);

        ReportCollector.Start(moduleDecl);
        try
        {
            var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
            Assert.False(validator.CanFullyImplementProtocol(concreteType, protocolDecl, out _));

            var report = ReportCollector.Complete();
            Assert.NotNull(report);
            Assert.DoesNotContain(
                report!.SkippedItems,
                i => i.Reason == SkipReason.ConformanceNotFullyImplementable);
        }
        finally
        {
            ReportCollector.Reset();
        }
    }

    #endregion

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
                    IsSynthesizedAccessor = false
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
                    IsSynthesizedAccessor = false
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

        // The closure method with AnyType arg is skipped from the interface (mirrors ProtocolHandler:
        // HasAnyTypeGenericArgInSignature catches Action<AnyType>). With no interface requirements
        // remaining, the concrete type can fully implement the protocol.
        Assert.True(result);
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
                    IsSynthesizedAccessor = false
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
                    IsSynthesizedAccessor = false
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
            IsSynthesizedAccessor = false
        };
        concreteType.Methods.Add(asyncMethod);

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var result = validator.CanFullyImplementProtocol(concreteType, protocolDecl);

        // Both protocol and concrete async methods include CancellationToken → match → true
        Assert.True(result);
    }

    #endregion

    #region Conformance-keep agrees with emission-skip (CS0535 guard)

    // A non-generic protocol requires `func provideValue() async -> Int`. The interface emits
    // `Task<nint> ProvideValueAsync()`. When the concrete witness is an async method on an
    // UNSPECIALIZED GENERIC parent, the emission pipeline DROPS it (an async wrapper on a generic
    // parent can't supply the parent's type metadata + self through a direct CallConvSwift P/Invoke).
    // If the conformance is kept, the generic class declares `: IValueProvider` but never emits the
    // satisfying member → CS0535 at consumer compile time. So the conformance must be dropped.
    [Fact]
    public void CanFullyImplementProtocol_AsyncWitnessOnGenericParent_DropsConformance()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = CreateAsyncIntProvider(moduleDecl);
        moduleDecl.Protocols.Add(protocolDecl);

        // GENERIC class conformer: GenericParameters populated → IsGeneric == true.
        var concreteType = CreateClassDecl("ValueBox", moduleDecl);
        concreteType.GenericParameters = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl(
                TypeName: "T",
                SugaredTypeName: "T",
                GenericConformances: new List<GenericParameterConformance>(),
                AssosiatedTypeConformances: new List<GenericParameterConformance>())
        };
        concreteType.Methods.Add(CreateAsyncIntMethod(
            "provideValue", "$s10TestModule8ValueBoxC12provideValueSiyYaF", concreteType, moduleDecl));

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var result = validator.CanFullyImplementProtocol(concreteType, protocolDecl);

        // The async-on-generic-parent witness is dropped at emission → conformance must drop too.
        Assert.False(result);
    }

    // Discrimination control: the SAME async-Int witness on a NON-generic parent IS emittable, so
    // the conformance must be KEPT. Guards against the fix degenerating into "always reject async Int".
    [Fact]
    public void CanFullyImplementProtocol_AsyncWitnessOnNonGenericParent_KeepsConformance()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = CreateAsyncIntProvider(moduleDecl);
        moduleDecl.Protocols.Add(protocolDecl);

        var concreteType = CreateClassDecl("ValueHolder", moduleDecl);
        concreteType.Methods.Add(CreateAsyncIntMethod(
            "provideValue", "$s10TestModule11ValueHolderC12provideValueSiyYaF", concreteType, moduleDecl));

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var result = validator.CanFullyImplementProtocol(concreteType, protocolDecl);

        Assert.True(result);
    }

    // Companion to AsyncWitnessOnGenericParent_DropsConformance: the SAME agreement-gate rejection
    // (async witness on an unspecialized generic parent → the emitter won't emit it) must be RESCUED
    // when the protocol requirement carries a direct extension default. The interface emits that
    // requirement as a DIM, so the conformer leans on the default instead of providing the witness —
    // dropping the whole conformance would needlessly lose a surface that compiles. Mirrors the
    // instance-property DIM rescue; the method agreement gate must reconsider the default before
    // returning false.
    [Fact]
    public void CanFullyImplementProtocol_AsyncWitnessOnGenericParent_KeptWhenExtensionDefaultExists()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = CreateAsyncIntProvider(moduleDecl);
        moduleDecl.Protocols.Add(protocolDecl);

        // GENERIC class conformer: the async witness here is dropped at emission (same shape as
        // AsyncWitnessOnGenericParent_DropsConformance).
        var concreteType = CreateClassDecl("ValueBox", moduleDecl);
        concreteType.GenericParameters = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl(
                TypeName: "T",
                SugaredTypeName: "T",
                GenericConformances: new List<GenericParameterConformance>(),
                AssosiatedTypeConformances: new List<GenericParameterConformance>())
        };
        concreteType.Methods.Add(CreateAsyncIntMethod(
            "provideValue", "$s10TestModule8ValueBoxC12provideValueSiyYaF", concreteType, moduleDecl));

        // A real protocol-extension default for provideValue() → the interface emits it as a DIM.
        var qualifiedProtoName = protocolDecl.SwiftTypeName!.ModuleQualifiedName;
        var protoMethod = protocolDecl.Methods.First();
        var methodKey = ProtocolExtensionEmitter.BuildMethodKey(protoMethod);
        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            [qualifiedProtoName] = new()
            {
                new ProtocolExtensionMethodDecl
                {
                    ProtocolQualifiedName = qualifiedProtoName,
                    MethodName = "provideValue",
                    PrintedName = methodKey,
                    RawSignature = "func provideValue() async -> Int",
                    ReturnsSelf = false,
                    IsMainActorIsolated = false,
                    IsStatic = false,
                    IsProperty = false,
                    HasSetter = false,
                    IsDeprecated = false,
                    IsMutating = false,
                    WhereConstraints = new List<string>()
                }
            }
        };
        var extensionDefaultsIndex = new ProtocolExtensionDefaultsIndex(extensionMethods, moduleDecl.Protocols);
        // The rescue uses the same default-resolution the interface emitter uses for DIM emission.
        Assert.True(extensionDefaultsIndex.HasMethodDefault(qualifiedProtoName, methodKey));

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase, extensionDefaultsIndex);
        var result = validator.CanFullyImplementProtocol(concreteType, protocolDecl);

        // The dropped witness is covered by the DIM → the conformance must be KEPT, not dropped.
        Assert.True(result);
    }

    // Regression (Codex r2): the agreement-gate rescue must use the SAME default resolution the
    // interface emitter uses to decide DIM emission — direct OR inherited sub-protocol default —
    // not a direct-only check. A default supplied by a SUB-protocol still produces a DIM on the
    // PARENT interface (ProtocolHandler emits method DIMs via HasMethodDefault), so a parent
    // conformance whose witness is dropped at emission is still satisfiable and must be KEPT. A
    // direct-only rescue (HasDirectMethodDefault) would wrongly drop it.
    [Fact]
    public void CanFullyImplementProtocol_AsyncWitnessWithSubProtocolDefault_KeptForParentConformance()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var parentProtocol = CreateAsyncIntProvider(moduleDecl);
        moduleDecl.Protocols.Add(parentProtocol);

        // A sub-protocol that refines the parent; its extension provides the parent's default.
        var subProtocol = new ProtocolDecl
        {
            Name = "RefinedValueProvider",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.RefinedValueProvider"),
            MangledName = "$s10TestModule20RefinedValueProviderP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec> { new NamedTypeSpec("TestModule.ValueProvider") },
            IsClassBound = false,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Protocols.Add(subProtocol);

        // Generic conformer whose async witness is dropped at emission (same shape as above).
        var concreteType = CreateClassDecl("ValueBox", moduleDecl);
        concreteType.GenericParameters = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl(
                TypeName: "T",
                SugaredTypeName: "T",
                GenericConformances: new List<GenericParameterConformance>(),
                AssosiatedTypeConformances: new List<GenericParameterConformance>())
        };
        concreteType.Methods.Add(CreateAsyncIntMethod(
            "provideValue", "$s10TestModule8ValueBoxC12provideValueSiyYaF", concreteType, moduleDecl));

        // Register the default under the SUB-protocol, not the parent.
        var parentQualified = parentProtocol.SwiftTypeName!.ModuleQualifiedName;
        var protoMethod = parentProtocol.Methods.First();
        var methodKey = ProtocolExtensionEmitter.BuildMethodKey(protoMethod);
        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["TestModule.RefinedValueProvider"] = new()
            {
                new ProtocolExtensionMethodDecl
                {
                    ProtocolQualifiedName = "TestModule.RefinedValueProvider",
                    MethodName = "provideValue",
                    PrintedName = methodKey,
                    RawSignature = "func provideValue() async -> Int",
                    ReturnsSelf = false,
                    IsMainActorIsolated = false,
                    IsStatic = false,
                    IsProperty = false,
                    HasSetter = false,
                    IsDeprecated = false,
                    IsMutating = false,
                    WhereConstraints = new List<string>()
                }
            }
        };
        var extensionDefaultsIndex = new ProtocolExtensionDefaultsIndex(extensionMethods, moduleDecl.Protocols);

        // The default is on the sub-protocol: a DIRECT check on the parent misses it, but the
        // emitter's full resolution (HasMethodDefault) finds it via the inheritance graph.
        Assert.False(extensionDefaultsIndex.HasDirectMethodDefault(parentQualified, methodKey));
        Assert.True(extensionDefaultsIndex.HasMethodDefault(parentQualified, methodKey));

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase, extensionDefaultsIndex);
        var result = validator.CanFullyImplementProtocol(concreteType, parentProtocol);

        // The dropped witness is covered by the sub-protocol DIM on the parent interface → KEPT.
        Assert.True(result);
    }

    // Property-side companion to the method sub-protocol regression above: the property DIM rescue
    // (the CanEmitProperty-skip arm) must likewise use the SAME default resolution the interface
    // emitter uses for property DIM emission — direct OR inherited sub-protocol default — not a
    // direct-only check. ProtocolHandler emits property DIMs via HasPropertyDefault (broad), so a
    // parent conformance whose unemittable property witness is covered by a SUB-protocol default
    // must be KEPT. A direct-only rescue (HasDirectPropertyDefault) would wrongly drop it.
    [Fact]
    public void CanFullyImplementProtocol_UnemittablePropertyWithSubProtocolDefault_KeptForParentConformance()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        // Parent protocol with a property of unresolvable type → AnyType fallback (same shape as
        // CanFullyImplementProtocol_ProtocolHasAnyTypeProperty_ReturnsFalse, which proves the
        // requirement is NOT skipped from the interface and so reaches the per-member check).
        var parentProtocol = new ProtocolDecl
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
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        parentProtocol.Properties.Add(CreatePropertyDecl(
            "data", new NamedTypeSpec("UnknownModule.Data"), moduleDecl,
            hasGetter: true, hasSetter: false, accessorParent: parentProtocol));
        moduleDecl.Protocols.Add(parentProtocol);

        // A sub-protocol that refines the parent; its extension provides the parent's `data` default.
        var subProtocol = new ProtocolDecl
        {
            Name = "RefinedDataSource",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.RefinedDataSource"),
            MangledName = "$s10TestModule17RefinedDataSourceP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec> { new NamedTypeSpec("TestModule.DataSource") },
            IsClassBound = false,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Protocols.Add(subProtocol);

        // Concrete type that HAS the `data` property but of the same unresolvable type → the
        // emitter skips it (CanEmitProperty AnyType fallback) → member-present-but-unemittable
        // rescue path, so the conformance hinges entirely on the DIM.
        var concreteType = CreateStructDecl("MyDataSource", moduleDecl);
        concreteType.Properties.Add(CreatePropertyDecl(
            "data", new NamedTypeSpec("UnknownModule.Data"), moduleDecl,
            hasGetter: true, hasSetter: false, accessorParent: concreteType));

        // Register the `data` default under the SUB-protocol, not the parent.
        var parentQualified = parentProtocol.SwiftTypeName!.ModuleQualifiedName;
        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["TestModule.RefinedDataSource"] = new()
            {
                new ProtocolExtensionMethodDecl
                {
                    ProtocolQualifiedName = "TestModule.RefinedDataSource",
                    MethodName = "data",
                    PrintedName = "data",
                    RawSignature = "var data: UnknownModule.Data { get }",
                    ReturnsSelf = false,
                    IsMainActorIsolated = false,
                    IsStatic = false,
                    IsProperty = true,
                    HasSetter = false,
                    IsDeprecated = false,
                    IsMutating = false,
                    WhereConstraints = new List<string>()
                }
            }
        };
        var extensionDefaultsIndex = new ProtocolExtensionDefaultsIndex(extensionMethods, moduleDecl.Protocols);

        // The default is on the sub-protocol: a DIRECT check on the parent misses it, but the
        // emitter's full resolution (HasPropertyDefault) finds it via the inheritance graph.
        Assert.False(extensionDefaultsIndex.HasDirectPropertyDefault(parentQualified, "data"));
        Assert.True(extensionDefaultsIndex.HasPropertyDefault(parentQualified, "data"));

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase, extensionDefaultsIndex);
        var result = validator.CanFullyImplementProtocol(concreteType, parentProtocol);

        // The unemittable property witness is covered by the sub-protocol DIM on the parent → KEPT.
        Assert.True(result);
    }

    // The static half of the DIM rescue. The instance path reconsiders the extension default before
    // dropping a conformance whose witness carries an unbridgeable async closure; the static path
    // ran the same unbridgeable check with no rescue, so the identical shape dropped on one path and
    // survived on the other. C# 11 static abstract members carry default bodies exactly like
    // instance DIMs, so a static requirement backed by an extension default does not need the
    // witness either — the conformance must be KEPT.
    [Fact]
    public void CanFullyImplementProtocol_StaticUnbridgeableAsyncWitness_KeptWhenExtensionDefaultExists()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = CreateStaticAsyncClosureProvider(moduleDecl);
        moduleDecl.Protocols.Add(protocolDecl);

        // Static witness with the same unbridgeable shape: the closure is `() async throws -> Int`
        // but the OUTER method is not async, so no adapter can ever be generated for it.
        var concreteType = CreateClassDecl("ValueBox", moduleDecl);
        concreteType.Methods.Add(CreateStaticAsyncClosureMethod(
            "provideValue", "$s10TestModule8ValueBoxC12provideValueyySiyYaKcFZ", concreteType, moduleDecl));

        var qualifiedProtoName = protocolDecl.SwiftTypeName!.ModuleQualifiedName;
        var protoMethod = protocolDecl.Methods.First();
        var methodKey = ProtocolExtensionEmitter.BuildMethodKey(protoMethod);
        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            [qualifiedProtoName] = new()
            {
                new ProtocolExtensionMethodDecl
                {
                    ProtocolQualifiedName = qualifiedProtoName,
                    MethodName = "provideValue",
                    PrintedName = methodKey,
                    RawSignature = "static func provideValue(handler: () async throws -> Int)",
                    ReturnsSelf = false,
                    IsMainActorIsolated = false,
                    IsStatic = true,
                    IsProperty = false,
                    HasSetter = false,
                    IsDeprecated = false,
                    IsMutating = false,
                    WhereConstraints = new List<string>()
                }
            }
        };
        var extensionDefaultsIndex = new ProtocolExtensionDefaultsIndex(extensionMethods, moduleDecl.Protocols);
        Assert.True(extensionDefaultsIndex.HasMethodDefault(qualifiedProtoName, methodKey));

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase, extensionDefaultsIndex);

        Assert.True(validator.CanFullyImplementProtocol(concreteType, protocolDecl));
    }

    // The control for the rescue above: with NO extension default there is no DIM to lean on, so the
    // unbridgeable static witness really does leave the requirement unimplemented and the
    // conformance must still be dropped. Without this, the rescue could degenerate into "static
    // requirements are never checked" and nothing would notice.
    [Fact]
    public void CanFullyImplementProtocol_StaticUnbridgeableAsyncWitness_DroppedWithoutExtensionDefault()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = CreateStaticAsyncClosureProvider(moduleDecl);
        moduleDecl.Protocols.Add(protocolDecl);

        var concreteType = CreateClassDecl("ValueBox", moduleDecl);
        concreteType.Methods.Add(CreateStaticAsyncClosureMethod(
            "provideValue", "$s10TestModule8ValueBoxC12provideValueyySiyYaKcFZ", concreteType, moduleDecl));

        var extensionDefaultsIndex = new ProtocolExtensionDefaultsIndex(
            new Dictionary<string, List<ProtocolExtensionMethodDecl>>(), moduleDecl.Protocols);
        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase, extensionDefaultsIndex);

        Assert.False(validator.CanFullyImplementProtocol(concreteType, protocolDecl));
    }

    // The whole-conformance rescue has to survive an UNLABELED first parameter. The defaults index
    // is keyed on the swiftinterface PrintedName (`apply(_:context:)`); the validator rebuilds that
    // key from the parsed requirement, whose wildcard parameter carries the parser's synthesized
    // `arg{i}` placeholder. Rendering the placeholder as a literal label produces a key that can
    // never match, so a conformer relying purely on the default looked unsatisfiable and lost its
    // ENTIRE `: IFrameStage` conformance — while a sibling that spelled the member out kept its own,
    // which is the asymmetry that makes the defect read as type-specific rather than key-specific.
    [Fact]
    public void CanFullyImplementProtocol_UnlabeledParamRequirement_KeptWhenExtensionDefaultExists()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = CreateProtocolWithVoidMethod("FrameStage", "apply", moduleDecl);
        // Replace the void requirement with an unlabeled-first-parameter one.
        protocolDecl.Methods.Clear();
        protocolDecl.Methods.Add(CreateUnlabeledFirstParamMethod(
            "apply", "$s10TestModule10FrameStageP5applyyS2i_SitF", null, moduleDecl));
        moduleDecl.Protocols.Add(protocolDecl);

        // Conformer provides NO implementation — it leans entirely on the extension default.
        var concreteType = CreateClassDecl("DoublingStage", moduleDecl);

        var qualifiedProtoName = protocolDecl.SwiftTypeName!.ModuleQualifiedName;
        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            [qualifiedProtoName] = new()
            {
                new ProtocolExtensionMethodDecl
                {
                    ProtocolQualifiedName = qualifiedProtoName,
                    MethodName = "apply",
                    // Exactly what the swiftinterface prints for `func apply(_ value: Int, context: Int)`.
                    PrintedName = "apply(_:context:)",
                    RawSignature = "func apply(_ value: Int, context: Int) -> Int",
                    ReturnsSelf = false,
                    IsMainActorIsolated = false,
                    IsStatic = false,
                    IsProperty = false,
                    HasSetter = false,
                    IsDeprecated = false,
                    IsMutating = false,
                    WhereConstraints = new List<string>()
                }
            }
        };
        var extensionDefaultsIndex = new ProtocolExtensionDefaultsIndex(extensionMethods, moduleDecl.Protocols);

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase, extensionDefaultsIndex);

        Assert.True(validator.CanFullyImplementProtocol(concreteType, protocolDecl));
    }

    // Swift lets one type declare `static let keySize` next to `let keySize`; C# has no such split,
    // so the type emitters keep whichever came first and drop the other. When the static one wins,
    // an instance interface requirement of that name has no possible implementation.
    [Fact]
    public void CanFullyImplementProtocol_StaticPropertyShadowsInstanceRequirement_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = CreateProtocolWithInstanceProperty("KeyedCipher", "keySize", moduleDecl);

        var concreteType = CreateClassDecl("DualKeySizeCipher", moduleDecl);
        // Declaration order matters: the static sibling comes first, so it claims the C# name.
        concreteType.Properties.Add(CreateStaticPropertyDecl(
            "keySize", new NamedTypeSpec("Swift.Int"), moduleDecl, hasGetter: true, hasSetter: false, accessorParent: concreteType));
        concreteType.Properties.Add(CreatePropertyDecl(
            "keySize", new NamedTypeSpec("Swift.Int"), moduleDecl, hasGetter: true, hasSetter: false, accessorParent: concreteType));

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);

        Assert.False(validator.CanFullyImplementProtocol(concreteType, protocolDecl));
    }

    // Companion: without the static sibling the very same instance witness satisfies the
    // requirement, so the drop above is attributable to the name collision and nothing else.
    [Fact]
    public void CanFullyImplementProtocol_InstancePropertyOnly_ReturnsTrue()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = CreateProtocolWithInstanceProperty("KeyedCipher", "keySize", moduleDecl);

        var concreteType = CreateClassDecl("InstanceKeySizeCipher", moduleDecl);
        concreteType.Properties.Add(CreatePropertyDecl(
            "keySize", new NamedTypeSpec("Swift.Int"), moduleDecl, hasGetter: true, hasSetter: false, accessorParent: concreteType));

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);

        Assert.True(validator.CanFullyImplementProtocol(concreteType, protocolDecl));
    }

    private static ProtocolDecl CreateProtocolWithInstanceProperty(string protocolName, string propertyName, ModuleDecl moduleDecl)
    {
        var protocolDecl = new ProtocolDecl
        {
            Name = protocolName,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{protocolName}"),
            MangledName = $"$s10TestModule{protocolName.Length}{protocolName}P",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = true,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        protocolDecl.Properties.Add(CreatePropertyDecl(
            propertyName, new NamedTypeSpec("Swift.Int"), moduleDecl, hasGetter: true, hasSetter: false, accessorParent: protocolDecl));
        moduleDecl.Protocols.Add(protocolDecl);
        return protocolDecl;
    }

    /// <summary>
    /// `func apply(_ value: Int, context: Int) -&gt; Int` as the parser produces it: the wildcard
    /// label arrives as the synthesized `arg0` placeholder with no captured Swift label.
    /// </summary>
    private static MethodDecl CreateUnlabeledFirstParamMethod(
        string name, string mangledName, TypeDecl? parent, ModuleDecl moduleDecl)
    {
        var unlabeled = CreateArgument("arg0", new NamedTypeSpec("Swift.Int"), moduleDecl);
        unlabeled.OriginalSwiftName = null;

        return new MethodDecl
        {
            Name = name,
            MangledName = mangledName,
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, new NamedTypeSpec("Swift.Int"), moduleDecl),
                unlabeled,
                CreateArgument("context", new NamedTypeSpec("Swift.Int"), moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parent,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
    }

    private static ProtocolDecl CreateStaticAsyncClosureProvider(ModuleDecl moduleDecl)
    {
        return new ProtocolDecl
        {
            Name = "ValueProvider",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ValueProvider"),
            MangledName = "$s10TestModule13ValueProviderP",
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
                CreateStaticAsyncClosureMethod(
                    "provideValue", "$s10TestModule13ValueProviderP12provideValueyySiyYaKcFZ", null, moduleDecl)
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
    }

    /// <summary>
    /// `static func provideValue(handler: () async throws -&gt; Int)` — a baseline-shaped async
    /// throwing closure on a NON-async outer method, which is unbridgeable by construction: the
    /// adapter the P/Invoke's (context, startFunc) pair needs is only generated inside an
    /// async-throws wrapper body.
    /// </summary>
    private static MethodDecl CreateStaticAsyncClosureMethod(
        string name, string mangledName, TypeDecl? parent, ModuleDecl moduleDecl)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = mangledName,
            MethodType = MethodType.Static,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl),
                CreateArgument("handler",
                    new ClosureTypeSpec(TupleTypeSpec.Empty, new NamedTypeSpec("Swift.Int"))
                    {
                        IsAsync = true,
                        Throws = true
                    },
                    moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parent,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
    }

    private static ProtocolDecl CreateAsyncIntProvider(ModuleDecl moduleDecl)
    {
        return new ProtocolDecl
        {
            Name = "ValueProvider",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ValueProvider"),
            MangledName = "$s10TestModule13ValueProviderP",
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
                CreateAsyncIntMethod(
                    "provideValue", "$s10TestModule13ValueProviderP12provideValueSiyYaF", null, moduleDecl)
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
    }

    private static MethodDecl CreateAsyncIntMethod(string name, string mangledName, TypeDecl? parent, ModuleDecl moduleDecl)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = mangledName,
            MethodType = MethodType.Instance,
            IsConstructor = false,
            // CSSignature[0] is the return slot: `async -> Int`.
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, new NamedTypeSpec("Swift.Int"), moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parent,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = true,
            IsSynthesizedAccessor = false
        };
    }

    #endregion

    #region Prediction ↔ emission agreement, observed on the EMITTED class declaration

    /// <summary>
    /// The conformance decision is only meaningful if it survives into the generated code, and the
    /// artifact manifest pins members (ctor, method) rather than the class's base list — so the
    /// interface could silently vanish from the emitted class with every other gate still green.
    /// This drives the real emission pipeline and reads the class declaration itself.
    ///
    /// The eligible shape (async-throws member carrying a baseline async-throws closure) is
    /// promoted to an async <c>@_cdecl</c> wrapper at emission, so the witness IS emitted and the
    /// conformer must declare the interface. The debug-default twin is the divergence case: the
    /// debug-parameter Swift wrapper is installed BEFORE the async promotion branch is reached, so
    /// emission declines to promote and the witness is not bridgeable. With no protocol-extension
    /// default to rescue it, the honest outcome is that the conformance is dropped — what must
    /// never happen is the interface being declared by a class whose witness emission skipped.
    /// </summary>
    [Fact]
    public void EmittedConformer_BaselineAsyncClosureWitness_DeclaresInterface()
    {
        var eligible = EmitConformerModule(withDebugDefaultParam: false);
        Assert.Contains("class AsyncClosureConformer", eligible);
        Assert.Contains("IAsyncClosureRequirement", DeclarationLineOf(eligible, "class AsyncClosureConformer"));
    }

    [Fact]
    public void EmittedConformer_DebugDefaultParamWitness_DoesNotDeclareInterface()
    {
        var diverging = EmitConformerModule(withDebugDefaultParam: true);
        Assert.Contains("class AsyncClosureConformer", diverging);
        Assert.DoesNotContain("IAsyncClosureRequirement", DeclarationLineOf(diverging, "class AsyncClosureConformer"));
    }

    /// <summary>The single source line that declares the named type, base list included.</summary>
    private static string DeclarationLineOf(string emitted, string declarationFragment)
    {
        var line = emitted.Split('\n').FirstOrDefault(l => l.Contains(declarationFragment));
        Assert.NotNull(line);
        return line!;
    }

    /// <summary>
    /// Emits a module holding one protocol requiring an async-throws method with a baseline
    /// async-throws closure parameter, and one class conforming to it. When
    /// <paramref name="withDebugDefaultParam"/> is set, both the requirement and the witness also
    /// carry a `file: StaticString = #file` debug parameter.
    /// </summary>
    private static string EmitConformerModule(bool withDebugDefaultParam)
    {
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = new ProtocolDecl
        {
            Name = "AsyncClosureRequirement",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.AsyncClosureRequirement"),
            MangledName = "$s10TestModule22AsyncClosureRequirementP",
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
        protocolDecl.Methods.Add(CreateAsyncClosureMethod(
            "$s10TestModule22AsyncClosureRequirementP3runySiyYaKcSiYaKF",
            protocolDecl, moduleDecl, withDebugDefaultParam));

        var conformer = CreateClassDecl("AsyncClosureConformer", moduleDecl);
        conformer.IsFinal = true;
        conformer.Methods.Add(CreateAsyncClosureMethod(
            "$s10TestModule21AsyncClosureConformerC3runySiyYaKcSiYaKF",
            conformer, moduleDecl, withDebugDefaultParam));
        conformer.Conformances.Add(new TypeConformance(
            conformer.SwiftTypeName,
            protocolDecl.SwiftTypeName,
            string.Empty));

        moduleDecl.Protocols.Add(protocolDecl);
        moduleDecl.Types.Add(protocolDecl);
        moduleDecl.Types.Add(conformer);

        // A non-empty async library name is what puts the run in XCFramework mode, the prerequisite
        // for any @_cdecl wrapper — without it nothing is promoted and both shapes look alike.
        var typeDatabase = new TypeDatabase { AsyncLibraryName = "TestModuleSwiftBindings" };
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
        typeDatabase.AddModuleDatabase(swiftModule);

        // The conformance-interface gate resolves the protocol through the type database, so the
        // protocol needs a record for the class to be able to declare the interface at all.
        var moduleTypeDatabase = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        moduleTypeDatabase.RegisterType(
            protocolDecl.SwiftTypeName!,
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "IAsyncClosureRequirement"),
                SwiftTypeName = protocolDecl.SwiftTypeName!,
                MetadataAccessor = "$s10TestModule22AsyncClosureRequirementMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(moduleTypeDatabase);

        var csStringWriter = new StringWriter();
        var handler = new ModuleHandler(new NullLogger<ModuleHandler>());
        var env = handler.Marshal(moduleDecl, typeDatabase);
        var conductor = new Conductor(new NullLoggerFactory());
        var context = new TypeHandlerContext(null, new(), null, EmissionContext: new ModuleEmissionContext());
        handler.Emit(new CSharpWriter(csStringWriter), new SwiftWriter(new StringWriter()), env, conductor, context);

        return csStringWriter.ToString();
    }

    private static MethodDecl CreateAsyncClosureMethod(
        string mangledName, TypeDecl? parent, ModuleDecl moduleDecl, bool withDebugDefaultParam)
    {
        var signature = new List<ArgumentDecl>
        {
            // [0] is the return slot: `async throws -> Int`.
            CreateArgument(string.Empty, new NamedTypeSpec("Swift.Int"), moduleDecl),
            CreateArgument(
                "provider",
                new ClosureTypeSpec(TupleTypeSpec.Empty, new NamedTypeSpec("Swift.Int"))
                {
                    IsAsync = true,
                    Throws = true
                },
                moduleDecl),
        };

        if (withDebugDefaultParam)
        {
            var debugArg = CreateArgument("file", new NamedTypeSpec("Swift.StaticString"), moduleDecl);
            debugArg.HasDefaultArg = true;
            signature.Add(debugArg);
        }

        return new MethodDecl
        {
            Name = "run",
            MangledName = mangledName,
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = signature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parent,
            ModuleDecl = moduleDecl,
            Throws = true,
            IsAsync = true,
            IsSynthesizedAccessor = false
        };
    }

    #endregion

    #region Mutating-aware async noun-getter naming parity (interface ↔ witness)

    [Fact]
    public void CanFullyImplementProtocol_MutatingAsyncNounGetter_MutatingWitness_ReturnsTrue()
    {
        // A mutating async noun-only zero-arg getter (the AsyncIteratorProtocol.next() shape)
        // is emitted WITHOUT the `Get` prefix on the interface. When the concrete witness is ALSO
        // mutating, it derives the SAME bare name through the same mutating-aware rule, so the
        // emitted names agree and the conformance is kept. This is the common case the validator's
        // IsMutating threading exists to keep green: predict the witness name with the witness's
        // real IsMutating, not a stale default.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = CreateProtocolWithMutatingAsyncGetter("Ticker", "token", moduleDecl);
        moduleDecl.Protocols.Add(protocolDecl);

        var concreteType = CreateStructDecl("LiveTicker", moduleDecl);
        concreteType.Methods.Add(CreateMutatingAsyncGetter("token", concreteType, moduleDecl, isMutating: true));

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var result = validator.CanFullyImplementProtocol(concreteType, protocolDecl);

        // interface `TokenAsync` == witness `TokenAsync` → parity holds → conformance kept.
        Assert.True(result);
    }

    [Fact]
    public void CanFullyImplementProtocol_MutatingAsyncNounGetter_NonMutatingWitness_GracefullyDropsConformance()
    {
        // Swift legally lets a NON-mutating witness satisfy a `mutating` requirement (always true
        // for a class witness; legal-but-unusual for a struct). The requirement projects to the bare
        // `TokenAsync` (mutating-excluded from the Get prefix) while the non-mutating witness projects
        // to `GetTokenAsync` — the names diverge. The validator predicts this divergence (using each
        // side's real IsMutating) and drops the conformance, so the generated C# still COMPILES rather
        // than emitting a CS0535 the consumer can't build. Keeping the conformance here would require
        // conformance-aware naming (the witness adopting the requirement's name), a naming-SSOT change
        // tracked separately. This test pins the graceful-degradation fail-safe.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = CreateProtocolWithMutatingAsyncGetter("Ticker", "token", moduleDecl);
        moduleDecl.Protocols.Add(protocolDecl);

        var concreteType = CreateStructDecl("SnapshotTicker", moduleDecl);
        concreteType.Methods.Add(CreateMutatingAsyncGetter("token", concreteType, moduleDecl, isMutating: false));

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var result = validator.CanFullyImplementProtocol(concreteType, protocolDecl);

        // interface `TokenAsync` != witness `GetTokenAsync` → parity fails → conformance dropped (no CS0535).
        Assert.False(result);
    }

    private static ProtocolDecl CreateProtocolWithMutatingAsyncGetter(string protocolName, string methodName, ModuleDecl moduleDecl)
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
            Methods = new List<MethodDecl> { CreateMutatingAsyncGetter(methodName, null, moduleDecl, isMutating: true) },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
    }

    private static MethodDecl CreateMutatingAsyncGetter(string name, TypeDecl? parent, ModuleDecl moduleDecl, bool isMutating)
    {
        // Noun-only, zero-arg, value-returning (Swift.Int), async. The single CSSignature element is
        // the return type; the mutating-aware Get-prefix gate keys off IsMutating.
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule{name.Length}{name}SiyYaF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl> { CreateArgument(string.Empty, new NamedTypeSpec("Swift.Int"), moduleDecl) },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parent,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = true,
            IsMutating = isMutating,
            IsSynthesizedAccessor = false
        };
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
                        IsSynthesizedAccessor = false
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

    #region Ancestor Member Walking (Session I5)

    [Fact]
    public void CanFullyImplementProtocol_DerivedFindsMethodInBase_ReturnsTrue()
    {
        // Derived class doesn't have the method, but base does → should pass
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = CreateProtocolWithVoidMethod("Doable", "doIt", moduleDecl);
        moduleDecl.Protocols.Add(protocolDecl);

        // Base class with the required method
        var baseClass = CreateClassDecl("BaseClass", moduleDecl);
        var baseMethod = CreateVoidMethod("doIt", moduleDecl);
        baseMethod.ParentDecl = baseClass;
        baseClass.Methods.Add(baseMethod);

        // Derived class with no methods but resolved superclass
        var derivedClass = CreateClassDecl("DerivedClass", moduleDecl);
        derivedClass.ResolvedSuperclass = baseClass;

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var result = validator.CanFullyImplementProtocol(derivedClass, protocolDecl);

        Assert.True(result);
    }

    [Fact]
    public void CanFullyImplementProtocol_DerivedFindsPropertyInBase_ReturnsTrue()
    {
        // Derived class doesn't have the property, but base does → should pass
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = new ProtocolDecl
        {
            Name = "Named",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Named"),
            MangledName = "$s10TestModule5NamedP",
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
            CreatePropertyDecl("name", new NamedTypeSpec("Swift.Int"), moduleDecl, hasGetter: true, hasSetter: false, accessorParent: protocolDecl));
        moduleDecl.Protocols.Add(protocolDecl);

        // Base class with the property
        var baseClass = CreateClassDecl("BaseClass", moduleDecl);
        baseClass.Properties.Add(
            CreatePropertyDecl("name", new NamedTypeSpec("Swift.Int"), moduleDecl, hasGetter: true, hasSetter: false, accessorParent: baseClass));

        // Derived class: no properties, resolved superclass
        var derivedClass = CreateClassDecl("DerivedClass", moduleDecl);
        derivedClass.ResolvedSuperclass = baseClass;

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var result = validator.CanFullyImplementProtocol(derivedClass, protocolDecl);

        Assert.True(result);
    }

    [Fact]
    public void CanFullyImplementProtocol_ThreeLevelChain_FindsMethodInGrandparent()
    {
        // Grandparent has the method, parent doesn't, child doesn't → should pass
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = CreateProtocolWithVoidMethod("Runnable", "run", moduleDecl);
        moduleDecl.Protocols.Add(protocolDecl);

        var grandparent = CreateClassDecl("GrandBase", moduleDecl);
        var gpMethod = CreateVoidMethod("run", moduleDecl);
        gpMethod.ParentDecl = grandparent;
        grandparent.Methods.Add(gpMethod);

        var parent = CreateClassDecl("MidBase", moduleDecl);
        parent.ResolvedSuperclass = grandparent;

        var child = CreateClassDecl("Child", moduleDecl);
        child.ResolvedSuperclass = parent;

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var result = validator.CanFullyImplementProtocol(child, protocolDecl);

        Assert.True(result);
    }

    [Fact]
    public void CanFullyImplementProtocol_MethodNotInBaseOrSelf_ReturnsFalse()
    {
        // Neither derived nor base has the method → fails
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = CreateProtocolWithVoidMethod("Stoppable", "stop", moduleDecl);
        moduleDecl.Protocols.Add(protocolDecl);

        var baseClass = CreateClassDecl("BaseClass", moduleDecl);
        var derivedClass = CreateClassDecl("DerivedClass", moduleDecl);
        derivedClass.ResolvedSuperclass = baseClass;

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var result = validator.CanFullyImplementProtocol(derivedClass, protocolDecl);

        Assert.False(result);
    }

    [Fact]
    public void CanFullyImplementProtocol_StructType_OnlyChecksSelf()
    {
        // Struct types have no inheritance → only own members checked
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = CreateProtocolWithVoidMethod("Printable", "printSelf", moduleDecl);
        moduleDecl.Protocols.Add(protocolDecl);

        // Struct without the method
        var structDecl = CreateStructDecl("MyStruct", moduleDecl);

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var result = validator.CanFullyImplementProtocol(structDecl, protocolDecl);

        Assert.False(result);
    }

    [Fact]
    public void CanFullyImplementProtocol_SkippedBase_AncestorMembersNotCounted()
    {
        // Base class has unsupported generic constraints → IsEffectivelyDerived is false.
        // GetEmittableAncestors stops at the non-emittable base. Derived class must
        // have its own members to satisfy the protocol.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = CreateProtocolWithVoidMethod("Flyable", "fly", moduleDecl);
        moduleDecl.Protocols.Add(protocolDecl);

        // Base class with the method but also unsupported generic constraints (SwiftUI)
        var baseClass = CreateClassDecl("GenericBase", moduleDecl);
        baseClass.GenericParameters = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl(
                TypeName: "T",
                SugaredTypeName: "T",
                GenericConformances: new List<GenericParameterConformance>
                {
                    new GenericParameterConformance(
                        Path: new[] { "T" },
                        ConformanceTarget: SwiftTypeName.FromModuleQualifiedName("SwiftUI.View"),
                        Kind: ConformanceKind.Protocol)
                },
                AssosiatedTypeConformances: new List<GenericParameterConformance>())
        };
        var baseMethod = CreateVoidMethod("fly", moduleDecl);
        baseMethod.ParentDecl = baseClass;
        baseClass.Methods.Add(baseMethod);

        // Derived class — has resolved superclass but base is non-emittable
        var derivedClass = CreateClassDecl("DerivedFly", moduleDecl);
        derivedClass.ResolvedSuperclass = baseClass;

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var result = validator.CanFullyImplementProtocol(derivedClass, protocolDecl);

        // Base is non-emittable → ancestor walk stops → method not found → false
        Assert.False(result);
    }

    [Fact]
    public void GetEmittableAncestors_NonClassType_YieldsOnlySelf()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDecl("MyStruct", moduleDecl);

        var ancestors = ProtocolConformanceValidator.GetEmittableAncestors(structDecl).ToList();

        Assert.Single(ancestors);
        Assert.Same(structDecl, ancestors[0]);
    }

    [Fact]
    public void GetEmittableAncestors_ClassWithNoSuperclass_YieldsOnlySelf()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("RootClass", moduleDecl);

        var ancestors = ProtocolConformanceValidator.GetEmittableAncestors(classDecl).ToList();

        Assert.Single(ancestors);
        Assert.Same(classDecl, ancestors[0]);
    }

    [Fact]
    public void GetEmittableAncestors_DeepChain_YieldsAllEmittable()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var grandparent = CreateClassDecl("Grandparent", moduleDecl);
        var parent = CreateClassDecl("Parent", moduleDecl);
        parent.ResolvedSuperclass = grandparent;
        var child = CreateClassDecl("Child", moduleDecl);
        child.ResolvedSuperclass = parent;

        var ancestors = ProtocolConformanceValidator.GetEmittableAncestors(child).ToList();

        Assert.Equal(3, ancestors.Count);
        Assert.Same(child, ancestors[0]);
        Assert.Same(parent, ancestors[1]);
        Assert.Same(grandparent, ancestors[2]);
    }

    [Fact]
    public void GetEmittableAncestors_StopsAtNonEmittableAncestor()
    {
        var moduleDecl = CreateModuleDecl("TestModule");

        // Grandparent with unsupported constraint
        var grandparent = CreateClassDecl("GenericGP", moduleDecl);
        grandparent.GenericParameters = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl(
                TypeName: "T",
                SugaredTypeName: "T",
                GenericConformances: new List<GenericParameterConformance>
                {
                    new GenericParameterConformance(
                        Path: new[] { "T" },
                        ConformanceTarget: SwiftTypeName.FromModuleQualifiedName("SwiftUI.View"),
                        Kind: ConformanceKind.Protocol)
                },
                AssosiatedTypeConformances: new List<GenericParameterConformance>())
        };

        var parent = CreateClassDecl("Parent", moduleDecl);
        parent.ResolvedSuperclass = grandparent;

        var child = CreateClassDecl("Child", moduleDecl);
        child.ResolvedSuperclass = parent;

        var ancestors = ProtocolConformanceValidator.GetEmittableAncestors(child).ToList();

        // Should yield child + parent, then stop (grandparent is non-emittable)
        Assert.Equal(2, ancestors.Count);
        Assert.Same(child, ancestors[0]);
        Assert.Same(parent, ancestors[1]);
    }

    #endregion

    #region Phantom Default Conformance

    [Fact]
    public void CanFullyImplementProtocol_MissingProperty_AcceptedWhenPhantomDefaultExists()
    {
        // Protocol requires typeErasedStorage but a concrete type omits it;
        // a phantom default from a PAT extension should satisfy the conformance.
        var typeDatabase = CreateTypeDatabaseWithProtocol("TestModule.Provider");
        var moduleDecl = CreateModuleDecl("TestModule");

        // Protocol requires: hasUpdate() method + typeErasedStorage property
        var protocolDecl = new ProtocolDecl
        {
            Name = "Provider",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Provider"),
            MangledName = "$s10TestModule8ProviderP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = false,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>
            {
                CreatePropertyDecl("typeErasedStorage", new NamedTypeSpec("Swift.Int"), moduleDecl, hasGetter: true, hasSetter: false)
            },
            Methods = new List<MethodDecl> { CreateVoidMethod("hasUpdate", moduleDecl) },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Protocols.Add(protocolDecl);

        // Concrete type has hasUpdate but NOT typeErasedStorage
        var concreteType = CreateClassDecl("FloatProvider", moduleDecl);
        concreteType.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName("TestModule.FloatProvider"),
            SwiftTypeName.FromModuleQualifiedName("TestModule.Provider"),
            "$sConformance"));
        var concreteMethod = CreateVoidMethod("hasUpdate", moduleDecl);
        concreteMethod.ParentDecl = concreteType;
        concreteType.Methods.Add(concreteMethod);
        moduleDecl.Types.Add(concreteType);

        // Build extension defaults index WITH phantom detection
        var extensionDefaultsIndex = new ProtocolExtensionDefaultsIndex(new(), moduleDecl.Protocols);
        extensionDefaultsIndex.DetectPhantomDefaults(moduleDecl);

        // Verify phantom default was detected
        Assert.True(extensionDefaultsIndex.HasDirectPropertyDefault("TestModule.Provider", "typeErasedStorage"));

        // Now the validator should accept the conformance
        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase, extensionDefaultsIndex);
        var result = validator.CanFullyImplementProtocol(concreteType, protocolDecl);
        Assert.True(result);
    }

    [Fact]
    public void CanFullyImplementProtocol_MissingProperty_RejectedWithoutPhantomDefaults()
    {
        // Same setup but WITHOUT phantom default detection — conformance should be rejected
        var typeDatabase = CreateTypeDatabaseWithProtocol("TestModule.Provider");
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = new ProtocolDecl
        {
            Name = "Provider",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Provider"),
            MangledName = "$s10TestModule8ProviderP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = false,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>
            {
                CreatePropertyDecl("typeErasedStorage", new NamedTypeSpec("Swift.Int"), moduleDecl, hasGetter: true, hasSetter: false)
            },
            Methods = new List<MethodDecl> { CreateVoidMethod("hasUpdate", moduleDecl) },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Protocols.Add(protocolDecl);

        var concreteType = CreateClassDecl("FloatProvider", moduleDecl);
        var concreteMethod = CreateVoidMethod("hasUpdate", moduleDecl);
        concreteMethod.ParentDecl = concreteType;
        concreteType.Methods.Add(concreteMethod);

        // No extension defaults index → no phantom defaults
        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var result = validator.CanFullyImplementProtocol(concreteType, protocolDecl);
        Assert.False(result);
    }

    [Fact]
    public void CanFullyImplementProtocol_UnemittableProperty_AcceptedWhenPhantomDefault()
    {
        // Concrete type HAS the property but it can't be emitted (AnyType fallback).
        // The phantom default detector should still catch this.
        var typeDatabase = CreateTypeDatabaseWithProtocol("TestModule.Provider");
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = new ProtocolDecl
        {
            Name = "Provider",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Provider"),
            MangledName = "$s10TestModule8ProviderP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = false,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>
            {
                // Protocol property with unresolvable type (will project to AnyType)
                CreatePropertyDecl("valueType", new NamedTypeSpec("UnknownModule.Metatype"), moduleDecl, hasGetter: true, hasSetter: false)
            },
            Methods = new List<MethodDecl> { CreateVoidMethod("process", moduleDecl) },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Protocols.Add(protocolDecl);

        // Concrete type has valueType but with unresolvable type → will be skipped by CanEmitProperty
        var concreteType = CreateClassDecl("ConcreteProvider", moduleDecl);
        concreteType.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName("TestModule.ConcreteProvider"),
            SwiftTypeName.FromModuleQualifiedName("TestModule.Provider"),
            "$sConformance"));
        concreteType.Properties.Add(
            CreatePropertyDecl("valueType", new NamedTypeSpec("UnknownModule.Metatype"), moduleDecl,
                hasGetter: true, hasSetter: false, accessorParent: concreteType));
        var method = CreateVoidMethod("process", moduleDecl);
        method.ParentDecl = concreteType;
        concreteType.Methods.Add(method);
        moduleDecl.Types.Add(concreteType);

        // Build with phantom detection + type database for emittability check
        var extensionDefaultsIndex = new ProtocolExtensionDefaultsIndex(new(), moduleDecl.Protocols);
        extensionDefaultsIndex.DetectPhantomDefaults(moduleDecl, typeDatabase);

        // valueType should be a phantom default (exists on type but can't be emitted)
        Assert.True(extensionDefaultsIndex.HasDirectPropertyDefault("TestModule.Provider", "valueType"));

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase, extensionDefaultsIndex);
        var result = validator.CanFullyImplementProtocol(concreteType, protocolDecl);
        Assert.True(result);
    }

    #endregion

    #region Helper Methods

    private static TypeDatabase CreateTypeDatabaseWithProtocol(string qualifiedName)
    {
        var parts = qualifiedName.Split('.');
        var moduleName = parts[0];
        var typeName = parts[1];

        // Build a type database with both Swift and the target module
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
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var targetModule = new ModuleTypeDatabase(moduleName, $"/tmp/{moduleName}.dylib");
        targetModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName(qualifiedName),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(moduleName, $"I{typeName}"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(qualifiedName),
                MetadataAccessor = $"$s{moduleName}{typeName}Ma",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(targetModule);
        return typeDatabase;
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
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    /// <summary>
    /// Creates a TypeDatabase with a Builder class registered in TestModule for TSelf conformance tests.
    /// </summary>
    private static TypeDatabase CreateTypeDatabaseWithBuilder()
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
                Kind = TypeRecordKind.Struct
            });
        // Swift.Array is needed so TryGetAnyTypeFallbackInfo doesn't flag Array<T> as missing
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Array"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftArray"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Array"),
                MetadataAccessor = "$sSaMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        var builderTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Builder");
        testModule.RegisterType(builderTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Builder"),
            SwiftTypeName = builderTypeName,
            MetadataAccessor = "$s10TestModule7BuilderCMa",
            Flags = TypeRecordFlags.RequiresMemoryManagement,
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
            IsSynthesizedAccessor = false
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
                    IsSynthesizedAccessor = false
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
                    IsSynthesizedAccessor = false
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

    private static ClassDecl CreateClassDecl(string name, ModuleDecl moduleDecl)
    {
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

    private static ProtocolDecl CreateProtocolWithVoidMethod(string protocolName, string methodName, ModuleDecl moduleDecl)
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
                CreateVoidMethod(methodName, moduleDecl)
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
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
                    IsSynthesizedAccessor = false
                }
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
    }

    #endregion

    #region TSelf Conformance Matching

    [Fact]
    public void CanFullyImplementProtocol_SelfReturningMethod_MatchesConcreteType()
    {
        // Protocol with Self-returning method: τ_0_0 → TSelf.
        // Concrete type returns itself → conformance should succeed.
        var typeDatabase = CreateTypeDatabaseWithBuilder();
        var moduleDecl = CreateModuleDecl("TestModule");

        // Protocol: method returns τ_0_0 (projected as TSelf)
        var protocolDecl = new ProtocolDecl
        {
            Name = "Configurable",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Configurable"),
            MangledName = "$s10TestModule12ConfigurableP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = false,
            HasSelfRequirement = true,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>
            {
                // Protocol: configure() -> τ_0_0 (→ TSelf)
                new()
                {
                    Name = "configure",
                    MangledName = "$s10TestModule12ConfigurablePAAE9configurexyF",
                    MethodType = MethodType.Instance,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        CreateArgument(string.Empty, new NamedTypeSpec("τ_0_0"), moduleDecl)
                    },
                    GenericParameters = new List<GenericArgumentDecl>(),
                    ParentDecl = null,
                    ModuleDecl = moduleDecl,
                    Throws = false,
                    IsAsync = false,
                    IsSynthesizedAccessor = false
                }
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Protocols.Add(protocolDecl);

        // Concrete type: configure() returns Builder (the concrete type itself)
        var concreteType = CreateClassDecl("Builder", moduleDecl);
        concreteType.Methods.Add(new MethodDecl
        {
            Name = "configure",
            MangledName = "$s10TestModule7BuilderC9configureACyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, new NamedTypeSpec("TestModule.Builder"), moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = concreteType,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        });

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var result = validator.CanFullyImplementProtocol(concreteType, protocolDecl);

        Assert.True(result);
    }

    [Fact]
    public void CanFullyImplementProtocol_AsyncSelfReturningMethod_MatchesTaskOfConcreteType()
    {
        // Protocol with async Self-returning method: τ_0_0 → TSelf, wrapped as Task<TSelf>.
        // Concrete type returns Task<Builder> → conformance should succeed.
        var typeDatabase = CreateTypeDatabaseWithBuilder();
        var moduleDecl = CreateModuleDecl("TestModule");

        // Protocol: async method returns τ_0_0 → projected as Task<TSelf>
        var protocolDecl = new ProtocolDecl
        {
            Name = "AsyncConfigurable",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.AsyncConfigurable"),
            MangledName = "$s10TestModule17AsyncConfigurableP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = false,
            HasSelfRequirement = true,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>
            {
                new()
                {
                    Name = "configure",
                    MangledName = "$s10TestModule17AsyncConfigurablePAAE9configurexyYaF",
                    MethodType = MethodType.Instance,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        CreateArgument(string.Empty, new NamedTypeSpec("τ_0_0"), moduleDecl)
                    },
                    GenericParameters = new List<GenericArgumentDecl>(),
                    ParentDecl = null,
                    ModuleDecl = moduleDecl,
                    Throws = false,
                    IsAsync = true,
                    IsSynthesizedAccessor = false
                }
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Protocols.Add(protocolDecl);

        // Concrete type: async configure() returns Builder → Task<Builder>
        var concreteType = CreateClassDecl("Builder", moduleDecl);
        concreteType.Methods.Add(new MethodDecl
        {
            Name = "configure",
            MangledName = "$s10TestModule7BuilderC9configureACyYaF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, new NamedTypeSpec("TestModule.Builder"), moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = concreteType,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = true,
            IsSynthesizedAccessor = false
        });

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var result = validator.CanFullyImplementProtocol(concreteType, protocolDecl);

        Assert.True(result);
    }

    [Fact]
    public void CanFullyImplementProtocol_ArrayOfSelfReturningMethod_MatchesArrayOfConcreteType()
    {
        // Protocol with method returning Array<τ_0_0> → IReadOnlyList<TSelf>.
        // Concrete type returns IReadOnlyList<Builder> → conformance should succeed.
        var typeDatabase = CreateTypeDatabaseWithBuilder();
        var moduleDecl = CreateModuleDecl("TestModule");

        // Protocol: method returns Array<τ_0_0> → IReadOnlyList<TSelf>
        var arrayOfSelf = new NamedTypeSpec("Swift.Array");
        arrayOfSelf.GenericParameters.Add(new NamedTypeSpec("τ_0_0"));

        var protocolDecl = new ProtocolDecl
        {
            Name = "ListProvider",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ListProvider"),
            MangledName = "$s10TestModule12ListProviderP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = false,
            HasSelfRequirement = true,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>
            {
                new()
                {
                    Name = "getAll",
                    MangledName = "$s10TestModule12ListProviderPAAE6getAllSayxGyF",
                    MethodType = MethodType.Instance,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        CreateArgument(string.Empty, arrayOfSelf, moduleDecl)
                    },
                    GenericParameters = new List<GenericArgumentDecl>(),
                    ParentDecl = null,
                    ModuleDecl = moduleDecl,
                    Throws = false,
                    IsAsync = false,
                    IsSynthesizedAccessor = false
                }
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Protocols.Add(protocolDecl);

        // Concrete type: getAll() returns Array<Builder> → IReadOnlyList<Builder>
        var arrayOfBuilder = new NamedTypeSpec("Swift.Array");
        arrayOfBuilder.GenericParameters.Add(new NamedTypeSpec("TestModule.Builder"));

        var concreteType = CreateClassDecl("Builder", moduleDecl);
        concreteType.Methods.Add(new MethodDecl
        {
            Name = "getAll",
            MangledName = "$s10TestModule7BuilderC6getAllSayACGyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, arrayOfBuilder, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = concreteType,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        });

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var result = validator.CanFullyImplementProtocol(concreteType, protocolDecl);

        Assert.True(result);
    }

    #endregion

    #region Self-Typed Parameter Matching (Issue 3)

    [Fact]
    public void FindMatchingMethod_SelfTypedParam_MatchesConformingType()
    {
        // Protocol method with τ_0_0 param, concrete type with its own type name → match found
        var typeDatabase = CreateTypeDatabaseWithBuilder();
        var moduleDecl = CreateModuleDecl("TestModule");

        // Protocol: apply(Self) → void
        var protocolDecl = new ProtocolDecl
        {
            Name = "Applicable",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Applicable"),
            MangledName = "$s10TestModule10ApplicableP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = false,
            HasSelfRequirement = true,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>
            {
                new()
                {
                    Name = "apply",
                    MangledName = "$s10TestModule10ApplicableP5applyyyxF",
                    MethodType = MethodType.Instance,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl),
                        CreateArgument("other", new NamedTypeSpec("τ_0_0"), moduleDecl)
                    },
                    GenericParameters = new List<GenericArgumentDecl>(),
                    ParentDecl = null,
                    ModuleDecl = moduleDecl,
                    Throws = false,
                    IsAsync = false,
                    IsSynthesizedAccessor = false
                }
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Protocols.Add(protocolDecl);

        // Concrete: apply(Builder) → void
        var concreteType = CreateClassDecl("Builder", moduleDecl);
        concreteType.Methods.Add(new MethodDecl
        {
            Name = "apply",
            MangledName = "$s10TestModule7BuilderC5applyyyACF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl),
                CreateArgument("other", new NamedTypeSpec("TestModule.Builder"), moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = concreteType,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        });

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var result = validator.CanFullyImplementProtocol(concreteType, protocolDecl);

        Assert.True(result);
    }

    [Fact]
    public void FindMatchingMethod_SelfTypedParam_RejectsWrongSelfParamType()
    {
        // Protocol method with τ_0_0 param, concrete type has Int instead of Builder → no match
        var typeDatabase = CreateTypeDatabaseWithBuilder();
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = new ProtocolDecl
        {
            Name = "Applicable",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Applicable"),
            MangledName = "$s10TestModule10ApplicableP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = false,
            HasSelfRequirement = true,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>
            {
                new()
                {
                    Name = "apply",
                    MangledName = "$s10TestModule10ApplicableP5applyyyxF",
                    MethodType = MethodType.Instance,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl),
                        CreateArgument("other", new NamedTypeSpec("τ_0_0"), moduleDecl)
                    },
                    GenericParameters = new List<GenericArgumentDecl>(),
                    ParentDecl = null,
                    ModuleDecl = moduleDecl,
                    Throws = false,
                    IsAsync = false,
                    IsSynthesizedAccessor = false
                }
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Protocols.Add(protocolDecl);

        // Concrete: apply(Int) → wrong type, should NOT match Self param
        var concreteType = CreateClassDecl("Builder", moduleDecl);
        concreteType.Methods.Add(new MethodDecl
        {
            Name = "apply",
            MangledName = "$s10TestModule7BuilderC5applyyySiF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl),
                CreateArgument("other", new NamedTypeSpec("Swift.Int"), moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = concreteType,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        });

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var result = validator.CanFullyImplementProtocol(concreteType, protocolDecl);

        Assert.False(result);
    }

    [Fact]
    public void FindMatchingMethod_SelfTypedParam_MixedPositions()
    {
        // Protocol: merge(τ_0_0, Swift.Int, τ_0_0). Self positions must equal conforming type.
        var typeDatabase = CreateTypeDatabaseWithBuilder();
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = new ProtocolDecl
        {
            Name = "Mergeable",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Mergeable"),
            MangledName = "$s10TestModule9MergeableP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = false,
            HasSelfRequirement = true,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>
            {
                new()
                {
                    Name = "merge",
                    MangledName = "$s10TestModule9MergeableP5mergeyyxSixF",
                    MethodType = MethodType.Instance,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl),
                        CreateArgument("first", new NamedTypeSpec("τ_0_0"), moduleDecl),
                        CreateArgument("count", new NamedTypeSpec("Swift.Int"), moduleDecl),
                        CreateArgument("second", new NamedTypeSpec("τ_0_0"), moduleDecl)
                    },
                    GenericParameters = new List<GenericArgumentDecl>(),
                    ParentDecl = null,
                    ModuleDecl = moduleDecl,
                    Throws = false,
                    IsAsync = false,
                    IsSynthesizedAccessor = false
                }
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Protocols.Add(protocolDecl);

        // Concrete: merge(Builder, Int, Builder) — correct
        var concreteType = CreateClassDecl("Builder", moduleDecl);
        concreteType.Methods.Add(new MethodDecl
        {
            Name = "merge",
            MangledName = "$s10TestModule7BuilderC5mergeyyACSiACF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl),
                CreateArgument("first", new NamedTypeSpec("TestModule.Builder"), moduleDecl),
                CreateArgument("count", new NamedTypeSpec("Swift.Int"), moduleDecl),
                CreateArgument("second", new NamedTypeSpec("TestModule.Builder"), moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = concreteType,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        });

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var result = validator.CanFullyImplementProtocol(concreteType, protocolDecl);

        Assert.True(result);
    }

    [Fact]
    public void FindMatchingMethod_SelfTypedParam_RejectsWrongNonSelfParam()
    {
        // Protocol: merge(τ_0_0, Swift.Int). Concrete has merge(Builder, Builder) — wrong non-Self param.
        var typeDatabase = CreateTypeDatabaseWithBuilder();
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = new ProtocolDecl
        {
            Name = "Mergeable",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Mergeable"),
            MangledName = "$s10TestModule9MergeableP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = false,
            HasSelfRequirement = true,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>
            {
                new()
                {
                    Name = "merge",
                    MangledName = "$s10TestModule9MergeableP5mergeyyxSiF",
                    MethodType = MethodType.Instance,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl),
                        CreateArgument("first", new NamedTypeSpec("τ_0_0"), moduleDecl),
                        CreateArgument("count", new NamedTypeSpec("Swift.Int"), moduleDecl)
                    },
                    GenericParameters = new List<GenericArgumentDecl>(),
                    ParentDecl = null,
                    ModuleDecl = moduleDecl,
                    Throws = false,
                    IsAsync = false,
                    IsSynthesizedAccessor = false
                }
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Protocols.Add(protocolDecl);

        // Concrete: merge(Builder, Builder) — wrong non-Self param type (Int expected, got Builder)
        var concreteType = CreateClassDecl("Builder", moduleDecl);
        concreteType.Methods.Add(new MethodDecl
        {
            Name = "merge",
            MangledName = "$s10TestModule7BuilderC5mergeyyACACF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl),
                CreateArgument("first", new NamedTypeSpec("TestModule.Builder"), moduleDecl),
                CreateArgument("count", new NamedTypeSpec("TestModule.Builder"), moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = concreteType,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        });

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var result = validator.CanFullyImplementProtocol(concreteType, protocolDecl);

        Assert.False(result);
    }

    #endregion

    #region AnyType Return Compatibility (Issue 3)

    [Fact]
    public void CanFullyImplementProtocol_AnyTypeReturn_MatchesConformingType()
    {
        // Protocol return type resolves to AnyType (unresolved Self),
        // concrete type returns its own type → should match via AnyType compatibility.
        var typeDatabase = CreateTypeDatabaseWithBuilder();
        var moduleDecl = CreateModuleDecl("TestModule");

        // Protocol: transform() -> AnyType (unresolved Self projected as AnyType)
        var protocolDecl = new ProtocolDecl
        {
            Name = "Transformable",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Transformable"),
            MangledName = "$s10TestModule13TransformableP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = false,
            HasSelfRequirement = true,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>
            {
                new()
                {
                    Name = "transform",
                    MangledName = "$s10TestModule13TransformableP9transformxyF",
                    MethodType = MethodType.Instance,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        // Return: Swift.AnyType (unresolved Self)
                        CreateArgument(string.Empty, new NamedTypeSpec("Swift.AnyType"), moduleDecl)
                    },
                    GenericParameters = new List<GenericArgumentDecl>(),
                    ParentDecl = null,
                    ModuleDecl = moduleDecl,
                    Throws = false,
                    IsAsync = false,
                    IsSynthesizedAccessor = false
                }
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Protocols.Add(protocolDecl);

        // Concrete: transform() -> Builder
        var concreteType = CreateClassDecl("Builder", moduleDecl);
        concreteType.Methods.Add(new MethodDecl
        {
            Name = "transform",
            MangledName = "$s10TestModule7BuilderC9transformACyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, new NamedTypeSpec("TestModule.Builder"), moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = concreteType,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        });

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var result = validator.CanFullyImplementProtocol(concreteType, protocolDecl);

        // AnyType in the interface (from unresolved Self/generic param) is NOT
        // compatible with the concrete type's name. C# interface methods require exact
        // type match — transform() -> AnyType != transform() -> Builder.
        Assert.False(result);
    }

    [Fact]
    public void CanFullyImplementProtocol_AnyTypeParam_RejectsConformance()
    {
        // Protocol: isContentEqual(to: AnyType) — from unresolved Self/τ_0_0
        // Concrete: isContentEqual(to: Widget) — uses actual type
        // C# interface requires exact type match: IsContentEqual(AnyType) != IsContentEqual(Widget)
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = new ProtocolDecl
        {
            Name = "ContentEquatable",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ContentEquatable"),
            MangledName = "$s10TestModule16ContentEquatableP",
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
                    Name = "isContentEqual",
                    MangledName = "$s10TestModule16ContentEquatableP02isC5Equalyp2to_tF",
                    MethodType = MethodType.Instance,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        // Return: Swift.Bool
                        CreateArgument(string.Empty, new NamedTypeSpec("Swift.Bool"), moduleDecl),
                        // Param: AnyType (unresolved Self)
                        CreateArgument("source", new NamedTypeSpec("Swift.AnyType"), moduleDecl)
                    },
                    GenericParameters = new List<GenericArgumentDecl>(),
                    ParentDecl = null,
                    ModuleDecl = moduleDecl,
                    Throws = false,
                    IsAsync = false,
                    IsSynthesizedAccessor = false
                }
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Protocols.Add(protocolDecl);

        var concreteType = CreateClassDecl("Widget", moduleDecl);
        concreteType.Methods.Add(new MethodDecl
        {
            Name = "isContentEqual",
            MangledName = "$s10TestModule6WidgetC02isC5EqualyAC2to_tF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, new NamedTypeSpec("Swift.Bool"), moduleDecl),
                CreateArgument("source", new NamedTypeSpec("TestModule.Widget"), moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = concreteType,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        });

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var result = validator.CanFullyImplementProtocol(concreteType, protocolDecl);

        // AnyType param in interface != Widget param in concrete → reject
        Assert.False(result);
    }

    #endregion

    #region Extension Default Awareness

    [Fact]
    public void CanFullyImplementProtocol_MethodHasExtensionDefault_ReturnsTrue()
    {
        // Protocol requires _interpolate(to:amount:spatialOutTangent:spatialInTangent:)
        // Concrete type doesn't implement it, but an extension on a sub-protocol provides the default.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        // AnyInterpolatable protocol requires a 4-param _interpolate
        var parentProtocol = new ProtocolDecl
        {
            Name = "AnyInterpolatable",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.AnyInterpolatable"),
            MangledName = "$s10TestModule17AnyInterpolatableP",
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
                    Name = "_interpolate",
                    MangledName = "$s10TestModule17AnyInterpolatablePAAE12_interpolateyyF",
                    MethodType = MethodType.Instance,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl),
                        CreateArgument("to", new NamedTypeSpec("Swift.Int"), moduleDecl),
                        CreateArgument("amount", new NamedTypeSpec("Swift.Int"), moduleDecl),
                        CreateArgument("spatialOutTangent", new NamedTypeSpec("Swift.Int"), moduleDecl),
                        CreateArgument("spatialInTangent", new NamedTypeSpec("Swift.Int"), moduleDecl)
                    },
                    GenericParameters = new List<GenericArgumentDecl>(),
                    ParentDecl = null,
                    ModuleDecl = moduleDecl,
                    Throws = false,
                    IsAsync = false,
                    IsSynthesizedAccessor = false
                }
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Protocols.Add(parentProtocol);

        // Interpolatable inherits AnyInterpolatable
        var childProtocol = new ProtocolDecl
        {
            Name = "Interpolatable",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Interpolatable"),
            MangledName = "$s10TestModule14InterpolatableP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec> { new NamedTypeSpec("TestModule.AnyInterpolatable") },
            IsClassBound = false,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Protocols.Add(childProtocol);

        // Extension on Interpolatable provides _interpolate default
        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["TestModule.Interpolatable"] = new()
            {
                new ProtocolExtensionMethodDecl
                {
                    ProtocolQualifiedName = "TestModule.Interpolatable",
                    MethodName = "_interpolate",
                    PrintedName = "_interpolate(to:amount:spatialOutTangent:spatialInTangent:)",
                    RawSignature = "func _interpolate(to: Self, amount: CGFloat, spatialOutTangent: CGPoint?, spatialInTangent: CGPoint?) -> Self",
                    ReturnsSelf = true,
                    IsMainActorIsolated = false,
                    IsStatic = false,
                    IsProperty = false,
                    HasSetter = false,
                    IsDeprecated = false,
                    IsMutating = false,
                    WhereConstraints = new List<string>()
                }
            }
        };
        var extensionDefaultsIndex = new ProtocolExtensionDefaultsIndex(extensionMethods, moduleDecl.Protocols);

        // Concrete type that conforms to both protocols but only has interpolate (2-param), not _interpolate (4-param)
        var concreteType = CreateStructDecl("VectorAnimationVector3D", moduleDecl);
        concreteType.Conformances = new List<TypeConformance>
        {
            new(SwiftTypeName.FromModuleQualifiedName("TestModule.VectorAnimationVector3D"),
                SwiftTypeName.FromModuleQualifiedName("TestModule.AnyInterpolatable"), ""),
            new(SwiftTypeName.FromModuleQualifiedName("TestModule.VectorAnimationVector3D"),
                SwiftTypeName.FromModuleQualifiedName("TestModule.Interpolatable"), "")
        };

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase, extensionDefaultsIndex);
        var result = validator.CanFullyImplementProtocol(concreteType, parentProtocol);

        // With inheritance graph enabled, sub-protocol extension default IS found
        // for parent protocol via inheritance chain traversal.
        Assert.True(result);
    }

    [Fact]
    public void CanFullyImplementProtocol_MethodHasNoDefault_ReturnsFalse()
    {
        // Same setup but NO extension default → should fail
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = CreateProtocolWithVoidMethod("AnyInterpolatable", "_interpolate", moduleDecl);
        moduleDecl.Protocols.Add(protocolDecl);

        var concreteType = CreateStructDecl("VectorAnimationVector3D", moduleDecl);

        // No extension defaults index
        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var result = validator.CanFullyImplementProtocol(concreteType, protocolDecl);

        Assert.False(result);
    }

    [Fact]
    public void CanFullyImplementProtocol_SubProtocolDefault_SatisfiesParent()
    {
        // Direct extension default on the parent protocol itself
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = CreateProtocolWithVoidMethod("Configurable", "configure", moduleDecl);
        moduleDecl.Protocols.Add(protocolDecl);

        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["TestModule.Configurable"] = new()
            {
                new ProtocolExtensionMethodDecl
                {
                    ProtocolQualifiedName = "TestModule.Configurable",
                    MethodName = "configure",
                    PrintedName = "configure()",
                    RawSignature = "func configure()",
                    ReturnsSelf = false,
                    IsMainActorIsolated = false,
                    IsStatic = false,
                    IsProperty = false,
                    HasSetter = false,
                    IsDeprecated = false,
                    IsMutating = false,
                    WhereConstraints = new List<string>()
                }
            }
        };
        var extensionDefaultsIndex = new ProtocolExtensionDefaultsIndex(extensionMethods, moduleDecl.Protocols);

        var concreteType = CreateStructDecl("MyConfig", moduleDecl);
        concreteType.Conformances = new List<TypeConformance>
        {
            new(SwiftTypeName.FromModuleQualifiedName("TestModule.MyConfig"),
                SwiftTypeName.FromModuleQualifiedName("TestModule.Configurable"), "")
        };

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase, extensionDefaultsIndex);
        var result = validator.CanFullyImplementProtocol(concreteType, protocolDecl);

        Assert.True(result);
    }

    #endregion

    #region Static Abstract Member Conformance

    [Fact]
    public void CanFullyImplementProtocol_MatchingStaticPropertyAndMethod_ReturnsTrue()
    {
        // Concrete type with matching static members → conformance passes with validation.
        var typeDatabase = CreateTypeDatabase();
        RegisterSwiftInt32(typeDatabase);
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = new ProtocolDecl
        {
            Name = "HasStatic",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.HasStatic"),
            MangledName = "$s10TestModule9HasStaticP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = false,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>
            {
                CreateStaticPropertyDecl("defaultValue", new NamedTypeSpec("Swift.Int32"), moduleDecl, hasGetter: true, hasSetter: false)
            },
            Methods = new List<MethodDecl>
            {
                CreateStaticVoidMethod("reset", moduleDecl),
                CreateVoidMethod("doWork", moduleDecl)
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Protocols.Add(protocolDecl);

        // Concrete type with ALL required members (instance + static)
        var concreteType = CreateStructDecl("MyType", moduleDecl);
        concreteType.Properties.Add(CreateStaticPropertyDecl("defaultValue", new NamedTypeSpec("Swift.Int32"), moduleDecl, hasGetter: true, hasSetter: false, accessorParent: concreteType));
        var staticMethod = CreateStaticVoidMethod("reset", moduleDecl);
        staticMethod.ParentDecl = concreteType;
        concreteType.Methods.Add(staticMethod);
        var instanceMethod = CreateVoidMethod("doWork", moduleDecl);
        instanceMethod.ParentDecl = concreteType;
        concreteType.Methods.Add(instanceMethod);

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var result = validator.CanFullyImplementProtocol(concreteType, protocolDecl);

        Assert.True(result);
    }

    [Fact]
    public void CanFullyImplementProtocol_MissingStaticMethod_StillPasses()
    {
        // Static members are emitted as static virtual (with throw body default).
        // Missing static members don't break conformance — the C# default satisfies the
        // interface contract. Swift guarantees the implementation exists (on the type or
        // via extension default). Lenient validation avoids false drops when our extension
        // default index has coverage gaps.
        var typeDatabase = CreateTypeDatabase();
        RegisterSwiftInt32(typeDatabase);
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = new ProtocolDecl
        {
            Name = "HasStaticMethod",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.HasStaticMethod"),
            MangledName = "$s10TestModule15HasStaticMethodP",
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
                CreateStaticVoidMethod("reset", moduleDecl)
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Protocols.Add(protocolDecl);

        // Concrete type WITHOUT the static method — still passes (static virtual default)
        var concreteType = CreateStructDecl("MyType", moduleDecl);

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var result = validator.CanFullyImplementProtocol(concreteType, protocolDecl);

        Assert.True(result);
    }

    [Fact]
    public void HasEmittableInterfaceMembers_OnlyStaticMembers_ReturnsTrue()
    {
        var typeDatabase = CreateTypeDatabase();
        RegisterSwiftInt32(typeDatabase);
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = new ProtocolDecl
        {
            Name = "StaticOnly",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.StaticOnly"),
            MangledName = "$s10TestModule10StaticOnlyP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = false,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>
            {
                CreateStaticPropertyDecl("value", new NamedTypeSpec("Swift.Int32"), moduleDecl, hasGetter: true, hasSetter: false)
            },
            Methods = new List<MethodDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var result = validator.HasEmittableInterfaceMembers(protocolDecl);

        Assert.True(result);
    }

    private static PropertyDecl CreateStaticPropertyDecl(string name, TypeSpec typeSpec, ModuleDecl moduleDecl, bool hasGetter, bool hasSetter, BaseDecl? accessorParent = null)
    {
        var accessors = new List<AccessorDecl>();
        if (hasGetter)
        {
            accessors.Add(new GetAccessorDecl
            {
                Method = new MethodDecl
                {
                    Name = $"{name}_Get",
                    MangledName = $"$s10TestModule{name}SivgZ",
                    MethodType = MethodType.Static,
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
                    IsSynthesizedAccessor = false
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
                    MangledName = $"$s10TestModule{name}SivsZ",
                    MethodType = MethodType.Static,
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
                    IsSynthesizedAccessor = false
                }
            });
        }

        return new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = typeSpec,
            IsStatic = true,
            HasStorage = false,
            Accessors = accessors,
            ParentDecl = accessorParent,
            ModuleDecl = moduleDecl
        };
    }

    private static MethodDecl CreateStaticVoidMethod(string name, ModuleDecl moduleDecl)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule{name}yyFZ",
            MethodType = MethodType.Static,
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
            IsSynthesizedAccessor = false
        };
    }

    private static void RegisterSwiftInt32(TypeDatabase typeDatabase)
    {
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (SwiftTypeName.FromModuleQualifiedName("Swift.Int32"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int32"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int32"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            })
        });
    }

    #endregion

    #region Inherited Protocol Conformance Validation

    [Fact]
    public void CanFullyImplementProtocol_InheritedProtocolRequirementMet_ReturnsTrue()
    {
        // Drawable inherits Describable. Concrete type has both describe() and draw().
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var describable = CreateProtocolWithVoidMethod("Describable", "describe", moduleDecl);
        var drawable = CreateProtocolWithVoidMethod("Drawable", "draw", moduleDecl);
        drawable.InheritedProtocols.Add(new NamedTypeSpec("TestModule.Describable"));

        moduleDecl.Protocols.Add(describable);
        moduleDecl.Protocols.Add(drawable);

        var concreteType = CreateStructDecl("Shape", moduleDecl);
        var describeMethod = CreateVoidMethod("describe", moduleDecl);
        describeMethod.ParentDecl = concreteType;
        var drawMethod = CreateVoidMethod("draw", moduleDecl);
        drawMethod.ParentDecl = concreteType;
        concreteType.Methods = new List<MethodDecl> { describeMethod, drawMethod };

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        Assert.True(validator.CanFullyImplementProtocol(concreteType, drawable));
    }

    [Fact]
    public void CanFullyImplementProtocol_InheritedProtocolRequirementMissing_ReturnsFalse()
    {
        // Drawable inherits Describable. Concrete type has draw() but NOT describe().
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var describable = CreateProtocolWithVoidMethod("Describable", "describe", moduleDecl);
        var drawable = CreateProtocolWithVoidMethod("Drawable", "draw", moduleDecl);
        drawable.InheritedProtocols.Add(new NamedTypeSpec("TestModule.Describable"));

        moduleDecl.Protocols.Add(describable);
        moduleDecl.Protocols.Add(drawable);

        var concreteType = CreateStructDecl("Shape", moduleDecl);
        var drawMethod = CreateVoidMethod("draw", moduleDecl);
        drawMethod.ParentDecl = concreteType;
        concreteType.Methods = new List<MethodDecl> { drawMethod };
        // Missing: describe()

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        Assert.False(validator.CanFullyImplementProtocol(concreteType, drawable));
    }

    [Fact]
    public void CanFullyImplementProtocol_CrossModuleInheritedProtocol_SkippedGracefully()
    {
        // If inherited protocol is from a different module (not in ModuleDecl.Protocols),
        // validation should still pass — cross-module requirements can't be validated.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var drawable = CreateProtocolWithVoidMethod("Drawable", "draw", moduleDecl);
        drawable.InheritedProtocols.Add(new NamedTypeSpec("OtherModule.Describable"));

        moduleDecl.Protocols.Add(drawable);

        var concreteType = CreateStructDecl("Shape", moduleDecl);
        var drawMethod = CreateVoidMethod("draw", moduleDecl);
        drawMethod.ParentDecl = concreteType;
        concreteType.Methods = new List<MethodDecl> { drawMethod };

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        // Should pass — cross-module inherited protocol is skipped
        Assert.True(validator.CanFullyImplementProtocol(concreteType, drawable));
    }

    [Fact]
    public void CanFullyImplementProtocol_WitnessNameCollidesWithParentGenericParam_DropsConformance()
    {
        // Name-parity prediction must fold in ParentGenericParameterNames the way the emitted
        // name does: a witness `t()` on generic `Container<T>` PascalCases to `T`, which the
        // emitter renames to `TMethod` (CS0102 vs the type parameter). The interface member is
        // `T` (protocol parents have no generic-param axis), so the emitted witness never
        // implements the interface slot — the validator must drop the conformance (CS0535). A
        // positional name recomputation without the axis predicts `T` and wrongly keeps it.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = CreateProtocolWithVoidMethod("Tickable", "t", moduleDecl);
        moduleDecl.Protocols.Add(protocolDecl);

        var concreteType = CreateClassDecl("Container", moduleDecl);
        concreteType.GenericParameters = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl(
                TypeName: "T",
                SugaredTypeName: "T",
                GenericConformances: new List<GenericParameterConformance>(),
                AssosiatedTypeConformances: new List<GenericParameterConformance>())
        };
        var witness = CreateVoidMethod("t", moduleDecl);
        witness.ParentDecl = concreteType;
        concreteType.Methods.Add(witness);

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        Assert.False(validator.CanFullyImplementProtocol(concreteType, protocolDecl));
    }

    // Discrimination control: the SAME witness on a NON-generic parent emits as `T` — matching
    // the interface member — so the conformance must be KEPT. Guards against the parity fix
    // degenerating into "always reject single-letter witnesses".
    [Fact]
    public void CanFullyImplementProtocol_WitnessNameOnNonGenericParent_KeepsConformance()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = CreateProtocolWithVoidMethod("Tickable", "t", moduleDecl);
        moduleDecl.Protocols.Add(protocolDecl);

        var concreteType = CreateClassDecl("Ticker", moduleDecl);
        var witness = CreateVoidMethod("t", moduleDecl);
        witness.ParentDecl = concreteType;
        concreteType.Methods.Add(witness);

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        Assert.True(validator.CanFullyImplementProtocol(concreteType, protocolDecl));
    }

    // STATIC counterpart: the same generic-parameter rename (`t` → `TMethod` on `Container<T>`)
    // must KEEP the conformance. Static requirements are emitted as `static virtual` with a
    // throwing default body, so a witness under a diverged C# name is compile-benign — the
    // default satisfies the interface slot, exactly like the member-absent case. Dropping here
    // would trade a compile-safe conformance for CS0311 on every generic constraint.
    [Fact]
    public void CanFullyImplementProtocol_StaticWitnessNameCollidesWithParentGenericParam_KeepsConformance()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = new ProtocolDecl
        {
            Name = "StaticTickable",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.StaticTickable"),
            MangledName = "$s10TestModule14StaticTickableP",
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
                CreateStaticVoidMethod("t", moduleDecl)
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Protocols.Add(protocolDecl);

        var concreteType = CreateClassDecl("Container", moduleDecl);
        concreteType.GenericParameters = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl(
                TypeName: "T",
                SugaredTypeName: "T",
                GenericConformances: new List<GenericParameterConformance>(),
                AssosiatedTypeConformances: new List<GenericParameterConformance>())
        };
        var witness = CreateStaticVoidMethod("t", moduleDecl);
        witness.ParentDecl = concreteType;
        concreteType.Methods.Add(witness);

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        Assert.True(validator.CanFullyImplementProtocol(concreteType, protocolDecl));
    }

    #endregion
}
