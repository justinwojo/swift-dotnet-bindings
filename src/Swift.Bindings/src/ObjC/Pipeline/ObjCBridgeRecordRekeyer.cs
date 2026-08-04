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
    /// the unqualified part of its incoming Swift key (<see cref="TypeRecord.SwiftTypeName"/>'s
    /// <c>Name</c>): the factory stamps every record's pre-rekey Swift key with the raw ObjC
    /// declaration name, so this is the raw name across all record kinds. (Its C# projection name is
    /// NOT a reliable source — a typed-enum record projects to <c>Foundation.NSString</c>, whose
    /// <c>Name</c> is <c>NSString</c>, not the typedef's own name.) When
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
            var rawObjCName = record.SwiftTypeName.Name;
            var swiftName = objcImportedTypeNames.TryGetValue(rawObjCName, out var mapped)
                ? mapped
                : record.SwiftTypeName.Name;
            var key = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{swiftName}");

            // The companion now DECLARES a renamed type under its Swift-import name, so the record's
            // C# projection has to follow or a Swift member typed by this ObjC type resolves to a
            // C# name the companion never emitted (CS0246). Only a projection that is this type's own
            // companion declaration moves: a record projecting onto a foreign type (a typed-enum
            // typedef projects to Foundation.NSString) keeps its projection whatever the key does.
            var csharpName = record.CSharpTypeName;
            var objcRuntimeName = record.ObjCRuntimeName;
            if (!string.Equals(swiftName, rawObjCName, StringComparison.Ordinal)
                && string.Equals(csharpName.Name, rawObjCName, StringComparison.Ordinal))
            {
                // A class declaration is emitted under the .NET acronym spelling (MapClassName turns
                // NSURLBox into NSUrlBox); an enum is emitted verbatim. The projection has to follow
                // the same mapping the emitter will apply, or it names a type the companion never
                // declared — the very CS0246 this re-key exists to prevent.
                var declaredName = record.Kind == TypeRecordKind.Class
                    ? ObjCTypeMapper.MapClassName(swiftName)
                    : swiftName;
                csharpName = CSharpTypeName.FromNamespaceAndName(csharpName.Namespace, declaredName);
                // Moving the projection off the raw name erases the only place the record carried its
                // ObjC identity, which superclass resolution cross-checks against a Clang USR. Keep it.
                objcRuntimeName = rawObjCName;
            }

            var keyUnchanged = key.Equals(record.SwiftTypeName);
            var nameUnchanged = csharpName.Equals(record.CSharpTypeName);
            result.Add(keyUnchanged && nameUnchanged
                ? record
                : record with
                {
                    SwiftTypeName = key,
                    CSharpTypeName = csharpName,
                    ObjCRuntimeName = objcRuntimeName,
                });
        }
        return result;
    }
}
