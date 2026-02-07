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

        Assert.Contains("public interface ISwiftReader<TElement>", csOutput);
        Assert.Contains("TElement Next();", csOutput);
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

        Assert.Contains("public interface ISwiftComparableLike<TSelf> where TSelf : ISwiftComparableLike<TSelf>", csOutput);
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

        Assert.Contains("public interface ISwiftCacheable : ISwiftHashable", csOutput);
        Assert.Contains("System.Int64 Count { get; }", csOutput);
        Assert.Contains("Task<System.Int64> Fetch(System.Int64 key);", csOutput);
        Assert.Contains("public unsafe class CacheableProxy : ISwiftCacheable, ISwiftObject", csOutput);
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

        // Interface should contain GetData with valid Container<System.Int64> return
        Assert.Contains("TestModule.Container<System.Int64> GetData();", csOutput);
        // Proxy should also have GetData
        Assert.Contains("public TestModule.Container<System.Int64> GetData()", csOutput);
    }

    [Theory]
    [InlineData("BatchedCollection<Swift.AnyType>", true)]
    [InlineData("BatchedCollection<AnyType>", true)]
    [InlineData("Swift.AnyType", false)]
    [InlineData("AnyType", false)]
    [InlineData("Container<System.Int64>", false)]
    [InlineData("System.String", false)]
    [InlineData("Func<Swift.AnyType, System.Boolean>", true)]
    public void ContainsAnyTypeGenericArg_DetectsCorrectly(string typeName, bool expected)
    {
        Assert.Equal(expected, ProtocolHandler.ContainsAnyTypeGenericArg(typeName));
    }

    #endregion

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
