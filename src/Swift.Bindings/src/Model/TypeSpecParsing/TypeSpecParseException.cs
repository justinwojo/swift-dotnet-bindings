// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;

namespace BindingsGeneration;

/// <summary>
/// Thrown by <see cref="TypeSpecParser"/> when a Swift type string cannot be parsed into a
/// <see cref="TypeSpec"/> — either because a token is unexpected/illegal, or because the
/// canonical (EOF-strict) entry point found trailing tokens after a complete type.
///
/// This is a dedicated type so callers can catch <em>parse failures</em> specifically
/// (<c>catch (TypeSpecParseException)</c>) and route them to an observable channel, rather
/// than using a bare <c>catch</c>/<c>catch (Exception)</c> that would also swallow genuine
/// bugs (NullReferenceException, etc.). The previous grammar threw bare
/// <see cref="Exception"/>s, which forced exactly that over-broad catch shape on every call site.
/// </summary>
public sealed class TypeSpecParseException : Exception
{
    public TypeSpecParseException(string message)
        : base(message)
    {
    }

    public TypeSpecParseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
