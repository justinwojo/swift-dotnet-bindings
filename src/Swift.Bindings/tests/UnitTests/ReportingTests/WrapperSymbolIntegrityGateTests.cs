// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The fail-closed net that turns a dangling wrapper-symbol P/Invoke into a generator error.
/// These tests drive it over a throwaway on-disk output directory, since it reconciles emitted
/// text on disk (all <c>.cs</c> EntryPoint refs against all <c>.swift</c> @_cdecl/@_silgen_name
/// defs). The false-positive smoke and the recursive-dependency case are as load-bearing as the
/// real-violation case: a gate that also fires on healthy output would block every build.
/// </summary>
public class WrapperSymbolIntegrityGateTests : IDisposable
{
    private readonly string _dir;

    public WrapperSymbolIntegrityGateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sbw-integrity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private void WriteCs(string name, params string[] entryPoints)
    {
        var lines = entryPoints.Select(ep =>
            $"    [LibraryImport(\"SwiftBindings\", EntryPoint = \"{ep}\")]\n" +
            $"    private static partial void {ep}();");
        File.WriteAllText(Path.Combine(_dir, name),
            "internal static class NativeMethods\n{\n" + string.Join("\n", lines) + "\n}\n");
    }

    private void WriteSwift(string relativePath, params string[] symbols)
    {
        var full = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var lines = symbols.Select(s =>
            $"@_cdecl(\"{s}\")\npublic func {s}() {{ }}");
        File.WriteAllText(full, string.Join("\n", lines) + "\n");
    }

    [Fact]
    public void HealthyOutput_EveryRefHasADef_NoViolation()
    {
        WriteCs("Module.cs", "SBW_Foo_get_bar_0", "SBW_Foo_set_bar_0");
        WriteSwift("Module.Wrapper.swift", "SBW_Foo_get_bar_0", "SBW_Foo_set_bar_0");

        var logger = new CapturingLogger();
        Assert.False(WrapperSymbolIntegrityGate.HasViolations(_dir, logger));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("SWIFTBIND108"));
    }

    [Fact]
    public void DanglingReference_RefWithoutDef_IsViolationAndLogsCodeAndSymbol()
    {
        // SBW_Foo_get_bar_0 is defined; SBW_Ghost_get_x_0 is referenced but never emitted.
        WriteCs("Module.cs", "SBW_Foo_get_bar_0", "SBW_Ghost_get_x_0");
        WriteSwift("Module.Wrapper.swift", "SBW_Foo_get_bar_0");

        var logger = new CapturingLogger();
        Assert.True(WrapperSymbolIntegrityGate.HasViolations(_dir, logger));
        Assert.Contains(logger.Messages, m => m.Contains("SWIFTBIND108"));
        // The offending symbol is named; the satisfied one is not.
        Assert.Contains(logger.Messages, m => m.Contains("SBW_Ghost_get_x_0"));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("undefined") && m.Contains("SBW_Foo_get_bar_0"));
    }

    [Fact]
    public void DefinitionInDependencySubdir_SatisfiesReference_Recursively()
    {
        // A co-emitted dependency wrapper lands under dep-swift/; the top-level C# references it.
        // The scan must recurse or this healthy shape would false-positive.
        WriteCs("Module.cs", "SBW_Dep_get_bar_0");
        WriteSwift("dep-swift/ModuleDependency.Wrapper.swift", "SBW_Dep_get_bar_0");

        var logger = new CapturingLogger();
        Assert.False(WrapperSymbolIntegrityGate.HasViolations(_dir, logger));
    }

    [Fact]
    public void SilgenNameDefinition_SatisfiesSwiftCcReference()
    {
        // SBSW_ (Swift-CC) symbols are defined via @_silgen_name, not @_cdecl.
        WriteCs("Module.cs", "SBSW_Foo_thunk_0");
        var full = Path.Combine(_dir, "Module.Wrapper.swift");
        File.WriteAllText(full, "@_silgen_name(\"SBSW_Foo_thunk_0\")\npublic func _sbsw_foo() { }\n");

        var logger = new CapturingLogger();
        Assert.False(WrapperSymbolIntegrityGate.HasViolations(_dir, logger));
    }

    [Fact]
    public void BuildIntermediates_UnderObjOrBin_AreNotScanned()
    {
        // A previously-built output dir may carry obj/ trees; a stray dangling ref there must not
        // fail generation (it isn't emitted source). The real emitted source is clean.
        WriteCs("Module.cs", "SBW_Foo_get_bar_0");
        WriteSwift("Module.Wrapper.swift", "SBW_Foo_get_bar_0");
        var objDir = Path.Combine(_dir, "obj", "Debug");
        Directory.CreateDirectory(objDir);
        File.WriteAllText(Path.Combine(objDir, "Stale.cs"),
            "[LibraryImport(\"X\", EntryPoint = \"SBW_Stale_Ghost_0\")] static partial void G();");

        var logger = new CapturingLogger();
        Assert.False(WrapperSymbolIntegrityGate.HasViolations(_dir, logger));
    }

    [Fact]
    public void MissingDirectory_IsNotAViolation()
    {
        var logger = new CapturingLogger();
        Assert.False(WrapperSymbolIntegrityGate.HasViolations(
            Path.Combine(_dir, "does-not-exist"), logger));
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
