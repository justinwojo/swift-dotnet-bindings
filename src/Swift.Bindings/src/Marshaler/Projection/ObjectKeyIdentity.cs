// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;

namespace BindingsGeneration;

/// <summary>
/// A Swift type whose value is one object reference (an Objective-C class, bound generic or not, a
/// string-backed Objective-C key type such as <c>NSAttributedString.Key</c>, or an optional of an object
/// type, whose nil is a null pointer), used inside a Swift collection: as a
/// dictionary key or set element (a hashed position), or as an array element or dictionary value.
/// The binding declares one identity class per such type in its module's <c>__SwiftObjectKeys</c>
/// class. For a type the module uses in a hashed position, its wrapper exports the metadata of
/// <c>Set&lt;Key&gt;</c>, from which the module initializer registers the type's metadata and
/// <c>Hashable</c> witness table; for a type used only in other positions it exports the type's own
/// metadata, which is all an element needs.
/// </summary>
/// <param name="SwiftTypeName">The module-qualified Swift type (<c>UIKit.UIApplication.LaunchOptionsKey</c>).</param>
public sealed record ObjectKeyIdentity(string SwiftTypeName)
{
    /// <summary>
    /// The class that holds a module's identities. Referenced unqualified: the generated code of a
    /// module (its types, its free functions and its <c>SwiftInterop</c> proxies) all live in or under
    /// the module's namespace, where the class is declared.
    /// </summary>
    public const string HolderClassName = "__SwiftObjectKeys";

    /// <summary>
    /// The identity class's name, one per Swift type and never shared by two: the spelling with every
    /// character other than a letter or digit flattened to <c>_</c>. Flattening alone is one-to-one only
    /// for a plain dotted name (<c>Foundation.NSUUID</c> → <c>Foundation_NSUUID</c>, each <c>_</c> a
    /// dot). Any other spelling, a generic one (<c>Swift.Optional&lt;M.Outer.Key&gt;</c>) or one whose
    /// names hold an underscore (<c>M.Outer_Key</c>, which would otherwise meet <c>M.Outer.Key</c>), is
    /// followed by <c>__</c> and a code for each flattened character in order, from which the spelling
    /// can be read back: a plain name never flattens to two adjacent underscores, and the codes hold no
    /// underscore, so no two spellings share a name.
    /// </summary>
    public string IdentityClassName { get; } = UniqueName(SwiftTypeName);

    /// <summary>The identity as referenced from the module's generated code.</summary>
    public string IdentityType => $"{HolderClassName}.{IdentityClassName}";

    /// <summary>The C# type a collection slot of this key holds.</summary>
    public string CarrierType => $"global::Swift.Runtime.SwiftObjectKey<{IdentityType}>";

    /// <summary>The Swift <c>Set</c> whose metadata the wrapper exports for this key.</summary>
    public string SwiftSetTypeName => $"Swift.Set<{SwiftTypeName}>";

    /// <summary>The exported symbol of the wrapper's <c>Set&lt;Key&gt;</c> metadata accessor.</summary>
    public string AccessorSymbol(string moduleName)
        => $"SBW_GetObjectKeySetMetadata_{moduleName}_{IdentityClassName}_{EmitterUtility.DeterministicHash8($"{moduleName}.{SwiftTypeName}")}";

    /// <summary>The C# name of the P/Invoke that calls <see cref="AccessorSymbol"/>.</summary>
    public string AccessorMethodName => $"__GetSetMetadata_{IdentityClassName}";

    /// <summary>
    /// The exported symbol of the wrapper's accessor of the type's own metadata, for a type the module
    /// uses only as an array element or dictionary value.
    /// </summary>
    public string ValueAccessorSymbol(string moduleName)
        => $"SBW_GetObjectValueMetadata_{moduleName}_{IdentityClassName}_{EmitterUtility.DeterministicHash8($"{moduleName}.{SwiftTypeName}")}";

    /// <summary>The C# name of the P/Invoke that calls <see cref="ValueAccessorSymbol"/>.</summary>
    public string ValueAccessorMethodName => $"__GetValueMetadata_{IdentityClassName}";

    /// <summary>
    /// The identity of <paramref name="typeSpec"/>, spelled module-qualified with its generic arguments
    /// (<c>Swift.Optional&lt;Foundation.NSUUID&gt;</c>, <c>Foundation.NSCache&lt;Foundation.NSString,
    /// Foundation.NSUUID&gt;</c>), or <c>null</c> when some part of it has no module-qualified Swift name.
    /// </summary>
    public static ObjectKeyIdentity? For(TypeSpec typeSpec)
        => SwiftSpelling(typeSpec) is { } name ? new ObjectKeyIdentity(name) : null;

    private static string? SwiftSpelling(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec { InnerType: null, IsInOut: false } named || !named.HasModule())
            return null;
        if (named.GenericParameters.Count == 0)
            return named.Name;
        var arguments = new List<string>(named.GenericParameters.Count);
        foreach (var argument in named.GenericParameters)
        {
            if (SwiftSpelling(argument) is not { } spelled)
                return null;
            arguments.Add(spelled);
        }
        return $"{named.Name}<{string.Join(", ", arguments)}>";
    }

    private static string UniqueName(string swiftTypeName)
    {
        var flattened = SafeName(swiftTypeName);
        if (IsPlainDottedName(swiftTypeName))
            return flattened;
        var codes = new StringBuilder();
        foreach (var c in swiftTypeName)
        {
            if (!char.IsAsciiLetterOrDigit(c))
                codes.Append(FlattenedCharacterCode(c));
        }
        return $"{flattened}__{codes}";
    }

    // Letters and digits separated by single dots, the one spelling flattening keeps one-to-one.
    private static bool IsPlainDottedName(string swiftTypeName)
    {
        if (swiftTypeName.Length == 0 || swiftTypeName[0] == '.' || swiftTypeName[^1] == '.')
            return false;
        for (var i = 0; i < swiftTypeName.Length; i++)
        {
            var c = swiftTypeName[i];
            if (c == '.' ? swiftTypeName[i - 1] == '.' : !char.IsAsciiLetterOrDigit(c))
                return false;
        }
        return true;
    }

    // A prefix-free code of letters and digits: one letter for a character a Swift spelling uses,
    // otherwise 'x' and the UTF-16 unit in four hex digits.
    private static string FlattenedCharacterCode(char c) => c switch
    {
        '.' => "d",
        '_' => "u",
        '<' => "l",
        '>' => "g",
        ',' => "c",
        ' ' => "s",
        '?' => "q",
        '&' => "a",
        ':' => "k",
        '(' => "p",
        ')' => "r",
        '[' => "b",
        ']' => "e",
        '-' => "m",
        _ => $"x{(int)c:X4}",
    };

    private static string SafeName(string swiftTypeName)
    {
        var builder = new StringBuilder(swiftTypeName.Length);
        foreach (var c in swiftTypeName)
            builder.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        return builder.ToString();
    }
}
