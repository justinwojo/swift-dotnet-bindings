// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Projection for frozen structs with memory management (ClassWithBufferStruct).
/// These are Swift structs that contain reference-counted fields (e.g., String fields),
/// are frozen (ABI-stable layout), but need memory management for their reference fields.
///
/// P/Invoke returns a .Buffer struct by value (blittable layout).
/// Direct returns need new IntPtr(&amp;result) with RequiresUnsafe = true.
/// Indirect returns receive a pointer directly.
///
/// Parameter direction: PayloadBuffer extraction (same as existing WrapperEmitter.Marshalling.cs:711).
/// Return direction: MarshalFromSwift constructs the ISwiftObject from the buffer pointer.
/// </summary>
public class FrozenWithMemoryProjection : ITypeProjection
{
    private readonly string _typeName;

    public FrozenWithMemoryProjection(string typeName)
    {
        _typeName = typeName;
    }

    public string PublicType => _typeName;
    public string PInvokeType => $"{_typeName}.Buffer";
    public string? PInvokeAttribute => null;

    /// <summary>
    /// The container generic type uses the .Buffer struct name, matching how MethodSignature
    /// emits FrozenBuffer types in P/Invoke signatures.
    /// </summary>
    public string SwiftContainerGenericType => $"{_typeName}.Buffer";

    /// <summary>
    /// For MarshalFromSwift calls, use the type name (not .Buffer). MarshalFromSwift needs
    /// the real type to construct instances via ISwiftObject.NewFromPayload.
    /// </summary>
    public string MarshalFromSwiftType => _typeName;

    /// <summary>
    /// The container type name for use in SwiftOptional&lt;T&gt; and TypeMetadata resolution.
    /// Uses .Buffer to match the P/Invoke return type.
    /// </summary>
    public string ContainerTypeName => $"{_typeName}.Buffer";

    public MarshalPlan GetParameterPlan(string paramName)
    {
        return new MarshalPlan
        {
            SetupStatements = new List<MarshalStatement>
            {
                new MarshalStatement.Using(
                    $"PayloadBuffer<{_typeName}.Buffer>", $"{paramName}Disposable", $"{paramName}.PayloadBuffer")
            },
            PInvokeExpression = $"{paramName}Disposable.Buffer"
        };
    }

    public MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy)
    {
        return strategy switch
        {
            ReturnStrategy.Direct => new MarshalPlan
            {
                PInvokeExpression = $"SwiftMarshal.MarshalFromSwift<{_typeName}>(new IntPtr(&{resultName}))",
                RequiresUnsafe = true
            },
            ReturnStrategy.IndirectResult => new MarshalPlan
            {
                PInvokeExpression = $"SwiftMarshal.MarshalFromSwift<{_typeName}>({resultName})"
            },
            ReturnStrategy.OutBuffer => new MarshalPlan
            {
                PInvokeExpression = $"SwiftMarshal.MarshalFromSwift<{_typeName}>({resultName})"
            },
            _ => MarshalPlan.PassThrough(resultName)
        };
    }

    public bool RequiresSwiftWrapper => false;
    public string? GetSwiftWrapperCode(SwiftWrapperContext context) => null;

    /// <summary>
    /// No parameter element conversion. Frozen-with-memory types inside containers (Array, Dictionary)
    /// would require lifecycle-managed PayloadBuffer extraction that can't be expressed in a LINQ Select
    /// lambda without leaking SafeHandle refs. No validated library uses this composition.
    /// Returning null causes a C# compile error (type mismatch) if this composition is ever attempted,
    /// which is preferable to silently leaking handles.
    /// </summary>
    public string? GetParameterElementConversion(string elementVar) => null;

    /// <summary>
    /// No return element conversion needed. When used inside Optional, ToNullable() handles
    /// construction via ISwiftObject.NewFromPayload. Standalone returns use GetReturnPlan.
    /// </summary>
    public string? GetReturnElementConversion(string elementVar) => null;

    public T Accept<T>(IProjectionVisitor<T> visitor) => visitor.Visit(this);
}
