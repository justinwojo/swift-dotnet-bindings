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
                    IsSynthesizedAccessor = false
                }
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        Assert.Contains("public interface IReader<TElement>", csOutput);
        // The method's return type is an AssociatedTypeReferenceSpec, which is now
        // skipped by the associated type reference gate in MemberGateEvaluator.
        // The interface is still emitted but the method is absent.
        Assert.DoesNotContain("TElement GetNext();", csOutput);
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
                        IsSynthesizedAccessor = false
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
                    IsSynthesizedAccessor = false
                }
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        // C# interface inheritance is disabled (GetInheritedInterfaceList returns empty)
        Assert.Contains("public interface ICacheable", csOutput);
        Assert.DoesNotContain(": ISwiftHashable", csOutput);
        Assert.Contains("int Count { get; }", csOutput);
        Assert.Contains("Task<nint> FetchAsync(nint key, global::System.Threading.CancellationToken cancellationToken = default);", csOutput);
        Assert.Contains("public unsafe partial class CacheableProxy : ICacheable, ISwiftObject, IDisposable", csOutput);
    }

    [Fact]
    public void Emit_ProtocolWithMutatingAsyncNounGetter_KeepsBareAsyncName_NonMutatingGetsGetPrefix()
    {
        // A mutating async noun-only zero-arg getter (the AsyncIteratorProtocol.next() shape)
        // must NOT receive the `Get` prefix: the interface declares it bare (e.g. TokenAsync) so
        // its name agrees with the concrete conformer's (which derives the name through the same
        // mutating-aware rule). A NON-mutating sibling of identical shape still gets the prefix
        // (GetWeatherAsync) — proving the rule is mutating-aware, not an async blanket. Without
        // threading IsMutating into the protocol-emission name path, the interface would emit
        // GetTokenAsync while the conformer emits TokenAsync, diverging into a CS0535.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var mutatingGetter = new MethodDecl
        {
            Name = "token",
            MangledName = "$s10TestModule6TickerP5tokenSiyYaF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl> { CreateArgument(string.Empty, new NamedTypeSpec("Swift.Int"), moduleDecl) },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = true,
            IsMutating = true,
            IsSynthesizedAccessor = false
        };

        var nonMutatingGetter = new MethodDecl
        {
            Name = "weather",
            MangledName = "$s10TestModule6TickerP7weatherSiyYaF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl> { CreateArgument(string.Empty, new NamedTypeSpec("Swift.Int"), moduleDecl) },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = true,
            IsMutating = false,
            IsSynthesizedAccessor = false
        };

        var protocolDecl = new ProtocolDecl
        {
            Name = "Ticker",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Ticker"),
            MangledName = "$s10TestModule6TickerP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec> { new("Swift.AnyObject") },
            IsClassBound = true,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl> { mutatingGetter, nonMutatingGetter },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        // Mutating async noun-getter stays bare — interface name agrees with the conformer.
        Assert.Contains("TokenAsync(", csOutput);
        Assert.DoesNotContain("GetTokenAsync", csOutput);
        // Non-mutating async noun-getter still gets the Get prefix (the consistency rule).
        Assert.Contains("GetWeatherAsync(", csOutput);
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

    [Fact]
    public void Emit_ProtocolMethodWithInoutParam_EmitsRefModifierOnInterfaceAndProxy()
    {
        // Regression: an `inout` Swift parameter must carry the C# `ref` modifier on the
        // protocol INTERFACE declaration (and the proxy's public method), not just on the
        // concrete conformer. When the interface omitted `ref`, concrete classes that emit
        // `ref` failed to satisfy the interface contract → CS0535 (the GRDB failure).
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        // A non-frozen struct → ClassWithOpaquePayload, matching the real GRDB `Row`/statement shape.
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (SwiftTypeName.FromModuleQualifiedName("TestModule.Row"), new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Row"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Row"),
                MetadataAccessor = "$s10TestModule3RowVMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            })
        });

        var inoutArg = CreateArgument("row", new NamedTypeSpec("TestModule.Row"), moduleDecl);
        inoutArg.IsInOut = true;

        var protocolDecl = new ProtocolDecl
        {
            Name = "RowWriter",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.RowWriter"),
            MangledName = "$s10TestModule9RowWriterP",
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
                    Name = "writeRow",
                    MangledName = "$s10TestModule9RowWriterP8writeRowyAA0F0Vz_tF",
                    MethodType = MethodType.Instance,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl),
                        inoutArg
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

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        // Interface declaration must carry `ref` on the inout parameter.
        var interfacePart = csOutput.Substring(0, csOutput.IndexOf("class RowWriterProxy"));
        Assert.Contains("ref TestModule.Row row", interfacePart);
        // Proxy's public (reverse-dispatch) method must mirror the interface signature.
        var proxyPart = csOutput.Substring(csOutput.IndexOf("class RowWriterProxy"));
        Assert.Contains("ref TestModule.Row row", proxyPart);
    }

    [Fact]
    public void Emit_ProtocolWithLabelOnlyOverloads_DisambiguatesWithLabelDerivedNames()
    {
        // A delegate-callback protocol whose two requirements share a base name AND project to the same
        // C# parameter types but differ only by argument labels — the LCK/RoomPlan shape:
        //   func conversationManager(_:didActivate:)
        //   func conversationManager(_:didDeactivate:)
        // Before disambiguation the projected C# overloads collide once labels are erased and all but one
        // is dropped as DuplicateSignature. Both must now survive as DISTINCT members, named ObjC-selector
        // style from the labels.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        MethodDecl DelegateMethod(string secondLabel, string mangled) => new()
        {
            Name = "conversationManager",
            MangledName = mangled,
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl),            // return: Void
                CreateArgument(string.Empty, new NamedTypeSpec("Swift.Int"), moduleDecl), // _ manager: Int
                CreateArgument(secondLabel, new NamedTypeSpec("Swift.Int"), moduleDecl),  // <label> session: Int
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };

        var protocolDecl = new ProtocolDecl
        {
            Name = "ConversationDelegate",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ConversationDelegate"),
            MangledName = "$s10TestModule20ConversationDelegateP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = true,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>
            {
                DelegateMethod("didActivate", "$s10TestModule20ConversationDelegateP19conversationManager_11didActivateySi_SitF"),
                DelegateMethod("didDeactivate", "$s10TestModule20ConversationDelegateP19conversationManager_13didDeactivateySi_SitF"),
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        // Both label-distinct overloads must survive as DISTINCT C# members.
        Assert.Contains("ConversationManagerDidActivate", csOutput);
        Assert.Contains("ConversationManagerDidDeactivate", csOutput);

        // The interface (not merely the proxy) must declare BOTH.
        var interfacePart = csOutput.Substring(0, csOutput.IndexOf("Proxy"));
        Assert.Contains("ConversationManagerDidActivate", interfacePart);
        Assert.Contains("ConversationManagerDidDeactivate", interfacePart);
    }

    [Fact]
    public void Emit_ProtocolWithMixedRenamedAndTypeDistinctSiblings_FoldsLabelsUniformly()
    {
        // A base-name family that MIXES a label-only collision with a type-distinct sibling:
        //   func room(_:didAdd:)        // (Int, Int)      \_ collide on the erased projection,
        //   func room(_:didRemove:)     // (Int, Int)      /  renamed to RoomDidAdd / RoomDidRemove
        //   func room(_:didFinishWith:error:)  // (Int, Int, Int) — DISTINCT overload, never collided
        // Left alone the third emits bare as `Room(...)`, reading inconsistently next to its renamed
        // siblings. The family-fold rule folds its labels too, so the whole `room` family reads uniformly
        // as RoomDidFinishWithError. The fold only re-labels the C# member — it moves no slot.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        MethodDecl RoomPair(string secondLabel, string mangled) => new()
        {
            Name = "room",
            MangledName = mangled,
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl),           // return: Void
                CreateArgument(string.Empty, new NamedTypeSpec("Swift.Int"), moduleDecl), // _ area: Int
                CreateArgument(secondLabel, new NamedTypeSpec("Swift.Int"), moduleDecl),  // <label> value: Int
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };

        var typeDistinct = new MethodDecl
        {
            Name = "room",
            MangledName = "$s10TestModule17RoomActivityObserverP4room_12didFinishWith5errorySi_S2itF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl),            // return: Void
                CreateArgument(string.Empty, new NamedTypeSpec("Swift.Int"), moduleDecl),  // _ area: Int
                CreateArgument("didFinishWith", new NamedTypeSpec("Swift.Int"), moduleDecl), // didFinishWith value: Int
                CreateArgument("error", new NamedTypeSpec("Swift.Int"), moduleDecl),         // error code: Int
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };

        var protocolDecl = new ProtocolDecl
        {
            Name = "RoomActivityObserver",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.RoomActivityObserver"),
            MangledName = "$s10TestModule20RoomActivityObserverP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = true,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>
            {
                RoomPair("didAdd", "$s10TestModule20RoomActivityObserverP4room_5didAddySi_SitF"),
                RoomPair("didRemove", "$s10TestModule20RoomActivityObserverP4room_8didRemoveySi_SitF"),
                typeDistinct,
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        // The colliding pair is renamed (existing behavior) AND the type-distinct sibling is folded.
        Assert.Contains("RoomDidAdd", csOutput);
        Assert.Contains("RoomDidRemove", csOutput);
        Assert.Contains("RoomDidFinishWithError", csOutput);
        // The type-distinct sibling must NOT read as a bare `Room(...)` overload — it was folded.
        Assert.DoesNotContain(" Room(", csOutput);

        // The interface (not merely the proxy) must declare all three under the folded names.
        var interfacePart = csOutput.Substring(0, csOutput.IndexOf("Proxy"));
        Assert.Contains("RoomDidAdd", interfacePart);
        Assert.Contains("RoomDidRemove", interfacePart);
        Assert.Contains("RoomDidFinishWithError", interfacePart);
    }

    [Fact]
    public void Emit_ProtocolWithTypeDistinctSiblingsOnly_DoesNotFoldLabels()
    {
        // Trigger boundary: a `room` family whose members are type-distinct but DON'T collide —
        //   func room(_:didAdd:)               // (Int, Int)
        //   func room(_:didFinishWith:error:)  // (Int, Int, Int)
        // These project to two legal C# overloads (`Room(nint, nint)` / `Room(nint, nint, nint)`), so
        // NEITHER is renamed. With no already-disambiguated sibling to fold toward, the family-fold rule
        // must stay inert — both emit bare as `Room`, no label-derived names appear.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var twoArg = new MethodDecl
        {
            Name = "room",
            MangledName = "$s10TestModule12RoomReporterP4room_5didAddySi_SitF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl),
                CreateArgument(string.Empty, new NamedTypeSpec("Swift.Int"), moduleDecl),
                CreateArgument("didAdd", new NamedTypeSpec("Swift.Int"), moduleDecl),
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };

        var threeArg = new MethodDecl
        {
            Name = "room",
            MangledName = "$s10TestModule12RoomReporterP4room_12didFinishWith5errorySi_S2itF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl),
                CreateArgument(string.Empty, new NamedTypeSpec("Swift.Int"), moduleDecl),
                CreateArgument("didFinishWith", new NamedTypeSpec("Swift.Int"), moduleDecl),
                CreateArgument("error", new NamedTypeSpec("Swift.Int"), moduleDecl),
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };

        var protocolDecl = new ProtocolDecl
        {
            Name = "RoomReporter",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.RoomReporter"),
            MangledName = "$s10TestModule12RoomReporterP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = true,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl> { twoArg, threeArg },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        // No collision anywhere in the family → the fold must not fire → no label-derived names.
        Assert.DoesNotContain("RoomDidAdd", csOutput);
        Assert.DoesNotContain("RoomDidFinishWithError", csOutput);
        // Both survive as bare `Room` overloads (distinct arities are legal C# overloads).
        Assert.Contains("Room(", csOutput);
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
            IsSynthesizedAccessor = false
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
        Assert.Contains("TestModule.Container<nint> GetData();", csOutput);
        // Proxy should also have GetData
        Assert.Contains("public TestModule.Container<nint> GetData()", csOutput);
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
                    // A property declared in a protocol body is a requirement (parser sets
                    // protocolReq == true). The vtable layout predicate keeps the slot for a
                    // requirement even when the C# surface can't project it (AnyType generic arg),
                    // which is exactly what this test asserts below (func_batchedItems_get present).
                    IsProtocolRequirement = true,
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
                                IsSynthesizedAccessor = false
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
                        IsSynthesizedAccessor = false
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
            IsSynthesizedAccessor = false
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

    #region Async-Void Method Naming Regression

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
                    IsSynthesizedAccessor = false
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
                    IsSynthesizedAccessor = false
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

    #region Protocol Parameter Name Normalization

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
                    IsSynthesizedAccessor = false
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
                    IsSynthesizedAccessor = false
                }
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        Assert.Contains("void Interpolate(nint value, string value2);", csOutput);
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
        Assert.Contains("int?", csOutput);
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

    #region Subscript Parameter Normalization

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
        Assert.Contains("this[nint value]", csOutput);
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

        Assert.Contains("this[nint value, string value2]", csOutput);
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
        Assert.Contains("nint GetResult()", interfacePart);
    }

    [Fact]
    public void InterfaceImpl_ProjectedCollision_VtableMirrorsRawProducerSlots()
    {
        // Two existential overloads that COLLAPSE to one C# method must still each get their OWN raw
        // Swift vtable slot, and a following distinct method's slot index must advance PAST both —
        // mirroring the Swift producer (EveryProtocolEmitter.EmitProtocolVtableStruct), which keys
        // slot allocation on the RAW method key (name + labels + raw Swift type spec) via
        // EveryProtocolEmitter.GetMethodKey, NOT the projected/collapsing C# key.
        //
        // handle(UnknownModule.Foo) and handle(UnknownModule.Bar) have distinct raw type specs, so
        // they take slots 0 and 1 even though both project to AnyType → a single C# Handle(object).
        // cleanup() therefore lands at slot 2. The OLD model keyed the index on the collapsing
        // projected key, gave the duplicate no slot, and put cleanup at 1 — which UNDER-counted the
        // Swift struct and shifted every later reverse-dispatch read (the Finding 8 / WitnessIndexProto
        // corruption, where the trailing method landed at 1 instead of Swift's 2 → EntryPointNotFound /
        // mis-dispatch). Layout has always been raw-keyed; what changed is fillability — the two
        // overloads now earn distinct C# names on the resolver's type rung, so BOTH slots are filled
        // rather than the second being left null.
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

        // Vtable mirrors the raw producer: both handle overloads get a slot (0, 1), cleanup at 2.
        Assert.Contains("func_handle_0", csOutput);
        Assert.Contains("func_handle_1", csOutput);
        Assert.Contains("func_cleanup_2", csOutput);
        // cleanup must NOT collapse back to the projected-key index 1 (the stale, struct-shrinking model).
        Assert.DoesNotContain("func_cleanup_1", csOutput);
        // C# still cannot carry two `Handle(object)` overloads, so the resolver separates them on its
        // type rung and the interface declares one member per raw slot. The bare `Handle(` is gone
        // precisely because neither overload can claim it — which is what lets a conforming Swift class,
        // whose own body resolves this shape on the same rung, keep satisfying the interface.
        // What matters here is that both raw slots earn a member and neither claims the bare name; the
        // exact token composition is the disambiguator's own contract, pinned by its unit tests.
        var interfacePart = csOutput.Substring(0, csOutput.IndexOf("class HandlerProxy"));
        var handleMembers = System.Text.RegularExpressions.Regex.Matches(interfacePart, @"\bHandle\w*\(")
            .Select(m => m.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(2, handleMembers.Count);
        Assert.DoesNotContain("Handle(", handleMembers);
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
                    IsSynthesizedAccessor = false
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
                    IsSynthesizedAccessor = false
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
                CSharpTypeName = CSharpTypeName.NIntType,
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
                    IsSynthesizedAccessor = false
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
                    IsSynthesizedAccessor = false
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
                    IsSynthesizedAccessor = false
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
                CSharpTypeName = CSharpTypeName.NIntType,
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

        // Child: inherits parent's 1 member → EmittedMemberCount = 0 direct + 1 inherited = 1
        Assert.True(typeDatabase.TryGetTypeRecord(childProtocol.SwiftTypeName, out var childRecord));
        Assert.Equal(1, childRecord.EmittedMemberCount);
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

        // Parent: inherits Grandparent's 1 member → 0 direct + 1 inherited = 1
        Assert.True(typeDatabase.TryGetTypeRecord(parentProtocol.SwiftTypeName, out var parentRecord));
        Assert.Equal(1, parentRecord.EmittedMemberCount);

        // Child: transitively inherits via Parent → 0 direct + 1 inherited = 1
        Assert.True(typeDatabase.TryGetTypeRecord(childProtocol.SwiftTypeName, out var childRecord));
        Assert.Equal(1, childRecord.EmittedMemberCount);
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

        // Nested child: inherits parent's 1 member → 0 direct + 1 inherited = 1
        Assert.True(typeDatabase.TryGetTypeRecord(nestedProtocol.SwiftTypeName, out var childRecord));
        Assert.Equal(1, childRecord.EmittedMemberCount);
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
                CSharpTypeName = CSharpTypeName.NIntType,
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
            IsSynthesizedAccessor = false
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
            IsSynthesizedAccessor = false
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

        // C# interface inheritance is disabled, so no inherited interface.
        // The closure property () -> Void is supported, so the interface still has members
        // and SB0004 does NOT apply.
        Assert.DoesNotContain(": IBaseProtocol", csOutput);
        Assert.DoesNotContain("DiagnosticId = \"SB0004\"", csOutput);
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
                    IsSynthesizedAccessor = false
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
        Assert.DoesNotContain("closure parameters cannot be marshalled", csOutput);
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
                    IsSynthesizedAccessor = false
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
        Assert.Contains("closure parameters cannot be marshalled", csOutput);

        // Vtable-slot-collision fix: closure-skipped methods are omitted from both the
        // Swift-facing and local vtable structs (Swift's EveryProtocol uses a fatalError
        // stub that bypasses the vtable). Emitting a receiver here would have no slot to
        // assign into, so no Receive_*_N trampoline is generated.
        Assert.DoesNotContain("Receive_handleWith_0", csOutput);
        Assert.DoesNotContain("Func_handleWith_0", csOutput);
        Assert.DoesNotContain("func_handleWith_0", csOutput);
    }

    private static (string csOutput, string swiftOutput) EmitProtocol(ProtocolDecl protocolDecl, TypeDatabase typeDatabase)
    {
        return EmitProtocol(protocolDecl, typeDatabase, TypeHandlerContext.Empty);
    }

    private static (string csOutput, string swiftOutput) EmitProtocol(ProtocolDecl protocolDecl, TypeDatabase typeDatabase, TypeHandlerContext context)
    {
        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        var handler = new ProtocolHandler(new NullLogger<ProtocolHandler>());
        var env = handler.Marshal(protocolDecl, typeDatabase);
        var conductor = new Conductor(new NullLoggerFactory());
        handler.Emit(csWriter, swiftWriter, env, conductor, context);

        return (csOutput.ToString(), swiftOutput.ToString());
    }

    #region Extension Default DIM Emission

    [Fact]
    public void Emit_MethodWithDirectExtensionDefault_EmitsAsDIM()
    {
        // Protocol with a method that has a direct extension default → DIM with throw body
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

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
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>
            {
                CreateMethodDecl("configure", moduleDecl)
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        // Extension on Configurable provides configure() default
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
        var index = new ProtocolExtensionDefaultsIndex(extensionMethods, new List<ProtocolDecl>());
        var emissionContext = new ModuleEmissionContext();
        emissionContext.ExtensionDefaultsIndex = index;
        var context = TypeHandlerContext.Empty with { EmissionContext = emissionContext };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase, context);

        // Should emit as DIM with throw body, NOT as abstract interface member
        Assert.Contains("=> throw new global::System.NotSupportedException(", csOutput);
        Assert.DoesNotContain("void Configure();", csOutput);
    }

    [Fact]
    public void Emit_MethodDIMDefault_ThrowingBodyCarriesNoObsoletePoison()
    {
        // A protocol-extension DIM default is a PARTIAL failure: a conformer that overrides
        // the member (including the Swift extension default the generator injects onto every
        // conformer) succeeds at runtime through this same interface slot. C# binds `x.Member()`
        // against the interface member, so an [Obsolete] here would flag every legitimate
        // override-dispatch call site — the opposite of a suppressed-proxy always-throw read.
        // The throwing DIM body must therefore stay bare. This locks that decision.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

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
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>
            {
                CreateMethodDecl("configure", moduleDecl)
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

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
        var index = new ProtocolExtensionDefaultsIndex(extensionMethods, new List<ProtocolDecl>());
        var emissionContext = new ModuleEmissionContext();
        emissionContext.ExtensionDefaultsIndex = index;
        var context = TypeHandlerContext.Empty with { EmissionContext = emissionContext };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase, context);

        // The bare throw is present…
        Assert.Contains("=> throw new global::System.NotSupportedException(", csOutput);
        // …and it is NOT poisoned with any Obsolete/diagnostic attribute.
        Assert.DoesNotContain("[Obsolete", csOutput);
        Assert.DoesNotContain("DiagnosticId", csOutput);
        Assert.DoesNotContain("SB0007", csOutput);
    }

    [Fact]
    public void Emit_PropertyDIMDefault_ThrowingBodyCarriesNoObsoletePoison()
    {
        // Same partial-failure reasoning as the method DIM case, for a property default.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = new ProtocolDecl
        {
            Name = "Themed",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Themed"),
            MangledName = "$s10TestModule6ThemedP",
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
                    Name = "defaultColor",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    IsStatic = false,
                    HasStorage = false,
                    Accessors = new List<AccessorDecl>
                    {
                        new GetAccessorDecl
                        {
                            Method = new MethodDecl
                            {
                                Name = "defaultColor_Get",
                                MangledName = "$sGet",
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

        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["TestModule.Themed"] = new()
            {
                new ProtocolExtensionMethodDecl
                {
                    ProtocolQualifiedName = "TestModule.Themed",
                    MethodName = "defaultColor",
                    PrintedName = "defaultColor",
                    RawSignature = "var defaultColor: Int { get }",
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
        var index = new ProtocolExtensionDefaultsIndex(extensionMethods, new List<ProtocolDecl>());
        var emissionContext = new ModuleEmissionContext();
        emissionContext.ExtensionDefaultsIndex = index;
        var context = TypeHandlerContext.Empty with { EmissionContext = emissionContext };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase, context);

        Assert.Contains("=> throw new global::System.NotSupportedException(", csOutput);
        Assert.DoesNotContain("[Obsolete", csOutput);
        Assert.DoesNotContain("DiagnosticId", csOutput);
        Assert.DoesNotContain("SB0007", csOutput);
    }

    [Fact]
    public void Emit_StaticVirtualInterfaceMember_ThrowingBodyCarriesNoObsoletePoison()
    {
        // The static-protocol-member shape emits a `static virtual` interface default whose
        // throwing body avoids CS8920 (so the interface can be a type argument) and is
        // overridden by every concrete conformer. `T.Member` through a generic constraint
        // dispatches to the conformer's real static; C# binds the reference against the
        // interface member, so poisoning it would flag legitimate generic-dispatch sites.
        // The static default therefore stays bare too — the decision is uniform with the DIM case.
        var typeDatabase = CreateTypeDatabase();
        RegisterSwiftInt32(typeDatabase);
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = new ProtocolDecl
        {
            Name = "StaticProto",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.StaticProto"),
            MangledName = "$s10TestModule11StaticProtoP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = false,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>
            {
                CreateStaticPropertyDecl("parentCategory", new NamedTypeSpec("Swift.Int32"), moduleDecl, hasGetter: true, hasSetter: false)
            },
            Methods = new List<MethodDecl>
            {
                // An instance method so the proxy has own members and gets emitted too.
                CreateMethodDecl("doWork", moduleDecl)
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        // Isolate the INTERFACE block. The proxy class (which follows) emits its own always-throwing
        // static stub that IS correctly [Obsolete]-poisoned — a proxy can never dispatch a static
        // requirement, so that stub is a total failure, unlike the interface's overridable default.
        // The policy under test is exactly the interface `static virtual` default: it must stay bare.
        var proxyStart = csOutput.IndexOf("Proxy class that enables", System.StringComparison.Ordinal);
        Assert.True(proxyStart >= 0, "proxy class marker present");
        var interfaceBlock = csOutput.Substring(0, proxyStart);

        // The interface emits the static virtual default with its bare throw…
        Assert.Contains("static virtual", interfaceBlock);
        Assert.Contains("Static protocol members must be accessed on concrete types", interfaceBlock);
        // …and the interface member is NOT poisoned.
        Assert.DoesNotContain("[Obsolete", interfaceBlock);
        Assert.DoesNotContain("DiagnosticId", interfaceBlock);
        Assert.DoesNotContain("SB0007", interfaceBlock);
    }

    [Fact]
    public void Emit_StaticVirtualInterfaceMethod_ThrowingBodyCarriesNoObsoletePoison()
    {
        // The static-METHOD sibling of the static-property branch: a static protocol method also
        // lowers to a throwing `static virtual` interface default (same CS8920-avoidance +
        // overridden-by-every-conformer partial-failure story). It must stay bare too, so the
        // "no poison on all four throw branches" policy is genuinely locked, not just the property.
        var typeDatabase = CreateTypeDatabase();
        RegisterSwiftInt32(typeDatabase);
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = new ProtocolDecl
        {
            Name = "StaticMethodProto",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.StaticMethodProto"),
            MangledName = "$s10TestModule17StaticMethodProtoP",
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
                // An instance method so the proxy has own members and gets emitted too.
                CreateMethodDecl("doWork", moduleDecl),
                CreateStaticVoidMethodDecl("reset", moduleDecl)
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        // Isolate the INTERFACE block — the proxy's own static-method stub that follows IS correctly
        // [Obsolete]-poisoned (a proxy can never dispatch a static, a total failure), so a whole-output
        // assertion would be about that stub, not the overridable interface default under test.
        var proxyStart = csOutput.IndexOf("Proxy class that enables", System.StringComparison.Ordinal);
        Assert.True(proxyStart >= 0, "proxy class marker present");
        var interfaceBlock = csOutput.Substring(0, proxyStart);

        // The interface emits the static virtual method default with its bare throw…
        Assert.Contains("static virtual", interfaceBlock);
        Assert.Contains("Static protocol members must be called on concrete types", interfaceBlock);
        // …and the interface member is NOT poisoned.
        Assert.DoesNotContain("[Obsolete", interfaceBlock);
        Assert.DoesNotContain("DiagnosticId", interfaceBlock);
        Assert.DoesNotContain("SB0007", interfaceBlock);
    }

    [Fact]
    public void Emit_MethodWithoutExtensionDefault_EmitsAsAbstract()
    {
        // Protocol with a method that has NO extension default → normal abstract interface member
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = new ProtocolDecl
        {
            Name = "Worker",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Worker"),
            MangledName = "$s10TestModule6WorkerP",
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
                CreateMethodDecl("doWork", moduleDecl)
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        // Empty extension index — no defaults
        var index = new ProtocolExtensionDefaultsIndex(
            new Dictionary<string, List<ProtocolExtensionMethodDecl>>(), new List<ProtocolDecl>());
        var emissionContext = new ModuleEmissionContext();
        emissionContext.ExtensionDefaultsIndex = index;
        var context = TypeHandlerContext.Empty with { EmissionContext = emissionContext };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase, context);

        // Should emit as abstract interface member with semicolon
        Assert.Contains("void DoWork();", csOutput);
        // The interface method should NOT have the extension default throw pattern
        Assert.DoesNotContain("This method uses a Swift protocol extension default", csOutput);
    }

    [Fact]
    public void Emit_MethodWithSubProtocolDefault_EmitsAsDIM()
    {
        // Parent protocol's method has a default from a sub-protocol →
        // parent interface MUST get a DIM when interface inheritance is enabled.
        // Without this, conforming types get CS0535 for inherited requirements
        // that are satisfied by the sub-protocol's extension default in Swift.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        // Parent protocol: AnyWorker requires process()
        var parentProtocol = new ProtocolDecl
        {
            Name = "AnyWorker",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.AnyWorker"),
            MangledName = "$s10TestModule9AnyWorkerP",
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
                CreateMethodDecl("process", moduleDecl)
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        // Sub-protocol Worker inherits AnyWorker
        var childProtocol = new ProtocolDecl
        {
            Name = "Worker",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Worker"),
            MangledName = "$s10TestModule6WorkerP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec> { new NamedTypeSpec("TestModule.AnyWorker") },
            IsClassBound = false,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        // Extension on Worker (sub-protocol) provides process() default
        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["TestModule.Worker"] = new()
            {
                new ProtocolExtensionMethodDecl
                {
                    ProtocolQualifiedName = "TestModule.Worker",
                    MethodName = "process",
                    PrintedName = "process()",
                    RawSignature = "func process()",
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
        var index = new ProtocolExtensionDefaultsIndex(extensionMethods,
            new List<ProtocolDecl> { parentProtocol, childProtocol });
        var emissionContext = new ModuleEmissionContext();
        emissionContext.ExtensionDefaultsIndex = index;
        var context = TypeHandlerContext.Empty with { EmissionContext = emissionContext };

        // Emit the PARENT protocol (AnyWorker) — sub-protocol default should produce DIM
        var (csOutput, _) = EmitProtocol(parentProtocol, typeDatabase, context);

        // Parent's process() should be emitted as a DIM (throw body) because the sub-protocol
        // Worker provides an extension default. This prevents CS0535 for types that conform
        // to AnyWorker via ISQLSpecificExpressible-style transitive interface inheritance.
        Assert.Contains("This method uses a Swift protocol extension default", csOutput);
    }

    [Fact]
    public void Emit_PropertyWithDirectExtensionDefault_EmitsAsDIM()
    {
        // Protocol with a property that has a direct extension default → DIM with throw body
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = new ProtocolDecl
        {
            Name = "Themed",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Themed"),
            MangledName = "$s10TestModule6ThemedP",
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
                    Name = "defaultColor",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    IsStatic = false,
                    HasStorage = false,
                    Accessors = new List<AccessorDecl>
                    {
                        new GetAccessorDecl
                        {
                            Method = new MethodDecl
                            {
                                Name = "defaultColor_Get",
                                MangledName = "$sGet",
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

        // Extension on Themed provides defaultColor default
        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["TestModule.Themed"] = new()
            {
                new ProtocolExtensionMethodDecl
                {
                    ProtocolQualifiedName = "TestModule.Themed",
                    MethodName = "defaultColor",
                    PrintedName = "defaultColor",
                    RawSignature = "var defaultColor: Int { get }",
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
        var index = new ProtocolExtensionDefaultsIndex(extensionMethods, new List<ProtocolDecl>());
        var emissionContext = new ModuleEmissionContext();
        emissionContext.ExtensionDefaultsIndex = index;
        var context = TypeHandlerContext.Empty with { EmissionContext = emissionContext };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase, context);

        // Should emit property with DIM throw body, NOT abstract { get; }
        Assert.Contains("=> throw new global::System.NotSupportedException(", csOutput);
        Assert.DoesNotContain("{ get; }", csOutput);
    }

    [Fact]
    public void Emit_GetSetPropertyWithGetterOnlyDefault_DoesNotEmitDIM()
    {
        // Protocol requires { get set } but extension default is getter-only → should stay abstract
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

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
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>
            {
                new()
                {
                    Name = "setting",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    IsStatic = false,
                    HasStorage = false,
                    Accessors = new List<AccessorDecl>
                    {
                        new GetAccessorDecl
                        {
                            Method = new MethodDecl
                            {
                                Name = "setting_Get",
                                MangledName = "$sGet",
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
                                IsSynthesizedAccessor = false
                            }
                        },
                        new SetAccessorDecl
                        {
                            Method = new MethodDecl
                            {
                                Name = "setting_Set",
                                MangledName = "$sSet",
                                MethodType = MethodType.Instance,
                                IsConstructor = false,
                                CSSignature = new List<ArgumentDecl>
                                {
                                    CreateArgument("newValue", new NamedTypeSpec("Swift.Int"), moduleDecl)
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

        // Extension on Configurable provides getter-only default for "setting"
        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["TestModule.Configurable"] = new()
            {
                new ProtocolExtensionMethodDecl
                {
                    ProtocolQualifiedName = "TestModule.Configurable",
                    MethodName = "setting",
                    PrintedName = "setting",
                    RawSignature = "var setting: Int { get }",
                    ReturnsSelf = false,
                    IsMainActorIsolated = false,
                    IsStatic = false,
                    IsProperty = true,
                    HasSetter = false, // getter-only default
                    IsDeprecated = false,
                    IsMutating = false,
                    WhereConstraints = new List<string>()
                }
            }
        };
        var index = new ProtocolExtensionDefaultsIndex(extensionMethods, new List<ProtocolDecl>());
        var emissionContext = new ModuleEmissionContext();
        emissionContext.ExtensionDefaultsIndex = index;
        var context = TypeHandlerContext.Empty with { EmissionContext = emissionContext };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase, context);

        // { get set } requirement with getter-only default should stay abstract, NOT become DIM
        Assert.Contains("{ get; set; }", csOutput);
        Assert.DoesNotContain("This property uses a Swift protocol extension default", csOutput);
    }

    [Fact]
    public void Emit_GetSetPropertyWithGetSetDefault_EmitsAsDIM()
    {
        // Protocol requires { get set } and extension default provides { get set } → should DIM
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

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
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>
            {
                new()
                {
                    Name = "setting",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    IsStatic = false,
                    HasStorage = false,
                    Accessors = new List<AccessorDecl>
                    {
                        new GetAccessorDecl
                        {
                            Method = new MethodDecl
                            {
                                Name = "setting_Get",
                                MangledName = "$sGet",
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
                                IsSynthesizedAccessor = false
                            }
                        },
                        new SetAccessorDecl
                        {
                            Method = new MethodDecl
                            {
                                Name = "setting_Set",
                                MangledName = "$sSet",
                                MethodType = MethodType.Instance,
                                IsConstructor = false,
                                CSSignature = new List<ArgumentDecl>
                                {
                                    CreateArgument("newValue", new NamedTypeSpec("Swift.Int"), moduleDecl)
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

        // Extension on Configurable provides { get set } default for "setting"
        var extensionMethods = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["TestModule.Configurable"] = new()
            {
                new ProtocolExtensionMethodDecl
                {
                    ProtocolQualifiedName = "TestModule.Configurable",
                    MethodName = "setting",
                    PrintedName = "setting",
                    RawSignature = "var setting: Int { get set }",
                    ReturnsSelf = false,
                    IsMainActorIsolated = false,
                    IsStatic = false,
                    IsProperty = true,
                    HasSetter = true, // { get set } default
                    IsDeprecated = false,
                    IsMutating = false,
                    WhereConstraints = new List<string>()
                }
            }
        };
        var index = new ProtocolExtensionDefaultsIndex(extensionMethods, new List<ProtocolDecl>());
        var emissionContext = new ModuleEmissionContext();
        emissionContext.ExtensionDefaultsIndex = index;
        var context = TypeHandlerContext.Empty with { EmissionContext = emissionContext };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase, context);

        // { get set } requirement with { get set } default → should DIM with both accessors throwing
        Assert.Contains("get => throw new global::System.NotSupportedException(", csOutput);
        Assert.Contains("set => throw new global::System.NotSupportedException(", csOutput);
        Assert.DoesNotContain("{ get; set; }", csOutput);
    }

    #endregion

    #region Internal Protocol Suppression

    [Fact]
    public void Emit_InternalProtocol_InterfaceAndProxyStillEmitted()
    {
        // Internal protocols still emit their interface and proxy class.
        // The interface is needed because public types may conform to internal protocols.
        // The proxy is needed because marshalling code references it (e.g., `new BoxProxy(existential)`).
        // The EveryProtocol conformance is skipped for internal protocols (ModuleHandler),
        // so the proxy's vtable/witness table P/Invokes will fail at runtime
        // (EntryPointNotFoundException), but the C# compilation must pass.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var protocolDecl = new ProtocolDecl
        {
            Name = "Box",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Box"),
            MangledName = "$s10TestModule3BoxP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = false,
            HasSelfRequirement = false,
            IsModuleInternal = true,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        // Interface IS emitted (public types may conform to internal protocols)
        Assert.Contains("public interface IBox", csOutput);
        // Proxy IS also emitted (needed by marshalling code like `new BoxProxy(existential)`)
        Assert.Contains("class BoxProxy", csOutput);
    }

    [Fact]
    public void Emit_PublicProtocol_NotSuppressed()
    {
        // Public protocols should emit normally.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var protocolDecl = new ProtocolDecl
        {
            Name = "Loadable",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Loadable"),
            MangledName = "$s10TestModule8LoadableP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = false,
            HasSelfRequirement = false,
            IsModuleInternal = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        Assert.Contains("public interface ILoadable", csOutput);
    }

    #endregion

    #region Static Abstract Protocol Members

    [Fact]
    public void Emit_StaticProperty_EmitsStaticAbstractInInterface()
    {
        var typeDatabase = CreateTypeDatabase();
        RegisterSwiftInt32(typeDatabase);
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = new ProtocolDecl
        {
            Name = "HasDefault",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.HasDefault"),
            MangledName = "$s10TestModule10HasDefaultP",
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
            Methods = new List<MethodDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        Assert.Contains("static virtual int DefaultValue", csOutput);
        Assert.Contains("public interface IHasDefault", csOutput);
    }

    [Fact]
    public void Emit_StaticMethod_EmitsStaticAbstractInInterface()
    {
        var typeDatabase = CreateTypeDatabase();
        RegisterSwiftInt32(typeDatabase);
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = new ProtocolDecl
        {
            Name = "Creatable",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Creatable"),
            MangledName = "$s10TestModule9CreatableP",
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
                CreateStaticMethodDecl("create", new NamedTypeSpec("Swift.Int32"), moduleDecl)
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        Assert.Contains("static virtual int Create()", csOutput);
    }

    [Fact]
    public void Emit_MixedProtocol_HasBothInstanceAndStaticAbstractMembers()
    {
        var typeDatabase = CreateTypeDatabase();
        RegisterSwiftInt32(typeDatabase);
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = new ProtocolDecl
        {
            Name = "MixedProto",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MixedProto"),
            MangledName = "$s10TestModule10MixedProtoP",
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
                CreateMethodDecl("getValue", moduleDecl)
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        // Both should appear
        Assert.Contains("static virtual int DefaultValue", csOutput);
        Assert.Contains("void GetValue();", csOutput);
    }

    [Fact]
    public void Emit_StaticPropertyWithUnsupportedType_SkippedWithActualGateReason()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        // Static property with an unresolvable type — should be skipped with actual gate reason, not StaticProtocolMember
        var protocolDecl = new ProtocolDecl
        {
            Name = "BadStaticProto",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.BadStaticProto"),
            MangledName = "$s10TestModule14BadStaticProtoP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = false,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>
            {
                CreateStaticPropertyDecl("badProp", new NamedTypeSpec("SwiftUI.View"), moduleDecl, hasGetter: true, hasSetter: false)
            },
            Methods = new List<MethodDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        // The property should NOT appear (skipped by gate, not by StaticProtocolMember)
        Assert.DoesNotContain("static virtual", csOutput);
        Assert.DoesNotContain("BadProp", csOutput);
    }

    [Fact]
    public void Emit_ProtocolWithOnlyStaticMembers_NonEmptyInterface()
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
            Methods = new List<MethodDecl>
            {
                CreateStaticMethodDecl("make", new NamedTypeSpec("Swift.Int32"), moduleDecl)
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        // Interface should not have SB0004 (empty interface) diagnostic attribute
        Assert.DoesNotContain("DiagnosticId = \"SB0004\"", csOutput);
        Assert.Contains("static virtual int Value", csOutput);
        Assert.Contains("static virtual int Make()", csOutput);
    }

    [Fact]
    public void Emit_ProxyEmitsNotSupportedStubsForStaticAbstractMembers()
    {
        var typeDatabase = CreateTypeDatabase();
        RegisterSwiftInt32(typeDatabase);
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = new ProtocolDecl
        {
            Name = "StaticProto",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.StaticProto"),
            MangledName = "$s10TestModule11StaticProtoP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = false,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>
            {
                CreateStaticPropertyDecl("count", new NamedTypeSpec("Swift.Int32"), moduleDecl, hasGetter: true, hasSetter: false)
            },
            Methods = new List<MethodDecl>
            {
                // Include an instance method so the proxy has own members and gets emitted
                CreateMethodDecl("doWork", moduleDecl),
                CreateStaticVoidMethodDecl("reset", moduleDecl)
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        // Proxy should contain NotSupportedException stubs for static members
        Assert.Contains("public static int Count => throw new NotSupportedException", csOutput);
        Assert.Contains("public static void Reset() => throw new NotSupportedException", csOutput);
    }

    [Fact]
    public void Emit_ConstructorsStillSkippedAsStaticProtocolMember()
    {
        var typeDatabase = CreateTypeDatabase();
        RegisterSwiftInt32(typeDatabase);
        var moduleDecl = CreateModuleDecl("TestModule");

        var protocolDecl = new ProtocolDecl
        {
            Name = "InitProto",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.InitProto"),
            MangledName = "$s10TestModule9InitProtoP",
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
                    Name = "init",
                    MangledName = "$s10TestModule9InitProtoPxycfC",
                    MethodType = MethodType.Static,
                    IsConstructor = true,
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
                }
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        // Constructor should NOT appear in the interface
        Assert.DoesNotContain("static virtual", csOutput);
    }

    private static PropertyDecl CreateStaticPropertyDecl(string name, TypeSpec typeSpec, ModuleDecl moduleDecl, bool hasGetter, bool hasSetter)
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
                    MethodType = MethodType.Static,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        CreateArgument(string.Empty, typeSpec, moduleDecl)
                    },
                    GenericParameters = new List<GenericArgumentDecl>(),
                    ParentDecl = null,
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
                    MethodType = MethodType.Static,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        CreateArgument(string.Empty, TupleTypeSpec.Empty, moduleDecl),
                        CreateArgument("newValue", typeSpec, moduleDecl)
                    },
                    GenericParameters = new List<GenericArgumentDecl>(),
                    ParentDecl = null,
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
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
    }

    private static MethodDecl CreateStaticVoidMethodDecl(string name, ModuleDecl moduleDecl)
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

    private static MethodDecl CreateStaticMethodDecl(string name, TypeSpec returnTypeSpec, ModuleDecl moduleDecl)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule{name}yyFZ",
            MethodType = MethodType.Static,
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

    #region Protocol Inheritance

    [Fact]
    public void Emit_ProtocolWithInheritedProtocol_EmitsInterfaceInheritance()
    {
        // Drawable inherits Describable → IDrawable : IDescribable
        var typeDatabase = CreateTypeDatabaseWithProtocolRecords(
            ("TestModule.Describable", "IDescribable"),
            ("TestModule.Drawable", "IDrawable"));
        var moduleDecl = CreateModuleDecl("TestModule");

        var parentProtocol = new ProtocolDecl
        {
            Name = "Describable",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Describable"),
            MangledName = "$s10TestModule11DescribableP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl> { CreateMethodDecl("describe", moduleDecl) },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var childProtocol = new ProtocolDecl
        {
            Name = "Drawable",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Drawable"),
            MangledName = "$s10TestModule8DrawableP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec> { new("TestModule.Describable") },
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl> { CreateMethodDecl("draw", moduleDecl) },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        moduleDecl.Protocols.Add(parentProtocol);
        moduleDecl.Protocols.Add(childProtocol);

        var (csOutput, _) = EmitProtocol(childProtocol, typeDatabase);

        Assert.Contains("public interface IDrawable : IDescribable", csOutput);
    }

    [Fact]
    public void Emit_ProtocolWithMultipleInheritedProtocols_EmitsAllParents()
    {
        // Animatable inherits both Describable and Drawable
        var typeDatabase = CreateTypeDatabaseWithProtocolRecords(
            ("TestModule.Describable", "IDescribable"),
            ("TestModule.Drawable", "IDrawable"),
            ("TestModule.Animatable", "IAnimatable"));
        var moduleDecl = CreateModuleDecl("TestModule");

        var animatable = new ProtocolDecl
        {
            Name = "Animatable",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Animatable"),
            MangledName = "$s10TestModule10AnimatableP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>
            {
                new("TestModule.Describable"),
                new("TestModule.Drawable")
            },
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl> { CreateMethodDecl("animate", moduleDecl) },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        moduleDecl.Protocols.Add(animatable);

        var (csOutput, _) = EmitProtocol(animatable, typeDatabase);

        Assert.Contains("public interface IAnimatable : IDescribable, IDrawable", csOutput);
    }

    [Fact]
    public void Emit_ProtocolInheritingPATProtocol_SkipsInheritance()
    {
        // Protocols with associated types can't be inherited in C# (generic interface, unknown type args)
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        typeDatabase.AddModuleDatabase(swiftModule);
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.PatProto"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "IPatProto"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.PatProto"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.HasAssociatedTypes,
                Kind = TypeRecordKind.Protocol
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Child"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "IChild"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Child"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(testModule);
        var moduleDecl = CreateModuleDecl("TestModule");

        var child = new ProtocolDecl
        {
            Name = "Child",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Child"),
            MangledName = "$s10TestModule5ChildP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec> { new("TestModule.PatProto") },
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl> { CreateMethodDecl("doSomething", moduleDecl) },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        moduleDecl.Protocols.Add(child);

        var (csOutput, _) = EmitProtocol(child, typeDatabase);

        // PAT parent should be skipped — interface should NOT inherit from IPatProto
        Assert.DoesNotContain("IPatProto", csOutput);
        Assert.Contains("public interface IChild", csOutput);
    }

    [Fact]
    public void Emit_ProtocolInheritingUnknownProtocol_SkipsInheritance()
    {
        // If parent protocol is not in type database, skip it
        var typeDatabase = CreateTypeDatabaseWithProtocolRecords(
            ("TestModule.Child", "IChild"));
        var moduleDecl = CreateModuleDecl("TestModule");

        var child = new ProtocolDecl
        {
            Name = "Child",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Child"),
            MangledName = "$s10TestModule5ChildP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec> { new("SomeOtherModule.Unknown") },
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl> { CreateMethodDecl("doSomething", moduleDecl) },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        moduleDecl.Protocols.Add(child);

        var (csOutput, _) = EmitProtocol(child, typeDatabase);

        // Unknown parent should be skipped — no inheritance
        Assert.Contains("public interface IChild\n", csOutput);
        Assert.DoesNotContain("IChild :", csOutput);
    }

    #endregion

    #region @objc optional DIM Emission

    [Fact]
    public void Emit_ObjCOptionalVoidMethod_EmitsAsDIMWithEmptyBody()
    {
        // Protocol with one mandatory method and one `@objc optional` void method.
        // The optional method must be a DIM with `{ }` body so consumers can leave it
        // unimplemented; the mandatory method stays an interface requirement.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var optionalMethod = CreateMethodDecl("optionalCallback", moduleDecl);
        optionalMethod.IsObjCOptional = true;

        var protocolDecl = new ProtocolDecl
        {
            Name = "OptionalCallbacks",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.OptionalCallbacks"),
            MangledName = "$s10TestModule17OptionalCallbacksP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = true,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>
            {
                CreateMethodDecl("requiredCallback", moduleDecl),
                optionalMethod
            },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        // Mandatory method stays an interface requirement (no body).
        Assert.Contains("void RequiredCallback();", csOutput);
        // Optional method emits a DIM with empty `{ }` body — no consumer implementation needed.
        Assert.Contains("void OptionalCallback()", csOutput);
        Assert.DoesNotContain("void OptionalCallback();", csOutput);
        // The DIM body must NOT throw (that's the extension-default pattern, not optional).
        // Optional methods should silently no-op so legacy ObjC delegate semantics are preserved.
        var optionalIndex = csOutput.IndexOf("void OptionalCallback()");
        var nextSemicolon = csOutput.IndexOf(';', optionalIndex);
        var nextBrace = csOutput.IndexOf('}', optionalIndex);
        Assert.True(nextBrace > 0 && (nextSemicolon < 0 || nextBrace < nextSemicolon),
            "Optional void method body should be `{ }`, not throw NotSupportedException.");
    }

    [Fact]
    public void Emit_ObjCOptionalReturningMethod_EmitsDefaultExpressionBody()
    {
        // `@objc optional func progressTotal() -> Int` → DIM whose body is `=> default!;`.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var optionalMethod = new MethodDecl
        {
            Name = "progressTotal",
            MangledName = "$s10TestModule13progressTotalSiyF",
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
            IsObjCOptional = true,
            IsSynthesizedAccessor = false
        };

        var protocolDecl = new ProtocolDecl
        {
            Name = "ProgressReporter",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ProgressReporter"),
            MangledName = "$s10TestModule16ProgressReporterP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = true,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl> { optionalMethod },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        // Inspect emitted method declaration; allow NameProvider whatever name it derives.
        Assert.Matches(@"nint\s+\w+\(\)\s*\n?\s*=>\s+default!;", csOutput);
        Assert.DoesNotContain("ProgressTotal();", csOutput); // no plain interface requirement form
    }

    [Fact]
    public void Emit_ObjCOptionalGetterProperty_EmitsExpressionBodyDIM()
    {
        // `@objc optional var label: Int { get }` → `long Label => default!;` DIM.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var optionalProperty = new PropertyDecl
        {
            Name = "label",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsStatic = false,
            HasStorage = false,
            IsObjCOptional = true,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl
                {
                    Method = new MethodDecl
                    {
                        Name = "label_Get",
                        MangledName = "$s10TestModule5labelSivg",
                        MethodType = MethodType.Instance,
                        IsConstructor = false,
                        CSSignature = new List<ArgumentDecl> { CreateArgument(string.Empty, new NamedTypeSpec("Swift.Int"), moduleDecl) },
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
            Name = "Labelled",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Labelled"),
            MangledName = "$s10TestModule8LabelledP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = true,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl> { optionalProperty },
            Methods = new List<MethodDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        Assert.Contains("int Label => default!;", csOutput);
        Assert.DoesNotContain("long Label { get; }", csOutput);
    }

    [Fact]
    public void Emit_ObjCOptionalAsyncReturningMethod_EmitsTaskFromResultDIM()
    {
        // `@objc optional func fetchValue() async -> Int` lowers to `Task<long>` —
        // which MUST emit `=> Task.FromResult<long>(default!);` rather than `=> default!;`.
        // The naive form yields a null Task and any consumer that `await`s it NREs.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var optionalAsync = new MethodDecl
        {
            Name = "fetchValue",
            MangledName = "$s10TestModule10fetchValueSiyYaF",
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
            IsAsync = true,
            IsObjCOptional = true,
            IsSynthesizedAccessor = false
        };

        var protocolDecl = new ProtocolDecl
        {
            Name = "AsyncFetcher",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.AsyncFetcher"),
            MangledName = "$s10TestModule12AsyncFetcherP",
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            GenericSignature = null,
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            IsClassBound = true,
            HasSelfRequirement = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl> { optionalAsync },
            Subscripts = new List<SubscriptDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };

        var (csOutput, _) = EmitProtocol(protocolDecl, typeDatabase);

        // The DIM body must use Task.FromResult<long>(default!) — NOT `=> default!;`.
        Assert.Matches(@"Task<nint>\s+\w+\([^)]*\)\s*\n?\s*=>\s+global::System\.Threading\.Tasks\.Task\.FromResult<nint>\(default!\);", csOutput);
        // Negative: must NOT fall through to the bare `=> default!;` body for Task<T>.
        Assert.DoesNotMatch(@"Task<nint>\s+\w+\([^)]*\)\s*\n?\s*=>\s+default!;", csOutput);
    }

    #endregion
}
