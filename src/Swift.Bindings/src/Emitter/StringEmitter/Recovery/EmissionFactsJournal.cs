// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Undo log for the one mutation emission is allowed to make to the frozen type database.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ITypeDatabase.ApplyEmissionResult"/> stamps emission-discovered facts — nested type
/// renames, emitted member counts, emitted class methods, metadata flags — onto records that are
/// otherwise immutable after the freeze. Those stamps are the one way a discarded attempt could leak
/// into the attempt that replaces it, and they cannot be undone by re-applying an inverse:
/// <c>TypeEmissionResult.ApplyTo</c> treats a null field as "leave unchanged", so there is no result
/// value that means "put this back the way it was".
/// </para>
/// <para>
/// The journal therefore keeps the pre-image of the whole record, captured the first time an attempt
/// stamps a given type. Restoring writes those records back verbatim, which is why a discarded
/// attempt leaves the database bit-identical rather than merely close.
/// </para>
/// <para>
/// Only the first write per type is captured. Later writes within the same attempt are stamps on top
/// of a record the attempt itself produced, so the original pre-image is still the correct thing to
/// roll back to.
/// </para>
/// </remarks>
internal sealed class EmissionFactsJournal
{
    private readonly Dictionary<string, (SwiftTypeName Name, TypeRecord PreImage)> _preImages =
        new(StringComparer.Ordinal);

    /// <summary>Number of distinct types this attempt has stamped.</summary>
    public int Count => _preImages.Count;

    /// <summary>
    /// Records the state of <paramref name="name"/>'s record before the attempt's first stamp on it.
    /// </summary>
    public void Capture(SwiftTypeName name, TypeRecord preImage) =>
        _preImages.TryAdd(name.ModuleQualifiedName, (name, preImage));

    /// <summary>Writes every captured pre-image back, undoing the attempt's stamps.</summary>
    public void RestoreInto(ITypeDatabase typeDatabase)
    {
        foreach (var (name, preImage) in _preImages.Values)
        {
            typeDatabase.RestoreEmissionRecord(name, preImage);
        }

        _preImages.Clear();
    }

    /// <summary>Drops the log without restoring — the attempt settled and its stamps are keepers.</summary>
    public void Commit() => _preImages.Clear();
}
