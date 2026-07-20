// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BindingsGeneration
{
    /// <summary>
    /// One group of dependency modules to finalize together. A group with a single member is an
    /// ordinary acyclic node finalized in isolation exactly as before; a group with more than one
    /// member (or a self-referential single member) is a strongly-connected component (SCC) — a
    /// module cycle with no valid sequential finalize order — whose members must be skeleton-seeded
    /// before any of them is layout-finalized.
    /// </summary>
    /// <typeparam name="T">The caller's per-dependency work item.</typeparam>
    internal sealed class FinalizationGroup<T>
    {
        /// <summary>The members of this group, in their original input order.</summary>
        public IReadOnlyList<T> Members { get; }

        /// <summary>
        /// True when this group is a genuine module cycle (more than one member, or a single member
        /// that references itself). Only a cycle needs cross-member skeleton seeding; an acyclic
        /// singleton finalizes against already-finalized predecessors, byte-identically to the
        /// historical sequential loop.
        /// </summary>
        public bool IsCycle { get; }

        public FinalizationGroup(IReadOnlyList<T> members, bool isCycle)
        {
            Members = members;
            IsCycle = isCycle;
        }
    }

    /// <summary>
    /// Computes the order in which dependency modules should be layout-finalized so that every
    /// module a given module references is finalized before it — decoupling finalize order from the
    /// order the modules were supplied on the command line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The plan is a <b>stable</b> topological order: among modules with no ordering constraint
    /// between them the original input order is preserved exactly. This is the byte-identity
    /// guarantee — when the supplied order is already a valid finalize order (the well-formed,
    /// in-order case), the plan is the identity permutation and downstream output cannot change.
    /// Reordering happens only where the supplied order violated a real reference edge (reverse
    /// CLI order, umbrella/re-export graphs), which is exactly where the old sequential loop lost
    /// cross-module layout facts.
    /// </para>
    /// <para>
    /// Edges must be the <b>exact</b> reference edges the finalizer consumes (a module's stored
    /// property types, superclass, and inherited protocols). Under-approximating the edge set only
    /// declines to fix a reordering case; it can never reorder an already-valid input, so it cannot
    /// break byte identity. Over-approximating (e.g. compile-import edges) could add a constraint an
    /// in-order input does not satisfy and force a reorder, so it is deliberately not used here.
    /// </para>
    /// </remarks>
    internal static class DependencyFinalizationPlanner
    {
        /// <summary>
        /// Produces the finalize-order groups for <paramref name="items"/>.
        /// </summary>
        /// <param name="items">The dependency work items, in their original (input) order.</param>
        /// <param name="keyOf">Extracts a work item's own module name.</param>
        /// <param name="referencedModulesOf">
        /// Extracts the module names a work item references. Only names that are themselves keys in
        /// <paramref name="items"/> create edges; references to modules outside the set (SDK,
        /// runtime built-ins, absent inputs) impose no finalize-order constraint here.
        /// </param>
        public static IReadOnlyList<FinalizationGroup<T>> Plan<T>(
            IReadOnlyList<T> items,
            Func<T, string> keyOf,
            Func<T, IEnumerable<string>> referencedModulesOf)
        {
            if (items.Count == 0)
                return Array.Empty<FinalizationGroup<T>>();

            if (items.Count == 1)
            {
                // A single item is acyclic unless it references itself (a degenerate self-loop),
                // which the general path below would also mark as a cycle. Kept consistent so the
                // fast path can never disagree with it. (In practice the reference scanner strips a
                // module's own name, so a self-loop never reaches here from the real pipeline.)
                string soleKey = keyOf(items[0]);
                bool soleSelfLoop = referencedModulesOf(items[0])
                    .Any(m => string.Equals(m, soleKey, StringComparison.Ordinal));
                return new[] { new FinalizationGroup<T>(new[] { items[0] }, isCycle: soleSelfLoop) };
            }

            // Index items by their module name. First writer wins on a duplicate name (the
            // sequential loop's own de-dup already skips a second module of the same name); the
            // duplicate keeps its original ordinal and is treated as edge-less.
            int n = items.Count;
            var indexByKey = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < n; i++)
                indexByKey.TryAdd(keyOf(items[i]), i);

            // Adjacency in original order: adj[i] = ordinals of the modules item i references that
            // are themselves in the set. Self-references and duplicates are recorded (a self-loop
            // marks a single-node SCC as a cycle) but never double-counted.
            var adj = new List<int>[n];
            var selfLoop = new bool[n];
            for (int i = 0; i < n; i++)
            {
                var seen = new HashSet<int>();
                var list = new List<int>();
                foreach (var refModule in referencedModulesOf(items[i]))
                {
                    if (refModule == null || !indexByKey.TryGetValue(refModule, out int j))
                        continue;
                    if (j == i)
                    {
                        selfLoop[i] = true;
                        continue;
                    }
                    if (seen.Add(j))
                        list.Add(j);
                }
                adj[i] = list;
            }

            // 1. Strongly-connected components via iterative Tarjan (membership only — the emission
            //    order Tarjan happens to produce is NOT used, because it is DFS-order and would not
            //    preserve the input order for independent nodes). Neighbors are visited in original
            //    order for determinism.
            int[] sccIdOf = TarjanSccIds(n, adj, out int sccCount);

            // Members of each SCC, kept in original input order.
            var members = new List<int>[sccCount];
            for (int c = 0; c < sccCount; c++)
                members[c] = new List<int>();
            for (int i = 0; i < n; i++)
                members[sccIdOf[i]].Add(i);

            // 2. Condensation DAG + stable Kahn. A component's priority is the minimum original
            //    ordinal of its members; the ready set is drained lowest-ordinal first. For an
            //    already-valid input this reproduces the input order exactly (identity permutation).
            var condAdj = new HashSet<int>[sccCount];
            var indegree = new int[sccCount];
            for (int c = 0; c < sccCount; c++)
                condAdj[c] = new HashSet<int>();
            for (int i = 0; i < n; i++)
            {
                int from = sccIdOf[i];
                foreach (int j in adj[i])
                {
                    int to = sccIdOf[j];
                    if (to == from)
                        continue;
                    // Edge i -> j means "i references j", so j must finalize before i: in the
                    // condensation the dependency component (to) points at the dependent (from).
                    if (condAdj[to].Add(from))
                        indegree[from]++;
                }
            }

            var minOrdinal = new int[sccCount];
            for (int c = 0; c < sccCount; c++)
                minOrdinal[c] = members[c].Min();

            // Ready set drained by ascending min ordinal (a small-N linear scan keeps it obviously
            // deterministic; dependency counts here are tiny).
            var order = new List<int>(sccCount);
            var emitted = new bool[sccCount];
            int remaining = sccCount;
            while (remaining > 0)
            {
                int pick = -1;
                for (int c = 0; c < sccCount; c++)
                {
                    if (emitted[c] || indegree[c] != 0)
                        continue;
                    if (pick == -1 || minOrdinal[c] < minOrdinal[pick])
                        pick = c;
                }

                // A cycle in the condensation is impossible (it is a DAG by construction); guard
                // defensively so a logic error degrades to input order instead of an infinite loop.
                if (pick == -1)
                {
                    for (int c = 0; c < sccCount; c++)
                        if (!emitted[c])
                        {
                            pick = c;
                            break;
                        }
                }

                emitted[pick] = true;
                order.Add(pick);
                remaining--;
                foreach (int dependent in condAdj[pick].OrderBy(x => x))
                    indegree[dependent]--;
            }

            var groups = new List<FinalizationGroup<T>>(sccCount);
            foreach (int c in order)
            {
                var memberItems = members[c].Select(i => items[i]).ToArray();
                bool isCycle = memberItems.Length > 1 || (memberItems.Length == 1 && selfLoop[members[c][0]]);
                groups.Add(new FinalizationGroup<T>(memberItems, isCycle));
            }
            return groups;
        }

        /// <summary>
        /// Iterative Tarjan SCC. Returns a per-node component id and the component count. Only
        /// membership is used by the caller; ids are otherwise opaque.
        /// </summary>
        private static int[] TarjanSccIds(int n, List<int>[] adj, out int sccCount)
        {
            var index = new int[n];
            var low = new int[n];
            var onStack = new bool[n];
            var sccId = new int[n];
            for (int i = 0; i < n; i++)
            {
                index[i] = -1;
                sccId[i] = -1;
            }

            var tarjanStack = new Stack<int>();
            int nextIndex = 0;
            int components = 0;

            // Explicit work stack: (node, next-neighbor-cursor).
            var work = new Stack<(int node, int cursor)>();

            for (int start = 0; start < n; start++)
            {
                if (index[start] != -1)
                    continue;

                work.Push((start, 0));
                while (work.Count > 0)
                {
                    var (v, cursor) = work.Pop();
                    if (cursor == 0)
                    {
                        index[v] = low[v] = nextIndex++;
                        tarjanStack.Push(v);
                        onStack[v] = true;
                    }

                    bool recursed = false;
                    var neighbors = adj[v];
                    for (int k = cursor; k < neighbors.Count; k++)
                    {
                        int w = neighbors[k];
                        if (index[w] == -1)
                        {
                            // Resume v at the next neighbor after this DFS child returns.
                            work.Push((v, k + 1));
                            work.Push((w, 0));
                            recursed = true;
                            break;
                        }
                        if (onStack[w] && index[w] < low[v])
                            low[v] = index[w];
                    }
                    if (recursed)
                        continue;

                    // All neighbors processed: settle v. Propagate low to the parent (the frame
                    // directly beneath v on the work stack, if it is v's DFS parent).
                    if (low[v] == index[v])
                    {
                        int w;
                        do
                        {
                            w = tarjanStack.Pop();
                            onStack[w] = false;
                            sccId[w] = components;
                        }
                        while (w != v);
                        components++;
                    }

                    if (work.Count > 0)
                    {
                        var parent = work.Peek();
                        if (low[v] < low[parent.node])
                            low[parent.node] = low[v];
                    }
                }
            }

            sccCount = components;
            return sccId;
        }
    }
}
