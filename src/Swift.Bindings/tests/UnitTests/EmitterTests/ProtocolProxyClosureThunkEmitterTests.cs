// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for the C# side of the protocol-proxy async-closure invoke thunks
/// (<see cref="ProtocolProxyEmitter.EmitAsyncClosureInvokeThunkHelper"/>).
///
/// The completion callback is an <c>[UnmanagedCallersOnly]</c> entry point Swift calls
/// directly, which pins the caller's TaskCompletionSource behind a GCHandle. Two properties
/// are load-bearing and are what these tests pin:
///   1. No managed exception may unwind back across the native boundary — that is undefined
///      behaviour, and this callback has no Swift error channel to fault instead, so the
///      shared UCO envelope's controlled FailFast is the only safe outcome.
///   2. The GCHandle must be released on EVERY path out of the callback — including the
///      defensive Target-mismatch path and a throwing resume — or the pinned TCS leaks.
/// </summary>
public class ProtocolProxyClosureThunkEmitterTests
{
    /// <summary>
    /// Emits the async-closure invoke thunk helper for the shape the emitter's gate
    /// guarantees: no closure arguments, Swift.Int32 return, non-throwing.
    /// </summary>
    private static string EmitThunk()
    {
        var typeDatabase = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDatabase);
        var closureTypeSpec = new ClosureTypeSpec(null, new NamedTypeSpec("Swift.Int32"))
        {
            IsAsync = true,
            Throws = false
        };

        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);

        ProtocolProxyEmitter.EmitAsyncClosureInvokeThunkHelper(
            csWriter, closureTypeSpec, closureHandler,
            helperMethodName: "_AsyncClosureInv_TEST",
            entryPointName: "sbw_proto_closure_invoke_TEST",
            libraryName: "TestLib");

        return output.ToString();
    }

    /// <summary>
    /// Extracts the body of the completion callback so assertions are scoped to it rather
    /// than to the whole emitted fragment (which also carries the DllImport and the invoker
    /// class, both of which legitimately mention the handle).
    /// </summary>
    private static string CompletionThunkBody(string emitted)
    {
        var start = emitted.IndexOf("_Completion(nint tcsHandle", System.StringComparison.Ordinal);
        Assert.True(start >= 0, "Expected a completion callback in the emitted thunk helper.");
        var end = emitted.IndexOf("private sealed class", start, System.StringComparison.Ordinal);
        return end >= 0 ? emitted.Substring(start, end - start) : emitted.Substring(start);
    }

    [Fact]
    public void CompletionThunk_IsGuardedByUcoFailFastEnvelope()
    {
        var body = CompletionThunkBody(EmitThunk());

        // A managed exception escaping this [UnmanagedCallersOnly] body would unwind into
        // Swift (undefined behaviour). The shared envelope converts it to a controlled,
        // attributable FailFast instead.
        Assert.Contains("catch (global::System.Exception", body);
        Assert.Contains("FailFastUnhandledClosureException", body);
    }

    [Fact]
    public void CompletionThunk_FreesHandleOnTargetMismatchPath()
    {
        var body = CompletionThunkBody(EmitThunk());

        // The Target type-test is defensive; when it does NOT match there is no TCS to resume,
        // but the handle this callback owns must still be released. Assert the free is not
        // lexically governed by the type-test — i.e. the type-test's consequent is the resume
        // alone, and the free sits outside it.
        var targetTest = body.IndexOf("_gch.Target is", System.StringComparison.Ordinal);
        var free = body.IndexOf("_gch.Free()", System.StringComparison.Ordinal);
        Assert.True(targetTest >= 0, "Expected the defensive Target type-test.");
        Assert.True(free > targetTest,
            "Expected the handle free to follow the Target type-test, not be nested before it.");

        // The free must run for the mismatch path AND for a throwing resume, so it belongs to a
        // finally rather than to the type-test's fall-through.
        Assert.Matches(new Regex(@"finally\s*\{\s*_gch\.Free\(\);\s*\}", RegexOptions.Singleline), body);
    }

    [Fact]
    public void CompletionThunk_ResumesTcsOnTargetMatch()
    {
        var body = CompletionThunkBody(EmitThunk());

        // The happy path still publishes the result — the guard must not have swallowed it.
        Assert.Contains("TrySetResult(result)", body);
    }

    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int32"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "int"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int32"),
                MetadataAccessor = "$ss5Int32VMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);
        return typeDatabase;
    }
}
