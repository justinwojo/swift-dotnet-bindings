// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.IO;
using System.Text;
using System.Threading;

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
    /// Registration rides an <see cref="AsyncLocal{T}"/>, so a thread or task started inside a
    /// capture inherits it (the console logger's own output thread depends on that) while a flow
    /// that started outside stays out.</para>
    /// </summary>
    /// <remarks>
    /// Nesting is supported: a capture restores its enclosing one on dispose.
    /// </remarks>
    public sealed class ConsoleCapture : IDisposable
    {
        private static readonly AsyncLocal<ConsoleCapture?> ActiveCapture = new();
        private static readonly object InstallLock = new();
        private static bool _installed;

        private readonly SinkWriter _out = new();
        private readonly SinkWriter _error = new();
        private readonly ConsoleCapture? _enclosing;
        private bool _disposed;

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

        /// <summary>Everything this flow has written to stdout since the capture began.</summary>
        public string Out => _out.Text;

        /// <summary>Everything this flow has written to stderr since the capture began.</summary>
        public string Error => _error.Text;

        public void Dispose()
        {
            if (_disposed)
                return;
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
                ActiveCapture.Value is { } capture ? _sinkOf(capture) : _console;

            public override void Write(char value) => Target.Write(value);

            public override void Write(string? value) => Target.Write(value);

            public override void Write(char[] buffer, int index, int count) =>
                Target.Write(buffer, index, count);

            public override void WriteLine() => Target.WriteLine();

            public override void WriteLine(string? value) => Target.WriteLine(value);

            public override void Flush() => Target.Flush();
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

            public override void Write(char value)
            {
                lock (_text) _text.Append(value);
            }

            public override void Write(string? value)
            {
                lock (_text) _text.Append(value);
            }

            public override void Write(char[] buffer, int index, int count)
            {
                lock (_text) _text.Append(buffer, index, count);
            }

            public override void WriteLine()
            {
                lock (_text) _text.Append(CoreNewLine);
            }

            public override void WriteLine(string? value)
            {
                lock (_text)
                {
                    _text.Append(value);
                    _text.Append(CoreNewLine);
                }
            }
        }
    }
}
