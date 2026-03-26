// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging;
using Xunit;

namespace BindingsGeneration.Tests;

public class SwiftInterfaceErrorRecoveryTests
{
    [Fact]
    public void TryParseSwiftInterface_SuccessfulParse_ReturnsResult()
    {
        var logger = new CapturingLogger();
        int failures = 0;

        var result = BindingsGenerator.TryParseSwiftInterface(
            "test data",
            () => new HashSet<string> { "item1", "item2" },
            () => new HashSet<string>(),
            logger, ref failures);

        Assert.Equal(2, result.Count);
        Assert.Contains("item1", result);
        Assert.Equal(0, failures);
        Assert.Empty(logger.Messages);
    }

    [Fact]
    public void TryParseSwiftInterface_ThrowingParser_ReturnsFallback()
    {
        var logger = new CapturingLogger();
        int failures = 0;

        var result = BindingsGenerator.TryParseSwiftInterface(
            "broken parser",
            () => throw new InvalidOperationException("parse failed"),
            () => new HashSet<string>(),
            logger, ref failures);

        Assert.Empty(result);
        Assert.Equal(1, failures);
        Assert.Contains(logger.Messages, m => m.Contains("Swiftinterface parsing failed") && m.Contains("broken parser"));
    }

    [Fact]
    public void TryParseSwiftInterface_MultipleFails_AccumulatesFailureCount()
    {
        var logger = new CapturingLogger();
        int failures = 0;

        BindingsGenerator.TryParseSwiftInterface(
            "parser 1",
            () => throw new Exception("fail 1"),
            () => new HashSet<string>(),
            logger, ref failures);

        BindingsGenerator.TryParseSwiftInterface(
            "parser 2",
            () => throw new Exception("fail 2"),
            () => new Dictionary<string, string>(),
            logger, ref failures);

        Assert.Equal(2, failures);
    }

    [Fact]
    public void TryParseSwiftInterface_NullReferenceException_RecoveredGracefully()
    {
        var logger = new CapturingLogger();
        int failures = 0;

        var result = BindingsGenerator.TryParseSwiftInterface(
            "null ref test",
            () =>
            {
                string? s = null;
                return new HashSet<string> { s!.Length.ToString() }; // throws NullReferenceException
            },
            () => new HashSet<string>(),
            logger, ref failures);

        Assert.Empty(result);
        Assert.Equal(1, failures);
    }

    [Fact]
    public void TryParseSwiftInterface_TupleReturn_ReturnsFallbackOnError()
    {
        var logger = new CapturingLogger();
        int failures = 0;

        var (keys, names) = BindingsGenerator.TryParseSwiftInterface(
            "tuple test",
            () => throw new Exception("tuple fail"),
            () => (new HashSet<string>(), new HashSet<string>()),
            logger, ref failures);

        Assert.Empty(keys);
        Assert.Empty(names);
        Assert.Equal(1, failures);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
