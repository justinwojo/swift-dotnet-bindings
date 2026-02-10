// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Detects method signatures that trigger the Mono JIT <c>jit-info.c:918</c> assertion crash.
/// The crash occurs when Mono encounters CallConvSwift calling convention in P/Invoke declarations.
/// This detector identifies three risky patterns:
/// <list type="bullet">
///   <item><description>Closure parameters — escaping closures use <c>delegate* unmanaged[Swift]</c> callbacks</description></item>
///   <item><description>Existential parameters — require existential metadata via CallConvSwift wrapper</description></item>
///   <item><description>SwiftString returns — <c>ToString()</c>/<c>Length</c> route through CallConvSwift P/Invokes</description></item>
/// </list>
/// </summary>
public static class MonoJitRiskDetector
{
    /// <summary>
    /// Risk categories for Mono JIT crash patterns. Multiple risks can be present simultaneously.
    /// </summary>
    [Flags]
    public enum MonoJitRisk
    {
        /// <summary>No risk detected — safe for Mono JIT.</summary>
        None = 0,

        /// <summary>
        /// Method has a closure parameter using Swift calling convention.
        /// Escaping closures (non-@convention(c)) trigger <c>[UnmanagedCallersOnly(CallConvs = CallConvSwift)]</c>
        /// which crashes Mono's JIT.
        /// </summary>
        ClosureParameter = 1,

        /// <summary>
        /// Method has an existential parameter (protocol type).
        /// Existential metadata access routes through CallConvSwift wrappers.
        /// </summary>
        ExistentialParameter = 2,

        /// <summary>
        /// Method returns Swift.String.
        /// SwiftString.ToString() and .Length use CallConvSwift P/Invokes internally.
        /// </summary>
        SwiftStringReturn = 4
    }

    /// <summary>
    /// Analyzes a method declaration for Mono JIT crash risk patterns.
    /// Returns a flags enum indicating which risk patterns are present.
    /// </summary>
    /// <param name="methodDecl">The method declaration to analyze.</param>
    /// <returns>Flags indicating detected risk patterns.</returns>
    public static MonoJitRisk AnalyzeMethod(MethodDecl methodDecl)
    {
        var risk = MonoJitRisk.None;

        if (methodDecl.CSSignature.Count == 0)
            return risk;

        // Check return type (CSSignature[0]) for SwiftString
        if (IsSwiftStringType(methodDecl.CSSignature[0].SwiftTypeSpec))
            risk |= MonoJitRisk.SwiftStringReturn;

        // Check parameters (CSSignature[1..]) for closures and existentials
        for (int i = 1; i < methodDecl.CSSignature.Count; i++)
        {
            var typeSpec = methodDecl.CSSignature[i].SwiftTypeSpec;

            if (IsRiskyClosureType(typeSpec))
                risk |= MonoJitRisk.ClosureParameter;

            if (IsExistentialType(typeSpec))
                risk |= MonoJitRisk.ExistentialParameter;
        }

        return risk;
    }

    /// <summary>
    /// Returns true if the method has any Mono JIT risk pattern.
    /// </summary>
    /// <param name="methodDecl">The method declaration to check.</param>
    /// <returns>True if at least one risk pattern is detected.</returns>
    public static bool IsMonoJitRisk(MethodDecl methodDecl)
    {
        return AnalyzeMethod(methodDecl) != MonoJitRisk.None;
    }

    /// <summary>
    /// Analyzes a method for Mono JIT risk and sets <see cref="MethodDecl.DetectedJitRisks"/>
    /// with the detected risk flags. This is informational only — it does not affect P/Invoke
    /// routing. Routing is controlled by <see cref="MethodDecl.UsesWrapperLibrary"/>, which is
    /// only set when a corresponding Swift wrapper function has been generated (e.g., by
    /// ArraySlice normalization or default parameter overload emitters).
    /// </summary>
    /// <param name="methodDecl">The method declaration to analyze and annotate.</param>
    public static void ApplyRiskDetection(MethodDecl methodDecl)
    {
        methodDecl.DetectedJitRisks = AnalyzeMethod(methodDecl);
    }

    /// <summary>
    /// Checks if a TypeSpec represents Swift.String, including Optional&lt;Swift.String&gt;.
    /// </summary>
    internal static bool IsSwiftStringType(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec named)
            return false;

        if (named.HasModule() && named.Name == "Swift.String")
            return true;

        // Optional<Swift.String>
        if (named.Name == "Swift.Optional" &&
            named.GenericParameters.Count == 1 &&
            IsSwiftStringType(named.GenericParameters[0]))
            return true;

        return false;
    }

    /// <summary>
    /// Checks if a TypeSpec is a closure using Swift calling convention (risky for Mono JIT).
    /// Returns false for @convention(c) closures which use Cdecl and are safe.
    /// Also detects Optional-wrapped closures (Swift.Optional&lt;ClosureTypeSpec&gt;).
    /// </summary>
    internal static bool IsRiskyClosureType(TypeSpec typeSpec)
    {
        var closure = ExtractClosureTypeSpec(typeSpec);
        if (closure == null)
            return false;

        // @convention(c) closures are safe — they use Cdecl, not Swift calling convention
        if (IsConventionC(closure))
            return false;

        return true;
    }

    /// <summary>
    /// Checks if a TypeSpec represents an existential type (protocol type or protocol composition),
    /// including Optional-wrapped existentials (e.g., Optional&lt;any Protocol&gt;).
    /// Detects both ProtocolListTypeSpec and single-protocol existentials (NamedTypeSpec with IsAny).
    /// </summary>
    internal static bool IsExistentialType(TypeSpec typeSpec)
    {
        if (typeSpec is ProtocolListTypeSpec)
            return true;

        if (typeSpec is NamedTypeSpec named)
        {
            if (named.IsAny)
                return true;

            // Optional<existential>
            if (named.Name == "Swift.Optional" &&
                named.GenericParameters.Count == 1 &&
                IsExistentialType(named.GenericParameters[0]))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Extracts a ClosureTypeSpec from a TypeSpec, handling both direct closures
    /// and Optional-wrapped closures.
    /// </summary>
    private static ClosureTypeSpec? ExtractClosureTypeSpec(TypeSpec typeSpec)
    {
        if (typeSpec is ClosureTypeSpec closure)
            return closure;

        // Check for Swift.Optional<Closure>
        if (typeSpec is NamedTypeSpec named &&
            named.Name == "Swift.Optional" &&
            named.GenericParameters.Count == 1 &&
            named.GenericParameters[0] is ClosureTypeSpec innerClosure)
        {
            return innerClosure;
        }

        return null;
    }

    /// <summary>
    /// Checks if a closure has @convention(c) attribute, making it safe for Mono JIT.
    /// </summary>
    private static bool IsConventionC(ClosureTypeSpec closure)
    {
        if (!closure.HasAttributes)
            return false;

        return closure.Attributes.Exists(attr =>
            attr.Name == "convention" &&
            attr.Parameters.Count > 0 &&
            attr.Parameters[0] == "c");
    }
}
