// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Behavioral guard for the <c>MarkEmitted()</c> mutators on <see cref="MethodDecl"/> and
/// <see cref="PropertyDecl"/> (AF05 Target B). The flag starts false and the single mutator sets
/// it true; this complements the static single-writer pin in
/// <c>WasEmittedAssignmentCountTests</c> (which forbids inline <c>WasEmitted = true</c> writes
/// elsewhere) by asserting the mutator's actual effect.
/// </summary>
public class MarkEmittedTests
{
    [Fact]
    public void MethodDecl_WasEmitted_DefaultsFalse()
    {
        var method = TestModelFactory.CreateMethod("doWork");
        Assert.False(method.WasEmitted);
    }

    [Fact]
    public void MethodDecl_MarkEmitted_SetsWasEmittedTrue()
    {
        var method = TestModelFactory.CreateMethod("doWork");
        method.MarkEmitted();
        Assert.True(method.WasEmitted);
    }

    [Fact]
    public void PropertyDecl_WasEmitted_DefaultsFalse()
    {
        var property = TestModelFactory.CreateProperty("State", parent: null);
        Assert.False(property.WasEmitted);
    }

    [Fact]
    public void PropertyDecl_MarkEmitted_SetsWasEmittedTrue()
    {
        var property = TestModelFactory.CreateProperty("State", parent: null);
        property.MarkEmitted();
        Assert.True(property.WasEmitted);
    }
}
