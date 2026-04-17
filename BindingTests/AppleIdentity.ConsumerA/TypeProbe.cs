// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Swift.Runtime;

// CA1416: callers in RuntimeTestsApp guard the actual invocation with
// OperatingSystem.IsIOSVersionAtLeast(16); the analyzer doesn't see that the
// guard is the only call site, so suppress here at the probe boundary.
#pragma warning disable CA1416

namespace AppleIdentity.ConsumerA;

/// <summary>
/// Session 6 / M8 probe. Exposes a stable type handle + metadata handle for a
/// SwiftBindings.Apple-owned supplement type so RuntimeTestsApp can compare
/// against the mirror probe in AppleIdentity.ConsumerB and assert both
/// assemblies resolve to the exact same System.Type and Swift TypeMetadata.
/// </summary>
public static class TypeProbe
{
    public static System.Type GetLanguageType() => typeof(Swift.Foundation.Locale.Language);

    public static TypeMetadata GetLanguageMetadata()
        => SwiftObjectHelper<Swift.Foundation.Locale.Language>.GetTypeMetadata();
}
