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
        Assert.Contains("Task<long> FetchAsync(long key, System.Threading.CancellationToken cancellationToken = default);", csOutput);
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
        Assert.Equal(expected, ProtocolHandler.ContainsAnyTypeGenericArg(typeName));
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
        Assert.Contains("Task FlushAsync(System.Threading.CancellationToken cancellationToken = default)", csOutput);
    }

    [Fact]
    public void Emit_AsyncValueMethod_GetsGetPrefix()
    {
        // Async method with non-void return → should get Get prefix for noun names
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

        // Non-void return + noun name → GetDataAsync (with CancellationToken)
        Assert.Contains("GetDataAsync(System.Threading.CancellationToken cancellationToken = default)", csOutput);
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
        // Regression: subscript with parameter named "value" would conflict
        // with C# indexer setter's implicit "value" parameter (CS0316).
        // GetCSharpParameterName sanitizes "value" to "_value".
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

        // Parameter "value" should be sanitized to "_value" (C# contextual keyword)
        Assert.Contains("this[long _value]", csOutput);
        Assert.DoesNotContain("this[long value]", csOutput);
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
        Assert.Contains("System.Threading.CancellationToken cancellationToken = default", csOutput);
        // Should be on the interface line
        Assert.Contains("GenerateKeyAsync(System.Threading.CancellationToken cancellationToken = default)", csOutput);
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
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "IFetcher"),
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

    private static (string csOutput, string swiftOutput) EmitProtocol(ProtocolDecl protocolDecl, TypeDatabase typeDatabase)
    {
        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        var handler = new ProtocolHandler(new NullLogger<ProtocolHandler>());
        var env = handler.Marshal(protocolDecl, typeDatabase);
        var conductor = new Conductor(new NullLoggerFactory());
        handler.Emit(csWriter, swiftWriter, env, conductor);

        return (csOutput.ToString(), swiftOutput.ToString());
    }
}
