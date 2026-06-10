// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests that SuperclassTypeName on TypeRecord serializes/deserializes through the module database XML.
/// </summary>
public class SuperclassModuleDatabaseTests
{
    [Fact]
    public void TypeRecord_WithSuperclassTypeName_StoresValue()
    {
        var superclassTypeName = SwiftTypeName.FromModuleQualifiedName("NetClient.Request");
        var record = new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("NetClient", "DataRequest"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("NetClient.DataRequest"),
            MetadataAccessor = "$s9NetClient11DataRequestCMa",
            Flags = TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class,
            SuperclassTypeName = superclassTypeName,
        };

        Assert.NotNull(record.SuperclassTypeName);
        Assert.Equal("NetClient.Request", record.SuperclassTypeName!.ModuleQualifiedName);
    }

    [Fact]
    public async Task Emit_ClassWithSuperclass_WritesSuperclassAttribute()
    {
        var dir = CreateTempDir();
        try
        {
            var module = new ModuleTypeDatabase("NetClient", "/fake/NetClient.dylib");
            var swiftName = SwiftTypeName.FromModuleQualifiedName("NetClient.DataRequest");
            var record = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("NetClient", "DataRequest"),
                SwiftTypeName = swiftName,
                MetadataAccessor = "$s9NetClient11DataRequestCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class,
                SuperclassTypeName = SwiftTypeName.FromModuleQualifiedName("NetClient.Request"),
            };
            module.RegisterType(swiftName, record);

            var path = ModuleDatabaseEmitter.Emit(module, dir, NullLogger.Instance);
            Assert.NotNull(path);

            var xml = File.ReadAllText(path!);
            Assert.Contains("superclass=\"NetClient.Request\"", xml);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Emit_ClassWithoutSuperclass_OmitsSuperclassAttribute()
    {
        var dir = CreateTempDir();
        try
        {
            var module = new ModuleTypeDatabase("NetClient", "/fake/NetClient.dylib");
            var swiftName = SwiftTypeName.FromModuleQualifiedName("NetClient.Request");
            var record = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("NetClient", "Request"),
                SwiftTypeName = swiftName,
                MetadataAccessor = "$s9NetClient7RequestCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class,
                // No SuperclassTypeName
            };
            module.RegisterType(swiftName, record);

            var path = ModuleDatabaseEmitter.Emit(module, dir, NullLogger.Instance);
            Assert.NotNull(path);

            var xml = File.ReadAllText(path!);
            Assert.DoesNotContain("superclass=", xml);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Emit_ThenLoad_SuperclassTypeNameRoundTrips()
    {
        var dir = CreateTempDir();
        try
        {
            var module = new ModuleTypeDatabase("NetClient", "/fake/NetClient.dylib");
            var swiftName = SwiftTypeName.FromModuleQualifiedName("NetClient.DataRequest");
            var superclassTypeName = SwiftTypeName.FromModuleQualifiedName("NetClient.Request");
            var record = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("NetClient", "DataRequest"),
                SwiftTypeName = swiftName,
                MetadataAccessor = "$s9NetClient11DataRequestCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class,
                SuperclassTypeName = superclassTypeName,
            };
            module.RegisterType(swiftName, record);

            var path = ModuleDatabaseEmitter.Emit(module, dir, NullLogger.Instance);
            Assert.NotNull(path);

            var typeDatabase = new TypeDatabase();
            await typeDatabase.LoadModuleDatabaseFromFile(path!);

            Assert.True(typeDatabase.TryGetTypeRecord(swiftName, out var loaded));
            Assert.NotNull(loaded!.SuperclassTypeName);
            Assert.Equal("NetClient.Request", loaded.SuperclassTypeName!.ModuleQualifiedName);
            Assert.Equal("NetClient", loaded.SuperclassTypeName.Module);
        }
        finally { Directory.Delete(dir, true); }
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mdb_super_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
