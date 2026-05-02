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
    /// <see cref="ObjCRootedClassProjection"/>, and non-frozen struct/complex-enum
    /// <see cref="NonFrozenStructProjection"/>.
    /// </summary>
    private static bool IsRawPointerClassProjection(ITypeProjection p)
        => p is ClassProjection or ObjCRootedClassProjection or NonFrozenStructProjection;

    private static string RawPointerClassLift(ITypeProjection p, string elementVar)
        => $"({p.PublicType})SwiftMarshal.MarshalFromSwiftObject<{p.PublicType}>({elementVar})";

    /// <summary>
    /// Element-level conversion for when this Tuple is the inner of a container that materializes
    /// fields as raw P/Invoke shapes — e.g. <c>SwiftOptional&lt;ValueTuple&lt;SwiftString, IntPtr&gt;&gt;</c>
    /// where <c>.Some</c> returns a ValueTuple with each field at its <c>PInvokeType</c>.
    /// Composes per-element conversions: <c>StringProjection</c> emits <c>{var}.Item1.ToString()</c>;
    /// <c>ClassProjection</c> needs an explicit lift from IntPtr to the class instance because
    /// nothing else in the Tuple-of-classes path constructs the wrapper (unlike SwiftArray/Dictionary
    /// AsProjected lambdas, which receive already-materialized instances).
    /// </summary>
    public string? GetReturnElementConversion(string elementVar)
    {
        var elemExprs = new List<string>();
        bool anyConversion = false;
        for (int i = 0; i < _elementProjections.Count; i++)
        {
            var proj = _elementProjections[i];
            var itemAccess = $"{elementVar}.Item{i + 1}";
            string? conv;
            if (IsRawPointerClassProjection(proj))
            {
                // Tuple stores PInvokeType for each field. For class-shaped slots that's
                // IntPtr (pure Swift class OR ObjC-rooted class) — lift via
                // MarshalFromSwiftObject. SwiftArray/SwiftDictionary don't hit this case
                // because their AsProjected lambdas receive already-materialized class
                // instances (T = MarshalFromSwiftType, not IntPtr).
                conv = RawPointerClassLift(proj, itemAccess);
            }
            else
            {
                conv = proj.GetReturnElementConversion(itemAccess);
            }

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
