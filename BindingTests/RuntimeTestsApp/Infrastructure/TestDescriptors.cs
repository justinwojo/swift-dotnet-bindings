// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace RuntimeTestsApp.Infrastructure;

/// <summary>
/// Describes a test class discovered at compile time by the source generator.
/// Replaces reflection-based discovery (Assembly.GetTypes, GetCustomAttribute, Activator.CreateInstance).
/// </summary>
public record TestClassDescriptor(
    string Name,
    Func<TestResults, TestBase> Factory,
    string? SkipReason,
    string? SkipOnSimulator,
    string? SkipOnDevice,
    IReadOnlyList<TestMethodDescriptor> Methods);

/// <summary>
/// Describes a test method discovered at compile time by the source generator.
/// Replaces reflection-based invocation (GetMethods, GetCustomAttribute, method.Invoke).
/// The Invoker delegate is normalized to Func&lt;TestBase, ValueTask&gt; for both sync and async methods.
/// </summary>
public record TestMethodDescriptor(
    string Name,
    Func<TestBase, ValueTask> Invoker,
    string? Skip,
    string? SkipOnSim,
    string? SkipOnDevice,
    string? SkipOnCatalystX64 = null,
    string? SkipOnMonoJit = null);
