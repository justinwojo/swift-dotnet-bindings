// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration.ObjC;

/// <summary>
/// Settles declaration eligibility before either bgen input is written. The same refused-type
/// closure governs record fields, aliases, blocks, methods, properties and free functions.
/// </summary>
internal static class ObjCEmissionEligibility
{
    internal static ObjCModule Prepare(ObjCModule module, ObjCBindingDiagnostics? diagnostics, bool? apiDefinition = null)
    {
        var typedefs = ObjCTypeMapper.BuildResolvedTypedefMap(module);
        var refused = new Dictionary<string, (ObjCSkipReason Reason, string Detail)>(StringComparer.Ordinal);
        var structs = module.Structs.Where(s => !AppleFrameworkRegistry.IsObjCSystemStruct(s.Name)).ToList();
        foreach (var s in structs)
        {
            if (s.HasUnsafeLayout || s.HasBitFields)
                refused[s.Name] = (ObjCSkipReason.UnsupportedConstruct,
                    s.UnsafeLayoutReason ?? "contains bitfield or unsupported native layout");
            else if (s.IsPacked || s.HasExplicitAlignment)
            {
                var measured = s.NativeLayout is { } layout
                    ? $" (Clang diagnostic snapshot: size {layout.SizeBits / 8} bytes, alignment {layout.AlignmentBits / 8} bytes)"
                    : " (Clang layout unavailable)";
                refused[s.Name] = (ObjCSkipReason.UnsupportedConstruct,
                    (s.IsPacked ? "packed native record" : "explicitly aligned native record")
                    + measured + "; no proven bgen by-value carrier");
            }
        }
        foreach (var e in module.Enums)
            if (e.Cases.Any(c => c.HasExplicitValue && c.EvaluatedValue == null && c.Value == null))
                refused[e.Name] = (ObjCSkipReason.UnsupportedConstruct, "explicit enum initializer has no evaluated integer value");

        // The prior StructsAndEnums fixed point is retained here, so both emitters consult it.
        // Ordinary resolvability remains the mapper's policy; only named refused dependencies
        // bypass its permissive uppercase-name heuristic.
        var known = ObjCTypeMapper.BuildKnownMappedTypes();
        foreach (var e in module.Enums) if (!refused.ContainsKey(e.Name)) known.Add(e.Name);
        foreach (var s in structs) if (!refused.ContainsKey(s.Name)) known.Add(s.Name);
        bool changed;
        do
        {
            changed = false;
            foreach (var s in structs)
            {
                if (refused.ContainsKey(s.Name)) continue;
                foreach (var field in s.Fields)
                {
                    var mapped = field.Type.FixedArraySize is > 0
                        ? ObjCTypeMapper.MapType(new ObjCTypeRef { Name = field.Type.Name, IsPointer = field.Type.IsPointer }, typedefMap: typedefs)
                        : ObjCTypeMapper.MapType(field.Type, typedefMap: typedefs);
                    if (mapped == s.Name) continue; // Existing self-pointer → IntPtr carrier.
                    var dependency = RefusedDependency(field.Type);
                    if (dependency != null || !ObjCTypeMapper.IsTypeResolvable(mapped, known, field.Type.Name))
                    {
                        refused[s.Name] = (ObjCSkipReason.UnresolvableType, dependency != null
                            ? $"field '{field.Name}' references refused type '{dependency}'"
                            : $"field '{field.Name}' has unresolvable type '{mapped}'");
                        known.Remove(s.Name);
                        changed = true;
                        break;
                    }
                }
            }
        } while (changed);

        string? RefusedDependency(ObjCTypeRef type, HashSet<string>? aliases = null)
        {
            if (refused.ContainsKey(type.Name)) return type.Name;
            aliases ??= new HashSet<string>(StringComparer.Ordinal);
            if (typedefs.TryGetValue(type.Name, out var target) && aliases.Add(type.Name))
            {
                var dependency = RefusedDependency(target, aliases);
                aliases.Remove(type.Name);
                if (dependency != null) return dependency;
            }
            if (type.PointeeType is { } pointee && RefusedDependency(pointee, aliases) is { } p) return p;
            if (type.BlockReturnType is { } ret && RefusedDependency(ret, aliases) is { } r) return r;
            foreach (var child in type.BlockParams.Concat(type.GenericArgs))
                if (RefusedDependency(child, aliases) is { } d) return d;
            return null;
        }
        bool Keep(string kind, string name, bool apiOwner, IEnumerable<ObjCTypeRef> types)
        {
            var dependency = types.Select(t => RefusedDependency(t)).FirstOrDefault(d => d != null);
            if (dependency == null) return true;
            if (apiDefinition == null || apiDefinition == apiOwner)
                diagnostics?.RecordSkip(kind, name, ObjCSkipReason.UnresolvableType,
                    $"references refused type '{dependency}': {refused[dependency].Detail}");
            return false;
        }
        bool KeepDeclaration(string kind, string name)
        {
            if (!refused.TryGetValue(name, out var reason)) return true;
            if (apiDefinition != true)
                diagnostics?.RecordSkip(kind, name, reason.Reason, reason.Detail);
            return false;
        }
        bool KeepOwnership(string kind, string name, bool apiOwner, bool consumed, string detail)
        {
            if (!consumed) return true;
            if (apiDefinition == null || apiDefinition == apiOwner)
                diagnostics?.RecordSkip(kind, name, ObjCSkipReason.UnsupportedConstruct, detail);
            return false;
        }
        List<ObjCMethodDecl> Methods(List<ObjCMethodDecl> methods) => methods.Where(m =>
            KeepOwnership("Method", m.Selector, true, m.ConsumesSelf || m.Parameters.Any(p => p.IsConsumed),
                "consumed Objective-C ownership requires a transferred native +1; bgen has no proven consumed-argument or receiver carrier") &&
            Keep("Method", m.Selector, true, m.Parameters.Select(p => p.Type).Prepend(m.ReturnType))).ToList();
        List<ObjCPropertyDecl> Properties(List<ObjCPropertyDecl> properties) => properties.Where(p =>
            KeepOwnership("Property", p.Name, true, p.HasConsumedAccessor || p.GetterOwnership == ObjCReturnOwnership.Retained,
                p.HasConsumedAccessor ? "property accessor consumes Objective-C ownership; no proven bgen carrier"
                    : "retained property getter has no proven bgen property ownership carrier") &&
            Keep("Property", p.Name, true, [p.Type])).ToList();

        return module with
        {
            Structs = module.Structs.Where(s => KeepDeclaration("Struct", s.Name)).ToList(),
            Enums = module.Enums.Where(e => KeepDeclaration("Enum", e.Name)).ToList(),
            Classes = module.Classes.Select(c => c with { Methods = Methods(c.Methods), Properties = Properties(c.Properties) }).ToList(),
            Protocols = module.Protocols.Select(p => p with { Methods = Methods(p.Methods), Properties = Properties(p.Properties) }).ToList(),
            Categories = module.Categories.Select(c => c with { Methods = Methods(c.Methods), Properties = Properties(c.Properties) }).ToList(),
            Functions = module.Functions.Where(f => KeepOwnership("Function", f.Name, false, f.Parameters.Any(p => p.IsConsumed), "consumed Objective-C parameter requires a transferred native +1; no proven C-function carrier") && Keep("Function", f.Name, false, f.Parameters.Select(p => p.Type).Prepend(f.ReturnType))).ToList(),
            Constants = module.Constants.Where(c => Keep("Constant", c.Name, true, [c.Type])).ToList(),
            Typedefs = module.Typedefs.Where(t => Keep("Typedef", t.Name, false, [t.UnderlyingType])).ToList()
        };
    }
}
