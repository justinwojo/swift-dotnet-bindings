// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Detects the "closed static factory" accessor shape:
/// a STATIC property declared on a generic type whose accessor signature does NOT
/// reference the parent's open generic parameters and whose return type is a fully
/// closed bound generic of the same parent nominal type (e.g.
/// <c>PatBoundedStatsQuery&lt;T&gt;.presetA : PatBoundedStatsQuery&lt;StatPayloadA&gt;</c>).
///
/// Mirrors WeatherKit's
/// <c>DailyWeatherStatisticsQuery&lt;T&gt;.temperature : DailyWeatherStatisticsQuery&lt;DayTemperatureStatistics&gt;</c>
/// and sibling factories. Because the receiver's open <c>T</c> never appears in the
/// accessor's call or return surface, the Swift compiler can hard-code a single
/// concrete instantiation — the wrapper does not need to thread parent metadata,
/// PWTs, or self.
/// </summary>
internal static class ClosedStaticFactoryGate
{
    /// <summary>
    /// Returns true when the property+accessor match the closed-static-factory shape.
    /// Required conditions:
    /// <list type="number">
    ///   <item><description>Property is <c>static</c>.</description></item>
    ///   <item><description>Parent is generic.</description></item>
    ///   <item><description>Accessor signature has no value parameters (getter-only, no setter coupling here).</description></item>
    ///   <item><description>Return type is a bound generic of the same parent nominal type.</description></item>
    ///   <item><description>Return type is fully closed with respect to the parent's open generic parameters.</description></item>
    /// </list>
    /// </summary>
    public static bool IsClosedStaticFactoryAccessor(PropertyDecl property, MethodDecl accessorMethod)
    {
        if (!property.IsStatic)
            return false;
        return IsClosedStaticFactoryAccessor(accessorMethod);
    }

    /// <summary>
    /// Overload that operates on the PropertyDecl + its getter accessor.
    /// Returns false if no getter accessor exists, or if the property has a
    /// setter — settable static properties carry a value parameter on the
    /// setter accessor and fall outside this shape; admitting the property at
    /// the wrapper-eligibility layer would skip helper gates that the setter
    /// still depends on.
    /// </summary>
    public static bool IsClosedStaticFactoryAccessor(PropertyDecl property)
    {
        if (property.Accessors.OfType<SetAccessorDecl>().Any())
            return false;
        var getter = property.Accessors.OfType<GetAccessorDecl>().FirstOrDefault();
        if (getter == null)
            return false;
        return IsClosedStaticFactoryAccessor(property, getter.Method);
    }

    /// <summary>
    /// Accessor-only overload. The accessor's <c>MethodType == Static</c> carries the IsStatic
    /// signal, so a back-pointer to PropertyDecl is not required. Used by the C# call-site
    /// emission path which only sees the MethodDecl.
    /// </summary>
    public static bool IsClosedStaticFactoryAccessor(MethodDecl accessorMethod)
    {
        if (accessorMethod.MethodType != MethodType.Static)
            return false;

        // Restricted to StructDecl parents: the wrapper unconditionally emits the
        // IndirectResult `(_ resultPtr: UnsafeMutableRawPointer)` shape and writes the
        // closed instance via initializeMemory. Under @_cdecl, CdeclReturnMapping.Classify
        // routes all structs (frozen and non-frozen) to IndirectResult — Swift structs
        // can't cross the cdecl boundary by value, even when @frozen. Class returns
        // classify as ClassPointer (direct IntPtr return), and simple enums as SimpleEnum
        // (direct raw-value return). Because SameNominalAsParent forces return-kind ==
        // parent-kind, gating on StructDecl keeps the @_cdecl wrapper shape in lockstep
        // with the P/Invoke signature PInvokeEmitter emits.
        if (accessorMethod.ParentDecl is not StructDecl parentTypeDecl || !parentTypeDecl.IsGeneric)
            return false;

        // Getter only — no value parameters means CSSignature is [return] only.
        // CSSignature[0] is the return slot; any value parameter (setter newValue,
        // multi-input accessor, etc.) makes Count > 1 and falls outside the shape.
        if (accessorMethod.CSSignature.Count != 1)
            return false;

        var returnSpec = accessorMethod.CSSignature[0].SwiftTypeSpec;
        return IsClosedFactoryReturn(returnSpec, parentTypeDecl);
    }

    /// <summary>
    /// Checks that <paramref name="returnSpec"/> is a bound generic of the same nominal type
    /// as <paramref name="parentTypeDecl"/> and contains no references to the parent's open
    /// generic parameters.
    /// </summary>
    private static bool IsClosedFactoryReturn(TypeSpec returnSpec, TypeDecl parentTypeDecl)
    {
        if (returnSpec is not NamedTypeSpec namedReturn)
            return false;

        // Must be a bound generic (has generic arguments). A bare-T return doesn't qualify
        // because it carries the parent's open parameter directly.
        if (namedReturn.GenericParameters.Count == 0)
            return false;

        if (!SameNominalAsParent(namedReturn, parentTypeDecl))
            return false;

        // Fully closed: return spec cannot reference any of the parent's open generic params.
        var parentGenericParamNames = parentTypeDecl.GenericParameters
            .Select(p => p.TypeName)
            .ToHashSet();
        if (WrapperValidation.TypeSpecReferencesGenericParam(returnSpec, parentGenericParamNames))
            return false;

        return true;
    }

    /// <summary>
    /// Returns true when <paramref name="namedReturn"/> names the same nominal type as
    /// <paramref name="parentTypeDecl"/>. Matches the module-qualified name, falling back
    /// to the last name component when no module prefix is present.
    /// </summary>
    private static bool SameNominalAsParent(NamedTypeSpec namedReturn, TypeDecl parentTypeDecl)
    {
        var parentModuleQualified = parentTypeDecl.SwiftTypeName.ModuleQualifiedName;
        if (namedReturn.Name == parentModuleQualified)
            return true;

        // Allow short-name match when the type spec was parsed without a module prefix.
        var shortName = namedReturn.NameWithoutModule;
        return shortName == parentTypeDecl.SwiftTypeName.Name;
    }
}
