// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration.ObjC;

/// <summary>
/// One <c>T *values</c> + <c>count:</c> parameter pair that projects to a single C# array
/// parameter. Both indices refer to positions in the method's parameter list.
/// </summary>
public sealed record ObjCArrayParameterPlan
{
    /// <summary>Index of the pointer parameter — the one that becomes the C# array.</summary>
    public required int PointerParameterIndex { get; init; }

    /// <summary>Index of the element-count parameter, supplied from the array's length.</summary>
    public required int CountParameterIndex { get; init; }

    /// <summary>Mapped C# element type of the array (e.g. <c>CGPoint</c>).</summary>
    public required string ElementType { get; init; }

    /// <summary>Mapped C# type of the count parameter (e.g. <c>nuint</c>).</summary>
    public required string CountType { get; init; }

    /// <summary>
    /// Mapped C# type of every other parameter, by parameter index; null at the two indices the
    /// pair occupies. These are forwarded verbatim from the public overload to the internal member,
    /// so the projection only accepts a method whose remaining parameters bgen reproduces
    /// unchanged (see <see cref="ObjCArrayParameterProjection"/>).
    /// </summary>
    public required IReadOnlyList<string?> PassThroughTypes { get; init; }
}

/// <summary>
/// Recognises the C-array argument convention an ObjC selector uses when it takes a run of value
/// types: a pointer to the first element immediately followed by an element count
/// (<c>+polylineWithCoordinates:count:</c>, <c>-setCoordinates:count:</c>). Structurally the pointer
/// is indistinguishable from a pointer to a single value, which is why it otherwise reaches the
/// <c>out T</c> projection — a projection that is wrong twice over for an array: on input it zeroes
/// the caller's buffer before the callee reads it, and on output it hands the callee storage for one
/// element to write <c>count</c> of.
///
/// The correlation signal is taken from the SELECTOR keyword, not the C parameter name: the keyword
/// is part of the method's published contract and survives header styles that omit parameter names,
/// while the variable name is incidental. Only an exact <c>count</c> keyword directly after the
/// pointer counts — anything looser (a range, a byte length, a stride) would be a guess about
/// units, and guessing wrong reintroduces the same silent-wrong-data failure in a new shape.
/// </summary>
public static class ObjCArrayParameterProjection
{
    /// <summary>
    /// C# types a count parameter may map to. The count is consumed as
    /// <c>({CountType})array.Length</c>, so it has to be an integral type an <c>int</c> converts to.
    /// </summary>
    private static readonly HashSet<string> IntegralCountTypes = new(StringComparer.Ordinal)
    {
        "nint", "nuint", "int", "uint", "long", "ulong", "short", "ushort", "byte", "sbyte",
    };

    /// <summary>
    /// Finds the single pointer+count pair in <paramref name="method"/>, or null when there is none,
    /// when the shape is ambiguous, or when the method's remaining parameters cannot be forwarded.
    ///
    /// Two conditions fail closed rather than guess. A method carrying TWO candidate pairs gets no
    /// plan, because a member gets one wrapper and picking one pair would leave the other
    /// mis-projected. And every remaining parameter must be one bgen reproduces verbatim from the
    /// ApiDefinition — the wrapper forwards it by name into the generated member's signature, so a
    /// parameter bgen re-types (a block, which becomes a generated delegate; a protocol reference,
    /// which becomes the generated interface) would not compile against it.
    /// </summary>
    public static ObjCArrayParameterPlan? TryPlan(
        ObjCMethodDecl method,
        HashSet<string>? genericTypeParams,
        Dictionary<string, ObjCTypeRef>? typedefMap,
        Dictionary<string, ObjCTypeRef>? blockTypedefMap,
        HashSet<string>? enumNames,
        HashSet<string>? localProtocolNames,
        HashSet<string>? classProtocolClashNames)
    {
        // A variadic selector already carries a synthesized trailing IntPtr for the va_list; the
        // wrapper cannot forward one, so leave those alone.
        if (method.IsVariadic)
            return null;

        var keywords = SelectorKeywords(method.Selector);
        var pointerIndex = -1;
        var countType = "";

        for (var i = 0; i + 1 < method.Parameters.Count; i++)
        {
            var pointer = method.Parameters[i];
            if (!ObjCTypeMapper.IsValueTypePointerShape(pointer.Type, typedefMap, enumNames))
                continue;
            if (i + 1 >= keywords.Count || !string.Equals(keywords[i + 1], "count", StringComparison.OrdinalIgnoreCase))
                continue;

            var mappedCount = ObjCTypeMapper.MapType(method.Parameters[i + 1].Type, typedefMap: typedefMap);
            if (!IntegralCountTypes.Contains(mappedCount))
                continue;

            // Two candidate pairs in one selector: no plan at all (see summary).
            if (pointerIndex >= 0)
                return null;

            pointerIndex = i;
            countType = mappedCount;
        }

        if (pointerIndex < 0)
            return null;

        var passThrough = new string?[method.Parameters.Count];
        for (var i = 0; i < method.Parameters.Count; i++)
        {
            if (i == pointerIndex || i == pointerIndex + 1)
                continue;

            var param = method.Parameters[i];
            // Out parameters of any flavour would have to be forwarded as `out`, and a second
            // value-type pointer is exactly the ambiguity this projection exists to resolve.
            if (ObjCTypeMapper.IsNSErrorOutParameter(param.Type)
                || ObjCTypeMapper.IsValueTypePointerShape(param.Type, typedefMap, enumNames)
                || param.Type.IsBlock
                || param.Type.IsFunctionPointer
                || param.Type.ProtocolQualifications.Count > 0)
                return null;

            // A block reaching here through a typedef is still a block once resolved.
            if (blockTypedefMap != null && blockTypedefMap.ContainsKey(param.Type.Name))
                return null;

            var synthesized = new HashSet<string>(StringComparer.Ordinal);
            var mapped = ObjCTypeMapper.MapType(
                param.Type, genericTypeParams: genericTypeParams, typedefMap: typedefMap,
                blockTypedefMap: blockTypedefMap, localProtocolNames: localProtocolNames,
                classProtocolClashNames: classProtocolClashNames, synthesizedProtocolInterfaces: synthesized);

            // A protocol reference — spelled bare for a protocol this binding declares, or `IFoo`
            // for an SDK one — names a type bgen generates rather than copies, so the forwarding
            // call could not be written against the ApiDefinition spelling.
            if (synthesized.Count > 0)
                return null;
            if (localProtocolNames != null && localProtocolNames.Contains(mapped))
                return null;

            passThrough[i] = mapped;
        }

        return new ObjCArrayParameterPlan
        {
            PointerParameterIndex = pointerIndex,
            CountParameterIndex = pointerIndex + 1,
            ElementType = ObjCTypeMapper.MapValueTypePointerParameterType(method.Parameters[pointerIndex].Type, typedefMap),
            CountType = countType,
            PassThroughTypes = passThrough,
        };
    }

    /// <summary>
    /// Whether the parameter at <paramref name="index"/> carries the C-array shape: a pointer to a
    /// value type whose immediately following selector keyword is <c>count</c>. This is the same
    /// signal <see cref="TryPlan"/> reads, exposed on its own so a caller that cannot USE a plan can
    /// still recognise the shape and refuse it. Projecting an array pointer as <c>out T</c> gives the
    /// callee one element of storage to read or write <c>count</c> elements through, so a member
    /// holding one has no sound signature at all and must fail closed rather than ship.
    /// <para/>
    /// Deliberately looser than <see cref="TryPlan"/> in one respect: it does not require the count
    /// parameter to map to an integral type. A declaration that names a count this projection cannot
    /// consume is still a declaration about an array, and guessing otherwise would put the wrong
    /// projection back on exactly the shape the keyword warned about.
    /// </summary>
    public static bool IsArrayShapedPointerParameter(
        ObjCMethodDecl method,
        int index,
        Dictionary<string, ObjCTypeRef>? typedefMap,
        HashSet<string>? enumNames)
    {
        if (index < 0 || index + 1 >= method.Parameters.Count)
            return false;
        if (!ObjCTypeMapper.IsValueTypePointerShape(method.Parameters[index].Type, typedefMap, enumNames))
            return false;

        var keywords = SelectorKeywords(method.Selector);
        return index + 1 < keywords.Count
            && string.Equals(keywords[index + 1], "count", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Splits an ObjC selector into its keywords, one per parameter — <c>a:b:c:</c> → a, b, c. A
    /// selector with no colons (a nullary selector) yields the single bare keyword, which no
    /// parameter can index into.
    /// </summary>
    internal static List<string> SelectorKeywords(string selector)
    {
        var keywords = new List<string>();
        var start = 0;
        for (var i = 0; i < selector.Length; i++)
        {
            if (selector[i] != ':')
                continue;
            keywords.Add(selector[start..i]);
            start = i + 1;
        }
        if (keywords.Count == 0)
            keywords.Add(selector);
        return keywords;
    }
}
