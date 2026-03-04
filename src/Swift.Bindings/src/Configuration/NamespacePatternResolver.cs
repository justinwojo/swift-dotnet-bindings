// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    /// <summary>
    /// Resolves configured namespace patterns for generated types and modules.
    /// </summary>
    public sealed class NamespacePatternResolver
    {
        public const string DefaultPattern = "{Module}";

        private readonly string _pattern;
        private readonly string? _frameworkName;

        public NamespacePatternResolver(string? pattern = null, string? frameworkName = null)
        {
            _pattern = string.IsNullOrWhiteSpace(pattern) ? DefaultPattern : pattern;
            _frameworkName = frameworkName;
        }

        /// <summary>
        /// Resolves a C# namespace for a Swift module.
        /// </summary>
        /// <param name="moduleName">Swift module name.</param>
        public string ResolveNamespace(string moduleName)
        {
            if (string.IsNullOrWhiteSpace(moduleName))
            {
                throw new ArgumentException("Module name must not be empty.", nameof(moduleName));
            }

            string frameworkValue = string.IsNullOrWhiteSpace(_frameworkName) ? moduleName : _frameworkName;
            return _pattern
                .Replace("{Module}", moduleName, StringComparison.Ordinal)
                .Replace("{Framework}", frameworkValue, StringComparison.Ordinal);
        }
    }
}
