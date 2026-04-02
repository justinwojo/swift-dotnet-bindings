// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Projection for Swift.Result&lt;Success, Failure&gt; ↔ C# SwiftResult&lt;TSuccess, TFailure&gt;.
/// Composes with inner projections for the success and failure types.
///
/// Parameter direction: extract PayloadBuffer from SwiftResult, pass as IntPtr.
/// Return direction: MarshalFromSwift&lt;SwiftResult&lt;S, F&gt;&gt;(resultPtr) → SwiftResult instance.
///
/// Result uses the same UnsafeRawPointer transport as other generic containers (Array, Dictionary, Set).
/// The @_cdecl wrapper writes Result to resultPtr via initializeMemory(as:repeating:count:)
/// and reads it via assumingMemoryBound(to:).pointee.
/// </summary>
public class ResultProjection : ITypeProjection
{
    private readonly ITypeProjection _successProjection;
    private readonly ITypeProjection _failureProjection;

    public ResultProjection(ITypeProjection successProjection, ITypeProjection failureProjection)
    {
        _successProjection = successProjection;
        _failureProjection = failureProjection;
    }

    /// <summary>The inner projection for the success type.</summary>
    public ITypeProjection SuccessProjection => _successProjection;

    /// <summary>The inner projection for the failure type.</summary>
    public ITypeProjection FailureProjection => _failureProjection;

    public string PublicType => $"SwiftResult<{_successProjection.MarshalFromSwiftType}, {_failureProjection.MarshalFromSwiftType}>";
    public string PInvokeType => "IntPtr";
    public string? PInvokeAttribute => null;

    /// <summary>
    /// Container type for SwiftResult — uses MarshalFromSwiftType for inner types
    /// so that classes/non-frozen structs use their public names.
    /// </summary>
    public string ContainerTypeName => $"SwiftResult<{_successProjection.MarshalFromSwiftType}, {_failureProjection.MarshalFromSwiftType}>";

    /// <summary>
    /// For MarshalFromSwift calls, use the full SwiftResult type with MarshalFromSwift inner types.
    /// </summary>
    public string MarshalFromSwiftType => ContainerTypeName;

    /// <summary>
    /// When Result appears as a generic parameter inside another container,
    /// use the full SwiftResult type name.
    /// </summary>
    public string SwiftContainerGenericType => $"SwiftResult<{_successProjection.SwiftContainerGenericType}, {_failureProjection.SwiftContainerGenericType}>";

    public MarshalPlan GetParameterPlan(string paramName)
    {
        // Result<T,E> in parameter direction is not yet supported.
        // SwiftResult.FromSuccess/FromFailure creates C#-only instances with no native
        // payload, and PayloadBuffer throws for those objects. Outbound Result arguments
        // would need native payload synthesis, which is not implemented.
        throw new NotSupportedException(
            $"Result<T,E> is not supported in parameter direction. " +
            $"Parameter '{paramName}' cannot be marshalled to Swift.");
    }

    public MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy)
    {
        var resultType = ContainerTypeName;
        var marshalFromSwift = $"SwiftMarshal.MarshalFromSwift<{resultType}>";

        return strategy switch
        {
            ReturnStrategy.Direct => new MarshalPlan
            {
                SetupStatements = new List<MarshalStatement>
                {
                    new MarshalStatement.Line(
                        $"var _swiftResult = {marshalFromSwift}(new IntPtr(&{resultName}));")
                },
                PInvokeExpression = $"_swiftResult",
                RequiresUnsafe = true
            },
            ReturnStrategy.IndirectResult or ReturnStrategy.OutBuffer => new MarshalPlan
            {
                SetupStatements = new List<MarshalStatement>
                {
                    new MarshalStatement.Line(
                        $"var _swiftResult = {marshalFromSwift}({resultName});")
                },
                PInvokeExpression = $"_swiftResult"
            },
            ReturnStrategy.AsyncCallback => new MarshalPlan
            {
                SetupStatements = new List<MarshalStatement>
                {
                    new MarshalStatement.Line(
                        $"var _swiftResult = {marshalFromSwift}({resultName});")
                },
                PInvokeExpression = $"_swiftResult"
            },
            _ => MarshalPlan.PassThrough(resultName)
        };
    }

    public bool RequiresSwiftWrapper => false;
    public string? GetSwiftWrapperCode(SwiftWrapperContext context) => null;

    public T Accept<T>(IProjectionVisitor<T> visitor) => visitor.Visit(this);
}
