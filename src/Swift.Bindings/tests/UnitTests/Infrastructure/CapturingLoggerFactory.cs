// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration.Tests;

/// <summary>
/// An <see cref="ILoggerFactory"/> whose loggers record every formatted message, so a test can
/// assert on diagnostics a component emits instead of on the state it returns. Every category
/// shares one message list, because callers under test typically span several logger categories
/// (e.g. the TBD parser and the demangling results built from it).
/// </summary>
internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    private readonly List<(LogLevel Level, string Message)> _entries = new();

    /// <summary>Every message logged through this factory, in order.</summary>
    public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries;

    /// <summary>Messages logged at <see cref="LogLevel.Warning"/> or above.</summary>
    public IEnumerable<string> Warnings =>
        _entries.Where(e => e.Level >= LogLevel.Warning).Select(e => e.Message);

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(_entries);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly List<(LogLevel Level, string Message)> _entries;

        internal CapturingLogger(List<(LogLevel Level, string Message)> entries) => _entries = entries;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
