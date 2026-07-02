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
/// Phase 1 handles ObjC classes (→ <see cref="TypeRecordKind.Class"/>, ObjCBridged) and NS_ENUM
/// (→ <see cref="TypeRecordKind.Enum"/>, SimpleEnum). NS_OPTIONS (imports as an OptionSet struct,
/// not an enum), ObjC protocols, and NS_TYPED_ENUM are out of Phase-1 scope.
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

        // NS_ENUM → SimpleEnum records. NS_OPTIONS (OptionSet struct) is excluded — it is not a
        // C# enum. The raw value type must be the Swift raw type the imported enum actually declares,
        // because the generic SimpleEnum param path reconstructs the case via `Type(rawValue:)` and
        // the @_cdecl wrapper's parameter is typed by RawValueTypeName — a mismatch won't compile.
        // Two distinct cases:
        //  - Native-width NS_ENUMs (NSInteger/NSUInteger, and the no-explicit-base default) import
        //    into Swift as `Int`/`UInt`, NOT `Int64`/`UInt64`, even though both share the same 64-bit
        //    C# width. Swift's synthesized initializer is `init(rawValue: Int)`, so the raw type must
        //    be the platform-width scalar `Int`/`UInt` or `X(rawValue:)` in the wrapper fails to bind.
        //  - Fixed-width NS_ENUMs (int32_t, uint8_t, …) keep their exact scalar spelling.
        // Either way the C# underlying width still round-trips: EnumHandler.GetCSharpEnumUnderlyingType
        // maps both "Int" and "Int64" to "long" (and "UInt"/"UInt64" to "ulong"), so the width the C#
        // side casts to and the scalar the wrapper receives agree by construction. The `Type(rawValue:)`
        // reconstruction is always available (every ObjC NS_ENUM imports as RawRepresentable), so no
        // per-enum case data is needed here.
        foreach (var enumDecl in module.Enums)
        {
            if (enumDecl.IsOptions)
                continue;

            var (companionBase, isNativeWidth) = StructsAndEnumsEmitter.ResolveEnumBackingType(enumDecl, typedefMap);
            var rawValueType = isNativeWidth
                ? (companionBase == "ulong" ? "UInt" : "Int")
                : EnumHandler.GetSwiftScalarType(companionBase);
            var key = SwiftTypeName.FromModuleQualifiedName(
                $"{moduleName}.{SwiftFacingName(enumDecl.SwiftName, enumDecl.Name)}");
            records.Add(new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(resolvedNamespace, enumDecl.Name),
                SwiftTypeName = key,
                MetadataAccessor = string.Empty,
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum,
                Kind = TypeRecordKind.Enum,
                RawValueTypeName = rawValueType,
            });
        }

        if (records.Count > 0)
        {
            logger.LogInformation(
                "Mixed bridge: synthesized {Count} ObjC type-resolution record(s) for module '{Module}' " +
                "({ClassCount} class(es), {EnumCount} enum(s)).",
                records.Count, moduleName,
                module.Classes.Count, module.Enums.Count(e => !e.IsOptions));
        }

        return records;
    }

    /// <summary>
    /// The identifier a Swift member uses to name this ObjC type: the explicit NS_SWIFT_NAME when
    /// present, else the raw ObjC name.
    /// </summary>
    private static string SwiftFacingName(string? swiftName, string objcName)
        => string.IsNullOrEmpty(swiftName) ? objcName : swiftName!;
}
