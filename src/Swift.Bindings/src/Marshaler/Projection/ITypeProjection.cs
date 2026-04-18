// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Strategy for how a return value is received from Swift.
/// </summary>
public enum ReturnStrategy
{
    /// <summary>The value is returned directly in a register.</summary>
    Direct,

    /// <summary>The value is returned via SwiftIndirectResult (large structs).</summary>
    IndirectResult,

    /// <summary>The value is returned via an out-buffer pointer (large optionals).</summary>
    OutBuffer,

    /// <summary>The value is returned via an async callback.</summary>
    AsyncCallback
}

/// <summary>
/// Defines how a single Swift type is projected to C# for P/Invoke interop.
/// Each projection knows its public C# type, its P/Invoke type, and how to
/// marshal between them in both parameter and return directions.
/// </summary>
public interface ITypeProjection
{
    /// <summary>The C# type seen by the consumer (e.g., "string", "IReadOnlyList&lt;int&gt;").</summary>
    string PublicType { get; }

    /// <summary>The C# type used in the P/Invoke declaration (e.g., "SwiftString", "IntPtr").</summary>
    string PInvokeType { get; }

    /// <summary>Optional attribute for the P/Invoke parameter (e.g., "[MarshalAs(UnmanagedType.U1)]").</summary>
    string? PInvokeAttribute { get; }

    /// <summary>
    /// Produces a plan for marshalling this type as a parameter (C# → Swift direction).
    /// </summary>
    /// <param name="paramName">The C# parameter name.</param>
    /// <returns>A marshal plan with setup, expression, and cleanup.</returns>
    MarshalPlan GetParameterPlan(string paramName);

    /// <summary>
    /// Produces a plan for marshalling this type as a return value (Swift → C# direction).
    /// </summary>
    /// <param name="resultName">The variable name for the raw P/Invoke result.</param>
    /// <param name="strategy">How the return value is received from Swift.</param>
    /// <returns>A marshal plan with setup, expression, and cleanup.</returns>
    MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy);

    /// <summary>Whether this projection requires a Swift wrapper function.</summary>
    bool RequiresSwiftWrapper { get; }

    /// <summary>
    /// Gets the Swift wrapper code if needed, or null if no wrapper is required.
    /// </summary>
    /// <param name="context">Context for generating the Swift wrapper.</param>
    /// <returns>Swift source code for the wrapper, or null.</returns>
    string? GetSwiftWrapperCode(SwiftWrapperContext context);

    /// <summary>
    /// Expression to convert a public-type element to P/Invoke type in a Select() lambda.
    /// Null means no conversion needed (passthrough).
    /// Used by container projections (Array, Dictionary, Optional) for element-wise composition.
    /// </summary>
    string? GetParameterElementConversion(string elementVar) => null;

    /// <summary>
    /// Expression to convert a P/Invoke element back to public type in a Select() lambda.
    /// Null means no conversion needed (passthrough).
    /// </summary>
    string? GetReturnElementConversion(string elementVar) => null;

    /// <summary>
    /// Whether elements produced by GetParameterElementConversion require disposal.
    /// When true, container projections emit disposal code in finally blocks.
    /// </summary>
    bool ElementRequiresDisposal => false;

    /// <summary>
    /// Optional callback/static-field declarations needed alongside the main method.
    /// Used by closures (callback thunk + function pointer field) and async (success/error callbacks).
    /// </summary>
    IReadOnlyList<CallbackDeclaration> CallbackDeclarations => Array.Empty<CallbackDeclaration>();

    /// <summary>
    /// The C# type to use when this projection appears as a generic parameter inside a Swift
    /// container (SwiftArray&lt;T&gt;, SwiftDictionary&lt;K,V&gt;, SwiftOptional&lt;T&gt;).
    /// For most projections this equals PInvokeType. Overridden by:
    /// - SimpleEnumProjection: returns enum PublicType (enums are blittable, generic param uses enum name)
    /// - StringProjection: returns "SwiftString" (PInvokeType is SwiftString.Buffer for lowered calls)
    /// - Container projections: returns ContainerTypeName (e.g., SwiftArray&lt;SwiftString&gt;)
    /// </summary>
    string SwiftContainerGenericType => PInvokeType;

    /// <summary>
    /// The C# runtime container type name for intermediate marshalling.
    /// For container projections (Array → SwiftArray&lt;T&gt;, Dictionary → SwiftDictionary&lt;K,V&gt;),
    /// this is the full container type used before PayloadBuffer extraction.
    /// For all others, defaults to PInvokeType.
    /// Used by OptionalProjection to construct SwiftOptional&lt;ContainerType&gt;.
    /// </summary>
    string ContainerTypeName => PInvokeType;

    /// <summary>
    /// The C# type to use in MarshalFromSwift&lt;T&gt;() calls for return marshalling.
    /// Defaults to SwiftContainerGenericType. Overridden by:
    /// - ClassProjection: returns PublicType (class name needed for ISwiftObject.NewFromPayload)
    /// - NonFrozenStructProjection: returns PublicType (same reason)
    /// For parameter direction, SwiftContainerGenericType is used instead (IntPtr for classes).
    /// </summary>
    string MarshalFromSwiftType => SwiftContainerGenericType;

    /// <summary>
    /// When true, signals that this projection uses ObjC container bridge semantics.
    /// For element projections (e.g., ObjCBridgeableProjection): tells container projections
    /// to use whole-container ObjC bridge (NSArray/NSDictionary/NSSet) instead of SwiftArray&lt;T&gt; pipeline.
    /// For container projections: tells OptionalProjection and parent containers to use nullable
    /// pointer ABI instead of SwiftOptional wrapper.
    /// </summary>
    bool UsesObjCContainerBridge => false;

    /// <summary>
    /// Accepts a projection visitor for compile-time exhaustive dispatch.
    /// Each concrete projection implements this with <c>visitor.Visit(this)</c>.
    /// </summary>
    T Accept<T>(IProjectionVisitor<T> visitor);

    /// <summary>
    /// Gets a parameter plan that creates the container object without extracting PayloadBuffer.
    /// Returns null for non-container projections (use GetParameterPlan instead).
    /// Used by OptionalProjection to wrap container objects in SwiftOptional before flattening.
    /// The PInvokeExpression of the returned plan is the container variable name.
    /// </summary>
    MarshalPlan? GetContainerCreationPlan(string paramName) => null;

    /// <summary>
    /// Expression to convert a container value to its public type.
    /// For example, ArrayProjection returns "{var}.AsProjected(e =&gt; e.ToString())".
    /// Returns null for non-container projections.
    /// Used by OptionalProjection return plan to convert the Some value of an optional container.
    /// </summary>
    string? GetReturnContainerConversion(string containerVar) => null;
}

/// <summary>
/// Context information needed to generate a Swift wrapper function.
/// </summary>
public record SwiftWrapperContext
{
    /// <summary>The mangled name of the Swift function being wrapped.</summary>
    public string MangledName { get; init; } = "";

    /// <summary>The module name.</summary>
    public string ModuleName { get; init; } = "";

    /// <summary>The method name.</summary>
    public string MethodName { get; init; } = "";

    /// <summary>
    /// The Swift expression for calling the original method (e.g., "try await __self.fetchData(arg0)").
    /// Set by the emitter. If empty, falls back to a placeholder using MethodName.
    /// </summary>
    public string OriginalCallExpression { get; init; } = "";

    /// <summary>
    /// The Swift type name for the async callback's return parameter (e.g., "String", "(String, Int)").
    /// Set by the emitter for complex return types where C# PInvokeType doesn't map to Swift.
    /// If empty, AsyncProjection falls back to mapping PInvokeType via MapPInvokeTypeToSwift.
    /// </summary>
    public string SwiftCallbackReturnType { get; init; } = "";
}
