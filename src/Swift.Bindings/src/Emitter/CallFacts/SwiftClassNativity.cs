// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Whether a class is a native Swift class, with no Objective-C class at the root of its superclass
/// chain. The runtime keeps an <c>unowned</c> reference to a native class as one pointer the value
/// can be moved in registers with; to any other class it tracks the reference by its address.
/// </summary>
internal static class SwiftClassNativity
{
    /// <summary>
    /// Whether a class the module declares is native. The chain is walked through the module's own
    /// classes; the first superclass declared in another module answers for the rest of the chain
    /// from its record, where its own module marked an Objective-C root.
    /// </summary>
    /// <param name="declared">The module's own declaration of a type, by module-qualified name.</param>
    /// <param name="recorded">Another module's record of a type, by module-qualified name.</param>
    public static bool IsNative(ClassDecl classDecl, Func<string, TypeDecl?> declared, Func<string, TypeRecord?> recorded)
    {
        ArgumentNullException.ThrowIfNull(classDecl);
        if (classDecl.HasObjCSuperclass)
            return false;
        foreach (var superclassName in classDecl.SuperclassNames)
        {
            var name = WithoutGenericArguments(superclassName);
            if (declared(name) is not { } superclass)
                return IsNative(recorded(name));
            if (superclass is not ClassDecl { HasObjCSuperclass: false })
                return false;
        }
        return true;
    }

    /// <summary>
    /// A specialized class name (<c>Base&lt;Int&gt;</c>, <c>Outer&lt;Int&gt;.Base</c>) without its
    /// arguments, the spelling declarations and records are keyed by.
    /// </summary>
    internal static string WithoutGenericArguments(string name)
    {
        if (name.IndexOf('<') < 0)
            return name;
        var result = new System.Text.StringBuilder(name.Length);
        int depth = 0;
        for (int i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c == '<')
                depth++;
            else if (c == '>' && depth > 0 && (i == 0 || name[i - 1] != '-')) // not a function arrow
                depth--;
            else if (depth == 0)
                result.Append(c);
        }
        return result.ToString();
    }

    /// <summary>
    /// Whether another module's record describes a native class. A class its own module found rooted
    /// in Objective-C is not. Otherwise the type's mangling says where the class was declared:
    /// <c>So</c> is the Clang importer's context, which only an imported Objective-C or Core
    /// Foundation class has, and any other Swift mangling is a class declared in Swift. A record with
    /// no mangling, as the Objective-C bridge writes for an imported module's classes and some SDK
    /// entries carry, is native only when it is not projected as an Objective-C class and its module
    /// is not one of the SDK's Objective-C modules. No record is not native.
    /// </summary>
    public static bool IsNative(TypeRecord? record)
    {
        if (record is null
            || record.Kind != TypeRecordKind.Class
            || (record.Flags & TypeRecordFlags.ObjCRooted) != 0)
            return false;
        var mangling = record.MetadataAccessor ?? string.Empty;
        if (mangling.StartsWith("$sSo", StringComparison.Ordinal))
            return false;
        if (mangling.StartsWith("$s", StringComparison.Ordinal))
            return true;
        return (record.Flags & TypeRecordFlags.ObjCBridged) == 0
               && !TypeDatabaseExtensions.IsObjCClassSwiftType(record.SwiftTypeName);
    }
}
