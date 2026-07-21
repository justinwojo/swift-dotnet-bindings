// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The fail-closed net that turns a dangling `new {X}Proxy(…)` construction into a generator error
/// (SWIFTBIND122) — the closure-completeness backstop for the SwiftRichString StyleProtocolProxy
/// CS0246. It reconciles emitted text on disk (all bare `new {X}Proxy(` references against all
/// `class {X}Proxy` definitions), so these tests drive it over a throwaway output directory. The
/// false-positive smokes (healthy output, generic proxy, cross-module-qualified reference) are as
/// load-bearing as the real-violation case: a gate that also fires on healthy output would block
/// every build.
/// </summary>
public class ProxyReferenceIntegrityGateTests : IDisposable
{
    private readonly string _dir;

    public ProxyReferenceIntegrityGateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sbw-proxy-integrity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private void WriteCs(string name, string body) =>
        File.WriteAllText(Path.Combine(_dir, name), body);

    [Fact]
    public void HealthyOutput_EveryProxyRefHasAClassDef_NoViolation()
    {
        WriteCs("Module.cs",
            "public unsafe partial class StylableProxy : IStylable { }\n" +
            "static class Consumer { static object M(IStylable v) => new StylableProxy(v); }\n");

        var logger = new CapturingLogger();
        Assert.False(ProxyReferenceIntegrityGate.HasViolations(_dir, suppressedProxyClassNames: null, logger));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("SWIFTBIND122"));
    }

    [Fact]
    public void DanglingReference_ProxyConstructedButNoClassDef_IsViolationAndNamesIt()
    {
        // The regression shape: the proxy class was suppressed (no `class StylableProxy`) but a
        // retained consumer still constructs `new StylableProxy(...)`.
        WriteCs("Module.cs",
            "public interface IStylable { }\n" +
            "static class Consumer { static object M(IStylable v) => new StylableProxy(v); }\n");

        var logger = new CapturingLogger();
        Assert.True(ProxyReferenceIntegrityGate.HasViolations(
            _dir, suppressedProxyClassNames: new[] { "StylableProxy" }, logger));
        Assert.Contains(logger.Messages, m => m.Contains("SWIFTBIND122"));
        Assert.Contains(logger.Messages, m => m.Contains("StylableProxy"));
        // Named as the downgrade-machinery leak because it IS in the suppressed set.
        Assert.Contains(logger.Messages, m => m.Contains("StylableProxy") && m.Contains("downgrade machinery"));
    }

    [Fact]
    public void DanglingReference_NotInSuppressedSet_IsStillAViolation()
    {
        // Even an UNRECORDED suppression (the future-arm-forgets-to-record case defect b guards) must
        // fail closed — the gate reconciles the outcome independent of the suppressed set.
        WriteCs("Module.cs",
            "static class Consumer { static object M(object v) => new GhostProxy(v); }\n");

        var logger = new CapturingLogger();
        Assert.True(ProxyReferenceIntegrityGate.HasViolations(_dir, suppressedProxyClassNames: null, logger));
        Assert.Contains(logger.Messages, m => m.Contains("GhostProxy") && m.Contains("not in the suppressed set"));
    }

    [Fact]
    public void GenericProxyDefinition_SatisfiesGenericConstruction()
    {
        // A generic proxy `class FooProxy<T>` must satisfy a `new FooProxy<Bar>(...)` construction —
        // the class-name capture ends at `Proxy` on both sides.
        WriteCs("Module.cs",
            "public unsafe partial class FooProxy<T> : IFoo<T> { }\n" +
            "static class Consumer { static object M(IFoo<int> v) => new FooProxy<int>(v); }\n");

        var logger = new CapturingLogger();
        Assert.False(ProxyReferenceIntegrityGate.HasViolations(_dir, suppressedProxyClassNames: null, logger));
    }

    [Fact]
    public void CrossModuleQualifiedReference_IsExcluded_NotAViolation()
    {
        // A cross-module proxy reference is always emitted fully qualified. It is NOT this module's
        // responsibility to define it, and the bare-only pattern must not treat it as a bare ref.
        WriteCs("Module.cs",
            "static class Consumer { static object M(object v) => " +
            "new global::Other.SwiftInterop.RemoteProxy(v); }\n");

        var logger = new CapturingLogger();
        Assert.False(ProxyReferenceIntegrityGate.HasViolations(_dir, suppressedProxyClassNames: null, logger));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("SWIFTBIND122"));
    }

    [Fact]
    public void ProxySuffixSubstring_IsNotAFalseMatch()
    {
        // `new FooProxyBuilder(` must not be captured as a `FooProxy` construction — the identifier
        // must END in `Proxy` immediately before `(` or `<`.
        WriteCs("Module.cs",
            "static class Consumer { static object M() => new FooProxyBuilder(); }\n");

        var logger = new CapturingLogger();
        Assert.False(ProxyReferenceIntegrityGate.HasViolations(_dir, suppressedProxyClassNames: null, logger));
    }

    [Fact]
    public void BuildIntermediates_UnderObjOrBin_AreNotScanned()
    {
        WriteCs("Module.cs",
            "public unsafe partial class StylableProxy : IStylable { }\n");
        var objDir = Path.Combine(_dir, "obj", "Debug");
        Directory.CreateDirectory(objDir);
        File.WriteAllText(Path.Combine(objDir, "Stale.cs"),
            "static class S { static object M() => new GhostProxy(); }\n");

        var logger = new CapturingLogger();
        Assert.False(ProxyReferenceIntegrityGate.HasViolations(_dir, suppressedProxyClassNames: null, logger));
    }

    [Fact]
    public void ProxyConstructionInDocComment_IsNotAFalseMatch()
    {
        // The generated fixtures DOCUMENT the `new {X}Proxy(…)` downgrade contract in XML doc comments
        // and `//` line comments. A comment is never compiled, so it must not read as a live dangling
        // construction — else the gate blocks every build that carries such documentation.
        WriteCs("Module.cs",
            "public interface IBoxable { }\n" +
            "static class Consumer {\n" +
            "    /// The wrapper would wrap the result in <c>new BoxableProxy(__v)</c>; suppressed → stub.\n" +
            "    // static __v => new BoxableProxy(__v) is dropped here.\n" +
            "    /* block: new BoxableProxy(x) mentioned in prose */\n" +
            "    static object M(IBoxable v) => v;\n" +
            "}\n");

        var logger = new CapturingLogger();
        Assert.False(ProxyReferenceIntegrityGate.HasViolations(_dir, suppressedProxyClassNames: null, logger));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("SWIFTBIND122"));
    }

    [Fact]
    public void ProxyConstructionInStringLiteral_IsNotAFalseMatch()
    {
        // Dead text inside a string literal (regular, verbatim, interpolated) is not compiled code, so
        // it must not read as a live construction — else a diagnostic message that quotes the pattern
        // would block the build.
        WriteCs("Module.cs",
            "static class Consumer {\n" +
            "    static string A() => \"new GhostProxy(x)\";\n" +
            "    static string B() => @\"verbatim new GhostProxy(x)\";\n" +
            "    static string C(int i) => $\"interp new GhostProxy({i})\";\n" +
            "}\n");

        var logger = new CapturingLogger();
        Assert.False(ProxyReferenceIntegrityGate.HasViolations(_dir, suppressedProxyClassNames: null, logger));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("SWIFTBIND122"));
    }

    [Fact]
    public void LiveConstructionAlongsideCommentedOne_StillFires()
    {
        // A comment mentioning the proxy must NOT mask a real live construction on another line.
        WriteCs("Module.cs",
            "static class Consumer {\n" +
            "    /// Documents new BoxableProxy(x) in prose.\n" +
            "    static object M(object v) => new BoxableProxy(v);\n" +
            "}\n");

        var logger = new CapturingLogger();
        Assert.True(ProxyReferenceIntegrityGate.HasViolations(_dir, suppressedProxyClassNames: null, logger));
        Assert.Contains(logger.Messages, m => m.Contains("BoxableProxy"));
    }

    [Fact]
    public void MissingDirectory_IsNotAViolation()
    {
        var logger = new CapturingLogger();
        Assert.False(ProxyReferenceIntegrityGate.HasViolations(
            Path.Combine(_dir, "does-not-exist"), suppressedProxyClassNames: null, logger));
    }

    [Fact]
    public void UnreadableEmittedSource_FailsClosed()
    {
        // An emitted artifact the gate cannot read may carry the ONLY dangling construction. The gate
        // names itself fail-closed, so an unreadable source must be a violation — never silently
        // skipped. A permission error surfaces as UnauthorizedAccessException (not IOException), which
        // the former narrow catch missed entirely.
        if (OperatingSystem.IsWindows())
            return; // Unix file-mode based; the generator targets macOS.

        WriteCs("Healthy.cs", "public unsafe partial class StylableProxy : IStylable { }\n");
        var locked = Path.Combine(_dir, "Locked.cs");
        File.WriteAllText(locked, "static class S { static object M(object v) => v; }\n");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            var logger = new CapturingLogger();
            Assert.True(ProxyReferenceIntegrityGate.HasViolations(_dir, suppressedProxyClassNames: null, logger));
            Assert.Contains(logger.Messages, m => m.Contains("SWIFTBIND122") && m.Contains("could not be read"));
            Assert.Contains(logger.Messages, m => m.Contains("unreadable emitted source") && m.Contains("Locked.cs"));
        }
        finally
        {
            // Restore read/write so the temp-dir teardown can delete it.
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
