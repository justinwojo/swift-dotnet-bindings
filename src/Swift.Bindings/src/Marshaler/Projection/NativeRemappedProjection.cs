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
    private readonly string? _fromFactoryMethod;
    private readonly string _toConversionMethod;

    /// <summary>
    /// Creates a native remapped projection.
    /// </summary>
    /// <param name="publicType">The .NET type name (e.g., "NSUrl", "NSData").</param>
    /// <param name="swiftWrapperType">The Swift wrapper type (e.g., "Swift.URL", "Swift.Data").</param>
    /// <param name="isFrozen">Whether the Swift type is frozen (affects SafeHandle vs value semantics).</param>
    /// <param name="fromFactoryMethod">Factory method for parameter conversion (e.g., "FromNSUrl"). If null, uses constructor.</param>
    /// <param name="toConversionMethod">Method for return conversion (e.g., "ToNSUrl"). Required — caller must derive from the native type name.</param>
    public NativeRemappedProjection(string publicType, string swiftWrapperType, bool isFrozen,
        string toConversionMethod, string? fromFactoryMethod = null)
    {
        _publicType = publicType;
        _swiftWrapperType = swiftWrapperType;
        _isFrozen = isFrozen;
        _fromFactoryMethod = fromFactoryMethod;
        _toConversionMethod = toConversionMethod;
    }

    public string PublicType => _publicType;
    public string PInvokeType => _isFrozen ? _swiftWrapperType : "SafeHandle";
    public string? PInvokeAttribute => null;

    public MarshalPlan GetParameterPlan(string paramName)
    {
        var initExpr = _fromFactoryMethod != null
            ? $"{_swiftWrapperType}.{_fromFactoryMethod}({paramName})"
            : $"new {_swiftWrapperType}({paramName})";

        var usingStmt = _isFrozen
            ? new MarshalStatement.Line($"var {paramName}Swift = {initExpr};")
            : (MarshalStatement)new MarshalStatement.Using(_swiftWrapperType, $"{paramName}Swift", initExpr);

        return new MarshalPlan
        {
            SetupStatements = new List<MarshalStatement> { usingStmt },
            PInvokeExpression = _isFrozen ? $"{paramName}Swift" : $"{paramName}Swift.Payload"
        };
    }

    public MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy)
    {
        if (_isFrozen && strategy == ReturnStrategy.Direct)
        {
            // Frozen types (Data) return by value from P/Invoke — result IS the Swift type.
            // Just call the conversion method directly on it.
            return new MarshalPlan
            {
                PInvokeExpression = $"{resultName}.{_toConversionMethod}()"
            };
        }

        if (strategy == ReturnStrategy.IndirectResult)
        {
            // Non-frozen (URL) via indirect result — marshal from pointer first
            return new MarshalPlan
            {
                PInvokeExpression = $"(({_swiftWrapperType})SwiftMarshal.MarshalFromSwift<{_swiftWrapperType}>({resultName})).{_toConversionMethod}()"
            };
        }

        // Non-frozen direct return or other strategies — construct from IntPtr
        return new MarshalPlan
        {
            PInvokeExpression = $"new {_swiftWrapperType}({resultName}).{_toConversionMethod}()"
        };
    }

    public bool RequiresSwiftWrapper => false;
    public string? GetSwiftWrapperCode(SwiftWrapperContext context) => null;

    public string? GetParameterElementConversion(string elementVar) =>
        _fromFactoryMethod != null
            ? $"{_swiftWrapperType}.{_fromFactoryMethod}({elementVar})"
            : $"new {_swiftWrapperType}({elementVar})";
    public string? GetReturnElementConversion(string elementVar)
    {
        return $"new {_swiftWrapperType}({elementVar}).{_toConversionMethod}()";
    }
    public bool ElementRequiresDisposal => true;
}
