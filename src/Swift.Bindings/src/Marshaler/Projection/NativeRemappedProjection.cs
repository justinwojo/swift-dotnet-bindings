// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Projection for native-remapped types (URL ↔ NSUrl, Data ↔ NSData).
/// Handles both frozen (Data → SwiftData, value type) and non-frozen (URL → SafeHandle) variants.
/// </summary>
public class NativeRemappedProjection : ITypeProjection
{
    private readonly string _publicType;
    private readonly string _swiftWrapperType;
    private readonly bool _isFrozen;

    /// <summary>
    /// Creates a native remapped projection.
    /// </summary>
    /// <param name="publicType">The .NET type name (e.g., "NSUrl", "NSData").</param>
    /// <param name="swiftWrapperType">The Swift wrapper type (e.g., "SwiftURL", "SwiftData").</param>
    /// <param name="isFrozen">Whether the Swift type is frozen (affects SafeHandle vs value semantics).</param>
    public NativeRemappedProjection(string publicType, string swiftWrapperType, bool isFrozen)
    {
        _publicType = publicType;
        _swiftWrapperType = swiftWrapperType;
        _isFrozen = isFrozen;
    }

    public string PublicType => _publicType;
    public string PInvokeType => _isFrozen ? _swiftWrapperType : "SafeHandle";
    public string? PInvokeAttribute => null;

    public MarshalPlan GetParameterPlan(string paramName)
    {
        if (_isFrozen)
        {
            return new MarshalPlan
            {
                SetupStatements = new List<MarshalStatement>
                {
                    new MarshalStatement.Using(_swiftWrapperType, $"{paramName}Swift", $"new {_swiftWrapperType}({paramName})")
                },
                PInvokeExpression = $"{paramName}Swift",
                UsingDeclarations = new List<(string, string)>
                {
                    (_swiftWrapperType, $"{paramName}Swift")
                }
            };
        }
        else
        {
            return new MarshalPlan
            {
                SetupStatements = new List<MarshalStatement>
                {
                    new MarshalStatement.Using(_swiftWrapperType, $"{paramName}Swift", $"new {_swiftWrapperType}({paramName})")
                },
                PInvokeExpression = $"{paramName}Swift.Payload",
                UsingDeclarations = new List<(string, string)>
                {
                    (_swiftWrapperType, $"{paramName}Swift")
                }
            };
        }
    }

    public MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy)
    {
        return new MarshalPlan
        {
            PInvokeExpression = $"new {_swiftWrapperType}({resultName}).To{_publicType}()"
        };
    }

    public bool RequiresSwiftWrapper => false;
    public string? GetSwiftWrapperCode(SwiftWrapperContext context) => null;

    public string? GetParameterElementConversion(string elementVar) => $"new {_swiftWrapperType}({elementVar})";
    public string? GetReturnElementConversion(string elementVar) => $"new {_swiftWrapperType}({elementVar}).To{_publicType}()";
    public bool ElementRequiresDisposal => true;
}
