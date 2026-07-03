// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Diagnostics.CodeAnalysis;
using BindingsGeneration.ObjC;
using Xunit;
using static BindingsGeneration.Tests.ObjCTests.ObjCTestHelpers;

namespace BindingsGeneration.Tests.ObjCTests;

/// <summary>
/// Tests for <see cref="ObjCBridgeRecordFactory"/> — the mixed-binding type-resolution bridge that
/// synthesizes <see cref="TypeRecord"/>s from a parsed <see cref="ObjCModule"/> so the Swift half of
/// a mixed binding can resolve references to ObjC-defined types. These assert the record contract
/// (Swift-facing key, shared companion namespace, kind/flag fidelity, enum-width round-trip) and,
/// crucially, that a synthesized record flows through the existing marshalling classifier
/// (<see cref="MarshallingHelpers.IsOptionalObjCBridged"/>) with no new marshaler code — the "zero
/// new marshaler code" premise of the design.
/// </summary>
public class ObjCBridgeRecordFactoryTests
{
    private const string Module = "FBSDKCoreKit";
    private const string Namespace = "FacebookLogin.Binding";

    // --- Class records ---

    [Fact]
    public void ClassRecord_KeyedByNsSwiftName_WhenPresent()
    {
        // NS_SWIFT_NAME(AccessToken) on FBSDKAccessToken: a Swift member names the type
        // `FBSDKCoreKit.AccessToken`, so THAT must be the Swift-facing resolution key.
        var module = ObjCModuleBuilder.Create(Module)
            .WithClass("FBSDKAccessToken", configure: c => c.SwiftName("AccessToken"))
            .Build();

        var record = Assert.Single(ObjCBridgeRecordFactory.CreateRecords(module, Module, Namespace, Logger));

        Assert.Equal("FBSDKCoreKit.AccessToken", record.SwiftTypeName.ModuleQualifiedName);
        Assert.Equal("FBSDKCoreKit", record.SwiftTypeName.Module);
        Assert.Equal("AccessToken", record.SwiftTypeName.Name);
    }

    [Fact]
    public void ClassRecord_KeyedByRawObjCName_WhenNoSwiftName()
    {
        var module = ObjCModuleBuilder.Create(Module)
            .WithClass("FBSDKSettings")
            .Build();

        var record = Assert.Single(ObjCBridgeRecordFactory.CreateRecords(module, Module, Namespace, Logger));

        Assert.Equal("FBSDKCoreKit.FBSDKSettings", record.SwiftTypeName.ModuleQualifiedName);
    }

    [Fact]
    public void ClassRecord_CSharpNameIsRawObjCNameInResolvedNamespace()
    {
        // The companion emits `partial interface FBSDKAccessToken` verbatim (NOT the SwiftName),
        // so the C# projection must be the raw ObjC name — and it must live in the SAME namespace
        // the companion is emitted into, or the resolved reference points at a type that isn't there.
        var module = ObjCModuleBuilder.Create(Module)
            .WithClass("FBSDKAccessToken", configure: c => c.SwiftName("AccessToken"))
            .Build();

        var record = Assert.Single(ObjCBridgeRecordFactory.CreateRecords(module, Module, Namespace, Logger));

        Assert.Equal(Namespace, record.CSharpTypeName.Namespace);
        Assert.Equal("FBSDKAccessToken", record.CSharpTypeName.Name);
        Assert.Equal("FacebookLogin.Binding.FBSDKAccessToken", record.CSharpTypeName.FullyQualifiedName);
    }

    [Fact]
    public void ClassRecord_IsObjCBridgedClassRequiringMemoryManagement()
    {
        var module = ObjCModuleBuilder.Create(Module)
            .WithClass("FBSDKAccessToken", configure: c => c.SwiftName("AccessToken"))
            .Build();

        var record = Assert.Single(ObjCBridgeRecordFactory.CreateRecords(module, Module, Namespace, Logger));

        Assert.Equal(TypeRecordKind.Class, record.Kind);
        Assert.True(MarshallingHelpers.IsObjCBridged(record));
        Assert.True(record.Flags.HasFlag(TypeRecordFlags.RequiresMemoryManagement));
    }

    // --- Enum records ---

    [Fact]
    public void EnumRecord_IsSimpleEnum()
    {
        var module = ObjCModuleBuilder.Create(Module)
            .WithEnum("FBSDKLoginBehavior", e => e.SwiftName("LoginBehavior").Case("browser").Case("systemAccount"))
            .Build();

        var record = Assert.Single(ObjCBridgeRecordFactory.CreateRecords(module, Module, Namespace, Logger));

        Assert.Equal("FBSDKCoreKit.LoginBehavior", record.SwiftTypeName.ModuleQualifiedName);
        Assert.Equal("FBSDKLoginBehavior", record.CSharpTypeName.Name);
        Assert.Equal(TypeRecordKind.Enum, record.Kind);
        Assert.True(record.Flags.HasFlag(TypeRecordFlags.SimpleEnum));
        Assert.False(MarshallingHelpers.IsObjCBridged(record));
    }

    [Fact]
    public void NsOptionsEnum_BridgesAsSimpleEnumWithOptionSetFlag()
    {
        // NS_OPTIONS imports as an OptionSet struct whose init(rawValue:) is non-failable. It bridges
        // as a SimpleEnum record (the C# companion is the [Flags] enum StructsAndEnumsEmitter emits),
        // but ADDITIONALLY carries the OptionSet flag so the @_cdecl reconstruction skips the failable
        // `guard let` form (see CdeclParamMapper). A plain NS_ENUM must NOT carry the flag.
        var module = ObjCModuleBuilder.Create(Module)
            .WithEnum("FBSDKShareBridgeOptions", e => e.Options().SwiftName("ShareBridgeOptions").UnderlyingType("NSUInteger").Case("photoAsset").Case("videoData"))
            .Build();

        var record = Assert.Single(ObjCBridgeRecordFactory.CreateRecords(module, Module, Namespace, Logger));

        Assert.Equal("FBSDKCoreKit.ShareBridgeOptions", record.SwiftTypeName.ModuleQualifiedName);
        Assert.Equal("FBSDKShareBridgeOptions", record.CSharpTypeName.Name);
        Assert.Equal(TypeRecordKind.Enum, record.Kind);
        Assert.True(record.Flags.HasFlag(TypeRecordFlags.SimpleEnum));
        Assert.True(record.Flags.HasFlag(TypeRecordFlags.OptionSet));
        // NSUInteger native-width unsigned → the Swift OptionSet's RawValue is UInt.
        Assert.Equal("UInt", record.RawValueTypeName);
        Assert.False(MarshallingHelpers.IsObjCBridged(record));
    }

    [Fact]
    public void NsEnum_DoesNotCarryOptionSetFlag()
    {
        // Guard against over-application: a plain (non-options) NS_ENUM must never get the OptionSet
        // flag, or its @_cdecl reconstruction would drop the failable guard a RawRepresentable needs.
        var module = ObjCModuleBuilder.Create(Module)
            .WithEnum("FBSDKLoginBehavior", e => e.Case("browser").Case("systemAccount"))
            .Build();

        var record = Assert.Single(ObjCBridgeRecordFactory.CreateRecords(module, Module, Namespace, Logger));

        Assert.True(record.Flags.HasFlag(TypeRecordFlags.SimpleEnum));
        Assert.False(record.Flags.HasFlag(TypeRecordFlags.OptionSet));
    }

    // The enum-width contract has TWO halves the factory must satisfy at once:
    //  (1) the raw value type round-trips through EnumHandler.GetCSharpEnumUnderlyingType back to the
    //      SAME C# base the companion enum declares (StructsAndEnumsEmitter.ResolveEnumBackingType) —
    //      else SimpleEnumProjection casts to a different width than the @_cdecl wrapper receives; and
    //  (2) the raw value type is the exact Swift RAW-TYPE SPELLING the imported enum declares. This is
    //      NOT determined by C# width alone: a native-width NS_ENUM (NSInteger/NSUInteger) imports into
    //      Swift as `Int`/`UInt`, whereas a fixed-width `int64_t`/`uint64_t` imports as `Int64`/`UInt64`
    //      — SAME C# `long`/`ulong`, DIFFERENT Swift raw. The wrapper reconstructs the case via
    //      `Type(rawValue:)`, so stamping `Int64` where Swift declares `init(rawValue: Int)` won't
    //      compile. The NSInteger-vs-int64_t rows below lock exactly that distinction.
    [Theory]
    [InlineData(null, "long", "Int")]      // NS_ENUM with no explicit base defaults to native-width signed
    [InlineData("NSInteger", "long", "Int")]
    [InlineData("NSUInteger", "ulong", "UInt")]
    [InlineData("int32_t", "int", "Int32")]
    [InlineData("uint32_t", "uint", "UInt32")]
    [InlineData("int8_t", "sbyte", "Int8")]
    [InlineData("uint8_t", "byte", "UInt8")]
    [InlineData("int16_t", "short", "Int16")]
    [InlineData("uint16_t", "ushort", "UInt16")]
    [InlineData("int64_t", "long", "Int64")]
    [InlineData("uint64_t", "ulong", "UInt64")]
    public void EnumRecord_RawValueWidthRoundTripsToCompanionBaseType(
        string? underlyingType, string expectedCompanionBase, string expectedSwiftRaw)
    {
        var enumBuilder = ObjCModuleBuilder.Create(Module);
        enumBuilder.WithEnum("FBSDKSomeEnum", e =>
        {
            e.Case("first");
            if (underlyingType != null)
                e.UnderlyingType(underlyingType);
        });
        var module = enumBuilder.Build();
        var enumDecl = Assert.Single(module.Enums);

        // What the companion enum will actually declare as its C# base type.
        var (companionBase, _) = StructsAndEnumsEmitter.ResolveEnumBackingType(enumDecl, typedefMap: null);
        Assert.Equal(expectedCompanionBase, companionBase);

        var record = Assert.Single(ObjCBridgeRecordFactory.CreateRecords(module, Module, Namespace, Logger));

        // (2) The stamped raw type is the exact Swift spelling the imported enum declares — native
        // width → Int/UInt, fixed width → Int32/Int64/… . This is what the @_cdecl wrapper's
        // `Type(rawValue:)` parameter must match.
        Assert.Equal(expectedSwiftRaw, record.RawValueTypeName);

        // (1) …and it still maps back to exactly the companion's declared C# base width (Int and Int64
        // both → long, UInt and UInt64 both → ulong), so the marshalled call frame stays consistent.
        Assert.Equal(companionBase, EnumHandler.GetCSharpEnumUnderlyingType(record.RawValueTypeName));
    }

    // --- NS_TYPED_ENUM / NS_TYPED_EXTENSIBLE_ENUM records ---

    /// <summary>
    /// Builds an ObjC typedef as the parser produces it for
    /// <c>typedef NSString *Name NS_TYPED_[EXTENSIBLE_]ENUM</c>: an NSString-pointer underlying type
    /// carrying the swift_wrapper attribute (IsSwiftNewType).
    /// </summary>
    private static ObjCTypedefDecl SwiftNewTypeString(string name) => new()
    {
        Name = name,
        UnderlyingType = new ObjCTypeRef { Name = "NSString", IsPointer = true },
        IsSwiftNewType = true,
    };

    [Fact]
    public void TypedEnumRecord_OverNSString_IsObjCBridgeableStruct()
    {
        // typedef NSString *FBSDKLoginAuthType NS_TYPED_EXTENSIBLE_ENUM. It imports into Swift as an
        // _ObjectiveCBridgeable value-type newtype backed by an NSString, so it must marshal through
        // the same URL↔NSURL ObjC bridge: an ObjCBridgeable struct record projecting to NSString.
        var module = ObjCModuleBuilder.Create(Module)
            .WithTypedef(SwiftNewTypeString("FBSDKLoginAuthType"))
            .Build();

        var record = Assert.Single(ObjCBridgeRecordFactory.CreateRecords(module, Module, Namespace, Logger));

        Assert.Equal(TypeRecordKind.Struct, record.Kind);
        Assert.True(record.Flags.HasFlag(TypeRecordFlags.ObjCBridgeable));
        // NOT the ObjCBridged (class-pointer) classifier — that would pick ObjCBridgedProjection,
        // whose container carrier is a raw IntPtr set (SwiftSet<IntPtr>), not the whole-NSSet bridge.
        Assert.False(MarshallingHelpers.IsObjCBridged(record));
        Assert.Equal("Foundation.NSString", record.CSharpTypeName.FullyQualifiedName);
        Assert.NotNull(record.NativeTypeName);
        Assert.Equal("Foundation.NSString", record.NativeTypeName!.FullyQualifiedName);
    }

    [Fact]
    public void TypedEnumRecord_KeyedByRawObjCName()
    {
        // The Swift-import name (NS_SWIFT_NAME(LoginAuthType)) isn't recoverable from the clang JSON
        // AST, so the factory keys by the RAW typedef name and ObjCBridgeRecordRekeyer remaps it later.
        // The rekeyer reads the raw name from SwiftTypeName.Name (CSharpTypeName is Foundation.NSString),
        // so the raw name MUST live on the Swift key.
        var module = ObjCModuleBuilder.Create(Module)
            .WithTypedef(SwiftNewTypeString("FBSDKLoginAuthType"))
            .Build();

        var record = Assert.Single(ObjCBridgeRecordFactory.CreateRecords(module, Module, Namespace, Logger));

        Assert.Equal("FBSDKCoreKit.FBSDKLoginAuthType", record.SwiftTypeName.ModuleQualifiedName);
        Assert.Equal("FBSDKLoginAuthType", record.SwiftTypeName.Name);
    }

    [Fact]
    public void TypedEnumRecord_ResolvesThroughTypedefChain()
    {
        // typedef NSString *Base;  typedef Base Alias NS_TYPED_ENUM;
        // BuildResolvedTypedefMap collapses the chain to NSString*, so the NSString-backing gate on the
        // swift_wrapper typedef still fires even when the base is one hop away.
        var module = ObjCModuleBuilder.Create(Module)
            .WithTypedef(new ObjCTypedefDecl
            {
                Name = "BaseString",
                UnderlyingType = new ObjCTypeRef { Name = "NSString", IsPointer = true },
            })
            .WithTypedef(new ObjCTypedefDecl
            {
                Name = "AliasedAuthType",
                UnderlyingType = new ObjCTypeRef { Name = "BaseString", IsPointer = true },
                IsSwiftNewType = true,
            })
            .Build();

        var record = Assert.Single(ObjCBridgeRecordFactory.CreateRecords(module, Module, Namespace, Logger));

        Assert.Equal("FBSDKCoreKit.AliasedAuthType", record.SwiftTypeName.ModuleQualifiedName);
        Assert.True(record.Flags.HasFlag(TypeRecordFlags.ObjCBridgeable));
    }

    [Fact]
    public void PlainNSStringTypedef_WithoutSwiftNewType_IsSkipped()
    {
        // A plain `typedef NSString *SerialNumber` (no NS_TYPED_ENUM) is NOT a bridgeable newtype —
        // it does not import as an _ObjectiveCBridgeable struct, so no record is synthesized.
        var module = ObjCModuleBuilder.Create(Module)
            .WithTypedef(new ObjCTypedefDecl
            {
                Name = "SerialNumber",
                UnderlyingType = new ObjCTypeRef { Name = "NSString", IsPointer = true },
                IsSwiftNewType = false,
            })
            .Build();

        Assert.Empty(ObjCBridgeRecordFactory.CreateRecords(module, Module, Namespace, Logger));
    }

    [Fact]
    public void TypedEnum_OverNonNSStringBase_IsSkipped()
    {
        // NS_TYPED_ENUM over a non-object base (e.g. an NSNumber-backed or numeric typedef) has no
        // NSString bridge; Phase-1 bridges only the NSString-backed shape. Skip rather than mis-marshal.
        var module = ObjCModuleBuilder.Create(Module)
            .WithTypedef(new ObjCTypedefDecl
            {
                Name = "SomeNumberKind",
                UnderlyingType = new ObjCTypeRef { Name = "NSNumber", IsPointer = true },
                IsSwiftNewType = true,
            })
            .Build();

        Assert.Empty(ObjCBridgeRecordFactory.CreateRecords(module, Module, Namespace, Logger));
    }

    // --- Mixed / empty modules ---

    [Fact]
    public void MixedModule_ProducesClassAndEnumRecords_IncludingOptions()
    {
        var module = ObjCModuleBuilder.Create(Module)
            .WithClass("FBSDKAccessToken", configure: c => c.SwiftName("AccessToken"))
            .WithClass("FBSDKProfile")
            .WithEnum("FBSDKLoginBehavior", e => e.Case("a"))
            .WithEnum("FBSDKLoginError", e => e.Options().Case("b"))
            .Build();

        var records = ObjCBridgeRecordFactory.CreateRecords(module, Module, Namespace, Logger);

        Assert.Equal(4, records.Count); // 2 classes + 1 NS_ENUM + 1 NS_OPTIONS
        Assert.Equal(2, records.Count(r => r.Kind == TypeRecordKind.Class));
        Assert.Equal(2, records.Count(r => r.Kind == TypeRecordKind.Enum));
        // Exactly the NS_OPTIONS record carries the OptionSet flag.
        Assert.Single(records, r => r.Flags.HasFlag(TypeRecordFlags.OptionSet));
    }

    [Fact]
    public void EmptyModule_ProducesNoRecords()
    {
        var module = ObjCModuleBuilder.Create(Module).Build();
        Assert.Empty(ObjCBridgeRecordFactory.CreateRecords(module, Module, Namespace, Logger));
    }

    // --- Downstream parity: the synthesized record drives the existing classifier ---

    [Fact]
    public void SynthesizedClassRecord_MakesOptionalResolveAsObjCBridged()
    {
        // The whole point of the bridge: a Swift member typed Optional<FBSDKCoreKit.AccessToken>.
        // FBSDKCoreKit is NOT an Apple fallback module and "AccessToken" has no ObjC class prefix,
        // so WITHOUT a database record IsOptionalObjCBridged returns false (the member degrades).
        // Registering the factory's record flips it to true through the existing DB branch — no new
        // marshaling code, exactly as the design promises.
        var module = ObjCModuleBuilder.Create(Module)
            .WithClass("FBSDKAccessToken", configure: c => c.SwiftName("AccessToken"))
            .Build();
        var records = ObjCBridgeRecordFactory.CreateRecords(module, Module, Namespace, Logger);

        var optional = TypeSpecParser.Parse("Swift.Optional<FBSDKCoreKit.AccessToken>");

        Assert.False(MarshallingHelpers.IsOptionalObjCBridged(optional, new StubTypeDatabase()));
        Assert.True(MarshallingHelpers.IsOptionalObjCBridged(optional, new StubTypeDatabase(records)));
    }

    /// <summary>
    /// Minimal ITypeDatabase seeded from a set of bridge records, keyed by their
    /// module-qualified Swift name — the same lookup shape production uses after registration.
    /// </summary>
    private sealed class StubTypeDatabase : ITypeDatabase
    {
        private readonly Dictionary<string, TypeRecord> _types;

        public StubTypeDatabase(IEnumerable<TypeRecord>? records = null)
            => _types = (records ?? []).ToDictionary(r => r.SwiftTypeName.ModuleQualifiedName);

        public string? AsyncLibraryName => null;
        public bool IsTypeProcessed(SwiftTypeName s) => _types.ContainsKey(s.ModuleQualifiedName);
        public bool TryGetTypeRecord(SwiftTypeName s, [NotNullWhen(true)] out TypeRecord? r) => _types.TryGetValue(s.ModuleQualifiedName, out r);
        public string GetLibraryPath(string m) => "";
        public void UpdateTypeRecord(SwiftTypeName n, TypeRecord r) { }
    }
}
