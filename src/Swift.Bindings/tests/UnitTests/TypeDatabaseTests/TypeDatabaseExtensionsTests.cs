// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

public class TypeDatabaseExtensionsTests
{
    [Fact]
    public void TryGetAnyTypeFallbackInfo_MissingType_ReturnsFallbackInfo()
    {
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("KnownModule", "/tmp/KnownModule.dylib");
        typeDatabase.AddModuleDatabase(module);

        var found = typeDatabase.TryGetAnyTypeFallbackInfo(new NamedTypeSpec("UnknownModule.MissingType"), out var fallbackInfo);

        Assert.True(found);
        Assert.NotNull(fallbackInfo);
        Assert.Equal("Type is missing from the type database", fallbackInfo.Value.Reason);
        Assert.Equal("UnknownModule.MissingType", fallbackInfo.Value.SwiftType);
    }

    [Fact]
    public void TryGetAnyTypeFallbackInfo_GenericParameter_ReturnsFalse()
    {
        var typeDatabase = new TypeDatabase();

        var found = typeDatabase.TryGetAnyTypeFallbackInfo(new NamedTypeSpec("T"), out var fallbackInfo);

        Assert.False(found);
        Assert.Null(fallbackInfo);
    }

    [Fact]
    public void TryGetAnyTypeFallbackInfo_ExistentialNamedType_ReturnsFallbackInfo()
    {
        var typeDatabase = new TypeDatabase();
        var existential = new NamedTypeSpec("Swift.Encoder")
        {
            IsAny = true
        };

        var found = typeDatabase.TryGetAnyTypeFallbackInfo(existential, out var fallbackInfo);

        Assert.True(found);
        Assert.NotNull(fallbackInfo);
        Assert.Equal("Existential type fallback", fallbackInfo.Value.Reason);
        Assert.Equal("any Swift.Encoder", fallbackInfo.Value.SwiftType);
    }
}
