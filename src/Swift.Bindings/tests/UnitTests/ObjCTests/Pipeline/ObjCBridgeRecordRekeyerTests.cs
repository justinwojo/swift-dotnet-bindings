// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using BindingsGeneration.ObjC;
using Xunit;

namespace BindingsGeneration.Tests.ObjCTests;

/// <summary>
/// Tests for <see cref="ObjCBridgeRecordRekeyer"/> — the production correction that reconciles a bridge
/// record's Swift key with the name a Swift member actually references. The factory keys by the raw ObjC
/// name (on real input the clang JSON AST can't supply NS_SWIFT_NAME), so a renamed type would be
/// registered under the wrong key and never resolve; the rekeyer applies the authoritative
/// <c>rawObjCName → swiftImportName</c> map the Swift ABI parse harvests. These assert the rename,
/// no-rename, unmapped-fallback, and foreign-module-anchoring behaviors on both class and enum records.
/// </summary>
public class ObjCBridgeRecordRekeyerTests
{
    private const string Module = "FBSDKCoreKit";
    private const string Namespace = "FacebookLogin.Binding";

    /// <summary>
    /// A record as the factory produces it on real input: keyed by the raw ObjC name (SwiftName null),
    /// with the C# projection carrying that same raw name — the field the rekeyer reads to look up the map.
    /// </summary>
    private static TypeRecord RawKeyedRecord(string rawObjCName, TypeRecordKind kind = TypeRecordKind.Class)
        => new()
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName(Namespace, rawObjCName),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{Module}.{rawObjCName}"),
            MetadataAccessor = string.Empty,
            Flags = kind == TypeRecordKind.Class
                ? TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement
                : TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum,
            Kind = kind,
        };

    [Fact]
    public void Rekey_RenamesToSwiftImportName_WhenMapped()
    {
        // FBSDKAccessToken carries NS_SWIFT_NAME(AccessToken): a Swift member references
        // FBSDKCoreKit.AccessToken, so the record must be re-keyed to that name to resolve.
        var records = new[] { RawKeyedRecord("FBSDKAccessToken") };
        var map = new Dictionary<string, string> { ["FBSDKAccessToken"] = "AccessToken" };

        var record = Assert.Single(ObjCBridgeRecordRekeyer.Rekey(records, Module, map));

        Assert.Equal("FBSDKCoreKit.AccessToken", record.SwiftTypeName.ModuleQualifiedName);
        Assert.Equal("AccessToken", record.SwiftTypeName.Name);
        Assert.Equal(Module, record.SwiftTypeName.Module);
    }

    [Fact]
    public void Rekey_PreservesCSharpProjectionAndKindAndFlags()
    {
        // Re-keying touches ONLY the Swift resolution key — the C# projection (the companion's raw
        // `partial interface` name) and the marshalling classification must survive untouched, or the
        // resolved reference points at a type the companion never emitted / marshals the wrong way.
        var original = RawKeyedRecord("FBSDKAccessToken");
        var map = new Dictionary<string, string> { ["FBSDKAccessToken"] = "AccessToken" };

        var record = Assert.Single(ObjCBridgeRecordRekeyer.Rekey(new[] { original }, Module, map));

        Assert.Equal("FacebookLogin.Binding.FBSDKAccessToken", record.CSharpTypeName.FullyQualifiedName);
        Assert.Equal(TypeRecordKind.Class, record.Kind);
        Assert.Equal(original.Flags, record.Flags);
    }

    [Fact]
    public void Rekey_IsNoOp_WhenSwiftNameEqualsRawName()
    {
        // MLevel-style: raw ObjC name == Swift import name (no rename). The map still carries the entry,
        // and the re-keyed name must equal the raw name — and the record instance is returned unchanged.
        var original = RawKeyedRecord("MLevel", TypeRecordKind.Enum);
        var map = new Dictionary<string, string> { ["MLevel"] = "MLevel" };

        var record = Assert.Single(ObjCBridgeRecordRekeyer.Rekey(new[] { original }, Module, map));

        Assert.Equal("FBSDKCoreKit.MLevel", record.SwiftTypeName.ModuleQualifiedName);
        Assert.Same(original, record); // key already correct → same reference, no needless clone
    }

    [Fact]
    public void Rekey_FallsBackToExistingName_WhenUnmapped()
    {
        // The ObjC type isn't referenced by this framework's own Swift ABI, so the map has no entry.
        // The record keeps its existing (raw) Swift name — the honest near-term behavior; a downstream
        // module referencing such a renamed-but-unreferenced type is the documented Phase-2 gap.
        var records = new[] { RawKeyedRecord("FBSDKProfile") };
        var map = new Dictionary<string, string>(); // empty

        var record = Assert.Single(ObjCBridgeRecordRekeyer.Rekey(records, Module, map));

        Assert.Equal("FBSDKCoreKit.FBSDKProfile", record.SwiftTypeName.ModuleQualifiedName);
    }

    [Fact]
    public void Rekey_RenamesEnumRecords_TheSameAsClasses()
    {
        // The rename path is kind-agnostic: an NS_ENUM renamed via NS_SWIFT_NAME re-keys identically.
        var records = new[] { RawKeyedRecord("FBSDKLoginBehavior", TypeRecordKind.Enum) };
        var map = new Dictionary<string, string> { ["FBSDKLoginBehavior"] = "LoginBehavior" };

        var record = Assert.Single(ObjCBridgeRecordRekeyer.Rekey(records, Module, map));

        Assert.Equal("FBSDKCoreKit.LoginBehavior", record.SwiftTypeName.ModuleQualifiedName);
        Assert.Equal(TypeRecordKind.Enum, record.Kind);
    }

    [Fact]
    public void Rekey_AnchorsForeignModuleRecordToModuleName()
    {
        // A record whose factory key landed in a different module (e.g. a modulemap module name that
        // differs from the Swift ABI's) is re-anchored to the authoritative moduleName, keeping it
        // coherent with the database it is registered into — even in the unmapped fallback.
        var foreign = new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName(Namespace, "FBSDKProfile"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("SomeOtherModule.FBSDKProfile"),
            MetadataAccessor = string.Empty,
            Flags = TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class,
        };

        var record = Assert.Single(ObjCBridgeRecordRekeyer.Rekey(new[] { foreign }, Module, new Dictionary<string, string>()));

        Assert.Equal("FBSDKCoreKit.FBSDKProfile", record.SwiftTypeName.ModuleQualifiedName);
    }

    [Fact]
    public void Rekey_TypedEnumRecord_ReadsRawNameFromSwiftKey_NotCSharpProjection()
    {
        // An NS_TYPED_ENUM record projects to Foundation.NSString, so its CSharpTypeName.Name is
        // "NSString" — NOT the typedef's own name. The raw ObjC name lives ONLY on the pre-rekey Swift
        // key (SwiftTypeName.Name == "FBSDKLoginAuthType"). The rekeyer must read the raw name from
        // there to hit the ABI map; reading CSharpTypeName.Name would look up "NSString", miss, and
        // leave the record stuck under the raw key (unresolvable by a Swift member naming LoginAuthType).
        var typedEnum = new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSString"),
            NativeTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSString"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{Module}.FBSDKLoginAuthType"),
            MetadataAccessor = string.Empty,
            Flags = TypeRecordFlags.ObjCBridgeable,
            Kind = TypeRecordKind.Struct,
        };
        var map = new Dictionary<string, string> { ["FBSDKLoginAuthType"] = "LoginAuthType" };

        var record = Assert.Single(ObjCBridgeRecordRekeyer.Rekey(new[] { typedEnum }, Module, map));

        Assert.Equal("FBSDKCoreKit.LoginAuthType", record.SwiftTypeName.ModuleQualifiedName);
        Assert.Equal("LoginAuthType", record.SwiftTypeName.Name);
        // The C# projection and ObjC-bridge classification survive the re-key untouched.
        Assert.Equal("Foundation.NSString", record.CSharpTypeName.FullyQualifiedName);
        Assert.True(record.Flags.HasFlag(TypeRecordFlags.ObjCBridgeable));
    }

    [Fact]
    public void Rekey_MapsEachRecordIndependently()
    {
        var records = new[]
        {
            RawKeyedRecord("FBSDKAccessToken"),
            RawKeyedRecord("FBSDKProfile"),                                  // unmapped → fallback
            RawKeyedRecord("FBSDKLoginBehavior", TypeRecordKind.Enum),
        };
        var map = new Dictionary<string, string>
        {
            ["FBSDKAccessToken"] = "AccessToken",
            ["FBSDKLoginBehavior"] = "LoginBehavior",
        };

        var result = ObjCBridgeRecordRekeyer.Rekey(records, Module, map);

        Assert.Equal(3, result.Count);
        Assert.Contains(result, r => r.SwiftTypeName.ModuleQualifiedName == "FBSDKCoreKit.AccessToken");
        Assert.Contains(result, r => r.SwiftTypeName.ModuleQualifiedName == "FBSDKCoreKit.FBSDKProfile");
        Assert.Contains(result, r => r.SwiftTypeName.ModuleQualifiedName == "FBSDKCoreKit.LoginBehavior");
    }
}
