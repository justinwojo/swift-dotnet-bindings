// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for the unified UCO (UnmanagedCallersOnly) guard envelope (Finding 38): the single
/// source of truth for the try/catch that converts a managed exception escaping a native
/// callback into a controlled fault instead of undefined cross-boundary unwinding.
/// </summary>
public class UcoGuardEmitterTests
{
    private static (CSharpWriter writer, System.IO.StringWriter sink) NewWriter()
    {
        var sink = new System.IO.StringWriter();
        return (new CSharpWriter(sink), sink);
    }

    [Fact]
    public void EmitOpen_EmitsTryBlockAndIndents()
    {
        var (w, sink) = NewWriter();
        UcoGuardEmitter.EmitOpen(w);
        w.WriteLine("body();");

        var output = sink.ToString();
        Assert.Contains("try", output);
        Assert.Contains("{", output);
        // Body emitted after EmitOpen is indented one level deeper than the `try`.
        Assert.Contains("    body();", output);
    }

    [Fact]
    public void EmitFailFastCatch_DefaultShape_IsFullyQualifiedUcoVariant()
    {
        var (w, sink) = NewWriter();
        UcoGuardEmitter.EmitFailFastCatch(w);

        var output = sink.ToString();
        Assert.Contains("catch (global::System.Exception __uco_ex)", output);
        Assert.Contains(
            "global::Swift.Runtime.SwiftClosureMarshaller.FailFastUnhandledClosureException(__uco_ex);",
            output);
        Assert.Contains("throw;", output);
    }

    [Fact]
    public void EmitFailFastCatch_UnqualifiedVariant_MatchesClosureBridgeShimByteForByte()
    {
        // The closure-bridge sites historically emit an unqualified marshaller reference and the
        // __ex variable name. The shim must reproduce that exactly so consolidation churns nothing.
        var (direct, directSink) = NewWriter();
        UcoGuardEmitter.EmitFailFastCatch(direct, exceptionVar: "__ex", fullyQualified: false);

        var (shim, shimSink) = NewWriter();
        ClosureEmitter.EmitNonThrowingFailFastCatch(shim);

        Assert.Equal(directSink.ToString(), shimSink.ToString());
        // And it really is the unqualified shape (no global::Swift.Runtime prefix).
        Assert.Contains("SwiftClosureMarshaller.FailFastUnhandledClosureException(__ex);", shimSink.ToString());
        Assert.DoesNotContain("global::Swift.Runtime.SwiftClosureMarshaller", shimSink.ToString());
    }

    [Fact]
    public void EmitClose_FailFast_ClosesTryThenEmitsCatch()
    {
        var (w, sink) = NewWriter();
        UcoGuardEmitter.EmitOpen(w);
        w.WriteLine("body();");
        UcoGuardEmitter.EmitClose(w, UcoGuardEmitter.UcoFaultPolicy.FailFast);

        var output = sink.ToString();
        Assert.Contains("body();", output);
        Assert.Contains("catch (global::System.Exception __uco_ex)", output);
        Assert.Contains("FailFastUnhandledClosureException(__uco_ex);", output);
        // The catch follows the closing brace of the try, not before it.
        var braceIdx = output.IndexOf("}", System.StringComparison.Ordinal);
        var catchIdx = output.IndexOf("catch", System.StringComparison.Ordinal);
        Assert.True(braceIdx >= 0 && catchIdx > braceIdx, "catch must follow the try's closing brace");
    }

    [Fact]
    public void EmitClose_ResumeBoxError_StillThrowsUntilFinding37RoutesThroughHere()
    {
        // Finding 37 emits its own resume-scope envelope inline rather than routing through this
        // seam; no caller passes ResumeBoxError. The seam fails loudly rather than silently
        // emitting a catch-free body.
        var (w, _) = NewWriter();
        UcoGuardEmitter.EmitOpen(w);
        Assert.Throws<System.NotImplementedException>(
            () => UcoGuardEmitter.EmitClose(w, UcoGuardEmitter.UcoFaultPolicy.ResumeBoxError));
    }

    [Fact]
    public void EmitClose_StreamFault_EmitsCallerSuppliedCatchBody()
    {
        // StreamFault (Defect I) is wired: the envelope owns the try/catch, the AsyncStream emitter
        // supplies the catch statements (which stream variable, what to return).
        var (w, sink) = NewWriter();
        UcoGuardEmitter.EmitOpen(w);
        w.WriteLine("return stream.DeliverElement(p) ? (byte)1 : (byte)0;");
        UcoGuardEmitter.EmitClose(w, UcoGuardEmitter.UcoFaultPolicy.StreamFault,
            streamFaultBody: new[] { "stream.FaultChannel(__uco_ex);", "return 0;" });

        var output = sink.ToString();
        Assert.Contains("catch (global::System.Exception __uco_ex)", output);
        Assert.Contains("stream.FaultChannel(__uco_ex);", output);
        Assert.Contains("return 0;", output);
        // The catch follows the try's closing brace.
        var braceIdx = output.IndexOf("}", System.StringComparison.Ordinal);
        var catchIdx = output.IndexOf("catch", System.StringComparison.Ordinal);
        Assert.True(braceIdx >= 0 && catchIdx > braceIdx, "catch must follow the try's closing brace");
    }

    [Fact]
    public void EmitClose_StreamFault_WithoutBody_ThrowsArgumentException()
    {
        // StreamFault's catch statements are callback-specific, so the envelope refuses to emit a
        // catch-free (silent-swallow) body when the caller forgets to supply them.
        var (w, _) = NewWriter();
        UcoGuardEmitter.EmitOpen(w);
        Assert.Throws<System.ArgumentException>(
            () => UcoGuardEmitter.EmitClose(w, UcoGuardEmitter.UcoFaultPolicy.StreamFault));
    }

    [Fact]
    public void EmitCloseAsyncWitnessFailFast_EmitsTwoMemberNamedArms_OceBeforeException()
    {
        // Finding 36: the async protocol-requirement receiver is satisfied through the synchronously-
        // blocked reverse-dispatch slot (upstream Issue 1) with no Swift error channel, so any escape
        // is process-terminating. The close must name the member and split out the cancellation case,
        // emitting the OperationCanceledException arm BEFORE the general Exception arm (else CS0160).
        var (w, sink) = NewWriter();
        UcoGuardEmitter.EmitOpen(w);
        w.WriteLine("return MarshalToSwiftBuffer(result);");
        UcoGuardEmitter.EmitCloseAsyncWitnessFailFast(w, "AsyncRefineModifierBase.refineModify");

        var output = sink.ToString();
        // Both arms, both member-named, each ending in throw;.
        Assert.Contains(
            "catch (global::System.OperationCanceledException __uco_ex_oce)",
            output);
        Assert.Contains(
            "global::Swift.Runtime.SwiftClosureMarshaller.FailFastAsyncWitnessCancellation(__uco_ex_oce, \"AsyncRefineModifierBase.refineModify\");",
            output);
        Assert.Contains("catch (global::System.Exception __uco_ex)", output);
        Assert.Contains(
            "global::Swift.Runtime.SwiftClosureMarshaller.FailFastAsyncWitnessException(__uco_ex, \"AsyncRefineModifierBase.refineModify\");",
            output);

        // Most-derived-first ordering: OperationCanceledException catch precedes the Exception catch.
        var oceIdx = output.IndexOf("OperationCanceledException", System.StringComparison.Ordinal);
        var exIdx = output.IndexOf("catch (global::System.Exception", System.StringComparison.Ordinal);
        Assert.True(oceIdx >= 0 && exIdx > oceIdx,
            "OperationCanceledException arm must precede the general Exception arm");

        // The catches follow the try's closing brace, and each arm rethrows (CS0161 terminator).
        var braceIdx = output.IndexOf("}", System.StringComparison.Ordinal);
        Assert.True(braceIdx >= 0 && oceIdx > braceIdx, "catches must follow the try's closing brace");
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(output, @"throw;").Count);

        // It is NOT the anonymous sync FailFast — that is the regression this close fixes.
        Assert.DoesNotContain("FailFastUnhandledClosureException", output);
    }

    [Fact]
    public void EmitCloseAsyncWitnessFailFast_EscapesMemberLiteral_CannotBreakOutOfStringLiteral()
    {
        // A member descriptor is embedded as a C# string literal in the emitted diagnostic; a quote or
        // backslash in an unusual identifier must be escaped so it can't terminate the literal.
        var (w, sink) = NewWriter();
        UcoGuardEmitter.EmitOpen(w);
        w.WriteLine("body();");
        UcoGuardEmitter.EmitCloseAsyncWitnessFailFast(w, "Odd\"Proto\\.method");

        var output = sink.ToString();
        Assert.Contains("\"Odd\\\"Proto\\\\.method\"", output);
    }

    [Fact]
    public void KvoDispatchTrampoline_FailFastsOnEscapingException_NotSwallowed()
    {
        // A KVO change handler is a non-throwing callback with no error channel. An exception
        // escaping it must FailFast (loud, attributable), never print-and-continue: swallowing it
        // hides a consumer bug and continues observing on a corrupted assumption.
        var (csOutput, _) = EmitKvoForObservableIntProperty();

        Assert.Contains("FailFastUnhandledClosureException", csOutput);
        // The old fail-soft behaviour and its false justification must be gone.
        Assert.DoesNotContain("Console.Error.WriteLine", csOutput);
        Assert.DoesNotContain("matches the standard", csOutput);
        // The dispatch trampoline is present and guarded.
        Assert.Contains("DispatchCount", csOutput);
        Assert.Contains("try", csOutput);
    }

    /// <summary>
    /// Drives <see cref="KvoExtensionEmitter"/> for a minimal NSObject-rooted class with one
    /// observable <c>@objc dynamic</c> Int property and returns the generated C#/Swift.
    /// </summary>
    private static (string csOutput, string swiftOutput) EmitKvoForObservableIntProperty()
    {
        var typeDatabase = new TypeDatabase
        {
            // XCFramework mode is gated on a non-empty AsyncLibraryName.
            AsyncLibraryName = "libSwiftBindings",
        };
        typeDatabase.AddModuleDatabase(new ModuleTypeDatabase("TestModule", "/fake/path"));

        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Dependencies = new List<string>(),
            Types = new List<TypeDecl>(),
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
        };

        var classDecl = new ClassDecl
        {
            Name = "Counter",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Counter"),
            MangledName = "$s10TestModule7CounterCN",
            IsObjCRooted = true,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };
        classDecl.Properties.Add(new PropertyDecl
        {
            Name = "count",
            IsObjCDynamic = true,
            HasStorage = true,
            IsStatic = false,
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            Accessors = new List<AccessorDecl>(),
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl,
        });
        moduleDecl.Types.Add(classDecl);

        var csSink = new System.IO.StringWriter();
        var swiftSink = new System.IO.StringWriter();
        var csWriter = new CSharpWriter(csSink);
        var swiftWriter = new SwiftWriter(swiftSink);

        KvoExtensionEmitter.EmitKvoExtensionsForClass(
            csWriter, swiftWriter, classDecl, typeDatabase,
            new ModuleEmissionContext(), NullLogger.Instance);

        return (csSink.ToString(), swiftSink.ToString());
    }
}
