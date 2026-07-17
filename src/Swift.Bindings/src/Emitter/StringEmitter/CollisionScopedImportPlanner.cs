// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    /// <summary>
    /// Plans the scoped <c>import</c> lines a wrapper needs when the bound module's own name is
    /// shadowed by a type of the same name.
    ///
    /// A shadowed module cannot be reached through a qualifier at all: Swift resolves the leading
    /// identifier of <c>Name.X</c> as a type whenever one is in scope and never falls back to
    /// module lookup, so every <c>Name.</c> the emitter writes is read as member lookup into the
    /// shadowing type and fails. The wrapper's answer is to strip those qualifiers and name the
    /// module's types bare — which re-opens the ambiguity the qualifiers existed to close, because
    /// a bare name resolves against every wildcard <c>import</c> at once. A module that both calls
    /// itself <c>SwiftMessages</c> and declares an <c>AnimationContext</c> hits both halves: it
    /// cannot say <c>SwiftMessages.AnimationContext</c>, and bare <c>AnimationContext</c> is
    /// ambiguous against SwiftUI's.
    ///
    /// A scoped import (<c>import class M.AnimationContext</c>) binds one name to one declaration
    /// and outranks the wildcard imports, so the bare name the strip leaves behind resolves
    /// unambiguously to the bound module's type — no qualifier required. It composes with the
    /// nested-type carve-out: <c>M.Nested</c> still reaches the shadowing type's members.
    ///
    /// Scoping every top-level type this way states the wrapper's actual position — where an
    /// imported module offers the same name as the module being bound, the bound module wins —
    /// which is precisely what qualification already asserts for every unshadowed module.
    /// </summary>
    internal static class CollisionScopedImportPlanner
    {
        /// <summary>
        /// Returns the <c>import &lt;kind&gt; &lt;module&gt;.&lt;Type&gt;</c> lines to emit for
        /// <paramref name="moduleDecl"/>'s top-level public types, sorted for stable output.
        ///
        /// Callers must only invoke this for a module whose name is actually shadowed; for every
        /// other module the plain qualifier works and these lines are noise.
        /// </summary>
        /// <param name="moduleDecl">The bound module.</param>
        /// <param name="compileImport">
        /// The module name to import through — the same remapped spelling the wildcard
        /// <c>import</c> uses, so a module reached via an umbrella stays consistent.
        /// </param>
        internal static IReadOnlyList<string> Plan(ModuleDecl moduleDecl, string compileImport)
        {
            if (string.IsNullOrEmpty(compileImport))
                return Array.Empty<string>();

            var lines = new SortedSet<string>(StringComparer.Ordinal);

            foreach (var decl in moduleDecl.Types.Concat<TypeDecl>(moduleDecl.Protocols))
            {
                if (!ShouldScope(decl, moduleDecl.Name))
                    continue;
                if (TryGetImportKeyword(decl, out var keyword))
                    lines.Add($"import {keyword} {compileImport}.{decl.Name}");
            }

            return lines.ToList();
        }

        /// <summary>
        /// Filters out the types a scoped import would either break on or do nothing for.
        /// </summary>
        private static bool ShouldScope(TypeDecl decl, string moduleName)
        {
            // Not part of the wrapper's visible surface — importing it by name wouldn't compile.
            if (decl.IsModuleInternal)
                return false;

            // The shadowing type itself. Scoping it would re-bind the very name the strip relies on
            // resolving to that type, and the nested-type carve-out already reaches its members.
            if (decl.Name == moduleName)
                return false;

            // Underscore-prefixed types are suppressed from the binding surface, and a nested or
            // otherwise non-identifier name isn't importable in scoped form.
            if (!IsPlainIdentifier(decl.Name) || decl.Name[0] == '_')
                return false;

            return true;
        }

        /// <summary>
        /// Maps a declaration to the keyword a scoped import must spell it with.
        ///
        /// The keyword has to match the declaration's kind exactly — swiftc rejects a mismatch and
        /// takes the whole wrapper with it — so an unrecognized declaration is skipped rather than
        /// guessed at. Skipping is safe by construction: the type simply keeps the bare,
        /// possibly-ambiguous reference it has today, which is the pre-existing behavior.
        ///
        /// Actors map to <c>class</c>: they parse as <see cref="ClassDecl"/>, and Swift has no
        /// <c>import actor</c> form — <c>import class M.SomeActor</c> is the accepted spelling.
        /// </summary>
        private static bool TryGetImportKeyword(TypeDecl decl, out string keyword)
        {
            keyword = decl switch
            {
                ClassDecl => "class",
                StructDecl => "struct",
                EnumDecl => "enum",
                ProtocolDecl => "protocol",
                _ => "",
            };
            return keyword.Length > 0;
        }

        private static bool IsPlainIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            if (!char.IsLetter(name[0]) && name[0] != '_')
                return false;
            foreach (var c in name)
            {
                if (!char.IsLetterOrDigit(c) && c != '_')
                    return false;
            }
            return true;
        }
    }
}
