// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration.ObjC;

/// <summary>
/// One accepted rename: the Objective-C declaration spelling and the Swift-import name the emitted
/// C# type takes instead.
/// </summary>
/// <param name="RawObjCName">The Objective-C declaration name (also the runtime registration name).</param>
/// <param name="SwiftImportName">The name Swift imports the type under, and the emitted C# type name.</param>
/// <param name="Kind">The declaration kind: <c>class</c>, <c>protocol</c>, or <c>enum</c>.</param>
public readonly record struct ObjCSwiftImportRename(string RawObjCName, string SwiftImportName, string Kind);

/// <summary>
/// Rewrites an <see cref="ObjCModule"/> so each type that Swift imports under a different name is
/// DECLARED in C# under that Swift-import name, while keeping its Objective-C runtime identity.
/// <para>
/// A mixed framework's ObjC half is the same API the framework's own Swift consumers see, and they
/// see it prefix-stripped or <c>NS_SWIFT_NAME</c>-renamed (<c>FBSDKAccessToken</c> is
/// <c>AccessToken</c> to Swift). The companion emitted the raw spelling, so the same type had two
/// names depending on which half of the binding you reached it through. This pass makes the emitted
/// name the Swift-import name; the raw name survives on <see cref="ObjCClassDecl.RawObjCName"/> and
/// friends, which is what the emitter writes into <c>[BaseType(…, Name = "…")]</c> /
/// <c>[Protocol(Name = "…")]</c>, so the ObjC runtime still registers the original class.
/// Selectors are untouched — they are not type names.
/// </para>
/// <para>
/// Coverage is partial BY CONSTRUCTION: the rename map comes from the Swift ABI, which can only name
/// a type its own members reference. A public ObjC type no Swift member mentions keeps its raw name.
/// A consequence worth stating plainly, because it is the one place this generator lets an upstream
/// change rename an existing member: when a later upstream release adds the FIRST Swift reference to
/// a previously unreferenced type, that type's C# name upgrades from raw to Swift-import. Persisting
/// the first-observed name instead would freeze the raw spelling forever and defeat the point of the
/// rename, so the upgrade is accepted and every applied rename is reported for review.
/// </para>
/// </summary>
public static class ObjCSwiftImportNameRewriter
{
    /// <summary>
    /// Returns <paramref name="module"/> with every eligible declaration renamed to its Swift-import
    /// name and every reference to it updated, plus the list of renames applied. Returns the input
    /// module unchanged (and an empty list) when nothing is eligible.
    /// </summary>
    /// <param name="module">The filtered module, immediately before emission.</param>
    /// <param name="objcImportedTypeNames">The <c>rawObjCName → swiftImportName</c> mapping the Swift
    /// ABI parse harvested (<c>SwiftABIParser.ObjCImportedTypeNames</c>). Empty on a pure-ObjC
    /// binding, where there is no Swift ABI to read import names from.</param>
    /// <param name="logger">Diagnostics sink.</param>
    public static (ObjCModule Module, IReadOnlyList<ObjCSwiftImportRename> Renames) Rewrite(
        ObjCModule module,
        IReadOnlyDictionary<string, string>? objcImportedTypeNames,
        ILogger logger)
    {
        var renames = BuildRenameMap(module, objcImportedTypeNames, reservedName: null, logger, out var accepted);
        if (renames.Count == 0)
            return (module, []);

        string Rename(string name) => renames.TryGetValue(name, out var mapped) ? mapped : name;

        var rewritten = module with
        {
            Classes = module.Classes.ConvertAll(c => c with
            {
                Name = Rename(c.Name),
                RawObjCName = renames.ContainsKey(c.Name) ? c.Name : c.RawObjCName,
                SuperclassName = c.SuperclassName == null ? null : Rename(c.SuperclassName),
                ProtocolNames = c.ProtocolNames.ConvertAll(Rename),
                Methods = c.Methods.ConvertAll(m => RewriteMethod(m, Rename)),
                Properties = c.Properties.ConvertAll(p => RewriteProperty(p, Rename)),
            }),
            Protocols = module.Protocols.ConvertAll(p => p with
            {
                Name = Rename(p.Name),
                RawObjCName = renames.ContainsKey(p.Name) ? p.Name : p.RawObjCName,
                InheritedProtocolNames = p.InheritedProtocolNames.ConvertAll(Rename),
                Methods = p.Methods.ConvertAll(m => RewriteMethod(m, Rename)),
                Properties = p.Properties.ConvertAll(pr => RewriteProperty(pr, Rename)),
            }),
            Enums = module.Enums.ConvertAll(e => e with
            {
                Name = Rename(e.Name),
                RawObjCName = renames.ContainsKey(e.Name) ? e.Name : e.RawObjCName,
                UnderlyingType = e.UnderlyingType == null ? null : RewriteTypeRef(e.UnderlyingType, Rename),
            }),
            Structs = module.Structs.ConvertAll(s => s with
            {
                Fields = s.Fields.ConvertAll(f => f with { Type = RewriteTypeRef(f.Type, Rename) }),
            }),
            Functions = module.Functions.ConvertAll(f => f with
            {
                ReturnType = RewriteTypeRef(f.ReturnType, Rename),
                Parameters = f.Parameters.ConvertAll(p => p with { Type = RewriteTypeRef(p.Type, Rename) }),
            }),
            Constants = module.Constants.ConvertAll(c => c with { Type = RewriteTypeRef(c.Type, Rename) }),
            Typedefs = module.Typedefs.ConvertAll(t => t with
            {
                UnderlyingType = RewriteTypeRef(t.UnderlyingType, Rename),
            }),
            ResolutionTypedefs = module.ResolutionTypedefs?.ConvertAll(t => t with
            {
                UnderlyingType = RewriteTypeRef(t.UnderlyingType, Rename),
            }),
            Categories = module.Categories.ConvertAll(cat => cat with
            {
                ClassName = Rename(cat.ClassName),
                ProtocolNames = cat.ProtocolNames.ConvertAll(Rename),
                Methods = cat.Methods.ConvertAll(m => RewriteMethod(m, Rename)),
                Properties = cat.Properties.ConvertAll(p => RewriteProperty(p, Rename)),
            }),
        };

        foreach (var r in accepted)
            logger.LogInformation(
                "ObjC {Kind} '{Raw}' is imported into Swift as '{Swift}'; emitting the C# type under " +
                "the Swift-import name, with ObjC runtime registration kept on the raw name.",
                r.Kind, r.RawObjCName, r.SwiftImportName);

        return (rewritten, accepted);
    }

    /// <summary>
    /// The vetted subset of <paramref name="objcImportedTypeNames"/> — the renames this pass would
    /// actually apply to <paramref name="module"/>.
    /// <para>
    /// This exists because the rename has TWO appliers that must agree: this rewriter (which renames
    /// the companion's DECLARATION) and <see cref="ObjCBridgeRecordRekeyer"/> (which moves the Swift
    /// side's reference to that declaration). If the rewriter declines a rename on a collision and the
    /// rekeyer applies it anyway, a Swift member resolves to a C# name the companion never emitted
    /// (CS0246) — or worse, to an unrelated type that happens to own that name. So acceptance is
    /// decided ONCE, here, against the module as parsed, and both appliers consume the result. Vetting
    /// before rather than after the emission filters is deliberate: filters only ever REMOVE
    /// declarations, so a rename that clears this check still clears it against the filtered module.
    /// </para>
    /// </summary>
    /// <param name="module">The module as parsed, before the emission filters.</param>
    /// <param name="objcImportedTypeNames">The raw <c>rawObjCName → swiftImportName</c> ABI mapping.</param>
    /// <param name="reservedName">A name the module's C# surface already spends outside its type
    /// declarations — the resolved namespace, which a same-named type would make ambiguous (CS0426).</param>
    /// <param name="logger">Diagnostics sink.</param>
    public static IReadOnlyDictionary<string, string> AcceptRenames(
        ObjCModule module,
        IReadOnlyDictionary<string, string>? objcImportedTypeNames,
        string? reservedName,
        ILogger logger)
    {
        var map = BuildRenameMap(module, objcImportedTypeNames, reservedName, logger, out _);
        if (objcImportedTypeNames == null || objcImportedTypeNames.Count == 0)
            return map;

        // The vet above covers DECLARATION renames — the shapes this rewriter itself applies. An
        // entry whose raw name is not declared here as a class, protocol or enum has no companion
        // declaration to rename, so the rewriter can never disagree with the rekeyer about it: the
        // rekeyer is its only applier. It must pass through unvetted. A typed-enum typedef
        // (NS_TYPED_EXTENSIBLE_ENUM under NS_SWIFT_NAME) reaches the Swift side only through its
        // bridge record, whose projection is Foundation.NSString — a foreign type the rekey never
        // moves — and dropping the entry leaves that record keyed by the raw ObjC name, so every
        // Swift member typed by the Swift-import name degrades to a placeholder and is skipped.
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in module.Classes) declared.Add(c.Name);
        foreach (var p in module.Protocols) declared.Add(p.Name);
        foreach (var e in module.Enums) declared.Add(e.Name);

        foreach (var (rawName, swiftName) in objcImportedTypeNames)
        {
            if (!declared.Contains(rawName)
                && !string.Equals(rawName, swiftName, StringComparison.Ordinal))
                map[rawName] = swiftName;
        }
        return map;
    }

    /// <summary>
    /// The subset of <paramref name="objcImportedTypeNames"/> that is safe to apply: the raw name is
    /// declared in this module as a class, protocol or enum; the Swift-import name actually differs;
    /// and the new name does not collide with any other name in the module's post-rename type
    /// namespace. A collision is DECLINED rather than disambiguated — two C# types of one name is a
    /// hard compile failure, and the raw name is always a correct fallback.
    /// </summary>
    static Dictionary<string, string> BuildRenameMap(
        ObjCModule module,
        IReadOnlyDictionary<string, string>? objcImportedTypeNames,
        string? reservedName,
        ILogger logger,
        out IReadOnlyList<ObjCSwiftImportRename> accepted)
    {
        accepted = [];
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (objcImportedTypeNames == null || objcImportedTypeNames.Count == 0)
            return map;

        var kinds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in module.Classes) kinds.TryAdd(c.Name, "class");
        foreach (var p in module.Protocols) kinds.TryAdd(p.Name, "protocol");
        foreach (var e in module.Enums) kinds.TryAdd(e.Name, "enum");

        // Every name that will exist after the rewrite. Seeded with every declared type name in the
        // module (including kinds this pass never renames, e.g. structs and typedefs) so a rename
        // can't land on one of them either — and with the two type names the emitters synthesize per
        // module rather than read off a declaration, which a rename would otherwise be free to land on.
        // Classes and protocols are seeded from their own lists rather than from `kinds`, which keeps
        // only the first spelling: a name declared as BOTH must reserve both kinds' emitted spellings.
        var occupied = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var c in module.Classes) Occupy(occupied, c.Name, "class");
        foreach (var p in module.Protocols) Occupy(occupied, p.Name, "protocol");
        foreach (var e in module.Enums) Occupy(occupied, e.Name, "enum");
        foreach (var s in module.Structs) Occupy(occupied, s.Name, "struct");
        foreach (var t in module.Typedefs) Occupy(occupied, t.Name, "typedef");
        Occupy(occupied, ObjCConstantsEmitter.ConstantsTypeName(module.ModuleName), "class");
        Occupy(occupied, $"{module.ModuleName}Functions", "class");
        if (!string.IsNullOrEmpty(reservedName))
            Occupy(occupied, reservedName, "class");

        // Deterministic order: the input map's enumeration order is not contractual, and two raw
        // names could target the same Swift name. Ordering by raw name makes which one wins (and
        // which is declined) a property of the source, not of dictionary internals.
        var acceptedList = new List<ObjCSwiftImportRename>();
        foreach (var rawName in objcImportedTypeNames.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var swiftName = objcImportedTypeNames[rawName];
            if (!kinds.TryGetValue(rawName, out var kind))
                continue;
            if (string.Equals(rawName, swiftName, StringComparison.Ordinal))
                continue;
            if (IsOccupied(occupied, swiftName, kind))
            {
                logger.LogInformation(
                    "ObjC {Kind} '{Raw}' imports into Swift as '{Swift}', but that name is already " +
                    "taken in this module — keeping the Objective-C name for the C# type.",
                    kind, rawName, swiftName);
                continue;
            }

            map[rawName] = swiftName;
            Vacate(occupied, rawName, kind);
            Occupy(occupied, swiftName, kind);
            acceptedList.Add(new ObjCSwiftImportRename(rawName, swiftName, kind));
        }

        accepted = acceptedList;
        return map;
    }

    /// <summary>
    /// Every top-level C# name a declaration of this spelling and kind can take. The declaration
    /// spelling alone is not enough on either axis: the acronym convention folds <c>NSURLThing</c> and
    /// <c>NSUrlThing</c> onto one emitted name, and a protocol additionally emits a forward
    /// <c>I{Name}</c> interface plus, when a class and a protocol share a spelling, the
    /// <c>{Name}Protocol</c> disambiguation (and its own <c>I</c> form). The clash forms are reserved
    /// whether or not the clash fires in this module today, because an accepted rename can create or
    /// dissolve one; holding them costs at most a declined rename (the raw name is always a correct
    /// fallback), while releasing one risks two declarations of a single C# name.
    /// </summary>
    static IEnumerable<string> ProjectedNames(string name, string kind)
    {
        var isProtocol = kind == "protocol";
        var mapped = isProtocol ? ObjCTypeMapper.MapProtocolName(name) : ObjCTypeMapper.MapClassName(name);
        yield return name;
        yield return mapped;
        if (!isProtocol)
            yield break;
        yield return $"I{mapped}";
        yield return $"{mapped}Protocol";
        yield return $"I{mapped}Protocol";
    }

    // Occupancy is a COUNT, not a set: two declarations can project onto one C# spelling, so the
    // release a renamed declaration performs must not drop the claim its namesake still holds.
    static void Occupy(Dictionary<string, int> occupied, string name, string kind)
    {
        foreach (var projected in ProjectedNames(name, kind).Distinct(StringComparer.Ordinal))
            occupied[projected] = occupied.TryGetValue(projected, out var count) ? count + 1 : 1;
    }

    static void Vacate(Dictionary<string, int> occupied, string name, string kind)
    {
        foreach (var projected in ProjectedNames(name, kind).Distinct(StringComparer.Ordinal))
        {
            if (!occupied.TryGetValue(projected, out var count))
                continue;
            if (count <= 1)
                occupied.Remove(projected);
            else
                occupied[projected] = count - 1;
        }
    }

    static bool IsOccupied(Dictionary<string, int> occupied, string name, string kind)
        => ProjectedNames(name, kind).Any(occupied.ContainsKey);

    static ObjCMethodDecl RewriteMethod(ObjCMethodDecl method, Func<string, string> rename) => method with
    {
        ReturnType = RewriteTypeRef(method.ReturnType, rename),
        Parameters = method.Parameters.ConvertAll(p => p with { Type = RewriteTypeRef(p.Type, rename) }),
    };

    static ObjCPropertyDecl RewriteProperty(ObjCPropertyDecl property, Func<string, string> rename) =>
        property with { Type = RewriteTypeRef(property.Type, rename) };

    /// <summary>
    /// Rewrites every type name reachable from <paramref name="typeRef"/> — the reference itself, its
    /// pointee, block signature, generic arguments and protocol qualifications. Missing any one arm
    /// leaves a member typed by a name no longer declared (CS0246), which is why this walks the whole
    /// shape rather than only the top-level name.
    /// </summary>
    static ObjCTypeRef RewriteTypeRef(ObjCTypeRef typeRef, Func<string, string> rename) => typeRef with
    {
        Name = rename(typeRef.Name),
        PointeeType = typeRef.PointeeType == null ? null : RewriteTypeRef(typeRef.PointeeType, rename),
        BlockParams = typeRef.BlockParams.ConvertAll(p => RewriteTypeRef(p, rename)),
        BlockReturnType = typeRef.BlockReturnType == null ? null : RewriteTypeRef(typeRef.BlockReturnType, rename),
        GenericArgs = typeRef.GenericArgs.ConvertAll(g => RewriteTypeRef(g, rename)),
        ProtocolQualifications = typeRef.ProtocolQualifications.ConvertAll(n => rename(n)),
    };
}
