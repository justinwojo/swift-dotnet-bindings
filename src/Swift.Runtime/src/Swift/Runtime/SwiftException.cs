// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace Swift.Runtime;

/// <summary>
/// SwiftException is thrown when a Swift async method throws an error.
/// This exception wraps the error message from Swift's Error protocol.
/// </summary>
public class SwiftException : Exception
{
    /// <summary>
    /// Creates a new SwiftException with the specified error message from Swift.
    /// </summary>
    /// <param name="message">The error message from Swift's Error.localizedDescription or String(describing:).</param>
    public SwiftException(string message) : base(message)
    {
    }

    /// <summary>
    /// Creates a new SwiftException with the specified error message and inner exception.
    /// </summary>
    /// <param name="message">The error message from Swift.</param>
    /// <param name="innerException">The inner exception that caused this error.</param>
    public SwiftException(string message, Exception innerException) : base(message, innerException)
    {
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
