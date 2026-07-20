// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Linq;

namespace BindingsGeneration
{
    /// <summary>
    /// The resolution state of a nominal skeleton's canonical owner.
    /// </summary>
    internal enum SkeletonOwnershipState
    {
        /// <summary>The declaring module is present in the run and owns the nominal.</summary>
        Resolved,

        /// <summary>
        /// The nominal is referenced/re-exported but its canonical owning module is not among the
        /// supplied inputs (nor a recognized SDK/runtime module). Recorded as a structured
        /// missing-input observation rather than a silent drop; session 07 migrates it to the ledger.
        /// </summary>
        UnresolvedOwner,
    }

    /// <summary>
    /// The pre-layout identity of one nominal: enough to reason about ownership, kind, and cross-
    /// module reference BEFORE — or independently of — its layout being finalized. A skeleton is
    /// explicitly NOT a partially-populated <see cref="TypeRecord"/>: it carries no effective
    /// frozen/memory-management verdict, no ABI field layout, and no inline size, so no layout or
    /// frozenness decision can ever read a defaulted value off it.
    /// </summary>
    /// <remarks>
    /// <see cref="IsDeclaredFrozen"/> is the raw <c>@frozen</c> attribute from the declaration — a
    /// parser input, never the effective frozenness (which only <c>ModuleProcessor.CacluateFlags</c>
    /// computes from the finalized property records). It is retained so a later SCC fixed-point can
    /// seed from the declared attribute without inventing one.
    /// </remarks>
    internal sealed class NominalSkeleton
    {
        public SwiftTypeName Identity { get; }
        public TypeRecordKind Kind { get; }

        /// <summary>The canonical owning module (the module whose ABI declares the nominal).</summary>
        public string OwningModule { get; }

        /// <summary>The nominal's mangled name, when the declaration carried one; else null.</summary>
        public string? MangledName { get; }

        /// <summary>The raw <c>@frozen</c> declaration attribute (a parser input, not a verdict).</summary>
        public bool IsDeclaredFrozen { get; }

        public SkeletonOwnershipState OwnershipState { get; }

        public NominalSkeleton(
            SwiftTypeName identity,
            TypeRecordKind kind,
            string owningModule,
            string? mangledName,
            bool isDeclaredFrozen,
            SkeletonOwnershipState ownershipState)
        {
            Identity = identity;
            Kind = kind;
            OwningModule = owningModule;
            MangledName = mangledName;
            IsDeclaredFrozen = isDeclaredFrozen;
            OwnershipState = ownershipState;
        }
    }

    /// <summary>
    /// A graph-wide, pre-layout identity plane for nominals, populated as each module is parsed and
    /// BEFORE the layout/hierarchy finalizer runs. It sits beside the Stage-2 symbol index and is
    /// deliberately kept OUT of the type-database lookup path: it never answers
    /// <c>TryGetTypeRecord</c> / <c>IsTypeRegistered</c>, and it never authorizes a
    /// <c>{mangled}Ma</c> metadata-accessor synthesis. Its readers are (a) the SCC-aware finalize
    /// orchestration, which seeds a module cycle's members before finalizing any of them, and
    /// (b) session 07's quarantine activation, which enumerates registered skeletons and their
    /// owner-resolution state.
    /// </summary>
    internal sealed class NominalSkeletonIndex
    {
        private readonly Dictionary<string, NominalSkeleton> _byIdentity =
            new(System.StringComparer.Ordinal);

        /// <summary>True until the first skeleton is registered. A gate for the acyclic fast path.</summary>
        public bool IsEmpty => _byIdentity.Count == 0;

        public int Count => _byIdentity.Count;

        /// <summary>
        /// Registers a skeleton. First writer wins on a duplicate identity — the canonical owning
        /// module's own declaration is registered first (during that module's parse), so a later
        /// re-export observation of the same nominal never displaces it.
        /// </summary>
        public void Register(NominalSkeleton skeleton)
        {
            _byIdentity.TryAdd(skeleton.Identity.ModuleQualifiedName, skeleton);
        }

        public bool TryGet(SwiftTypeName identity, out NominalSkeleton skeleton)
            => _byIdentity.TryGetValue(identity.ModuleQualifiedName, out skeleton!);

        /// <summary>
        /// The existence question — "is this nominal known to the graph at all" — kept separate from
        /// the type database's "is it a finalized, layout-bearing record" question so a caller can
        /// never accidentally read layout off a skeleton.
        /// </summary>
        public bool IsNominalKnown(SwiftTypeName identity)
            => _byIdentity.ContainsKey(identity.ModuleQualifiedName);

        /// <summary>All registered skeletons whose owner could not be resolved to a supplied input.</summary>
        public IReadOnlyList<NominalSkeleton> UnresolvedOwners()
            => _byIdentity.Values.Where(s => s.OwnershipState == SkeletonOwnershipState.UnresolvedOwner).ToList();
    }
}
