// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Projection for the Swift KeyPath family (AnyKeyPath, PartialKeyPath&lt;Root&gt;,
/// KeyPath&lt;Root,Value&gt;, WritableKeyPath&lt;Root,Value&gt;, ReferenceWritableKeyPath&lt;Root,Value&gt;).
///
/// KeyPaths are Swift reference classes with a single-pointer ABI at the @_cdecl boundary:
/// returns are +1 retained, parameters are @guaranteed (borrowed). The C# wrappers in
/// <see cref="Swift.KeyPath{TRoot,TValue}"/> and friends derive directly from
/// SafeHandleZeroOrMinusOneIsInvalid (no SwiftClassHandle indirection), so parameter
/// marshalling uses <c>paramName.DangerousGetHandle()</c> directly — without a
/// <c>.Payload</c> hop.
///
/// Equality is delegated to <c>AnyKeyPath.==</c> via a runtime shim, never pointer identity:
/// cross-module compilation can produce two distinct objects for the same logical key path.
///
/// Currently covers the foundation pass-through path only (RequiresSwiftWrapper=false).
/// <c>RequiresSwiftWrapper</c> may be flipped true for typed-singleton trampolines in the future.
/// </summary>
public class KeyPathProjection : ITypeProjection
{
    private readonly string _publicType;

    /// <summary>
    /// The unqualified C# class name (e.g. "AnyKeyPath", "PartialKeyPath", "KeyPath",
    /// "WritableKeyPath", "ReferenceWritableKeyPath"). Used as a discriminator by
    /// emitter sites that need to special-case writable vs read-only key paths.
    /// </summary>
    public string ShortName { get; }

    /// <summary>
    /// Projected public C# types for each Swift generic parameter, in declaration order
    /// (PartialKeyPath: [TRoot]; KeyPath/WritableKeyPath/ReferenceWritableKeyPath: [TRoot, TValue]).
    /// Empty for AnyKeyPath.
    /// </summary>
    public IReadOnlyList<string> GenericArgPublicTypes { get; }

    public KeyPathProjection(string shortName, IReadOnlyList<string> genericArgPublicTypes)
    {
        ShortName = shortName;
        GenericArgPublicTypes = genericArgPublicTypes;
        _publicType = genericArgPublicTypes.Count == 0
            ? $"Swift.{shortName}"
            : $"Swift.{shortName}<{string.Join(", ", genericArgPublicTypes)}>";
    }

    public string PublicType => _publicType;
    public string PInvokeType => "IntPtr";
    public string? PInvokeAttribute => null;

    /// <summary>
    /// MarshalFromSwift uses the public type so <see cref="Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwiftObject{T}"/>
    /// can resolve <c>T.NewFromPayload(IntPtr)</c> via the concrete typed wrapper.
    /// </summary>
    public string MarshalFromSwiftType => _publicType;

    /// <summary>
    /// The C# KeyPath wrapper IS the SafeHandle (no separate container struct), so the
    /// "container" type for bound-generic-class-return marshalling is the public wrapper
    /// itself. This matches the convention of other container projections (ArrayProjection,
    /// DictionaryProjection, OptionalProjection, SetProjection, ResultProjection) which
    /// all set <c>ContainerTypeName == MarshalFromSwiftType</c>. Without this override,
    /// the default <c>ContainerTypeName => PInvokeType</c> would return "IntPtr",
    /// causing the bound-generic class-return branch in WrapperEmitter to emit
    /// <c>SwiftMarshal.MarshalFromSwift&lt;IntPtr&gt;(...)</c> — a C# compile error when
    /// the declared return type is the typed wrapper (e.g. <c>PartialKeyPath&lt;TEntity&gt;</c>).
    /// </summary>
    public string ContainerTypeName => _publicType;

    public MarshalPlan GetParameterPlan(string paramName)
    {
        // The C# KeyPath wrapper IS a SafeHandle (no Payload indirection). The Swift side
        // borrows @guaranteed for the duration of the call, so we DangerousGetHandle the
        // raw pointer and let the SafeHandle keep it alive. No DangerousAddRef needed:
        // the wrapper outlives the call frame because it's still a live local on the
        // managed stack.
        return new MarshalPlan
        {
            PInvokeExpression = $"{paramName}.DangerousGetHandle()"
        };
    }

    public MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy)
    {
        // Swift returns +1 retained. MarshalFromSwiftObject calls T.NewFromPayload which
        // constructs the concrete SafeHandle-derived wrapper and adopts the retain.
        return new MarshalPlan
        {
            PInvokeExpression = $"({_publicType})SwiftMarshal.MarshalFromSwiftObject<{_publicType}>({resultName})"
        };
    }

    public bool RequiresSwiftWrapper => false;
    public string? GetSwiftWrapperCode(SwiftWrapperContext context) => null;

    // Container element conversions — same shape as ClassProjection but without the
    // .Payload hop because the wrapper IS the SafeHandle.
    public string? GetParameterElementConversion(string elementVar) =>
        $"{elementVar}.DangerousGetHandle()";

    public string? GetReturnElementConversion(string elementVar) => null;

    public T Accept<T>(IProjectionVisitor<T> visitor) => visitor.Visit(this);
}
