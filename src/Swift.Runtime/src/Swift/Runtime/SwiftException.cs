// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace Swift.Runtime;

/// <summary>
/// SwiftException is thrown when a Swift method throws an untyped error.
/// It wraps the error message from Swift's Error protocol, and — when thrown via
/// <see cref="Swift.Runtime.InteropServices.SwiftMarshal.ThrowSwiftError"/> — also carries the
/// live (retained) Swift error box through <see cref="ErrorHandle"/>, so a consumer can recover
/// error identity beyond the flattened message. The handle is released when this exception is
/// finalized, under the process-exit guard.
/// </summary>
public class SwiftException : Exception
{
    private IntPtr _errorHandle;
    private readonly Action<IntPtr>? _releaseError;

    /// <summary>
    /// Creates a new SwiftException with the specified error message from Swift.
    /// </summary>
    /// <param name="message">The error message from Swift's Error.localizedDescription or String(describing:).</param>
    public SwiftException(string message) : base(message)
    {
        // No native handle to release — skip the finalizer entirely.
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Creates a new SwiftException with the specified error message and inner exception.
    /// </summary>
    /// <param name="message">The error message from Swift.</param>
    /// <param name="innerException">The inner exception that caused this error.</param>
    public SwiftException(string message, Exception innerException) : base(message, innerException)
    {
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Creates a SwiftException that carries the live (retained) Swift error box. The boxed error
    /// is owned by this exception and released when it is finalized — never on the throw path — so
    /// the throw performs no P/Invoke (matching the maccatalyst-x64 Mono unwinder constraint that
    /// the untyped throw not run a P/Invoke in a <c>finally</c> around the throw).
    /// </summary>
    /// <param name="message">The error description from Swift's String(describing:).</param>
    /// <param name="errorHandle">The retained Swift error pointer (caller transfers ownership).</param>
    /// <param name="releaseError">Action that releases one ARC reference on the error (SBW_ReleaseError).</param>
    internal SwiftException(string message, IntPtr errorHandle, Action<IntPtr> releaseError) : base(message)
    {
        _errorHandle = errorHandle;
        _releaseError = releaseError;
        // This instance owns the retained error box; keep finalization registered so the box is
        // released when the exception is collected. If there is no live handle, suppress.
        if (errorHandle == IntPtr.Zero)
            GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The live (retained) Swift error box this exception carries, or <see cref="IntPtr.Zero"/> when
    /// the exception was constructed without one. Valid for the lifetime of the exception. The
    /// handle is a Swift error object reference usable with the per-binding
    /// <c>SBW_GetErrorDescription</c> / typed error extraction wrappers.
    /// <para>
    /// The handle is <b>borrowed, not owned</b> by the caller: this exception owns the single ARC
    /// reference and releases it on finalization. Read it and pass it to the read-only error wrappers,
    /// but do <b>not</b> call <c>SBW_ReleaseError</c> (or otherwise release it) yourself — doing so
    /// double-frees when the exception is later finalized. Do not retain the value past the
    /// exception's own lifetime.
    /// </para>
    /// </summary>
    public IntPtr ErrorHandle => _errorHandle;

    /// <summary>
    /// Releases the carried Swift error box, under the process-exit guard. The release is a P/Invoke
    /// via <see cref="_releaseError"/>; during process exit the Swift runtime may be partially torn
    /// down, so the release is skipped (mirroring the SwiftClassHandle finalizer policy).
    /// </summary>
    ~SwiftException()
    {
        var handle = _errorHandle;
        if (handle == IntPtr.Zero)
            return;
        _errorHandle = IntPtr.Zero;
        if (!SwiftExitGuard.IsProcessExiting)
            _releaseError?.Invoke(handle);
    }
}

/// <summary>
/// SwiftException&lt;TError&gt; is thrown when a Swift method with typed throws
/// (e.g., <c>throws(ParseError)</c>) encounters an error.
/// The <see cref="Error"/> property provides access to the typed error value,
/// which is populated for both sync and async methods when the error type can
/// be extracted from the Swift error box. Falls back to default (null) only
/// when the Swift <c>as?</c> cast fails (rare edge case).
/// </summary>
/// <typeparam name="TError">The Swift error type (typically an enum with Error conformance).</typeparam>
public class SwiftException<TError> : SwiftException
{
    /// <summary>
    /// The typed Swift error value, or default if the error value could not be extracted.
    /// For both sync and async methods, this contains the fully-marshalled error enum with case
    /// and associated values when extraction succeeds. Falls back to default (null for reference types)
    /// only when the Swift <c>as?</c> cast fails — the error message is still available
    /// via <see cref="Exception.Message"/>.
    /// </summary>
    public TError? Error { get; }

    /// <summary>
    /// Creates a new SwiftException&lt;TError&gt; with only an error message (no typed error value).
    /// Used as fallback when the Swift extractor's <c>as?</c> cast fails (rare edge case).
    /// </summary>
    /// <param name="message">The error message from Swift.</param>
    public SwiftException(string message) : base(message)
    {
        Error = default;
    }

    /// <summary>
    /// Creates a new SwiftException&lt;TError&gt; with both the typed error value and error message.
    /// Used for both sync and async throwing methods when error extraction succeeds.
    /// </summary>
    /// <param name="error">The typed Swift error value.</param>
    /// <param name="message">The error message from Swift's String(describing:).</param>
    public SwiftException(TError error, string message) : base(message)
    {
        Error = error;
    }
}
