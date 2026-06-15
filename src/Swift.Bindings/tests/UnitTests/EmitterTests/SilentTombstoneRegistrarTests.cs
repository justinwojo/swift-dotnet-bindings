// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Verifies the pre-pass only tags types that a handler would actually emit via
/// the opaque tombstone branch. False positives here produce spurious SB0002
/// diagnostics at call sites referencing ordinary C# enums or namespace classes.
/// </summary>
public class SilentTombstoneRegistrarTests
{
    [Fact]
    public void Precompute_NamespaceEnumWithAllMembersSkipped_NotRegistered()
    {
        // A caseless enum emits as a static class, not an opaque ISwiftObject.
        // Even if every method is skipped (e.g., generic method the emitter
        // cannot lower), the call site can still call remaining static members,
        // so SB0002 would be a false positive.
        var moduleDecl = BuildModule();
        var enumDecl = BuildEnum(moduleDecl, "NS", cases: new(), withSkippedGenericMethod: true);
        moduleDecl.Types.Add(enumDecl);

        var ctx = new ModuleEmissionContext();
        SilentTombstoneRegistrar.Precompute(moduleDecl, new StubTypeDatabase(), ctx);

        Assert.False(ctx.IsSilentTombstone("TestModule.NS"));
    }

    [Fact]
    public void Precompute_SimpleEnumWithAllMembersSkipped_NotRegistered()
    {
        // A simple enum (cases, no associated values, no raw value or integral) emits
        // as a C# enum value type. All its non-case members may be skipped without
        // making the enum itself a tombstone.
        var moduleDecl = BuildModule();
        var enumDecl = BuildEnum(
            moduleDecl,
            "Color",
            cases: new() { BuildCase("Red"), BuildCase("Blue") },
            withSkippedGenericMethod: true);
        moduleDecl.Types.Add(enumDecl);

        var ctx = new ModuleEmissionContext();
        SilentTombstoneRegistrar.Precompute(moduleDecl, new StubTypeDatabase(), ctx);

        Assert.False(ctx.IsSilentTombstone("TestModule.Color"));
    }

    [Fact]
    public void Precompute_CrossModuleExtensionClass_NotRegistered()
    {
        // A class whose SwiftTypeName.Module differs from its containing moduleDecl is
        // a cross-module extension — emitted as a static helper class, not an opaque
        // wrapper. The real type lives in the other module.
        var moduleDecl = BuildModule("ExtensionModule");
        var classDecl = new ClassDecl
        {
            Name = "Foo",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("OwnerModule.Foo"),
            MangledName = "",
            IsFinal = true,
            GenericParameters = new(),
            Properties = new(),
            Methods = new() { BuildGenericSkippedMethod(moduleDecl) },
            Types = new(),
            Operators = new(),
            Subscripts = new(),
            Conformances = new(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };
        moduleDecl.Types.Add(classDecl);

        var ctx = new ModuleEmissionContext();
        SilentTombstoneRegistrar.Precompute(moduleDecl, new StubTypeDatabase(), ctx);

        Assert.False(ctx.IsSilentTombstone("OwnerModule.Foo"));
    }

    [Fact]
    public void Precompute_CrossModuleExtensionStruct_NotRegistered()
    {
        // Mirror of Precompute_CrossModuleExtensionClass_NotRegistered for StructDecl.
        // FrozenStructHandler and NonFrozenStructHandler route cross-module struct
        // extensions through CrossModuleExtensionEmitter (static extension surface),
        // never through the opaque-tombstone branch. A struct registered here would
        // trip AssertSilentTombstoneInvariant because no AddEmittedOpaqueType call
        // ever happens on that path. Reproduces a regression where a Foundation frozen
        // struct surfaced through a third-party module's ABI as a cross-module extension
        // receiver.
        var moduleDecl = BuildModule("PaymentSdkCoreModule");
        var structDecl = new StructDecl
        {
            Name = "Decimal",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.Decimal"),
            MangledName = "",
            IsFrozen = true,
            GenericParameters = new(),
            Properties = new(),
            Methods = new() { BuildGenericSkippedMethod(moduleDecl) },
            Types = new(),
            Operators = new(),
            Subscripts = new(),
            Conformances = new(),
            MetadataAccessor = "",
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };
        moduleDecl.Types.Add(structDecl);

        var ctx = new ModuleEmissionContext();
        SilentTombstoneRegistrar.Precompute(moduleDecl, new StubTypeDatabase(), ctx);

        Assert.False(ctx.IsSilentTombstone("Foundation.Decimal"));
    }

    [Fact]
    public void Precompute_CrossModuleExtensionStruct_NonFrozenShape_NotRegistered()
    {
        // The parser sets StructDecl.IsFrozen from the extension node's own attributes,
        // which never carry @frozen — so cross-module extension receivers like
        // `extension Swift.Array where Element == UInt8` arrive at the emitter with
        // IsFrozen = false and dispatch to NonFrozenStructHandler. That handler's
        // cross-module guard must mirror FrozenStructHandler's: route to
        // CrossModuleExtensionEmitter and never register here. Without this guard, the
        // wrapper class is emitted into the host module's namespace (colliding with
        // Swift.SwiftArray; partial class double colliding with primitive).
        var moduleDecl = BuildModule("CryptoLibModule");
        var structDecl = new StructDecl
        {
            Name = "Array",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Array"),
            MangledName = "",
            IsFrozen = false,
            GenericParameters = new(),
            Properties = new(),
            Methods = new() { BuildGenericSkippedMethod(moduleDecl) },
            Types = new(),
            Operators = new(),
            Subscripts = new(),
            Conformances = new(),
            MetadataAccessor = "",
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };
        moduleDecl.Types.Add(structDecl);

        var ctx = new ModuleEmissionContext();
        SilentTombstoneRegistrar.Precompute(moduleDecl, new StubTypeDatabase(), ctx);

        Assert.False(ctx.IsSilentTombstone("Swift.Array"));
    }

    [Fact]
    public void Precompute_UnderscoreSuppressedStruct_NotRegistered()
    {
        // HandleBaseDecl suppresses underscore-prefixed types registered via
        // ModuleEmissionContext.SetUnderscoreSuppressedNames. The pre-pass must
        // mirror that suppression so these types don't end up as silent tombstones.
        var moduleDecl = BuildModule();
        var structDecl = BuildStruct(moduleDecl, "_Hidden", withSkippedMember: true);
        moduleDecl.Types.Add(structDecl);

        var ctx = new ModuleEmissionContext();
        ctx.SetUnderscoreSuppressedNames(new HashSet<string> { "TestModule._Hidden" });
        SilentTombstoneRegistrar.Precompute(moduleDecl, new StubTypeDatabase(), ctx);

        Assert.False(ctx.IsSilentTombstone("TestModule._Hidden"));
    }

    [Fact]
    public void Precompute_SpiProtectedStruct_NotRegistered()
    {
        // HandleBaseDecl suppresses @_spi types — they never emit a C# type,
        // so they must never be registered as silent tombstones.
        var moduleDecl = BuildModule();
        var structDecl = BuildStruct(moduleDecl, "SpiOnly", withSkippedMember: true);
        structDecl.IsSpiProtected = true;
        moduleDecl.Types.Add(structDecl);

        var ctx = new ModuleEmissionContext();
        SilentTombstoneRegistrar.Precompute(moduleDecl, new StubTypeDatabase(), ctx);

        Assert.False(ctx.IsSilentTombstone("TestModule.SpiOnly"));
    }

    [Fact]
    public void Precompute_SwiftUIViewStruct_NotRegistered()
    {
        // HandleBaseDecl routes SwiftUI View structs to SwiftUIBridgeCollector instead
        // of emitting them through a type handler — they must never be registered as
        // silent tombstones.
        var moduleDecl = BuildModule();
        var structDecl = BuildStruct(moduleDecl, "MyView", withSkippedMember: true);
        structDecl.Conformances.Add(new TypeConformance(
            ConformingType: structDecl.SwiftTypeName,
            Protocol: SwiftTypeName.FromModuleQualifiedName("SwiftUI.View"),
            ProtocolConformanceDescriptor: ""));
        moduleDecl.Types.Add(structDecl);

        var ctx = new ModuleEmissionContext();
        SilentTombstoneRegistrar.Precompute(moduleDecl, new StubTypeDatabase(), ctx);

        Assert.False(ctx.IsSilentTombstone("TestModule.MyView"));
    }

    [Fact]
    public void Precompute_NestedProtocolWithAllMembersSkipped_NotRegistered()
    {
        // ProtocolHandler has no opaque-tombstone branch — it emits an interface and
        // (optionally) a proxy. A protocol nested inside a struct/class/enum walks
        // through this pre-pass as a TypeDecl, so it must be short-circuited before
        // the member-count check or it becomes a false-positive tombstone.
        var moduleDecl = BuildModule();
        var outerStruct = BuildStruct(moduleDecl, "Outer", withSkippedMember: false);
        var nestedProtocol = new ProtocolDecl
        {
            Name = "Inner",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Outer.Inner"),
            MangledName = "",
            GenericParameters = new(),
            Properties = new(),
            Methods = new() { BuildGenericSkippedMethod(moduleDecl) },
            Types = new(),
            Operators = new(),
            Subscripts = new(),
            ParentDecl = outerStruct,
            ModuleDecl = moduleDecl,
        };
        outerStruct.Types.Add(nestedProtocol);
        moduleDecl.Types.Add(outerStruct);

        var ctx = new ModuleEmissionContext();
        SilentTombstoneRegistrar.Precompute(moduleDecl, new StubTypeDatabase(), ctx);

        Assert.False(ctx.IsSilentTombstone("TestModule.Outer.Inner"));
    }

    [Fact]
    public void Precompute_StructWithAllMembersSkipped_RegisteredWithModuleQualifiedName()
    {
        // Positive baseline: a struct with no emittable members and at least one
        // skipped member takes the opaque branch and must be registered under its
        // full module-qualified name (matches NamedTypeSpec.Name at call sites).
        var moduleDecl = BuildModule();
        var structDecl = new StructDecl
        {
            Name = "Opaque",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Opaque"),
            MangledName = "",
            IsFrozen = false,
            GenericParameters = new(),
            Properties = new(),
            Methods = new() { BuildGenericSkippedMethod(moduleDecl) },
            Types = new(),
            Operators = new(),
            Subscripts = new(),
            Conformances = new(),
            MetadataAccessor = "",
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };
        moduleDecl.Types.Add(structDecl);

        var ctx = new ModuleEmissionContext();
        SilentTombstoneRegistrar.Precompute(moduleDecl, new StubTypeDatabase(), ctx);

        Assert.True(ctx.IsSilentTombstone("TestModule.Opaque"));
    }

    private static ModuleDecl BuildModule(string name = "TestModule") => new()
    {
        Name = name,
        ParentDecl = null,
        ModuleDecl = null,
        Properties = new(),
        Methods = new(),
        Types = new(),
        Dependencies = new(),
        Protocols = new(),
    };

    private static StructDecl BuildStruct(
        ModuleDecl moduleDecl,
        string name,
        bool withSkippedMember) => new()
    {
        Name = name,
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
        MangledName = "",
        IsFrozen = false,
        GenericParameters = new(),
        Properties = new(),
        Methods = withSkippedMember ? new() { BuildGenericSkippedMethod(moduleDecl) } : new(),
        Types = new(),
        Operators = new(),
        Subscripts = new(),
        Conformances = new(),
        MetadataAccessor = "",
        ParentDecl = moduleDecl,
        ModuleDecl = moduleDecl,
    };

    private static EnumDecl BuildEnum(
        ModuleDecl moduleDecl,
        string name,
        List<EnumCaseDecl> cases,
        bool withSkippedGenericMethod)
    {
        var enumDecl = new EnumDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = "",
            IsFrozen = true,
            Cases = cases,
            GenericParameters = new(),
            Properties = new(),
            Methods = withSkippedGenericMethod ? new() { BuildGenericSkippedMethod(moduleDecl) } : new(),
            Types = new(),
            Operators = new(),
            Subscripts = new(),
            Conformances = new(),
            MetadataAccessor = "",
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };
        return enumDecl;
    }

    private static EnumCaseDecl BuildCase(string name) => new()
    {
        Name = name,
        MangledName = "",
        ParentDecl = null,
        ModuleDecl = null,
    };

    /// <summary>
    /// A method that MemberEmissionValidator will classify as non-emittable because
    /// it references SwiftUI (an unsupported module) — contributes to the "skipped"
    /// count without the type gaining any emittable members.
    /// </summary>
    private static MethodDecl BuildGenericSkippedMethod(ModuleDecl moduleDecl) => new()
    {
        Name = "skipMe",
        MangledName = "",
        MethodType = MethodType.Instance,
        IsConstructor = false,
        Throws = false,
        IsAsync = false,
        IsSynthesizedAccessor = false,
        CSSignature = new()
        {
            new ArgumentDecl
            {
                Name = "",
                PrivateName = "",
                SwiftTypeSpec = TupleTypeSpec.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = null,
                ModuleDecl = moduleDecl,
            },
            new ArgumentDecl
            {
                Name = "view",
                PrivateName = "view",
                SwiftTypeSpec = new NamedTypeSpec("SwiftUI.View"),
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = null,
                ModuleDecl = moduleDecl,
            },
        },
        GenericParameters = new(),
        ParentDecl = null,
        ModuleDecl = moduleDecl,
    };

    private sealed class StubTypeDatabase : ITypeDatabase
    {
        public string? AsyncLibraryName => null;
        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) => false;
        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, [NotNullWhen(true)] out TypeRecord? record)
        {
            record = null;
            return false;
        }
        public string GetLibraryPath(string moduleName) => "";
        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
    }
}
