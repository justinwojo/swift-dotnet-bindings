// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.CodeAnalysis.CSharp;

namespace BindingsGeneration;

/// <summary>
/// How Swift source spells a declaration's name, as opposed to the C# name the parser stores.
/// </summary>
/// <remarks>
/// The parser stores a C#-keyword name with an underscore in front (Swift's <c>struct event</c> is
/// held as <c>_event</c>), so a test of the stored name for a leading underscore reads every
/// keyword-named public declaration as one of Swift's underscore-prefixed implementation details.
/// Questions about the Swift declaration, such as that convention, have to read the Swift spelling.
/// </remarks>
public static class SwiftDeclSpelling
{
    /// <summary>
    /// The declaration's name as Swift source spells it: <c>event</c> for the type the parser holds
    /// as <c>_event</c>. The C#-keyword prefix is undone only when the type's
    /// <see cref="TypeDecl.SwiftTypeName"/> confirms it, so every other name is returned as is and a
    /// Swift type genuinely named <c>_class</c> keeps its underscore.
    /// </summary>
    public static string SwiftSpelledName(BaseDecl decl)
    {
        var name = decl.Name;
        if (decl is TypeDecl { SwiftTypeName: { } swiftTypeName }
            && name.Length == swiftTypeName.Name.Length + 1
            && name[0] == '_'
            && string.CompareOrdinal(name, 1, swiftTypeName.Name, 0, swiftTypeName.Name.Length) == 0
            && SyntaxFacts.GetKeywordKind(swiftTypeName.Name) != SyntaxKind.None)
            return swiftTypeName.Name;
        return name;
    }

    /// <summary>
    /// Whether Swift spells the declaration's name with a leading underscore, Swift's convention for
    /// an implementation detail kept public for binary compatibility. A keyword-named declaration
    /// (<c>struct event</c>, stored as <c>_event</c>) is not one.
    /// </summary>
    public static bool HasUnderscoreSwiftName(BaseDecl decl)
        => SwiftSpelledName(decl).StartsWith('_');
}
