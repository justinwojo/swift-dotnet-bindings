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
        var superclassTypeName = SwiftTypeName.FromModuleQualifiedName("Alamofire.Request");
        var record = new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Alamofire", "DataRequest"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Alamofire.DataRequest"),
            MetadataAccessor = "$s9Alamofire11DataRequestCMa",
            Flags = TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class,
            SuperclassTypeName = superclassTypeName,
        };

        Assert.NotNull(record.SuperclassTypeName);
        Assert.Equal("Alamofire.Request", record.SuperclassTypeName!.ModuleQualifiedName);
    }

    [Fact]
    public async Task Emit_ClassWithSuperclass_WritesSuperclassAttribute()
    {
        var dir = CreateTempDir();
        try
        {
            var module = new ModuleTypeDatabase("Alamofire", "/fake/Alamofire.dylib");
            var swiftName = SwiftTypeName.FromModuleQualifiedName("Alamofire.DataRequest");
            var record = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Alamofire", "DataRequest"),
                SwiftTypeName = swiftName,
                MetadataAccessor = "$s9Alamofire11DataRequestCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class,
                SuperclassTypeName = SwiftTypeName.FromModuleQualifiedName("Alamofire.Request"),
            };
            module.RegisterType(swiftName, record);

            var path = ModuleDatabaseEmitter.Emit(module, dir, NullLogger.Instance);
            Assert.NotNull(path);

            var xml = File.ReadAllText(path!);
            Assert.Contains("superclass=\"Alamofire.Request\"", xml);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Emit_ClassWithoutSuperclass_OmitsSuperclassAttribute()
    {
        var dir = CreateTempDir();
        try
        {
            var module = new ModuleTypeDatabase("Alamofire", "/fake/Alamofire.dylib");
            var swiftName = SwiftTypeName.FromModuleQualifiedName("Alamofire.Request");
            var record = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Alamofire", "Request"),
                SwiftTypeName = swiftName,
                MetadataAccessor = "$s9Alamofire7RequestCMa",
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
            var module = new ModuleTypeDatabase("Alamofire", "/fake/Alamofire.dylib");
            var swiftName = SwiftTypeName.FromModuleQualifiedName("Alamofire.DataRequest");
            var superclassTypeName = SwiftTypeName.FromModuleQualifiedName("Alamofire.Request");
            var record = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Alamofire", "DataRequest"),
                SwiftTypeName = swiftName,
                MetadataAccessor = "$s9Alamofire11DataRequestCMa",
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
            Assert.Equal("Alamofire.Request", loaded.SuperclassTypeName!.ModuleQualifiedName);
            Assert.Equal("Alamofire", loaded.SuperclassTypeName.Module);
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
