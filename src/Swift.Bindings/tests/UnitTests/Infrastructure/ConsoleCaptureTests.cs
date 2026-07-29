// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Threading;
using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// Pins the isolation <see cref="ConsoleCapture"/> owes its callers.
    ///
    /// <para>Tests that assert on a CLI entry point's console output share one process with tests
    /// that write to the console for unrelated reasons (the linked BindingTests
    /// <c>TestLogger</c>, for one, writes <c>[PASS] …</c> lines). Redirecting the process-global
    /// <see cref="Console"/> makes those two populations collide: a capture swallows the other
    /// test's output and asserts on it, and whichever capture restores last leaves the console
    /// pointing somewhere the still-open capture cannot see. An <c>xunit</c> collection can
    /// serialize the capturers but has no way to reach the writers, so the isolation has to be
    /// structural.</para>
    /// </summary>
    public class ConsoleCaptureTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        [Fact]
        public void Capture_ExcludesWritesFromAConcurrentFlow()
        {
            const string foreignMarker = "SBX_FOREIGN_CONSOLE_LINE";
            const string ownMarker = "SBX_OWN_CONSOLE_LINE";

            using var captureOpen = new ManualResetEventSlim();
            using var foreignDone = new ManualResetEventSlim();

            // Started BEFORE the capture opens, so it models what it has to model: a writer whose
            // flow the capture never entered. A thread started inside the capture (the console
            // logger's own output thread, say) is part of that flow by construction and is
            // expected to be captured.
            var foreign = new Thread(() =>
            {
                captureOpen.Wait(Timeout);
                for (var i = 0; i < 10; i++)
                    Console.WriteLine($"{foreignMarker} {i}");
                foreignDone.Set();
            })
            { IsBackground = true };
            foreign.Start();

            string captured;
            using (var capture = ConsoleCapture.Begin())
            {
                captureOpen.Set();
                Assert.True(foreignDone.Wait(Timeout));
                Console.WriteLine(ownMarker);
                captured = capture.Out;
            }

            foreign.Join(Timeout);

            Assert.Contains(ownMarker, captured);
            Assert.DoesNotContain(foreignMarker, captured);
        }

        [Fact]
        public void ConcurrentCaptures_EachSeeOnlyTheirOwnWrites()
        {
            const string firstMarker = "SBX_FIRST_CAPTURE_LINE";
            const string secondMarker = "SBX_SECOND_CAPTURE_LINE";

            using var bothOpen = new Barrier(2);
            using var bothWritten = new Barrier(2);

            string? first = null;
            string? second = null;

            void Run(string marker, Action<string> record)
            {
                using var capture = ConsoleCapture.Begin();
                bothOpen.SignalAndWait(Timeout);
                Console.WriteLine(marker);
                bothWritten.SignalAndWait(Timeout);
                record(capture.Out);
            }

            var a = new Thread(() => Run(firstMarker, text => first = text)) { IsBackground = true };
            var b = new Thread(() => Run(secondMarker, text => second = text)) { IsBackground = true };
            a.Start();
            b.Start();
            Assert.True(a.Join(Timeout));
            Assert.True(b.Join(Timeout));

            Assert.Contains(firstMarker, first);
            Assert.DoesNotContain(secondMarker, first);
            Assert.Contains(secondMarker, second);
            Assert.DoesNotContain(firstMarker, second);
        }

        [Fact]
        public void Capture_SeparatesStdoutFromStderr()
        {
            const string outMarker = "SBX_STDOUT_LINE";
            const string errorMarker = "SBX_STDERR_LINE";

            using var capture = ConsoleCapture.Begin();
            Console.Out.WriteLine(outMarker);
            Console.Error.WriteLine(errorMarker);

            Assert.Contains(outMarker, capture.Out);
            Assert.DoesNotContain(errorMarker, capture.Out);
            Assert.Contains(errorMarker, capture.Error);
            Assert.DoesNotContain(outMarker, capture.Error);
        }
    }
}
