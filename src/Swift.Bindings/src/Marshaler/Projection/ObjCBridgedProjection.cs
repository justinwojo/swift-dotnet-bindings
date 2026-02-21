// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Projection for ObjC bridged types (UIImage, NSObject subclasses, etc.).
/// Parameter direction: extract .Handle from the .NET iOS binding object.
/// Return direction: wrap IntPtr with GetNSObject&lt;T&gt;().
/// </summary>
public class ObjCBridgedProjection : ITypeProjection
{
    private readonly string _csharpTypeName;

    public ObjCBridgedProjection(string csharpTypeName)
    {
        _csharpTypeName = csharpTypeName;
    }

    public string PublicType => _csharpTypeName;
    public string PInvokeType => "IntPtr";
    public string? PInvokeAttribute => null;

    public MarshalPlan GetParameterPlan(string paramName)
    {
        return new MarshalPlan
        {
            SetupStatements = new List<MarshalStatement>
            {
                new MarshalStatement.Line($"var {paramName}Handle = {paramName}.Handle;")
            },
            PInvokeExpression = $"{paramName}Handle"
        };
    }

    public MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy)
    {
        return new MarshalPlan
        {
            PInvokeExpression = $"ObjCRuntime.Runtime.GetNSObject<{_csharpTypeName}>({resultName})!"
        };
    }

    public bool RequiresSwiftWrapper => false;
    public string? GetSwiftWrapperCode(SwiftWrapperContext context) => null;

    public string? GetParameterElementConversion(string elementVar) => $"{elementVar}.Handle";
    public string? GetReturnElementConversion(string elementVar) => $"ObjCRuntime.Runtime.GetNSObject<{_csharpTypeName}>({elementVar})!";
}
