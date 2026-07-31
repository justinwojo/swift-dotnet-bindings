// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration.ObjC;

public static class ObjCTypeMapper
{
    static ObjCTypeMapper()
    {
        // Mirror ObjCUsingsEmitter's startup assert. The ObjC type-mapping tables this class
        // depends on (pointer/CoreFoundation-ref/primitive/value-type/system-struct/acronym)
        // now live in AppleFrameworkRegistry, loaded from the schema-versioned sibling file
        // objc-type-mappings.json. A failed embed/load would otherwise silently produce empty
        // maps and mis-map every ObjC type to a passthrough name, so fail loud at first touch.
        if (!AppleFrameworkRegistry.HasObjCTypeMappings)
            throw new InvalidOperationException(
                "ObjCTypeMapper requires the folded ObjC type-mapping tables owned by "
                + "AppleFrameworkRegistry (objc-type-mappings.json), but they report empty. "
                + "Ensure the data file is embedded and its schemaVersion matches "
                + $"AppleFrameworkRegistry.ExpectedObjCTypeMappingsSchemaVersion ({AppleFrameworkRegistry.ExpectedObjCTypeMappingsSchemaVersion}).");
    }

    public static string MapType(ObjCTypeRef typeRef, string? declaringClassName = null, HashSet<string>? genericTypeParams = null, Dictionary<string, ObjCTypeRef>? typedefMap = null, Dictionary<string, ObjCTypeRef>? blockTypedefMap = null, HashSet<string>? localProtocolNames = null, HashSet<string>? classProtocolClashNames = null, ICollection<string>? synthesizedProtocolInterfaces = null)
    {
        // 0a. C function pointers and anonymous records → IntPtr
        if (typeRef.IsFunctionPointer || typeRef.IsAnonymousRecord)
            return "IntPtr";

        // 0b. Fixed-size C array (e.g., uint8_t [4] → "byte[4]", NSString *[4] → "string[4]")
        // Must be checked before primitive mapping, which would discard the array size.
        if (typeRef.FixedArraySize is > 0)
        {
            var elementType = MapType(new ObjCTypeRef { Name = typeRef.Name, IsPointer = typeRef.IsPointer }, declaringClassName, genericTypeParams, typedefMap, localProtocolNames: localProtocolNames, classProtocolClashNames: classProtocolClashNames, synthesizedProtocolInterfaces: synthesizedProtocolInterfaces);
            return $"{elementType}[{typeRef.FixedArraySize}]";
        }

        // 1. Block types
        if (typeRef.IsBlock)
            return MapBlockType(typeRef, genericTypeParams, typedefMap, localProtocolNames, classProtocolClashNames, synthesizedProtocolInterfaces);

        // 2. instancetype
        if (typeRef.Name == "instancetype")
            return declaringClassName ?? "NSObject";

        // 3. Protocol-qualified id (id<Proto> or id<Proto1, Proto2>)
        if (typeRef.Name == "id" && typeRef.ProtocolQualifications.Count > 0)
        {
            // Multi-protocol: id<Proto1, Proto2> — use the first bindable protocol.
            // Filter out NSObject (implicit in ObjC) and NSFastEnumeration (no .NET binding).
            var protocols = typeRef.ProtocolQualifications
                .Where(p => p != "NSObject" && p != "NSFastEnumeration")
                .ToList();
            if (protocols.Count == 0)
                return "NSObject";
            // A protocol-typed member (parameter / return / property) binds to the protocol's
            // INTERFACE, `IFoo` — for an own protocol AND an SDK one. bgen generates the consumer
            // interface `IFoo` from the `[Protocol] interface Foo` declaration; binding a member to
            // the bare name instead makes bgen pick the generated Model CLASS (`Foo : NSObject`), so
            // a conforming subclass marshals through `GetNSObject<Foo>` and throws an
            // InvalidCastException at runtime. The api-definition contract compile (a plain csc pass
            // over ApiDefinition.cs, before bgen runs) has no bgen-generated `IFoo` in scope, so the
            // emitter writes an empty `interface IFoo {}` forward declaration per own protocol to
            // satisfy this reference (an SDK protocol's interface already ships in the platform
            // assembly). localProtocolNames is consulted only by the direct-protocol-name arm below.
            return RecordSynthesizedProtocolInterface(
                $"I{MapProtocolName(protocols[0], classProtocolClashNames)}", synthesizedProtocolInterfaces);
        }

        // 3b. Typed generic collections: NSArray<T> → T[], NSDictionary<K,V> → NSDictionary<K,V>
        if (typeRef.GenericArgs.Count > 0)
        {
            var mappedGeneric = MapGenericCollectionType(typeRef, declaringClassName, genericTypeParams, typedefMap, blockTypedefMap, localProtocolNames, classProtocolClashNames, synthesizedProtocolInterfaces);
            if (mappedGeneric != null)
                return mappedGeneric;
        }

        // 4. Known pointer types
        if (typeRef.IsPointer && AppleFrameworkRegistry.TryMapObjCPointerType(typeRef.Name, out var mapped))
            return mapped;

        // 5. Special non-pointer types
        if (typeRef.Name == "id")
            return "NSObject";
        if (typeRef.Name == "SEL")
            return "Selector";
        if (typeRef.Name == "Class")
            return "Class";

        // 6. Primitive types (void* → IntPtr, not "void")
        if (typeRef.Name == "void" && typeRef.IsPointer)
            return "IntPtr";
        if (AppleFrameworkRegistry.TryMapObjCPrimitiveType(typeRef.Name, out var primitive))
            return primitive;

        // 7. ObjC lightweight generic type parameters → NSObject
        // Only recognize params declared by the owning class via ObjCTypeParamDecl in the AST.
        // No hardcoded fallback set — avoids cross-type collisions where a generic param name
        // in one class matches a real type name used elsewhere.
        if (genericTypeParams != null && genericTypeParams.Contains(typeRef.Name))
            return "NSObject";

        // 8. CoreFoundation Ref types (typedefs for CF pointers, e.g., CGImageRef → CGImage)
        if (AppleFrameworkRegistry.TryMapCoreFoundationRefType(typeRef.Name, out var cfMapped))
            return cfMapped;

        // 8b. Apple SDK structs the platform assembly already declares keep their PUBLIC (typedef)
        // spelling. This has to run BEFORE the typedef hop below: Foundation spells `NSRange` as
        // `typedef struct _NSRange NSRange;`, and that typedef reaches us through the system-header
        // typedef set, so the hop would rewrite the member's type to the private record tag
        // `_NSRange` — a name Microsoft.iOS never declares, which then fails the resolvability gate
        // and silently drops every member taking one. The tag is an implementation detail of the
        // header; the name the platform binds, and the one a consumer writes, is the typedef.
        // Keyed on the registered system-struct set rather than a "strip a leading underscore"
        // rewrite so it can only ever fire on names we have explicitly claimed — an underscore strip
        // would also fire on an unrelated third-party `_Foo` and resolve it to a `Foo` that does not
        // exist. Deliberately NOT keyed on the broader objcValueTypes set: that one also carries
        // simd_*/MTL* names which de-sugar to scalars today, and claiming those would emit C# type
        // names with no declaration (CS0246 → whole-binding failure).
        if (AppleFrameworkRegistry.IsObjCSystemStruct(typeRef.Name))
            return ApplyDotNetAcronymConvention(typeRef.Name);

        // 8c. Apple SDK enums Microsoft.iOS already binds. Same ordering reason as 8b — an
        // NS_ENUM/NS_OPTIONS typedef is visible in the system-header typedef set, so without this
        // arm the member either de-sugars to the raw integer (losing the enum) or falls through to
        // the bare-name fallback, which the api-definition resolvability gate rejects: enums are not
        // in the Apple SDK type-name provenance set (that collects classes and protocols), so the
        // -fmodules prefix fallback never gets a chance to accept them. The table also carries the
        // acronym convention Microsoft.iOS applies (NSJSONReadingOptions → NSJsonReadingOptions),
        // which a mechanical rename cannot always reproduce (UIControlEvents → UIControlEvent).
        if (AppleFrameworkRegistry.TryMapObjCSystemEnum(typeRef.Name, out var systemEnum))
            return systemEnum;

        // 9. Typedef alias resolution (pre-resolved, single-hop lookup)
        if (typedefMap != null && typedefMap.TryGetValue(typeRef.Name, out var resolved))
        {
            // Preserve pointer from usage when the typedef itself is non-pointer.
            // e.g., typedef NSString BRAlias; usage: BRAlias * → should resolve to NSString *
            if (typeRef.IsPointer && !resolved.IsPointer)
            {
                var withPointer = new ObjCTypeRef
                {
                    Name = resolved.Name,
                    IsPointer = true,
                    Nullability = typeRef.Nullability,
                    ProtocolQualifications = [.. resolved.ProtocolQualifications],
                    GenericArgs = [.. resolved.GenericArgs],
                    BlockReturnType = resolved.BlockReturnType,
                    IsBlock = resolved.IsBlock,
                };
                withPointer.BlockParams.AddRange(resolved.BlockParams);
                return MapType(withPointer, declaringClassName, genericTypeParams, typedefMap: null, blockTypedefMap: blockTypedefMap, localProtocolNames: localProtocolNames, classProtocolClashNames: classProtocolClashNames, synthesizedProtocolInterfaces: synthesizedProtocolInterfaces);
            }
            return MapType(resolved, declaringClassName, genericTypeParams, typedefMap: null, blockTypedefMap: blockTypedefMap, localProtocolNames: localProtocolNames, classProtocolClashNames: classProtocolClashNames, synthesizedProtocolInterfaces: synthesizedProtocolInterfaces);
        }

        // 10. Block typedef name resolution (e.g., TypeNotificationBlock → Action<string, Type>)
        if (blockTypedefMap != null && blockTypedefMap.TryGetValue(typeRef.Name, out var blockResolved))
            return MapBlockType(blockResolved, genericTypeParams, typedefMap, localProtocolNames, classProtocolClashNames, synthesizedProtocolInterfaces);

        // 10b. A member typed directly by an own-protocol name (e.g. `MLNAnnotation *`, parsed
        // without an `id<…>` qualification) binds to the protocol INTERFACE, exactly like the id<>
        // form in step 3. Without this it falls through to the bare fallback below and bgen picks
        // the generated Model CLASS → the same conforming-subclass InvalidCastException. Resolved at
        // contract-compile time against the empty `interface IFoo {}` forward declaration the
        // emitter writes for each own protocol.
        if (localProtocolNames != null && localProtocolNames.Contains(typeRef.Name))
            return RecordSynthesizedProtocolInterface(
                $"I{MapProtocolName(typeRef.Name, classProtocolClashNames)}", synthesizedProtocolInterfaces);

        // 11. ObjC-to-.NET naming convention fallback (NS-prefix only)
        return ApplyDotNetAcronymConvention(typeRef.Name);
    }

    /// <summary>
    /// Records an <c>I</c>-prefixed name that <see cref="MapType"/> synthesized for a protocol
    /// reference, and returns it unchanged.
    /// <para>
    /// The <c>I</c> prefix is an emitter invention: a protocol-typed member binds to the protocol's
    /// INTERFACE (<c>IFoo</c>), not the bare name. That makes an emitted name ambiguous downstream —
    /// <c>ICMUserAttributes</c> is a third-party class whose real name starts with <c>I</c>, while
    /// <c>IUITableViewDelegate</c> is <c>UITableViewDelegate</c> with an <c>I</c> we added. The two
    /// are indistinguishable by spelling, so <see cref="IsApiDefinitionTypeResolvable"/> cannot
    /// safely strip a leading <c>I</c> without knowing which is which.
    /// </para>
    /// <para>
    /// Recording at the point of synthesis is what makes that knowledge exact. The alternative —
    /// re-deriving "would this name have been synthesized?" from a source name — cannot work: it
    /// has to reproduce acronym normalization (<c>NSURLSessionDelegate</c> →
    /// <c>INSUrlSessionDelegate</c>) and the class/protocol clash suffix (<c>Foo</c> →
    /// <c>IFooProtocol</c>), and it has no source name at all for <c>id&lt;Proto&gt;</c> (whose
    /// <c>ObjCTypeRef.Name</c> is literally <c>"id"</c>) or for protocol references nested inside
    /// block parameters and generic arguments. A sink threaded through this class's own recursion
    /// captures every position by construction and cannot drift from the mapping logic, because it
    /// IS the mapping logic.
    /// </para>
    /// </summary>
    private static string RecordSynthesizedProtocolInterface(
        string mappedName, ICollection<string>? synthesizedProtocolInterfaces)
    {
        synthesizedProtocolInterfaces?.Add(mappedName);
        return mappedName;
    }

    /// <summary>
    /// Maps an ObjC class name to its .NET MAUI binding name.
    /// Like MapProtocolName but for class names used in BaseType attributes.
    /// Applies acronym conventions without pointer type semantics
    /// (NSString stays NSString, not string).
    /// </summary>
    public static string MapClassName(string name)
    {
        // Check explicit pointer mappings for non-string types
        // (NSString → string is wrong for BaseType, keep NSString)
        if (AppleFrameworkRegistry.TryMapObjCPointerType(name, out var mapped) && mapped != "string" && mapped != "bool")
            return mapped;

        return ApplyDotNetAcronymConvention(name);
    }

    /// <summary>
    /// Maps an ObjC protocol name to its .NET MAUI binding convention name.
    /// E.g., NSURLSessionTaskDelegate → NSUrlSessionTaskDelegate, NSXPCListenerDelegate → NSXpcListenerDelegate.
    /// </summary>
    public static string MapProtocolName(string name) => ApplyDotNetAcronymConvention(name);

    /// <summary>
    /// Like <see cref="MapProtocolName(string)"/>, but applies the class/protocol disambiguation
    /// suffix. When an ObjC name exists as BOTH a class and a protocol in the same module, the two
    /// are distinct runtime entities that share a spelling; the class keeps the bare managed name
    /// (it carries the load-bearing superclass) and the protocol's managed interface is renamed
    /// <c>{Name}Protocol</c> (the canonical dotnet/macios convention, e.g. <c>NSAccessibilityElement</c>
    /// the class + <c>NSAccessibilityElementProtocol</c>). The native selector mapping is preserved
    /// separately via <c>[Protocol(Name = "...")]</c> on the renamed declaration.
    /// <paramref name="classProtocolClashNames"/> is the precomputed set of clashing names.
    /// </summary>
    public static string MapProtocolName(string name, HashSet<string>? classProtocolClashNames)
    {
        var mapped = ApplyDotNetAcronymConvention(name);
        return classProtocolClashNames != null && classProtocolClashNames.Contains(name)
            ? $"{mapped}Protocol"
            : mapped;
    }

    /// <summary>
    /// Applies .NET naming convention to ObjC type names: NSXPC* → NSXpc*, NSURL* → NSUrl*,
    /// etc. Only triggers on NS-prefixed names — other framework prefixes (CB, CG, AV, ...)
    /// keep their original casing because Microsoft.iOS does not apply the convention there.
    /// Acronym casing pairs live in AppleFrameworkRegistry (objc-type-mappings.json),
    /// ordered longer-first so substring overlaps (HTTPS contains HTTP) resolve correctly.
    /// </summary>
    internal static string ApplyDotNetAcronymConvention(string name)
    {
        if (!name.StartsWith("NS", StringComparison.Ordinal))
            return name;
        var result = name;
        foreach (var (objc, dn) in AppleFrameworkRegistry.ObjCAcronymConventions)
        {
            if (result.Contains(objc, StringComparison.Ordinal))
                result = result.Replace(objc, dn, StringComparison.Ordinal);
        }
        return result;
    }

    /// <summary>
    /// Reverses <see cref="ApplyDotNetAcronymConvention"/> for SDK-name lookups: takes a
    /// .NET-cased managed name and recovers the original ObjC acronym casing so the name
    /// can be looked up in <c>appleSdkTypeNames</c> (which stores raw clang AST names).
    /// </summary>
    internal static string ReverseDotNetAcronymConvention(string mappedName)
    {
        var result = mappedName;
        foreach (var (objc, dn) in AppleFrameworkRegistry.ObjCAcronymConventions)
        {
            if (result.Contains(dn, StringComparison.Ordinal))
                result = result.Replace(dn, objc, StringComparison.Ordinal);
        }
        return result;
    }

    /// <summary>
    /// Returns a formatted generic type hint for a type with GenericArgs, or null if none.
    /// E.g., NSArray&lt;NSString *&gt; → "Element type: string"
    ///       NSDictionary&lt;NSString *, NSNumber *&gt; → "Key type: string, Value type: NSNumber"
    /// </summary>
    public static string? FormatGenericTypeHint(ObjCTypeRef typeRef, string? declaringClassName = null, HashSet<string>? genericTypeParams = null, Dictionary<string, ObjCTypeRef>? typedefMap = null, Dictionary<string, ObjCTypeRef>? blockTypedefMap = null)
    {
        if (typeRef.GenericArgs.Count == 0)
            return null;

        // For generic type parameters (e.g., T in NSArray<T>), preserve the original name
        // instead of mapping to NSObject, since the hint is for human readability.
        var mappedArgs = typeRef.GenericArgs
            .Select(a =>
            {
                var mapped = genericTypeParams != null && genericTypeParams.Contains(a.Name)
                    ? a.Name
                    : MapType(a, declaringClassName, genericTypeParams, typedefMap, blockTypedefMap);
                if (a.Nullability == ObjCNullability.Nullable)
                    mapped += " (nullable)";
                else if (a.Nullability == ObjCNullability.Nonnull)
                    mapped += " (nonnull)";
                return mapped;
            })
            .ToList();

        return typeRef.Name switch
        {
            "NSArray" or "NSMutableArray" or "NSSet" or "NSMutableSet" or "NSOrderedSet" or "NSMutableOrderedSet"
                when mappedArgs.Count == 1 => $"Element type: {mappedArgs[0]}",
            "NSDictionary" or "NSMutableDictionary"
                when mappedArgs.Count == 2 => $"Key type: {mappedArgs[0]}, Value type: {mappedArgs[1]}",
            _ => $"Generic args: {string.Join(", ", mappedArgs)}"
        };
    }

    public static Dictionary<string, ObjCTypeRef> BuildBlockTypedefMap(ObjCModule module) =>
        module.Typedefs
            .Where(t => t.UnderlyingType.IsBlock)
            .ToDictionary(t => t.Name, t => t.UnderlyingType);

    public static Dictionary<string, ObjCTypeRef> BuildResolvedTypedefMap(ObjCModule module)
    {
        // Use ResolutionTypedefs (all headers) when available for broader typedef resolution.
        // This resolves external C types (e.g., nanopb pb_type_t → uint_least8_t → byte)
        // that are defined in included headers outside the framework's own Headers directory.
        var sourceTypedefs = module.ResolutionTypedefs ?? module.Typedefs;

        var raw = new Dictionary<string, ObjCTypeRef>();
        var structNames = new HashSet<string>(module.Structs.Select(s => s.Name));

        foreach (var t in sourceTypedefs)
        {
            // Skip block typedefs (emitted as delegates) and struct typedefs (emitted as structs)
            if (t.UnderlyingType.IsBlock || structNames.Contains(t.Name))
                continue;
            raw[t.Name] = t.UnderlyingType;
        }

        // Resolve chains: A → B → NSString* becomes A → NSString*
        var resolved = new Dictionary<string, ObjCTypeRef>();
        foreach (var (name, typeRef) in raw)
        {
            var current = typeRef;
            var visited = new HashSet<string> { name };
            while (raw.TryGetValue(current.Name, out var next) && visited.Add(current.Name))
                current = next;
            resolved[name] = current;
        }
        return resolved;
    }

    /// <summary>
    /// Returns the set of all C# type names that MapType can produce via its built-in mappings
    /// (primitives, pointer types, CoreFoundation refs). Used by emitters to detect unresolvable
    /// passthrough types that would cause compile errors.
    /// </summary>
    public static HashSet<string> BuildKnownMappedTypes()
    {
        var known = new HashSet<string>();
        foreach (var v in AppleFrameworkRegistry.ObjCPrimitiveTypeMappedValues) known.Add(v);
        foreach (var v in AppleFrameworkRegistry.ObjCPointerTypeMappedValues) known.Add(v);
        foreach (var v in AppleFrameworkRegistry.CoreFoundationRefTypeMappedValues) known.Add(v);
        // Types that MapType returns directly (not via dictionaries)
        known.Add("NSObject");
        known.Add("Selector");
        known.Add("Class");
        known.Add("NativeHandle");
        // Apple framework value types (CGPoint, CGRect, NSRange, etc.) — these are
        // recognized everywhere else in the type mapper as well-known Apple value
        // types but reach IsApiDefinitionTypeResolvable as raw passthrough names.
        // Without this, CoreGraphics/CoreLocation/etc. types get rejected under the
        // -fmodules fallback because their prefixes aren't registered as ObjC class
        // prefixes (those frameworks expose only C structs, not ObjC classes).
        foreach (var v in AppleFrameworkRegistry.ObjCValueTypeNames) known.Add(v);
        // The system-struct set is what MapType now claims by name (keeping the public typedef
        // spelling instead of hopping to the private record tag), so the same set has to be known
        // here or the claim would produce a name the resolvability gate still rejects. Almost all
        // of these are already covered by the value-type set above; seeding both keeps the claim
        // and the known-type set from drifting apart as either table grows.
        foreach (var v in AppleFrameworkRegistry.ObjCSystemStructNames) known.Add(v);
        // Apple SDK enums resolve through their framework `using` (Foundation/UIKit/CoreLocation/
        // CoreGraphics) exactly like the value types above, and are likewise invisible to the Apple
        // SDK type-name provenance set, which collects only classes and protocols.
        foreach (var v in AppleFrameworkRegistry.ObjCSystemEnumMappedValues) known.Add(v);
        return known;
    }

    /// <summary>
    /// Checks whether a mapped type name will be resolvable in StructsAndEnums.cs.
    /// <para>
    /// Uses a CamelCase heuristic: ObjC/Apple framework types and module-defined ObjC
    /// structs/enums start uppercase (<c>CGBitmapInfo</c>, <c>UIColor</c>, <c>NSCoder</c>,
    /// <c>SDImagePixelFormat</c>), while C-internal types are snake_case
    /// (<c>pb_wire_type_t</c>, <c>pb_size_t</c>). This is deliberately broader than
    /// <see cref="IsApiDefinitionTypeResolvable"/>: StructsAndEnums.cs <em>emits</em> the
    /// value-type definitions, so a module-local CamelCase struct is resolvable by being
    /// generated alongside, and an Apple value type resolves via its framework <c>using</c>.
    /// The known-Apple-class-prefix set is the wrong gate here — it catalogues ObjC <em>class</em>
    /// prefixes (NS, UI, AR, …) and omits value-type prefixes like <c>CG</c>/<c>CF</c> and every
    /// third-party module prefix, so keying on it would drop legitimate Apple value types
    /// (<c>CGBitmapInfo</c>) and module-defined structs.
    /// </para>
    /// <para>
    /// Per the F24 design review the casing decision keys on the retained <em>source ObjC
    /// identity</em> (<paramref name="sourceObjCName"/>) when threaded through, not the
    /// already-mapped text — the source name is the authoritative origin signal — falling back
    /// to <paramref name="mappedType"/> otherwise.
    /// </para>
    /// </summary>
    public static bool IsTypeResolvable(string mappedType, HashSet<string> knownTypes, string? sourceObjCName = null)
    {
        if (IsKnownMappedOrPatternType(mappedType, knownTypes)) return true;
        // ObjC/Apple framework + module-defined types are CamelCase; C-internal types are snake_case.
        var identity = !string.IsNullOrEmpty(sourceObjCName) ? sourceObjCName : mappedType;
        return identity.Length > 0 && char.IsUpper(identity[0]);
    }

    /// <summary>
    /// Checks whether a mapped type name will be resolvable in ApiDefinition.cs.
    /// Source-aware: uses Apple SDK type names collected during parsing to distinguish
    /// Apple framework types (available via .NET iOS bindings) from third-party types
    /// (not available). When <paramref name="appleSdkTypeNames"/> is null (e.g.
    /// <c>-fmodules</c> mode where SDK types are loaded from precompiled module files
    /// and never expanded into the AST), falls back to the known Apple framework ObjC
    /// class prefix list registered in <see cref="AppleFrameworkRegistry"/>. The bare
    /// "any uppercase letter" rule is too permissive there: under -fmodules a
    /// a cross-framework type referenced from a sibling xcframework
    /// would otherwise pass the check and produce CS0246 at compile time.
    /// <para>
    /// <paramref name="synthesizedProtocolInterfaces"/> is the set of <c>I</c>-prefixed names
    /// <see cref="MapType"/> synthesized while mapping the type(s) under test (see
    /// <see cref="RecordSynthesizedProtocolInterface"/>). It gates the leading-<c>I</c> strip: only
    /// an <c>I</c> the emitter itself added may be stripped. Without it the strip accepts any
    /// third-party name whose tail happens to start with an Apple prefix — <c>ICMUserAttributes</c>
    /// strips to <c>CMUserAttributes</c>, matches CoreMedia's <c>CM</c>, and is emitted as though it
    /// were an Apple type, producing CS0246 in the api-definition contract compile and failing the
    /// whole binding with SWIFTBIND113. That defeats the exact purpose of this fallback.
    /// </para>
    /// <para>
    /// The parameter is deliberately <em>required</em>. A call site that forgets it would silently
    /// change whether a member or a property name is reserved, diverging the emission decision from
    /// the dedup replay that mirrors it; making it required turns that mistake into a compile error.
    /// Pass an empty set when no protocol interface can be involved.
    /// </para>
    /// </summary>
    public static bool IsApiDefinitionTypeResolvable(string mappedType, HashSet<string> knownTypes, HashSet<string>? appleSdkTypeNames, IReadOnlySet<string> synthesizedProtocolInterfaces)
    {
        // A wrapper type (Action<…>, Func<…>, NSDictionary<K,V>, NSSet<T>, T[]) is itself a known
        // pattern, but an argument type INSIDE it can still be unresolvable — e.g. a cross-module
        // third-party class with no `using` and no local declaration nested in a block parameter
        // (Action<FBSDKAppLink, NSError>). Accepting the wrapper whole would emit a member whose
        // inner name fails CS0246 in the api-definition contract compile, so resolvability must
        // recurse into the arguments: every argument has to resolve too. Primitive and Apple
        // value-type element names (byte, string, CGRect, …) are in knownTypes, so legitimate
        // wrappers like byte[] and Action<CGRect> are unaffected; ObjC lightweight generic
        // parameters were already mapped to NSObject by MapType before reaching here.
        foreach (var argument in EnumerateTypeArguments(mappedType))
        {
            // The set covers the whole mapped type tree, so nested positions (block params,
            // block returns, generic args) carry their provenance down unchanged.
            if (!IsApiDefinitionTypeResolvable(argument, knownTypes, appleSdkTypeNames, synthesizedProtocolInterfaces))
                return false;
        }

        // Only an `I` this emitter synthesized for a protocol reference may be stripped; a leading
        // `I` that is part of the vendor's own type name must not be.
        var isSynthesizedProtocolInterface =
            mappedType.Length > 1 && mappedType[0] == 'I' && char.IsUpper(mappedType[1])
            && synthesizedProtocolInterfaces.Contains(mappedType);

        if (IsKnownMappedOrPatternType(mappedType, knownTypes)) return true;
        // Check Apple SDK types: classes and protocols declared in Apple SDK headers
        if (appleSdkTypeNames != null && appleSdkTypeNames.Count > 0)
        {
            if (ContainsAppleSdkType(appleSdkTypeNames, mappedType)) return true;
            // Protocol interfaces have I prefix (e.g., ICTTelephonyNetworkInfoDelegate → CTTelephonyNetworkInfoDelegate)
            if (isSynthesizedProtocolInterface
                && ContainsAppleSdkType(appleSdkTypeNames, mappedType[1..])) return true;
            return false;
        }
        // -fmodules fallback: only accept names whose head matches a known Apple
        // framework ObjC class prefix.
        if (mappedType.Length == 0 || !char.IsUpper(mappedType[0])) return false;
        if (AppleFrameworkRegistry.TypeNameStartsWithKnownObjCPrefix(mappedType)) return true;
        // Protocol interface form: an emitter-synthesized I on an Apple class prefix
        // (e.g. IUITableViewDelegate -> UITableViewDelegate). Note this branch is only reached
        // when the FULL name did not already match a prefix, so names like INSObjectProtocol
        // never arrive here — "IN" is Intents' own registered prefix and matches one line above.
        if (isSynthesizedProtocolInterface
            && AppleFrameworkRegistry.TypeNameStartsWithKnownObjCPrefix(mappedType[1..]))
            return true;
        return false;
    }

    /// <summary>
    /// Checks if a mapped C# type name exists in the Apple SDK type names set.
    /// The SDK set stores raw ObjC names (e.g., NSURLSessionDelegate, NSXPCConnection) but the
    /// mapped name uses .NET acronym convention (NSUrlSessionDelegate, NSXpcConnection), so we
    /// also try the reverse-renamed form.
    /// </summary>
    private static bool ContainsAppleSdkType(HashSet<string> appleSdkTypeNames, string mappedName)
    {
        if (appleSdkTypeNames.Contains(mappedName)) return true;
        var objcName = ReverseDotNetAcronymConvention(mappedName);
        if (!string.Equals(objcName, mappedName, StringComparison.Ordinal) && appleSdkTypeNames.Contains(objcName)) return true;
        return false;
    }

    /// <summary>
    /// Yields the top-level type arguments of a mapped C# type string so the resolvability gate can
    /// recurse into them: the element of an array (<c>T[]</c> / <c>T[16]</c> → <c>T</c>) or the
    /// generic arguments of a constructed type (<c>Action&lt;A, B&lt;C&gt;&gt;</c> → <c>A</c>,
    /// <c>B&lt;C&gt;</c>), split on top-level commas so a nested generic stays intact (the recursion
    /// descends into it). A non-generic, non-array name yields nothing. Each yielded substring is
    /// strictly shorter than the input, so the recursing caller terminates.
    /// </summary>
    private static IEnumerable<string> EnumerateTypeArguments(string mappedType)
    {
        if (string.IsNullOrEmpty(mappedType)) yield break;

        // Array element: "T[]" / "T[16]" → "T" (the element may itself be generic — recursion handles it).
        if (mappedType[^1] == ']')
        {
            var open = mappedType.LastIndexOf('[');
            if (open > 0)
                yield return mappedType[..open];
            yield break;
        }

        // Constructed generic: "Outer<A, B<C>>" → ["A", "B<C>"]; split on depth-0 commas only.
        if (mappedType[^1] == '>')
        {
            var lt = mappedType.IndexOf('<');
            if (lt <= 0) yield break;
            var inner = mappedType.Substring(lt + 1, mappedType.Length - lt - 2);
            var depth = 0;
            var start = 0;
            for (var i = 0; i < inner.Length; i++)
            {
                switch (inner[i])
                {
                    case '<': depth++; break;
                    case '>': depth--; break;
                    case ',' when depth == 0:
                        yield return inner[start..i].Trim();
                        start = i + 1;
                        break;
                }
            }
            yield return inner[start..].Trim();
        }
    }

    private static bool IsKnownMappedOrPatternType(string mappedType, HashSet<string> knownTypes)
    {
        if (knownTypes.Contains(mappedType)) return true;
        // Action/Func delegate types (from block mappings)
        if (mappedType.StartsWith("Action", StringComparison.Ordinal) ||
            mappedType.StartsWith("Func<", StringComparison.Ordinal)) return true;
        // Array types (from fixed-size C arrays and typed NSArray<T>, e.g., "byte[]", "NSUrl[]")
        if (mappedType.EndsWith("]", StringComparison.Ordinal)) return true;
        // Typed generic collections: NSDictionary<K,V>, NSSet<T>
        if (mappedType.StartsWith("NSDictionary<", StringComparison.Ordinal) ||
            mappedType.StartsWith("NSSet<", StringComparison.Ordinal)) return true;
        return false;
    }

    public static bool IsNullableAttribute(ObjCTypeRef typeRef) =>
        typeRef.Nullability == ObjCNullability.Nullable;

    public static bool IsNSErrorOutParameter(ObjCTypeRef typeRef) =>
        typeRef.Name == "NSError"
        && typeRef.IsPointer
        && typeRef.PointeeType is { Name: "NSError", IsPointer: true };

    /// <summary>
    /// Detects whether a parameter type is a pointer to a value type (primitive, struct, enum),
    /// which should be emitted as an <c>out</c> parameter in C# bindings.
    /// Examples: <c>BOOL *</c> → <c>out bool</c>, <c>CGPoint *</c> → <c>out CGPoint</c>.
    /// ObjC object pointers (e.g., <c>NSObject *</c>) return false — they are references, not out-params.
    /// Double pointers (e.g., <c>NSError **</c>) also return false — they have their own handling.
    /// </summary>
    public static bool IsValueTypePointerParameter(ObjCTypeRef typeRef) =>
        IsValueTypePointerParameter(typeRef, typedefMap: null, enumNames: null);

    /// <summary>
    /// Overload that resolves through typedefs and recognizes enum types as value types.
    /// <paramref name="typedefMap"/> resolves typedef'd names (e.g., MyErrorCode → NSInteger).
    /// <paramref name="enumNames"/> is the set of enum type names defined in the module.
    /// </summary>
    public static bool IsValueTypePointerParameter(ObjCTypeRef typeRef, Dictionary<string, ObjCTypeRef>? typedefMap, HashSet<string>? enumNames)
    {
        // Must be a pointer type
        if (!typeRef.IsPointer) return false;
        // Double pointers are not value-type out params (e.g., NSError **)
        if (typeRef.PointeeType != null) return false;
        // Blocks, function pointers, anonymous records are not value type pointers
        if (typeRef.IsBlock || typeRef.IsFunctionPointer || typeRef.IsAnonymousRecord) return false;
        // id<Protocol> pointers are object types
        if (typeRef.ProtocolQualifications.Count > 0) return false;
        // Generic containers (NSArray<T> *) are object types
        if (typeRef.GenericArgs.Count > 0) return false;

        var name = typeRef.Name;

        // Resolve through typedefs: e.g., typedef NSInteger MyErrorCode; MyErrorCode * → out nint
        if (typedefMap != null && typedefMap.TryGetValue(name, out var resolved))
        {
            var underlying = resolved;
            var visited = new HashSet<string> { name };
            while (typedefMap.TryGetValue(underlying.Name, out var deeper) && visited.Add(underlying.Name))
                underlying = deeper;
            name = underlying.Name;
        }

        // void * → IntPtr, not an out param
        if (name == "void") return false;
        // id, Class, SEL — object/meta types, not value type pointers
        if (name is "id" or "Class" or "SEL" or "instancetype") return false;
        // CoreFoundation Ref types (dispatch_queue_t, CGImageRef, etc.) — opaque pointers
        if (AppleFrameworkRegistry.IsCoreFoundationRefType(name)) return false;

        // If it maps to a primitive, it's a value type pointer (e.g., BOOL *, int *, CGFloat *)
        if (AppleFrameworkRegistry.IsObjCPrimitiveType(name)) return true;

        // ObjC object types: known pointer type mappings (NSString *, NSObject *, etc.)
        if (AppleFrameworkRegistry.IsObjCPointerType(name)) return false;

        // Struct types from Apple frameworks (CG*, CL*, MK*, CM*, etc.)
        if (AppleFrameworkRegistry.IsObjCValueType(name)) return true;

        // Enum types are value types — pointer to enum is an out-param
        // Check both the resolved name (for typedef aliases) and the original name
        if (enumNames != null && (enumNames.Contains(name) || enumNames.Contains(typeRef.Name))) return true;

        return false;
    }

    /// <summary>
    /// Maps a value-type pointer parameter to its C# <c>out</c> type.
    /// E.g., <c>_Bool *</c> → <c>bool</c>, <c>CGPoint *</c> → <c>CGPoint</c>.
    /// Call only after <see cref="IsValueTypePointerParameter"/> returns true.
    /// </summary>
    public static string MapValueTypePointerParameterType(ObjCTypeRef typeRef, Dictionary<string, ObjCTypeRef>? typedefMap = null)
    {
        // Map the pointee (non-pointer version of the type)
        var pointee = new ObjCTypeRef { Name = typeRef.Name };
        return MapType(pointee, typedefMap: typedefMap);
    }

    static string MapBlockType(ObjCTypeRef typeRef, HashSet<string>? genericTypeParams = null, Dictionary<string, ObjCTypeRef>? typedefMap = null, HashSet<string>? localProtocolNames = null, HashSet<string>? classProtocolClashNames = null, ICollection<string>? synthesizedProtocolInterfaces = null)
    {
        string returnType;
        if (typeRef.BlockReturnType == null)
        {
            returnType = "void";
        }
        else if (BlockReturnMapsToProtocolInterface(typeRef.BlockReturnType, typedefMap, localProtocolNames))
        {
            // A block/delegate that RETURNS a protocol type (`id<Proto>`, or a bare own-protocol
            // name) must bind that return slot to `NSObject`, NOT the protocol interface `IProto`
            // that every other position uses. bgen marshals a block's return value through
            // `Runtime.RetainAndAutoreleaseNSObject(retval)`, whose parameter is `NSObject?`; an
            // `INativeObject` protocol interface is not an `NSObject`, so the generated
            // Trampolines.g.cs fails to compile (CS1503). Block PARAMETER positions are unaffected
            // (bgen reads those via `Runtime.GetINativeObject<IProto>()`), so only the return is
            // widened. Any conforming NSObject the consumer returns still marshals correctly, and
            // widening only in return position preserves the `IProto` binding — and its
            // conforming-subclass InvalidCastException fix — for parameters, properties, and
            // ordinary (non-block) method returns.
            returnType = "NSObject";
        }
        else
        {
            returnType = MapType(typeRef.BlockReturnType, genericTypeParams: genericTypeParams, typedefMap: typedefMap, localProtocolNames: localProtocolNames, classProtocolClashNames: classProtocolClashNames, synthesizedProtocolInterfaces: synthesizedProtocolInterfaces);
        }

        var paramTypes = typeRef.BlockParams.Select(p => MapType(p, genericTypeParams: genericTypeParams, typedefMap: typedefMap, localProtocolNames: localProtocolNames, classProtocolClashNames: classProtocolClashNames, synthesizedProtocolInterfaces: synthesizedProtocolInterfaces)).ToList();

        if (paramTypes.Count > 16)
            return "NSObject";

        if (returnType == "void")
        {
            return paramTypes.Count == 0
                ? "Action"
                : $"Action<{string.Join(", ", paramTypes)}>";
        }

        var allTypes = paramTypes.Append(returnType);
        return $"Func<{string.Join(", ", allTypes)}>";
    }

    /// <summary>
    /// True when a block's return type would map to a protocol INTERFACE (<c>IProto</c>) — an
    /// <c>id&lt;Proto&gt;</c> carrying at least one bindable protocol, or a bare own-protocol name.
    /// Mirrors the two <see cref="MapType"/> arms (protocol-qualified <c>id</c> and direct
    /// own-protocol-name) that emit <c>IProto</c>. Such a return must be widened to <c>NSObject</c>
    /// in block position; see <see cref="MapBlockType"/> for why.
    /// </summary>
    static bool BlockReturnMapsToProtocolInterface(ObjCTypeRef returnType, Dictionary<string, ObjCTypeRef>? typedefMap, HashSet<string>? localProtocolNames)
    {
        // Either form MapType emits `IProto` for, checked WITHOUT a typedef hop:
        //  - `id<Proto>` carrying at least one bindable protocol (mirrors MapType arm 3), and
        //  - a bare own-protocol name, e.g. a block returning `SomeProto *` (mirrors MapType arm 10b).
        // The `id<Proto>` check must run BEFORE any hop: `id` itself resolves through typedefMap to
        // the clang-internal `objc_object` (which carries no protocol qualifications), so hopping
        // first would lose them.
        bool MapsToInterfaceDirectly(ObjCTypeRef t) =>
            (t.Name == "id" && t.ProtocolQualifications.Any(p => p != "NSObject" && p != "NSFastEnumeration"))
            || (localProtocolNames != null && localProtocolNames.Contains(t.Name));

        if (MapsToInterfaceDirectly(returnType))
            return true;

        // A typedef referenced by name — resolve a single hop and re-check BOTH forms, mirroring
        // MapType's typedef-hop arm feeding the protocol-`id` and bare-own-protocol arms. Covers
        // `typedef id<Proto> Alias;` AND `typedef Proto Alias;` (an alias of a bare protocol name);
        // re-checking only the `id<Proto>` form here would let the latter leak `IProto` (CS1503).
        return typedefMap != null
            && typedefMap.TryGetValue(returnType.Name, out var resolved)
            && MapsToInterfaceDirectly(resolved);
    }

    /// <summary>
    /// Maps ObjC generic collection types to their C# equivalents:
    /// - NSArray&lt;T&gt; / NSMutableArray&lt;T&gt; → T[] (when T is a concrete type, not a generic param)
    /// - NSDictionary&lt;K,V&gt; / NSMutableDictionary&lt;K,V&gt; → NSDictionary&lt;K,V&gt; (preserves generic args)
    /// - NSSet&lt;T&gt; / NSMutableSet&lt;T&gt; / NSOrderedSet&lt;T&gt; → NSSet&lt;T&gt; (preserves generic args)
    /// Returns null if the type doesn't qualify for generic mapping (e.g., generic param element type).
    /// </summary>
    private static string? MapGenericCollectionType(ObjCTypeRef typeRef, string? declaringClassName, HashSet<string>? genericTypeParams, Dictionary<string, ObjCTypeRef>? typedefMap, Dictionary<string, ObjCTypeRef>? blockTypedefMap, HashSet<string>? localProtocolNames = null, HashSet<string>? classProtocolClashNames = null, ICollection<string>? synthesizedProtocolInterfaces = null)
    {
        // NSArray<T> / NSMutableArray<T> with a single concrete element type → T[]
        if (typeRef.Name is "NSArray" or "NSMutableArray" && typeRef.GenericArgs.Count == 1)
        {
            var elemArg = typeRef.GenericArgs[0];
            // If element is a generic type parameter (e.g., ObjectType from class decl), fall through to NSObject[]
            // which isn't useful — return null to let normal mapping handle it as plain NSArray.
            if (genericTypeParams != null && genericTypeParams.Contains(elemArg.Name))
                return null;
            var mappedElem = MapType(elemArg, declaringClassName, genericTypeParams, typedefMap, blockTypedefMap, localProtocolNames, classProtocolClashNames, synthesizedProtocolInterfaces);
            // Closures (Action/Func<>) and nested arrays (T[][]) don't implement INativeObject,
            // which is required by bgen's NSArray.FromNSObjects<T>() / CFArray.ArrayFromHandle<T>().
            // Fall back to untyped NSArray for these element types.
            if (mappedElem == "Action" ||
                mappedElem.StartsWith("Action<", StringComparison.Ordinal) ||
                mappedElem.StartsWith("Func<", StringComparison.Ordinal) ||
                mappedElem.EndsWith("[]", StringComparison.Ordinal))
                return null;
            return $"{mappedElem}[]";
        }

        // NSDictionary<K,V> / NSMutableDictionary<K,V> → NSDictionary<K,V>
        // Note: NSDictionary<TKey, TValue> requires INativeObject — can't use mapped types like string.
        // Only emit typed generics when both args are known NS/ObjC object types.
        if (typeRef.Name is "NSDictionary" or "NSMutableDictionary" && typeRef.GenericArgs.Count == 2)
        {
            var keyArg = typeRef.GenericArgs[0];
            var valArg = typeRef.GenericArgs[1];
            if (genericTypeParams != null &&
                (genericTypeParams.Contains(keyArg.Name) || genericTypeParams.Contains(valArg.Name)))
                return null;
            var mappedKey = MapNativeObjectGenericArg(keyArg, declaringClassName, genericTypeParams, typedefMap, blockTypedefMap);
            var mappedVal = MapNativeObjectGenericArg(valArg, declaringClassName, genericTypeParams, typedefMap, blockTypedefMap);
            if (mappedKey == null || mappedVal == null)
                return null; // Fall back to plain NSDictionary
            return $"NSDictionary<{mappedKey}, {mappedVal}>";
        }

        // NSSet<T> / NSMutableSet<T> / NSOrderedSet<T> / NSMutableOrderedSet<T> → NSSet<T>
        // Same INativeObject constraint as NSDictionary.
        if (typeRef.Name is "NSSet" or "NSMutableSet" or "NSOrderedSet" or "NSMutableOrderedSet"
            && typeRef.GenericArgs.Count == 1)
        {
            var elemArg = typeRef.GenericArgs[0];
            if (genericTypeParams != null && genericTypeParams.Contains(elemArg.Name))
                return null;
            var mappedElem = MapNativeObjectGenericArg(elemArg, declaringClassName, genericTypeParams, typedefMap, blockTypedefMap);
            if (mappedElem == null)
                return null; // Fall back to plain NSSet
            return $"NSSet<{mappedElem}>";
        }

        return null;
    }

    /// <summary>
    /// Maps an ObjC generic arg for NSDictionary/NSSet, ensuring the result implements INativeObject.
    /// Returns null if the type can't be used as a generic arg (e.g., typedefs to NSString, primitives).
    /// Unlike <see cref="MapType"/>, this does NOT convert NSString→string or other INativeObject-incompatible mappings.
    /// </summary>
    private static string? MapNativeObjectGenericArg(ObjCTypeRef typeRef, string? declaringClassName, HashSet<string>? genericTypeParams, Dictionary<string, ObjCTypeRef>? typedefMap, Dictionary<string, ObjCTypeRef>? blockTypedefMap)
    {
        var name = typeRef.Name;

        // Resolve typedefs — if it's a typedef for NSString,
        // use NSString instead of the typedef name (which won't exist as a C# type).
        if (typedefMap != null && typedefMap.TryGetValue(name, out var resolved))
        {
            // Follow the typedef chain to the underlying type
            var underlying = resolved;
            var visited = new HashSet<string> { name };
            while (typedefMap.TryGetValue(underlying.Name, out var deeper) && visited.Add(underlying.Name))
                underlying = deeper;
            name = underlying.Name;
        }

        // Known NS object types that implement INativeObject
        if (AppleFrameworkRegistry.TryMapObjCPointerType(name, out var mapped))
        {
            // string doesn't implement INativeObject — keep NSString
            if (mapped == "string")
                return "NSString";
            return mapped;
        }

        // If it's "id" (any object), use NSObject
        if (name == "id")
            return "NSObject";

        // Primitive types (int, bool, etc.) can't be used as NSDictionary generic args
        if (AppleFrameworkRegistry.IsObjCPrimitiveType(name))
            return null;

        // Unknown pointer types: module-local ObjC classes (e.g., GIDClaim) are emitted as
        // partial interfaces by bgen, which don't implement INativeObject at compile time.
        // Only use known Apple SDK types as generic args; for everything else, fall back to
        // the untyped container.
        return null;
    }
}
