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
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Widget"),
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
                Assert.Equal("TestModule", loaded.CSharpTypeName.Namespace);
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
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MyLib", "Manager"),
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
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MyLib", "Color"),
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
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MyLib", "IConfigurable"),
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
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Nuke", "ImageRequest_UserInfoKey"),
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
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MyLib", "AnyHashable"),
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
                        CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MyLib", name),
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
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MyLib", "IDescribable"),
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
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MyLib", "IRenderable"),
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

        [Fact]
        public async Task Emit_NonCopyableStruct_RoundTrips()
        {
            var dir = CreateTempDir();
            try
            {
                var module = new ModuleTypeDatabase("MyLib", "/fake/MyLib.dylib");
                var swiftName = SwiftTypeName.FromModuleQualifiedName("MyLib.UniqueHandle");
                var record = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MyLib", "UniqueHandle"),
                    SwiftTypeName = swiftName,
                    MetadataAccessor = "$s5MyLib12UniqueHandleVMa",
                    Flags = TypeRecordFlags.Frozen | TypeRecordFlags.NonCopyable,
                    Kind = TypeRecordKind.Struct
                };
                module.RegisterType(swiftName, record);

                var path = ModuleDatabaseEmitter.Emit(module, dir, NullLogger.Instance);
                Assert.NotNull(path);

                var xml = File.ReadAllText(path!);
                Assert.Contains("nonCopyable=\"true\"", xml);

                // Round-trip: load and verify NonCopyable flag survives
                var typeDatabase = new TypeDatabase();
                await typeDatabase.LoadModuleDatabaseFromFile(path);

                Assert.True(typeDatabase.TryGetTypeRecord(swiftName, out var loaded));
                Assert.Equal(TypeRecordKind.Struct, loaded!.Kind);
                Assert.True(loaded.Flags.HasFlag(TypeRecordFlags.NonCopyable));
                Assert.True(loaded.Flags.HasFlag(TypeRecordFlags.Frozen));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task Emit_CopyableStruct_DoesNotSetNonCopyableFlag()
        {
            var dir = CreateTempDir();
            try
            {
                var module = new ModuleTypeDatabase("MyLib", "/fake/MyLib.dylib");
                var swiftName = SwiftTypeName.FromModuleQualifiedName("MyLib.Point");
                var record = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MyLib", "Point"),
                    SwiftTypeName = swiftName,
                    MetadataAccessor = "$s5MyLib5PointVMa",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                };
                module.RegisterType(swiftName, record);

                var path = ModuleDatabaseEmitter.Emit(module, dir, NullLogger.Instance);
                Assert.NotNull(path);

                var xml = File.ReadAllText(path!);
                Assert.DoesNotContain("nonCopyable", xml);

                var typeDatabase = new TypeDatabase();
                await typeDatabase.LoadModuleDatabaseFromFile(path);

                Assert.True(typeDatabase.TryGetTypeRecord(swiftName, out var loaded));
                Assert.False(loaded!.Flags.HasFlag(TypeRecordFlags.NonCopyable));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task Emit_InlineSize_RoundTrips()
        {
            var dir = CreateTempDir();
            try
            {
                var module = new ModuleTypeDatabase("MyLib", "/fake/MyLib.dylib");
                var swiftName = SwiftTypeName.FromModuleQualifiedName("MyLib.TwoWordStruct");
                var record = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MyLib", "TwoWordStruct"),
                    SwiftTypeName = swiftName,
                    MetadataAccessor = "$s5MyLib14TwoWordStructV",
                    Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                    Kind = TypeRecordKind.Struct,
                    InlineSize = 16
                };
                module.RegisterType(swiftName, record);

                var path = ModuleDatabaseEmitter.Emit(module, dir, NullLogger.Instance);
                Assert.NotNull(path);

                var xml = File.ReadAllText(path!);
                Assert.Contains("inlineSize=\"16\"", xml);

                // Round-trip: load and verify InlineSize survives
                var typeDatabase = new TypeDatabase();
                await typeDatabase.LoadModuleDatabaseFromFile(path);

                Assert.True(typeDatabase.TryGetTypeRecord(swiftName, out var loaded));
                Assert.Equal(16, loaded!.InlineSize);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task Emit_WithoutInlineSize_DoesNotEmitAttribute()
        {
            var dir = CreateTempDir();
            try
            {
                var module = new ModuleTypeDatabase("MyLib", "/fake/MyLib.dylib");
                var swiftName = SwiftTypeName.FromModuleQualifiedName("MyLib.SimpleStruct");
                var record = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MyLib", "SimpleStruct"),
                    SwiftTypeName = swiftName,
                    MetadataAccessor = "$s5MyLib12SimpleStructV",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                    // No InlineSize
                };
                module.RegisterType(swiftName, record);

                var path = ModuleDatabaseEmitter.Emit(module, dir, NullLogger.Instance);
                Assert.NotNull(path);

                var xml = File.ReadAllText(path!);
                Assert.DoesNotContain("inlineSize", xml);

                var typeDatabase = new TypeDatabase();
                await typeDatabase.LoadModuleDatabaseFromFile(path);

                Assert.True(typeDatabase.TryGetTypeRecord(swiftName, out var loaded));
                Assert.Null(loaded!.InlineSize);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task Emit_EmittedClassMethods_EmptyList_RoundTripsAsEmptyNotNull()
        {
            // A class whose binding emitted zero instance methods (e.g. all candidates skipped
            // by validation gates) must serialize as an EXPLICIT empty <emittedMethods/> element,
            // not be omitted. Omission round-trips back to null, which the cross-module verifier
            // treats as a legacy database and trusts the Swift IsOverride bit — silently
            // reopening CS0115 for any derived class that overrides into this parent.
            var dir = CreateTempDir();
            try
            {
                var module = new ModuleTypeDatabase("MyLib", "/fake/MyLib.dylib");
                var swiftName = SwiftTypeName.FromModuleQualifiedName("MyLib.SilentParent");
                var record = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MyLib", "SilentParent"),
                    SwiftTypeName = swiftName,
                    MetadataAccessor = "$s5MyLib12SilentParentC",
                    Flags = TypeRecordFlags.RequiresMemoryManagement,
                    Kind = TypeRecordKind.Class,
                    EmittedClassMethods = new List<EmittedClassMethod>(),
                };
                module.RegisterType(swiftName, record);

                var path = ModuleDatabaseEmitter.Emit(module, dir, NullLogger.Instance);
                Assert.NotNull(path);

                var xml = File.ReadAllText(path!);
                // The element must be present (closing tag in self-closed or open form)
                Assert.Contains("emittedMethods", xml);

                var typeDatabase = new TypeDatabase();
                await typeDatabase.LoadModuleDatabaseFromFile(path);

                Assert.True(typeDatabase.TryGetTypeRecord(swiftName, out var loaded));
                // Must round-trip as a non-null empty list, NOT null. The verifier
                // distinguishes "processed → zero methods → reject override" from
                // "legacy → unverifiable → trust Swift bit".
                Assert.NotNull(loaded!.EmittedClassMethods);
                Assert.Empty(loaded!.EmittedClassMethods!);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task Emit_EmittedClassMethods_WithCSharpName_RoundTrips()
        {
            // Verify both Swift and C# names persist through XML serialization. The cross-module
            // override verifier compares the persisted CSharpName against the derived class's
            // C# name to catch NameProvider renames (property/nested-type collisions, builder
            // patterns) that Swift name + parameter types alone wouldn't detect.
            var dir = CreateTempDir();
            try
            {
                var module = new ModuleTypeDatabase("MyLib", "/fake/MyLib.dylib");
                var swiftName = SwiftTypeName.FromModuleQualifiedName("MyLib.NamedParent");
                var record = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MyLib", "NamedParent"),
                    SwiftTypeName = swiftName,
                    MetadataAccessor = "$s5MyLib11NamedParentC",
                    Flags = TypeRecordFlags.RequiresMemoryManagement,
                    Kind = TypeRecordKind.Class,
                    EmittedClassMethods = new List<EmittedClassMethod>
                    {
                        // Builder rename: Swift `tag()` returning Self collides with a `tag`
                        // property → NameProvider renames to `WithTag`.
                        new("tag", "WithTag", Array.Empty<string>()),
                        new("describe", "Describe", new[] { "Swift.String" }),
                    },
                };
                module.RegisterType(swiftName, record);

                var path = ModuleDatabaseEmitter.Emit(module, dir, NullLogger.Instance);
                Assert.NotNull(path);

                var xml = File.ReadAllText(path!);
                Assert.Contains("csharpName=\"WithTag\"", xml);
                Assert.Contains("csharpName=\"Describe\"", xml);
                Assert.Contains("paramTypes=\"Swift.String\"", xml);

                var typeDatabase = new TypeDatabase();
                await typeDatabase.LoadModuleDatabaseFromFile(path);

                Assert.True(typeDatabase.TryGetTypeRecord(swiftName, out var loaded));
                Assert.NotNull(loaded!.EmittedClassMethods);
                Assert.Equal(2, loaded!.EmittedClassMethods!.Count);

                var tag = loaded!.EmittedClassMethods!.Single(m => m.SwiftName == "tag");
                Assert.Equal("WithTag", tag.CSharpName);
                Assert.Empty(tag.ParameterSwiftTypes);

                var describe = loaded!.EmittedClassMethods!.Single(m => m.SwiftName == "describe");
                Assert.Equal("Describe", describe.CSharpName);
                Assert.Equal(new[] { "Swift.String" }, describe.ParameterSwiftTypes);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task Emit_EmittedClassMethods_NullList_OmitsElement()
        {
            // Null EmittedClassMethods (non-class records, or class records that haven't run
            // through the populator) must NOT emit an <emittedMethods> element. On read, that
            // absence is what tells the verifier "legacy database, fall back to trusting Swift's
            // IsOverride bit". This preserves compatibility with already-published parent NuGets.
            var dir = CreateTempDir();
            try
            {
                var module = new ModuleTypeDatabase("MyLib", "/fake/MyLib.dylib");
                var swiftName = SwiftTypeName.FromModuleQualifiedName("MyLib.LegacyClass");
                var record = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MyLib", "LegacyClass"),
                    SwiftTypeName = swiftName,
                    MetadataAccessor = "$s5MyLib11LegacyClassC",
                    Flags = TypeRecordFlags.RequiresMemoryManagement,
                    Kind = TypeRecordKind.Class,
                    EmittedClassMethods = null,
                };
                module.RegisterType(swiftName, record);

                var path = ModuleDatabaseEmitter.Emit(module, dir, NullLogger.Instance);
                Assert.NotNull(path);

                var xml = File.ReadAllText(path!);
                Assert.DoesNotContain("emittedMethods", xml);

                var typeDatabase = new TypeDatabase();
                await typeDatabase.LoadModuleDatabaseFromFile(path);

                Assert.True(typeDatabase.TryGetTypeRecord(swiftName, out var loaded));
                Assert.Null(loaded!.EmittedClassMethods);
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
