// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Context for member validation, carrying all state needed to evaluate emission and wrapper gates.
/// Created once per type iteration in HandleBaseDecl and passed to the pipeline.
/// </summary>
public sealed class ValidationContext
{
    public ITypeDatabase TypeDatabase { get; }
    public PInvokeHelperContext? PInvokeHelperContext { get; }
    public ModuleEmissionContext EmissionContext { get; }
    public TypeDecl? ParentType { get; }
    public ModuleDecl? ModuleDecl { get; }
    public IReadOnlySet<string>? SiblingPropertyNames { get; }
    public Conductor? Conductor { get; }

    public ValidationContext(
        ITypeDatabase typeDatabase,
        PInvokeHelperContext? pInvokeHelperContext,
        ModuleEmissionContext emissionContext,
        TypeDecl? parentType,
        ModuleDecl? moduleDecl,
        IReadOnlySet<string>? siblingPropertyNames,
        Conductor? conductor)
    {
        TypeDatabase = typeDatabase;
        PInvokeHelperContext = pInvokeHelperContext;
        EmissionContext = emissionContext;
        ParentType = parentType;
        ModuleDecl = moduleDecl;
        SiblingPropertyNames = siblingPropertyNames;
        Conductor = conductor;
    }
}

/// <summary>
/// Result of validating a member for emission via <see cref="MemberValidationPipeline.ValidateMethodEmission"/>.
/// </summary>
public sealed class ValidationResult
{
    /// <summary>Whether the member should be emitted.</summary>
    public bool ShouldEmit { get; }

    /// <summary>Skip reason if not emitting; null if emitting.</summary>
    public SkipReason? Reason { get; }

    /// <summary>Human-readable details about why the member was skipped.</summary>
    public string? Details { get; }

    /// <summary>
    /// True if the member was skipped because it is a synthesized protocol member
    /// (e.g., hash(into:) for Hashable). Callers should use
    /// <see cref="ReportCollector.RecordMemberSynthesized"/> instead of RecordMemberSkipped.
    /// </summary>
    public bool IsSynthesized { get; }

    private ValidationResult(bool shouldEmit, SkipReason? reason, string? details, bool isSynthesized)
    {
        ShouldEmit = shouldEmit;
        Reason = reason;
        Details = details;
        IsSynthesized = isSynthesized;
    }

    public static readonly ValidationResult Emit = new(true, null, null, false);

    public static ValidationResult Skip(SkipReason reason, string details) =>
        new(false, reason, details, false);

    public static ValidationResult Synthesized(string details) =>
        new(false, null, details, true);
}

/// <summary>
/// Result of validating a member for wrapper eligibility via
/// <see cref="MemberValidationPipeline.ValidateWrapperEligibility"/>.
/// </summary>
public sealed class WrapperValidationResult
{
    /// <summary>Whether the member should have a @_cdecl wrapper generated.</summary>
    public bool ShouldEmitWrapper { get; }

    /// <summary>Skip reason name for diagnostics (null if wrapping).</summary>
    public string? RejectionReason { get; }

    private WrapperValidationResult(bool shouldEmitWrapper, string? rejectionReason)
    {
        ShouldEmitWrapper = shouldEmitWrapper;
        RejectionReason = rejectionReason;
    }

    public static readonly WrapperValidationResult Wrap = new(true, null);

    public static WrapperValidationResult Reject(string reason) => new(false, reason);
}
