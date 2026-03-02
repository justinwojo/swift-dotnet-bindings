// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices.Swift;

namespace Swift;

/// <summary>
/// Exception type for signaling Swift errors from simplified throwing closure callbacks.
/// When a simplified closure overload (Action/Func instead of SwiftResult-returning delegate)
/// needs to propagate a Swift error, throw this exception. The generated wrapper catches it
/// and converts it to <c>SwiftResult.FromFailure(ex.Error)</c>.
/// </summary>
public class SwiftErrorException : Exception
{
    /// <summary>
    /// Gets the underlying Swift error.
    /// </summary>
    public SwiftError Error { get; }

    /// <summary>
    /// Creates a new SwiftErrorException wrapping a Swift error.
    /// </summary>
    /// <param name="error">The Swift error to propagate.</param>
    public SwiftErrorException(SwiftError error)
        : base("A Swift error occurred.")
    {
        Error = error;
    }
}
