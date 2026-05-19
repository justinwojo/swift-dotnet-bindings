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
/// Session 3 covers the foundation pass-through path only (RequiresSwiftWrapper=false).
/// Session 4 may flip <c>RequiresSwiftWrapper</c> true for typed-singleton trampolines.
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
