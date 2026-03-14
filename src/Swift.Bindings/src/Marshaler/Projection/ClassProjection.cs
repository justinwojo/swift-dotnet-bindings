// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Projection for Swift class types.
/// Swift classes use SwiftClassHandle (direct ARC-bridged SafeHandle). Returns use
/// MarshalFromSwift to wrap the pointer directly. Parameters pass
/// _handle.DangerousGetHandle() (the handle IS the Swift object pointer).
/// </summary>
public class ClassProjection : ITypeProjection
{
    private readonly string _typeName;

    public ClassProjection(string typeName)
    {
        _typeName = typeName;
    }

    public string PublicType => _typeName;
    public string PInvokeType => "IntPtr";
    public string? PInvokeAttribute => null;

    /// <summary>
    /// For MarshalFromSwift, use the class name (not IntPtr). MarshalFromSwift needs the real
    /// type to construct instances via ISwiftObject.NewFromPayload.
    /// </summary>
    public string MarshalFromSwiftType => _typeName;

    public MarshalPlan GetParameterPlan(string paramName)
    {
        // SwiftClassHandle: DangerousGetHandle() IS the Swift object pointer (no buffer).
        // Payload property returns the SwiftClassHandle, DangerousGetHandle() extracts the IntPtr.
        return new MarshalPlan
        {
            PInvokeExpression = $"{paramName}.Payload.DangerousGetHandle()"
        };
    }

    public MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy)
    {
        // Swift classes return a pointer directly. Pass to MarshalFromSwift which calls
        // NewFromPayload to create a SwiftClassHandle. No buffer allocation needed.
        return new MarshalPlan
        {
            PInvokeExpression = $"({_typeName})SwiftMarshal.MarshalFromSwift<{_typeName}>({resultName})"
        };
    }

    public bool RequiresSwiftWrapper => false;
    public string? GetSwiftWrapperCode(SwiftWrapperContext context) => null;

    // SwiftClassHandle: Payload property returns SwiftClassHandle,
    // DangerousGetHandle() extracts the Swift object pointer directly.
    public string? GetParameterElementConversion(string elementVar) =>
        $"{elementVar}.Payload.DangerousGetHandle()";

    /// <summary>
    /// No return element conversion needed. When used inside Optional, ToNullable() handles
    /// construction via ISwiftObject.NewFromPayload. Standalone returns use GetReturnPlan.
    /// </summary>
    public string? GetReturnElementConversion(string elementVar) => null;

    public T Accept<T>(IProjectionVisitor<T> visitor) => visitor.Visit(this);
}
