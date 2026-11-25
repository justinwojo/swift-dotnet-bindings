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
    private readonly bool _isCollectionSlot;

    public ClassProjection(string typeName)
        : this(typeName, isCollectionSlot: false)
    {
    }

    private ClassProjection(string typeName, bool isCollectionSlot)
    {
        _typeName = typeName;
        _isCollectionSlot = isCollectionSlot;
    }

    /// <summary>
    /// The same projection as a collection's element, key or value: an array element, a dictionary
    /// key or value, or a set element (see <see cref="SwiftContainerGenericType"/>).
    /// </summary>
    public ClassProjection AsCollectionSlot() => new(_typeName, isCollectionSlot: true);

    public string PublicType => _typeName;
    public string PInvokeType => "IntPtr";
    public string? PInvokeAttribute => null;

    /// <summary>
    /// For MarshalFromSwift, use the class name (not IntPtr). MarshalFromSwift needs the real
    /// type to construct instances via ISwiftObject.NewFromPayload.
    /// </summary>
    public string MarshalFromSwiftType => _typeName;

    /// <summary>
    /// In a collection the element is the class itself, not its bare pointer: a
    /// <c>SwiftArray&lt;IntPtr&gt;</c> is an array of <c>Swift.Int</c>, whose storage holds no reference on
    /// its elements while Swift, reading it as the class's array, releases what it replaces or
    /// destroys; a <c>SwiftSet&lt;IntPtr&gt;</c> or a dictionary keyed by <c>IntPtr</c> also hashes and
    /// compares by address where Swift uses the class's own <c>Hashable</c> conformance. Carried as the
    /// wrapper, each element is copied in through the class's value witness (+1), and a key or set
    /// element is hashed through the class's <c>Hashable</c> witness table, which the module
    /// initializer registers for the wrapper (<see cref="HashedSwiftClass"/>).
    /// </summary>
    public string SwiftContainerGenericType => _isCollectionSlot ? _typeName : PInvokeType;

    public MarshalPlan GetParameterPlan(string paramName)
    {
        // SwiftClassHandle: DangerousGetHandle() IS the Swift object pointer (no buffer).
        // Payload property returns the SwiftClassHandle, DangerousGetHandle() extracts the IntPtr.
        return new MarshalPlan
        {
            PInvokeExpression = LiveHandleExpressions.SwiftObjectArgument(paramName, paramName)
        };
    }

    public MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy)
    {
        // Swift classes return a pointer directly. Pass to MarshalFromSwift which calls
        // NewFromPayload to create a SwiftClassHandle. No buffer allocation needed.
        return new MarshalPlan
        {
            PInvokeExpression = $"({_typeName})SwiftMarshal.MarshalFromSwiftObject<{_typeName}>({resultName})"
        };
    }

    public bool RequiresSwiftWrapper => false;
    public string? GetSwiftWrapperCode(SwiftWrapperContext context) => null;

    // SwiftClassHandle: Payload property returns SwiftClassHandle,
    // DangerousGetHandle() extracts the Swift object pointer directly.
    public string? GetParameterElementConversion(string elementVar) =>
        LiveHandleExpressions.SwiftObject(elementVar);

    /// <summary>
    /// No return element conversion needed. When used inside Optional, ToNullable() handles
    /// construction via ISwiftObject.NewFromPayload. Standalone returns use GetReturnPlan.
    /// </summary>
    public string? GetReturnElementConversion(string elementVar) => null;

    public T Accept<T>(IProjectionVisitor<T> visitor) => visitor.Visit(this);
}
