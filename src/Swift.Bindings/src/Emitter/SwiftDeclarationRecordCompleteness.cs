// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Member = BindingsGeneration.SwiftDeclarationRecordKeys.Member;
using MemberTraits = BindingsGeneration.SwiftDeclarationRecordKeys.MemberTraits;

namespace BindingsGeneration
{
    /// <summary>
    /// Checks a module's Swift declaration record against the compiled assembly it describes. The
    /// package merge compares records to refuse a copy whose C# name now means another Swift
    /// declaration (SWIFTBIND138), so a public member the record does not name is a name the merge
    /// cannot check. Every public or protected type and member of the module's namespace must
    /// therefore be named by a record key, or be matched by one of the named <see cref="Rules"/>,
    /// each of which says why no Swift declaration can move the names it matches. A member neither
    /// covers is a failure, so a new emitted kind fails until it is recorded or a rule is written
    /// for it. Every record key must also name a member the assembly declares: a key that names
    /// nothing describes a signature the binding does not have, and the member it means goes
    /// unchecked.
    /// </summary>
    public static class SwiftDeclarationRecordCompleteness
    {
        /// <summary>What a rule reads besides the member: the assembly's index and the module's namespace and Swift name.</summary>
        /// <param name="Index">The module's public and protected types and members.</param>
        /// <param name="Namespace">The module's namespace.</param>
        /// <param name="Module">The module's Swift name, as its record states it.</param>
        public sealed record Scope(SwiftDeclarationRecordKeys.AssemblyIndex Index, string Namespace, string? Module)
        {
            /// <summary>
            /// The assembly the index was read from, whose other namespaces hold the interfaces a proxy's
            /// interface refines from another binding. Without it, no such interface is found, so no
            /// member is taken to implement one.
            /// </summary>
            public byte[]? Image { get; init; }

            private readonly Dictionary<string, SwiftDeclarationRecordKeys.AssemblyIndex> _namespaces = new(StringComparer.Ordinal);

            /// <summary>
            /// The interface or type of the namespace-qualified name <paramref name="qualified"/> in
            /// another namespace of <see cref="Image"/>, with that namespace's index; null when the image
            /// does not declare it.
            /// </summary>
            public (SwiftDeclarationRecordKeys.AssemblyIndex Index, Member Type)? ForeignType(string qualified)
            {
                if (Image is null)
                    return null;
                // The longest namespace prefix whose index declares the rest as a nested path.
                for (var dot = qualified.LastIndexOf('.'); dot > 0; dot = qualified.LastIndexOf('.', dot - 1))
                {
                    var ns = qualified[..dot];
                    if (!_namespaces.TryGetValue(ns, out var index))
                        _namespaces[ns] = index = SwiftDeclarationRecordKeys.Index(Image, ns);
                    if (index.Type(qualified[(dot + 1)..]) is { } type)
                        return (index, type);
                }
                return null;
            }
        }

        /// <summary>A fixed-name rule.</summary>
        /// <param name="Name">The rule's name, as the report counts it.</param>
        /// <param name="Justification">Why no Swift declaration can move a name the rule matches.</param>
        /// <param name="Matches">Whether the rule covers a member or type.</param>
        public sealed record Rule(string Name, string Justification, Func<Member, Scope, bool> Matches);

        /// <summary>The result of <see cref="Check"/>.</summary>
        /// <param name="UnresolvedKeys">
        /// Record keys that name no member the assembly declares (failed), including a key that spells
        /// a parameter differently from the member it was written for: that member is then uncovered too.
        /// </param>
        public sealed record Report(
            int Listed,
            int Recorded,
            IReadOnlyDictionary<string, int> ByRule,
            IReadOnlyList<Member> Uncovered,
            IReadOnlyList<string> UnresolvedKeys)
        {
            public bool Passed => Uncovered.Count == 0 && UnresolvedKeys.Count == 0;
        }

        /// <summary>
        /// Checks the record <paramref name="record"/> of <paramref name="namespace"/> against the
        /// assembly <paramref name="image"/>, with <paramref name="rules"/> (default <see cref="Rules"/>).
        /// </summary>
        public static Report Check(byte[] image, string @namespace, byte[] record, IReadOnlyList<Rule>? rules = null)
        {
            rules ??= Rules;
            var index = SwiftDeclarationRecordKeys.Index(image, @namespace);
            using var document = JsonDocument.Parse(record);
            var module = document.RootElement.TryGetProperty("module", out var moduleName) ? moduleName.GetString() : null;
            var recorded = new HashSet<Member>(ReferenceEqualityComparer.Instance);
            var swiftTypes = new HashSet<string>(StringComparer.Ordinal);
            var unresolved = new List<string>();
            if (document.RootElement.TryGetProperty("types", out var types))
            {
                foreach (var type in types.EnumerateObject())
                {
                    swiftTypes.Add(type.Name);
                    if (index.Type(type.Name) is { } member)
                        recorded.Add(member);
                    else
                        unresolved.Add($"type {type.Name} => {type.Value.GetString()}");
                }
            }
            var candidates = new List<IReadOnlyList<Member>>();
            if (document.RootElement.TryGetProperty("members", out var members))
            {
                foreach (var entry in members.EnumerateObject())
                {
                    var key = SwiftDeclarationRecordKeys.Parse(entry.Name);
                    var resolved = key is null ? Array.Empty<Member>() : index.Resolve(key, module, swiftTypes);
                    if (resolved.Count > 0)
                        candidates.Add(resolved);
                    else
                        unresolved.Add($"{entry.Name} => {entry.Value.GetString()}");
                }
            }
            // One key names one member. Keys that resolve to the same members (two parameter types that
            // differ only by namespace, which a key does not spell) cover as many of them as there are
            // keys: a maximum matching of keys to members credits each member at most one key.
            foreach (var member in MatchKeys(candidates))
                recorded.Add(member);

            var scope = new Scope(index, @namespace, module) { Image = image };
            var byRule = rules.ToDictionary(r => r.Name, _ => 0, StringComparer.Ordinal);
            var uncovered = new List<Member>();
            foreach (var member in index.Members)
            {
                if (recorded.Contains(member))
                    continue;
                var rule = rules.FirstOrDefault(r => r.Matches(member, scope));
                if (rule is null)
                    uncovered.Add(member);
                else
                    byRule[rule.Name]++;
            }
            return new Report(index.Members.Count, recorded.Count, byRule, uncovered, unresolved);
        }

        // Kuhn's augmenting-path matching of keys (each a list of the members it resolves to) to
        // members. Returns the matched members.
        private static IEnumerable<Member> MatchKeys(IReadOnlyList<IReadOnlyList<Member>> keys)
        {
            var owner = new Dictionary<Member, int>(ReferenceEqualityComparer.Instance);
            for (var k = 0; k < keys.Count; k++)
                Augment(k, new HashSet<Member>(ReferenceEqualityComparer.Instance));
            return owner.Keys;

            bool Augment(int k, HashSet<Member> seen)
            {
                foreach (var member in keys[k])
                {
                    if (!seen.Add(member))
                        continue;
                    if (!owner.TryGetValue(member, out var other) || Augment(other, seen))
                    {
                        owner[member] = k;
                        return true;
                    }
                }
                return false;
            }
        }

        // The runtime's own types a plumbing constructor takes. A Swift parameter never projects to one.
        private static readonly HashSet<string> RuntimeParameterTypes = new(StringComparer.Ordinal)
        {
            "SwiftInheritanceChain", "NativeHandle", "SwiftHandle", "SwiftObjectOwnership",
        };

        private static bool IsRuntimeType(string canonical)
        {
            var name = canonical.TrimEnd('~', '?');
            return RuntimeParameterTypes.Contains(name) || name.StartsWith("ExistentialContainer", StringComparison.Ordinal);
        }

        // A plumbing constructor: a runtime type first, then runtime types or flags (a Boolean ownership
        // flag after an existential container). A constructor of ordinary types (IntPtr, Boolean) is a
        // Swift initializer's, which the record must name.
        private static bool IsPlumbingConstructor(Member member) =>
            member.Parameters.Length > 0 && IsRuntimeType(member.Parameters[0])
            && member.Parameters.Skip(1).All(p => IsRuntimeType(p) || p == "Boolean");

        private static string Generic(string canonical)
        {
            var name = canonical.TrimEnd('~', '?');
            var angle = name.IndexOf('<');
            return angle < 0 ? name : name[..angle];
        }

        // The runtime's handle, payload and tag members, by name and by the runtime type each has. A
        // Swift property of one of these names has a Swift type, and is recorded under its own key.
        private static bool IsPlumbingProperty(Member member, Scope scope) => member.Name switch
        {
            "Payload" => Generic(member.ValueType) is "SwiftSafeHandle" or "SwiftClassHandle",
            "PayloadBuffer" => Generic(member.ValueType) == "PayloadBuffer",
            "PayloadConstructionSemantics" => member.ValueType == "PayloadConstructionSemantics",
            "Tag" => Generic(member.ValueType) == "CaseTag",
            "Handle" => member.ValueType is "IntPtr" or "NativeHandle" && ImplementsRuntimeInterface(member, scope),
            _ => false,
        };

        private static Member? Declaring(Member member, Scope scope) => scope.Index.Type(member.ParentPath);

        private static bool ImplementsRuntimeInterface(Member member, Scope scope) =>
            DeclaredIn(member, scope, t => t.Interfaces.Any(i => i.StartsWith("Swift.Runtime.", StringComparison.Ordinal)));

        private static bool DeclaredIn(Member member, Scope scope, Func<Member, bool> type) =>
            Declaring(member, scope) is { } declaring && type(declaring);

        private static bool IsType(Member member) => member.Kind == "type";

        private static bool IsStaticClass(Member type) => IsType(type) && type.DeclaringKind == "class" && type.IsStatic;

        // A payload enum's case members are named from a case: a `CaseTag` constant of the same name. A
        // case member whose name collides with its enum's is suffixed `Value` (`Payload.PayloadValue`).
        private static bool IsCaseMember(Member member, Scope scope)
        {
            if (!(member.IsStatic || member.Kind == "method"))
                return false;
            var name = member.Name.StartsWith("TryGet", StringComparison.Ordinal) && member.Kind == "method" ? member.Name["TryGet".Length..] : member.Name;
            var cases = scope.Index.MembersOf(member.ParentPath + ".CaseTag");
            return cases.Any(c => c.Kind == "field" && (c.Name == name || name.EndsWith("Value", StringComparison.Ordinal) && c.Name == name[..^"Value".Length]));
        }

        // A protocol composition's interface: `I{A}And{B}`, declaring nothing of its own over two or more components.
        private static bool IsCompositionInterface(Member? type, Scope scope) =>
            type is { Kind: "type", DeclaringKind: "interface" } && type.Interfaces.Length >= 2
            && type.Name.Contains("And", StringComparison.Ordinal) && scope.Index.MembersOf(type.Path).All(m => m.Kind == "type");

        // The proxy of a composition interface: `{A}And{B}Proxy` implementing `I{A}And{B}`.
        private static bool IsCompositionProxy(Member? type, Scope scope) =>
            type is { Kind: "type", DeclaringKind: "class" } && type.Name.EndsWith("Proxy", StringComparison.Ordinal)
            && type.Interfaces.Select(Leaf).Contains("I" + type.Name[..^"Proxy".Length])
            && IsCompositionInterface(scope.Index.Type(Qualify(type.ParentPath, "I" + type.Name[..^"Proxy".Length])), scope);

        // A member of a composition proxy that implements a requirement of one of its components.
        private static bool IsCompositionProxyMember(Member member, Scope scope) =>
            Declaring(member, scope) is { } proxy && IsCompositionProxy(proxy, scope)
            && scope.Index.Type(Qualify(proxy.ParentPath, "I" + proxy.Name[..^"Proxy".Length])) is { } composition
            && InterfaceDeclares(scope.Index, composition, member, scope, new HashSet<string>(StringComparer.Ordinal));

        // The interface a protocol proxy implements: `{Name}Proxy` in a namespace under the module's
        // implements `I{Name}`, which the module declares.
        private static Member? ProxiedInterface(Member? type, Scope scope)
        {
            if (type is not { Kind: "type", DeclaringKind: "class" } || type.ParentPath.Length == 0 || scope.Index.Type(type.ParentPath) is not null
                || !type.Name.EndsWith("Proxy", StringComparison.Ordinal))
                return null;
            var protocol = type.Name[..^"Proxy".Length];
            // NameProvider names a protocol's interface I{Name}, or ISwift{Name} where I{Name} would
            // collide with a runtime or System interface.
            foreach (var name in new[] { "I" + protocol, "ISwift" + protocol })
            {
                // The interface the proxy implements, found by its path: a nested and a top-level
                // protocol of one name are two interfaces.
                if (type.Interfaces.Where(i => Leaf(i) == name).Select(i => ModuleType(i, scope))
                        .FirstOrDefault(t => t is { DeclaringKind: "interface" } && !t.Path.StartsWith(type.ParentPath + ".", StringComparison.Ordinal)) is { } iface)
                    return iface;
            }
            return null;
        }

        // A member of a protocol proxy that implements one of the interface's own members (by name and
        // kind), or the proxy plumbing: constructors taking the interface (and an ownership mode) and the
        // witness table handle.
        private static bool IsProxyMember(Member member, Scope scope)
        {
            var proxy = Declaring(member, scope);
            if (ProxiedInterface(proxy, scope) is not { } iface)
                return false;
            if (member.Kind == "ctor")
                return member.Parameters.Length >= 1 && member.Parameters[0].TrimEnd('~') == iface.Name
                       && member.Parameters.Skip(1).All(p => p == "ProxyImplOwnership");
            if (member.Kind == "property" && member.Name == "ProtocolWitnessTableHandle")
                return true;
            return InterfaceDeclares(scope.Index, iface, member, scope, new HashSet<string>(StringComparer.Ordinal));
        }

        // Whether the interface, or an interface it extends, declares a member of the member's name and
        // kind. An interface of another binding's namespace (a dependency's protocol the interface
        // refines) is read from the same assembly; one the assembly does not declare, or one of the
        // runtime or the base class library, declares nothing a Swift requirement projects to, so a
        // member only it could declare is not taken to implement it.
        private static bool InterfaceDeclares(SwiftDeclarationRecordKeys.AssemblyIndex index, Member iface, Member member, Scope scope, HashSet<string> seen)
        {
            if (!seen.Add(index.GetHashCode() + ":" + iface.Path))
                return false;
            if (index.MembersOf(iface.Path).Any(m => m.Name == member.Name && m.Kind == member.Kind))
                return true;
            foreach (var parentName in iface.Interfaces)
            {
                if (ModuleType(parentName, scope) is { DeclaringKind: "interface" } parent)
                {
                    if (InterfaceDeclares(scope.Index, parent, member, scope, seen))
                        return true;
                }
                else if (IsForeignBindingInterface(parentName, scope) && scope.ForeignType(parentName) is { Type.DeclaringKind: "interface" } foreign
                         && InterfaceDeclares(foreign.Index, foreign.Type, member, scope, seen))
                {
                    return true;
                }
            }
            return false;
        }

        // An interface of another binding's namespace: neither the module's own, nor the runtime's
        // (Swift.*), nor the base class library's (System.*).
        private static bool IsForeignBindingInterface(string qualified, Scope scope)
        {
            var dot = qualified.LastIndexOf('.');
            var ns = dot < 0 ? "" : qualified[..dot];
            return ns.Length > 0 && ns != scope.Namespace && !ns.StartsWith(scope.Namespace + ".", StringComparison.Ordinal)
                   && ns != "System" && !ns.StartsWith("System.", StringComparison.Ordinal)
                   && ns != "Swift" && !ns.StartsWith("Swift.", StringComparison.Ordinal);
        }

        // The ISwiftObject members, by their runtime signatures and results: static TypeMetadata
        // GetTypeMetadata(), static ISwiftObject NewFromPayload(IntPtr), static ProtocolConformanceDescriptor
        // GetProtocolConformanceDescriptor<TProtocol>() and the instance int MarshalToSwift(ref Span<byte>).
        // A Swift member of one of these names has Swift's parameters and result, so it does not match.
        private static bool IsPlumbingMethod(Member m) => (m.Name, m.IsStatic, m.GenericArity, string.Join(",", m.Parameters), m.ValueType.TrimEnd('~')) is
            ("GetTypeMetadata", true, 0, "", "TypeMetadata") or ("NewFromPayload", true, 0, "IntPtr", "ISwiftObject")
            or ("GetProtocolConformanceDescriptor", true, 1, "", "ProtocolConformanceDescriptor") or ("MarshalToSwift", false, 0, "&Span<Byte>", "Int32");

        // A function holder as the module emitter writes it: a non-static class with no interfaces, and
        // only static members besides its parameterless constructor. An unrecorded Swift class of a
        // holder's name implements the runtime's interfaces, and a caseless Swift enum emits as a static
        // class, so either stays uncovered.
        private static bool IsGeneratedFunctionHolder(string name, Scope scope) =>
            scope.Index.FunctionHolders(scope.Module).Contains(name) && scope.Index.Type(name) is { IsStatic: false, Interfaces.IsEmpty: true }
            && scope.Index.MembersOf(name).All(m => m.IsStatic && m.Kind != "type" || m.Kind == "ctor" && m.Parameters.IsEmpty);

        // The module's type of a namespace-qualified name (`Module.Outer.IInner`), or null for another namespace's.
        private static Member? ModuleType(string qualified, Scope scope) =>
            qualified.StartsWith(scope.Namespace + ".", StringComparison.Ordinal) ? scope.Index.Type(qualified[(scope.Namespace.Length + 1)..]) : null;

        private static string Leaf(string qualified) => qualified[(qualified.LastIndexOf('.') + 1)..];

        private static string Qualify(string parent, string name) => parent.Length > 0 ? parent + "." + name : name;

        /// <summary>The fixed-name rules: names no Swift declaration chooses.</summary>
        public static IReadOnlyList<Rule> Rules { get; } = new Rule[]
        {
            new("lifecycle-and-equality",
                "Dispose, Finalize, Equals, GetHashCode and ToString are the names .NET's object, IDisposable and IEquatable<T> contracts fix; the runtime emits them, and a Swift member of one of these names is recorded under its own key.",
                (m, _) => m.Kind == "method" && m.Name is "Dispose" or "Finalize" or "Equals" or "GetHashCode" or "ToString"),
            new("operators",
                "A C# operator's name is fixed by its symbol (op_Equality for ==), and its operands are its signature: two Swift declarations of one symbol and operand types are one Swift operator.",
                (m, _) => m.Kind == "method" && (m.Traits & MemberTraits.SpecialName) != 0 && m.Name.StartsWith("op_", StringComparison.Ordinal)),
            new("runtime-handle-plumbing",
                "The handle, payload, tag and metadata plumbing the runtime gives every projected type (each matched by name and runtime type: the _handle field and Payload property of a SwiftClassHandle or SwiftSafeHandle, PayloadBuffer, the CaseTag-typed Tag, PayloadConstructionSemantics, a Swift.Runtime type's IntPtr Handle; the ISwiftObject members by their runtime signatures, static GetTypeMetadata(), NewFromPayload(IntPtr) and GetProtocolConformanceDescriptor<TProtocol>() and the instance MarshalToSwift(ref Span<byte>), on a type implementing a Swift.Runtime interface; constructors whose first parameter is a runtime type such as SwiftInheritanceChain, NativeHandle or an ExistentialContainer): fixed names and runtime types, which no Swift declaration projects to.",
                (m, s) => m.Kind switch
                {
                    "field" => m.Name is "_handle" && Generic(m.ValueType) == "SwiftClassHandle",
                    "property" => IsPlumbingProperty(m, s),
                    "method" => IsPlumbingMethod(m) && ImplementsRuntimeInterface(m, s),
                    "ctor" => IsPlumbingConstructor(m),
                    _ => false,
                }),
            new("compiler-generated",
                "Members the C# compiler generates (records, iterators, closures) carry names a Swift declaration cannot spell.",
                (m, _) => (m.Traits & MemberTraits.CompilerGenerated) != 0 || m.Name.StartsWith('<')),
            new("delegate-members",
                "A delegate type's Invoke, BeginInvoke, EndInvoke and constructor are fixed by the CLR; the delegate type itself is recorded or matched by its own rule.",
                (m, s) => m.Kind != "type" && DeclaredIn(m, s, t => t.DeclaringKind == "delegate")),
            new("case-constants",
                "Simple-enum constants and CaseTag constants: their values derive from the Swift case, and the package merge's compiled-in value check compares them across copies (SWIFTBIND138 M7).",
                (m, s) => m.Kind == "field" && (m.Traits & MemberTraits.Literal) != 0
                          && DeclaredIn(m, s, t => t.DeclaringKind == "enum" || t.Name == "CaseTag")),
            new("payload-case-members",
                "A payload enum's case factories, TryGet<Case> accessors and payload-less case properties are named from their case (suffixed Value when the case shares its enum's name): each has a CaseTag constant of its name, which the merge's value check compares, so the member cannot move without its constant.",
                IsCaseMember),
            new("raw-value-conversions",
                "FromRawValue, ToRawValue, FromSwiftRawValue, ToSwiftRawValue and the enum Matches helper are the raw-value contract of an enum projection, named by the runtime; their signatures are the enum and its raw type.",
                (m, s) => m.Kind == "method" && m.Name is "FromRawValue" or "ToRawValue" or "FromSwiftRawValue" or "ToSwiftRawValue"
                          || m.Kind == "method" && m.Name == "Matches" && DeclaredIn(m, s, t => s.Index.Type(t.Path + ".CaseTag") is not null)),
            new("swift-protocol-contracts",
                "Members the generator names from a Swift standard-library conformance rather than from a declaration: Codable's EncodeToJson and DecodeFromJson, an AsyncSequence's GetAsyncEnumerator(CancellationToken), an OptionSet's Contains(Self) and a CaseIterable's AllCases. A type has at most one conformance to each protocol, so one name means one conformance.",
                (m, s) => m.Kind == "method" && m.Name is "EncodeToJson" or "DecodeFromJson"
                          || m.Kind == "method" && m.Name == "GetAsyncEnumerator" && m.Parameters.SequenceEqual(new[] { "CancellationToken" })
                          || m.Kind == "method" && m.Name == "Contains" && m.Parameters.Length == 1
                             && m.Parameters[0].TrimEnd('~') == m.ParentPath[(m.ParentPath.LastIndexOf('.') + 1)..]
                          || m.Kind == "property" && m.IsStatic && m.Name == "AllCases"),
            new("module-function-holder",
                "The class holding a module's module-level functions (Functions, or the other names the module emitter falls back to); each function is recorded under its own key, and the class's implicit constructor is the C# default.",
                (m, s) => IsType(m) && m.ParentPath.Length == 0 && IsGeneratedFunctionHolder(m.Name, s)
                          || m.Kind == "ctor" && m.Parameters.Length == 0 && IsGeneratedFunctionHolder(m.ParentPath, s)),
            new("foreign-type-hosts",
                "A class this module declares under another module's Swift type name holds only the types this module's extensions of that type nest in it (each recorded under its own path), and its implicit constructor is the C# default: its name is the foreign type's, which names only that type.",
                (m, s) => (IsType(m) ? m : m.Kind == "ctor" && m.Parameters.Length == 0 ? Declaring(m, s) : null) is { DeclaringKind: "class", IsStatic: false } host
                          && s.Index.MembersOf(host.Path).All(x => x.Kind == "type" || x.Kind == "ctor" && x.Parameters.Length == 0)
                          && s.Index.MembersOf(host.Path).Any(x => x.Kind == "type")),
            new("generated-containers",
                "Static classes the generator names from the type whose members they hold: extension classes (*Extensions, *CsmExtensions, *KvoExtensions, the mangled constrained-extension classes), key-path containers and factories (*KeyPaths, *Factory) and base-call trampolines (*BaseTrampolines). The name is formed from the extended or conforming type's C# name, which only that type has; each member is recorded, or matched, on its own.",
                (m, _) => IsStaticClass(m) && (m.Name.EndsWith("Extensions", StringComparison.Ordinal) || m.Name.EndsWith("KeyPaths", StringComparison.Ordinal)
                                               || m.Name.EndsWith("Factory", StringComparison.Ordinal) || m.Name.EndsWith("BaseTrampolines", StringComparison.Ordinal))),
            new("case-tag-and-buffer-types",
                "A payload enum's discriminator enum is CaseTag, and a frozen struct with reference fields keeps its layout in a nested Buffer struct: fixed names nested in the projected type, which no Swift declaration chooses.",
                (m, _) => IsType(m) && m.ParentPath.Length > 0 && (m.Name == "CaseTag" && m.DeclaringKind == "enum" || m.Name == "Buffer" && m.DeclaringKind == "struct")),
            new("case-floor-helpers",
                "__{Enum}CaseFloors and its Require({Enum}) check an enum's case availability floors: named from the enum, which only that enum has, and taking it.",
                (m, s) => IsType(m) && m.Name.StartsWith("__", StringComparison.Ordinal) && m.Name.EndsWith("CaseFloors", StringComparison.Ordinal)
                          || m.Kind == "method" && m.Name == "Require" && DeclaredIn(m, s, t => t.Name.StartsWith("__", StringComparison.Ordinal) && t.Name.EndsWith("CaseFloors", StringComparison.Ordinal))),
            new("swift-object-keys",
                "__SwiftObjectKeys and the types nested in it carry the runtime keys of Swift types used as dictionary keys or optional payloads; each nested name spells the full Swift type identity (module, nesting, generic arguments), so one name is one Swift type.",
                (m, _) => IsType(m) && (m.Path == "__SwiftObjectKeys" || m.ParentPath == "__SwiftObjectKeys" || m.ParentPath.StartsWith("__SwiftObjectKeys.", StringComparison.Ordinal))),
            new("protocol-compositions",
                "A protocol composition A & B projects to I{A}And{B}, which declares nothing of its own, and to its proxy {A}And{B}Proxy, whose members implement the components' requirements. The name is formed from the components, and each requirement is recorded under its component's interface, so a requirement moving in a component moves its recorded key.",
                (m, s) => IsCompositionInterface(m, s) || IsCompositionProxy(m, s) || m.Kind != "type" && IsCompositionProxyMember(m, s)),

            new("protocol-proxies",
                "A protocol's proxy, {Name}Proxy in the module's SwiftInterop namespace, implements the protocol's interface I{Name}: its name is formed from the protocol's, each member it implements is recorded under the interface (so it moves only with the interface's member), and its constructors (the interface and an ownership mode) and ProtocolWitnessTableHandle are the runtime's existential plumbing.",
                (m, s) => ProxiedInterface(m, s) is not null || m.Kind != "type" && IsProxyMember(m, s)),
        };
    }
}
