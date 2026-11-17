// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Projection for ObjC bridged types (UIImage, NSObject subclasses, etc.).
/// Parameter direction: extract .Handle from the .NET iOS binding object.
/// Return direction: adopt the +1 reference a Swift result carries (see
/// <see cref="MarshallingHelpers.ReturnedObjectPointer"/>).
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
                new MarshalStatement.Line($"var {paramName}Handle = {LiveHandleExpressions.ObjCArgument(paramName, paramName)};")
            },
            PInvokeExpression = $"{paramName}Handle"
        };
    }

    public MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy)
    {
        return new MarshalPlan
        {
            PInvokeExpression = MarshallingHelpers.FormatObjCBridgeCall(
                _csharpTypeName, MarshallingHelpers.ReturnedObjectPointer(resultName, strategy), nonNull: true, ownsReference: true),
            RequiresUnsafe = strategy == ReturnStrategy.IndirectResult
        };
    }

    public bool RequiresSwiftWrapper => false;
    public string? GetSwiftWrapperCode(SwiftWrapperContext context) => null;

    public string? GetParameterElementConversion(string elementVar) => LiveHandleExpressions.ObjCSelf(elementVar);
    public string? GetReturnElementConversion(string elementVar) => MarshallingHelpers.FormatObjCBridgeCall(_csharpTypeName, elementVar, nonNull: true);

    public T Accept<T>(IProjectionVisitor<T> visitor) => visitor.Visit(this);
}
