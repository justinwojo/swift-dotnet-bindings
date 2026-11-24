// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;

namespace Swift.Runtime;

/// <summary>
/// Translates between the managed identity of a resilient Swift enum's cases (its binding's
/// <c>CaseTag</c> constants) and the tags the loaded library gives them.
/// </summary>
/// <remarks>
/// <para>
/// A resilient enum's case tags belong to the library that defines it, not to its interface: a
/// newer library may insert a case anywhere, so the tag of a case can differ between OS releases,
/// and so between the SDK a binding was generated from and the OS it runs on. Swift therefore
/// exports one 32-bit global per case (its "enum case" symbol, the case's mangled name with a
/// <c>WC</c> suffix) holding the tag the running library uses, and every Swift client reads it to
/// build or test the case. A binding does the same through this map.
/// </para>
/// <para>
/// The managed identity of a case is derived from its name (<c>EnumDecl.GetCaseId</c> in the
/// generator), so every binding of the enum, from whichever SDK, gives a case the same constant: a
/// library compiled against one copy of a package keeps working in an app that runs another.
/// </para>
/// <para>
/// When none of the case symbols resolves, the enum is not resilient in the library this process
/// loaded (a module built without library evolution), and its tags are the declaration-order tags
/// the generator recorded. When some resolve, a case whose symbol is missing is not in the loaded
/// library: it cannot be built, and no value of the enum is that case.
/// </para>
/// </remarks>
public sealed class SwiftEnumCaseMap
{
    /// <summary>The managed identity <see cref="ToCaseId"/> returns for a tag the binding names no case for.</summary>
    public const uint UnknownCase = uint.MaxValue;

    private const uint MaximumTag = 1u << 20;

    private readonly string _libraryName;
    private readonly string[] _caseSymbols;
    private readonly uint[] _caseIds;
    private readonly uint[] _declarationTags;
    private Resolved? _resolved;

    /// <summary>Creates the map of one enum. The three arrays hold one entry per case, in the same order.</summary>
    /// <param name="libraryName">The library that defines the enum.</param>
    /// <param name="caseSymbols">Each case's "enum case" symbol, without the leading underscore.</param>
    /// <param name="caseIds">Each case's managed identity.</param>
    /// <param name="declarationTags">Each case's tag when the enum is not resilient.</param>
    public SwiftEnumCaseMap(string libraryName, string[] caseSymbols, uint[] caseIds, uint[] declarationTags)
    {
        ArgumentNullException.ThrowIfNull(libraryName);
        ArgumentNullException.ThrowIfNull(caseSymbols);
        ArgumentNullException.ThrowIfNull(caseIds);
        ArgumentNullException.ThrowIfNull(declarationTags);
        if (caseSymbols.Length != caseIds.Length || caseIds.Length != declarationTags.Length)
            throw new ArgumentException("The case symbols, identities and declaration tags must have one entry per case.");
        _libraryName = libraryName;
        _caseSymbols = caseSymbols;
        _caseIds = caseIds;
        _declarationTags = declarationTags;
    }

    /// <summary>
    /// The managed identity of the case the loaded library tags <paramref name="tag"/>, or
    /// <see cref="UnknownCase"/> when the binding names no such case (a case the library gained
    /// after the SDK the binding was generated from).
    /// </summary>
    public uint ToCaseId(uint tag)
    {
        var idByTag = Resolve().IdByTag;
        return tag < (uint)idByTag.Length ? idByTag[tag] : UnknownCase;
    }

    /// <summary>
    /// The tag the loaded library gives the case <paramref name="caseId"/> names.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">The loaded library has no such case.</exception>
    public uint ToTag(uint caseId)
    {
        var resolved = Resolve();
        var index = Array.IndexOf(_caseIds, caseId);
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(caseId), caseId, "The enum has no case with this identity.");
        return resolved.TagByCase[index] is uint tag
            ? tag
            : throw new PlatformNotSupportedException(
                $"The case {_caseSymbols[index]} is not in the {_libraryName} this process loaded.");
    }

    /// <summary>Whether the loaded library has the case <paramref name="caseId"/> names.</summary>
    public bool HasCase(uint caseId)
    {
        var index = Array.IndexOf(_caseIds, caseId);
        return index >= 0 && Resolve().TagByCase[index] is not null;
    }

    private Resolved Resolve()
    {
        var resolved = Volatile.Read(ref _resolved);
        if (resolved is not null)
            return resolved;
        resolved = Read();
        return Interlocked.CompareExchange(ref _resolved, resolved, null) ?? resolved;
    }

    private unsafe Resolved Read()
    {
        var tags = new uint?[_caseSymbols.Length];
        var found = 0;
        if (SwiftFrameworkResolver.TryLoadWithFrameworkFallback(_libraryName, out var library))
        {
            try
            {
                for (var i = 0; i < _caseSymbols.Length; i++)
                {
                    if (NativeLibrary.TryGetExport(library, _caseSymbols[i], out var address))
                    {
                        // A case index of a real enum is far below this bound; a larger value is not one.
                        var tag = *(uint*)address;
                        if (tag < MaximumTag)
                            tags[i] = tag;
                        found++;
                    }
                }
            }
            finally
            {
                // The exports read above are values, not addresses kept past this call.
                NativeLibrary.Free(library);
            }
        }
        if (found == 0)
        {
            for (var i = 0; i < tags.Length; i++)
                tags[i] = _declarationTags[i];
        }

        var size = 0;
        foreach (var tag in tags)
        {
            if (tag is uint t && t >= (uint)size)
                size = checked((int)t + 1);
        }
        var idByTag = new uint[size];
        Array.Fill(idByTag, UnknownCase);
        for (var i = 0; i < tags.Length; i++)
        {
            if (tags[i] is uint t)
                idByTag[t] = _caseIds[i];
        }
        return new Resolved(idByTag, tags);
    }

    private sealed record Resolved(uint[] IdByTag, uint?[] TagByCase);
}
