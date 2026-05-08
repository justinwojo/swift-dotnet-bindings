// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Layer-neutral helpers for combining <see cref="AvailabilityAnnotation"/> lists across a
/// member and its enclosing type chain. Lives under <c>Model/</c> so parser and type-database
/// construction code (e.g. <c>ModuleProcessor</c> populating
/// <c>TypeRecord.AvailabilityAnnotations</c>) can use the same merge semantics as the emitter
/// without depending on string-emitter infrastructure.
/// </summary>
public static class AvailabilityHelpers
{
    /// <summary>
    /// Merges member-level and parent-type availability annotations for a top-level emission
    /// target (e.g. <c>@_cdecl</c> wrapper) that does not implicitly inherit the enclosing
    /// type's availability. Walks the full parent chain so nested types like
    /// <c>StoreKit.Product.SubscriptionInfo.RenewalInfo.AdvancedCommerceInfo.Item</c> pick up
    /// availability declared on any ancestor. Repeated platform+version pairs are deduped at
    /// the @available emission step.
    /// </summary>
    public static IReadOnlyList<AvailabilityAnnotation>? MergeAvailability(
        IReadOnlyList<AvailabilityAnnotation>? memberAnnotations,
        BaseDecl? parentDecl)
    {
        return MergeAvailabilityFromAncestors(memberAnnotations, parentDecl);
    }

    /// <summary>
    /// Walks the parent chain of <paramref name="startDecl"/> and merges every TypeDecl's
    /// availability annotations with <paramref name="memberAnnotations"/>. Returns null when
    /// neither the member nor any ancestor declares availability.
    /// </summary>
    public static IReadOnlyList<AvailabilityAnnotation>? MergeAvailabilityFromAncestors(
        IReadOnlyList<AvailabilityAnnotation>? memberAnnotations,
        BaseDecl? startDecl)
    {
        List<AvailabilityAnnotation>? merged = null;

        BaseDecl? current = startDecl;
        while (current is TypeDecl td)
        {
            if (td.AvailabilityAnnotations is { Count: > 0 } parentAnnotations)
            {
                merged ??= new List<AvailabilityAnnotation>();
                merged.AddRange(parentAnnotations);
            }
            current = td.ParentDecl;
        }

        if (memberAnnotations is { Count: > 0 })
        {
            merged ??= new List<AvailabilityAnnotation>();
            merged.AddRange(memberAnnotations);
        }

        return merged;
    }
}
