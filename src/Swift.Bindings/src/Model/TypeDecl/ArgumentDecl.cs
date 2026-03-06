// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    /// <summary>
    /// Represents an argument declaration.
    /// </summary>
    public record ArgumentDecl : BaseDecl
    {
        /// <summary>
        /// Type of the argument
        /// <summary>
        public required TypeSpec SwiftTypeSpec { get; set; }

        /// <summary>
        /// The private name of the argument.
        /// </summary>
        public required string PrivateName { get; set; }

        /// <summary>
        /// Indicates the inout annotation of the argument.
        /// </summary>
        public required bool IsInOut { get; set; }

        /// <summary>
        /// Indicates if the argument is generic.
        /// </summary>
        public required bool IsGeneric { get; set; }

        /// <summary>
        /// Indicates if the argument has a default value in Swift.
        /// When true, the parameter could potentially be omitted by callers
        /// if overload generation is implemented.
        /// </summary>
        public bool HasDefaultArg { get; set; } = false;

        /// <summary>
        /// The raw Swift default expression from .swiftinterface (e.g., "10", "true", ".mid", "nil").
        /// Only populated when HasDefaultArg is true AND the .swiftinterface provided the value.
        /// </summary>
        public string? SwiftDefaultExpression { get; set; }

        /// <summary>
        /// The deduplicated C# parameter name, set by NameProvider.DeduplicateParameterNames().
        /// When set, NameProvider.GetCSharpParameterName() returns this value.
        /// </summary>
        public string? CSharpName { get; set; }
    }
}
