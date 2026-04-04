// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Projection for non-frozen structs, classes (ClassWithOpaquePayload), and complex enums.
/// Parameter direction: extract .Payload.DangerousGetHandle() from SafeHandle.
/// Return direction: construct from IntPtr handle, or MarshalFromSwift for complex enums.
/// </summary>
public class NonFrozenStructProjection : ITypeProjection
{
    private readonly string _typeName;
    private readonly bool _useMarshalFromSwift;

    /// <summary>
    /// Creates a non-frozen struct projection.
    /// </summary>
    /// <param name="typeName">The C# type name.</param>
    /// <param name="useMarshalFromSwift">When true, uses (T)MarshalFromSwift&lt;T&gt;(result) for returns
    /// instead of new T(result). Used for complex enums with SafeHandle-based opaque payloads.</param>
    public NonFrozenStructProjection(string typeName, bool useMarshalFromSwift = false)
    {
        _typeName = typeName;
        _useMarshalFromSwift = useMarshalFromSwift;
    }

    public string PublicType => _typeName;
    public string PInvokeType => "IntPtr";
    public string? PInvokeAttribute => null;

    /// <summary>
    /// For MarshalFromSwift, use the type name (not IntPtr). MarshalFromSwift needs the real
    /// type to construct instances via ISwiftObject.NewFromPayload.
    /// </summary>
    public string MarshalFromSwiftType => _typeName;

    public MarshalPlan GetParameterPlan(string paramName)
    {
        return new MarshalPlan
        {
            PInvokeExpression = $"{paramName}.Payload.DangerousGetHandle()"
        };
    }

    public MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy)
    {
        // All non-frozen struct returns use MarshalFromSwift — the constructor taking
        // SwiftHandle/IntPtr is private. MarshalFromSwift goes through ISwiftObject.NewFromPayload.
        // Complex enums (_useMarshalFromSwift) need an explicit cast to the type.
        if (_useMarshalFromSwift)
        {
            return new MarshalPlan
            {
                PInvokeExpression = $"({_typeName})SwiftMarshal.MarshalFromSwiftObject<{_typeName}>({resultName})"
            };
        }

        return new MarshalPlan
        {
            PInvokeExpression = $"SwiftMarshal.MarshalFromSwiftObject<{_typeName}>({resultName})"
        };
    }

    public bool RequiresSwiftWrapper => false;
    public string? GetSwiftWrapperCode(SwiftWrapperContext context) => null;

    public string? GetParameterElementConversion(string elementVar) => $"{elementVar}.Payload.DangerousGetHandle()";

    /// <summary>
    /// No return element conversion needed. When used inside Optional, ToNullable() handles
    /// construction via ISwiftObject.NewFromPayload. When used standalone, GetReturnPlan handles it.
    /// </summary>
    public string? GetReturnElementConversion(string elementVar) => null;

    public T Accept<T>(IProjectionVisitor<T> visitor) => visitor.Visit(this);
}
