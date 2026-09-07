// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Pins the binary shape of the <see cref="ExistentialContainerFactory"/> entry points generated
/// bindings call. A binding compiled against an earlier runtime references these methods by their
/// exact parameter lists; adding an optional parameter to an existing method replaces that member,
/// and every already-shipped binding then fails with a missing-method error at its first
/// existential argument. New parameters must arrive as sibling overloads, and the original shapes
/// must stay callable.
/// </summary>
public class ExistentialContainerFactoryApiTests
{
    private static MethodInfo[] CreateOwnedExistential1Overloads()
        => typeof(ExistentialContainerFactory)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == nameof(ExistentialContainerFactory.CreateOwnedExistential1))
            .ToArray();

    /// <summary>
    /// True when <paramref name="parameter"/> is the <c>Func&lt;TProtocol, ISwiftExistentialConvertible&lt;ExistentialContainer1&gt;&gt;</c>
    /// wrap-fallback of its method. Compared structurally rather than by constructing the closed
    /// type, which keeps the test AOT-analyzable.
    /// </summary>
    private static bool IsWrapFallback(MethodInfo method, ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(Func<,>))
            return false;
        var args = type.GetGenericArguments();
        return args[0] == method.GetGenericArguments()[0]
            && args[1] == typeof(ISwiftExistentialConvertible<ExistentialContainer1>);
    }

    [Fact]
    public void LegacyOneParameterOverload_IsStillDeclared()
    {
        var legacy = CreateOwnedExistential1Overloads()
            .Where(m => m.GetParameters().Length == 1)
            .ToArray();

        var single = Assert.Single(legacy);
        Assert.True(single.GetParameters()[0].ParameterType.IsGenericMethodParameter);
    }

    [Fact]
    public void LegacyValueAndFallbackOverload_IsStillDeclared()
    {
        var legacy = CreateOwnedExistential1Overloads()
            .Where(m => m.GetParameters().Length == 2)
            .Where(m => IsWrapFallback(m, m.GetParameters()[1]))
            .ToArray();

        var single = Assert.Single(legacy);
        Assert.True(single.GetParameters()[0].ParameterType.IsGenericMethodParameter);
    }

    [Fact]
    public void ClassBoundCarrierFlag_IsNeverOptional()
    {
        var flagged = CreateOwnedExistential1Overloads()
            .Where(m => m.GetParameters().Any(p => p.ParameterType == typeof(bool)))
            .ToArray();

        Assert.NotEmpty(flagged);
        foreach (var method in flagged)
        {
            var flag = method.GetParameters().Single(p => p.ParameterType == typeof(bool));
            Assert.False(flag.IsOptional, $"{method} declares its class-bound flag as optional; an optional parameter on an existing shape is a binary break for already-compiled bindings — add a sibling overload instead.");
        }
    }

    [Fact]
    public void EveryOverloadShape_IsAccountedFor()
    {
        var shapes = CreateOwnedExistential1Overloads()
            .Select(m => string.Join(",", m.GetParameters().Select(p =>
                p.ParameterType.IsGenericMethodParameter ? "T"
                : p.ParameterType == typeof(bool) ? "bool"
                : IsWrapFallback(m, p) ? "wrap"
                : p.ParameterType.Name)))
            .OrderBy(s => s)
            .ToArray();

        Assert.Equal(new[] { "T", "T,bool", "T,wrap", "T,wrap,bool" }, shapes);
    }
}
