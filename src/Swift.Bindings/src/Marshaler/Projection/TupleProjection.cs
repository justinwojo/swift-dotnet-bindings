// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Projection for Swift tuples ↔ C# ValueTuple.
/// Composes with per-element projections for element-wise marshalling.
///
/// Parameter direction: per-element conversion from public types to P/Invoke types.
/// Return direction: per-element MarshalFromSwift + conversion from result.Item{N}.
/// </summary>
public class TupleProjection : ITypeProjection
{
    private readonly IReadOnlyList<ITypeProjection> _elementProjections;

    public TupleProjection(IReadOnlyList<ITypeProjection> elementProjections)
    {
        _elementProjections = elementProjections;
    }

    /// <summary>The per-element projections.</summary>
    public IReadOnlyList<ITypeProjection> ElementProjections => _elementProjections;

    public string PublicType
    {
        get
        {
            var types = _elementProjections.Select(p => p.PublicType);
            return $"({string.Join(", ", types)})";
        }
    }

    public string PInvokeType
    {
        get
        {
            var types = _elementProjections.Select(p => p.PInvokeType);
            return $"ValueTuple<{string.Join(", ", types)}>";
        }
    }

    public string? PInvokeAttribute => null;

    /// <summary>
    /// The carrier/container element type: composes each element's <see cref="ITypeProjection.MarshalFromSwiftType"/>
    /// (a class → its wrapper type, String → <c>SwiftString</c>) rather than the P/Invoke type
    /// (class → <c>IntPtr</c>). When this tuple is the payload of <c>SwiftOptional</c>/<c>SwiftResult</c>/
    /// <c>SwiftArray</c>/etc., the carrier's Swift value-witness metadata is derived from this C# type, so a
    /// class element MUST appear as its wrapper type (metadata <c>Kind == Class</c>) — otherwise the tuple
    /// VWT treats the class slot as POD, the carrier neither retains the class on copy nor releases it on
    /// destroy, and the wire buffer's <c>+1</c> leaks. Mirrors Array/Dictionary/Set/Optional/Result, which
    /// already compose <c>MarshalFromSwiftType</c> for their element/inner types.
    /// </summary>
    public string MarshalFromSwiftType =>
        $"ValueTuple<{string.Join(", ", _elementProjections.Select(p => p.MarshalFromSwiftType))}>";

    public MarshalPlan GetParameterPlan(string paramName)
    {
        var needsConversion = _elementProjections.Any(p => p.GetParameterElementConversion("x") != null);
        if (!needsConversion)
            return MarshalPlan.PassThrough(paramName);

        // Per-element conversion
        var setup = new List<MarshalStatement>();
        var elemExprs = new List<string>();

        for (int i = 0; i < _elementProjections.Count; i++)
        {
            var proj = _elementProjections[i];
            var itemAccess = $"{paramName}.Item{i + 1}";
            var conv = proj.GetParameterElementConversion(itemAccess);
            if (conv != null)
            {
                var elemVar = $"{paramName}Elem{i}";
                setup.Add(new MarshalStatement.Line($"var {elemVar} = {conv};"));
                elemExprs.Add(elemVar);
            }
            else
            {
                elemExprs.Add(itemAccess);
            }
        }

        return new MarshalPlan
        {
            SetupStatements = setup,
            PInvokeExpression = $"({string.Join(", ", elemExprs)})"
        };
    }

    public MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy)
    {
        // Top-level tuple return needs the same per-element treatment as the Optional<Tuple>
        // inner-element path: the P/Invoke result is a ValueTuple<…> at PInvokeType (e.g.
        // ValueTuple<SwiftString, IntPtr> for (String, Class)), but the public surface is the
        // PublicType ValueTuple ((string, Animal)). Without the per-element lift, a direct
        // (String, Class) return would leak raw IntPtr into the public tuple's class slot.
        bool NeedsConversion(ITypeProjection p)
            => IsRawPointerClassProjection(p) || p.GetReturnElementConversion("x") != null;

        var needsConversion = _elementProjections.Any(NeedsConversion);

        if (!needsConversion)
        {
            return strategy switch
            {
                ReturnStrategy.Direct => MarshalPlan.PassThrough(resultName),
                _ => MarshalPlan.PassThrough(resultName)
            };
        }

        // Per-element marshalling from result items
        var setup = new List<MarshalStatement>();
        var elemExprs = new List<string>();
        var requiresUnsafe = false;

        for (int i = 0; i < _elementProjections.Count; i++)
        {
            var proj = _elementProjections[i];
            var itemAccess = strategy == ReturnStrategy.Direct
                ? $"{resultName}.Item{i + 1}"
                : $"{resultName}.Item{i + 1}";

            string? conv;
            if (IsRawPointerClassProjection(proj))
            {
                // Tuple PInvokeType is IntPtr for class fields — lift to the public class
                // instance via MarshalFromSwiftObject. Mirrors the path in
                // GetReturnElementConversion below for the inner-tuple case.
                conv = RawPointerClassLift(proj, itemAccess);
            }
            else
            {
                conv = proj.GetReturnElementConversion(itemAccess);
            }

            if (conv != null)
            {
                var elemVar = $"elem{i}";
                setup.Add(new MarshalStatement.Line($"var {elemVar} = {conv};"));
                elemExprs.Add(elemVar);
            }
            else
            {
                elemExprs.Add(itemAccess);
            }
        }

        return new MarshalPlan
        {
            SetupStatements = setup,
            PInvokeExpression = $"({string.Join(", ", elemExprs)})",
            RequiresUnsafe = requiresUnsafe
        };
    }

    /// <summary>
    /// Projections whose P/Invoke field for a tuple slot is a raw <c>IntPtr</c> that must
    /// be materialized via <c>SwiftMarshal.MarshalFromSwiftObject&lt;T&gt;</c> to produce the
    /// public instance. All members share the same shape: <c>PInvokeType == "IntPtr"</c>,
    /// <c>GetReturnPlan</c> wraps via MarshalFromSwiftObject, and
    /// <c>GetReturnElementConversion</c> returns null (so the tuple cannot delegate to it).
    /// Covers pure-Swift <see cref="ClassProjection"/>, ObjC-rooted
    /// <see cref="ObjCRootedClassProjection"/>, non-frozen struct/complex-enum
    /// <see cref="NonFrozenStructProjection"/>, and the Swift KeyPath family
    /// (<see cref="KeyPathProjection"/>), which all share the IntPtr-via-MarshalFromSwiftObject shape.
    /// </summary>
    private static bool IsRawPointerClassProjection(ITypeProjection p)
        => p is ClassProjection or ObjCRootedClassProjection or NonFrozenStructProjection or KeyPathProjection;

    private static string RawPointerClassLift(ITypeProjection p, string elementVar)
        => $"({p.PublicType})SwiftMarshal.MarshalFromSwiftObject<{p.PublicType}>({elementVar})";

    /// <summary>
    /// True when this tuple, extracted from an OWNING carrier (Optional.Some / Result.Success), must
    /// bind <c>.Some</c>/<c>.Success</c> ONCE (each access re-extracts, leaking a fresh +1 per access)
    /// and dispose any self-owning elements consumed in place. Two shapes force it:
    ///  - class / non-frozen-struct elements, which the carrier's class-aware metadata extracts as a
    ///    self-owning (+1) wrapper handed to the caller, and
    ///  - self-owning <c>ISwiftObject</c> elements (e.g. <c>SwiftString</c> at +1) converted to a
    ///    managed value in place, which must be disposed after conversion or leak.
    /// </summary>
    public bool RequiresOwnedCarrierExtraction =>
        _elementProjections.Any(p => IsRawPointerClassProjection(p) || p.ElementRequiresDisposal);

    /// <summary>
    /// Builds the setup statements plus the public ValueTuple expression for a tuple already bound to
    /// <paramref name="tupleLocal"/> and extracted ONCE from an owning carrier. The carrier's
    /// class-aware tuple metadata (see <see cref="MarshalFromSwiftType"/>) means each element arrives
    /// as its self-owning wrapper type: class / non-frozen-struct elements are handed to the caller
    /// as-is (the caller owns the +1); elements converted to a non-disposable public type (e.g.
    /// <c>SwiftString</c> → <c>string</c>) are read into a managed local then disposed after every
    /// element has been read. The returned expression is a ValueTuple of per-element locals, so it
    /// stays valid after the consumed-element wrappers are disposed.
    /// </summary>
    public (List<MarshalStatement> Setup, string Expression) GetOwnedCarrierReturnConversion(string tupleLocal)
    {
        var setup = new List<MarshalStatement>();
        var disposeAfter = new List<MarshalStatement>();
        var elemVars = new List<string>();

        for (int i = 0; i < _elementProjections.Count; i++)
        {
            var proj = _elementProjections[i];
            var itemAccess = $"{tupleLocal}.Item{i + 1}";
            var elemVar = $"{tupleLocal}_e{i}";
            var conv = proj.GetReturnElementConversion(itemAccess) ?? itemAccess;
            setup.Add(new MarshalStatement.Line($"var {elemVar} = {conv};"));

            if (proj.ElementRequiresDisposal)
            {
                // Self-owning ISwiftObject element converted to a managed value (e.g. SwiftString →
                // string): dispose the wrapper's +1 after every element has been read. Class /
                // non-frozen-struct elements pass through self-owning and are disposed by the caller.
                disposeAfter.Add(new MarshalStatement.Line($"{itemAccess}.Dispose();"));
            }

            elemVars.Add(elemVar);
        }

        setup.AddRange(disposeAfter);
        return (setup, $"({string.Join(", ", elemVars)})");
    }

    /// <summary>
    /// Element-level conversion for when this Tuple is an element of a container (SwiftArray /
    /// SwiftDictionary / SwiftSet / SwiftOptional / SwiftResult). The container's generic argument is
    /// the element's <see cref="ITypeProjection.MarshalFromSwiftType"/>, so for this tuple the field
    /// supplied here is its <see cref="MarshalFromSwiftType"/> form — each slot is already its wrapper
    /// type (a class is the wrapper instance, not a raw <c>IntPtr</c>; a String is <c>SwiftString</c>).
    /// Class / non-frozen-struct slots therefore pass through unchanged; only slots with their own
    /// element conversion (e.g. <c>SwiftString</c> → <c>{var}.ToString()</c>) are rewritten. This is
    /// distinct from <see cref="GetReturnPlan"/>, which operates on the direct P/Invoke result at
    /// <see cref="PInvokeType"/> (class slot is <c>IntPtr</c>) and must lift via MarshalFromSwiftObject.
    /// </summary>
    public string? GetReturnElementConversion(string elementVar)
    {
        var elemExprs = new List<string>();
        bool anyConversion = false;
        for (int i = 0; i < _elementProjections.Count; i++)
        {
            var proj = _elementProjections[i];
            var itemAccess = $"{elementVar}.Item{i + 1}";
            var conv = proj.GetReturnElementConversion(itemAccess);

            if (conv != null)
            {
                elemExprs.Add(conv);
                anyConversion = true;
            }
            else
            {
                elemExprs.Add(itemAccess);
            }
        }
        return anyConversion ? $"({string.Join(", ", elemExprs)})" : null;
    }

    public bool RequiresSwiftWrapper => false;
    public string? GetSwiftWrapperCode(SwiftWrapperContext context) => null;

    public T Accept<T>(IProjectionVisitor<T> visitor) => visitor.Visit(this);
}
