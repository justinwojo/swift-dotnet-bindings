// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
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
        /// </summary>
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
        /// The Swift value-ownership convention for this parameter (consuming / borrowing /
        /// inout / default), parsed from the ABI JSON <c>paramValueOwnership</c> field.
        /// Authoritative for parser-produced declarations; synthetic argument declarations
        /// (setter newValue, subscript indices, bridge parameters) leave this at
        /// <see cref="ParameterOwnership.Default"/> and rely on <see cref="IsInOut"/> instead.
        /// Distinguishes <see cref="ParameterOwnership.Owned"/> (consuming, +1 transfer) from
        /// <see cref="ParameterOwnership.Shared"/> (borrowing, +0) — a distinction <see cref="IsInOut"/>
        /// cannot express — so ownership-transfer paths avoid double-free / +0-forwarding bugs.
        /// </summary>
        public ParameterOwnership Ownership { get; set; } = ParameterOwnership.Default;

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

        /// <summary>
        /// The base identifier the emitters build this parameter's generated scratch locals from
        /// (<c>{base}Buffer</c>, <c>{base}Swift</c>, <c>{base}Handle</c>, the ObjC container owners,
        /// and the suffixed P/Invoke parameter names the call-argument path reconstructs).
        /// Set alongside <see cref="CSharpName"/> by NameProvider.DeduplicateParameterNames().
        /// <para>
        /// Equal to <see cref="CSharpName"/> except when a sibling parameter is itself named exactly
        /// what one of those derived locals would be spelled — then it is escaped, and the wrapper
        /// body opens with a <c>ref</c> alias binding the escaped base to the real parameter, so
        /// reads and writebacks stay identical while the derived names move out of the way.
        /// </para>
        /// </summary>
        public string? MarshallingBaseName { get; set; }

        /// <summary>
        /// Indicates the parameter is declared with Swift's <c>_const</c> modifier, requiring
        /// the caller to pass a compile-time-constant literal (e.g.
        /// <c>init(min: _const Swift.Int, max: _const Swift.Int)</c>). ABI JSON strips this;
        /// the swiftinterface is the only source. Set via
        /// <c>SwiftABIParser.ApplyMemberConstLiteralFlags</c> from
        /// <see cref="SwiftInterfaceFacts.ConstLiteralParameters"/>. Wrapper emitters reject
        /// any member with a <c>_const</c> parameter because the @_cdecl boundary passes a
        /// runtime value and Swift rejects the call.
        /// </summary>
        public bool IsConstLiteral { get; set; } = false;

        /// <summary>
        /// Subscript-only: true when the Swift source had no external argument label at this
        /// position (i.e. the declaration used <c>subscript(name: T)</c> or <c>subscript(_ name: T)</c>).
        /// Set by the ABI parser at the synthetic <c>index{i}</c> injection points. Emitters use
        /// this flag — not a pattern match on <see cref="BaseDecl.Name"/> — to decide whether to
        /// emit <c>_</c> vs the real label, since a real external label could literally be
        /// <c>index0</c>/<c>index1</c>/... and would otherwise collide with the synthetic sentinel.
        /// Always false for non-subscript parameters.
        /// </summary>
        public bool IsUnlabeledSubscriptIndex { get; set; } = false;
    }
}
