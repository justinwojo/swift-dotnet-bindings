// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace BindingsGeneration;

/// <summary>
/// Which .NET engines refuse which register-convention shapes of a direct Swift-convention
/// transition: the table <c>Swift.Runtime.SwiftTransitionGuard</c> refuses by at run time, restated
/// by name so a diagnostic or a package check can say where a guarded member throws. The bits are
/// those of <c>Swift.Runtime.SwiftTransitionFacts</c>; a unit test holds the table to the runtime's.
/// Base class library only: the generator, the build's gates and the SDK's MSBuild tasks
/// link-compile it.
/// </summary>
public static class SwiftTransitionEngines
{
    public const ulong SelfRegister = 1UL << 0;
    public const ulong SpecialBeforeArguments = 1UL << 1;
    public const ulong LoweredSignature = 1UL << 2;
    public const ulong FunctionPointerSelfRegister = 1UL << 8;
    public const ulong FunctionPointerSpecialBeforeArguments = 1UL << 9;
    public const ulong FunctionPointerLoweredSignature = 1UL << 10;
    public const ulong ReverseCallback = 1UL << 16;

    /// <summary>Every shape with a name; any other bit is refused on every engine.</summary>
    public const ulong Known = SelfRegister | SpecialBeforeArguments | LoweredSignature
        | FunctionPointerSelfRegister | FunctionPointerSpecialBeforeArguments | FunctionPointerLoweredSignature | ReverseCallback;

    private static readonly (ulong Bit, string Name)[] Names =
    {
        (SelfRegister, nameof(SelfRegister)),
        (SpecialBeforeArguments, nameof(SpecialBeforeArguments)),
        (LoweredSignature, nameof(LoweredSignature)),
        (FunctionPointerSelfRegister, nameof(FunctionPointerSelfRegister)),
        (FunctionPointerSpecialBeforeArguments, nameof(FunctionPointerSpecialBeforeArguments)),
        (FunctionPointerLoweredSignature, nameof(FunctionPointerLoweredSignature)),
        (ReverseCallback, nameof(ReverseCallback)),
    };

    /// <summary>One engine and the known shapes its guard refuses.</summary>
    public sealed record Engine(string Name, ulong Refuses);

    /// <summary>
    /// The engines, in a fixed order. NativeAOT and a CoreCLR that compiles code run every known
    /// shape; Mono mishandles the self register; the CoreCLR interpreter mishandles a different set
    /// on each architecture.
    /// </summary>
    public static readonly ImmutableArray<Engine> All = ImmutableArray.Create(
        new Engine("NativeAOT", 0),
        new Engine("Mono", SelfRegister | FunctionPointerSelfRegister),
        new Engine("CoreCLR (JIT)", 0),
        new Engine("the CoreCLR interpreter (arm64)", SelfRegister | SpecialBeforeArguments | FunctionPointerSpecialBeforeArguments),
        new Engine("the CoreCLR interpreter (x86_64)", LoweredSignature | FunctionPointerLoweredSignature | ReverseCallback));

    /// <summary>The engines whose guard refuses a transition of <paramref name="shapes"/>: all of them for a bit no table entry names.</summary>
    public static ImmutableArray<string> Refusing(ulong shapes) =>
        All.Where(e => (shapes & (e.Refuses | ~Known)) != 0).Select(e => e.Name).ToImmutableArray();

    /// <summary>The names of <paramref name="shapes"/>' bits, in bit order; an unnamed bit is spelled in hex.</summary>
    public static ImmutableArray<string> ShapeNames(ulong shapes)
    {
        var names = Names.Where(n => (shapes & n.Bit) != 0).Select(n => n.Name).ToList();
        if ((shapes & ~Known) is var unknown and not 0)
            names.Add($"0x{unknown:X}");
        return names.ToImmutableArray();
    }

    /// <summary>The bits of shape <paramref name="names"/>, or null when one is not a shape name or hex spelling this table reads.</summary>
    public static ulong? ShapesOf(IEnumerable<string> names)
    {
        ulong shapes = 0;
        foreach (var name in names)
        {
            var bit = Names.FirstOrDefault(n => n.Name == name).Bit;
            if (bit != 0)
                shapes |= bit;
            else if (name.StartsWith("0x", StringComparison.Ordinal)
                     && ulong.TryParse(name.AsSpan(2), System.Globalization.NumberStyles.AllowHexSpecifier, null, out var hex) && hex != 0 && (hex & Known) == 0)
                shapes |= hex;
            else
                return null;
        }
        return shapes;
    }
}
