// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Identifies a phase in the @_cdecl parameter ordering.
/// Both the C# P/Invoke signature and the Swift wrapper parameter list
/// are assembled by iterating over these phases in order.
/// </summary>
public enum CdeclPhase
{
    /// <summary>Indirect result buffer pointer (first param when needed).</summary>
    ResultPtr,

    /// <summary>Error out-pointer for throwing methods.</summary>
    ErrorOut,

    /// <summary>Instance self parameter.</summary>
    Self,

    /// <summary>Regular method/constructor arguments (including subscript indices and newValue).</summary>
    Arguments,

    /// <summary>Generic type metadata and protocol conformance witnesses.</summary>
    Metadata
}

/// <summary>
/// The result of <see cref="CdeclSignatureContract.DetermineParameterOrder"/>:
/// an ordered list of phases describing the parameter layout, plus convenience flags.
/// </summary>
public class CdeclParameterOrder
{
    public CdeclParameterOrder(IReadOnlyList<CdeclPhase> phases, bool needsResultPtr)
    {
        Phases = phases;
        NeedsResultPtr = needsResultPtr;
    }

    /// <summary>Ordered list of parameter phases.</summary>
    public IReadOnlyList<CdeclPhase> Phases { get; }

    /// <summary>Whether an indirect result buffer parameter is needed.</summary>
    public bool NeedsResultPtr { get; }
}

/// <summary>
/// Single source of truth for parameter phase ordering in @_cdecl wrappers.
/// Both <see cref="SignatureHandler.GetPInvokeSignature"/> (C# side) and
/// the four wrapper emitters (Swift side) derive their parameter ordering from this contract.
///
/// The contract defines ORDERING — which phases appear and in what sequence.
/// It does NOT handle type mapping, parameter splitting (closures → 2 params,
/// strings → 2 params), or reconstruction code.
/// </summary>
public static class CdeclSignatureContract
{
    /// <summary>
    /// Determines the parameter phase ordering for a given method.
    ///
    /// Constructor semantics (class vs struct):
    /// - Struct: [ResultPtr] [ErrorOut?] [Arguments?] [Metadata]
    /// - Class:  [ErrorOut?] [Arguments?] [Metadata]
    ///
    /// Protocol extension methods:
    /// - [ResultPtr?] [Self?] [Arguments?] [Metadata] [ErrorOut?]
    ///
    /// Regular methods, property/subscript accessors:
    /// - [ResultPtr?] [Arguments?] [Metadata] [Self?] [ErrorOut?]
    /// </summary>
    /// <param name="env">The method environment.</param>
    /// <param name="overrideNeedsResultPtr">
    /// Optional override for the indirect result decision. When provided, this value is used
    /// instead of calling <see cref="MarshallingHelpers.MethodRequiresIndirectResult"/>.
    /// Wrapper emitters pass their own needsResultPtr (from GetCdeclReturnMapping) since they
    /// handle ResultPtr outside the phase loop. The PInvoke side skips the ResultPtr phase
    /// entirely (HandleReturnType handles it), so the value doesn't affect PInvoke output.
    /// </param>
    /// <param name="overrideHasArguments">
    /// Optional override for the Arguments phase. Property/subscript setter emitters pass true
    /// because the newValue parameter is not represented in MethodDecl.CSSignature.
    /// </param>
    /// <param name="overrideNeedsSelf">
    /// Optional override for the Self phase. Property/subscript emitters pass !IsStatic because
    /// accessor MethodDecls may not carry the static flag (it lives on PropertyDecl/SubscriptDecl).
    /// </param>
    public static CdeclParameterOrder DetermineParameterOrder(
        MethodEnvironment env,
        bool? overrideNeedsResultPtr = null,
        bool? overrideHasArguments = null,
        bool? overrideNeedsSelf = null)
    {
        var phases = new List<CdeclPhase>();
        bool needsResultPtr;
        bool hasArgs = overrideHasArguments ?? HasArguments(env);
        bool throws = env.MethodDecl.Throws;
        bool needsSelf = overrideNeedsSelf ?? MarshallingHelpers.MethodRequiresSwiftSelf(env);

        if (env.MethodDecl.IsConstructor)
        {
            bool isClass = env.ParentDecl is ClassDecl;
            // Struct constructors always write to result buffer.
            // Class constructors return a pointer directly — no buffer.
            needsResultPtr = overrideNeedsResultPtr ?? !isClass;

            if (needsResultPtr)
                phases.Add(CdeclPhase.ResultPtr);
            if (throws)
                phases.Add(CdeclPhase.ErrorOut);
            if (hasArgs)
                phases.Add(CdeclPhase.Arguments);
            phases.Add(CdeclPhase.Metadata);
        }
        else if (env.MethodDecl.IsProtocolExtensionMethod)
        {
            needsResultPtr = overrideNeedsResultPtr ?? MarshallingHelpers.MethodRequiresIndirectResult(env);

            if (needsResultPtr)
                phases.Add(CdeclPhase.ResultPtr);
            if (needsSelf)
                phases.Add(CdeclPhase.Self);
            if (hasArgs)
                phases.Add(CdeclPhase.Arguments);
            phases.Add(CdeclPhase.Metadata);
            if (throws)
                phases.Add(CdeclPhase.ErrorOut);
        }
        else
        {
            // Regular methods, property getters/setters, subscript accessors
            needsResultPtr = overrideNeedsResultPtr ?? MarshallingHelpers.MethodRequiresIndirectResult(env);

            if (needsResultPtr)
                phases.Add(CdeclPhase.ResultPtr);
            if (hasArgs)
                phases.Add(CdeclPhase.Arguments);
            phases.Add(CdeclPhase.Metadata);
            if (needsSelf)
                phases.Add(CdeclPhase.Self);
            if (throws)
                phases.Add(CdeclPhase.ErrorOut);
        }

        return new CdeclParameterOrder(phases, needsResultPtr);
    }

    /// <summary>
    /// Returns true if the method has meaningful arguments beyond the return type.
    /// Debug parameters and empty tuple parameters are excluded.
    /// </summary>
    private static bool HasArguments(MethodEnvironment env)
    {
        return env.MethodDecl.CSSignature.Skip(1)
            .Any(a => !DefaultParameterOverloadEmitter.IsDebugParameter(a)
                    && !a.SwiftTypeSpec.IsEmptyTuple);
    }
}
