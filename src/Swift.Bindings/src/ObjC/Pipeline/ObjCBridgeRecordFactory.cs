// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using BindingsGeneration;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration.ObjC;

/// <summary>
/// Synthesizes <see cref="TypeRecord"/>s from a parsed <see cref="ObjCModule"/> so the Swift half of
/// a mixed (ObjC + Swift) binding can resolve references to ObjC-defined types. Without this bridge,
/// a Swift member that names an ObjC type (e.g. <c>FBSDKCoreKit.AccessToken</c>, bound on the ObjC
/// side as <c>partial interface FBSDKAccessToken</c>) has no <see cref="TypeRecord"/> to resolve
/// against — Swift resolution can't see the ObjC binding — so the member degrades to
/// <c>Swift.AnyType</c>/<c>object</c> or is dropped. Registering these records into the module's OWN
/// <c>ModuleTypeDatabase</c> before Swift resolution runs lets the existing marshalling pipeline
/// (<c>ObjCBridgedProjection</c> for classes, <c>SimpleEnumProjection</c> for NS_ENUM) handle them
/// with no new marshaler code.
/// <para>
/// The Swift-facing key is <c>{module}.{SwiftName ?? Name}</c> — the ObjC declaration's
/// <c>NS_SWIFT_NAME</c> when present (that is exactly the identifier a Swift member uses), else the
/// raw ObjC name. The C# projection is <c>{resolvedNamespace}.{Name}</c> — the RAW ObjC name, because
/// the companion emitters declare <c>partial interface {cls.Name}</c> /
/// <c>public enum {enumDecl.Name}</c> verbatim (NOT a remapped name); the namespace is the one
/// <see cref="ObjCPipeline.Parse"/> resolved, threaded through so a record and its companion type
/// share one namespace exactly.
/// </para>
/// <para>
/// A subtlety the <c>SwiftName ?? Name</c> key hides: on real input <see cref="ObjCClassDecl.SwiftName"/>
/// is almost always null. Clang's <c>-ast-dump=json</c> (this generator's AST source) emits the
/// <c>SwiftNameAttr</c> node but OMITS its string argument, so the parser cannot recover an explicit
/// <c>NS_SWIFT_NAME</c> — and it cannot model automatic prefix stripping at all. This factory therefore
/// keys by the raw ObjC name in practice, and <see cref="ObjCBridgeRecordRekeyer"/> re-keys each record
/// to the correct Swift-import name using the authoritative <c>rawObjCName → swiftImportName</c> map the
/// Swift ABI parse harvests (<c>SwiftABIParser.ObjCImportedTypeNames</c>). The <c>SwiftName</c>-aware
/// keying here still stands for any path that can supply the name directly (and keeps this factory
/// independently testable).
/// </para>
/// <para>
/// Handles ObjC classes (→ <see cref="TypeRecordKind.Class"/>, ObjCBridged), NS_ENUM
/// (→ <see cref="TypeRecordKind.Enum"/>, SimpleEnum), and NS_TYPED_ENUM /
/// NS_TYPED_EXTENSIBLE_ENUM over an NSString base (→ <see cref="TypeRecordKind.Struct"/>,
/// ObjCBridgeable). A typed enum such as <c>typedef NSString *FBSDKLoginAuthType
/// NS_TYPED_EXTENSIBLE_ENUM</c> imports into Swift as an <c>_ObjectiveCBridgeable</c> value-type
/// newtype wrapper backed by an NSString, so it marshals through the same whole-object /
/// whole-container ObjC bridge as <c>Foundation.URL ↔ NSURL</c>: the C# projection is
/// <c>Foundation.NSString</c> and the record carries a matching <c>NativeTypeName</c> so
/// <see cref="ITypeProjection"/> selection lands on <c>ObjCBridgeableProjection</c>. NS_OPTIONS
/// bitmasks bridge as SimpleEnum records too (their C# companion is the <c>[Flags]</c> enum
/// <see cref="StructsAndEnumsEmitter"/> already emits, so the raw-value round-trip is identical to
/// NS_ENUM) but additionally carry <see cref="TypeRecordFlags.OptionSet"/>: an NS_OPTIONS imports
/// into Swift as an OptionSet struct whose <c>init(rawValue:)</c> is non-failable, so the flag steers
/// the <c>@_cdecl</c> reconstruction away from the failable <c>guard let</c> form real enums use.
/// ObjC protocols remain out of scope.
/// </para>
/// </summary>
public static class ObjCBridgeRecordFactory
{
    /// <summary>
    /// Builds the bridge records for <paramref name="module"/>. The caller registers each returned
    /// record into the target module's <c>ModuleTypeDatabase</c> under its
    /// <see cref="TypeRecord.SwiftTypeName"/> with a Swift-wins conflict policy
    /// (<c>ConflictPolicy.KeepExisting</c>) — a Swift-owned type of the same name always wins, so
    /// these records only fill the gaps Swift resolution can't.
    /// </summary>
    /// <param name="module">The eligibility-filtered module from <see cref="ObjCPipeline.Parse"/>.</param>
    /// <param name="moduleName">The Swift/ObjC module name (e.g. <c>FBSDKCoreKit</c>) — the module
    /// component of every synthesized <see cref="SwiftTypeName"/>.</param>
    /// <param name="resolvedNamespace">The companion's resolved C# namespace, from
    /// <see cref="ObjCPipeline.Parse"/>.</param>
    /// <param name="logger">Diagnostics sink.</param>
    public static IReadOnlyList<TypeRecord> CreateRecords(
        ObjCModule module, string moduleName, string resolvedNamespace, ILogger logger)
    {
        var records = new List<TypeRecord>();
        var typedefMap = ObjCTypeMapper.BuildResolvedTypedefMap(module);

        // ObjC classes → ObjCBridged class records. The existing ObjCBridgedProjection marshals
        // these as an object pointer (IntPtr in P/Invoke, .Handle extraction in wrappers), the
        // same path Apple-SDK ObjC classes take.
        foreach (var cls in module.Classes)
        {
            var key = SwiftTypeName.FromModuleQualifiedName(
                $"{moduleName}.{SwiftFacingName(cls.SwiftName, cls.Name)}");
            records.Add(new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(resolvedNamespace, cls.Name),
                SwiftTypeName = key,
                MetadataAccessor = string.Empty,
                Flags = TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class,
            });
        }

        // NS_ENUM and NS_OPTIONS → SimpleEnum records. Both import into Swift as a RawRepresentable
        // value type reconstructed via `Type(rawValue:)`, and both share the same C# companion shape:
        // the plain / `[Flags]` enum StructsAndEnumsEmitter already emits. So SimpleEnumProjection (a
        // raw-value cast) marshals either with no new marshaler code. The only difference is on the
        // Swift side: an NS_OPTIONS bitmask imports as an OptionSet struct whose `init(rawValue:)` is
        // NON-failable, so its record additionally carries TypeRecordFlags.OptionSet to steer the
        // @_cdecl reconstruction to the direct (non-`guard let`) form — see CdeclParamMapper.
        // The raw value type must be the Swift raw type the imported type actually declares, because
        // the generic SimpleEnum param path reconstructs via `Type(rawValue:)` and the @_cdecl
        // wrapper's parameter is typed by RawValueTypeName — a mismatch won't compile. Two cases:
        //  - Native-width (NSInteger/NSUInteger, and the no-explicit-base default) imports into Swift
        //    as `Int`/`UInt`, NOT `Int64`/`UInt64`, even though both share the same 64-bit C# width.
        //    Swift's synthesized initializer is `init(rawValue: Int)`, so the raw type must be the
        //    platform-width scalar `Int`/`UInt` or `X(rawValue:)` in the wrapper fails to bind.
        //  - Fixed-width (int32_t, uint8_t, …) keeps its exact scalar spelling.
        // Either way the C# underlying width still round-trips: EnumHandler.GetCSharpEnumUnderlyingType
        // maps both "Int" and "Int64" to "long" (and "UInt"/"UInt64" to "ulong"), so the width the C#
        // side casts to and the scalar the wrapper receives agree by construction. The `Type(rawValue:)`
        // reconstruction is always available (RawRepresentable NS_ENUM / OptionSet NS_OPTIONS alike),
        // so no per-case data is needed here.
        foreach (var enumDecl in module.Enums)
        {
            var (companionBase, isNativeWidth) = StructsAndEnumsEmitter.ResolveEnumBackingType(enumDecl, typedefMap);
            var rawValueType = isNativeWidth
                ? (companionBase == "ulong" ? "UInt" : "Int")
                : EnumHandler.GetSwiftScalarType(companionBase);
            var key = SwiftTypeName.FromModuleQualifiedName(
                $"{moduleName}.{SwiftFacingName(enumDecl.SwiftName, enumDecl.Name)}");
            var flags = TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum;
            if (enumDecl.IsOptions)
                flags |= TypeRecordFlags.OptionSet;
            records.Add(new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(resolvedNamespace, enumDecl.Name),
                SwiftTypeName = key,
                MetadataAccessor = string.Empty,
                Flags = flags,
                Kind = TypeRecordKind.Enum,
                RawValueTypeName = rawValueType,
            });
        }

        // NS_TYPED_ENUM / NS_TYPED_EXTENSIBLE_ENUM over an NSString base → ObjCBridgeable struct
        // records. Clang lowers these to a typedef carrying the swift_wrapper attribute
        // (IsSwiftNewType); Swift imports each as an _ObjectiveCBridgeable value-type newtype backed
        // by an NSString. Marshalling reuses the URL↔NSURL path: the public C# projection and the ABI
        // carrier are both Foundation.NSString, and NativeTypeName being set routes selection to
        // ObjCBridgeableProjection (whole-object pointer bridge for scalar/optional positions,
        // whole-container NSArray/NSSet/NSDictionary bridge for collection positions). Only the
        // NSString-backed shape is bridged here — a typed enum over a non-object base (e.g. a typedef
        // over a numeric type marked NS_TYPED_ENUM) has no _ObjectiveCBridgeable import and is skipped.
        // The record is keyed by the RAW ObjC typedef name; ObjCBridgeRecordRekeyer re-keys it to the
        // Swift-import name (e.g. FBSDKLoginAuthType → LoginAuthType) using the ABI-harvested map.
        var typedefCount = 0;
        foreach (var td in module.Typedefs)
        {
            if (!td.IsSwiftNewType || !ResolvesToNSStringPointer(td.UnderlyingType, typedefMap))
                continue;

            var key = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{td.Name}");
            var nsString = CSharpTypeName.FromNamespaceAndName("Foundation", "NSString");
            records.Add(new TypeRecord
            {
                CSharpTypeName = nsString,
                NativeTypeName = nsString,
                SwiftTypeName = key,
                MetadataAccessor = string.Empty,
                Flags = TypeRecordFlags.ObjCBridgeable,
                Kind = TypeRecordKind.Struct,
            });
            typedefCount++;
        }

        if (records.Count > 0)
        {
            logger.LogInformation(
                "Mixed bridge: synthesized {Count} ObjC type-resolution record(s) for module '{Module}' " +
                "({ClassCount} class(es), {EnumCount} enum(s), {TypedEnumCount} typed enum(s)).",
                records.Count, moduleName,
                module.Classes.Count, module.Enums.Count, typedefCount);
        }

        return records;
    }

    /// <summary>
    /// The identifier a Swift member uses to name this ObjC type: the explicit NS_SWIFT_NAME when
    /// present, else the raw ObjC name.
    /// </summary>
    private static string SwiftFacingName(string? swiftName, string objcName)
        => string.IsNullOrEmpty(swiftName) ? objcName : swiftName!;

    /// <summary>
    /// True when <paramref name="type"/> is <c>NSString *</c> directly or resolves to it through the
    /// module's typedef chain (<paramref name="typedefMap"/> already collapses chains to their leaf).
    /// Gates typed-enum bridging: only an NSString-backed NS_TYPED_ENUM imports into Swift as an
    /// <c>_ObjectiveCBridgeable</c> newtype and can cross the boundary as an NSString pointer.
    /// </summary>
    private static bool ResolvesToNSStringPointer(ObjCTypeRef type, Dictionary<string, ObjCTypeRef> typedefMap)
    {
        if (type is { Name: "NSString", IsPointer: true })
            return true;

        if (typedefMap.TryGetValue(type.Name, out var resolved))
        {
            if (resolved is { Name: "NSString", IsPointer: true })
                return true;
            if (resolved.Name == "NSString" && type.IsPointer)
                return true;
        }

        return false;
    }
}
