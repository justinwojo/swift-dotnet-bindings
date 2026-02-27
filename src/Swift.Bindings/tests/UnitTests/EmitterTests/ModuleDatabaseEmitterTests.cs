// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests
{
    public class ModuleDatabaseEmitterTests
    {
        [Fact]
        public async Task Emit_StructRecords_RoundTrips()
        {
            var dir = CreateTempDir();
            try
            {
                var module = new ModuleTypeDatabase("TestModule", "/fake/TestModule.dylib");
                var swiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.Widget");
                var record = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.TestModule", "Widget"),
                    SwiftTypeName = swiftName,
                    MetadataAccessor = "$s10TestModule6WidgetV",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                };
                module.RegisterType(swiftName, record);

                var path = ModuleDatabaseEmitter.Emit(module, dir, NullLogger.Instance);

                Assert.NotNull(path);
                Assert.True(File.Exists(path));

                // Round-trip: load the emitted file
                var typeDatabase = new TypeDatabase();
                await typeDatabase.LoadModuleDatabaseFromFile(path);

                Assert.True(typeDatabase.IsModuleProcessed("TestModule"));
                Assert.True(typeDatabase.TryGetTypeRecord(swiftName, out var loaded));
                Assert.Equal("Widget", loaded!.CSharpTypeName.Name);
                Assert.Equal("Swift.TestModule", loaded.CSharpTypeName.Namespace);
                Assert.Equal("$s10TestModule6WidgetV", loaded.MetadataAccessor);
                Assert.Equal(TypeRecordKind.Struct, loaded.Kind);
                Assert.True((loaded.Flags & TypeRecordFlags.Frozen) != 0);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task Emit_ClassRecords_PreservesFlags()
        {
            var dir = CreateTempDir();
            try
            {
                var module = new ModuleTypeDatabase("MyLib", "/fake/MyLib.dylib");
                var swiftName = SwiftTypeName.FromModuleQualifiedName("MyLib.Manager");
                var record = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.MyLib", "Manager"),
                    SwiftTypeName = swiftName,
                    MetadataAccessor = "$s5MyLib7ManagerC",
                    Flags = TypeRecordFlags.RequiresMemoryManagement | TypeRecordFlags.ObjCBridged,
                    Kind = TypeRecordKind.Class
                };
                module.RegisterType(swiftName, record);

                var path = ModuleDatabaseEmitter.Emit(module, dir, NullLogger.Instance);
                Assert.NotNull(path);

                var typeDatabase = new TypeDatabase();
                await typeDatabase.LoadModuleDatabaseFromFile(path);

                Assert.True(typeDatabase.TryGetTypeRecord(swiftName, out var loaded));
                Assert.Equal(TypeRecordKind.Class, loaded!.Kind);
                Assert.True((loaded.Flags & TypeRecordFlags.RequiresMemoryManagement) != 0);
                Assert.True((loaded.Flags & TypeRecordFlags.ObjCBridged) != 0);
                Assert.False((loaded.Flags & TypeRecordFlags.Frozen) != 0);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task Emit_EnumRecords_PreservesRawValueType()
        {
            var dir = CreateTempDir();
            try
            {
                var module = new ModuleTypeDatabase("MyLib", "/fake/MyLib.dylib");
                var swiftName = SwiftTypeName.FromModuleQualifiedName("MyLib.Color");
                var record = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.MyLib", "Color"),
                    SwiftTypeName = swiftName,
                    MetadataAccessor = "$s5MyLib5ColorO",
                    Flags = TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum,
                    Kind = TypeRecordKind.Enum,
                    RawValueTypeName = "Int"
                };
                module.RegisterType(swiftName, record);

                var path = ModuleDatabaseEmitter.Emit(module, dir, NullLogger.Instance);
                Assert.NotNull(path);

                var typeDatabase = new TypeDatabase();
                await typeDatabase.LoadModuleDatabaseFromFile(path);

                Assert.True(typeDatabase.TryGetTypeRecord(swiftName, out var loaded));
                Assert.Equal(TypeRecordKind.Enum, loaded!.Kind);
                Assert.True((loaded.Flags & TypeRecordFlags.SimpleEnum) != 0);
                Assert.Equal("Int", loaded.RawValueTypeName);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task Emit_ProtocolRecords_PreservesKindAndFlags()
        {
            var dir = CreateTempDir();
            try
            {
                var module = new ModuleTypeDatabase("MyLib", "/fake/MyLib.dylib");
                var swiftName = SwiftTypeName.FromModuleQualifiedName("MyLib.Configurable");
                var record = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.MyLib", "IConfigurable"),
                    SwiftTypeName = swiftName,
                    MetadataAccessor = "$s5MyLib12ConfigurableP",
                    Flags = TypeRecordFlags.HasAssociatedTypes,
                    Kind = TypeRecordKind.Protocol
                };
                module.RegisterType(swiftName, record);

                var path = ModuleDatabaseEmitter.Emit(module, dir, NullLogger.Instance);
                Assert.NotNull(path);

                var typeDatabase = new TypeDatabase();
                await typeDatabase.LoadModuleDatabaseFromFile(path);

                Assert.True(typeDatabase.TryGetTypeRecord(swiftName, out var loaded));
                Assert.Equal(TypeRecordKind.Protocol, loaded!.Kind);
                Assert.True((loaded.Flags & TypeRecordFlags.HasAssociatedTypes) != 0);
                Assert.Equal("IConfigurable", loaded.CSharpTypeName.Name);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task Emit_NativeTypeName_Preserved()
        {
            var dir = CreateTempDir();
            try
            {
                var module = new ModuleTypeDatabase("Foundation", "/System/lib/Foundation.dylib");
                var swiftName = SwiftTypeName.FromModuleQualifiedName("Foundation.URL");
                var record = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "URL"),
                    SwiftTypeName = swiftName,
                    MetadataAccessor = "$s10Foundation3URLV",
                    Flags = TypeRecordFlags.None,
                    Kind = TypeRecordKind.Struct,
                    NativeTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSUrl")
                };
                module.RegisterType(swiftName, record);

                var path = ModuleDatabaseEmitter.Emit(module, dir, NullLogger.Instance);
                Assert.NotNull(path);

                var typeDatabase = new TypeDatabase();
                await typeDatabase.LoadModuleDatabaseFromFile(path);

                Assert.True(typeDatabase.TryGetTypeRecord(swiftName, out var loaded));
                Assert.NotNull(loaded!.NativeTypeName);
                Assert.Equal("Foundation", loaded.NativeTypeName!.Namespace);
                Assert.Equal("NSUrl", loaded.NativeTypeName.Name);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_EmptyModule_ReturnsNull()
        {
            var dir = CreateTempDir();
            try
            {
                var module = new ModuleTypeDatabase("EmptyModule", "/fake/Empty.dylib");

                var path = ModuleDatabaseEmitter.Emit(module, dir, NullLogger.Instance);

                Assert.Null(path);
                // Verify no file was written
                Assert.Empty(Directory.GetFiles(dir, "*.xml"));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task Emit_NestedType_PreservesFullName()
        {
            var dir = CreateTempDir();
            try
            {
                var module = new ModuleTypeDatabase("Nuke", "/fake/Nuke.dylib");
                var swiftName = SwiftTypeName.FromModuleQualifiedName("Nuke.ImageRequest.UserInfoKey");
                var record = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Nuke", "ImageRequest_UserInfoKey"),
                    SwiftTypeName = swiftName,
                    MetadataAccessor = "$s4Nuke12ImageRequestV11UserInfoKeyV",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                };
                module.RegisterType(swiftName, record);

                var path = ModuleDatabaseEmitter.Emit(module, dir, NullLogger.Instance);
                Assert.NotNull(path);

                var typeDatabase = new TypeDatabase();
                await typeDatabase.LoadModuleDatabaseFromFile(path);

                Assert.True(typeDatabase.TryGetTypeRecord(swiftName, out var loaded));
                Assert.Equal("ImageRequest_UserInfoKey", loaded!.CSharpTypeName.Name);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task Emit_ExistentialKind_RoundTrips()
        {
            var dir = CreateTempDir();
            try
            {
                var module = new ModuleTypeDatabase("MyLib", "/fake/MyLib.dylib");
                var swiftName = SwiftTypeName.FromModuleQualifiedName("MyLib.AnyHashable");
                var record = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.MyLib", "AnyHashable"),
                    SwiftTypeName = swiftName,
                    MetadataAccessor = "$s5MyLib11AnyHashableV",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Existential
                };
                module.RegisterType(swiftName, record);

                var path = ModuleDatabaseEmitter.Emit(module, dir, NullLogger.Instance);
                Assert.NotNull(path);

                var typeDatabase = new TypeDatabase();
                await typeDatabase.LoadModuleDatabaseFromFile(path);

                Assert.True(typeDatabase.TryGetTypeRecord(swiftName, out var loaded));
                Assert.Equal(TypeRecordKind.Existential, loaded!.Kind);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_EntityOrder_IsDeterministic()
        {
            var dir = CreateTempDir();
            try
            {
                // Register types in reverse-alphabetical order to ensure
                // ConcurrentDictionary iteration order doesn't leak through.
                var module = new ModuleTypeDatabase("MyLib", "/fake/MyLib.dylib");
                var names = new[] { "Zebra", "Apple", "Mango" };
                foreach (var name in names)
                {
                    var swiftName = SwiftTypeName.FromModuleQualifiedName($"MyLib.{name}");
                    module.RegisterType(swiftName, new TypeRecord
                    {
                        CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.MyLib", name),
                        SwiftTypeName = swiftName,
                        MetadataAccessor = "",
                        Flags = TypeRecordFlags.Frozen,
                        Kind = TypeRecordKind.Struct
                    });
                }

                var path1 = ModuleDatabaseEmitter.Emit(module, dir, NullLogger.Instance);
                var content1 = File.ReadAllText(path1!);

                // Emit again — output must be byte-identical
                File.Delete(path1);
                var path2 = ModuleDatabaseEmitter.Emit(module, dir, NullLogger.Instance);
                var content2 = File.ReadAllText(path2!);

                Assert.Equal(content1, content2);

                // Verify alphabetical order: Apple before Mango before Zebra
                var appleIdx = content1.IndexOf("\"Apple\"", StringComparison.Ordinal);
                var mangoIdx = content1.IndexOf("\"Mango\"", StringComparison.Ordinal);
                var zebraIdx = content1.IndexOf("\"Zebra\"", StringComparison.Ordinal);
                Assert.True(appleIdx < mangoIdx, "Apple should appear before Mango");
                Assert.True(mangoIdx < zebraIdx, "Mango should appear before Zebra");
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task Emit_ProtocolWithInheritedRequirementsOnly_RoundTrips()
        {
            var dir = CreateTempDir();
            try
            {
                var module = new ModuleTypeDatabase("MyLib", "/fake/MyLib.dylib");
                var swiftName = SwiftTypeName.FromModuleQualifiedName("MyLib.Describable");
                var record = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.MyLib", "IDescribable"),
                    SwiftTypeName = swiftName,
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.InheritedRequirementsOnly,
                    Kind = TypeRecordKind.Protocol
                };
                module.RegisterType(swiftName, record);

                var path = ModuleDatabaseEmitter.Emit(module, dir, NullLogger.Instance);
                Assert.NotNull(path);

                // Verify the XML contains the attribute
                var xml = File.ReadAllText(path!);
                Assert.Contains("inheritedRequirementsOnly=\"true\"", xml);

                // Round-trip: load and verify flag survives
                var typeDatabase = new TypeDatabase();
                await typeDatabase.LoadModuleDatabaseFromFile(path);

                Assert.True(typeDatabase.TryGetTypeRecord(swiftName, out var loaded));
                Assert.Equal(TypeRecordKind.Protocol, loaded!.Kind);
                Assert.True(loaded.Flags.HasFlag(TypeRecordFlags.InheritedRequirementsOnly));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task Emit_ProtocolWithoutInheritedRequirementsOnly_DoesNotSetFlag()
        {
            var dir = CreateTempDir();
            try
            {
                var module = new ModuleTypeDatabase("MyLib", "/fake/MyLib.dylib");
                var swiftName = SwiftTypeName.FromModuleQualifiedName("MyLib.Renderable");
                var record = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.MyLib", "IRenderable"),
                    SwiftTypeName = swiftName,
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.None,
                    Kind = TypeRecordKind.Protocol
                };
                module.RegisterType(swiftName, record);

                var path = ModuleDatabaseEmitter.Emit(module, dir, NullLogger.Instance);
                Assert.NotNull(path);

                // Verify the XML does NOT contain the attribute
                var xml = File.ReadAllText(path!);
                Assert.DoesNotContain("inheritedRequirementsOnly", xml);

                // Round-trip: flag should not be set
                var typeDatabase = new TypeDatabase();
                await typeDatabase.LoadModuleDatabaseFromFile(path);

                Assert.True(typeDatabase.TryGetTypeRecord(swiftName, out var loaded));
                Assert.False(loaded!.Flags.HasFlag(TypeRecordFlags.InheritedRequirementsOnly));
            }
            finally { Directory.Delete(dir, true); }
        }

        private static string CreateTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"mdb_emit_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
