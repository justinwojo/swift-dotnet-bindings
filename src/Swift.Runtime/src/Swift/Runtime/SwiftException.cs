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
/// The <see cref="Error"/> property provides access to the typed error value
/// when available (async methods), or null when only the error message is
/// available (sync methods — error value extraction from existential box
/// requires Swift runtime support not yet implemented).
/// </summary>
/// <typeparam name="TError">The Swift error type (typically an enum with Error conformance).</typeparam>
public class SwiftException<TError> : SwiftException
{
    /// <summary>
    /// The typed Swift error value, or default if the error value could not be extracted.
    /// For async methods, this contains the fully-marshalled error enum with case and associated values.
    /// For sync methods, this is default (null for reference types) — the error message is still available
    /// via <see cref="Exception.Message"/>.
    /// </summary>
    public TError? Error { get; }

    /// <summary>
    /// Creates a new SwiftException&lt;TError&gt; with only an error message (no typed error value).
    /// Used for sync throwing methods where the error value cannot be extracted from the existential box.
    /// </summary>
    /// <param name="message">The error message from Swift.</param>
    public SwiftException(string message) : base(message)
    {
        Error = default;
    }

    /// <summary>
    /// Creates a new SwiftException&lt;TError&gt; with both the typed error value and error message.
    /// Used for async throwing methods where the error bytes are transported across the boundary.
    /// </summary>
    /// <param name="error">The typed Swift error value.</param>
    /// <param name="message">The error message from Swift's String(describing:).</param>
    public SwiftException(TError error, string message) : base(message)
    {
        Error = error;
    }
}
