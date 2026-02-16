// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    /// <summary>
    /// Topological sort utility for determining build ordering of multi-framework dependencies.
    /// Uses Kahn's algorithm with lexical tie-breaking for deterministic output.
    /// </summary>
    public static class TopologicalSort
    {
        /// <summary>
        /// Returns nodes in dependency-first order (dependencies before dependents).
        /// Ties broken lexically (ordinal) for deterministic output across runs/platforms.
        /// </summary>
        /// <param name="graph">Adjacency list where each key depends on its values.
        /// E.g., { "A" → ["B", "C"] } means A depends on B and C.</param>
        /// <returns>List of nodes in topological order (dependencies first).</returns>
        /// <exception cref="InvalidOperationException">Thrown if a cycle is detected.</exception>
        public static List<string> Sort(Dictionary<string, List<string>> graph)
        {
            if (graph.Count == 0)
                return new List<string>();

            // Collect all nodes (both keys and values)
            var allNodes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (node, deps) in graph)
            {
                allNodes.Add(node);
                foreach (var dep in deps)
                    allNodes.Add(dep);
            }

            // Compute in-degree for each node
            // In-degree = number of nodes that depend on this node
            // We want dependency-first order, so edges go from dependent → dependency
            // Reverse: dependency ← dependent, so in-degree counts how many things depend on it
            // Actually: graph[A] = [B, C] means A depends on B and C
            // For dependency-first: B and C should come before A
            // So the edge direction for topological sort is: A → B, A → C (A has edges to its deps)
            // In-degree of A = number of nodes that list A as a dependency
            var inDegree = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var node in allNodes)
                inDegree[node] = 0;

            foreach (var (node, deps) in graph)
            {
                // node depends on deps, so node should come after deps
                // For Kahn's: edge from dep → node (dep must be processed first)
                // In-degree of node increases for each dependency
                inDegree[node] = deps.Count;
            }

            // Use SortedSet for lexical tie-breaking
            var available = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var (node, degree) in inDegree)
            {
                if (degree == 0)
                    available.Add(node);
            }

            var result = new List<string>(allNodes.Count);

            // Build reverse adjacency: for each dep, which nodes depend on it
            var dependents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var node in allNodes)
                dependents[node] = new List<string>();

            foreach (var (node, deps) in graph)
            {
                foreach (var dep in deps)
                {
                    if (dependents.ContainsKey(dep))
                        dependents[dep].Add(node);
                }
            }

            while (available.Count > 0)
            {
                var current = available.Min!;
                available.Remove(current);
                result.Add(current);

                // For each node that depends on current, decrement its in-degree
                foreach (var dependent in dependents[current])
                {
                    inDegree[dependent]--;
                    if (inDegree[dependent] == 0)
                        available.Add(dependent);
                }
            }

            if (result.Count != allNodes.Count)
            {
                throw new InvalidOperationException(
                    "Dependency cycle detected in framework graph. " +
                    "Cyclic dependencies cannot be topologically sorted.");
            }

            return result;
        }
    }
}
