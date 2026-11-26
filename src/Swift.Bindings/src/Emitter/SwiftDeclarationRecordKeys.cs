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
    /// apart the overloads of one member name, which share their declaring type.
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
        }

        /// <summary>A public or protected member of the assembly, as <see cref="Index"/> lists it.</summary>
        /// <param name="Kind">method, ctor, property, indexer, field, event or type.</param>
        /// <param name="Parameters">Canonical parameter types with <c>?</c> and <c>~</c> marks; empty for a property, field, event or type.</param>
        public sealed record Member(string ParentPath, string Name, string Kind, ImmutableArray<string> Parameters, int GenericArity,
            bool IsStatic, bool DeclaringTypeIsInterface)
        {
            /// <summary>The kind of the declaring type (of the type itself, for a type): class, struct, interface, enum or delegate.</summary>
            public string DeclaringKind { get; init; } = "class";

            /// <summary>Metadata facts the completeness check's fixed-name rules read.</summary>
            public MemberTraits Traits { get; init; }

            /// <summary>For a type, its base type's name without namespace or arity; empty otherwise.</summary>
            public string BaseType { get; init; } = "";

            /// <summary>For a type, the names (without namespace or arity) of the interfaces it implements; empty otherwise.</summary>
            public ImmutableArray<string> Interfaces { get; init; } = ImmutableArray<string>.Empty;

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
            /// <summary>A virtual method that overrides its base's slot (virtual, not a new slot).</summary>
            Override = 1,
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
            var list = key[(open + 1)..close];
            if (list.Trim().Length > 0)
                foreach (var parameter in TopLevel(list, ','))
                {
                    if (CanonicalParameter(parameter) is not { } canonical)
                        return null;
                    parameters.Add(canonical);
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
            return new Key(parent, name, parameters.ToImmutable(), arity) { IndexerSpelling = indexer };
        }

        /// <summary>
        /// The canonical form of one parameter of a key (<c>ref string?</c> → <c>&amp;String?</c>), or
        /// null when it does not read as a C# type.
        /// </summary>
        public static string? CanonicalParameter(string spelled)
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
            return CanonicalType(text) is { } type ? (byRef ? "&" : "") + type : null;
        }

        /// <summary>
        /// The canonical form of a C# type as a key spells it, keeping each <c>?</c>; null when
        /// <paramref name="spelled"/> does not read as a type.
        /// </summary>
        public static string? CanonicalType(string spelled)
        {
            var text = spelled.Trim();
            var at = 0;
            var result = ReadType(text, ref at);
            while (at < text.Length && text[at] == ' ') at++;
            return result is not null && at == text.Length ? result : null;
        }

        private static string? ReadType(string text, ref int at)
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
                    if (CanonicalType(WithoutElementName(element.Trim())) is not { } type)
                        return null;
                    elements.Add(type);
                }
                if (elements.Count < 2)
                    return null;
                core = "ValueTuple<" + string.Join(",", elements) + ">";
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
                    if (CanonicalParameter(part) is not { } type)
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
                    while (at < text.Length && (char.IsLetterOrDigit(text[at]) || text[at] is '_' or '@' or '.' or ':'))
                        at++;
                    if (at == from)
                        return null;
                    names.Append(text, from, at - from);
                    if (at < text.Length && text[at] == '<')
                    {
                        var close = MatchingClose(text, at, '<', '>');
                        if (close < 0)
                            return null;
                        foreach (var argument in TopLevel(text[(at + 1)..close], ','))
                        {
                            if (CanonicalType(argument) is not { } type)
                                return null;
                            args.Add(type);
                        }
                        at = close + 1;
                        if (at < text.Length && text[at] == '.')
                        {
                            names.Clear();
                            at++;
                            continue;
                        }
                    }
                    break;
                }
                if (start == at)
                    return null;
                core = SimpleName(names.ToString()) + (args.Count > 0 ? "<" + string.Join(",", args) + ">" : "");
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
            private readonly Dictionary<(string Parent, string Name), List<Member>> _byName = new();
            private readonly Dictionary<string, Member> _types = new(StringComparer.Ordinal);
            private readonly List<Member> _members = new();
            private readonly Dictionary<string, List<Member>> _byParent = new(StringComparer.Ordinal);

            /// <summary>Every public or protected member and type, in metadata order.</summary>
            public IReadOnlyList<Member> Members => _members;

            /// <summary>The nested C# paths of every public or protected type.</summary>
            public IReadOnlyCollection<string> Types => _types.Keys;

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
                    _types[member.Path] = member;
                var id = (member.ParentPath, member.Name);
                if (!_byName.TryGetValue(id, out var list))
                    _byName[id] = list = new List<Member>();
                list.Add(member);
            }

            /// <summary>
            /// The members <paramref name="key"/> can name: its declaring type and name, its parameter
            /// count and generic arity, and parameters it spells (<see cref="Spells"/>). A module-level
            /// function's key names no declaring type, so every type of the namespace is searched.
            /// </summary>
            public IReadOnlyList<Member> Resolve(Key key)
            {
                IEnumerable<Member> named = key.ParentPath.Length > 0
                    ? _byName.TryGetValue((key.ParentPath, key.Name), out var list) ? list : Enumerable.Empty<Member>()
                    : _members.Where(m => m.Name == key.Name && m.Kind is "method");
                if (key.Parameters is not { } parameters)
                    return named.Where(m => m.Kind is "property" or "field" or "event" or "type").ToList();
                var kind = key.IsIndexer ? "indexer" : key.Name == ".ctor" ? "ctor" : "method";
                var spelled = named.Where(m => m.Kind == kind && m.Parameters.Length == parameters.Length
                                               && m.Parameters.Zip(parameters).All(p => Spells(p.Second, p.First))).ToList();
                // A key's `N is the Swift declaration's generic arity, which also counts a protocol's
                // `Self` and a generic type's own parameters, which the C# member does not declare: it
                // picks between members it spells, but does not refuse the one member it spells.
                var exact = spelled.Where(m => m.GenericArity == key.GenericArity).ToList();
                return exact.Count > 0 ? exact : spelled;
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
                var ns = md.GetString(type.Namespace);
                if (ns != @namespace)
                    continue;
                AddType(md, handle, "", index, ImmutableArray<string>.Empty);
            }
            return index;
        }

        private static void AddType(MetadataReader md, TypeDefinitionHandle handle, string parentPath, AssemblyIndex index, ImmutableArray<string> outerGenerics)
        {
            var type = md.GetTypeDefinition(handle);
            if (!Visible(type.Attributes))
                return;
            var name = WithoutArity(md.GetString(type.Name));
            var path = parentPath.Length > 0 ? parentPath + "." + name : name;
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
            index.Add(new Member(parentPath, name, "type", ImmutableArray<string>.Empty, 0, isStatic, false)
            {
                DeclaringKind = kind,
                BaseType = WithoutArity(baseType[(baseType.LastIndexOf('.') + 1)..]),
                Interfaces = type.GetInterfaceImplementations()
                    .Select(i => BaseTypeName(md, md.GetInterfaceImplementation(i).Interface))
                    .Select(n => WithoutArity(n[(n.LastIndexOf('.') + 1)..])).ToImmutableArray(),
                Traits = CompilerGenerated(md, type.GetCustomAttributes()),
            });
            foreach (var nested in type.GetNestedTypes())
                AddType(md, nested, path, index, generics);

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
                var propertyName = md.GetString(property.Name);
                var isIndexer = signature.ParameterTypes.Length > 0;
                index.Add(new Member(path, isIndexer ? IndexerName(propertyName) : propertyName, isIndexer ? "indexer" : "property",
                    signature.ParameterTypes, 0, (visible.Attributes & MethodAttributes.Static) != 0, isInterface)
                {
                    DeclaringKind = kind,
                    Traits = MethodTraits(visible.Attributes) | CompilerGenerated(md, property.GetCustomAttributes()),
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
                    (md.GetMethodDefinition(adder).Attributes & MethodAttributes.Static) != 0, isInterface)
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
                    (field.Attributes & FieldAttributes.Static) != 0, isInterface)
                {
                    DeclaringKind = kind,
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
                    signature.GenericParameterCount, (method.Attributes & MethodAttributes.Static) != 0, isInterface)
                {
                    DeclaringKind = kind,
                    Traits = MethodTraits(method.Attributes) | CompilerGenerated(md, method.GetCustomAttributes()),
                });
            }
        }

        private static MemberTraits MethodTraits(MethodAttributes attributes) =>
            ((attributes & MethodAttributes.Virtual) != 0 && (attributes & MethodAttributes.NewSlot) == 0 ? MemberTraits.Override : MemberTraits.None)
            | ((attributes & MethodAttributes.SpecialName) != 0 ? MemberTraits.SpecialName : MemberTraits.None);

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
        /// <c>T</c>, <c>~</c> after a reference type or a type parameter (which a key may annotate).
        /// </summary>
        private sealed class Renderer : ISignatureTypeProvider<string, Context>
        {
            private readonly MetadataReader _md;
            public Renderer(MetadataReader md) => _md = md;

            public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
            {
                PrimitiveTypeCode.String or PrimitiveTypeCode.Object => typeCode + "~",
                PrimitiveTypeCode.TypedReference => "TypedReference",
                _ => typeCode.ToString(),
            };

            public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
                WithoutArity(reader.GetString(reader.GetTypeDefinition(handle).Name)) + Mark(reader, rawTypeKind, IsNullable(reader, handle));

            public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
            {
                var reference = reader.GetTypeReference(handle);
                var name = reader.GetString(reference.Name);
                return WithoutArity(name) + Mark(reader, rawTypeKind,
                    name == "Nullable`1" && reader.GetString(reference.Namespace) == "System");
            }

            public string GetTypeFromSpecification(MetadataReader reader, Context genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
                reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

            // A `Nullable<T>` definition carries the marker `!` until its instantiation replaces it with `T?`.
            private static string Mark(MetadataReader reader, byte rawTypeKind, bool nullable) =>
                nullable ? "!" : rawTypeKind == (byte)SignatureTypeKind.ValueType ? "" : "~";

            private static bool IsNullable(MetadataReader reader, TypeDefinitionHandle handle)
            {
                var definition = reader.GetTypeDefinition(handle);
                return reader.GetString(definition.Name) == "Nullable`1" && reader.GetString(definition.Namespace) == "System";
            }

            public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
            {
                if (genericType == "Nullable!")
                    return typeArguments[0] + "?";
                var mark = genericType.EndsWith('~') ? "~" : "";
                var name = mark.Length > 0 ? genericType[..^1] : genericType;
                if (name == "ValueTuple")
                {
                    var elements = typeArguments.ToList();
                    // An eighth element nests the rest: ValueTuple<T1..T7, ValueTuple<T8..>>.
                    if (elements.Count == 8 && elements[7].StartsWith("ValueTuple<", StringComparison.Ordinal))
                    {
                        var rest = elements[7];
                        elements.RemoveAt(7);
                        elements.Add(rest["ValueTuple<".Length..^1]);
                    }
                    return "ValueTuple<" + string.Join(",", elements) + ">";
                }
                return name + "<" + string.Join(",", typeArguments) + ">" + mark;
            }

            public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', shape.Rank - 1) + "]~";
            public string GetSZArrayType(string elementType) => elementType + "[]~";
            public string GetByReferenceType(string elementType) => "&" + elementType;
            public string GetPointerType(string elementType) => elementType + "*";
            public string GetPinnedType(string elementType) => elementType;
            public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
            public string GetGenericMethodParameter(Context genericContext, int index) =>
                (index < genericContext.MethodGenerics.Length ? genericContext.MethodGenerics[index] : "!!" + index) + "~";
            public string GetGenericTypeParameter(Context genericContext, int index) =>
                (index < genericContext.TypeGenerics.Length ? genericContext.TypeGenerics[index] : "!" + index) + "~";
            public string GetFunctionPointerType(MethodSignature<string> signature) =>
                "fnptr<" + string.Join(",", signature.ParameterTypes.Append(signature.ReturnType)) + ">";
        }
    }
}
