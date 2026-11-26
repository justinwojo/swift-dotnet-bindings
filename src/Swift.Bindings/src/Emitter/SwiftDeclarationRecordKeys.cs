// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace BindingsGeneration
{
    /// <summary>
    /// Reads a Swift declaration record's member keys (<c>Parent.Name(T1,T2)`N</c>, <c>Parent.this[T]</c>,
    /// <c>Parent.Name</c>) against the compiled assembly they describe. A key spells its parameters as the
    /// member declares them, so it cannot say on its own whether a <c>T?</c> is a nullable reference
    /// annotation (no part of the CLR signature) or <c>Nullable&lt;T&gt;</c> (a signature of its own).
    /// The assembly can: <see cref="Index"/> lists each public or protected member of a namespace with
    /// its parameters rendered in the canonical form <see cref="CanonicalType"/> renders a key's, in which
    /// <c>?</c> marks a <c>Nullable&lt;T&gt;</c> and <c>~</c> a reference type that may carry an annotation.
    /// The package merge (<c>SwiftBindingPackageMerge</c>) resolves a respelled key to its CLR signature
    /// this way, and the compile gate's completeness check enumerates what a record must cover with it.
    /// The canonical form drops namespaces, <c>global::</c> and tuple element names, maps C# keywords to
    /// their CLR names, and writes a tuple as <c>ValueTuple&lt;…&gt;</c>; resolution only has to tell
    /// apart the overloads of one member name, which share their declaring type. The merge also needs
    /// to tell a binding type from a base class library type of the same simple name, so each listed
    /// member carries its parameters in a qualified form as well (<see cref="Member.QualifiedParameters"/>),
    /// which a key's own qualified spelling (<see cref="Key.QualifiedParameters"/>) is read against with
    /// <see cref="CanName"/>.
    /// </summary>
    public static class SwiftDeclarationRecordKeys
    {
        /// <summary>A record key taken apart.</summary>
        /// <param name="ParentPath">The nested C# path of the declaring type, empty for a module-level function.</param>
        /// <param name="Name">The member's C# name: <c>.ctor</c> for a constructor, <c>Item</c> for an indexer.</param>
        /// <param name="Parameters">The canonical parameter types, null for a key without a parameter list (a property).</param>
        /// <param name="GenericArity">The method's generic arity (the key's <c>`N</c> suffix).</param>
        public sealed record Key(string ParentPath, string Name, ImmutableArray<string>? Parameters, int GenericArity)
        {
            public bool IsIndexer => Name == "Item" && Parameters is not null && IndexerSpelling;
            internal bool IndexerSpelling { get; init; }

            /// <summary>
            /// The parameter types as the key qualifies them (<see cref="QualifiedParameter"/>), one per
            /// <see cref="Parameters"/> entry; null for a key without a parameter list.
            /// </summary>
            public ImmutableArray<string>? QualifiedParameters { get; init; }
        }

        /// <summary>A public or protected member of the assembly, as <see cref="Index"/> lists it.</summary>
        /// <param name="Kind">method, ctor, property, indexer, field, event or type.</param>
        /// <param name="Parameters">Canonical parameter types with <c>?</c> and <c>~</c> marks; empty for a property, field, event or type.</param>
        public sealed record Member(string ParentPath, string Name, string Kind, ImmutableArray<string> Parameters, int GenericArity,
            bool IsStatic)
        {
            /// <summary>The kind of the declaring type (of the type itself, for a type): class, struct, interface, enum or delegate.</summary>
            public string DeclaringKind { get; init; } = "class";

            /// <summary>Metadata facts the completeness check's fixed-name rules read.</summary>
            public MemberTraits Traits { get; init; }

            /// <summary>For a property, indexer or field, its canonical type; empty otherwise.</summary>
            public string ValueType { get; init; } = "";

            /// <summary>For a type, the namespace-qualified names (without arity) of the interfaces it implements; empty otherwise.</summary>
            public ImmutableArray<string> Interfaces { get; init; } = ImmutableArray<string>.Empty;

            /// <summary>
            /// The parameters in the qualified form: each type named by its namespace and enclosing types,
            /// a nested type after <c>+</c> (<c>Kit.Box+Inner</c>), every name with its own generic arity
            /// (<c>System.Collections.Generic.IReadOnlyList`1&lt;System.Int32?&gt;</c>),
            /// <c>?</c> only for a <c>Nullable&lt;T&gt;</c>, and a type parameter by its position (<c>!0</c>,
            /// <c>!!0</c>), so two members have the same list exactly when they have the same CLR signature.
            /// </summary>
            public ImmutableArray<string> QualifiedParameters { get; init; } = ImmutableArray<string>.Empty;

            /// <summary>
            /// The declaring type's qualified name (<see cref="QualifiedParameters"/>'s form): where C# looks up
            /// the member's parameter types. For a type, its enclosing type's, or its namespace.
            /// </summary>
            public string QualifiedParent { get; init; } = "";

            /// <summary>For a type, its qualified name (<see cref="QualifiedParameters"/>'s form, <c>Kit.Box`1+Inner</c>); empty otherwise.</summary>
            public string QualifiedName { get; init; } = "";

            /// <summary>The member's full path: its declaring type's path and its name.</summary>
            public string Path => ParentPath.Length > 0 ? ParentPath + "." + Name : Name;

            /// <summary>The member's display form, for diagnostics.</summary>
            public override string ToString() =>
                (ParentPath.Length > 0 ? ParentPath + "." : "") + Name
                + (Kind is "method" or "ctor" ? "(" + string.Join(",", Parameters) + ")" : Kind == "indexer" ? "[" + string.Join(",", Parameters) + "]" : "")
                + (GenericArity > 0 ? "`" + GenericArity : "");
        }

        /// <summary>Metadata facts of a listed member.</summary>
        [Flags]
        public enum MemberTraits
        {
            None = 0,
            /// <summary>A special-name method: an operator or a conversion.</summary>
            SpecialName = 2,
            /// <summary>A constant field.</summary>
            Literal = 4,
            /// <summary>Carries <c>CompilerGeneratedAttribute</c>.</summary>
            CompilerGenerated = 8,
        }

        /// <summary>
        /// Takes <paramref name="key"/> apart, or returns null for a key whose brackets do not close or
        /// whose parameter types do not read as C# types.
        /// </summary>
        public static Key? Parse(string key)
        {
            var open = key.IndexOfAny(new[] { '(', '[' });
            if (open < 0)
            {
                var dot = LastTopLevelDot(key, key.Length);
                return new Key(dot < 0 ? "" : StripGenericArguments(key[..dot]), dot < 0 ? key : key[(dot + 1)..], null, 0);
            }
            var closing = key[open] == '(' ? ')' : ']';
            var close = MatchingClose(key, open, key[open], closing);
            if (close < 0)
                return null;
            var arity = 0;
            var rest = key[(close + 1)..];
            if (rest.Length > 0 && (rest[0] != '`' || !int.TryParse(rest[1..], out arity)))
                return null;
            var parameters = ImmutableArray.CreateBuilder<string>();
            var qualified = ImmutableArray.CreateBuilder<string>();
            var list = key[(open + 1)..close];
            if (list.Trim().Length > 0)
                foreach (var parameter in TopLevel(list, ','))
                {
                    if (CanonicalParameter(parameter) is not { } canonical || QualifiedParameter(parameter) is not { } spelled)
                        return null;
                    parameters.Add(canonical);
                    qualified.Add(spelled);
                }
            var head = key[..open];
            var lastDot = LastTopLevelDot(head, head.Length);
            var parent = lastDot < 0 ? "" : StripGenericArguments(head[..lastDot]);
            var name = lastDot < 0 ? head : head[(lastDot + 1)..];
            var indexer = key[open] == '[';
            if (indexer && name != "this")
                return null;
            if (indexer)
                name = "Item";
            else if (parent.Length > 0 && name == parent[(parent.LastIndexOf('.') + 1)..])
                name = ".ctor";
            return new Key(parent, name, parameters.ToImmutable(), arity) { IndexerSpelling = indexer, QualifiedParameters = qualified.ToImmutable() };
        }

        /// <summary>
        /// The canonical form of one parameter of a key (<c>ref string?</c> → <c>&amp;String?</c>), or
        /// null when it does not read as a C# type.
        /// </summary>
        public static string? CanonicalParameter(string spelled) => Parameter(spelled, qualified: false);

        /// <summary>
        /// One parameter of a key in the qualified form <see cref="Member.QualifiedParameters"/> uses, as far
        /// as the key spells it: a C# keyword as its <c>global::System</c> type, a name with the
        /// qualification the key gives it (<c>global::</c> kept) and each name's own generic arity
        /// (<c>Outer&lt;T&gt;.Inner</c> → <c>Outer`1.Inner&lt;T&gt;</c>), each <c>?</c> kept. Null when it
        /// does not read as a C# type.
        /// </summary>
        public static string? QualifiedParameter(string spelled) => Parameter(spelled, qualified: true);

        private static string? Parameter(string spelled, bool qualified)
        {
            var text = spelled.Trim();
            var byRef = false;
            foreach (var modifier in new[] { "this ", "params ", "scoped " })
                if (text.StartsWith(modifier, StringComparison.Ordinal))
                    text = text[modifier.Length..].TrimStart();
            foreach (var modifier in new[] { "ref readonly ", "ref ", "out ", "in " })
                if (text.StartsWith(modifier, StringComparison.Ordinal))
                {
                    text = text[modifier.Length..].TrimStart();
                    byRef = true;
                    break;
                }
            return Type(text, qualified) is { } type ? (byRef ? "&" : "") + type : null;
        }

        /// <summary>
        /// The canonical form of a C# type as a key spells it, keeping each <c>?</c>; null when
        /// <paramref name="spelled"/> does not read as a type.
        /// </summary>
        public static string? CanonicalType(string spelled) => Type(spelled, qualified: false);

        private static string? Type(string spelled, bool qualified)
        {
            var text = spelled.Trim();
            var at = 0;
            var result = ReadType(text, ref at, qualified);
            while (at < text.Length && text[at] == ' ') at++;
            return result is not null && at == text.Length ? result : null;
        }

        private static string? ReadType(string text, ref int at, bool qualified)
        {
            while (at < text.Length && text[at] == ' ') at++;
            if (at >= text.Length)
                return null;
            string core;
            if (text[at] == '(')
            {
                var close = MatchingClose(text, at, '(', ')');
                if (close < 0)
                    return null;
                var elements = new List<string>();
                foreach (var element in TopLevel(text[(at + 1)..close], ','))
                {
                    if (Type(WithoutElementName(element.Trim()), qualified) is not { } type)
                        return null;
                    elements.Add(type);
                }
                if (elements.Count < 2)
                    return null;
                core = (qualified ? Language + "System.ValueTuple`" + elements.Count : "ValueTuple") + "<" + string.Join(",", elements) + ">";
                at = close + 1;
            }
            else if (text.AsSpan(at).StartsWith("delegate*", StringComparison.Ordinal))
            {
                var lt = text.IndexOf('<', at);
                if (lt < 0)
                    return null;
                var close = MatchingClose(text, lt, '<', '>');
                if (close < 0)
                    return null;
                var parts = new List<string>();
                foreach (var part in TopLevel(text[(lt + 1)..close], ','))
                {
                    if (Parameter(part, qualified) is not { } type)
                        return null;
                    parts.Add(type);
                }
                core = "fnptr<" + string.Join(",", parts) + ">";
                at = close + 1;
            }
            else
            {
                var start = at;
                var names = new StringBuilder();
                // A type nested in a generic type (`Outer<T>.Inner<U>`) is, in metadata, `Inner` with the
                // outer type's arguments ahead of its own: `Inner<T,U>`.
                var args = new List<string>();
                while (true)
                {
                    var from = at;
                    while (at < text.Length && (IdentifierPart(text[at]) || text[at] is '@' or '.' or ':'))
                        at++;
                    if (at == from)
                        return null;
                    names.Append(text, from, at - from);
                    if (at < text.Length && text[at] == '<')
                    {
                        var close = MatchingClose(text, at, '<', '>');
                        if (close < 0)
                            return null;
                        var own = 0;
                        foreach (var argument in TopLevel(text[(at + 1)..close], ','))
                        {
                            if (Type(argument, qualified) is not { } type)
                                return null;
                            args.Add(type);
                            own++;
                        }
                        if (qualified)
                            names.Append('`').Append(own);
                        at = close + 1;
                        if (at < text.Length && text[at] == '.')
                        {
                            // The qualified form keeps the enclosing names; the canonical one only the last.
                            if (qualified)
                                names.Append('.');
                            else
                                names.Clear();
                            at++;
                            continue;
                        }
                    }
                    break;
                }
                if (start == at)
                    return null;
                core = (qualified ? QualifiedSpelling(names.ToString()) : SimpleName(names.ToString()))
                       + (args.Count > 0 ? "<" + string.Join(",", args) + ">" : "");
            }
            // Suffixes: nullable marks, arrays and pointers, in the order the type spells them.
            var suffix = new StringBuilder();
            while (at < text.Length)
            {
                if (text[at] == '?')
                {
                    suffix.Append('?');
                    at++;
                }
                else if (text[at] == '*')
                {
                    suffix.Append('*');
                    at++;
                }
                else if (text[at] == '[')
                {
                    var close = MatchingClose(text, at, '[', ']');
                    if (close < 0 || text[(at + 1)..close].Any(c => c is not (',' or ' ')))
                        return null;
                    suffix.Append('[').Append(',', text[(at + 1)..close].Count(c => c == ',')).Append(']');
                    at = close + 1;
                }
                else
                    break;
            }
            return core + suffix;
        }

        /// <summary>A type name without namespace or <c>global::</c>, with a C# keyword as its CLR name.</summary>
        private static string SimpleName(string name)
        {
            var colons = name.LastIndexOf("::", StringComparison.Ordinal);
            if (colons >= 0) name = name[(colons + 2)..];
            var dot = name.LastIndexOf('.');
            if (dot >= 0) name = name[(dot + 1)..];
            if (name.StartsWith('@')) name = name[1..];
            return name switch
            {
                "bool" => "Boolean", "byte" => "Byte", "sbyte" => "SByte", "char" => "Char", "short" => "Int16",
                "ushort" => "UInt16", "int" => "Int32", "uint" => "UInt32", "long" => "Int64", "ulong" => "UInt64",
                "nint" => "IntPtr", "nuint" => "UIntPtr", "float" => "Single", "double" => "Double", "decimal" => "Decimal",
                "string" => "String", "object" => "Object", "void" => "Void",
                _ => name,
            };
        }

        /// <summary>
        /// The qualifier of a type C# names by its own syntax, a keyword or a tuple: always the core
        /// library's type, whatever a compilation declares. No C# spelling holds it, so no alias is read as it.
        /// </summary>
        public const string Language = "#";

        /// <summary>
        /// A type name as spelled, without verbatim <c>@</c>s, with a lone C# keyword as its
        /// <see cref="Language"/> <c>System</c> type.
        /// </summary>
        private static string QualifiedSpelling(string name)
        {
            var colons = name.IndexOf("::", StringComparison.Ordinal);
            var alias = colons < 0 ? "" : name[..(colons + 2)];
            var path = name[alias.Length..];
            if (alias.Length == 0 && path.IndexOf('.') < 0 && SimpleName(path) is var clr && clr != path && !path.StartsWith('@'))
                return Language + "System." + clr;
            return alias + string.Join(".", path.Split('.').Select(s => s.StartsWith('@') ? s[1..] : s));
        }

        /// <summary>
        /// Whether the key's qualified parameter <paramref name="spelled"/> (<see cref="Key.QualifiedParameters"/>)
        /// can name the compiled qualified parameter <paramref name="compiled"/> (<see cref="Member.QualifiedParameters"/>)
        /// of a member of the type <paramref name="container"/> (its <see cref="Member.QualifiedParent"/>),
        /// for a pair <see cref="Resolve"/> already matched by simple names. A type keeps its own name
        /// however C# reaches it, so the last segments must agree. A namespace-level type is reached
        /// only through namespaces: a name C# reads from the global namespace (a keyword, <c>global::</c>)
        /// must be it, and any other must end it, since generation may qualify it further after the key
        /// is taken. A nested type may also be reached through a type that inherits it
        /// (<c>Deps.Child.Value</c> for <c>Deps.Base+Value</c>), so any spelling of its name can name it,
        /// except a single-segment <c>global::Value</c>, and a keyword or tuple (<see cref="Language"/>),
        /// which is always the core library's type. One reading is narrower: a relative name whose first segment
        /// (<c>Outer`1</c>) the emitted code reads as the container's own nested type
        /// (<paramref name="ownNestedType"/>, which the caller answers only where the emitted spelling is
        /// the key's and C# lookup finds that type first) is that type, or a type nested in or inherited
        /// by it. Anything this cannot pair is allowed, so the answer is never narrower than the compiled
        /// member's real reading.
        /// </summary>
        public static bool CanName(string spelled, string compiled, string container, Func<string, bool> ownNestedType)
        {
            var names = NameTokens(spelled);
            var targets = NameTokens(compiled);
            if (names.Count != targets.Count)
                return true;
            for (var i = 0; i < names.Count; i++)
                if (!CanNameType(names[i], targets[i], container, ownNestedType))
                    return false;
            return true;
        }

        private static bool CanNameType(string spelled, string compiled, string container, Func<string, bool> ownNestedType)
        {
            // A type parameter is the member's or its type's own, whatever the key calls it.
            if (compiled.StartsWith('!'))
                return true;
            if (spelled == "fnptr" || compiled == "fnptr")
                return spelled == compiled;
            if (spelled.StartsWith(Language, StringComparison.Ordinal))
                return spelled[Language.Length..] == compiled;
            var global = spelled.StartsWith("global::", StringComparison.Ordinal);
            var path = global ? spelled["global::".Length..] : spelled;
            // Another alias qualifier (`extern alias`) names something this index cannot see.
            if (path.Contains("::", StringComparison.Ordinal))
                return true;
            var segments = path.Split('.');
            var full = compiled.Split('.', '+');
            if (segments[^1] != full[^1])
                return false;
            var nested = compiled.Contains('+');
            if (!global && container.Length > 0 && ownNestedType(segments[0]))
                return segments.Length == 1 ? compiled == container + "+" + path : nested;
            if (!nested)
                return global
                    ? path == compiled
                    : segments.Length <= full.Length && full.AsSpan(full.Length - segments.Length).SequenceEqual(segments);
            return !global || segments.Length > 1;
        }

        // The type names of a qualified form, in order: everything between its punctuation.
        private static List<string> NameTokens(string type)
        {
            var tokens = new List<string>();
            var from = -1;
            for (var i = 0; i <= type.Length; i++)
            {
                var name = i < type.Length && type[i] is not ('<' or '>' or ',' or '[' or ']' or '*' or '&' or '?' or '~' or ' ');
                if (name && from < 0)
                    from = i;
                else if (!name && from >= 0)
                {
                    tokens.Add(type[from..i]);
                    from = -1;
                }
            }
            return tokens;
        }

        private static string WithoutElementName(string element)
        {
            var depth = 0;
            for (var i = element.Length - 1; i >= 0; i--)
            {
                if (element[i] is ')' or '>' or ']') depth++;
                else if (element[i] is '(' or '<' or '[') depth--;
                else if (element[i] == ' ' && depth == 0)
                {
                    var head = element[..i].TrimEnd();
                    // `ref int x` would not be a tuple element; a trailing word after a type is its name.
                    return head.Length > 0 && !head.EndsWith(',') ? head : element;
                }
            }
            return element;
        }

        private static string StripGenericArguments(string path)
        {
            var text = new StringBuilder(path.Length);
            var depth = 0;
            foreach (var c in path)
            {
                if (c == '<') depth++;
                else if (c == '>') depth--;
                else if (depth == 0) text.Append(c);
            }
            return text.ToString();
        }

        private static int LastTopLevelDot(string text, int end)
        {
            var depth = 0;
            for (var i = end - 1; i >= 0; i--)
            {
                if (text[i] is '>' or ')' or ']') depth++;
                else if (text[i] is '<' or '(' or '[') depth--;
                else if (text[i] == '.' && depth == 0) return i;
            }
            return -1;
        }

        private static IEnumerable<string> TopLevel(string list, char separator)
        {
            var (depth, from) = (0, 0);
            for (var i = 0; i < list.Length; i++)
            {
                if (list[i] is '(' or '<' or '[') depth++;
                else if (list[i] is ')' or '>' or ']') depth--;
                else if (list[i] == separator && depth == 0)
                {
                    yield return list[from..i];
                    from = i + 1;
                }
            }
            yield return list[from..];
        }

        // A character a C# identifier may hold after its first: letters and letter numbers, digits,
        // combining marks, connectors and formatting characters (C# spec, identifiers). A Swift name
        // the generator writes verbatim may hold any of them.
        private static bool IdentifierPart(char c) => char.GetUnicodeCategory(c) is
            System.Globalization.UnicodeCategory.UppercaseLetter or System.Globalization.UnicodeCategory.LowercaseLetter
            or System.Globalization.UnicodeCategory.TitlecaseLetter or System.Globalization.UnicodeCategory.ModifierLetter
            or System.Globalization.UnicodeCategory.OtherLetter or System.Globalization.UnicodeCategory.LetterNumber
            or System.Globalization.UnicodeCategory.DecimalDigitNumber or System.Globalization.UnicodeCategory.NonSpacingMark
            or System.Globalization.UnicodeCategory.SpacingCombiningMark or System.Globalization.UnicodeCategory.ConnectorPunctuation
            or System.Globalization.UnicodeCategory.Format;

        private static int MatchingClose(string text, int open, char opening, char closing)
        {
            var depth = 0;
            for (var i = open; i < text.Length; i++)
            {
                if (text[i] == opening) depth++;
                else if (text[i] == closing && --depth == 0) return i;
            }
            return -1;
        }

        /// <summary>
        /// Whether a key's canonical parameter <paramref name="key"/> (with its <c>?</c> marks) spells the
        /// compiled parameter <paramref name="compiled"/> (with its <c>?</c> and <c>~</c> marks): the same
        /// types, a <c>?</c> wherever the compiled type is <c>Nullable&lt;T&gt;</c>, and any other <c>?</c>
        /// only on a reference type or a type parameter (<c>~</c>).
        /// </summary>
        public static bool Spells(string key, string compiled)
        {
            int i = 0, j = 0;
            while (true)
            {
                var keyMark = i < key.Length && key[i] == '?';
                var mark = j < compiled.Length && compiled[j] is '?' or '~' ? compiled[j] : '\0';
                if (keyMark || mark != '\0')
                {
                    if (mark == '?' && !keyMark) return false;
                    if (keyMark && mark == '\0') return false;
                    if (keyMark) i++;
                    if (mark != '\0') j++;
                    continue;
                }
                if (i == key.Length || j == compiled.Length)
                    return i == key.Length && j == compiled.Length;
                if (key[i] != compiled[j])
                    return false;
                i++;
                j++;
            }
        }

        /// <summary>
        /// The compiled parameter list <paramref name="parameters"/> with only its <c>Nullable&lt;T&gt;</c>
        /// marks: the CLR signature, which two keys share exactly when they name one member.
        /// </summary>
        public static string ClrSignature(IEnumerable<string> parameters) =>
            string.Join(",", parameters).Replace("~", "", StringComparison.Ordinal);

        /// <summary>The members of one assembly's namespace, by declaring type path and name.</summary>
        public sealed class AssemblyIndex
        {
            /// <summary>
            /// The top-level classes of the namespace that can hold the module <paramref name="module"/>'s
            /// module-level functions: a class named as the module emitter names its holder (the module's
            /// name, or, where the namespace already ends in it, <c>Functions</c>, <c>GlobalFunctions</c>,
            /// <c>{module}Functions</c> or <c>Functions{N}</c>) that the record does not list as a Swift
            /// type (<paramref name="swiftTypes"/>, the record's type keys). The emitter never gives the
            /// holder a name a Swift type already has, and a binding project may add its own partial
            /// declarations to the holder, so the holder is told from a Swift class by the record, not by
            /// its shape.
            /// </summary>
            public IEnumerable<string> FunctionHolders(string? module, IReadOnlySet<string>? swiftTypes = null) =>
                MembersOf("").Where(t => t.Kind == "type" && t.DeclaringKind == "class"
                    && (t.Name == module || t.Name is "Functions" or "GlobalFunctions" || t.Name == module + "Functions"
                        || t.Name.StartsWith("Functions", StringComparison.Ordinal) && t.Name.Length > 9 && t.Name[9..].All(char.IsAsciiDigit))
                    && swiftTypes?.Contains(t.Name) != true)
                    .Select(t => t.Name);

            private readonly Dictionary<(string Parent, string Name), List<Member>> _byName = new();
            private readonly Dictionary<string, Member> _types = new(StringComparer.Ordinal);
            private readonly List<Member> _members = new();
            private readonly Dictionary<string, List<Member>> _byParent = new(StringComparer.Ordinal);

            /// <summary>Every public or protected member and type, in metadata order.</summary>
            public IReadOnlyList<Member> Members => _members;

            /// <summary>The nested C# paths of every public or protected type.</summary>
            public IReadOnlyCollection<string> Types => _types.Keys;

            /// <summary>The qualified name (<see cref="Member.QualifiedParameters"/>'s form) of every public or protected type.</summary>
            public IReadOnlySet<string> QualifiedTypes => _qualifiedTypes;

            private readonly HashSet<string> _qualifiedTypes = new(StringComparer.Ordinal);

            /// <summary>The type at the nested C# path <paramref name="path"/>, or null.</summary>
            public Member? Type(string path) => _types.TryGetValue(path, out var type) ? type : null;

            /// <summary>The members declared directly in the type at <paramref name="path"/>.</summary>
            public IReadOnlyList<Member> MembersOf(string path) => _byParent.TryGetValue(path, out var members) ? members : Array.Empty<Member>();

            internal void Add(Member member)
            {
                _members.Add(member);
                if (!_byParent.TryGetValue(member.ParentPath, out var siblings))
                    _byParent[member.ParentPath] = siblings = new List<Member>();
                siblings.Add(member);
                if (member.Kind == "type")
                {
                    _types[member.Path] = member;
                    _qualifiedTypes.Add(member.QualifiedName);
                }
                var id = (member.ParentPath, member.Name);
                if (!_byName.TryGetValue(id, out var list))
                    _byName[id] = list = new List<Member>();
                list.Add(member);
            }

            /// <summary>
            /// The members <paramref name="key"/> can name: those it spells (<see cref="Spelled"/>), of its
            /// generic arity when any of them has it.
            /// </summary>
            public IReadOnlyList<Member> Resolve(Key key, string? module = null, IReadOnlySet<string>? swiftTypes = null)
            {
                var spelled = Spelled(key, module, swiftTypes);
                if (key.Parameters is null)
                    return spelled;
                // A key's `N is the Swift declaration's generic arity, which also counts a protocol's
                // `Self` and a generic type's own parameters, which the C# member does not declare: it
                // picks between members it spells, but does not refuse the one member it spells.
                var exact = spelled.Where(m => m.GenericArity == key.GenericArity).ToList();
                return exact.Count > 0 ? exact : spelled;
            }

            /// <summary>
            /// Every member <paramref name="key"/> spells, whatever its generic arity: its declaring type
            /// and name, its parameter count, and parameters it spells (<see cref="Spells"/>). A
            /// module-level function's key names no declaring type; it names a member of a class
            /// <see cref="FunctionHolders"/> lists for <paramref name="module"/> and the record's Swift
            /// types <paramref name="swiftTypes"/>.
            /// </summary>
            public IReadOnlyList<Member> Spelled(Key key, string? module = null, IReadOnlySet<string>? swiftTypes = null)
            {
                IEnumerable<Member> named = key.ParentPath.Length > 0
                    ? _byName.TryGetValue((key.ParentPath, key.Name), out var list) ? list : Enumerable.Empty<Member>()
                    : FunctionHolders(module, swiftTypes).SelectMany(h => _byName.TryGetValue((h, key.Name), out var functions) ? functions : Enumerable.Empty<Member>());
                // A module-level function is a static member of its holder; a binding project's instance
                // helper on the holder is not one.
                if (key.ParentPath.Length == 0)
                    named = named.Where(m => m.IsStatic);
                if (key.Parameters is not { } parameters)
                    return named.Where(m => m.Kind is "property" or "field" or "event" or "type").ToList();
                var kind = key.IsIndexer ? "indexer" : key.Name == ".ctor" ? "ctor" : "method";
                return named.Where(m => m.Kind == kind && m.Parameters.Length == parameters.Length
                                        && m.Parameters.Zip(parameters).All(p => Spells(p.Second, p.First))).ToList();
            }
        }

        /// <summary>
        /// Lists the public and protected types and members of <paramref name="namespace"/> (and the
        /// namespaces under it) in the assembly <paramref name="image"/>. A member is listed under its
        /// declaring type's nested C# path within the namespace.
        /// </summary>
        public static AssemblyIndex Index(byte[] image, string @namespace)
        {
            var index = new AssemblyIndex();
            using var pe = new PEReader(ImmutableArray.Create(image));
            if (!pe.HasMetadata)
                return index;
            var md = pe.GetMetadataReader();
            foreach (var handle in md.TypeDefinitions)
            {
                var type = md.GetTypeDefinition(handle);
                if (!type.GetDeclaringType().IsNil)
                    continue;
                // A type of a namespace under the module's (the generated `.SwiftInterop` proxies) is
                // listed under that namespace's relative path, which no type of the module's own
                // namespace shares.
                var ns = md.GetString(type.Namespace);
                if (ns == @namespace)
                    AddType(md, handle, "", Qualified(ns, md.GetString(type.Name)), index);
                else if (ns.StartsWith(@namespace + ".", StringComparison.Ordinal))
                    AddType(md, handle, ns[(@namespace.Length + 1)..], Qualified(ns, md.GetString(type.Name)), index);
            }
            return index;
        }

        // `qualified` is the type's own qualified name (Member.QualifiedName).
        private static void AddType(MetadataReader md, TypeDefinitionHandle handle, string parentPath, string qualified, AssemblyIndex index)
        {
            var type = md.GetTypeDefinition(handle);
            if (!Visible(type.Attributes))
                return;
            var name = WithoutArity(md.GetString(type.Name));
            var path = parentPath.Length > 0 ? parentPath + "." + name : name;
            var separator = Math.Max(qualified.LastIndexOf('+'), qualified.LastIndexOf('.'));
            var qualifiedParent = separator < 0 ? "" : qualified[..separator];
            var isInterface = (type.Attributes & TypeAttributes.Interface) != 0;
            var baseType = BaseTypeName(md, type.BaseType);
            var kind = isInterface ? "interface" : baseType switch
            {
                "System.Enum" => "enum",
                "System.ValueType" => "struct",
                "System.MulticastDelegate" => "delegate",
                _ => "class",
            };
            var generics = type.GetGenericParameters().Select(p => md.GetString(md.GetGenericParameter(p).Name)).ToImmutableArray();
            var isStatic = (type.Attributes & (TypeAttributes.Abstract | TypeAttributes.Sealed)) == (TypeAttributes.Abstract | TypeAttributes.Sealed) && !isInterface;
            index.Add(new Member(parentPath, name, "type", ImmutableArray<string>.Empty, 0, isStatic)
            {
                DeclaringKind = kind,
                Interfaces = type.GetInterfaceImplementations()
                    .Select(i => NestedTypeName(md, md.GetInterfaceImplementation(i).Interface)).ToImmutableArray(),
                Traits = CompilerGenerated(md, type.GetCustomAttributes()),
                QualifiedParent = qualifiedParent,
                QualifiedName = qualified,
            });
            foreach (var nested in type.GetNestedTypes())
                AddType(md, nested, path, qualified + "+" + md.GetString(md.GetTypeDefinition(nested).Name), index);

            var accessors = new HashSet<MethodDefinitionHandle>();
            foreach (var propertyHandle in type.GetProperties())
            {
                var property = md.GetPropertyDefinition(propertyHandle);
                var getter = property.GetAccessors().Getter;
                var setter = property.GetAccessors().Setter;
                if (!getter.IsNil) accessors.Add(getter);
                if (!setter.IsNil) accessors.Add(setter);
                var accessor = new[] { getter, setter }.Where(a => !a.IsNil).Select(md.GetMethodDefinition)
                    .Where(m => VisibleMember(m.Attributes)).Cast<MethodDefinition?>().FirstOrDefault();
                if (accessor is not { } visible)
                    continue;
                var signature = property.DecodeSignature(new Renderer(md), new Context(generics, ImmutableArray<string>.Empty));
                var qualifiedSignature = property.DecodeSignature(new Renderer(md, qualified: true), default);
                var propertyName = md.GetString(property.Name);
                var isIndexer = signature.ParameterTypes.Length > 0;
                index.Add(new Member(path, isIndexer ? IndexerName(propertyName) : propertyName, isIndexer ? "indexer" : "property",
                    signature.ParameterTypes, 0, (visible.Attributes & MethodAttributes.Static) != 0)
                {
                    DeclaringKind = kind,
                    Traits = MethodTraits(visible.Attributes) | CompilerGenerated(md, property.GetCustomAttributes()),
                    ValueType = signature.ReturnType,
                    QualifiedParameters = qualifiedSignature.ParameterTypes,
                    QualifiedParent = qualified,
                });
            }
            foreach (var eventHandle in type.GetEvents())
            {
                var @event = md.GetEventDefinition(eventHandle);
                var adder = @event.GetAccessors().Adder;
                if (!adder.IsNil) accessors.Add(adder);
                if (!@event.GetAccessors().Remover.IsNil) accessors.Add(@event.GetAccessors().Remover);
                if (adder.IsNil || !VisibleMember(md.GetMethodDefinition(adder).Attributes))
                    continue;
                index.Add(new Member(path, md.GetString(@event.Name), "event", ImmutableArray<string>.Empty, 0,
                    (md.GetMethodDefinition(adder).Attributes & MethodAttributes.Static) != 0)
                {
                    DeclaringKind = kind,
                    Traits = MethodTraits(md.GetMethodDefinition(adder).Attributes),
                });
            }
            foreach (var fieldHandle in type.GetFields())
            {
                var field = md.GetFieldDefinition(fieldHandle);
                var attributes = field.Attributes & FieldAttributes.FieldAccessMask;
                if (attributes is not (FieldAttributes.Public or FieldAttributes.Family or FieldAttributes.FamORAssem)
                    || (field.Attributes & FieldAttributes.SpecialName) != 0)
                    continue;
                index.Add(new Member(path, md.GetString(field.Name), "field", ImmutableArray<string>.Empty, 0,
                    (field.Attributes & FieldAttributes.Static) != 0)
                {
                    DeclaringKind = kind,
                    ValueType = field.DecodeSignature(new Renderer(md), new Context(generics, ImmutableArray<string>.Empty)),
                    Traits = ((field.Attributes & FieldAttributes.Literal) != 0 ? MemberTraits.Literal : MemberTraits.None)
                             | CompilerGenerated(md, field.GetCustomAttributes()),
                });
            }
            foreach (var methodHandle in type.GetMethods())
            {
                if (accessors.Contains(methodHandle))
                    continue;
                var method = md.GetMethodDefinition(methodHandle);
                if (!VisibleMember(method.Attributes))
                    continue;
                var methodName = md.GetString(method.Name);
                if (methodName == ".cctor")
                    continue;
                var methodGenerics = method.GetGenericParameters().Select(p => md.GetString(md.GetGenericParameter(p).Name)).ToImmutableArray();
                var signature = method.DecodeSignature(new Renderer(md), new Context(generics, methodGenerics));
                index.Add(new Member(path, methodName, methodName == ".ctor" ? "ctor" : "method", signature.ParameterTypes,
                    signature.GenericParameterCount, (method.Attributes & MethodAttributes.Static) != 0)
                {
                    DeclaringKind = kind,
                    Traits = MethodTraits(method.Attributes) | CompilerGenerated(md, method.GetCustomAttributes()),
                    ValueType = signature.ReturnType,
                    QualifiedParameters = method.DecodeSignature(new Renderer(md, qualified: true), default).ParameterTypes,
                    QualifiedParent = qualified,
                });
            }
        }

        private static MemberTraits MethodTraits(MethodAttributes attributes) =>
            (attributes & MethodAttributes.SpecialName) != 0 ? MemberTraits.SpecialName : MemberTraits.None;

        private static MemberTraits CompilerGenerated(MetadataReader md, CustomAttributeHandleCollection attributes)
        {
            foreach (var handle in attributes)
            {
                var constructor = md.GetCustomAttribute(handle).Constructor;
                var parent = constructor.Kind switch
                {
                    HandleKind.MemberReference => md.GetMemberReference((MemberReferenceHandle)constructor).Parent,
                    HandleKind.MethodDefinition => md.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType(),
                    _ => default(EntityHandle),
                };
                var name = parent.Kind switch
                {
                    HandleKind.TypeReference => md.GetString(md.GetTypeReference((TypeReferenceHandle)parent).Name),
                    HandleKind.TypeDefinition => md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)parent).Name),
                    _ => "",
                };
                if (name == "CompilerGeneratedAttribute")
                    return MemberTraits.CompilerGenerated;
            }
            return MemberTraits.None;
        }

        private static string BaseTypeName(MetadataReader md, EntityHandle handle) => handle.IsNil ? "" : handle.Kind switch
        {
            HandleKind.TypeReference => Qualified(md.GetString(md.GetTypeReference((TypeReferenceHandle)handle).Namespace),
                md.GetString(md.GetTypeReference((TypeReferenceHandle)handle).Name)),
            HandleKind.TypeDefinition => Qualified(md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)handle).Namespace),
                md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)handle).Name)),
            HandleKind.TypeSpecification => BaseTypeName(md, GenericTypeOf(md, (TypeSpecificationHandle)handle)),
            _ => "",
        };

        // A type's namespace-qualified name with its enclosing types (`Ns.Outer.IInner`), each segment
        // without its generic arity, so two interfaces of one name nested in different types stay apart.
        private static string NestedTypeName(MetadataReader md, EntityHandle handle)
        {
            switch (handle.Kind)
            {
                case HandleKind.TypeDefinition:
                {
                    var definition = md.GetTypeDefinition((TypeDefinitionHandle)handle);
                    var declaring = definition.GetDeclaringType();
                    var prefix = declaring.IsNil ? md.GetString(definition.Namespace) : NestedTypeName(md, declaring);
                    return Qualified(prefix, WithoutArity(md.GetString(definition.Name)));
                }
                case HandleKind.TypeReference:
                {
                    var reference = md.GetTypeReference((TypeReferenceHandle)handle);
                    var prefix = reference.ResolutionScope.Kind == HandleKind.TypeReference
                        ? NestedTypeName(md, reference.ResolutionScope) : md.GetString(reference.Namespace);
                    return Qualified(prefix, WithoutArity(md.GetString(reference.Name)));
                }
                case HandleKind.TypeSpecification:
                    return NestedTypeName(md, GenericTypeOf(md, (TypeSpecificationHandle)handle));
                default:
                    return "";
            }
        }

        private static string Qualified(string @namespace, string name) => @namespace.Length > 0 ? @namespace + "." + name : name;

        // A generic base's specification blob: GENERICINST (0x15), CLASS or VALUETYPE, then the type's coded index.
        private static EntityHandle GenericTypeOf(MetadataReader md, TypeSpecificationHandle handle)
        {
            var blob = md.GetBlobReader(md.GetTypeSpecification(handle).Signature);
            if (blob.ReadByte() != 0x15)
                return default;
            blob.ReadByte();
            return blob.ReadTypeHandle();
        }

        // `Item` is the indexer's default name; `[IndexerName]` can rename it, and a key spells every indexer `this[…]`.
        private static string IndexerName(string propertyName) => "Item";

        private static bool Visible(TypeAttributes attributes) =>
            (attributes & TypeAttributes.VisibilityMask) is TypeAttributes.Public or TypeAttributes.NestedPublic
                or TypeAttributes.NestedFamily or TypeAttributes.NestedFamORAssem;

        private static bool VisibleMember(MethodAttributes attributes) =>
            (attributes & MethodAttributes.MemberAccessMask) is MethodAttributes.Public or MethodAttributes.Family or MethodAttributes.FamORAssem;

        private static string WithoutArity(string name)
        {
            var tick = name.IndexOf('`');
            return tick < 0 ? name : name[..tick];
        }

        private readonly record struct Context(ImmutableArray<string> TypeGenerics, ImmutableArray<string> MethodGenerics);

        /// <summary>
        /// Renders a signature type in the canonical form: <c>?</c> after a <c>Nullable&lt;T&gt;</c>'s
        /// <c>T</c>, <c>~</c> after a reference type or a type parameter (which a key may annotate). With
        /// <c>qualified</c>, in <see cref="Member.QualifiedParameters"/>'s form instead.
        /// </summary>
        private sealed class Renderer : ISignatureTypeProvider<string, Context>
        {
            private readonly MetadataReader _md;
            private readonly bool _qualified;
            public Renderer(MetadataReader md, bool qualified = false) => (_md, _qualified) = (md, qualified);

            public string GetPrimitiveType(PrimitiveTypeCode typeCode) => _qualified ? "System." + typeCode : typeCode switch
            {
                PrimitiveTypeCode.String or PrimitiveTypeCode.Object => typeCode + "~",
                PrimitiveTypeCode.TypedReference => "TypedReference",
                _ => typeCode.ToString(),
            };

            public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
                (_qualified ? NestedQualifiedName(reader, handle) : WithoutArity(reader.GetString(reader.GetTypeDefinition(handle).Name)))
                + Mark(reader, rawTypeKind, IsNullable(reader, handle));

            public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
            {
                var reference = reader.GetTypeReference(handle);
                var name = reader.GetString(reference.Name);
                return (_qualified ? NestedQualifiedName(reader, handle) : WithoutArity(name)) + Mark(reader, rawTypeKind,
                    name == "Nullable`1" && reader.GetString(reference.Namespace) == "System");
            }

            public string GetTypeFromSpecification(MetadataReader reader, Context genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
                reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

            // A `Nullable<T>` definition carries the marker `!` until its instantiation replaces it with `T?`.
            private string Mark(MetadataReader reader, byte rawTypeKind, bool nullable) =>
                nullable ? "!" : _qualified || rawTypeKind == (byte)SignatureTypeKind.ValueType ? "" : "~";

            // `Ns.Outer`1.Inner`: the namespace, each enclosing type and the type, each with its own arity.
            private static string NestedQualifiedName(MetadataReader reader, EntityHandle handle)
            {
                switch (handle.Kind)
                {
                    case HandleKind.TypeDefinition:
                    {
                        var definition = reader.GetTypeDefinition((TypeDefinitionHandle)handle);
                        var declaring = definition.GetDeclaringType();
                        return declaring.IsNil
                            ? Qualified(reader.GetString(definition.Namespace), reader.GetString(definition.Name))
                            : NestedQualifiedName(reader, declaring) + "+" + reader.GetString(definition.Name);
                    }
                    case HandleKind.TypeReference:
                    {
                        var reference = reader.GetTypeReference((TypeReferenceHandle)handle);
                        return reference.ResolutionScope.Kind == HandleKind.TypeReference
                            ? NestedQualifiedName(reader, reference.ResolutionScope) + "+" + reader.GetString(reference.Name)
                            : Qualified(reader.GetString(reference.Namespace), reader.GetString(reference.Name));
                    }
                    default:
                        return "";
                }
            }

            private static bool IsNullable(MetadataReader reader, TypeDefinitionHandle handle)
            {
                var definition = reader.GetTypeDefinition(handle);
                return reader.GetString(definition.Name) == "Nullable`1" && reader.GetString(definition.Namespace) == "System";
            }

            public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
            {
                if (genericType.EndsWith('!'))
                    return typeArguments[0] + "?";
                var mark = genericType.EndsWith('~') ? "~" : "";
                var name = mark.Length > 0 ? genericType[..^1] : genericType;
                if (_qualified ? name.StartsWith("System.ValueTuple`", StringComparison.Ordinal) : name == "ValueTuple")
                {
                    var elements = typeArguments.ToList();
                    // An eighth element nests the rest: ValueTuple<T1..T7, ValueTuple<T8..>>.
                    if (elements.Count == 8 && elements[7].StartsWith(_qualified ? "System.ValueTuple`" : "ValueTuple<", StringComparison.Ordinal))
                    {
                        var rest = elements[7];
                        elements.RemoveAt(7);
                        elements.Add(rest[(rest.IndexOf('<') + 1)..^1]);
                    }
                    return (_qualified ? "System.ValueTuple`" + elements.Count : "ValueTuple") + "<" + string.Join(",", elements) + ">";
                }
                return name + "<" + string.Join(",", typeArguments) + ">" + mark;
            }

            public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', shape.Rank - 1) + "]" + (_qualified ? "" : "~");
            public string GetSZArrayType(string elementType) => elementType + (_qualified ? "[]" : "[]~");
            public string GetByReferenceType(string elementType) => "&" + elementType;
            public string GetPointerType(string elementType) => elementType + "*";
            public string GetPinnedType(string elementType) => elementType;
            public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
            // The qualified form names a type parameter by its position, which the CLR signature holds.
            public string GetGenericMethodParameter(Context genericContext, int index) => _qualified ? "!!" + index :
                (index < genericContext.MethodGenerics.Length ? genericContext.MethodGenerics[index] : "!!" + index) + "~";
            public string GetGenericTypeParameter(Context genericContext, int index) => _qualified ? "!" + index :
                (index < genericContext.TypeGenerics.Length ? genericContext.TypeGenerics[index] : "!" + index) + "~";
            public string GetFunctionPointerType(MethodSignature<string> signature) =>
                "fnptr<" + string.Join(",", signature.ParameterTypes.Append(signature.ReturnType)) + ">";
        }
    }
}
