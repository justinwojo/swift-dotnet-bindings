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
    public static string EmitApiDefinition(ObjCModule module, string ns = "TestNamespace")
    {
        var dir = Path.Combine(Path.GetTempPath(), $"apidefinition_test_{Guid.NewGuid():N}");
        try
        {
            var path = ApiDefinitionEmitter.Emit(module, dir, ns, Logger);
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
    /// Emit an ObjCModule through StructsAndEnumsEmitter and return the main file content.
    /// Handles temp directory creation and cleanup.
    /// </summary>
    public static string EmitStructsAndEnums(ObjCModule module, string ns = "TestLib.Binding")
    {
        var (content, _) = EmitStructsAndEnumsBoth(module, ns);
        return content;
    }

    /// <summary>
    /// Emit an ObjCModule through StructsAndEnumsEmitter and return both main and bgen delegate file content.
    /// </summary>
    public static (string main, string? bgenDelegates) EmitStructsAndEnumsBoth(ObjCModule module, string ns = "TestLib.Binding")
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"structs_enums_test_{Guid.NewGuid():N}");
        try
        {
            var result = StructsAndEnumsEmitter.Emit(module, tempDir, ns, Logger);
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
