// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static BindingsGeneration.Tests.ProtocolExtensionTestHelpers;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for existential parameter support in protocol extension methods.
/// Verifies: known protocol existential params pass gate, wrapper renders "any Protocol" by value,
/// unknown/PAT/generic/ObjC-mixed protocols blocked.
/// </summary>
public class ProtocolExtensionExistentialParamTests
{
    private static readonly ILogger Logger = NullLogger.Instance;

    // ─── Known single-protocol existential param injected ──────────────

    [Fact]
    public void ExistentialParam_KnownProtocol_MethodInjected()
    {
        var (moduleDecl, conformingType, typeDatabase) = CreateSetupWithAdditionalProtocol("TestModule", "MyClass", "TestProtocol", "OtherProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("accept", "public func accept(_ item: any TestModule.OtherProtocol)"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Single(conformingType.Methods);
        Assert.Equal("accept", conformingType.Methods[0].Name);
    }

    // ─── Wrapper renders "any Protocol" by value ───────────────────────

    [Fact]
    public void ExistentialParam_SwiftWrapper_RendersAnyProtocol()
    {
        var (moduleDecl, conformingType, typeDatabase) = CreateSetupWithAdditionalProtocol("TestModule", "MyClass", "TestProtocol", "OtherProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("accept", "public func accept(_ item: any TestModule.OtherProtocol)"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        var wrapperLines = string.Join("\n", ctx.ProtocolExtSwiftWrapperLines);
        // Existential param: "any TestModule.OtherProtocol" (by value, module-qualified to avoid
        // ambiguity with Foundation/UIKit protocols of the same leaf name), NOT "UnsafeMutableRawPointer"
        Assert.Contains("any TestModule.OtherProtocol", wrapperLines);
        // Should NOT have Unmanaged.fromOpaque for existential param
        Assert.DoesNotContain("Unmanaged<OtherProtocol>", wrapperLines);
    }

    // ─── Existential param passed directly (no Unmanaged conversion) ───

    [Fact]
    public void ExistentialParam_PassedDirectly_NoConversion()
    {
        var (moduleDecl, conformingType, typeDatabase) = CreateSetupWithAdditionalProtocol("TestModule", "MyClass", "TestProtocol", "OtherProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("accept", "public func accept(_ item: any TestModule.OtherProtocol)"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        var wrapperLines = string.Join("\n", ctx.ProtocolExtSwiftWrapperLines);
        // The param should be passed directly — no "let __otherProtocol = ..." conversion
        Assert.Contains("instance.accept(otherProtocol)", wrapperLines);
    }

    // ─── Unknown protocol blocked ──────────────────────────────────────

    [Fact]
    public void ExistentialParam_UnknownProtocol_MethodNotInjected()
    {
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "MyClass", "TestProtocol");
        // UnknownProto has no TypeRecord
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("accept", "public func accept(_ item: any TestModule.UnknownProto)"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Empty(conformingType.Methods);
    }

    // ─── PAT protocol blocked (HasAssociatedTypes) ─────────────────────

    [Fact]
    public void ExistentialParam_PATProtocol_MethodNotInjected()
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.TestProtocol"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ITestProtocol"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.TestProtocol"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "MyClass"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
                MetadataAccessor = "$s10TestModule7MyClassCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        // PAT protocol with associated types
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Collection"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ICollection"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Collection"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.HasAssociatedTypes,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(testModule);

        var moduleDecl = CreateModuleDecl("TestModule");
        var conformingType = CreateClassDecl("MyClass", moduleDecl);
        conformingType.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
            SwiftTypeName.FromModuleQualifiedName("TestModule.TestProtocol"),
            ""));

        // PAT protocols have HasAssociatedTypes flag → GetPublicExistentialType returns a
        // non-generic interface name, but the actual emitted interface is generic.
        // PInvokeEmitter consumes publicType directly → C# type mismatch. Must stay gated.
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("accept", "public func accept(_ item: any TestModule.Collection)"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Empty(conformingType.Methods);
    }

    // ─── Self-requirement protocol blocked ─────────────────────────────

    [Fact]
    public void ExistentialParam_SelfRequirement_MethodNotInjected()
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.TestProtocol"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ITestProtocol"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.TestProtocol"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "MyClass"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
                MetadataAccessor = "$s10TestModule7MyClassCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        // Protocol with Self requirement (e.g., Equatable)
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Equatable"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "IEquatable"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Equatable"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.HasSelfRequirement,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(testModule);

        var moduleDecl = CreateModuleDecl("TestModule");
        var conformingType = CreateClassDecl("MyClass", moduleDecl);
        conformingType.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
            SwiftTypeName.FromModuleQualifiedName("TestModule.TestProtocol"),
            ""));

        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("compare", "public func compare(_ other: any TestModule.Equatable)"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Empty(conformingType.Methods);
    }

    // ─── Generic protocol existential blocked (publicType→AnyType) ─────

    [Fact]
    public void ExistentialParam_GenericProtocol_MethodNotInjected()
    {
        var (moduleDecl, conformingType, typeDatabase) = CreateSetupWithAdditionalProtocol("TestModule", "MyClass", "TestProtocol", "OtherProtocol");
        // "any OtherProtocol<SomeType>" → generic protocol existential → AnyType
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("accept", "public func accept(_ item: any TestModule.OtherProtocol<SomeModule.SomeType>)"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Empty(conformingType.Methods);
    }

    // ─── ObjC mixed composition blocked ────────────────────────────────

    [Fact]
    public void ExistentialParam_ObjCMixedComposition_MethodNotInjected()
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.TestProtocol"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ITestProtocol"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.TestProtocol"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "MyClass"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
                MetadataAccessor = "$s10TestModule7MyClassCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
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

        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("accept",
                "public func accept(_ item: any TestProtocol & ObjectiveC.NSObjectProtocol)"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Empty(conformingType.Methods);
    }

    // ─── Combo: existential param + throws ─────────────────────────────

    [Fact]
    public void ExistentialParam_WithThrows_Injected()
    {
        var (moduleDecl, conformingType, typeDatabase) = CreateSetupWithAdditionalProtocol("TestModule", "MyClass", "TestProtocol", "OtherProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("validate", "public func validate(_ item: any TestModule.OtherProtocol) throws"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Single(conformingType.Methods);
        Assert.True(conformingType.Methods[0].Throws);

        var wrapperLines = string.Join("\n", ctx.ProtocolExtSwiftWrapperLines);
        Assert.Contains("any TestModule.OtherProtocol", wrapperLines);
        Assert.Contains(" throws {", wrapperLines);
    }

    // ─── Duplicate-leaf-type params get numeric suffixes ────────────────

    [Fact]
    public void ExistentialParam_DuplicateLeafType_GetsNumericSuffix()
    {
        // Repro of a swift_compile failure: two unlabeled params of the same existential type
        // both derive the same base internal name (`otherProtocol`), producing
        // `invalid redeclaration of 'otherProtocol'` at the swiftc layer.
        // ComputeUniqueParamNames must suffix the duplicate with a numeric index.
        var (moduleDecl, conformingType, typeDatabase) = CreateSetupWithAdditionalProtocol(
            "TestModule", "MyClass", "TestProtocol", "OtherProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("combine",
                "public func combine(_ a: any TestModule.OtherProtocol, _ b: any TestModule.OtherProtocol)"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        var wrapperLines = string.Join("\n", ctx.ProtocolExtSwiftWrapperLines);
        // Both internal names must appear exactly once each in the wrapper signature.
        Assert.Contains("_ otherProtocol: any TestModule.OtherProtocol", wrapperLines);
        Assert.Contains("_ otherProtocol2: any TestModule.OtherProtocol", wrapperLines);
        // The call site must forward both unique names, not the same one twice.
        Assert.Contains("instance.combine(otherProtocol, otherProtocol2)", wrapperLines);

        // The injected C# MethodDecl must dedup parameter PrivateName too — the regular
        // (non-closure) protocol-extension path used to skip dedup, generating two C#
        // params both named `otherProtocol`.
        var csSignature = conformingType.Methods[0].CSSignature;
        var paramPrivateNames = csSignature.Skip(1).Select(p => p.PrivateName).ToList();
        Assert.Equal(new[] { "otherProtocol", "otherProtocol2" }, paramPrivateNames);
    }

    [Fact]
    public void ExistentialParam_ThreeIdenticalLeafTypes_GetSequentialSuffixes()
    {
        // Triple-collision shape: three unlabelled params of the same existential
        // type must produce `otherProtocol`, `otherProtocol2`, `otherProtocol3` —
        // not a regression to two `otherProtocol2` aliases when the counter resets.
        // Catches off-by-one regressions in ComputeUniqueParamNames where the seen
        // count is read after rather than before the increment.
        var (moduleDecl, conformingType, typeDatabase) = CreateSetupWithAdditionalProtocol(
            "TestModule", "MyClass", "TestProtocol", "OtherProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("merge",
                "public func merge(_ a: any TestModule.OtherProtocol, _ b: any TestModule.OtherProtocol, _ c: any TestModule.OtherProtocol)"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        var wrapperLines = string.Join("\n", ctx.ProtocolExtSwiftWrapperLines);
        Assert.Contains("_ otherProtocol: any TestModule.OtherProtocol", wrapperLines);
        Assert.Contains("_ otherProtocol2: any TestModule.OtherProtocol", wrapperLines);
        Assert.Contains("_ otherProtocol3: any TestModule.OtherProtocol", wrapperLines);
        Assert.Contains("instance.merge(otherProtocol, otherProtocol2, otherProtocol3)", wrapperLines);

        var csSignature = conformingType.Methods[0].CSSignature;
        var paramPrivateNames = csSignature.Skip(1).Select(p => p.PrivateName).ToList();
        Assert.Equal(new[] { "otherProtocol", "otherProtocol2", "otherProtocol3" }, paramPrivateNames);
    }

    [Fact]
    public void ExistentialParam_LabelledFollowedByUnlabelledSameLeaf_DedupAcrossLabelKinds()
    {
        // Mixed-label shape: the first param has an explicit label (`first:`)
        // that happens to differ from the type-derived base name, the second
        // is unlabelled and falls through to `otherProtocol`. They DON'T collide.
        // But two unlabelled params *after* the labelled one MUST still get
        // suffixed against each other. Without dedup across label kinds, the
        // counter would only count unlabelled collisions and miss labelled
        // params that share a sanitized base name.
        var (moduleDecl, conformingType, typeDatabase) = CreateSetupWithAdditionalProtocol(
            "TestModule", "MyClass", "TestProtocol", "OtherProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("compose",
                "public func compose(otherProtocol: any TestModule.OtherProtocol, _ b: any TestModule.OtherProtocol)"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        var wrapperLines = string.Join("\n", ctx.ProtocolExtSwiftWrapperLines);
        // The labelled param keeps its label; the unlabelled one's type-derived base
        // name collides with that label and must be suffixed.
        Assert.Contains("_ otherProtocol: any TestModule.OtherProtocol", wrapperLines);
        Assert.Contains("_ otherProtocol2: any TestModule.OtherProtocol", wrapperLines);

        var csSignature = conformingType.Methods[0].CSSignature;
        var paramPrivateNames = csSignature.Skip(1).Select(p => p.PrivateName).ToList();
        Assert.Equal(new[] { "otherProtocol", "otherProtocol2" }, paramPrivateNames);
    }

    // ─── Helper Methods ──────────────────────────────────────────────

    /// <summary>
    /// Extended setup that registers an additional protocol (used for existential param tests
    /// where the param type is a different protocol than the one being extended).
    /// </summary>
    private static (ModuleDecl moduleDecl, ClassDecl conformingType, TypeDatabase typeDatabase)
        CreateSetupWithAdditionalProtocol(string moduleName, string className, string protocolName, string additionalProtocol)
    {
        var typeDatabase = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        typeDatabase.AddModuleDatabase(swiftModule);

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
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{additionalProtocol}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName($"Swift.{moduleName}", $"I{additionalProtocol}"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{additionalProtocol}"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(testModule);

        var moduleDecl = CreateModuleDecl(moduleName);
        var conformingType = CreateClassDecl(className, moduleDecl);
        conformingType.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{className}"),
            SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{protocolName}"),
            ""));

        return (moduleDecl, conformingType, typeDatabase);
    }
}
