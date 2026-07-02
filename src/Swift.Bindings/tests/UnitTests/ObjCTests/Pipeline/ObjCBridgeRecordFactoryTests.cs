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
    public void NsOptionsEnum_IsExcluded()
    {
        // NS_OPTIONS imports as an OptionSet struct, not a C# enum — Phase 1 does not bridge it.
        var module = ObjCModuleBuilder.Create(Module)
            .WithEnum("FBSDKLoginError", e => e.Options().UnderlyingType("NSUInteger").Case("a"))
            .Build();

        Assert.Empty(ObjCBridgeRecordFactory.CreateRecords(module, Module, Namespace, Logger));
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

    // --- Mixed / empty modules ---

    [Fact]
    public void MixedModule_ProducesClassAndEnumRecords_SkipsOptions()
    {
        var module = ObjCModuleBuilder.Create(Module)
            .WithClass("FBSDKAccessToken", configure: c => c.SwiftName("AccessToken"))
            .WithClass("FBSDKProfile")
            .WithEnum("FBSDKLoginBehavior", e => e.Case("a"))
            .WithEnum("FBSDKLoginError", e => e.Options().Case("b"))
            .Build();

        var records = ObjCBridgeRecordFactory.CreateRecords(module, Module, Namespace, Logger);

        Assert.Equal(3, records.Count); // 2 classes + 1 NS_ENUM; NS_OPTIONS excluded
        Assert.Equal(2, records.Count(r => r.Kind == TypeRecordKind.Class));
        Assert.Single(records, r => r.Kind == TypeRecordKind.Enum);
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
