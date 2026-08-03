// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;

namespace BindingsGeneration.ObjC;

/// <summary>
/// Emits a module's <c>extern</c> constants as a bgen <c>[Static]</c> interface inside
/// <c>ApiDefinition.cs</c>.
/// <para/>
/// The file this writes into is load-bearing. bgen synthesizes the <c>Dlfcn.GetStringConstant</c>
/// (and <c>GetNInt</c>/<c>GetDouble</c>/...) backing for a <c>[Field]</c> member ONLY when it reads
/// the declaration from an <c>ObjcBindingApiDefinition</c> input. A <c>[Field]</c> declared in an
/// <c>ObjcBindingCoreSource</c> file is compiled verbatim, so it stays a get-only auto-property with
/// no initializer: it compiles, it IntelliSenses, and it returns <c>null</c>/zero forever. Moving a
/// constant out of this file therefore silently un-binds it without any compile error.
/// </summary>
internal static class ObjCConstantsEmitter
{
    /// <summary>
    /// The bgen <c>[Field]</c> types whose reader exists in <c>Dlfcn</c>. A constant of any other
    /// type has no backing bgen can generate, so it is recorded as a skip instead of emitted.
    /// </summary>
    static readonly HashSet<string> FieldSupportedTypes =
        ["NSString", "nint", "nuint", "nfloat", "int", "float", "double"];

    /// <summary>
    /// The bgen library name every emitted <c>[Field]</c> is bound against.
    /// <para/>
    /// bgen turns this into <c>Libraries.__Internal.Handle = Dlfcn.dlopen (null, 0)</c>, and
    /// <c>dlopen</c> with a null path returns a handle equivalent to <c>RTLD_DEFAULT</c> — a
    /// process-wide search over every loaded image, not a main-executable-only lookup. That is the
    /// one form that resolves for BOTH linkage shapes this generator supports: a dynamically linked
    /// embedded framework (the symbol lives in the framework image, which the consuming app links,
    /// so it is loaded and searched) and a statically linked archive (the symbol is linked into the
    /// app image itself). Naming the framework instead makes bgen emit
    /// <c>Dlfcn.dlopen("&lt;name&gt;", 0)</c>, and dyld does not search for a bare leaf name inside an
    /// app bundle — the handle would come back null and every constant would read null again, only
    /// with a harder-to-diagnose cause. An absolute path additionally bakes a build-host location
    /// into shipped code.
    /// </summary>
    internal const string FieldLibraryName = "__Internal";

    /// <summary>The C# type name bgen generates for the module's constants.</summary>
    internal static string ConstantsTypeName(string moduleName) => $"{moduleName}Constants";

    /// <summary>
    /// Appends the <c>[Static] partial interface {Module}Constants</c> block, or nothing when the
    /// module has no constant bgen can back. Unsupported constants are always recorded as skips so
    /// the drop is visible in the binding report rather than only as a comment in a generated file.
    /// </summary>
    internal static void Emit(
        StringBuilder sb,
        ObjCModule module,
        Dictionary<string, ObjCTypeRef> typedefMap,
        ObjCBindingDiagnostics? diagnostics)
    {
        var constants = module.Constants.Where(c => c.IsExtern).ToList();
        if (constants.Count == 0)
            return;

        var prefix = ResolveModuleTag(constants);

        // Split first so an all-unsupported module emits no interface at all: bgen turns an empty
        // [Static] interface into an empty static class, which reads as "this module has constants"
        // when it does not.
        var emittable = new List<(ObjCConstantDecl Constant, string FieldType)>();
        foreach (var constant in constants)
        {
            var fieldType = MapConstantFieldType(constant.Type, typedefMap);
            if (FieldSupportedTypes.Contains(fieldType))
            {
                emittable.Add((constant, fieldType));
            }
            else
            {
                diagnostics?.RecordSkip(
                    "constant", constant.Name, ObjCSkipReason.UnsupportedConstruct,
                    $"[Field] has no Dlfcn reader for '{fieldType}'");
            }
        }

        if (emittable.Count == 0)
            return;

        sb.AppendLine("    [Static]");
        sb.AppendLine($"    partial interface {ConstantsTypeName(module.ModuleName)}");
        sb.AppendLine("    {");

        // Seeded with the containing type's own name: a member spelled exactly like its enclosing
        // type is CS0542, and either naming path can land there (a symbol literally named
        // `MLNConstants` in a module named `MLN` with no tag stripped, or one whose stripped
        // remainder reproduces the class name).
        var emittedNames = new HashSet<string>(StringComparer.Ordinal)
        {
            ConstantsTypeName(module.ModuleName),
        };
        var first = true;
        foreach (var (constant, fieldType) in emittable)
        {
            if (!first)
                sb.AppendLine();
            first = false;

            var name = ResolveMemberName(constant.Name, prefix, emittedNames, diagnostics);
            ObjCAvailabilityEmitter.EmitAvailabilityAttributes(sb, constant.Availability, "        ");
            sb.AppendLine($"        [Field(\"{constant.Name}\", \"{FieldLibraryName}\")]");
            sb.AppendLine($"        {fieldType} {name} {{ get; }}");
        }

        sb.AppendLine("    }");
        sb.AppendLine();
    }

    /// <summary>
    /// The C source spellings whose width follows the pointer width. <c>NSInteger</c>/<c>NSUInteger</c>
    /// are absent because they already map straight to <c>nint</c>/<c>nuint</c>.
    /// <para/>
    /// Only <c>long</c> and <c>unsigned long</c> are reachable against today's primitive table in
    /// <c>objc-type-mappings.json</c> — the elaborated spellings are listed so that a table gaining
    /// one of them promotes it too, rather than silently dropping the constant. The promotion stays
    /// guarded by the mapped result either way, so an unmapped spelling here is inert, not wrong.
    /// </summary>
    static readonly HashSet<string> NativeWidthIntegerSpellings = new(StringComparer.Ordinal)
    {
        "long", "signed long", "long int", "signed long int",
        "unsigned long", "unsigned long int",
    };

    /// <summary>
    /// Maps a constant's declared type to the C# type its <c>[Field]</c> is declared with.
    /// <para/>
    /// Deliberately a constant-site helper rather than a change to
    /// <see cref="ObjCTypeMapper.MapType"/>: that is the shared mapping entry point for methods,
    /// properties, block parameters and struct fields, and widening <c>long</c> to <c>nint</c> there
    /// would silently rewrite signatures across the whole corpus. Here it is sound and narrow —
    /// <c>nint</c>/<c>nuint</c> are the only widths <c>Dlfcn</c> reads a word-sized integer symbol
    /// at, so without the promotion the constant is simply dropped.
    /// </summary>
    static string MapConstantFieldType(ObjCTypeRef type, Dictionary<string, ObjCTypeRef> typedefMap)
    {
        // An NSString* constant is declared as NSString (bgen reads it with GetStringConstant), not
        // as the `string` MapType returns. Typedef'd spellings resolve too, which is what makes an
        // NS_TYPED_EXTENSIBLE_ENUM constant (typedef NSString *OUEventName) bind.
        if (IsNSStringType(type, typedefMap))
            return "NSString";

        var mapped = ObjCTypeMapper.MapType(type, typedefMap: typedefMap);

        // Keyed on the SOURCE spelling, not on the mapped result: the mapping table sends the
        // fixed-width `int64_t` / `long long` / `uint64_t` family to `long`/`ulong` as well, and
        // those are 64-bit by definition rather than pointer-width. Promoting on the mapped type
        // would silently retype a fixed-width constant as native-width.
        if (mapped is "long" or "ulong" && IsNativeWidthInteger(type, typedefMap))
            return mapped == "long" ? "nint" : "nuint";

        return mapped;
    }

    static bool IsNativeWidthInteger(ObjCTypeRef type, Dictionary<string, ObjCTypeRef> typedefMap)
    {
        if (type.IsPointer)
            return false;
        if (NativeWidthIntegerSpellings.Contains(type.Name))
            return true;
        // typedefMap is already chain-resolved, so one lookup reaches the leaf spelling.
        return typedefMap.TryGetValue(type.Name, out var resolved)
            && !resolved.IsPointer
            && NativeWidthIntegerSpellings.Contains(resolved.Name);
    }

    /// <summary>
    /// Checks if a type is NSString* directly or through typedef chain resolution.
    /// e.g., MOSNotification (typedef for NSString*) → true.
    /// </summary>
    internal static bool IsNSStringType(ObjCTypeRef type, Dictionary<string, ObjCTypeRef> typedefMap)
    {
        // Direct NSString* check
        if (type is { Name: "NSString", IsPointer: true })
            return true;

        // Resolve through typedef chain: the constant's type name may be a typedef
        // for NSString* (e.g., typedef NSString *MOSNotification).
        // The typedefMap resolves chains, so we just need a single lookup.
        if (typedefMap.TryGetValue(type.Name, out var resolved))
        {
            if (resolved is { Name: "NSString", IsPointer: true })
                return true;
            // Also handle when the typedef drops the pointer but the usage adds it
            if (resolved.Name == "NSString" && type.IsPointer)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns the uppercase module tag every constant in <paramref name="constants"/> shares
    /// (<c>MLNShapeSourceOptionClustered</c>, <c>MLNOfflinePackErrorNotification</c> → <c>MLN</c>),
    /// or <see langword="null"/> when there is no such tag.
    /// <para/>
    /// Same conservative shape as the enum-case strip: it fires only when EVERY constant carries the
    /// tag, so one oddly-named constant leaves the whole module un-stripped rather than producing a
    /// half-renamed surface. The tag is taken from the constants themselves because the module name
    /// does not carry it (a module named <c>Mapbox</c> prefixes its constants <c>MLN</c>).
    /// </summary>
    internal static string? ResolveModuleTag(IReadOnlyList<ObjCConstantDecl> constants)
    {
        if (constants.Count == 0)
            return null;

        // Longest common ordinal prefix across every constant name.
        var common = constants[0].Name;
        foreach (var c in constants.Skip(1))
        {
            var limit = Math.Min(common.Length, c.Name.Length);
            var i = 0;
            while (i < limit && common[i] == c.Name[i])
                i++;
            common = common[..i];
            if (common.Length == 0)
                return null;
        }

        // Keep only the leading all-uppercase run: the tag is an acronym, and the common prefix can
        // reach into a shared word (a module whose only constants are MLNShapeSourceOption* has
        // "MLNShapeSourceOption" in common).
        var run = 0;
        while (run < common.Length && common[run] is >= 'A' and <= 'Z')
            run++;
        if (run == 0)
            return null;

        // The last uppercase letter of the run starts the next PascalCase word when a lowercase
        // letter follows it (FBSDKAppEventName → the run is "FBSDKA", the tag is "FBSDK").
        if (run < common.Length && char.IsLower(common[run]))
            run--;

        if (run is < 2 or > 6)
            return null;

        var tag = common[..run];

        // Every constant must keep a non-empty PascalCase remainder, or the strip would produce an
        // empty or lowercase-leading identifier.
        foreach (var c in constants)
        {
            if (c.Name.Length <= tag.Length)
                return null;
            if (c.Name[tag.Length] is not (>= 'A' and <= 'Z'))
                return null;
        }

        return tag;
    }

    /// <summary>
    /// Produces the emitted member name for a constant: the module tag stripped when one was
    /// resolved, otherwise the raw name with its first letter capitalised. The remainder of a
    /// stripped name is emitted verbatim because the ObjC constant idiom already leaves it
    /// PascalCase — running it through a capitalise-first pass would fold an all-caps remainder.
    /// </summary>
    static string ResolveMemberName(
        string constantName,
        string? prefix,
        HashSet<string> emittedNames,
        ObjCBindingDiagnostics? diagnostics)
    {
        var name = prefix != null && constantName.StartsWith(prefix, StringComparison.Ordinal)
            ? constantName[prefix.Length..]
            : ToPascalCase(constantName);

        if (emittedNames.Add(name))
            return name;

        // Unreachable while the tag strip removes the SAME prefix from every (already unique) name,
        // but two members sharing a name is a hard CS0102 rather than a degraded binding, so the
        // ladder stays as a floor.
        var disambiguated = name;
        var n = 2;
        while (!emittedNames.Add(disambiguated = $"{name}_{n}"))
            n++;

        diagnostics?.RecordSkip(
            "constant", constantName, ObjCSkipReason.DuplicateSignature,
            $"constant name '{name}' collides with a sibling; emitted as '{disambiguated}'");

        return disambiguated;
    }

    static string ToPascalCase(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;
        return char.ToUpperInvariant(name[0]) + name[1..];
    }
}
