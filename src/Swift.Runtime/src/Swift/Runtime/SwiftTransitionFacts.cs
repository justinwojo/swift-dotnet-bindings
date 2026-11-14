// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace Swift.Runtime;

/// <summary>
/// The register-convention shapes of a direct Swift-convention transition — a
/// <c>[UnmanagedCallConv(CallConvSwift)]</c> import, a call through an
/// <c>unmanaged[Swift]</c> function pointer, or a Swift-convention callback handed to Swift — that
/// some .NET runtimes do not execute correctly.
/// </summary>
/// <remarks>
/// <para>
/// A generated binding records, for each direct transition it keeps, which of these shapes the
/// transition has, and guards every member that reaches one with
/// <see cref="SwiftTransitionGuard.ThrowIfUnsupported"/>. Whether a shape is safe is a property of
/// the executing runtime, not of the binding: <see cref="SwiftTransitionGuard.UnsupportedFacts"/>
/// names the shapes the current one refuses.
/// </para>
/// <para>
/// The values are stable: a binding compiles them into its IL as constants. A bit this runtime
/// does not know is refused on every runtime, so a binding generated for a newer runtime never
/// runs a shape an older one cannot judge.
/// </para>
/// </remarks>
[Flags]
public enum SwiftTransitionFacts : ulong
{
    /// <summary>No shape any runtime is known to mishandle.</summary>
    None = 0,

    /// <summary>
    /// The import passes an untyped <c>SwiftSelf</c>, so the runtime loads the Swift self register
    /// (x20 on arm64, r13 on x86_64) on the way in.
    /// </summary>
    SelfRegister = 1UL << 0,

    /// <summary>
    /// The import declares a <c>SwiftSelf</c> or <c>SwiftError*</c> before an ordinary argument.
    /// </summary>
    SpecialBeforeArguments = 1UL << 1,

    /// <summary>
    /// The import's signature needs Swift lowering: a <c>SwiftSelf</c>, <c>SwiftError*</c> or
    /// <c>SwiftIndirectResult</c>, or a parameter or result that is not a primitive scalar or pointer.
    /// </summary>
    LoweredSignature = 1UL << 2,

    /// <summary><see cref="SelfRegister"/>, for a call through an <c>unmanaged[Swift]</c> function pointer.</summary>
    FunctionPointerSelfRegister = 1UL << 8,

    /// <summary><see cref="SpecialBeforeArguments"/>, for a call through an <c>unmanaged[Swift]</c> function pointer.</summary>
    FunctionPointerSpecialBeforeArguments = 1UL << 9,

    /// <summary><see cref="LoweredSignature"/>, for a call through an <c>unmanaged[Swift]</c> function pointer.</summary>
    FunctionPointerLoweredSignature = 1UL << 10,

    /// <summary>
    /// The member hands Swift the address of a managed Swift-convention <c>[UnmanagedCallersOnly]</c>
    /// callback as a closure function. Swift calls it with the closure context in the self register
    /// (x20 on arm64, r13 on x86_64), and with the error register when the closure throws, so the
    /// runtime's reverse stub must apply Swift lowering on the way back in.
    /// </summary>
    ReverseCallback = 1UL << 16,
}
