// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// Captures what the code under test writes to <see cref="Console"/>, scoped to the calling
    /// flow instead of the process.
    ///
    /// <para>Redirecting <see cref="Console.Out"/>/<see cref="Console.Error"/> for the duration of
    /// a test is process-global: it captures every other test writing to the console at that
    /// moment, and two overlapping redirects leave the console pointing wherever the last one
    /// restored. Both are unavoidable while the capture IS the redirect — an <c>xunit</c>
    /// collection can serialize the capturers but cannot reach the writers.</para>
    ///
    /// <para>So the console is redirected exactly once per process, permanently, to a writer that
    /// routes each write to the sink registered for the writing flow — and to the real console
    /// when the flow has none. Capturing then costs nothing globally: concurrent captures are
    /// independent, and a test that captures nothing keeps writing to the console it always had.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <para>Registration rides an <see cref="AsyncLocal{T}"/>, which is what makes a thread or task
    /// STARTED INSIDE a capture inherit it: <see cref="Thread.Start()"/> and the task schedulers
    /// capture the current <see cref="ExecutionContext"/>. The console logger's drain thread is
    /// created inside the capture that builds the logger factory and writes from there, so it is
    /// captured only because of that flow — starting such a thread under
    /// <see cref="ExecutionContext.SuppressFlow"/>, or handing the work to a pre-existing
    /// long-lived worker, would silently stop capturing its output.</para>
    ///
    /// <para>Inheritance runs one way only, so a thread can outlive the capture that spawned it —
    /// the console logger's drain thread again, since a factory nobody disposes keeps it running for
    /// the process lifetime. A disposed capture therefore stops being a destination: its late
    /// writes go to the real console rather than into a buffer no one will read.</para>
    ///
    /// <para>Nesting is supported: a capture restores its enclosing one on dispose.</para>
    /// </remarks>
    public sealed class ConsoleCapture : IDisposable
    {
        private static readonly AsyncLocal<ConsoleCapture?> ActiveCapture = new();
        private static readonly object InstallLock = new();
        private static bool _installed;

        private readonly SinkWriter _out = new();
        private readonly SinkWriter _error = new();
        private readonly ConsoleCapture? _enclosing;

        // Read by the router from threads other than the disposing one.
        private volatile bool _disposed;
        private string? _finalOut;
        private string? _finalError;

        private ConsoleCapture(ConsoleCapture? enclosing) => _enclosing = enclosing;

        /// <summary>
        /// Starts capturing console output written by this flow. Dispose the result to stop.
        /// </summary>
        public static ConsoleCapture Begin()
        {
            InstallRouters();
            var capture = new ConsoleCapture(ActiveCapture.Value);
            ActiveCapture.Value = capture;
            return capture;
        }

        /// <summary>
        /// Everything this flow wrote to stdout while the capture was open. Stays readable after
        /// disposal, and stops growing at it.
        /// </summary>
        public string Out => _finalOut ?? _out.Text;

        /// <summary>
        /// Everything this flow wrote to stderr while the capture was open. Stays readable after
        /// disposal, and stops growing at it.
        /// </summary>
        public string Error => _finalError ?? _error.Text;

        public void Dispose()
        {
            if (_disposed)
                return;
            // Frozen before the flag flips, so no reader can see a disposed capture with no text.
            _finalOut = _out.DrainAndRelease();
            _finalError = _error.DrainAndRelease();
            _disposed = true;
            ActiveCapture.Value = _enclosing;
        }

        private static void InstallRouters()
        {
            lock (InstallLock)
            {
                if (_installed)
                    return;
                // Captured before the swap so the routers' no-capture path reaches the real
                // console rather than recursing back through Console.Out.
                var realOut = Console.Out;
                var realError = Console.Error;
                Console.SetOut(new RoutingWriter(realOut, capture => capture._out));
                Console.SetError(new RoutingWriter(realError, capture => capture._error));
                _installed = true;
            }
        }

        /// <summary>
        /// The permanently-installed <see cref="Console"/> writer. Holds no capture state of its
        /// own — it resolves the destination per write, so installing it is not an act anyone has
        /// to undo.
        /// </summary>
        /// <remarks>
        /// Every write and flush entry point is overridden rather than left to the base class, so a
        /// caller cannot reach one destination by picking an overload the router does not know
        /// about while its neighbours reach the other.
        /// </remarks>
        private sealed class RoutingWriter : TextWriter
        {
            private readonly TextWriter _console;
            private readonly Func<ConsoleCapture, TextWriter> _sinkOf;

            internal RoutingWriter(TextWriter console, Func<ConsoleCapture, TextWriter> sinkOf)
            {
                _console = console;
                _sinkOf = sinkOf;
            }

            public override Encoding Encoding => _console.Encoding;

            private TextWriter Target =>
                ActiveCapture.Value is { _disposed: false } capture ? _sinkOf(capture) : _console;

            public override void Write(char value) => Target.Write(value);

            public override void Write(string? value) => Target.Write(value);

            public override void Write(char[]? buffer) => Target.Write(buffer);

            public override void Write(char[] buffer, int index, int count) =>
                Target.Write(buffer, index, count);

            public override void Write(ReadOnlySpan<char> buffer) => Target.Write(buffer);

            public override void WriteLine() => Target.WriteLine();

            public override void WriteLine(char value) => Target.WriteLine(value);

            public override void WriteLine(string? value) => Target.WriteLine(value);

            public override void WriteLine(char[]? buffer) => Target.WriteLine(buffer);

            public override void WriteLine(char[] buffer, int index, int count) =>
                Target.WriteLine(buffer, index, count);

            public override void WriteLine(ReadOnlySpan<char> buffer) => Target.WriteLine(buffer);

            public override Task WriteAsync(char value) => Target.WriteAsync(value);

            public override Task WriteAsync(string? value) => Target.WriteAsync(value);

            public override Task WriteAsync(char[] buffer, int index, int count) =>
                Target.WriteAsync(buffer, index, count);

            public override Task WriteAsync(
                ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default) =>
                Target.WriteAsync(buffer, cancellationToken);

            public override Task WriteLineAsync() => Target.WriteLineAsync();

            public override Task WriteLineAsync(char value) => Target.WriteLineAsync(value);

            public override Task WriteLineAsync(string? value) => Target.WriteLineAsync(value);

            public override Task WriteLineAsync(char[] buffer, int index, int count) =>
                Target.WriteLineAsync(buffer, index, count);

            public override Task WriteLineAsync(
                ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default) =>
                Target.WriteLineAsync(buffer, cancellationToken);

            public override void Flush() => Target.Flush();

            public override Task FlushAsync() => Target.FlushAsync();

            public override Task FlushAsync(CancellationToken cancellationToken) =>
                Target.FlushAsync(cancellationToken);
        }

        /// <summary>
        /// A capture's destination. Locked because a capture's own flow can include threads that
        /// write while the test thread reads — the console logger drains its queue on a thread of
        /// its own.
        /// </summary>
        private sealed class SinkWriter : TextWriter
        {
            private readonly StringBuilder _text = new();

            public override Encoding Encoding => Encoding.UTF8;

            internal string Text
            {
                get { lock (_text) return _text.ToString(); }
            }

            /// <summary>
            /// Returns everything written so far and gives up the buffer, so a capture the test is
            /// finished with does not hold its output for the rest of the process.
            /// </summary>
            internal string DrainAndRelease()
            {
                lock (_text)
                {
                    var text = _text.ToString();
                    _text.Clear();
                    _text.Capacity = 0;
                    return text;
                }
            }

            public override void Write(char value)
            {
                lock (_text) _text.Append(value);
            }

            public override void Write(string? value)
            {
                lock (_text) _text.Append(value);
            }

            public override void Write(char[]? buffer)
            {
                if (buffer is null)
                    return;
                lock (_text) _text.Append(buffer);
            }

            public override void Write(char[] buffer, int index, int count)
            {
                lock (_text) _text.Append(buffer, index, count);
            }

            public override void Write(ReadOnlySpan<char> buffer)
            {
                lock (_text) _text.Append(buffer);
            }

            public override void WriteLine()
            {
                lock (_text) _text.Append(CoreNewLine);
            }

            public override void WriteLine(char value)
            {
                lock (_text)
                {
                    _text.Append(value);
                    _text.Append(CoreNewLine);
                }
            }

            public override void WriteLine(string? value)
            {
                lock (_text)
                {
                    _text.Append(value);
                    _text.Append(CoreNewLine);
                }
            }

            public override void WriteLine(char[]? buffer)
            {
                lock (_text)
                {
                    if (buffer is not null)
                        _text.Append(buffer);
                    _text.Append(CoreNewLine);
                }
            }

            public override void WriteLine(char[] buffer, int index, int count)
            {
                lock (_text)
                {
                    _text.Append(buffer, index, count);
                    _text.Append(CoreNewLine);
                }
            }

            public override void WriteLine(ReadOnlySpan<char> buffer)
            {
                lock (_text)
                {
                    _text.Append(buffer);
                    _text.Append(CoreNewLine);
                }
            }

            // The async overloads complete synchronously: appending to a StringBuilder cannot
            // block, and the base class would otherwise schedule the work, reordering these writes
            // against the synchronous ones interleaved with them.
            public override Task WriteAsync(char value)
            {
                Write(value);
                return Task.CompletedTask;
            }

            public override Task WriteAsync(string? value)
            {
                Write(value);
                return Task.CompletedTask;
            }

            public override Task WriteAsync(char[] buffer, int index, int count)
            {
                Write(buffer, index, count);
                return Task.CompletedTask;
            }

            public override Task WriteAsync(
                ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
            {
                if (cancellationToken.IsCancellationRequested)
                    return Task.FromCanceled(cancellationToken);
                Write(buffer.Span);
                return Task.CompletedTask;
            }

            public override Task WriteLineAsync()
            {
                WriteLine();
                return Task.CompletedTask;
            }

            public override Task WriteLineAsync(char value)
            {
                WriteLine(value);
                return Task.CompletedTask;
            }

            public override Task WriteLineAsync(string? value)
            {
                WriteLine(value);
                return Task.CompletedTask;
            }

            public override Task WriteLineAsync(char[] buffer, int index, int count)
            {
                WriteLine(buffer, index, count);
                return Task.CompletedTask;
            }

            public override Task WriteLineAsync(
                ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
            {
                if (cancellationToken.IsCancellationRequested)
                    return Task.FromCanceled(cancellationToken);
                WriteLine(buffer.Span);
                return Task.CompletedTask;
            }
        }
    }
}
