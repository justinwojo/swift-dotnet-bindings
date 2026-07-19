// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;

namespace BindingsGeneration;

/// <summary>
/// A typed, emission-time descriptor of one emitted native call: the entry point it binds, the library
/// it binds against, the calling convention it resolved to, and the lowered C# carriers of its return
/// and parameters. Built from the exact facts that render the <c>[UnmanagedCallConv]</c> /
/// <c>[LibraryImport]</c> / signature text — the same resolved calling convention, the same combined-and
/// -deduplicated parameter string — so a plan and the text it describes agree by construction rather
/// than by a later cross-check.
/// </summary>
/// <remarks>
/// <para>
/// This is the foundation for typed call-plan validation — the successor <see cref="AbiContractChecker"/>
/// names in its own header, where today it recovers a <c>PInvokeInfo</c> by regex-scanning already
/// -emitted C#. A future pass swaps that text scan for these plans, read straight from the emission
/// context. This wave only <em>populates</em> the plans and proves them stable (double-emit yields
/// identical plans) and text-faithful (a plan's fields appear verbatim in the declaration it describes);
/// it does not yet validate against them.
/// </para>
/// <para>
/// The plan deliberately carries only what the P/Invoke emission site already knows — entry point,
/// library, resolved calling convention, and lowered return/parameter carriers. The richer ABI facts a
/// full validator will want (wrapper <see cref="ArtifactId"/>, per-target symbol availability,
/// self / indirect-result / ownership conventions, and struct size/align/register layout) are
/// <em>not</em> reconstructed here from partial information; they are left for the session that turns
/// these descriptors into the validator, so the foundation never records a fact it cannot source
/// faithfully.
/// </para>
/// </remarks>
public sealed record AbiCallPlan
{
    /// <summary>The C# name of the emitted P/Invoke declaration.</summary>
    public required string MethodName { get; init; }

    /// <summary>The native entry-point symbol the declaration binds (the <c>EntryPoint</c> argument).</summary>
    public required string EntryPoint { get; init; }

    /// <summary>The library the declaration binds against (the <c>LibraryImport</c> path).</summary>
    public required string Library { get; init; }

    /// <summary>
    /// The calling convention actually rendered, after <see cref="PInvokeEmitHelper.SelectCallingConvention"/>
    /// reconciles the entry-point prefix against the caller's request. This is the resolved convention,
    /// not the requested one, so it matches the emitted <c>CallConvCdecl</c>/<c>CallConvSwift</c> attribute.
    /// </summary>
    public required PInvokeCallingConvention CallingConvention { get; init; }

    /// <summary>
    /// The lowered C# return carrier as rendered — <c>"void"</c> for an async declaration, otherwise the
    /// declared return type.
    /// </summary>
    public required string ReturnCarrier { get; init; }

    /// <summary>
    /// The lowered C# parameter carriers, in declaration order, as the type portion of each rendered
    /// parameter (the segment with its trailing name removed, any <c>[MarshalAs]</c>/<c>ref</c>/<c>in</c>
    /// modifiers kept). Metadata parameters are included, matching the rendered signature. Empty for a
    /// parameterless call.
    /// </summary>
    public required ImmutableArray<string> ParameterCarriers { get; init; }

    /// <summary>True when the declaration is an async P/Invoke (which always renders a <c>void</c> return).</summary>
    public bool IsAsync { get; init; }

    /// <summary>
    /// A stable diagnostic identity for this call: its C# method name and its entry-point symbol, separated
    /// by a space. Within one containing C# type two P/Invoke declarations sharing both would be a duplicate
    /// declaration (<c>CS0111</c>); across different containing types they can legally coincide, so this is
    /// the primary <em>ordering</em> key for the plan snapshot, NOT the dedup identity — the whole plan
    /// value is (see <see cref="Equals(AbiCallPlan?)"/>), so two calls sharing a key but differing in
    /// carriers are both retained rather than one silently overwriting the other.
    /// </summary>
    public string Key => string.Concat(MethodName, " ", EntryPoint);

    // The compiler-synthesized record equality would compare ParameterCarriers with
    // EqualityComparer<ImmutableArray<string>>.Default, whose Equals is backing-array *reference*
    // identity — so two plans with the same carriers but distinct arrays (every double-emit) would be
    // unequal, and equal-content plans would collide-miss in a HashSet. The determinism contract, and any
    // future consumer that compares or dedups plans, needs structural value equality over the carriers.
    public bool Equals(AbiCallPlan? other) =>
        other is not null
        && MethodName == other.MethodName
        && EntryPoint == other.EntryPoint
        && Library == other.Library
        && CallingConvention == other.CallingConvention
        && ReturnCarrier == other.ReturnCarrier
        && IsAsync == other.IsAsync
        && ParameterCarriers.AsSpan().SequenceEqual(other.ParameterCarriers.AsSpan());

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(MethodName);
        hash.Add(EntryPoint);
        hash.Add(Library);
        hash.Add(CallingConvention);
        hash.Add(ReturnCarrier);
        hash.Add(IsAsync);
        foreach (var carrier in ParameterCarriers)
            hash.Add(carrier);
        return hash.ToHashCode();
    }
}
