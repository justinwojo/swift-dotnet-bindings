// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.IO;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// Pins the A3 stderr-routing contract on the console logger factory:
    /// <see cref="BindingsGenerator.CreateLoggerFactory"/> configures
    /// <c>LogToStandardErrorThreshold = LogLevel.Error</c>, so Error/Critical diagnostics land on
    /// stderr while Information/Warning stay on stdout. That is what keeps a <c>LogError</c> on the
    /// <c>--resolve-auto-deps</c> failure path off the frozen <c>PROJREF|</c>/<c>WARN|</c> stdout
    /// grammar the SDK captures via <c>ConsoleToMSBuild</c>.
    ///
    /// The <see cref="AutoDepResolverCliTests"/> grammar test cannot observe this: its stdout lines
    /// are written straight to <c>Console.Out</c> by <c>AutoDepResolver.Run</c>, never through the
    /// logger, so its "stderr carries no grammar line" assertion holds regardless of the threshold.
    /// This test drives the logger directly, so removing the threshold line (routing Error back to
    /// stdout) turns it red.
    /// </summary>
    [Collection("ConsoleCapture")]
    public class CreateLoggerFactoryTests
    {
        [Fact]
        public void CreateLoggerFactory_RoutesErrorToStdErr_AndInformationToStdOut()
        {
            const string errorMarker = "SBX_ERROR_ROUTES_TO_STDERR";
            const string infoMarker = "SBX_INFO_ROUTES_TO_STDOUT";

            string stdout;
            string stderr;

            // The console logger captures System.Console.Out/Error when its provider is constructed
            // (inside CreateLoggerFactory), so the capture must be open before the factory is
            // created and stay open until it has flushed. Its output thread is started inside this
            // scope, which is what puts its writes in this capture's flow.
            using (var capture = ConsoleCapture.Begin())
            {
                // Verbosity 1 == Information minimum — the level the SDK Exec invokes the generator at.
                var factory = BindingsGenerator.CreateLoggerFactory(1);
                var logger = factory.CreateLogger("StdErrThresholdTest");
                logger.LogInformation(infoMarker);
                logger.LogError(errorMarker);
                // Disposing the factory completes the ConsoleLoggerProcessor queue and joins its output
                // thread; a non-blocking sink drains well within that join, so the captured text is
                // final on return.
                factory.Dispose();

                stdout = capture.Out;
                stderr = capture.Error;
            }

            // Error → stderr only.
            Assert.Contains(errorMarker, stderr);
            Assert.DoesNotContain(errorMarker, stdout);

            // Information → stdout only.
            Assert.Contains(infoMarker, stdout);
            Assert.DoesNotContain(infoMarker, stderr);
        }
    }
}
