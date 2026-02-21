// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Projection for Swift class types.
/// Swift classes return a pointer directly. The return path allocates native memory,
/// stores the pointer, and wraps via MarshalFromSwift. Parameters pass
/// Payload.DangerousGetHandle().
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
        return new MarshalPlan
        {
            PInvokeExpression = $"{paramName}.Payload.DangerousGetHandle()"
        };
    }

    public MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy)
    {
        // Swift classes return a pointer. We allocate native memory, store the pointer,
        // and wrap via MarshalFromSwift. The try/catch ensures NativeMemory.Free on failure.
        // The return is embedded inside the try block (PInvokeExpression is empty).
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
        $"{elementVar}.Payload.DangerousGetHandle()";

    /// <summary>
    /// No return element conversion needed. When used inside Optional, ToNullable() handles
    /// construction via ISwiftObject.NewFromPayload. Standalone returns use GetReturnPlan.
    /// </summary>
    public string? GetReturnElementConversion(string elementVar) => null;
}
