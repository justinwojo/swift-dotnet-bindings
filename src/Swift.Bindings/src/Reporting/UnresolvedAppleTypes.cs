// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Names the Apple-framework types a member could not be bound against.
/// </summary>
/// <remarks>
/// <para>
/// Most "unsupported type" skips resolve to one root cause: a type from a framework the binding
/// depends on is not in the type database, so the resolver hands back <c>Swift.AnyType</c> and the
/// member is dropped. The skip detail that reaches the report says the shape was unprojectable but
/// not <em>which</em> type made it so — which is the one fact needed to decide whether the gap is
/// worth closing, since one missing type usually accounts for many skipped members.
/// </para>
/// <para>
/// Scoped deliberately to dependency-module Apple types: a type from the module being bound is the
/// generator's own business, and a non-Apple third-party module is the consumer's to supply. Only a
/// framework in <see cref="AppleFrameworkRegistry"/> is something this project could register.
/// </para>
/// </remarks>
public static class UnresolvedAppleTypes
{
    /// <summary>
    /// The distinct module-qualified names of Apple-framework types named by <paramref name="specs"/>
    /// that the type database cannot resolve, excluding types declared by
    /// <paramref name="bindingModuleName"/> itself. Ordinal-ordered so the same member always yields
    /// the same detail string. Empty when nothing qualifies.
    /// </summary>
    public static IReadOnlyList<string> Find(
        IEnumerable<TypeSpec?> specs, ITypeDatabase typeDatabase, string? bindingModuleName)
    {
        ArgumentNullException.ThrowIfNull(specs);
        ArgumentNullException.ThrowIfNull(typeDatabase);

        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spec in specs)
            TypeSpecHelpers.CollectNominalTypeNames(spec, named);

        if (named.Count == 0)
            return Array.Empty<string>();

        var unresolved = new List<string>();
        foreach (var moduleQualifiedName in named)
        {
            var separator = moduleQualifiedName.IndexOf('.');
            if (separator <= 0)
                continue;

            var module = moduleQualifiedName[..separator];
            if (string.Equals(module, bindingModuleName, StringComparison.Ordinal))
                continue;
            if (!AppleFrameworkRegistry.IsKnownModule(module))
                continue;
            if (typeDatabase.IsTypeProcessed(new NamedTypeSpec(moduleQualifiedName)))
                continue;

            unresolved.Add(moduleQualifiedName);
        }

        unresolved.Sort(StringComparer.Ordinal);
        return unresolved;
    }

    /// <summary>
    /// A sentence naming the unresolved Apple types for appending to a skip detail, or an empty
    /// string when there are none. Report-only: never routed into a generated-source comment, so the
    /// emitted C# is unaffected by what this returns.
    /// </summary>
    public static string DescribeSuffix(
        IEnumerable<TypeSpec?> specs, ITypeDatabase typeDatabase, string? bindingModuleName)
    {
        var unresolved = Find(specs, typeDatabase, bindingModuleName);
        return unresolved.Count == 0
            ? string.Empty
            : $" Unprojected Apple {(unresolved.Count == 1 ? "type" : "types")}: {string.Join(", ", unresolved)}.";
    }

    /// <summary>
    /// <see cref="DescribeSuffix(IEnumerable{TypeSpec?}, ITypeDatabase, string?)"/> over a method's
    /// full signature — the return slot and every parameter.
    /// </summary>
    public static string DescribeSuffix(MethodDecl methodDecl, ITypeDatabase typeDatabase, string? bindingModuleName)
    {
        ArgumentNullException.ThrowIfNull(methodDecl);
        return DescribeSuffix(
            methodDecl.CSSignature.Select(a => (TypeSpec?)a.SwiftTypeSpec), typeDatabase, bindingModuleName);
    }
}
