// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Projection for Swift class types rooted in an ObjC hierarchy (e.g., inherits from NSObject/CALayer).
/// Parameters use Handle-based marshalling (stackalloc temp buffer).
/// Returns use the same buffer + MarshalFromSwift pattern as ClassProjection
/// (ObjC-rooted NewFromPayload frees the buffer and wraps via NSObject).
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
        return new MarshalPlan
        {
            SetupStatements = new List<MarshalStatement>
            {
                new MarshalStatement.Line($"IntPtr* _{paramName}_ptr = stackalloc IntPtr[1];"),
                new MarshalStatement.Line($"*_{paramName}_ptr = {paramName}.Handle;"),
            },
            PInvokeExpression = $"(IntPtr)_{paramName}_ptr",
            RequiresUnsafe = true
        };
    }

    public MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy)
    {
        // Same pattern as ClassProjection: allocate buffer, store pointer, MarshalFromSwift.
        // ObjC-rooted NewFromPayload reads the pointer, frees the buffer, and wraps via NSObject.
        return new MarshalPlan
        {
            SetupStatements = new List<MarshalStatement>
            {
                new MarshalStatement.Line($"var classPayload = NativeMemory.Alloc((nuint)sizeof(IntPtr));"),
                new MarshalStatement.Block("try", new List<MarshalStatement>
                {
                    new MarshalStatement.Line($"*(IntPtr*)classPayload = {resultName};"),
                    new MarshalStatement.Line($"return ({_typeName})SwiftMarshal.MarshalFromSwift<{_typeName}>(new IntPtr(classPayload));"),
                }),
                new MarshalStatement.Block("catch", new List<MarshalStatement>
                {
                    new MarshalStatement.Line("NativeMemory.Free(classPayload);"),
                    new MarshalStatement.Line("throw;"),
                }),
            },
            RequiresUnsafe = true
        };
    }

    public bool RequiresSwiftWrapper => false;
    public string? GetSwiftWrapperCode(SwiftWrapperContext context) => null;

    public string? GetParameterElementConversion(string elementVar) =>
        $"{elementVar}.Handle";

    public string? GetReturnElementConversion(string elementVar) => null;
}
