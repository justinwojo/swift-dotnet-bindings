// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;

namespace Swift.Runtime;

/// <summary>
/// The runtime's single check for "this value has no copy", and the single wording it reports when
/// a marshalling lane needs one.
///
/// <para>Swift fills a <c>~Copyable</c> type's <c>initializeWithCopy</c> value witness with
/// <c>__swift_cannot_copy_noncopyable_type</c>, which aborts the process. That is the right
/// behaviour for Swift code, where the type checker has already made the call impossible; it is the
/// wrong behaviour here, where the value's copyability is a runtime property of a generic argument
/// nothing in the C# type system can constrain. <c>SwiftOptional&lt;T&gt;</c> takes any <c>T</c>,
/// and a generated generic closure bridge constrains its <c>T</c> to <see cref="ISwiftObject"/> —
/// a constraint that cannot express Swift's implicit <c>T: Copyable</c>. So a consumer can reach
/// these lanes with a non-copyable type, and the only signal available at that point is the value
/// witness flag.</para>
///
/// <para>Checking the flag first turns an unattributable <c>SIGTRAP</c> inside the Swift runtime
/// into a <see cref="SwiftRuntimeException"/> that names the type and the lane. It does not make
/// the operation work — nothing can, since the lane genuinely needs two owners of a value Swift
/// permits one of — but it fails at the call the consumer wrote, in a form a <c>catch</c> can see
/// and a crash report can attribute.</para>
/// </summary>
internal static class NonCopyableValueGuard
{
    /// <summary>
    /// Whether the type described by <paramref name="metadata"/> is <c>~Copyable</c>. False for
    /// invalid metadata: an unresolvable type is not a copyability verdict, and the caller's
    /// existing handling for that case stays in charge.
    /// </summary>
    internal static unsafe bool IsNonCopyable(TypeMetadata metadata)
        => metadata.IsValid && metadata.ValueWitnessTable->IsNonCopyable;

    /// <summary>
    /// The exception a lane throws when it holds a borrowed non-copyable value and its contract
    /// requires producing an independent second one.
    /// </summary>
    /// <param name="valueTypeName">The type as the consumer spelled it.</param>
    /// <param name="lane">What the lane was about to do, in the consumer's terms.</param>
    internal static SwiftRuntimeException CannotDuplicate(string valueTypeName, string lane)
        => new SwiftRuntimeException(
            $"'{valueTypeName}' is a Swift ~Copyable type, so it has no copy value witness: {lane} " +
            "would need a second owner of a value Swift permits exactly one of, and calling the " +
            "type's copy witness aborts the process. Pass the value through a member that names " +
            "the ~Copyable type directly — a borrowing or consuming parameter, or a plain return — " +
            "which crosses as a pointer that is borrowed or moved rather than copied.");
}
