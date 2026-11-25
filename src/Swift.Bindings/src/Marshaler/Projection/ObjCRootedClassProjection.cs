// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Projection for Swift class types rooted in an ObjC hierarchy (e.g., inherits from NSObject/CALayer).
/// Parameters use Handle-based marshalling (stackalloc temp buffer).
/// Returns pass the raw pointer directly to MarshalFromSwift (same as ClassProjection).
/// </summary>
public class ObjCRootedClassProjection : ITypeProjection
{
    private readonly string _typeName;
    private readonly bool _isCollectionSlot;

    public ObjCRootedClassProjection(string typeName)
        : this(typeName, isCollectionSlot: false)
    {
    }

    private ObjCRootedClassProjection(string typeName, bool isCollectionSlot)
    {
        _typeName = typeName;
        _isCollectionSlot = isCollectionSlot;
    }

    /// <summary>
    /// The same projection as a collection's element, key or value (see
    /// <see cref="SwiftContainerGenericType"/>).
    /// </summary>
    public ObjCRootedClassProjection AsCollectionSlot() => new(_typeName, isCollectionSlot: true);

    public string PublicType => _typeName;
    public string PInvokeType => "IntPtr";
    public string? PInvokeAttribute => null;
    public string MarshalFromSwiftType => _typeName;

    /// <summary>
    /// In a collection the element is the class itself, as for a pure Swift class
    /// (<see cref="ClassProjection.SwiftContainerGenericType"/>): copied in through the class's value
    /// witness (+1), so the storage is the class's own and holds a reference on every element, and a
    /// key or set element is hashed through the class's own <c>Hashable</c> witness table.
    /// </summary>
    public string SwiftContainerGenericType => _isCollectionSlot ? _typeName : PInvokeType;

    public MarshalPlan GetParameterPlan(string paramName)
    {
        // ObjC-rooted classes use Handle (the NSObject pointer, which IS the Swift object pointer).
        // Swift wrappers expect a buffer address for non-self class params, so create call-scoped temp.
        // Strip verbatim prefix for compound variable names: @in → in, so _{bareName}_ptr = _in_ptr (valid),
        // not _@in_ptr (invalid — @ is only valid at the START of a C# identifier).
        var bareName = NameProvider.StripVerbatimPrefix(paramName);
        return new MarshalPlan
        {
            SetupStatements = new List<MarshalStatement>
            {
                new MarshalStatement.Line($"IntPtr* _{bareName}_ptr = stackalloc IntPtr[1];"),
                new MarshalStatement.Line($"*_{bareName}_ptr = {LiveHandleExpressions.ObjCArgument(paramName, paramName)};"),
            },
            PInvokeExpression = $"(IntPtr)_{bareName}_ptr",
            RequiresUnsafe = true
        };
    }

    public MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy)
    {
        // ObjC-rooted classes return a pointer directly, same as pure Swift classes.
        // Pass to MarshalFromSwift which calls NewFromPayload to wrap via NSObject.
        return new MarshalPlan
        {
            PInvokeExpression = $"({_typeName})SwiftMarshal.MarshalFromSwiftObject<{_typeName}>({resultName})"
        };
    }

    public bool RequiresSwiftWrapper => false;
    public string? GetSwiftWrapperCode(SwiftWrapperContext context) => null;

    // Cast to IntPtr: ObjC classes return ObjCRuntime.NativeHandle from .Handle. A container
    // carries the wrapper itself (SwiftContainerGenericType), so its element conversion is skipped.
    public string? GetParameterElementConversion(string elementVar) =>
        LiveHandleExpressions.ObjCSelf(elementVar);

    public string? GetReturnElementConversion(string elementVar) => null;

    public T Accept<T>(IProjectionVisitor<T> visitor) => visitor.Visit(this);
}
