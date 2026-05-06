// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace Swift;

/// <summary>
/// Marks a generated C# type whose Swift counterpart conforms to <c>Swift.Sendable</c>.
/// Instances of the type are safe to share across .NET threads — Swift guarantees
/// that no Sendable type can race on its own state under any concurrency boundary
/// (actor, <c>Task.detached</c>, GCD, etc.).
/// </summary>
/// <remarks>
/// .NET has no native equivalent of Swift's <c>Sendable</c> marker. This attribute is
/// purely informational: it surfaces Swift's thread-safety guarantee in the C# binding so
/// consumers can decide whether locking around the value is necessary. The attribute does
/// not change runtime behaviour and is not enforced by any analyzer today.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum,
    AllowMultiple = false, Inherited = false)]
public sealed class SwiftSendableAttribute : Attribute
{
}
