// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Projection for simple C# enums backed by an integer underlying type.
/// Parameter direction: cast enum to underlying type.
/// Return direction: cast underlying type to enum.
/// </summary>
public class SimpleEnumProjection : ITypeProjection
{
    private readonly string _enumTypeName;
    private readonly string _underlyingType;

    public SimpleEnumProjection(string enumTypeName, string underlyingType)
    {
        _enumTypeName = enumTypeName;
        _underlyingType = underlyingType;
    }

    public string PublicType => _enumTypeName;
    public string PInvokeType => _underlyingType;
    public string? PInvokeAttribute => null;

    public MarshalPlan GetParameterPlan(string paramName)
    {
        return new MarshalPlan
        {
            PInvokeExpression = $"({_underlyingType}){paramName}"
        };
    }

    public MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy)
    {
        return new MarshalPlan
        {
            PInvokeExpression = $"({_enumTypeName}){resultName}"
        };
    }

    // Enums are blittable — SwiftArray<MyEnum> uses the enum name, not the underlying type
    public string SwiftContainerGenericType => _enumTypeName;

    public bool RequiresSwiftWrapper => false;
    public string? GetSwiftWrapperCode(SwiftWrapperContext context) => null;

    // Enums are blittable — no element conversion needed inside containers.
    // SwiftArray<MyEnum> / SwiftDictionary<MyEnum,...> work with enum values directly.
    // Standalone parameter/return plans handle the cast to/from underlying type.
    public string? GetParameterElementConversion(string elementVar) => null;
    public string? GetReturnElementConversion(string elementVar) => null;
}
