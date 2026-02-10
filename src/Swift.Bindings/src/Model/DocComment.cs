// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    /// <summary>
    /// Structured representation of a Swift doc comment, parsed from symbol graph JSON.
    /// </summary>
    public sealed record DocComment
    {
        /// <summary>
        /// The summary text (lines before the first blank line or directive).
        /// </summary>
        public string Summary { get; init; } = string.Empty;

        /// <summary>
        /// Parameter descriptions keyed by Swift public label.
        /// </summary>
        public Dictionary<string, string> Parameters { get; init; } = new();

        /// <summary>
        /// The "Returns:" description, if present.
        /// </summary>
        public string? Returns { get; init; }

        /// <summary>
        /// The "Throws:" description, if present.
        /// </summary>
        public string? Throws { get; init; }

        /// <summary>
        /// Additional remarks (Note, Important, Warning, Remark, Precondition, Postcondition, Complexity).
        /// </summary>
        public List<string> Remarks { get; init; } = new();

        /// <summary>
        /// Returns true if this doc comment has no meaningful content.
        /// </summary>
        public bool IsEmpty => string.IsNullOrWhiteSpace(Summary)
            && Parameters.Count == 0
            && Returns == null
            && Throws == null
            && Remarks.Count == 0;
    }
}
