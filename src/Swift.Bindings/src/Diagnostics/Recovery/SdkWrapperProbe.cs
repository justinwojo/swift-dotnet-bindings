// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// The wrapper compile the verify-recover loop grades in the SDK's generate pass. That pass renders the
/// wrapper but does not ship it (<c>--skip-wrapper-compilation</c>; the SDK's <c>_CompileSwiftWrapper</c>
/// target compiles it later), so the probe compiles a copy of the rendered sources in a scratch
/// directory and deletes it afterwards. The loop still withdraws and re-renders in the real output
/// directory; only the probe's build products stay out of it.
/// </summary>
public static class SdkWrapperProbe
{
    /// <summary>
    /// Copies the sources a wrapper compile reads from <paramref name="request"/>'s output directory —
    /// the wrapper Swift files and the native thunk assembly — into a fresh scratch directory, runs
    /// <paramref name="compile"/> there, and removes the directory. Diagnostics name files by file name,
    /// which the copy keeps, so attribution is the same as for an in-place compile.
    /// </summary>
    public static WrapperCompileDiagnostics Run(
        WrapperRecoveryCompileRequest request,
        Func<WrapperRecoveryCompileRequest, string, WrapperCompileDiagnostics> compile,
        ILogger logger)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "swiftbindings-wrapper-probe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            foreach (var source in ProbeSources(request.OutputDirectory))
                File.Copy(source, Path.Combine(scratch, Path.GetFileName(source)));
            return compile(request, scratch);
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Could not remove the wrapper probe directory {Dir}: {Message}", scratch, ex.Message);
            }
        }
    }

    /// <summary>
    /// The files a wrapper compile collects from its directory: the wrapper Swift sources (the SwiftUI
    /// bridge compiles separately and is left out) and every architecture's thunk assembly.
    /// </summary>
    internal static IEnumerable<string> ProbeSources(string outputDirectory)
    {
        if (!Directory.Exists(outputDirectory))
            return [];
        return SwiftWrapperCompiler.CollectSwiftFiles(outputDirectory)
            .Concat(Directory.GetFiles(outputDirectory, "*.s"));
    }
}
