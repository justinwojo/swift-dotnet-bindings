// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using BindingsGeneration;

namespace BindingsGeneration.ObjC;

/// <summary>
/// Re-keys the ObjC type-resolution records produced by <see cref="ObjCBridgeRecordFactory"/> from
/// the raw Objective-C declaration name to the Swift-import name a Swift member actually references.
/// <para>
/// The factory keys each record by the only name the ObjC side reliably knows — the raw ObjC name
/// (e.g. <c>FBSDKAccessToken</c>). But a Swift member names the type by its <em>Swift-import</em> name
/// (e.g. <c>FBSDKCoreKit.AccessToken</c>), which the Swift compiler derives from an explicit
/// <c>NS_SWIFT_NAME</c> or automatic prefix stripping — a mapping Clang's <c>-ast-dump=json</c> does
/// not expose (it omits the <c>SwiftNameAttr</c> string argument). The authoritative source is the
/// Swift ABI itself: every ObjC-imported type reference carries the Swift name in <c>printedName</c>
/// and the raw ObjC identity in its Clang <c>usr</c>. <c>SwiftABIParser.ObjCImportedTypeNames</c>
/// harvests that <c>rawObjCName → swiftImportName</c> mapping during the parse; this re-keyer applies
/// it so each record resolves under the name Swift resolution looks up.
/// </para>
/// </summary>
public static class ObjCBridgeRecordRekeyer
{
    /// <summary>
    /// Returns <paramref name="records"/> with each <see cref="TypeRecord.SwiftTypeName"/> re-keyed to
    /// <c>{<paramref name="moduleName"/>}.{swiftImportName}</c>. The record's raw ObjC name is read from
    /// its C# projection name (the companion emits <c>partial interface {rawName}</c> /
    /// <c>enum {rawName}</c> verbatim, so <see cref="TypeRecord.CSharpTypeName"/>'s <c>Name</c> is the
    /// raw ObjC name regardless of any name the factory stamped on the Swift key). When
    /// <paramref name="objcImportedTypeNames"/> has no entry for that raw name — the ObjC type is not
    /// referenced by this framework's own Swift ABI, so the ABI cannot tell us its Swift-import name —
    /// the record keeps its existing (raw) Swift name, anchored to <paramref name="moduleName"/>. A
    /// downstream module referencing such an unreferenced, renamed ObjC type is a known Phase-2 gap.
    /// </summary>
    /// <param name="records">Records from <see cref="ObjCBridgeRecordFactory.CreateRecords"/>.</param>
    /// <param name="moduleName">The authoritative module name from the Swift parse — every re-keyed
    /// record's module component, keeping it coherent with the database it is registered into.</param>
    /// <param name="objcImportedTypeNames">The <c>rawObjCName → swiftImportName</c> mapping harvested
    /// from the Swift ABI (<c>SwiftABIParser.ObjCImportedTypeNames</c>).</param>
    public static IReadOnlyList<TypeRecord> Rekey(
        IReadOnlyList<TypeRecord> records,
        string moduleName,
        IReadOnlyDictionary<string, string> objcImportedTypeNames)
    {
        var result = new List<TypeRecord>(records.Count);
        foreach (var record in records)
        {
            var rawObjCName = record.CSharpTypeName.Name;
            var swiftName = objcImportedTypeNames.TryGetValue(rawObjCName, out var mapped)
                ? mapped
                : record.SwiftTypeName.Name;
            var key = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{swiftName}");
            result.Add(key.Equals(record.SwiftTypeName) ? record : record with { SwiftTypeName = key });
        }
        return result;
    }
}
