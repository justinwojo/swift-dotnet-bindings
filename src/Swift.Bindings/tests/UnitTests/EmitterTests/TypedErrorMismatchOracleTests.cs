// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Runs the emitted Swift catch and managed receiver across a native C callback.
/// The model deliberately claims a different typed error than Swift actually throws.
/// Materialization/retain/free stubs reject any payload work on this nil-wire path.
/// Ordinary BindingTests separately validate correctly typed payloads with Swift.Runtime.
/// </summary>
public class TypedErrorMismatchOracleTests
{
    [SkippableTheory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test-only dynamically compiled receiver, retained in full.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Test-only dynamically compiled receiver, retained in full.")]
    public void MismatchedTypedError_CompletesUntypedAndReleasesNativeError(bool classError, bool useHolder, bool cancellation)
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "Native Swift oracle requires macOS and Xcode.");
        var (cs, swift) = TypedThrowsEmitterTests.GenerateThrowingMethod(true, true, classError: classError);
        var catchMatch = Regex.Match(swift,
            @"let _isCancelled: Int32 = .*?errorCallback\(nil, 0, _msgPtr, _isCancelled, _sbwTask, 0\)\s*\}\s*\}",
            RegexOptions.Singleline);
        Assert.True(catchMatch.Success, "Generated Swift catch must include the nil fallback.");
        var callback = CSharpSyntaxTree.ParseText("class Generated {" + cs + "}").GetRoot()
            .DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.ParameterList.Parameters.FirstOrDefault()?.Identifier.Text == "errorPtr" &&
                m.ParameterList.Parameters.LastOrDefault()?.Identifier.Text == "errorTypeId");

        var directory = Path.Combine(Path.GetTempPath(), "typed-error-wire-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var swiftPath = Path.Combine(directory, "Mismatch.swift");
            var libraryPath = Path.Combine(directory, "libMismatch.dylib");
            File.WriteAllText(swiftPath, $$"""
                import Foundation
                enum TestModule {
                    {{(classError ? "final class ParseError: Error {}" : "enum ParseError: Error { case expected }")}}
                }
                private var deinitializations: Int32 = 0
                final class ActualError: Error, CustomStringConvertible {
                    var description: String { "dynamic mismatch sentinel" }
                    deinit { deinitializations += 1 }
                }
                @_cdecl("runTypedErrorMismatch")
                public func runTypedErrorMismatch(
                    _ errorCallback: @escaping @convention(c) (UnsafeRawPointer?, Int, UnsafePointer<CChar>?, Int32, Int64, Int32) -> Void,
                    _ _sbwTask: Int64
                ) -> Int32 {
                    deinitializations = 0
                    do { throw {{(cancellation ? "CancellationError()" : "ActualError()")}} }
                    catch {
                        {{catchMatch.Value}}
                    }
                    return deinitializations
                }
                """);
            RunCompiler(swiftPath, libraryPath);

            // Preserve the generated method verbatim, including its unmanaged entry,
            // holder/direct selection, exception containment and GCHandle finally.
            var source = $$"""
                using System;
                using System.Threading;
                using System.Threading.Tasks;
                using System.Runtime.InteropServices;
                using System.Runtime.CompilerServices;
                public class SwiftException : Exception {
                    public SwiftException(string message) : base(message) { }
                }
                public class SwiftException<T> : SwiftException {
                    public SwiftException(T value, string message) : base(message) { }
                }
                namespace TestModule { public class ParseError { } }
                public static class SwiftMarshal {
                    public static T MarshalFromSwift<T>(IntPtr value) => throw new Exception("Unexpected typed materialization");
                }
                namespace Swift.Runtime {
                    public sealed class SwiftAsyncCallHolder {
                        public object Tcs;
                        public int CleanupCount;
                        public void Cleanup() { CleanupCount++; }
                        public CancellationToken CaptureCancellationToken() => default;
                    }
                    public static class Arc {
                        public static void Release(IntPtr value) => throw new Exception("Unexpected retain release");
                    }
                }
                public static unsafe class Generated {
                    private static void SBW_Free(IntPtr value) => throw new Exception("Unexpected buffer free");
                    {{callback}}
                    public static object[] Run(string libraryPath, bool useHolder) {
                        var library = NativeLibrary.Load(libraryPath);
                        try {
                            var tcs = new TaskCompletionSource<int>();
                            var holder = new Swift.Runtime.SwiftAsyncCallHolder { Tcs = tcs };
                            object target = useHolder ? holder : tcs;
                            var weak = new WeakReference(target);
                            var handle = GCHandle.Alloc(target);
                            var driver = (delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<IntPtr, nint, IntPtr, int, IntPtr, int, void>, IntPtr, int>)NativeLibrary.GetExport(library, "runTypedErrorMismatch");
                            var nativeDeinit = driver(&{{callback.Identifier.Text}}, GCHandle.ToIntPtr(handle));
                            if (!tcs.Task.IsCompleted) throw new Exception("Receiver did not complete task");
                            var error = tcs.Task.Exception?.InnerException;
                            return new object[] { tcs.Task.IsCanceled ? "Canceled" : error.GetType().Name, error?.Message ?? "", holder.CleanupCount, nativeDeinit, weak };
                        }
                        finally { NativeLibrary.Free(library); }
                    }
                }
                """;
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
                .Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p));
            var compilation = CSharpCompilation.Create("TypedErrorWire" + Guid.NewGuid().ToString("N"),
                new[] { CSharpSyntaxTree.ParseText(source) }, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
            using var stream = new MemoryStream();
            var emitted = compilation.Emit(stream);
            Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
            var assembly = Assembly.Load(stream.ToArray());
            var run = assembly.GetType("Generated").GetMethod("Run").CreateDelegate<Func<string, bool, object[]>>();
            var result = run(libraryPath, useHolder);
            Assert.Equal(cancellation ? "Canceled" : "SwiftException", result[0]);
            Assert.Equal(cancellation ? "" : "dynamic mismatch sentinel", result[1]);
            Assert.Equal(useHolder ? 1 : 0, result[2]);
            Assert.Equal(cancellation ? 0 : 1, result[3]);
            // A leaked GCHandle would retain its target after the generated callback.
            var targetReference = (WeakReference)result[4];
            for (var attempt = 0; attempt < 3 && targetReference.IsAlive; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            Assert.False(targetReference.IsAlive, "Completion must free the callback GCHandle.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void RunCompiler(string swiftPath, string libraryPath)
    {
        var start = new ProcessStartInfo("/usr/bin/xcrun") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "swiftc", "-swift-version", "5", "-emit-library", swiftPath, "-o", libraryPath })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Swift mismatch oracle compilation exceeded one minute.");
        }
        Assert.True(process.ExitCode == 0, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }
}
