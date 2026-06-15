// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for the explicit <see cref="GenerationMode"/> (Finding 14d): the single named decision
/// that replaced the inline <c>!string.IsNullOrEmpty(AsyncLibraryName)</c> sentinel previously
/// copied across the emitter. Asserts the mode derives correctly from whether a companion wrapper
/// library is configured, and that <see cref="WrapperValidation.IsXCFrameworkMode"/> reads the
/// explicit mode rather than re-deriving the sentinel by hand.
/// </summary>
public class GenerationModeTests
{
    [Fact]
    public void GenerationMode_NoAsyncLibrary_IsDirect()
    {
        ITypeDatabase db = new TypeDatabase();
        Assert.Equal(GenerationMode.Direct, db.GenerationMode);
        Assert.False(WrapperValidation.IsXCFrameworkMode(db));
    }

    [Fact]
    public void GenerationMode_EmptyAsyncLibrary_IsDirect()
    {
        ITypeDatabase db = new TypeDatabase { AsyncLibraryName = "" };
        Assert.Equal(GenerationMode.Direct, db.GenerationMode);
        Assert.False(WrapperValidation.IsXCFrameworkMode(db));
    }

    [Fact]
    public void GenerationMode_WithAsyncLibrary_IsXCFramework()
    {
        ITypeDatabase db = new TypeDatabase { AsyncLibraryName = "TestModuleSwiftBindings" };
        Assert.Equal(GenerationMode.XCFramework, db.GenerationMode);
        Assert.True(WrapperValidation.IsXCFrameworkMode(db));
    }

    [Theory]
    [InlineData(GenerationMode.Direct, false)]
    [InlineData(GenerationMode.XCFramework, true)]
    public void IsXCFrameworkMode_ConsultsGenerationMode_NotAsyncLibrarySentinel(
        GenerationMode mode, bool expected)
    {
        // The helper must read the explicit mode. Set AsyncLibraryName to the OPPOSITE of the mode
        // so a regression that re-derived the result from the sentinel would flip the answer.
        var db = new ModeOverrideTypeDatabase
        {
            ModeOverride = mode,
            AsyncLibraryName = mode == GenerationMode.Direct ? "ShouldBeIgnored" : null,
        };

        Assert.Equal(expected, WrapperValidation.IsXCFrameworkMode(db));
    }

    /// <summary>
    /// Mock that decouples the explicit mode from the <c>AsyncLibraryName</c> sentinel by declaring
    /// its own <see cref="ITypeDatabase.GenerationMode"/> (overriding the interface default member).
    /// </summary>
    private sealed class ModeOverrideTypeDatabase : ITypeDatabase
    {
        public GenerationMode ModeOverride { get; init; }
        public GenerationMode GenerationMode => ModeOverride;
        public string? AsyncLibraryName { get; init; }
        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) => false;
        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, [NotNullWhen(true)] out TypeRecord? record)
        {
            record = null;
            return false;
        }
        public string GetLibraryPath(string moduleName) => "";
        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
    }
}
