// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Regression tests for WU1: B6 guard relaxation allowing Optional&lt;any Protocol&gt; through.
/// Verifies that MethodHandler and MemberEmissionValidator correctly allow Optional existentials
/// with known protocols while still blocking Dictionary, Set, and unknown protocol combinations.
/// </summary>
public class ExistentialOptionalGuardTests
{
    #region MethodHandler / MemberEmissionValidator — method path

    [Fact]
    public void Method_WithOptionalExistentialParam_KnownProtocol_NotBlockedByB6()
    {
        var typeDatabase = CreateTypeDatabaseWithProtocol("TestModule.ImageProcessing");
        var optionalExistentialParam = CreateOptionalExistentialTypeSpec("TestModule.ImageProcessing");

        var method = CreateMethodDecl("configure", "TestModule.TestType",
            parameters: new[] { ("processor", optionalExistentialParam as TypeSpec) });

        var result = MemberEmissionValidator.CanEmitMethod(method, typeDatabase, out var details, out _);

        // Must NOT be blocked by B6 guard (UnsupportedExistential).
        // May fail later in signature checks (UnsupportedSignature) due to minimal TypeDatabase.
        Assert.True(result != SkipReason.UnsupportedExistential,
            $"Expected not UnsupportedExistential, but got {result}: {details}");
    }

    [Fact]
    public void Method_WithOptionalExistentialParam_UnknownProtocol_StillSkipped()
    {
        // No protocol TypeRecord registered → AllProtocolsHaveTypeRecords returns false
        var typeDatabase = CreateTypeDatabase();
        var optionalExistentialParam = CreateOptionalExistentialTypeSpec("TestModule.UnknownProtocol");

        var method = CreateMethodDecl("configure", "TestModule.TestType",
            parameters: new[] { ("processor", optionalExistentialParam as TypeSpec) });

        var result = MemberEmissionValidator.CanEmitMethod(method, typeDatabase, out var details, out _);

        Assert.NotNull(result);
        Assert.Equal(SkipReason.UnsupportedExistential, result);
    }

    [Fact]
    public void Method_WithOptionalExistentialParam_ObjCProtocol_AllowedThrough()
    {
        // Single-protocol ObjC (ObjectiveC.NSObjectProtocol) with TypeRecord:
        // GetPublicExistentialType returns "INSObjectProtocol" (not "object").
        // ObjC filtering only applies in GetCompositionInterfaceName (multi-protocol).
        // So single-protocol ObjC IS allowed through the B6 guard (correctly).
        // It may fail later on other checks (e.g., UnsupportedSignature) but NOT UnsupportedExistential.
        var typeDatabase = CreateTypeDatabaseWithObjCProtocol();
        var optionalExistentialParam = CreateOptionalExistentialTypeSpec("ObjectiveC.NSObjectProtocol");

        var method = CreateMethodDecl("configure", "TestModule.TestType",
            parameters: new[] { ("obj", optionalExistentialParam as TypeSpec) });

        var result = MemberEmissionValidator.CanEmitMethod(method, typeDatabase, out var details, out _);

        // Not UnsupportedExistential — allowed through B6 guard (may fail later for other reasons)
        Assert.True(result == null || result != SkipReason.UnsupportedExistential,
            $"Expected not UnsupportedExistential, but got {result}: {details}");
    }

    [Fact]
    public void Constructor_WithOptionalExistentialParam_KnownProtocol_PassesValidator()
    {
        // The validator always passes constructors through (line 721: if IsConstructor return null).
        // The actual constructor existential guard lives in MethodHandler.EmitConstructor (line 167),
        // which is tested indirectly via integration tests (TestFramework).
        // This test verifies the validator doesn't interfere with constructor existential params.
        var typeDatabase = CreateTypeDatabaseWithProtocol("TestModule.ImageProcessing");
        var optionalExistentialParam = CreateOptionalExistentialTypeSpec("TestModule.ImageProcessing");

        var method = CreateConstructorDecl("TestModule.TestType",
            parameters: new[] { ("processor", optionalExistentialParam as TypeSpec) });

        var result = MemberEmissionValidator.CanEmitMethod(method, typeDatabase, out var details, out _);

        // Constructors always pass the validator (no B6 guard for constructors in validator)
        Assert.Null(result);
    }

    [Fact]
    public void Constructor_WithOptionalExistentialParam_UnknownProtocol_StillPassesValidator()
    {
        // Even unknown-protocol constructors pass the validator — the MethodHandler
        // constructor path handles existential bypass separately.
        var typeDatabase = CreateTypeDatabase();
        var optionalExistentialParam = CreateOptionalExistentialTypeSpec("TestModule.UnknownProtocol");

        var method = CreateConstructorDecl("TestModule.TestType",
            parameters: new[] { ("processor", optionalExistentialParam as TypeSpec) });

        var result = MemberEmissionValidator.CanEmitMethod(method, typeDatabase, out var details, out _);

        Assert.Null(result);
    }

    [Fact]
    public void Method_WithDictionaryExistentialParam_StillSkipped()
    {
        var typeDatabase = CreateTypeDatabaseWithProtocolAndDictionary("TestModule.ImageProcessing");

        // Dictionary<String, any ImageProcessing> — not Array or Optional, still blocked
        var existentialInner = new NamedTypeSpec("TestModule.ImageProcessing") { IsAny = true };
        var dictTypeSpec = new NamedTypeSpec("Swift.Dictionary");
        dictTypeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        dictTypeSpec.GenericParameters.Add(existentialInner);

        var method = CreateMethodDecl("configure", "TestModule.TestType",
            parameters: new[] { ("dict", dictTypeSpec as TypeSpec) });

        var result = MemberEmissionValidator.CanEmitMethod(method, typeDatabase, out var details, out _);

        Assert.NotNull(result);
        Assert.Equal(SkipReason.UnsupportedExistential, result);
    }

    [Fact]
    public void Validator_CanEmitMethod_OptionalExistentialParam_NotBlockedByB6()
    {
        // Verify the validator path does not block Optional<any Protocol> as UnsupportedExistential
        var typeDatabase = CreateTypeDatabaseWithProtocol("TestModule.DataCaching");
        var optionalExistentialParam = CreateOptionalExistentialTypeSpec("TestModule.DataCaching");

        var method = CreateMethodDecl("setCache", "TestModule.TestType",
            parameters: new[] { ("cache", optionalExistentialParam as TypeSpec) });

        var result = MemberEmissionValidator.CanEmitMethod(method, typeDatabase, out var details, out _);

        Assert.True(result != SkipReason.UnsupportedExistential,
            $"Expected not UnsupportedExistential, but got {result}: {details}");
    }

    [Fact]
    public void Method_WithOptionalMixedCompositionParam_StillSkipped()
    {
        // P1 fix: Optional<any ImageProcessing & UIViewControllerTransitioningDelegate>
        // ObjC filtering drops the UIKit protocol → filteredCount (1) != originalCount (2).
        // Marshalling would cast to ISwiftExistentialConvertible<ExistentialContainer1>
        // but the Swift ABI provides ExistentialContainer2 → runtime mismatch.
        var typeDatabase = CreateTypeDatabaseWithMixedComposition();

        var existentialInner1 = new NamedTypeSpec("TestModule.ImageProcessing");
        var existentialInner2 = new NamedTypeSpec("UIKit.UIViewControllerTransitioningDelegate");
        var protocolList = new ProtocolListTypeSpec(new[] { existentialInner1, existentialInner2 });
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(protocolList);

        var method = CreateMethodDecl("configure", "TestModule.TestType",
            parameters: new[] { ("delegate", optionalSpec as TypeSpec) });

        var result = MemberEmissionValidator.CanEmitMethod(method, typeDatabase, out var details, out _);

        Assert.NotNull(result);
        Assert.Equal(SkipReason.UnsupportedExistential, result);
    }

    #endregion

    #region Helpers

    private static NamedTypeSpec CreateOptionalExistentialTypeSpec(string protocolName)
    {
        var existentialInner = new NamedTypeSpec(protocolName) { IsAny = true };
        var optionalTypeSpec = new NamedTypeSpec("Swift.Optional");
        optionalTypeSpec.GenericParameters.Add(existentialInner);
        return optionalTypeSpec;
    }

    private static MethodDecl CreateMethodDecl(string name, string parentTypeName,
        TypeSpec? returnType = null, (string name, TypeSpec type)[]? parameters = null)
    {
        var moduleDecl = CreateModuleDecl();
        var csSignature = new List<ArgumentDecl>();

        csSignature.Add(new ArgumentDecl
        {
            Name = "_return",
            PrivateName = "_return",
            SwiftTypeSpec = returnType ?? TupleTypeSpec.Empty,
            IsGeneric = false,
            IsInOut = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        });

        if (parameters != null)
        {
            foreach (var (pName, pType) in parameters)
            {
                csSignature.Add(new ArgumentDecl
                {
                    Name = pName,
                    PrivateName = pName,
                    SwiftTypeSpec = pType,
                    IsGeneric = false,
                    IsInOut = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                });
            }
        }

        return new MethodDecl
        {
            Name = name,
            MangledName = "$s4test" + name,
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = CreateStructDecl(parentTypeName.Split('.').Last()),
            ModuleDecl = moduleDecl
        };
    }

    private static MethodDecl CreateConstructorDecl(string parentTypeName,
        (string name, TypeSpec type)[]? parameters = null)
    {
        var moduleDecl = CreateModuleDecl();
        var csSignature = new List<ArgumentDecl>();

        csSignature.Add(new ArgumentDecl
        {
            Name = "_return",
            PrivateName = "_return",
            SwiftTypeSpec = TupleTypeSpec.Empty,
            IsGeneric = false,
            IsInOut = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        });

        if (parameters != null)
        {
            foreach (var (pName, pType) in parameters)
            {
                csSignature.Add(new ArgumentDecl
                {
                    Name = pName,
                    PrivateName = pName,
                    SwiftTypeSpec = pType,
                    IsGeneric = false,
                    IsInOut = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                });
            }
        }

        return new MethodDecl
        {
            Name = "init",
            MangledName = "$s4testcfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = CreateStructDecl(parentTypeName.Split('.').Last()),
            ModuleDecl = moduleDecl
        };
    }

    private static ModuleDecl CreateModuleDecl()
    {
        return new ModuleDecl
        {
            Name = "TestModule",
            Types = new List<TypeDecl>(),
            Protocols = new List<ProtocolDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Dependencies = new List<string>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static StructDecl CreateStructDecl(string name)
    {
        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = "$sN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = true,
            MetadataAccessor = "$sMa"
        };
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
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftOptional"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
                MetadataAccessor = "$sSqMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Enum
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithProtocol(string protocolName)
    {
        var typeDatabase = new TypeDatabase();
        // Reuse SwiftModule with Int + Optional
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
            SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftOptional"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
                MetadataAccessor = "$sSqMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Enum
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var parts = protocolName.Split('.');
        var moduleName = parts[0];
        var shortName = parts[1];

        var moduleDb = new ModuleTypeDatabase(moduleName, $"/tmp/{moduleName}.dylib");
        moduleDb.RegisterType(
            SwiftTypeName.FromModuleQualifiedName(protocolName),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(moduleName, $"I{shortName}"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(protocolName),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(moduleDb);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithObjCProtocol()
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
            SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftOptional"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
                MetadataAccessor = "$sSqMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Enum
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        typeDatabase.AddModuleDatabase(testModule);

        // Register in ObjectiveC module — IsObjCModuleType will detect this
        var objcModule = new ModuleTypeDatabase("ObjectiveC", "/usr/lib/libobjc.dylib");
        objcModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("ObjectiveC.NSObjectProtocol"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("ObjectiveC", "INSObjectProtocol"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("ObjectiveC.NSObjectProtocol"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(objcModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithProtocolAndDictionary(string protocolName)
    {
        // Build from scratch to avoid duplicate module registrations
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
            SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftOptional"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
                MetadataAccessor = "$sSqMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Enum
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Dictionary"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System.Collections.Generic", "Dictionary"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Dictionary"),
                MetadataAccessor = "$sSDMa",
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
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var parts = protocolName.Split('.');
        var moduleName = parts[0];
        var shortName = parts[1];

        var testModule = new ModuleTypeDatabase(moduleName, $"/tmp/{moduleName}.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName(protocolName),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(moduleName, $"I{shortName}"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(protocolName),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithMixedComposition()
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
            SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftOptional"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
                MetadataAccessor = "$sSqMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Enum
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        // Non-ObjC protocol
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.ImageProcessing"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "IImageProcessing"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ImageProcessing"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(testModule);

        // ObjC protocol (UIKit module → IsObjCModuleType returns true)
        var uikitModule = new ModuleTypeDatabase("UIKit", "/System/Library/Frameworks/UIKit.framework/UIKit");
        uikitModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("UIKit.UIViewControllerTransitioningDelegate"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("UIKit", "IUIViewControllerTransitioningDelegate"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("UIKit.UIViewControllerTransitioningDelegate"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(uikitModule);
        return typeDatabase;
    }

    #endregion
}
