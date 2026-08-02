// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;

namespace BindingsGeneration;

/// <summary>
/// Post-emission step that stamps each type's property renames onto its <see cref="TypeRecord"/>
/// so they survive the module-database XML round-trip.
///
/// <para>Runs after emission rather than off the pre-pass decisions on purpose: only a property
/// that was actually written to C# output should be advertised, and only the emission-time stamp
/// has seen every property-side scheme (the enclosing-type <c>Value</c> rule, the nested-type
/// rename channel, the enum-case channel, and case-only disambiguation). Re-deriving the name
/// here would mean recomputing all four from a sibling set this step no longer has — which is
/// exactly the drift the ledger exists to prevent.</para>
/// </summary>
public static class PropertyRenameLedger
{
    /// <summary>
    /// Walks every type in the module (including protocols and nested types) and records the
    /// property renames on its type record. A visited type always gets a list, empty included —
    /// see <see cref="TypeRecord.RenamedMembers"/> for why empty and null must stay distinct.
    /// </summary>
    public static void Populate(ModuleDecl moduleDecl, ITypeDatabase typeDatabase)
    {
        foreach (var typeDecl in moduleDecl.Types)
            PopulateRecursive(typeDecl, typeDatabase);
        foreach (var protocolDecl in moduleDecl.Protocols)
            PopulateRecursive(protocolDecl, typeDatabase);
    }

    private static void PopulateRecursive(TypeDecl typeDecl, ITypeDatabase typeDatabase)
    {
        if (typeDatabase.TryGetTypeRecord(typeDecl.SwiftTypeName, out _))
        {
            typeDatabase.ApplyEmissionResult(typeDecl.SwiftTypeName, new TypeEmissionResult
            {
                RenamedMembers = CollectRenames(typeDecl),
            });
        }

        foreach (var nestedType in typeDecl.Types)
            PopulateRecursive(nestedType, typeDatabase);
    }

    private static IReadOnlyList<RenamedMember> CollectRenames(TypeDecl typeDecl)
    {
        // Evidence that a property really reached the output differs by path, so each path
        // contributes the signal it actually has. A concrete type's property handler settles the
        // C# name EARLY and can still bail out of several accessor preflights afterwards, so there
        // the flag is the honest signal. A protocol requirement never sets that flag — it is a concrete-type
        // ancestry signal, and requiring it here wrote an empty list for every protocol, which
        // this ledger's own contract reads as "processed, renamed nothing" — but its interface
        // path stamps the name with no bail-out left between the stamp and the write, so there
        // the stamp is the signal.
        var requiresEmissionFlag = typeDecl is not ProtocolDecl;

        var renames = new List<RenamedMember>();
        foreach (var property in typeDecl.Properties)
        {
            if (requiresEmissionFlag && !property.WasEmitted)
                continue;
            if (property.EmittedCSharpName is not { } emittedName)
                continue;

            // The natural projection is what a reader would predict from the Swift name alone;
            // anything else is a scheme having fired.
            if (emittedName == NameProvider.GetPropertyBaseName(property.Name))
                continue;

            // Two buckets exhaustively partition the property side: either the case-only pass
            // chose the base name, or one of the `Value` channels did (the enclosing-type rule,
            // the nested-type rename channel, the enum-case channel — all three append `Value`,
            // which IS PropertyValueSuffix). A property that passed through both records the
            // channel that settled the FINAL name.
            var scheme = property.CaseDisambiguatedName == emittedName
                ? NameCollisionScheme.CaseOnlyMemberCollision
                : NameCollisionScheme.PropertyValueSuffix;

            renames.Add(new RenamedMember(
                RenamedMemberKind.Property,
                property.Name,
                property.IsStatic,
                emittedName,
                scheme.ToString()));
        }
        return renames;
    }
}
