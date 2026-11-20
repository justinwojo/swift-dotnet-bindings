// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The API manifest is also the producer's proof of what a module owed: surface accounting reads
/// an empty manifest as "this module's emission ran and recorded no members", and a missing one as
/// an artifact that was never written.
/// </summary>
public sealed class ApiManifestEmitterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"api-manifest-emitter-{Guid.NewGuid():N}");

    public ApiManifestEmitterTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static JsonElement Read(string path) => JsonDocument.Parse(File.ReadAllText(path)).RootElement;

    [Fact]
    public void ModuleWithNoRecordedMembersStillGetsAnEmptyManifest()
    {
        var path = ApiManifestEmitter.Emit("Toasts", "Toasts", new ModuleEmissionContext(), _directory, NullLogger.Instance);

        Assert.Equal(Path.Combine(_directory, "Toasts.api-manifest.json"), path);
        var root = Read(path!);
        Assert.Equal(ApiManifestEmitter.SchemaVersion, root.GetProperty("schema_version").GetInt32());
        Assert.Equal("Toasts", root.GetProperty("module").GetString());
        Assert.Equal(0, root.GetProperty("members").GetArrayLength());
    }

    [Fact]
    public void RerenderThatDropsTheLastMemberReplacesTheStaleManifest()
    {
        var populated = new ModuleEmissionContext();
        populated.RecordApiManifestEntry("Toast.Show()", "sym_show");
        var path = ApiManifestEmitter.Emit("Toasts", "Toasts", populated, _directory, NullLogger.Instance);
        Assert.Equal(1, Read(path!).GetProperty("members").GetArrayLength());

        ApiManifestEmitter.Emit("Toasts", "Toasts", new ModuleEmissionContext(), _directory, NullLogger.Instance);

        Assert.Equal(0, Read(path!).GetProperty("members").GetArrayLength());
    }

    [Fact]
    public void NoEmissionContextWritesNothing()
    {
        var path = ApiManifestEmitter.Emit("Toasts", "Toasts", null, _directory, NullLogger.Instance);

        Assert.Null(path);
        Assert.Empty(Directory.GetFiles(_directory));
    }
}
