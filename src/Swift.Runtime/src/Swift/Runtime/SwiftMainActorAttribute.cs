// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace Swift.Runtime;

/// <summary>
/// Marks a generated C# member or type whose Swift counterpart is isolated to the main
/// actor (<c>@MainActor</c>). Such declarations must be invoked on the platform main
/// thread; calling them off the main thread is unsupported in Swift.
/// </summary>
/// <remarks>
/// .NET has no native equivalent of Swift's actor isolation. This attribute is purely
/// informational: it surfaces the Swift <c>@MainActor</c> constraint in the binding so
/// consumers (and analyzers) can see the main-thread requirement that the compiled
/// library and the swiftinterface otherwise hide. In Debug builds the generated wrapper
/// additionally calls <see cref="MainActorGuard.AssertMainThread"/> at entry to catch an
/// off-main-thread call during development. The attribute does not change runtime
/// behaviour and is not enforced at runtime in Release builds.
/// </remarks>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum |
    AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Property,
    AllowMultiple = false, Inherited = false)]
public sealed class SwiftMainActorAttribute : Attribute
{
}
