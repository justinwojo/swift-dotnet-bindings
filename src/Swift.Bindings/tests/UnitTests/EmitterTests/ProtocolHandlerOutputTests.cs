// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

public class ProtocolHandlerOutputTests
{
    [Fact]
    public void Emit_ProtocolWithAssociatedTypes_EmitsGenericInterfaceAndSkipsProxy()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var protocolDecl = new ProtocolDecl
        {
            Name = "Reader",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Reader"),
            MangledName = "$s10TestModule6ReaderP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl> { new() { Name = "Element" } },
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = false,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>
            {
                new()
                {
                    Name = "next",
                    MangledName = "$s10TestModule6ReaderP4next7ElementQzyF",
                    MethodType = MethodType.Instance,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        CreateArgument(string.Empty, new AssociatedTypeReferenceSpec("Self.Element"), moduleDecl)
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

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        Assert.Contains("public interface IReader<TElement>", csOutput);
        Assert.Contains("TElement GetNext();", csOutput);
        Assert.DoesNotContain("class ReaderProxy", csOutput);
    }

    [Fact]
    public void Emit_ProtocolWithSelfRequirement_EmitsRecursiveConstraintAndSkipsProxy()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var protocolDecl = new ProtocolDecl
        {
            Name = "ComparableLike",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ComparableLike"),
            MangledName = "$s10TestModule14ComparableLikeP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = "<Self where Self : ComparableLike>",
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = false,
            HasSelfRequirement = true,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        Assert.Contains("public interface IComparableLike<TSelf> where TSelf : IComparableLike<TSelf>", csOutput);
        Assert.DoesNotContain("class ComparableLikeProxy", csOutput);
    }

    [Fact]
    public void Emit_ProtocolWithMembers_EmitsProxyAndAsyncSignatures()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var property = new PropertyDecl
        {
            Name = "count",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl
                {
                    Method = new MethodDecl
                    {
                        Name = "count_Get",
                        MangledName = "$s10TestModule8CacheableP5countSivg",
                        MethodType = MethodType.Instance,
                        IsConstructor = false,
                        CSSignature = new List<ArgumentDecl> { CreateArgument(string.Empty, new NamedTypeSpec("Swift.Int"), moduleDecl) },
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
            Name = "Cacheable",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Cacheable"),
            MangledName = "$s10TestModule8CacheableP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>
            {
                new("Swift.AnyObject"),
                new("Swift.Hashable")
            },
            IsClassBound = true,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl> { property },
            Methods = new List<MethodDecl>
            {
                new()
                {
                    Name = "fetch",
                    MangledName = "$s10TestModule8CacheableP5fetchSiyYaF",
                    MethodType = MethodType.Instance,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        CreateArgument(string.Empty, new NamedTypeSpec("Swift.Int"), moduleDecl),
                        CreateArgument("key", new NamedTypeSpec("Swift.Int"), moduleDecl)
                    },
                    GenericParameters = new List<GenericArgumentDecl>(),
                    ParentDecl = null,
                    ModuleDecl = moduleDecl,
                    Throws = false,
                    IsAsync = true,
                    Visibility = Visibility.Public
                }
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        Assert.Contains("public interface ICacheable : ISwiftHashable", csOutput);
        Assert.Contains("long Count { get; }", csOutput);
        Assert.Contains("Task<long> FetchAsync(long key, global::System.Threading.CancellationToken cancellationToken = default);", csOutput);
        Assert.Contains("public unsafe partial class CacheableProxy : ICacheable, ISwiftObject, IDisposable", csOutput);
    }

    [Fact]
    public void Emit_ProtocolWithDuplicateMethodSignatures_EmitsSingleMethodDeclaration()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var protocolDecl = new ProtocolDecl
        {
            Name = "Duplicated",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Duplicated"),
            MangledName = "$s10TestModule10DuplicatedP",
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
                CreateMethodDecl("refresh", moduleDecl),
                CreateMethodDecl("refresh", moduleDecl)
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        Assert.Equal(1, EmitterTestHelpers.CountOccurrences(csOutput, "void Refresh();"));
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

    private static MethodDecl CreateMethodDecl(string name, ModuleDecl moduleDecl)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule10DuplicatedP{name}yyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl> { CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl) },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    #region AnyType Generic Argument Skip Tests

    [Fact]
    public void Emit_MethodWithAnyTypeGenericReturnArg_SkipsMethodOnInterface()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        // Register a bound generic type (e.g., BatchedCollection) so it doesn't fall back to AnyType itself
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (SwiftTypeName.FromModuleQualifiedName("TestModule.BatchedCollection"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "BatchedCollection"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.BatchedCollection"),
                MetadataAccessor = "$s10TestModule17BatchedCollectionVMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            })
        });

        // Create a bound generic return type with an unresolvable type parameter
        // → resolves to BatchedCollection<Swift.AnyType>
        var returnTypeSpec = new NamedTypeSpec("TestModule.BatchedCollection");
        returnTypeSpec.GenericParameters.Add(new NamedTypeSpec("SomeUnknownProtocol"));

        var protocolDecl = new ProtocolDecl
        {
            Name = "SwiftCollection",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.SwiftCollection"),
            MangledName = "$s10TestModule15SwiftCollectionP",
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
                CreateMethodDeclWithReturn("batched", returnTypeSpec, moduleDecl),
                CreateMethodDecl("toArray", moduleDecl) // normal method, should still emit
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        // Interface should NOT contain Batched (AnyType generic arg)
        Assert.DoesNotContain("Batched", csOutput.Split("class")[0]); // only check interface part
        // Interface should still contain ToArray (no AnyType issue)
        Assert.Contains("void ToArray();", csOutput);
    }

    [Fact]
    public void Emit_MethodWithAnyTypeGenericReturnArg_SkipsMethodOnProxy()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (SwiftTypeName.FromModuleQualifiedName("TestModule.BatchedCollection"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "BatchedCollection"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.BatchedCollection"),
                MetadataAccessor = "$s10TestModule17BatchedCollectionVMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            })
        });

        var returnTypeSpec = new NamedTypeSpec("TestModule.BatchedCollection");
        returnTypeSpec.GenericParameters.Add(new NamedTypeSpec("SomeUnknownProtocol"));

        var protocolDecl = new ProtocolDecl
        {
            Name = "SwiftCollection",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.SwiftCollection"),
            MangledName = "$s10TestModule15SwiftCollectionP",
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
                CreateMethodDeclWithReturn("batched", returnTypeSpec, moduleDecl),
                CreateMethodDecl("toArray", moduleDecl)
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        // Proxy class should NOT contain a public Batched method
        var proxyPart = csOutput.Substring(csOutput.IndexOf("class SwiftCollectionProxy"));
        Assert.DoesNotContain("public TestModule.BatchedCollection<Swift.AnyType> Batched", proxyPart);
        // Proxy class should NOT contain a Receive_batched receiver
        Assert.DoesNotContain("Receive_batched", proxyPart);
        // Proxy should still contain ToArray
        Assert.Contains("public void ToArray()", proxyPart);
    }

    [Fact]
    public void Emit_MethodWithAnyTypeGenericReturnArg_PreservesVtableField()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (SwiftTypeName.FromModuleQualifiedName("TestModule.BatchedCollection"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "BatchedCollection"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.BatchedCollection"),
                MetadataAccessor = "$s10TestModule17BatchedCollectionVMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            })
        });

        var returnTypeSpec = new NamedTypeSpec("TestModule.BatchedCollection");
        returnTypeSpec.GenericParameters.Add(new NamedTypeSpec("SomeUnknownProtocol"));

        var protocolDecl = new ProtocolDecl
        {
            Name = "SwiftCollection",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.SwiftCollection"),
            MangledName = "$s10TestModule15SwiftCollectionP",
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
                CreateMethodDeclWithReturn("batched", returnTypeSpec, moduleDecl),
                CreateMethodDecl("toArray", moduleDecl)
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        // Vtable struct fields must still exist (Swift layout preservation)
        Assert.Contains("func_batched_0", csOutput);
        Assert.Contains("Func_batched_0", csOutput);
        // But vtable assignment should NOT reference a receiver
        Assert.DoesNotContain("&Receive_batched_0", csOutput);
    }

    [Fact]
    public void Emit_MethodWithValidBoundGenericReturn_EmitsNormally()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        // Register both the generic container and its type argument
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (SwiftTypeName.FromModuleQualifiedName("TestModule.Container"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Container"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Container"),
                MetadataAccessor = "$s10TestModule9ContainerVMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            })
        });

        // Return Container<Int> — Int resolves to Int64, no AnyType
        var returnTypeSpec = new NamedTypeSpec("TestModule.Container");
        returnTypeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));

        var protocolDecl = new ProtocolDecl
        {
            Name = "DataProvider",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.DataProvider"),
            MangledName = "$s10TestModule12DataProviderP",
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
                CreateMethodDeclWithReturn("getData", returnTypeSpec, moduleDecl)
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        // Interface should contain GetData with valid Container<long> return
        Assert.Contains("TestModule.Container<long> GetData();", csOutput);
        // Proxy should also have GetData
        Assert.Contains("public TestModule.Container<long> GetData()", csOutput);
    }

    [Theory]
    [InlineData("BatchedCollection<Swift.AnyType>", true)]
    [InlineData("BatchedCollection<AnyType>", true)]
    [InlineData("Swift.AnyType", false)]
    [InlineData("AnyType", false)]
    [InlineData("Container<long>", false)]
    [InlineData("System.String", false)]
    [InlineData("Func<Swift.AnyType, bool>", true)]
    [InlineData("Container<MyAnyTypeModel>", false)]   // substring false-positive guard
    [InlineData("Container<AnyTypeHelper>", false)]     // prefix match guard
    [InlineData("Container<SomeAnyType>", false)]       // suffix match guard
    [InlineData("Container<_AnyType>", false)]          // underscore prefix guard
    [InlineData("Container<AnyType_>", false)]          // underscore suffix guard
    public void ContainsAnyTypeGenericArg_DetectsCorrectly(string typeName, bool expected)
    {
        Assert.Equal(expected, MemberGateEvaluator.ContainsAnyTypeGenericArg(typeName));
    }

    [Fact]
    public void Emit_PropertyWithAnyTypeGenericArg_SkipsPropertyOnInterfaceAndProxy()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (SwiftTypeName.FromModuleQualifiedName("TestModule.BatchedCollection"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "BatchedCollection"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.BatchedCollection"),
                MetadataAccessor = "$s10TestModule17BatchedCollectionVMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            })
        });

        // Property type: BatchedCollection<SomeUnknownProtocol> → BatchedCollection<Swift.AnyType>
        var propertyTypeSpec = new NamedTypeSpec("TestModule.BatchedCollection");
        propertyTypeSpec.GenericParameters.Add(new NamedTypeSpec("SomeUnknownProtocol"));

        var protocolDecl = new ProtocolDecl
        {
            Name = "SwiftCollection",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.SwiftCollection"),
            MangledName = "$s10TestModule15SwiftCollectionP",
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
                    Name = "batchedItems",
                    SwiftTypeSpec = propertyTypeSpec,
                    IsStatic = false,
                    HasStorage = false,
                    Accessors = new List<AccessorDecl>
                    {
                        new GetAccessorDecl
                        {
                            Method = new MethodDecl
                            {
                                Name = "batchedItems_Get",
                                MangledName = "$s10TestModule15SwiftCollectionP12batchedItemsVg",
                                MethodType = MethodType.Instance,
                                IsConstructor = false,
                                CSSignature = new List<ArgumentDecl> { CreateArgument(string.Empty, propertyTypeSpec, moduleDecl) },
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
            Methods = new List<MethodDecl>
            {
                CreateMethodDecl("toArray", moduleDecl)
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        // Interface should NOT contain the property with AnyType generic arg
        Assert.DoesNotContain("BatchedItems", csOutput.Substring(0, csOutput.IndexOf("class SwiftCollectionProxy")));
        // Proxy class should NOT contain the property
        var proxyPart = csOutput.Substring(csOutput.IndexOf("class SwiftCollectionProxy"));
        Assert.DoesNotContain("BatchedItems", proxyPart);
        Assert.DoesNotContain("Receive_batchedItems", proxyPart);
        // Proxy should still contain ToArray
        Assert.Contains("public void ToArray()", proxyPart);
        // Vtable struct fields must still exist (Swift layout preservation)
        Assert.Contains("func_batchedItems_get", proxyPart);
    }

    [Fact]
    public void Emit_SubscriptWithAnyTypeGenericArg_SkipsSubscriptOnInterfaceAndProxy()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (SwiftTypeName.FromModuleQualifiedName("TestModule.Wrapper"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Wrapper"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Wrapper"),
                MetadataAccessor = "$s10TestModule7WrapperVMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            })
        });

        // Subscript return type: Wrapper<SomeUnknownProtocol> → Wrapper<Swift.AnyType>
        var returnTypeSpec = new NamedTypeSpec("TestModule.Wrapper");
        returnTypeSpec.GenericParameters.Add(new NamedTypeSpec("SomeUnknownProtocol"));

        var protocolDecl = new ProtocolDecl
        {
            Name = "IndexedCollection",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.IndexedCollection"),
            MangledName = "$s10TestModule17IndexedCollectionP",
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
                CreateMethodDecl("count", moduleDecl)
            },
            Subscripts = new List<SubscriptDecl>
            {
                new()
                {
                    Name = "subscript",
                    MangledName = "$s10TestModule17IndexedCollectionP9subscriptSig",
                    ReturnTypeSpec = returnTypeSpec,
                    IsStatic = false,
                    IndexParameters = new List<ArgumentDecl>
                    {
                        CreateArgument("index", new NamedTypeSpec("Swift.Int"), moduleDecl)
                    },
                    Accessors = new List<AccessorDecl>
                    {
                        new GetAccessorDecl { Method = CreateMethodDecl("subscript_get", moduleDecl) }
                    },
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        // Interface should NOT contain the subscript with AnyType generic arg
        Assert.DoesNotContain("this[", csOutput.Substring(0, csOutput.IndexOf("class IndexedCollectionProxy")));
        // Proxy class should NOT contain the subscript
        var proxyPart = csOutput.Substring(csOutput.IndexOf("class IndexedCollectionProxy"));
        Assert.DoesNotContain("this[", proxyPart.Substring(proxyPart.IndexOf("Interface Implementation")));
        Assert.DoesNotContain("Receive_subscript_0", proxyPart);
        // Proxy should still contain Count
        Assert.Contains("public void Count()", proxyPart);
        // Vtable struct fields must still exist (Swift layout preservation)
        Assert.Contains("func_subscript_0_get", proxyPart);
    }

    #endregion

    #region [UnsupportedSwiftType] Interface Member Tests

    [Fact]
    public void Emit_InterfacePropertyWithAnyTypeFallback_EmitsUnsupportedSwiftTypeAttribute()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var property = new PropertyDecl
        {
            Name = "data",
            SwiftTypeSpec = new NamedTypeSpec("UnknownModule.Foo"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl
                {
                    Method = new MethodDecl
                    {
                        Name = "data_Get",
                        MangledName = "$s10TestModule8ReadableP4dataSivg",
                        MethodType = MethodType.Instance,
                        IsConstructor = false,
                        CSSignature = new List<ArgumentDecl> { CreateArgument(string.Empty, new NamedTypeSpec("UnknownModule.Foo"), moduleDecl) },
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
            Properties = new List<PropertyDecl> { property },
            Methods = new List<MethodDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        Assert.Contains("[global::Swift.UnsupportedSwiftType(\"Type is missing from the type database\", \"UnknownModule.Foo\")]", csOutput);
        Assert.Contains("Swift.AnyType Data { get; }", csOutput);
    }

    [Fact]
    public void Emit_InterfaceMethodReturnWithAnyTypeFallback_EmitsUnsupportedSwiftTypeAttribute()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

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
                CreateMethodDeclWithReturn("process", new NamedTypeSpec("UnknownModule.Bar"), moduleDecl)
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        Assert.Contains("[global::Swift.UnsupportedSwiftType(\"Type is missing from the type database\", \"UnknownModule.Bar\")]", csOutput);
        Assert.Contains("Swift.AnyType Process();", csOutput);
    }

    [Fact]
    public void Emit_InterfaceMethodParamWithAnyTypeFallback_EmitsUnsupportedSwiftTypeAttribute()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var method = new MethodDecl
        {
            Name = "transform",
            MangledName = "$s10TestModule11TransformerP9transformyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl),
                CreateArgument("input", new NamedTypeSpec("UnknownModule.Baz"), moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

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
            Methods = new List<MethodDecl> { method },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        Assert.Contains("[global::Swift.UnsupportedSwiftType(\"Type is missing from the type database\", \"UnknownModule.Baz\")]", csOutput);
        Assert.Contains("void Transform(Swift.AnyType input);", csOutput);
    }

    [Fact]
    public void Emit_InterfaceSubscriptReturnWithAnyTypeFallback_EmitsUnsupportedSwiftTypeAttribute()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = new ProtocolDecl
        {
            Name = "Storage",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Storage"),
            MangledName = "$s10TestModule7StorageP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = false,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Subscripts = new List<SubscriptDecl>
            {
                new()
                {
                    Name = "subscript",
                    MangledName = "$s10TestModule7StorageP9subscriptig",
                    ReturnTypeSpec = new NamedTypeSpec("UnknownModule.Value"),
                    IsStatic = false,
                    IndexParameters = new List<ArgumentDecl>
                    {
                        CreateArgument("key", new NamedTypeSpec("Swift.Int"), moduleDecl)
                    },
                    Accessors = new List<AccessorDecl>
                    {
                        new GetAccessorDecl { Method = CreateMethodDecl("subscript_get", moduleDecl) }
                    },
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        Assert.Contains("[global::Swift.UnsupportedSwiftType(\"Type is missing from the type database\", \"UnknownModule.Value\")]", csOutput);
        Assert.Contains("this[", csOutput);
    }

    [Fact]
    public void Emit_InterfaceSubscriptParamWithAnyTypeFallback_EmitsUnsupportedSwiftTypeAttribute()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = new ProtocolDecl
        {
            Name = "Lookup",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Lookup"),
            MangledName = "$s10TestModule6LookupP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = false,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Subscripts = new List<SubscriptDecl>
            {
                new()
                {
                    Name = "subscript",
                    MangledName = "$s10TestModule6LookupP9subscriptig",
                    ReturnTypeSpec = new NamedTypeSpec("Swift.Int"),
                    IsStatic = false,
                    IndexParameters = new List<ArgumentDecl>
                    {
                        CreateArgument("key", new NamedTypeSpec("UnknownModule.Key"), moduleDecl)
                    },
                    Accessors = new List<AccessorDecl>
                    {
                        new GetAccessorDecl { Method = CreateMethodDecl("subscript_get", moduleDecl) }
                    },
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        Assert.Contains("[global::Swift.UnsupportedSwiftType(\"Type is missing from the type database\", \"UnknownModule.Key\")]", csOutput);
        Assert.Contains("this[", csOutput);
    }

    #endregion

    #region Async-Void Method Naming Regression (Codex P1)

    [Fact]
    public void Emit_AsyncVoidMethod_NoGetPrefix()
    {
        // Regression: async void methods had returnType changed to "Task" before
        // hasReturnValue was computed, causing noun-only names to get Get prefix.
        // "flush" async void → should be FlushAsync, not GetFlushAsync
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var protocolDecl = new ProtocolDecl
        {
            Name = "AsyncCache",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.AsyncCache"),
            MangledName = "$s10TestModule10AsyncCacheP",
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
                    Name = "flush",
                    MangledName = "$s10TestModule10AsyncCacheP5flushyyYaF",
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
                    IsAsync = true,
                    Visibility = Visibility.Public
                }
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        // Should be FlushAsync (void return → no Get prefix)
        Assert.Contains("FlushAsync(", csOutput);
        Assert.DoesNotContain("GetFlushAsync", csOutput);
        // Return type should be Task, not Task<void>; async methods include CancellationToken
        Assert.Contains("Task FlushAsync(global::System.Threading.CancellationToken cancellationToken = default)", csOutput);
    }

    [Fact]
    public void Emit_AsyncValueMethod_SkipsGetPrefix()
    {
        // Async method with non-void return → no Get prefix (async methods skip it)
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var protocolDecl = new ProtocolDecl
        {
            Name = "DataProvider",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.DataProvider"),
            MangledName = "$s10TestModule12DataProviderP",
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
                    Name = "data",
                    MangledName = "$s10TestModule12DataProviderP4datayyYaF",
                    MethodType = MethodType.Instance,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        CreateArgument(string.Empty, new NamedTypeSpec("Swift.String"), moduleDecl)
                    },
                    GenericParameters = new List<GenericArgumentDecl>(),
                    ParentDecl = null,
                    ModuleDecl = moduleDecl,
                    Throws = false,
                    IsAsync = true,
                    Visibility = Visibility.Public
                }
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        // Non-void return + noun name → DataAsync (async methods skip Get prefix)
        Assert.Contains("DataAsync(global::System.Threading.CancellationToken cancellationToken = default)", csOutput);
    }

    #endregion

    #region Protocol Parameter Name Normalization (Codex P1)

    [Fact]
    public void Emit_ProtocolMethodWithArg0_UsesTypeDerivedName()
    {
        // Regression: protocol interface emission used raw arg.Name ("arg0")
        // instead of GetCSharpParameterName which derives from type
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
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
                    Name = "process",
                    MangledName = "$s10TestModule9ProcessorP7processyyF",
                    MethodType = MethodType.Instance,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl),  // return
                        new ArgumentDecl  // parameter with arg0 name
                        {
                            Name = "arg0",
                            PrivateName = "",
                            SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                            IsInOut = false,
                            IsGeneric = false,
                            ParentDecl = null,
                            ModuleDecl = moduleDecl
                        }
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

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        // "arg0" with Swift.String type → "value" (type-derived) in the interface signature
        Assert.Contains("string value)", csOutput);
        // Interface method declaration should not contain "arg0" parameter name
        Assert.DoesNotContain("string arg0)", csOutput);
    }

    [Fact]
    public void Emit_ProtocolMethodWithUnderscoreParams_DeduplicatesNames()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var protocolDecl = new ProtocolDecl
        {
            Name = "Interpolator",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Interpolator"),
            MangledName = "$s10TestModule12InterpolatorP",
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
                    Name = "interpolate",
                    MangledName = "$s10TestModule12InterpolatorP11interpolateyyF",
                    MethodType = MethodType.Instance,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl),
                        new ArgumentDecl
                        {
                            Name = "_",
                            PrivateName = string.Empty,
                            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                            IsInOut = false,
                            IsGeneric = false,
                            ParentDecl = null,
                            ModuleDecl = moduleDecl
                        },
                        new ArgumentDecl
                        {
                            Name = "_",
                            PrivateName = string.Empty,
                            SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                            IsInOut = false,
                            IsGeneric = false,
                            ParentDecl = null,
                            ModuleDecl = moduleDecl
                        }
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

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        Assert.Contains("void Interpolate(long value, string value2);", csOutput);
    }

    #endregion

    #region Subscript Type Conversion Tests (WU3)

    [Fact]
    public void Emit_InterfaceSubscript_SwiftOptional_ConvertedToNullable()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var optionalReturn = new NamedTypeSpec("Swift.Optional");
        optionalReturn.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));

        var protocolDecl = new ProtocolDecl
        {
            Name = "Cache",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Cache"),
            MangledName = "$s10TestModule5CacheP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = false,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Subscripts = new List<SubscriptDecl>
            {
                new()
                {
                    Name = "subscript",
                    MangledName = "$s10TestModule5CacheP9subscriptig",
                    ReturnTypeSpec = optionalReturn,
                    IsStatic = false,
                    IndexParameters = new List<ArgumentDecl>
                    {
                        CreateArgument("key", new NamedTypeSpec("Swift.String"), moduleDecl)
                    },
                    Accessors = new List<AccessorDecl>
                    {
                        new GetAccessorDecl { Method = CreateMethodDecl("subscript_get", moduleDecl) }
                    },
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        // Interface subscript should use nullable int?, not SwiftOptional
        Assert.Contains("long?", csOutput);
        Assert.Contains("this[", csOutput);
        // Parameters should also be converted (SwiftString → string)
        Assert.Contains("string", csOutput);
    }

    [Fact]
    public void Emit_InterfaceSubscript_SwiftString_ConvertedToString()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = new ProtocolDecl
        {
            Name = "StringLookup",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.StringLookup"),
            MangledName = "$s10TestModule12StringLookupP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = false,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Subscripts = new List<SubscriptDecl>
            {
                new()
                {
                    Name = "subscript",
                    MangledName = "$s10TestModule12StringLookupP9subscriptig",
                    ReturnTypeSpec = new NamedTypeSpec("Swift.String"),
                    IsStatic = false,
                    IndexParameters = new List<ArgumentDecl>
                    {
                        CreateArgument("index", new NamedTypeSpec("Swift.Int"), moduleDecl)
                    },
                    Accessors = new List<AccessorDecl>
                    {
                        new GetAccessorDecl { Method = CreateMethodDecl("subscript_get", moduleDecl) }
                    },
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        // Interface subscript return type should be string, not SwiftString
        Assert.Contains("string this[", csOutput);
    }

    #endregion

    #region Subscript Parameter Normalization (Codex P1)

    [Fact]
    public void Emit_InterfaceSubscript_ValueParam_SanitizedToAvoidCS0316()
    {
        // "value" is a valid C# parameter name — no longer sanitized.
        // It was previously sanitized to "_value" to avoid CS0316 but that's no longer needed.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var protocolDecl = new ProtocolDecl
        {
            Name = "ValueLookup",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ValueLookup"),
            MangledName = "$s10TestModule11ValueLookupP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = false,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Subscripts = new List<SubscriptDecl>
            {
                new()
                {
                    Name = "subscript",
                    MangledName = "$s10TestModule11ValueLookupP9subscriptig",
                    ReturnTypeSpec = new NamedTypeSpec("Swift.Int"),
                    IsStatic = false,
                    IndexParameters = new List<ArgumentDecl>
                    {
                        CreateArgument("value", new NamedTypeSpec("Swift.Int"), moduleDecl)
                    },
                    Accessors = new List<AccessorDecl>
                    {
                        new GetAccessorDecl { Method = CreateMethodDecl("subscript_get", moduleDecl) }
                    },
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        // Parameter "value" is no longer sanitized — it's valid as a parameter name
        Assert.Contains("this[long value]", csOutput);
        Assert.DoesNotContain("this[long _value]", csOutput);
    }

    [Fact]
    public void Emit_InterfaceSubscript_UnderscoreParams_DeduplicatesNames()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var protocolDecl = new ProtocolDecl
        {
            Name = "LabelLookup",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.LabelLookup"),
            MangledName = "$s10TestModule11LabelLookupP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = false,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Subscripts = new List<SubscriptDecl>
            {
                new()
                {
                    Name = "subscript",
                    MangledName = "$s10TestModule11LabelLookupP9subscriptig",
                    ReturnTypeSpec = new NamedTypeSpec("Swift.Int"),
                    IsStatic = false,
                    IndexParameters = new List<ArgumentDecl>
                    {
                        new ArgumentDecl
                        {
                            Name = "_",
                            PrivateName = string.Empty,
                            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                            IsInOut = false,
                            IsGeneric = false,
                            ParentDecl = null,
                            ModuleDecl = moduleDecl
                        },
                        new ArgumentDecl
                        {
                            Name = "_",
                            PrivateName = string.Empty,
                            SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                            IsInOut = false,
                            IsGeneric = false,
                            ParentDecl = null,
                            ModuleDecl = moduleDecl
                        }
                    },
                    Accessors = new List<AccessorDecl>
                    {
                        new GetAccessorDecl { Method = CreateMethodDecl("subscript_get", moduleDecl) }
                    },
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        Assert.Contains("this[long value, string value2]", csOutput);
    }

    #endregion

    #region A6 — Projected C# Signature Dedup Tests

    [Fact]
    public void ProtocolHandler_DuplicateAfterAnyTypeFallback_SecondSkipped()
    {
        // Two methods with different unknown types that both collapse to AnyType
        // produce duplicate C# signatures — second should be skipped.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = new ProtocolDecl
        {
            Name = "Converter",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Converter"),
            MangledName = "$s10TestModule9ConverterP",
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
                CreateMethodDeclWithReturn("convert", new NamedTypeSpec("UnknownModule.Foo"), moduleDecl),
                CreateMethodDeclWithReturn("convert", new NamedTypeSpec("UnknownModule.Bar"), moduleDecl)
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        // Only one Convert() should appear on the interface (second is projected-duplicate)
        var interfacePart = csOutput.Substring(0, csOutput.IndexOf("class ConverterProxy"));
        Assert.Equal(1, EmitterTestHelpers.CountOccurrences(interfacePart, "Convert()"));
    }

    [Fact]
    public void ProtocolHandler_DistinctMethods_BothEmitted()
    {
        // Two methods with different resolvable return types should both be emitted.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = new ProtocolDecl
        {
            Name = "Calculator",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Calculator"),
            MangledName = "$s10TestModule10CalculatorP",
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
                CreateMethodDecl("reset", moduleDecl),
                CreateMethodDeclWithReturn("result", new NamedTypeSpec("Swift.Int"), moduleDecl)
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        var interfacePart = csOutput.Substring(0, csOutput.IndexOf("class CalculatorProxy"));
        Assert.Contains("void Reset()", interfacePart);
        Assert.Contains("long GetResult()", interfacePart);
    }

    [Fact]
    public void InterfaceImpl_ProjectedCollision_PreservesMethodIndex()
    {
        // Protocol with 3 methods where method 2 is AnyType-duplicate of method 1 (same primary key).
        // Method 3 (cleanup) should still get vtable index 1 (not 0), preserving sequential alignment.
        // The duplicate handle method is primary-skipped (both resolve to same AnyType param key).
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

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
                // Method 0: handle(UnknownModule.Foo) → handle(AnyType)
                CreateMethodDeclWithParam("handle", "UnknownModule.Foo", moduleDecl),
                // Method 1: handle(UnknownModule.Bar) → handle(AnyType) — primary dup of method 0
                CreateMethodDeclWithParam("handle", "UnknownModule.Bar", moduleDecl),
                // Method 2: cleanup() — distinct, gets next vtable index
                CreateMethodDecl("cleanup", moduleDecl)
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        // Vtable should have handle at index 0 and cleanup at index 1
        Assert.Contains("func_handle_0", csOutput);
        Assert.Contains("func_cleanup_1", csOutput);
        // Interface should only have one Handle (duplicate skipped)
        var interfacePart = csOutput.Substring(0, csOutput.IndexOf("class HandlerProxy"));
        Assert.Equal(1, EmitterTestHelpers.CountOccurrences(interfacePart, "Handle("));
    }

    #endregion

    #region Protocol Async CancellationToken Tests

    [Fact]
    public void Emit_ProtocolAsyncMethod_InterfaceHasCancellationTokenParam()
    {
        // Protocol interface async method must include CancellationToken to match WrapperEmitter emission.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var protocolDecl = new ProtocolDecl
        {
            Name = "KeyGenerator",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.KeyGenerator"),
            MangledName = "$s10TestModule12KeyGeneratorP",
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
                    Name = "generateKey",
                    MangledName = "$s10TestModule12KeyGeneratorP11generateKeySiyYaKF",
                    MethodType = MethodType.Instance,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        CreateArgument(string.Empty, new NamedTypeSpec("Swift.Int"), moduleDecl)
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

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        // Interface method must have CancellationToken
        Assert.Contains("global::System.Threading.CancellationToken cancellationToken = default", csOutput);
        // Should be on the interface line
        Assert.Contains("GenerateKeyAsync(global::System.Threading.CancellationToken cancellationToken = default)", csOutput);
    }

    [Fact]
    public void Emit_ProtocolSyncMethod_InterfaceDoesNotHaveCancellationTokenParam()
    {
        // Sync protocol methods should NOT have CancellationToken.
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
                new()
                {
                    Name = "increment",
                    MangledName = "$s10TestModule7CounterP9incrementyyF",
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
                }
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        Assert.DoesNotContain("CancellationToken", csOutput);
    }

    [Fact]
    public void Emit_ProtocolAsyncMethod_ProxyPassesCancellationTokenToImpl()
    {
        // Protocol proxy implementation must pass cancellationToken to _csharpImpl delegation.
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

        // Register the protocol type so the proxy class is emitted
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        var protoTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Fetcher");
        testModule.RegisterType(protoTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "IFetcher"),
            SwiftTypeName = protoTypeName,
            MetadataAccessor = "$s10TestModule7FetcherMa",
            Flags = TypeRecordFlags.None,
            Kind = TypeRecordKind.Protocol
        });
        typeDatabase.AddModuleDatabase(testModule);

        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = new ProtocolDecl
        {
            Name = "Fetcher",
            SwiftTypeName = protoTypeName,
            MangledName = "$s10TestModule7FetcherP",
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
                    Name = "fetch",
                    MangledName = "$s10TestModule7FetcherP5fetchSiyYaKF",
                    MethodType = MethodType.Instance,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        CreateArgument(string.Empty, new NamedTypeSpec("Swift.Int"), moduleDecl)
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

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        // Proxy class should have CancellationToken in method signature
        Assert.Contains("FetcherProxy", csOutput);
        // The proxy delegation should pass cancellationToken to _csharpImpl
        Assert.Contains("cancellationToken", csOutput);
    }

    #endregion

    #region Dictionary Generic Arg Preservation (typeTranslator fix)

    [Fact]
    public void Emit_InterfaceMethodWithClosureParam_EmittedInInterfaceWithProxyStub()
    {
        // Protocol methods with closure parameters are emitted in the interface so
        // concrete types can implement them. The proxy gets a NotSupportedException stub
        // because proxy receivers can't marshal closures (MarshalFromSwift<T> falls through to AnyType).
        var typeDatabase = CreateTypeDatabaseWithDictionary();
        var moduleDecl = CreateModuleDecl("TestModule");

        // Closure: (Optional<Dictionary<AnyHashable, Int>>, Optional<Bool>) -> Void
        var closureParams = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.Dictionary",
                new NamedTypeSpec("Swift.AnyHashable"),
                new NamedTypeSpec("Swift.Int"))),
            new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.Bool"))
        });
        var closureType = new ClosureTypeSpec(closureParams, TupleTypeSpec.Empty);

        var protocolDecl = new ProtocolDecl
        {
            Name = "DataFetcher",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.DataFetcher"),
            MangledName = "$s10TestModule11DataFetcherP",
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
                    Name = "fetchData",
                    MangledName = "$s10TestModule11DataFetcherP9fetchDatayyF",
                    MethodType = MethodType.Instance,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl),
                        CreateArgument("completion", closureType, moduleDecl)
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

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        Assert.Contains("public interface IDataFetcher", csOutput);
        // Closure methods are now emitted in the interface for concrete type implementation
        Assert.Contains("FetchData", csOutput);
    }

    [Fact]
    public void Emit_InterfaceMethodWithOptionalDictionary_PreservesGenericArgs()
    {
        // Bug fix: Protocol interface method with Optional<Dictionary<K,V>> in non-closure param
        // must emit IReadOnlyDictionary<K,V>? (with generic args), not bare IReadOnlyDictionary?
        // This tests the typeTranslator fix in ProtocolHandler.GetCSharpTypeName.
        // (The original closure-based test was superseded by the closure skip gate;
        // this test covers the same generic-arg preservation through a non-closure path.)
        var typeDatabase = CreateTypeDatabaseWithDictionary();
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = new ProtocolDecl
        {
            Name = "DataFetcher",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.DataFetcher"),
            MangledName = "$s10TestModule11DataFetcherP",
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
                    Name = "fetchData",
                    MangledName = "$s10TestModule11DataFetcherP9fetchDatayyF",
                    MethodType = MethodType.Instance,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl),
                        CreateArgument("data", new NamedTypeSpec("Swift.Optional",
                            new NamedTypeSpec("Swift.Dictionary",
                                new NamedTypeSpec("Swift.AnyHashable"),
                                new NamedTypeSpec("Swift.Int"))), moduleDecl)
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

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        // Interface must emit projected dictionary type with generic args
        Assert.Contains("IReadOnlyDictionary<", csOutput);
        // Must NOT have bare type without generic args
        Assert.DoesNotContain("IReadOnlyDictionary?", csOutput.Replace("IReadOnlyDictionary<", ""));
    }

    private static TypeDatabase CreateTypeDatabaseWithDictionary()
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
            SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftOptional"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Dictionary"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftDictionary"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Dictionary"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.AnyHashable"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftAnyHashable"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.AnyHashable"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    #endregion

    #region FixupProtocolInheritedRequirements Tests

    [Fact]
    public void Fixup_ChildBeforeParent_EmptyParent_ChildRemainsZero()
    {
        // Scenario: child protocol (Taggable) emitted before parent (BaseMarker).
        // Both have 0 direct members. After fixup, child should have EmittedMemberCount=0.
        var typeDatabase = CreateTypeDatabaseWithProtocolRecords(
            ("TestModule.BaseMarker", "IBaseMarker"),
            ("TestModule.Taggable", "ITaggable"));
        var moduleDecl = CreateModuleDecl("TestModule");

        var parentProtocol = new ProtocolDecl
        {
            Name = "BaseMarker",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.BaseMarker"),
            MangledName = "$s10TestModule10BaseMarkerP",
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

        var childProtocol = new ProtocolDecl
        {
            Name = "Taggable",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Taggable"),
            MangledName = "$s10TestModule8TaggableP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec> { new NamedTypeSpec("TestModule.BaseMarker") },
            IsClassBound = false,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        // Emit child FIRST, then parent — the order-dependent scenario
        EmitProtocol(childProtocol, typeDatabase);
        EmitProtocol(parentProtocol, typeDatabase);

        // Put both in moduleDecl.Types for the fixup
        moduleDecl.Types.Add(childProtocol);
        moduleDecl.Types.Add(parentProtocol);

        // Run fixup
        ProtocolHandler.FixupProtocolInheritedRequirements(moduleDecl, typeDatabase);

        // Verify: child should have EmittedMemberCount=0 (empty parent, 0 direct)
        Assert.True(typeDatabase.TryGetTypeRecord(childProtocol.SwiftTypeName, out var childRecord));
        Assert.Equal(0, childRecord.EmittedMemberCount);

        // Parent should also be 0
        Assert.True(typeDatabase.TryGetTypeRecord(parentProtocol.SwiftTypeName, out var parentRecord));
        Assert.Equal(0, parentRecord.EmittedMemberCount);
    }

    [Fact]
    public void Fixup_ChildBeforeParent_NonEmptyParent_ChildGetsInherited()
    {
        // Scenario: child protocol (StrictTaggable) emitted before parent (Describable).
        // Parent has 1 direct member. After fixup, child should have EmittedMemberCount=1.
        var typeDatabase = CreateTypeDatabaseWithProtocolRecords(
            ("TestModule.Describable", "IDescribable"),
            ("TestModule.StrictTaggable", "IStrictTaggable"));
        var moduleDecl = CreateModuleDecl("TestModule");

        var parentProtocol = new ProtocolDecl
        {
            Name = "Describable",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Describable"),
            MangledName = "$s10TestModule11DescribableP",
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
                    Name = "description",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                    HasStorage = false,
                    IsStatic = false,
                    Accessors = new AccessorDecl[]
                    {
                        new GetAccessorDecl
                        {
                            Method = new MethodDecl
                            {
                                Name = "get_description",
                                MangledName = "$s10TestModule11DescribableP11descriptionSSvg",
                                MethodType = MethodType.Instance,
                                IsConstructor = false,
                                CSSignature = new List<ArgumentDecl>
                                {
                                    CreateArgument(string.Empty, new NamedTypeSpec("Swift.String"), moduleDecl)
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

        var childProtocol = new ProtocolDecl
        {
            Name = "StrictTaggable",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.StrictTaggable"),
            MangledName = "$s10TestModule14StrictTaggableP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec> { new NamedTypeSpec("TestModule.Describable") },
            IsClassBound = false,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        // Emit child FIRST, then parent
        EmitProtocol(childProtocol, typeDatabase);
        EmitProtocol(parentProtocol, typeDatabase);

        moduleDecl.Types.Add(childProtocol);
        moduleDecl.Types.Add(parentProtocol);

        // Run fixup
        ProtocolHandler.FixupProtocolInheritedRequirements(moduleDecl, typeDatabase);

        // Parent has 1 direct member (description property)
        Assert.True(typeDatabase.TryGetTypeRecord(parentProtocol.SwiftTypeName, out var parentRecord));
        Assert.Equal(1, parentRecord.EmittedMemberCount);

        // Child: 0 direct + 1 inherited with members → total > 0
        Assert.True(typeDatabase.TryGetTypeRecord(childProtocol.SwiftTypeName, out var childRecord));
        Assert.True(childRecord.EmittedMemberCount > 0);
    }

    [Fact]
    public void Fixup_TransitiveInheritance_ChildBeforeParentBeforeGrandparent_Propagates()
    {
        // Scenario: Child → Parent → Grandparent (non-empty).
        // Emitted in order: Child, Parent, Grandparent.
        // After fixup, Parent.EmittedMemberCount > 0 (inherits Grandparent's member),
        // and Child.EmittedMemberCount > 0 (transitively inherits via Parent).
        var typeDatabase = CreateTypeDatabaseWithProtocolRecords(
            ("TestModule.Grandparent", "IGrandparent"),
            ("TestModule.Parent", "IParent"),
            ("TestModule.Child", "IChild"));
        var moduleDecl = CreateModuleDecl("TestModule");

        var grandparentProtocol = new ProtocolDecl
        {
            Name = "Grandparent",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Grandparent"),
            MangledName = "$s10TestModule11GrandparentP",
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
                    Name = "id",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    HasStorage = false,
                    IsStatic = false,
                    Accessors = new AccessorDecl[]
                    {
                        new GetAccessorDecl
                        {
                            Method = new MethodDecl
                            {
                                Name = "get_id",
                                MangledName = "$s10TestModule11GrandparentP2idSivg",
                                MethodType = MethodType.Instance,
                                IsConstructor = false,
                                CSSignature = new List<ArgumentDecl>
                                {
                                    CreateArgument(string.Empty, new NamedTypeSpec("Swift.Int"), moduleDecl)
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

        var parentProtocol = new ProtocolDecl
        {
            Name = "Parent",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Parent"),
            MangledName = "$s10TestModule6ParentP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec> { new NamedTypeSpec("TestModule.Grandparent") },
            IsClassBound = false,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var childProtocol = new ProtocolDecl
        {
            Name = "Child",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Child"),
            MangledName = "$s10TestModule5ChildP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec> { new NamedTypeSpec("TestModule.Parent") },
            IsClassBound = false,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        // Emit in worst-case order: child, then parent, then grandparent
        EmitProtocol(childProtocol, typeDatabase);
        EmitProtocol(parentProtocol, typeDatabase);
        EmitProtocol(grandparentProtocol, typeDatabase);

        moduleDecl.Types.Add(childProtocol);
        moduleDecl.Types.Add(parentProtocol);
        moduleDecl.Types.Add(grandparentProtocol);

        // Run fixup — must iterate to fixed point for transitive propagation
        ProtocolHandler.FixupProtocolInheritedRequirements(moduleDecl, typeDatabase);

        // Grandparent: 1 direct member
        Assert.True(typeDatabase.TryGetTypeRecord(grandparentProtocol.SwiftTypeName, out var gpRecord));
        Assert.Equal(1, gpRecord.EmittedMemberCount);

        // Parent: 0 direct + 1 inherited (Grandparent has members) → > 0
        Assert.True(typeDatabase.TryGetTypeRecord(parentProtocol.SwiftTypeName, out var parentRecord));
        Assert.True(parentRecord.EmittedMemberCount > 0);

        // Child: 0 direct + 1 inherited (Parent now has members after fixup) → > 0
        Assert.True(typeDatabase.TryGetTypeRecord(childProtocol.SwiftTypeName, out var childRecord));
        Assert.True(childRecord.EmittedMemberCount > 0);
    }

    [Fact]
    public void Fixup_NestedProtocol_InheritsNonEmptyProtocol_GetsInherited()
    {
        // Scenario: protocol nested inside a struct inherits a non-empty top-level protocol.
        // The fixup must recurse into nested types to find it.
        var typeDatabase = CreateTypeDatabaseWithProtocolRecords(
            ("TestModule.Identifiable", "IIdentifiable"),
            ("TestModule.Outer.ChildProtocol", "IChildProtocol"));
        var moduleDecl = CreateModuleDecl("TestModule");

        var parentProtocol = new ProtocolDecl
        {
            Name = "Identifiable",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Identifiable"),
            MangledName = "$s10TestModule12IdentifiableP",
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
                    Name = "id",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    HasStorage = false,
                    IsStatic = false,
                    Accessors = new AccessorDecl[]
                    {
                        new GetAccessorDecl
                        {
                            Method = new MethodDecl
                            {
                                Name = "get_id",
                                MangledName = "$s10TestModule12IdentifiableP2idSivg",
                                MethodType = MethodType.Instance,
                                IsConstructor = false,
                                CSSignature = new List<ArgumentDecl>
                                {
                                    CreateArgument(string.Empty, new NamedTypeSpec("Swift.Int"), moduleDecl)
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

        var nestedProtocol = new ProtocolDecl
        {
            Name = "ChildProtocol",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Outer.ChildProtocol"),
            MangledName = "$s10TestModule5OuterV13ChildProtocolP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec> { new NamedTypeSpec("TestModule.Identifiable") },
            IsClassBound = false,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        // Emit nested child before parent
        EmitProtocol(nestedProtocol, typeDatabase);
        EmitProtocol(parentProtocol, typeDatabase);

        // Nest ChildProtocol inside a struct — NOT in moduleDecl.Types directly
        var outerStruct = new StructDecl
        {
            Name = "Outer",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Outer"),
            MangledName = "$s10TestModule5OuterVN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { nestedProtocol },
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = "$s10TestModule5OuterVMa"
        };
        moduleDecl.Types.Add(outerStruct);
        moduleDecl.Types.Add(parentProtocol);

        ProtocolHandler.FixupProtocolInheritedRequirements(moduleDecl, typeDatabase);

        // Parent: 1 direct member
        Assert.True(typeDatabase.TryGetTypeRecord(parentProtocol.SwiftTypeName, out var parentRecord));
        Assert.Equal(1, parentRecord.EmittedMemberCount);

        // Nested child: 0 direct + 1 inherited (Identifiable has members) → > 0
        Assert.True(typeDatabase.TryGetTypeRecord(nestedProtocol.SwiftTypeName, out var childRecord));
        Assert.True(childRecord.EmittedMemberCount > 0);
    }

    [Fact]
    public void Fixup_NestedProtocol_InheritsEmptyMarker_RemainsZero()
    {
        // Nested protocol inherits empty marker protocol → EmittedMemberCount stays 0.
        var typeDatabase = CreateTypeDatabaseWithProtocolRecords(
            ("TestModule.Marker", "IMarker"),
            ("TestModule.Container.Inner", "IInner"));
        var moduleDecl = CreateModuleDecl("TestModule");

        var markerProtocol = new ProtocolDecl
        {
            Name = "Marker",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Marker"),
            MangledName = "$s10TestModule6MarkerP",
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

        var nestedProtocol = new ProtocolDecl
        {
            Name = "Inner",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Container.Inner"),
            MangledName = "$s10TestModule9ContainerV5InnerP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec> { new NamedTypeSpec("TestModule.Marker") },
            IsClassBound = false,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        EmitProtocol(nestedProtocol, typeDatabase);
        EmitProtocol(markerProtocol, typeDatabase);

        var containerStruct = new StructDecl
        {
            Name = "Container",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Container"),
            MangledName = "$s10TestModule9ContainerVN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { nestedProtocol },
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = "$s10TestModule9ContainerVMa"
        };
        moduleDecl.Types.Add(containerStruct);
        moduleDecl.Types.Add(markerProtocol);

        ProtocolHandler.FixupProtocolInheritedRequirements(moduleDecl, typeDatabase);

        Assert.True(typeDatabase.TryGetTypeRecord(markerProtocol.SwiftTypeName, out var markerRecord));
        Assert.Equal(0, markerRecord.EmittedMemberCount);

        Assert.True(typeDatabase.TryGetTypeRecord(nestedProtocol.SwiftTypeName, out var nestedRecord));
        Assert.Equal(0, nestedRecord.EmittedMemberCount);
    }

    /// <summary>
    /// Creates a TypeDatabase with protocol TypeRecords registered in the TestModule.
    /// Each tuple is (moduleQualifiedName, csharpInterfaceName).
    /// </summary>
    private static TypeDatabase CreateTypeDatabaseWithProtocolRecords(params (string swiftName, string csharpName)[] protocols)
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
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        foreach (var (swiftName, csharpName) in protocols)
        {
            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(swiftName);
            testModule.RegisterType(
                swiftTypeName,
                new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", csharpName),
                    SwiftTypeName = swiftTypeName,
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.None,
                    Kind = TypeRecordKind.Protocol
                });
        }
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    #endregion

    private static MethodDecl CreateMethodDeclWithParam(string name, string paramTypeName, ModuleDecl moduleDecl)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule{name}yyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl),
                CreateArgument("input", new NamedTypeSpec(paramTypeName), moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    private static MethodDecl CreateMethodDeclWithReturn(string name, TypeSpec returnTypeSpec, ModuleDecl moduleDecl)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule{name}yyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, returnTypeSpec, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    // --- SB0004: Empty interface with skipped members ---

    [Fact]
    public void Emit_ProtocolWithClosureProperty_EmitsInInterfaceNoSB0004()
    {
        // Protocol with a closure-typed property → emitted in interface (no longer SB0004)
        // Closure properties are now part of the interface for concrete type implementation.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var protocolDecl = new ProtocolDecl
        {
            Name = "CallbackProtocol",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.CallbackProtocol"),
            MangledName = "$s10TestModule16CallbackProtocolP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = false,
            HasSelfRequirement = false,
            Subscripts = new List<SubscriptDecl>(),
            Properties = new List<PropertyDecl>
            {
                new()
                {
                    Name = "callback",
                    SwiftTypeSpec = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty),
                    IsStatic = false,
                    HasStorage = false,
                    Accessors = new List<AccessorDecl>
                    {
                        new GetAccessorDecl
                        {
                            Method = CreateMethodDecl("callback_get", moduleDecl)
                        }
                    },
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            Methods = new List<MethodDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        // Closure property is now emitted in the interface — no SB0004
        Assert.DoesNotContain("DiagnosticId = \"SB0004\"", csOutput);
        Assert.Contains("public interface ICallbackProtocol", csOutput);
        Assert.Contains("Callback", csOutput);
    }

    [Fact]
    public void Emit_MarkerProtocolWithNoMembers_DoesNotEmitSB0004()
    {
        // Genuine marker protocol — zero declared members → no diagnostic
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var protocolDecl = new ProtocolDecl
        {
            Name = "MarkerProtocol",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MarkerProtocol"),
            MangledName = "$s10TestModule14MarkerProtocolP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = false,
            HasSelfRequirement = false,
            Subscripts = new List<SubscriptDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        Assert.DoesNotContain("DiagnosticId = \"SB0004\"", csOutput);
        Assert.Contains("public interface IMarkerProtocol", csOutput);
    }

    [Fact]
    public void Emit_ProtocolWithEmittedMembers_DoesNotEmitSB0004()
    {
        // Protocol with a successfully emitted member → no SB0004
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var protocolDecl = new ProtocolDecl
        {
            Name = "CountableProtocol",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.CountableProtocol"),
            MangledName = "$s10TestModule17CountableProtocolP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = false,
            HasSelfRequirement = false,
            Subscripts = new List<SubscriptDecl>(),
            Properties = new List<PropertyDecl>
            {
                new()
                {
                    Name = "count",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    IsStatic = false,
                    HasStorage = false,
                    Accessors = new List<AccessorDecl>
                    {
                        new GetAccessorDecl
                        {
                            Method = CreateMethodDecl("count_get", moduleDecl)
                        }
                    },
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            Methods = new List<MethodDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        Assert.DoesNotContain("DiagnosticId = \"SB0004\"", csOutput);
        Assert.Contains("public interface ICountableProtocol", csOutput);
    }

    [Fact]
    public void Emit_DerivedProtocolWithAllOwnMembersSkipped_DoesNotEmitSB0004()
    {
        // A derived protocol inheriting from a non-empty parent, but with all of its
        // own members skipped, should NOT get SB0004 — the interface still has
        // inherited members via the base interface.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var protocolDecl = new ProtocolDecl
        {
            Name = "DerivedProtocol",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.DerivedProtocol"),
            MangledName = "$s10TestModule15DerivedProtocolP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>
            {
                new NamedTypeSpec("TestModule.BaseProtocol")
            },
            IsClassBound = false,
            HasSelfRequirement = false,
            Subscripts = new List<SubscriptDecl>(),
            // A closure property that will be skipped
            Properties = new List<PropertyDecl>
            {
                new()
                {
                    Name = "onComplete",
                    SwiftTypeSpec = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty),
                    IsStatic = false,
                    HasStorage = false,
                    Accessors = new List<AccessorDecl>
                    {
                        new GetAccessorDecl
                        {
                            Method = CreateMethodDecl("onComplete_get", moduleDecl)
                        }
                    },
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            Methods = new List<MethodDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        Assert.DoesNotContain("DiagnosticId = \"SB0004\"", csOutput);
        // Should still have the inherited interface
        Assert.Contains(": IBaseProtocol", csOutput);
    }

    [Fact]
    public void Emit_ProxyClass_SuppressesSB0003AndSB0004()
    {
        // Verify that generated proxy classes include pragma warning disable
        // to prevent self-referential obsolete warnings
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var protocolDecl = new ProtocolDecl
        {
            Name = "SimpleProtocol",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.SimpleProtocol"),
            MangledName = "$s10TestModule14SimpleProtocolP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = false,
            HasSelfRequirement = false,
            Subscripts = new List<SubscriptDecl>(),
            Properties = new List<PropertyDecl>
            {
                new()
                {
                    Name = "count",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    IsStatic = false,
                    HasStorage = false,
                    Accessors = new List<AccessorDecl>
                    {
                        new GetAccessorDecl
                        {
                            Method = CreateMethodDecl("count_get", moduleDecl)
                        }
                    },
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            Methods = new List<MethodDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        Assert.Contains("#pragma warning disable SB0003, SB0004", csOutput);
        Assert.Contains("#pragma warning restore SB0003, SB0004", csOutput);
    }

    [Fact]
    public void Emit_ExistentialParamMethod_EmitsReceiverAndVtable()
    {
        // End-to-end ProtocolHandler test: a protocol with an existential-only method
        // should emit a receiver callback and vtable assignment (not NotSupportedException).
        // This tests the root-cause path in ProtocolHandler.cs:270 where existential-only
        // methods are NOT added to skippedMethodKeys.
        var typeDatabase = CreateTypeDatabaseWithProtocolRecords(
            ("TestModule.EventSource", "IEventSource"));
        var moduleDecl = CreateModuleDecl("TestModule");

        var existentialType = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.EventSource") });

        var protocolDecl = new ProtocolDecl
        {
            Name = "EventHandler",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.EventHandler"),
            MangledName = "$s10TestModule12EventHandlerP",
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
                    Name = "didReceive",
                    MangledName = "$s10TestModule12EventHandlerP10didReceiveyyAA0C6Source_pF",
                    MethodType = MethodType.Instance,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl),
                        CreateArgument("source", existentialType, moduleDecl)
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

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        // Interface should contain the method
        Assert.Contains("public interface IEventHandler", csOutput);
        Assert.Contains("DidReceive", csOutput);

        // Proxy should be emitted with receiver callback
        Assert.Contains("class EventHandlerProxy", csOutput);
        Assert.Contains("Receive_didReceive_0", csOutput);

        // Vtable should wire up the receiver function pointer
        Assert.Contains("&Receive_didReceive_0", csOutput);

        // Receiver should unmarshal ExistentialContainer and wrap in proxy
        Assert.Contains("ExistentialContainer1", csOutput);
        Assert.Contains("EventSourceProxy", csOutput);

        // Interface impl should dispatch to _csharpImpl when wrapping C# implementation
        Assert.Contains("_csharpImpl", csOutput);
        Assert.Contains("_csharpImpl.DidReceive", csOutput);

        // The method should NOT have a closure-skipped NotSupportedException stub
        // (The SB0003 NotSupportedException is expected for the Swift-container fallback path,
        // but the key assertion is that the receiver + vtable + _csharpImpl dispatch are present,
        // proving the method was NOT skipped from emission.)
        Assert.DoesNotContain("Closure parameters cannot be marshalled", csOutput);
    }

    [Fact]
    public void Emit_ClosureAndExistentialParamMethod_ClosureCausesSkip()
    {
        // When a method has BOTH a closure param AND an existential param,
        // the closure param causes the method to be skipped (NotSupportedException stub).
        // The existential param alone would be fine, but closure takes priority.
        var typeDatabase = CreateTypeDatabaseWithProtocolRecords(
            ("TestModule.EventSource", "IEventSource"));
        var moduleDecl = CreateModuleDecl("TestModule");

        var closureType = new ClosureTypeSpec
        {
            Arguments = TupleTypeSpec.Empty,
            ReturnType = TupleTypeSpec.Empty,
        };
        var existentialType = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.EventSource") });

        var protocolDecl = new ProtocolDecl
        {
            Name = "MixedHandler",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MixedHandler"),
            MangledName = "$s10TestModule12MixedHandlerP",
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
                    Name = "handleWith",
                    MangledName = "$s10TestModule12MixedHandlerP10handleWithyyAA0C6Source_pyXEtF",
                    MethodType = MethodType.Instance,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl),
                        CreateArgument("source", existentialType, moduleDecl),
                        CreateArgument("completion", closureType, moduleDecl)
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

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        // Interface should contain the method (it's InterfaceOnly, emitted for concrete types)
        Assert.Contains("public interface IMixedHandler", csOutput);
        Assert.Contains("HandleWith", csOutput);

        // Proxy should emit NotSupportedException stub (closure param forces skip)
        Assert.Contains("class MixedHandlerProxy", csOutput);
        Assert.Contains("Closure parameters cannot be marshalled", csOutput);

        // No receiver should be emitted for this method
        Assert.DoesNotContain("Receive_handleWith_0", csOutput);
    }

    private static (string csOutput, string swiftOutput) EmitProtocol(ProtocolDecl protocolDecl, TypeDatabase typeDatabase)
    {
        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        var handler = new ProtocolHandler(new NullLogger<ProtocolHandler>());
        var env = handler.Marshal(protocolDecl, typeDatabase);
        var conductor = new Conductor(new NullLoggerFactory());
        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        return (csOutput.ToString(), swiftOutput.ToString());
    }
}
