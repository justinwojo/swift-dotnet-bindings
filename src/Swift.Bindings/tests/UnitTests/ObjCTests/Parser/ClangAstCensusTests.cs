// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using BindingsGeneration.ObjC;
using Microsoft.Extensions.Logging;
using Xunit;
using static BindingsGeneration.Tests.ObjCTests.ObjCTestHelpers;

namespace BindingsGeneration.Tests.ObjCTests;

/// <summary>
/// Tests for the Clang-AST observability layer (Finding 63): the systemic-failure hard error
/// (zero top-level AST nodes) and the top-level node-kind census that turns silently-skipped
/// declaration kinds into a loud <c>SWIFTBIND029</c> diagnostic, measured against the in-code
/// "golden" vocabulary <see cref="ClangAstParser.KnownTopLevelNodeKinds"/>.
/// </summary>
public class ClangAstCensusTests
{
    // ---- Systemic-failure hard error: zero AST nodes from a non-empty header set ----

    [Fact]
    public void Parse_EmptyTranslationUnit_ThrowsSystemicFailure()
    {
        var json = """
        {
            "kind": "TranslationUnitDecl",
            "inner": []
        }
        """;

        var ex = Assert.Throws<InvalidOperationException>(
            () => ClangAstParser.Parse(json, "TestLib", DefaultHeadersPath));
        Assert.Contains("SWIFTBIND029", ex.Message);
    }

    [Fact]
    public void Parse_AbsentInner_ThrowsSystemicFailure()
    {
        // A clang dump shell with no `inner` array at all is the same systemic failure.
        var json = """{ "kind": "TranslationUnitDecl" }""";

        var ex = Assert.Throws<InvalidOperationException>(
            () => ClangAstParser.Parse(json, "TestLib", DefaultHeadersPath));
        Assert.Contains("SWIFTBIND029", ex.Message);
    }

    // ---- Node-kind census against the golden vocabulary ----

    [Fact]
    public void Parse_UnknownTopLevelKind_RaisesSwiftbind029_NamingTheKind()
    {
        // A real clang interface (known kind) plus a hypothetical future decl kind the switch does
        // not handle. The known kind must NOT warn; the unknown one must be surfaced by name.
        var json = WrapInTranslationUnit($$"""
            {
                "kind": "ObjCInterfaceDecl",
                "name": "RealClass",
                {{MakeLoc()}},
                "inner": []
            },
            {
                "kind": "ObjCFutureWeirdDecl",
                "name": "Mystery",
                {{MakeLoc()}}
            }
            """);

        var logger = new CapturingLogger();
        // Should not throw — inner is non-empty.
        ClangAstParser.Parse(json, "TestLib", DefaultHeadersPath, logger);

        var warnings = logger.Entries
            .Where(e => e.Level == LogLevel.Warning && e.Message.Contains("SWIFTBIND029"))
            .ToList();
        var warning = Assert.Single(warnings);
        Assert.Contains("ObjCFutureWeirdDecl", warning.Message);
        // The recognized kind must not appear in the unrecognized-kinds warning.
        Assert.DoesNotContain("ObjCInterfaceDecl", warning.Message);
    }

    [Fact]
    public void Parse_AllKnownKinds_DoesNotRaiseSwiftbind029()
    {
        var json = WrapInTranslationUnit($$"""
            {
                "kind": "ObjCInterfaceDecl",
                "name": "RealClass",
                {{MakeLoc()}},
                "inner": []
            },
            {
                "kind": "EnumDecl",
                "name": "RealEnum",
                {{MakeLoc()}},
                "inner": []
            }
            """);

        var logger = new CapturingLogger();
        ClangAstParser.Parse(json, "TestLib", DefaultHeadersPath, logger);

        Assert.DoesNotContain(logger.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("SWIFTBIND029"));
    }

    [Fact]
    public void Parse_LogsFullCensusAtDebug()
    {
        var json = WrapInTranslationUnit($$"""
            {
                "kind": "ObjCInterfaceDecl",
                "name": "RealClass",
                {{MakeLoc()}},
                "inner": []
            }
            """);

        var logger = new CapturingLogger();
        ClangAstParser.Parse(json, "TestLib", DefaultHeadersPath, logger);

        Assert.Contains(logger.Entries,
            e => e.Level == LogLevel.Debug
              && e.Message.Contains("node-kind census")
              && e.Message.Contains("ObjCInterfaceDecl=1"));
    }

    // ---- Golden-vocabulary guard: every handled switch case is in the known set ----

    [Theory]
    [InlineData("ObjCInterfaceDecl")]
    [InlineData("ObjCProtocolDecl")]
    [InlineData("ObjCCategoryDecl")]
    [InlineData("EnumDecl")]
    [InlineData("RecordDecl")]
    [InlineData("FunctionDecl")]
    [InlineData("VarDecl")]
    [InlineData("TypedefDecl")]
    public void KnownTopLevelNodeKinds_CoversEveryHandledSwitchCase(string handledKind)
    {
        // Guard: a top-level kind the parser actually parses must be in the golden vocabulary,
        // otherwise the census would cry wolf on it every run. Keep this in lockstep with the
        // switch in ClangAstParser.Parse.
        Assert.Contains(handledKind, ClangAstParser.KnownTopLevelNodeKinds);
    }

    /// <summary>Captures log entries with their level for diagnostic assertions.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
