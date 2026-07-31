// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using BindingsGeneration.ObjC;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests.ObjCTests;

/// <summary>
/// Shared test utilities for ObjC test classes.
/// </summary>
public static class ObjCTestHelpers
{
    /// <summary>Shared NullLogger for all ObjC tests.</summary>
    public static readonly ILogger Logger = NullLogger.Instance;

    /// <summary>Shorthand for creating a simple ObjCTypeRef.</summary>
    public static ObjCTypeRef SimpleType(string name, bool isPointer = false) =>
        new() { Name = name, IsPointer = isPointer };

    /// <summary>
    /// Emit an ObjCModule through ApiDefinitionEmitter and return the file content.
    /// Handles temp directory creation and cleanup.
    /// </summary>
    public static string EmitApiDefinition(ObjCModule module, string ns = "TestNamespace", PlatformInfo? platformInfo = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"apidefinition_test_{Guid.NewGuid():N}");
        try
        {
            var path = ApiDefinitionEmitter.Emit(module, dir, ns, Logger, diagnostics: null, platformInfo: platformInfo);
            Assert.Equal(Path.Combine(dir, "ApiDefinition.cs"), path);
            return File.ReadAllText(path);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// Emit an ObjCModule through ApiDefinitionEmitter, returning both the file content and the
    /// diagnostics collector so tests can assert recorded skips (e.g. <c>DuplicateSelector</c>).
    /// </summary>
    public static (string Content, ObjCBindingDiagnostics Diagnostics) EmitApiDefinitionWithDiagnostics(
        ObjCModule module, string ns = "TestNamespace", PlatformInfo? platformInfo = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"apidefinition_test_{Guid.NewGuid():N}");
        var diagnostics = new ObjCBindingDiagnostics();
        try
        {
            var path = ApiDefinitionEmitter.Emit(module, dir, ns, Logger, diagnostics: diagnostics, platformInfo: platformInfo);
            return (File.ReadAllText(path), diagnostics);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// Emit an ObjCModule through ApiDefinitionEmitter and return the ApiDefinition content, the
    /// companion array-overloads file (null when none was written), and the diagnostics collector.
    /// The two files are a pair — the overload forwards to an <c>[Internal]</c> member declared in
    /// the ApiDefinition — so tests that assert on one usually need to assert on the other.
    /// </summary>
    /// <param name="seedStaleArrayOverloads">
    /// Writes a placeholder array-overloads file before emitting, so a test can observe that a run
    /// producing no overloads clears a leftover from a previous generate.
    /// </param>
    public static (string ApiDefinition, string? ArrayOverloads, ObjCBindingDiagnostics Diagnostics) EmitApiDefinitionWithArrayOverloads(
        ObjCModule module, string ns = "TestNamespace", PlatformInfo? platformInfo = null, bool seedStaleArrayOverloads = false)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"apidefinition_test_{Guid.NewGuid():N}");
        var diagnostics = new ObjCBindingDiagnostics();
        try
        {
            Directory.CreateDirectory(dir);
            var overloadsPath = Path.Combine(dir, ObjCArrayOverloadsEmitter.FileName);
            if (seedStaleArrayOverloads)
                File.WriteAllText(overloadsPath, "// stale content from a previous generate\n");

            var path = ApiDefinitionEmitter.Emit(module, dir, ns, Logger, diagnostics: diagnostics, platformInfo: platformInfo);
            return (File.ReadAllText(path),
                    File.Exists(overloadsPath) ? File.ReadAllText(overloadsPath) : null,
                    diagnostics);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// Emit an ObjCModule through StructsAndEnumsEmitter and return the main file content.
    /// Handles temp directory creation and cleanup.
    /// </summary>
    public static string EmitStructsAndEnums(ObjCModule module, string ns = "TestLib.Binding", PlatformInfo? platformInfo = null)
    {
        var (content, _) = EmitStructsAndEnumsBoth(module, ns, platformInfo);
        return content;
    }

    /// <summary>
    /// Emit an ObjCModule through StructsAndEnumsEmitter and return both main and bgen delegate file content.
    /// </summary>
    public static (string main, string? bgenDelegates) EmitStructsAndEnumsBoth(ObjCModule module, string ns = "TestLib.Binding", PlatformInfo? platformInfo = null)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"structs_enums_test_{Guid.NewGuid():N}");
        try
        {
            var result = StructsAndEnumsEmitter.Emit(module, tempDir, ns, Logger, diagnostics: null, platformInfo: platformInfo);
            Assert.NotNull(result);
            var main = File.ReadAllText(result!.FilePath);
            var bgen = result.BgenDelegatesFilePath != null
                ? File.ReadAllText(result.BgenDelegatesFilePath)
                : null;
            return (main, bgen);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    /// <summary>
    /// Emit an ObjCModule through StructsAndEnumsEmitter and return the main file content plus the
    /// diagnostics recorded during emission (e.g. enum-case disambiguation skips).
    /// </summary>
    public static (string main, ObjCBindingDiagnostics Diagnostics) EmitStructsAndEnumsWithDiagnostics(
        ObjCModule module, string ns = "TestLib.Binding", PlatformInfo? platformInfo = null)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"structs_enums_test_{Guid.NewGuid():N}");
        var diagnostics = new ObjCBindingDiagnostics();
        try
        {
            var result = StructsAndEnumsEmitter.Emit(module, tempDir, ns, Logger, diagnostics: diagnostics, platformInfo: platformInfo);
            Assert.NotNull(result);
            return (File.ReadAllText(result!.FilePath), diagnostics);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    /// <summary>
    /// Wrap inner JSON in a TranslationUnitDecl for ClangAstParser tests.
    /// </summary>
    public static string WrapInTranslationUnit(string innerJson) =>
        $$"""
        {
            "kind": "TranslationUnitDecl",
            "inner": [{{innerJson}}]
        }
        """;

    /// <summary>
    /// Create a JSON loc object for ClangAstParser tests.
    /// </summary>
    public static string MakeLoc(string file = "/Frameworks/TestLib.framework/Headers/TestLib.h") =>
        $"\"loc\": {{ \"file\": \"{file}\" }}";

    /// <summary>Default headers path for parser tests.</summary>
    public const string DefaultHeadersPath = "/Frameworks/TestLib.framework/Headers";
}
