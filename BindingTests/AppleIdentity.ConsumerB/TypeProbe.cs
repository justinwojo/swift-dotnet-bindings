// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Swift.Runtime;

// CA1416: see ConsumerA/TypeProbe.cs for rationale.
#pragma warning disable CA1416

namespace AppleIdentity.ConsumerB;

/// <summary>
/// Mirror of AppleIdentity.ConsumerA.TypeProbe. See ConsumerA/TypeProbe.cs.
/// </summary>
public static class TypeProbe
{
    public static System.Type GetLanguageType() => typeof(Swift.Foundation.Locale.Language);

    public static TypeMetadata GetLanguageMetadata()
        => SwiftObjectHelper<Swift.Foundation.Locale.Language>.GetTypeMetadata();
}
