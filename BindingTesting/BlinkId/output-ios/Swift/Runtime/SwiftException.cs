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
