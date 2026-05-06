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

    /// <summary>
    /// When this projection is the inner element of a Swift container
    /// (SwiftArray, SwiftDictionary, SwiftSet, SwiftOptional), the container's
    /// per-element storage holds the struct's payload bytes by value — *not* a
    /// pointer. So the generic type parameter must be the C# wrapper type
    /// (which implements ISwiftObject and routes through MarshalToSwift /
    /// VWT.InitializeWithCopy for per-element marshalling), not IntPtr.
    /// Using IntPtr produces SwiftArray&lt;IntPtr&gt; whose 1-word-per-slot
    /// storage cannot represent the inline TStruct layout the Swift side
    /// expects via assumingMemoryBound(to: Array&lt;TStruct&gt;.self).
    /// </summary>
    public string SwiftContainerGenericType => _typeName;

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

    /// <summary>
    /// Returns <c>e.Payload.DangerousGetHandle()</c> — extracts the struct's payload
    /// pointer as IntPtr. Used by:
    ///  - <see cref="ClosureProjection"/> when invoking a Swift-supplied closure with a
    ///    NonFrozenStruct C# arg: the function pointer expects IntPtr (1-word handle),
    ///    so the lambda body passes <c>arg.Payload.DangerousGetHandle()</c>.
    ///  - ProtocolProxyEmitter / AccessorConversionVisitors paths that bridge between
    ///    a typed C# wrapper and an IntPtr-typed Swift entry point.
    ///
    /// NOTE: Swift-container projections (<see cref="ArrayProjection"/>,
    /// <see cref="DictionaryProjection"/>, <see cref="SetProjection"/>, and
    /// <see cref="OptionalProjection"/>'s container-element path) detect
    /// "<c>SwiftContainerGenericType == PublicType</c>" and SKIP this per-element
    /// conversion — for <c>SwiftArray&lt;TStruct&gt;</c> they want the typed wrapper
    /// directly so <c>ISwiftObject.MarshalToSwift</c> copies the struct payload bytes
    /// by value into the contiguous Swift array slot.
    /// </summary>
    public string? GetParameterElementConversion(string elementVar) =>
        $"{elementVar}.Payload.DangerousGetHandle()";

    /// <summary>
    /// No return element conversion needed. When used inside Optional, ToNullable() handles
    /// construction via ISwiftObject.NewFromPayload. When used standalone, GetReturnPlan handles it.
    /// </summary>
    public string? GetReturnElementConversion(string elementVar) => null;

    public T Accept<T>(IProjectionVisitor<T> visitor) => visitor.Visit(this);
}
