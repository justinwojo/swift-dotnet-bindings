// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    /// <summary>
    /// Represents the kind of operator.
    /// </summary>
    public enum OperatorKind
    {
        /// <summary>
        /// Unary operator (single operand).
        /// </summary>
        Unary,

        /// <summary>
        /// Binary operator (two operands).
        /// </summary>
        Binary
    }

    /// <summary>
    /// Represents an operator declaration.
    /// Wraps a MethodDecl and adds operator-specific metadata.
    /// </summary>
    public sealed record OperatorDecl : BaseDecl
    {
        /// <summary>
        /// The Swift operator symbol (e.g., "+", "==", "!").
        /// </summary>
        public required string OperatorSymbol { get; set; }

        /// <summary>
        /// The kind of operator (unary or binary).
        /// </summary>
        public required OperatorKind Kind { get; set; }

        /// <summary>
        /// For unary operators, indicates whether it is a prefix operator.
        /// True for prefix (e.g., -x, !x), false for postfix.
        /// </summary>
        public required bool IsPrefix { get; set; }

        /// <summary>
        /// The underlying method declaration for the operator.
        /// Contains mangled name, signature, and marshalling information.
        /// </summary>
        public required MethodDecl UnderlyingMethod { get; set; }
    }
}
