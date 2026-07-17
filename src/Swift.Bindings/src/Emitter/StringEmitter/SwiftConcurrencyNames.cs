// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Module-qualified spellings of the concurrency types the generator writes into its own
/// wrapper infrastructure (task registry, cancellation, async closure thunks).
///
/// These names are emitted as literals rather than resolved from a bound type, so nothing
/// stops a bound module from declaring its own <c>Task</c> — and several do (a networking
/// module modelling a request body as <c>enum Task</c> is a common shape). The wrapper
/// imports that module, so a bare <c>Task</c> in emitted infrastructure becomes ambiguous
/// and swiftc rejects the whole wrapper, taking every binding in the module with it.
///
/// <c>_Concurrency</c> is re-exported by the Swift stdlib, which is implicitly imported into
/// every file, so the prefix needs no import of its own. That does not make it collision-proof:
/// the same "a type in scope shadows the module of the same name" rule this whole qualification
/// scheme exists to work around would apply just as well to a bound module that declares a
/// public type literally named <c>_Concurrency</c>. Unlike the bound-module case, there is no
/// fix available here — Swift has no source-level module-alias syntax (no `import X as Y`), so
/// there is no way to write a qualifier that survives a same-named type shadowing the real
/// module. Accepted as a residual: a public API named `_Concurrency` is exceptionally unlikely
/// (a leading-underscore name colliding with a real stdlib module), and such a library would
/// already be fighting the same ambiguity in its own consumers, independent of this generator.
/// </summary>
internal static class SwiftConcurrencyNames
{
    /// <summary>Qualified <c>Task</c> — use for both the type and its statics (<c>Task.isCancelled</c>).</summary>
    internal const string Task = "_Concurrency.Task";

    /// <summary>Qualified <c>CheckedContinuation</c>.</summary>
    internal const string CheckedContinuation = "_Concurrency.CheckedContinuation";
}
