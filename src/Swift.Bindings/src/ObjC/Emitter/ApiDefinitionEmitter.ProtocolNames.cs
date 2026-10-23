// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace BindingsGeneration.ObjC;

public static partial class ApiDefinitionEmitter
{
    // bgen flattens adopted protocols into adapters, Models, and conforming classes. A child's
    // local dedup cannot rename a collision between two parents: the name must change where the
    // method is declared. Plan those changes before writing any protocol, and replay the same
    // plan when seeding descendants. Keep declaration identity so diamonds do not look like two
    // methods, and inspect actual adoption closures rather than unrelated sibling protocols.
    static IReadOnlyDictionary<ObjCMethodDecl, string> PlanProtocolMethodNames(
        ObjCModule module, Dictionary<string, ObjCProtocolDecl> protocolsByName,
        Dictionary<string, ObjCTypeRef> typedefMap, Dictionary<string, ObjCTypeRef> blockTypedefMap,
        HashSet<string> knownTypes, HashSet<string>? appleSdkTypes, HashSet<string>? enumNames,
        HashSet<string>? localProtocolNames, HashSet<string>? classProtocolClashNames)
    {
        // Collect existing class member names from the same emission path that will write them.
        // This includes inherited/weak properties, locally disambiguated methods and public array
        // overloads. A guessed list of raw property/selector names would miss those projections.
        var classSignatures = new HashSet<string>(StringComparer.Ordinal);
        var classProperties = new HashSet<string>(StringComparer.Ordinal);
        var droppedClasses = ComputeUnresolvableBaseClasses(module, knownTypes, appleSdkTypes);
        var classesByName = new Dictionary<string, ObjCClassDecl>(StringComparer.Ordinal);
        foreach (var cls in module.Classes)
            classesByName.TryAdd(cls.Name, cls);
        var delegates = module.Protocols.Where(p => p.IsDelegateProtocol).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var cls in module.Classes)
            EmitClass(new StringBuilder(), cls, typedefMap, blockTypedefMap, knownTypes, appleSdkTypes,
                localProtocolNames, classProtocolClashNames, delegates, enumNames, droppedClasses,
                protocolsByName, classesByName, [], NullLogger.Instance, diagnostics: null,
                collectMemberNames: (signatures, properties) =>
                {
                    classSignatures.UnionWith(signatures);
                    classProperties.UnionWith(properties);
                });

        var names = new Dictionary<ObjCMethodDecl, string>(ReferenceEqualityComparer.Instance);
        var seeded = false;
        while (true)
        {
            var cache = new Dictionary<string, ProtocolEmissionSet>(StringComparer.Ordinal);
            foreach (var proto in module.Protocols.OrderBy(p => p.Name, StringComparer.Ordinal))
                ComputeProtocolEmissionSet(proto, protocolsByName, cache, typedefMap, blockTypedefMap,
                    knownTypes, appleSdkTypes, enumNames, localProtocolNames, classProtocolClashNames, names);

            if (!seeded)
            {
                // Freeze existing local dedup decisions before freeing any short name. Otherwise
                // renaming one method lets its later sibling steal that name on the next replay,
                // changing an unrelated API (or even which selector an existing C# call invokes).
                foreach (var set in cache.Values)
                    foreach (var entry in set.Methods)
                        names.TryAdd(entry.Key, entry.Value.Name);
                seeded = true;
            }

            var surfaces = cache.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => p.Value.Methods.Values.ToList()).ToList();
            foreach (var cls in module.Classes.Where(c => !droppedClasses.ContainsKey(c.Name)).OrderBy(c => c.Name, StringComparer.Ordinal))
            {
                var methods = new Dictionary<ObjCMethodDecl, ProtocolMethodEmission>(ReferenceEqualityComparer.Instance);
                foreach (var protocol in cls.ProtocolNames)
                    if (cache.TryGetValue(protocol, out var set))
                        foreach (var entry in set.Methods.Where(m => !m.Key.IsOptional))
                            methods.TryAdd(entry.Key, entry.Value);
                surfaces.Add(methods.Values.ToList());
            }

            // Reserve the complete current surface before picking a replacement. That includes
            // full-selector names already in use, and properties which cannot overload methods.
            var signatures = cache.Values.SelectMany(s => s.Methods.Values)
                .Select(m => $"{m.Name}({m.Parameters})").ToHashSet(StringComparer.Ordinal);
            var properties = cache.Values.SelectMany(s => s.PropertyNames).ToHashSet(StringComparer.Ordinal);
            signatures.UnionWith(classSignatures);
            properties.UnionWith(classProperties);
            var collision = surfaces.SelectMany(surface => surface
                    .GroupBy(m => $"{m.Name}({m.Parameters})", StringComparer.Ordinal)
                    // bgen merges identical selectors; a diamond also contributes only one
                    // declaration. Different selectors need independently callable members.
                    .Where(group => group.Select(m => (m.Method.Selector, m.Method.IsInstanceMethod)).Distinct().Count() > 1))
                .FirstOrDefault();
            if (collision == null)
                return names;

            // Preserve one existing name deterministically. Every redeclaration of a losing
            // selector in this surface receives the same replacement; a diamond remains shared.
            var groups = collision.GroupBy(m => (m.Method.Selector, m.Method.IsInstanceMethod))
                .OrderBy(g => g.Key.Selector, StringComparer.Ordinal).ThenBy(g => g.Key.IsInstanceMethod).ToList();
            foreach (var group in groups.Skip(1))
            {
                var member = group.OrderBy(m => m.Owner, StringComparer.Ordinal).First();
                var fullName = SelectorToFullMethodName(member.Method.Selector);
                var candidate = fullName;
                var suffix = 2;
                while (signatures.Contains($"{candidate}({member.Parameters})") || properties.Contains(candidate))
                    candidate = fullName + suffix++;
                foreach (var entry in group)
                    names[entry.Method] = candidate;
                signatures.Add($"{candidate}({member.Parameters})");
            }
        }
    }

}
