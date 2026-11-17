// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// The one ordering rule for a reverse Swift-convention callback: an
/// <c>[UnmanagedCallersOnly(CallConvs = [CallConvSwift])]</c> method Swift calls as a closure's
/// function, and the <c>delegate* unmanaged[Swift]&lt;…&gt;</c> type that names it. Every such
/// callback the generator emits builds its parameter list and its pointer type here, so the two
/// cannot drift apart and no emitter places a special parameter on its own.
///
/// <para>The order is: <c>SwiftError*</c> first (throwing callbacks only), then
/// <c>SwiftIndirectResult</c> (callbacks whose result Swift passes by address), then the ordinary
/// parameters in Swift's order, then <c>SwiftSelf</c>, the closure context, last.</para>
///
/// <para>The indirect result is a <c>SwiftIndirectResult</c>, never an ordinary pointer: Swift
/// passes a closure's result buffer in the indirect-result register (x8 on arm64, rax on x86_64)
/// and leaves the first argument register to the first ordinary argument, so a callback that
/// declared the buffer as a leading <c>void*</c> read an unrelated register and wrote the result
/// through it.</para>
///
/// <para>Why this order: the JIT, NativeAOT and Mono assign the error and self registers by the
/// parameter's type, so the position is ABI-neutral there. The .NET 11 CoreCLR interpreter, which a
/// Debug iOS app runs on, is positional for reverse calls: its Swift reverse thunk stores the error
/// register into the FIRST managed argument slot and supports the context only in the LAST
/// (dotnet/runtime#136522, which upstream treats as by design). An error parameter anywhere else
/// overwrites the first ordinary argument with the incoming error register (zero) before the
/// callback body runs.</para>
///
/// <para>Scope: the Swift lane only. A C-convention callback (<c>useCdecl</c>) takes its order from
/// the Swift <c>@_cdecl</c> adapter that calls it, which this rule does not touch. Forward calls
/// through an <c>unmanaged[Swift]</c> pointer keep their special parameters trailing, which is the
/// order the interpreter supports in that direction.</para>
/// </summary>
internal static class ReverseSwiftCallbackSignature
{
    /// <summary>The error out-parameter's declaration in a throwing callback.</summary>
    public const string ErrorParameter = "SwiftError* errorOut";

    /// <summary>The closure context's declaration: Swift passes it in the self register.</summary>
    public const string ContextParameter = "SwiftSelf context";

    /// <summary>
    /// The indirect result's declaration: Swift passes the result buffer in the indirect-result
    /// register. The callback body reads the buffer as <see cref="IndirectResultBuffer"/>.
    /// </summary>
    public const string IndirectResultParameter = "global::System.Runtime.InteropServices.Swift.SwiftIndirectResult __indirectResult";

    /// <summary>The statement that names the indirect result's buffer <c>indirectResult</c>.</summary>
    public const string IndirectResultBuffer = "void* indirectResult = __indirectResult.Value;";

    private const string ErrorType = "SwiftError*";
    private const string ContextType = "SwiftSelf";
    private const string IndirectResultType = "global::System.Runtime.InteropServices.Swift.SwiftIndirectResult";

    /// <summary>
    /// The callback's full parameter list: <paramref name="ordinaryParameters"/> (declarations, in
    /// Swift's order) with the error and context placed by the rule.
    /// </summary>
    public static List<string> Parameters(IEnumerable<string> ordinaryParameters, bool throws, bool indirectResult = false) =>
        Arrange(ordinaryParameters, throws ? ErrorParameter : null, indirectResult ? IndirectResultParameter : null, ContextParameter);

    /// <summary>
    /// The <c>delegate* unmanaged[Swift]&lt;…&gt;</c> type of a callback whose ordinary parameter
    /// types are <paramref name="ordinaryTypes"/> and whose result is <paramref name="returnType"/>.
    /// Mirrors <see cref="Parameters"/> position for position.
    /// </summary>
    public static string FunctionPointerType(IEnumerable<string> ordinaryTypes, bool throws, string returnType, bool indirectResult = false)
    {
        var types = Arrange(ordinaryTypes, throws ? ErrorType : null, indirectResult ? IndirectResultType : null, ContextType);
        types.Add(returnType);
        return $"delegate* unmanaged[Swift]<{string.Join(", ", types)}>";
    }

    private static List<string> Arrange(IEnumerable<string> ordinary, string? error, string? indirectResult, string context)
    {
        var arranged = new List<string>();
        if (error is not null)
            arranged.Add(error);
        if (indirectResult is not null)
            arranged.Add(indirectResult);
        arranged.AddRange(ordinary);
        arranged.Add(context);
        return arranged;
    }
}
