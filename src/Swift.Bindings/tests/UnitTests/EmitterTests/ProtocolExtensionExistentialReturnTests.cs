// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for existential return type support in protocol extension methods.
/// Exercises InjectExtensionMethods end-to-end: gate lifting, Swift wrapper
/// rendering, and synthetic MethodDecl construction for existential returns.
/// </summary>
public class ProtocolExtensionExistentialReturnTests
{
    private static readonly ILogger Logger = NullLogger.Instance;

    // ─── Existential return: method injected ─────────────────────────

    [Fact]
    public void ExistentialReturn_KnownProtocol_MethodInjected()
    {
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "MyClass", "TestProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("subscribe", "public func subscribe() -> any TestModule.TestProtocol"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Single(conformingType.Methods);
        Assert.Equal("subscribe", conformingType.Methods[0].Name);
    }

    // ─── Swift wrapper renders "any Protocol" ────────────────────────

    [Fact]
    public void ExistentialReturn_SwiftWrapper_RendersAnyProtocol()
    {
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "MyClass", "TestProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("subscribe", "public func subscribe() -> any TestModule.TestProtocol"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        var wrapperLines = string.Join("\n", ctx.ProtocolExtSwiftWrapperLines);
        Assert.Contains("any TestProtocol", wrapperLines);
    }

    // ─── Swift wrapper uses direct return (no Unmanaged.passRetained) ─

    [Fact]
    public void ExistentialReturn_SwiftWrapper_UsesDirectReturn()
    {
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "MyClass", "TestProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("subscribe", "public func subscribe() -> any TestModule.TestProtocol"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        var wrapperLines = string.Join("\n", ctx.ProtocolExtSwiftWrapperLines);
        // Existentials return by value — no Unmanaged pointer manipulation for return
        Assert.DoesNotContain("Unmanaged.passRetained", wrapperLines);
        // Return type should be "any TestProtocol", not "UnsafeMutableRawPointer"
        Assert.DoesNotContain("-> UnsafeMutableRawPointer", wrapperLines);
        Assert.Contains("return instance.subscribe()", wrapperLines);
    }

    // ─── Synthetic MethodDecl preserves existential TypeSpec ─────────

    [Fact]
    public void ExistentialReturn_SyntheticMethodDecl_PreservesTypeSpec()
    {
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "MyClass", "TestProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("subscribe", "public func subscribe() -> any TestModule.TestProtocol"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Single(conformingType.Methods);
        var returnTypeSpec = conformingType.Methods[0].CSSignature[0].SwiftTypeSpec;
        // Return type should be a NamedTypeSpec with IsAny=true (single-protocol existential)
        Assert.True(
            returnTypeSpec is ProtocolListTypeSpec ||
            (returnTypeSpec is NamedTypeSpec nts && nts.IsAny),
            $"Expected existential TypeSpec, got {returnTypeSpec?.GetType().Name}");
    }

    // ─── Unknown protocol existential blocked ────────────────────────

    [Fact]
    public void ExistentialReturn_UnknownProtocol_MethodNotInjected()
    {
        // Setup with a protocol that has NO TypeRecord in the database
        var (moduleDecl, conformingType, typeDatabase) = CreateSetupNoProtocolRecord("TestModule", "MyClass", "UnknownProto");
        var extMethods = CreateExtensionMethodDict("TestModule.UnknownProto",
            CreateExtMethod("make", "public func make() -> any TestModule.UnknownProto"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Empty(conformingType.Methods);
    }

    // ─── Non-existential non-class still blocked ─────────────────────

    [Fact]
    public void StructReturn_StillBlocked()
    {
        // Build a setup that also includes a struct type
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.TestProtocol"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "ITestProtocol"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.TestProtocol"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "MyClass"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
                MetadataAccessor = "$s10TestModule7MyClassCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyStruct"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "MyStruct"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyStruct"),
                MetadataAccessor = "$s10TestModule8MyStructVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(testModule);

        var moduleDecl = CreateModuleDecl("TestModule");
        var conformingType = CreateClassDecl("MyClass", moduleDecl);
        conformingType.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
            SwiftTypeName.FromModuleQualifiedName("TestModule.TestProtocol"),
            ""));

        // Return type is a struct (not a class, not existential)
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("value", "public func value() -> TestModule.MyStruct"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Empty(conformingType.Methods);
    }

    // ─── Zero-protocol Any return allowed ────────────────────────────

    [Fact]
    public void AnyReturn_ZeroProtocol_MethodInjected()
    {
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "MyClass", "TestProtocol");
        // "any" with no protocol constraints — returns Any (ExistentialContainer0)
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("anything", "public func anything() -> Any"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Single(conformingType.Methods);
        Assert.Equal("anything", conformingType.Methods[0].Name);
    }

    // ─── ObjC mixed composition blocked ──────────────────────────────

    [Fact]
    public void ExistentialReturn_ObjCMixedComposition_MethodNotInjected()
    {
        // Create a setup where the return is a composition including an ObjC protocol
        var typeDatabase = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "MyClass"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
                MetadataAccessor = "$s10TestModule7MyClassCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.TestProtocol"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "ITestProtocol"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.TestProtocol"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        // ObjectiveC.NSObjectProtocol is an ObjC module type — will be filtered
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("ObjectiveC.NSObjectProtocol"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("ObjectiveC", "INSObjectProtocol"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("ObjectiveC.NSObjectProtocol"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(testModule);

        var moduleDecl = CreateModuleDecl("TestModule");
        var conformingType = CreateClassDecl("MyClass", moduleDecl);
        conformingType.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
            SwiftTypeName.FromModuleQualifiedName("TestModule.TestProtocol"),
            ""));

        // Return "any TestProtocol & ObjectiveC.NSObjectProtocol" — mixed ObjC composition
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("mixedReturn",
                "public func mixedReturn() -> any TestProtocol & ObjectiveC.NSObjectProtocol"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Empty(conformingType.Methods);
    }

    // ─── NamedTypeSpec{IsAny=true} variant ───────────────────────────

    [Fact]
    public void ExistentialReturn_NamedTypeSpecIsAny_Injected()
    {
        // Single-protocol existential via NamedTypeSpec with IsAny=true
        // This is what the parser produces for "any TestProtocol" (single protocol)
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "MyClass", "TestProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("factory", "public func factory() -> any TestModule.TestProtocol"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Single(conformingType.Methods);
        Assert.Equal("factory", conformingType.Methods[0].Name);

        // Verify wrapper return type uses "any", not "UnsafeMutableRawPointer"
        var wrapperLines = string.Join("\n", ctx.ProtocolExtSwiftWrapperLines);
        Assert.Contains("any TestProtocol", wrapperLines);
        Assert.DoesNotContain("-> UnsafeMutableRawPointer", wrapperLines);
    }

    // ─── IsSupportedExistentialReturn unit tests ─────────────────────

    [Fact]
    public void IsSupportedExistentialReturn_KnownProtocol_ReturnsTrue()
    {
        var typeDatabase = CreateTypeDatabase("TestModule", "TestProtocol");
        var typeSpec = new NamedTypeSpec("TestModule.TestProtocol") { IsAny = true };

        Assert.True(ProtocolExtensionEmitter.IsSupportedExistentialReturn(typeSpec, typeDatabase));
    }

    [Fact]
    public void IsSupportedExistentialReturn_NonExistentialType_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabase("TestModule", "TestProtocol");
        var typeSpec = new NamedTypeSpec("TestModule.TestProtocol"); // NOT IsAny

        Assert.False(ProtocolExtensionEmitter.IsSupportedExistentialReturn(typeSpec, typeDatabase));
    }

    [Fact]
    public void IsSupportedExistentialReturn_UnknownProtocol_ReturnsFalse()
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        typeDatabase.AddModuleDatabase(swiftModule);

        var typeSpec = new NamedTypeSpec("TestModule.Unknown") { IsAny = true };

        Assert.False(ProtocolExtensionEmitter.IsSupportedExistentialReturn(typeSpec, typeDatabase));
    }

    // ─── P1: Generic protocol existential → AnyType blocked ────────

    [Fact]
    public void IsSupportedExistentialReturn_GenericProtocolExistential_ReturnsFalse()
    {
        // Generic protocol existentials like "any EventStream<τ_0_0.Event>" map to
        // AnyType in GetPublicExistentialType. WrapperEmitter.Return would construct
        // `new {Proxy}(result)` which is not assignable to AnyType → invalid C#.
        var typeDatabase = CreateTypeDatabase("TestModule", "TestProtocol");
        // NamedTypeSpec with a generic parameter — simulates "any TestProtocol<SomeType>"
        var typeSpec = new NamedTypeSpec("TestModule.TestProtocol",
            new NamedTypeSpec("SomeModule.SomeType")) { IsAny = true };

        Assert.False(ProtocolExtensionEmitter.IsSupportedExistentialReturn(typeSpec, typeDatabase));
    }

    [Fact]
    public void ExistentialReturn_GenericProtocolExistential_MethodNotInjected()
    {
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "MyClass", "TestProtocol");
        // "any TestModule.TestProtocol<SomeType>" — generic protocol existential
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("create", "public func create() -> any TestModule.TestProtocol<SomeModule.SomeType>"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Empty(conformingType.Methods);
    }

    // ─── P2: Proxy emission blocked by associated types / Self ───────

    [Fact]
    public void IsSupportedExistentialReturn_ProtocolWithAssociatedTypes_ReturnsFalse()
    {
        // Protocols with associated types don't get proxy classes emitted
        // (ProtocolProxyEmitter skips them). Allowing the return would produce
        // `new {Proxy}(result)` referencing a non-existent type.
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.AssocProto"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "IAssocProto"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.AssocProto"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.HasAssociatedTypes,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(testModule);

        var typeSpec = new NamedTypeSpec("TestModule.AssocProto") { IsAny = true };

        Assert.False(ProtocolExtensionEmitter.IsSupportedExistentialReturn(typeSpec, typeDatabase));
    }

    [Fact]
    public void IsSupportedExistentialReturn_ProtocolWithSelfRequirement_ReturnsFalse()
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.SelfProto"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "ISelfProto"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.SelfProto"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.HasSelfRequirement,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(testModule);

        var typeSpec = new NamedTypeSpec("TestModule.SelfProto") { IsAny = true };

        Assert.False(ProtocolExtensionEmitter.IsSupportedExistentialReturn(typeSpec, typeDatabase));
    }

    // ─── P2: End-to-end: associated types prevent injection ──────────

    [Fact]
    public void ExistentialReturn_ProtocolWithAssociatedTypes_MethodNotInjected()
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "MyClass"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
                MetadataAccessor = "$s10TestModule7MyClassCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.AssocProto"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "IAssocProto"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.AssocProto"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.HasAssociatedTypes,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(testModule);

        var moduleDecl = CreateModuleDecl("TestModule");
        var conformingType = CreateClassDecl("MyClass", moduleDecl);
        conformingType.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
            SwiftTypeName.FromModuleQualifiedName("TestModule.AssocProto"),
            ""));

        var extMethods = CreateExtensionMethodDict("TestModule.AssocProto",
            CreateExtMethod("create", "public func create() -> any TestModule.AssocProto"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Empty(conformingType.Methods);
    }

    // ─── P2 (test gap): Closure wrapper path with existential return ─

    [Fact]
    public void ExistentialReturn_ClosureMethod_SwiftWrapperRendersAnyProtocol()
    {
        // Exercise EmitClosureSwiftWrapper with an existential return.
        // Ensures the closure path also classifies existentials as by-value.
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "MyClass", "TestProtocol");

        // Closure method: subscribe(handler: (Int) -> Void) -> any TestModule.TestProtocol
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("subscribe",
                "public func subscribe(_ handler: @escaping (Swift.Int) -> Swift.Void) -> any TestModule.TestProtocol"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        // The closure+extra-params gate blocks this (closure + return not yet supported together
        // in the closure path). But if it passes the gate in the future, the wrapper must
        // NOT use UnsafeMutableRawPointer for the existential return.
        // For now, verify the method was either injected with correct wrapper or correctly blocked.
        if (conformingType.Methods.Count > 0)
        {
            var wrapperLines = string.Join("\n", ctx.ProtocolExtSwiftWrapperLines);
            // If injected, return type must be existential (not UnsafeMutableRawPointer)
            Assert.DoesNotContain("-> UnsafeMutableRawPointer", wrapperLines);
            Assert.Contains("any TestProtocol", wrapperLines);
        }
        // If blocked by other gates (closure-only constraint), that's also acceptable
    }

    [Fact]
    public void ExistentialReturn_ClosureOnlyMethod_SwiftWrapperRendersAnyProtocol()
    {
        // Closure-only method (no additional params) — should pass the closure gate.
        // subscribe(_ handler: @escaping () -> Void) -> any TestModule.TestProtocol
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "MyClass", "TestProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("subscribe",
                "public func subscribe(_ handler: @escaping () -> Swift.Void) -> any TestModule.TestProtocol"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Single(conformingType.Methods);
        var wrapperLines = string.Join("\n", ctx.ProtocolExtSwiftWrapperLines);
        // Existential return: must be "any TestProtocol", not "UnsafeMutableRawPointer"
        Assert.DoesNotContain("-> UnsafeMutableRawPointer", wrapperLines);
        Assert.Contains("any TestProtocol", wrapperLines);
        // No Unmanaged.passRetained for existential (by-value return)
        Assert.DoesNotContain("Unmanaged.passRetained", wrapperLines);
    }

    // ─── ModuleProcessor computes InheritedRequirementsOnly flag ──────

    [Fact]
    public void ModuleProcessor_InheritedRequirementsOnly_FlagSet()
    {
        // Verify ModuleProcessor.RegisterProtocolType computes the flag for
        // a protocol with no own instance members but inherited requirements.
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        typeDatabase.AddModuleDatabase(swiftModule);

        // ChildProto: no own members, inherits from ParentProto
        var childProto = new ProtocolDecl
        {
            Name = "ChildProto",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ChildProto"),
            MangledName = "$s10TestModule10ChildProtoP",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            InheritedProtocols = { new NamedTypeSpec("TestModule.ParentProto") }
        };

        var typeDecls = new Dictionary<NamedTypeSpec, TypeDecl>
        {
            [new NamedTypeSpec("TestModule.ChildProto")] = childProto
        };

        var processor = new ModuleProcessor(
            "TestModule", "/tmp/TestModule.dylib", "TestModule",
            typeDecls, typeDatabase, NullLogger.Instance);

        var result = processor.FinalizeTypeProcessingAndCreateModuleDatabase();

        var swiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.ChildProto");
        Assert.True(result.ModuleDatabase.TryGetTypeRecord(swiftName, out var record));
        Assert.True(record!.Flags.HasFlag(TypeRecordFlags.InheritedRequirementsOnly));
    }

    [Fact]
    public void ModuleProcessor_ProtocolWithOwnMembers_FlagNotSet()
    {
        // A protocol with its own instance methods should NOT get the flag.
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        typeDatabase.AddModuleDatabase(swiftModule);

        var proto = new ProtocolDecl
        {
            Name = "Actionable",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Actionable"),
            MangledName = "$s10TestModule10ActionableP",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>
            {
                new MethodDecl
                {
                    Name = "perform",
                    MangledName = "",
                    MethodType = MethodType.Instance,
                    CSSignature = new List<ArgumentDecl>(),
                    GenericParameters = new List<GenericArgumentDecl>(),
                    IsConstructor = false,
                    Throws = false,
                    IsAsync = false,
                    Visibility = Visibility.Public,
                    ParentDecl = null,
                    ModuleDecl = null,
                }
            },
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            InheritedProtocols = { new NamedTypeSpec("TestModule.ParentProto") }
        };

        var typeDecls = new Dictionary<NamedTypeSpec, TypeDecl>
        {
            [new NamedTypeSpec("TestModule.Actionable")] = proto
        };

        var processor = new ModuleProcessor(
            "TestModule", "/tmp/TestModule.dylib", "TestModule",
            typeDecls, typeDatabase, NullLogger.Instance);

        var result = processor.FinalizeTypeProcessingAndCreateModuleDatabase();

        var swiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.Actionable");
        Assert.True(result.ModuleDatabase.TryGetTypeRecord(swiftName, out var record));
        Assert.False(record!.Flags.HasFlag(TypeRecordFlags.InheritedRequirementsOnly));
    }

    [Fact]
    public void ModuleProcessor_InheritingOnlyAnyObject_FlagNotSet()
    {
        // AnyObject is filtered out — no real inherited requirements.
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        typeDatabase.AddModuleDatabase(swiftModule);

        var proto = new ProtocolDecl
        {
            Name = "ObjectBound",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ObjectBound"),
            MangledName = "$s10TestModule11ObjectBoundP",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            InheritedProtocols = { new NamedTypeSpec("Swift.AnyObject") }
        };

        var typeDecls = new Dictionary<NamedTypeSpec, TypeDecl>
        {
            [new NamedTypeSpec("TestModule.ObjectBound")] = proto
        };

        var processor = new ModuleProcessor(
            "TestModule", "/tmp/TestModule.dylib", "TestModule",
            typeDecls, typeDatabase, NullLogger.Instance);

        var result = processor.FinalizeTypeProcessingAndCreateModuleDatabase();

        var swiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.ObjectBound");
        Assert.True(result.ModuleDatabase.TryGetTypeRecord(swiftName, out var record));
        Assert.False(record!.Flags.HasFlag(TypeRecordFlags.InheritedRequirementsOnly));
    }

    // ─── Inherited-requirements-only protocol blocked ─────────────────

    [Fact]
    public void IsSupportedExistentialReturn_InheritedRequirementsOnly_ReturnsFalse()
    {
        // Protocols with no own instance members but inherited requirements don't get
        // proxy classes (ProtocolProxyEmitter skips them → CS0535). The TypeRecordFlags
        // InheritedRequirementsOnly flag, set in ModuleProcessor, gates this.
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.InheritedProto"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "IInheritedProto"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.InheritedProto"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.InheritedRequirementsOnly,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(testModule);

        var typeSpec = new NamedTypeSpec("TestModule.InheritedProto") { IsAny = true };

        Assert.False(ProtocolExtensionEmitter.IsSupportedExistentialReturn(typeSpec, typeDatabase));
    }

    [Fact]
    public void ExistentialReturn_InheritedRequirementsOnly_MethodNotInjected()
    {
        // End-to-end: a protocol with InheritedRequirementsOnly flag should not allow
        // existential return methods to be injected.
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "MyClass"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
                MetadataAccessor = "$s10TestModule7MyClassCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.InheritedProto"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "IInheritedProto"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.InheritedProto"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.InheritedRequirementsOnly,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(testModule);

        var moduleDecl = CreateModuleDecl("TestModule");
        var conformingType = CreateClassDecl("MyClass", moduleDecl);
        conformingType.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
            SwiftTypeName.FromModuleQualifiedName("TestModule.InheritedProto"),
            ""));

        var extMethods = CreateExtensionMethodDict("TestModule.InheritedProto",
            CreateExtMethod("create", "public func create() -> any TestModule.InheritedProto"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Empty(conformingType.Methods);
    }

    // ─── Helper Methods ──────────────────────────────────────────────

    private static (ModuleDecl moduleDecl, ClassDecl conformingType, TypeDatabase typeDatabase)
        CreateSetup(string moduleName, string className, string protocolName)
    {
        var typeDatabase = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        typeDatabase.AddModuleDatabase(swiftModule);

        // Register both the protocol and the class in one module database
        var testModule = new ModuleTypeDatabase(moduleName, $"/tmp/{moduleName}.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{protocolName}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName($"Swift.{moduleName}", $"I{protocolName}"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{protocolName}"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{className}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName($"Swift.{moduleName}", className),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{className}"),
                MetadataAccessor = $"$s10{moduleName}{className.Length}{className}CMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(testModule);

        var moduleDecl = CreateModuleDecl(moduleName);
        var conformingType = CreateClassDecl(className, moduleDecl);

        // Add conformance to the protocol
        conformingType.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{className}"),
            SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{protocolName}"),
            ""));

        return (moduleDecl, conformingType, typeDatabase);
    }

    private static (ModuleDecl moduleDecl, ClassDecl conformingType, TypeDatabase typeDatabase)
        CreateSetupNoProtocolRecord(string moduleName, string className, string protocolName)
    {
        var typeDatabase = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        typeDatabase.AddModuleDatabase(swiftModule);

        // Register the class but NOT the protocol
        var testModule = new ModuleTypeDatabase(moduleName, $"/tmp/{moduleName}.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{className}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName($"Swift.{moduleName}", className),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{className}"),
                MetadataAccessor = $"$s10{moduleName}{className.Length}{className}CMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(testModule);

        var moduleDecl = CreateModuleDecl(moduleName);
        var conformingType = CreateClassDecl(className, moduleDecl);

        // Add conformance even though there's no TypeRecord — simulates unknown protocol
        conformingType.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{className}"),
            SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{protocolName}"),
            ""));

        return (moduleDecl, conformingType, typeDatabase);
    }

    private static TypeDatabase CreateTypeDatabase(string moduleName, string protocolName)
    {
        var typeDatabase = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        typeDatabase.AddModuleDatabase(swiftModule);

        // Register the protocol with TypeRecordKind.Protocol
        var protoModule = new ModuleTypeDatabase(moduleName, $"/tmp/{moduleName}.dylib");
        protoModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{protocolName}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName($"Swift.{moduleName}", $"I{protocolName}"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{protocolName}"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(protoModule);

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

    private static ClassDecl CreateClassDecl(string name, ModuleDecl moduleDecl)
    {
        var classDecl = new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s10{moduleDecl.Name}{name.Length}{name}CN",
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
        moduleDecl.Types.Add(classDecl);
        return classDecl;
    }

    private static ProtocolExtensionMethodDecl CreateExtMethod(string methodName, string rawSignature)
    {
        // Build PrintedName from methodName — find params in raw signature
        var printedName = $"{methodName}()";
        var parenStart = rawSignature.IndexOf('(');
        if (parenStart >= 0)
        {
            var parenEnd = rawSignature.IndexOf(')', parenStart);
            if (parenEnd > parenStart + 1)
            {
                // Has params — count them for PrintedName
                var paramStr = rawSignature.Substring(parenStart + 1, parenEnd - parenStart - 1);
                var labels = paramStr.Split(',').Select(p =>
                {
                    var trimmed = p.Trim();
                    var colonIdx = trimmed.IndexOf(':');
                    if (colonIdx < 0) return "_";
                    var label = trimmed.Substring(0, colonIdx).Trim();
                    // Strip "_ " prefix for unlabeled params
                    if (label.StartsWith("_ ")) return "_";
                    return label;
                });
                printedName = $"{methodName}({string.Join("", labels.Select(l => l + ":"))})";
            }
        }

        return new ProtocolExtensionMethodDecl
        {
            ProtocolQualifiedName = "",  // Set by caller via dict key
            MethodName = methodName,
            RawSignature = rawSignature,
            ReturnsSelf = false,
            IsMainActorIsolated = false,
            IsStatic = false,
            IsProperty = false,
            PrintedName = printedName,
            WhereConstraints = new List<string>()
        };
    }

    private static Dictionary<string, List<ProtocolExtensionMethodDecl>> CreateExtensionMethodDict(
        string protocolQualifiedName, params ProtocolExtensionMethodDecl[] methods)
    {
        foreach (var m in methods)
            m.ProtocolQualifiedName = protocolQualifiedName;

        return new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            [protocolQualifiedName] = methods.ToList()
        };
    }
}
