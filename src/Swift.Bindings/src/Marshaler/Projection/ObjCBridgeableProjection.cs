// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Projection for ObjC-bridgeable Swift value types (e.g., Foundation.URL ↔ NSURL).
/// These types freely bridge to ObjC classes via _ObjectiveCBridgeable and cross
/// the @_cdecl boundary as ObjC object pointers (IntPtr), not Swift struct bytes.
///
/// Parameter direction: extract .Handle from the .NET iOS binding object (e.g., NSUrl.Handle).
/// Return direction: wrap IntPtr with GetNSObject&lt;T&gt;() (same as ObjCBridgedProjection).
///
/// Distinct from ObjCBridgedProjection (which handles ObjC class wrappers like UIImage)
/// and NativeRemappedProjection (which handles Swift wrapper ↔ .NET native type conversion).
/// </summary>
public class ObjCBridgeableProjection : ITypeProjection
{
    private readonly string _csharpTypeName;

    public ObjCBridgeableProjection(string csharpTypeName)
    {
        _csharpTypeName = csharpTypeName;
    }

    public string PublicType => _csharpTypeName;
    public string PInvokeType => "IntPtr";
    public string? PInvokeAttribute => null;

    /// <summary>
    /// Signals container projections that this element type should use whole-container
    /// ObjC bridge (NSArray, NSDictionary, NSSet) instead of SwiftArray&lt;T&gt; pipeline.
    /// </summary>
    public bool UsesObjCContainerBridge => true;

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
            PInvokeExpression = MarshallingHelpers.FormatObjCBridgeCall(_csharpTypeName, resultName, nonNull: true)
        };
    }

    public bool RequiresSwiftWrapper => false;
    public string? GetSwiftWrapperCode(SwiftWrapperContext context) => null;

    public string? GetParameterElementConversion(string elementVar) => $"{elementVar}.Handle";
    public string? GetReturnElementConversion(string elementVar) => MarshallingHelpers.FormatObjCBridgeCall(_csharpTypeName, elementVar, nonNull: true);
    public bool ElementRequiresDisposal => false;

    public T Accept<T>(IProjectionVisitor<T> visitor) => visitor.Visit(this);
}
