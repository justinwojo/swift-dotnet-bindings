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

    public ObjCRootedClassProjection(string typeName)
    {
        _typeName = typeName;
    }

    public string PublicType => _typeName;
    public string PInvokeType => "IntPtr";
    public string? PInvokeAttribute => null;
    public string MarshalFromSwiftType => _typeName;

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
                new MarshalStatement.Line($"*_{bareName}_ptr = {paramName}.Handle;"),
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

    // Cast to IntPtr: ObjC classes return ObjCRuntime.NativeHandle from .Handle,
    // but SwiftArray<IntPtr>/container projections need IntPtr.
    public string? GetParameterElementConversion(string elementVar) =>
        $"(IntPtr){elementVar}.Handle";

    public string? GetReturnElementConversion(string elementVar) => null;

    public T Accept<T>(IProjectionVisitor<T> visitor) => visitor.Visit(this);
}
