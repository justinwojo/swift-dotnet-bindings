// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace RuntimeTestsApp.Infrastructure;

/// <summary>
/// Marks a test class as crash-prone (e.g., triggers Mono JIT assertion).
/// Classes with this attribute are sorted last in execution order so that
/// if they crash the process, all safe tests have already completed.
/// Use --safe-only to skip crash-risk classes entirely.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class CrashRiskAttribute : Attribute
{
    public string? Reason { get; }

    public CrashRiskAttribute() { }

    public CrashRiskAttribute(string reason)
    {
        Reason = reason;
    }
}
