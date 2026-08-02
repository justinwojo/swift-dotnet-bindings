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
        public async Task Emit_OptionSetEnum_PreservesOptionSetFlag()
        {
            // An OptionSet (bridged NS_OPTIONS) SimpleEnum must round-trip its OptionSet flag through
            // the module-database XML, or a cross-module consumer would reconstruct it with the failable
            // guard form and fail to compile against the non-failable OptionSet init(rawValue:).
            var dir = CreateTempDir();
            try
            {
                var module = new ModuleTypeDatabase("MyLib", "/fake/MyLib.dylib");
                var swiftName = SwiftTypeName.FromModuleQualifiedName("MyLib.ShareOptions");
                var record = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MyLib", "ShareOptions"),
                    SwiftTypeName = swiftName,
                    MetadataAccessor = "$s5MyLib12ShareOptionsV",
                    Flags = TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum | TypeRecordFlags.OptionSet,
                    Kind = TypeRecordKind.Enum,
                    RawValueTypeName = "UInt"
                };
                module.RegisterType(swiftName, record);

                var path = ModuleDatabaseEmitter.Emit(module, dir, NullLogger.Instance);
                Assert.NotNull(path);

                var typeDatabase = new TypeDatabase();
                await typeDatabase.LoadModuleDatabaseFromFile(path);

                Assert.True(typeDatabase.TryGetTypeRecord(swiftName, out var loaded));
                Assert.True(loaded!.Flags.HasFlag(TypeRecordFlags.SimpleEnum));
                Assert.True(loaded.Flags.HasFlag(TypeRecordFlags.OptionSet));
                Assert.Equal("UInt", loaded.RawValueTypeName);
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
                var module = new ModuleTypeDatabase("ImagePipeline", "/fake/ImagePipeline.dylib");
                var swiftName = SwiftTypeName.FromModuleQualifiedName("ImagePipeline.ImageRequest.UserInfoKey");
                var record = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("ImagePipeline", "ImageRequest_UserInfoKey"),
                    SwiftTypeName = swiftName,
                    MetadataAccessor = "$s13ImagePipeline12ImageRequestV11UserInfoKeyV",
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

        [Fact]
        public async Task Emit_SuppressedProxies_RoundTripsThroughXml()
        {
            // RealityFoundation suppresses HasCollisionProxy / HasAnchoringProxy when their
            // EveryProtocol conformance can't satisfy a constraint. RealityKit's umbrella-
            // aware existential marshaler will then emit cross-module qualified references
            // (`RealityFoundation.SwiftInterop.HasCollisionProxy`) that the post-pass must
            // strip. The information flows through the dependency XML, so the round trip
            // here is the contract: emit → load → cross-module set.
            var dir = CreateTempDir();
            try
            {
                var module = new ModuleTypeDatabase("RealityFoundation", "/fake/RealityFoundation.dylib");
                var swiftName = SwiftTypeName.FromModuleQualifiedName("RealityFoundation.Anchor");
                module.RegisterType(swiftName, new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("RealityFoundation", "Anchor"),
                    SwiftTypeName = swiftName,
                    MetadataAccessor = "$s17RealityFoundation6AnchorC",
                    Flags = TypeRecordFlags.RequiresMemoryManagement,
                    Kind = TypeRecordKind.Class,
                });

                var suppressed = new HashSet<string>(StringComparer.Ordinal)
                {
                    "HasCollisionProxy",
                    "HasAnchoringProxy",
                    "ComponentProxy",
                };

                var path = ModuleDatabaseEmitter.Emit(
                    module, dir, NullLogger.Instance, suppressed,
                    suppressedProxyNamespace: "RealityFoundation");
                Assert.NotNull(path);

                var xml = File.ReadAllText(path!);
                Assert.Contains("<suppressedProxies namespace=\"RealityFoundation\">", xml);
                Assert.Contains("name=\"ComponentProxy\"", xml);
                Assert.Contains("name=\"HasAnchoringProxy\"", xml);
                Assert.Contains("name=\"HasCollisionProxy\"", xml);

                // Names must be ordered deterministically so XML diffs stay clean across
                // generator runs (HashSet iteration order is not stable).
                var componentIdx = xml.IndexOf("ComponentProxy", StringComparison.Ordinal);
                var hasAnchoringIdx = xml.IndexOf("HasAnchoringProxy", StringComparison.Ordinal);
                var hasCollisionIdx = xml.IndexOf("HasCollisionProxy", StringComparison.Ordinal);
                Assert.True(componentIdx < hasAnchoringIdx);
                Assert.True(hasAnchoringIdx < hasCollisionIdx);

                var typeDatabase = new TypeDatabase();
                await typeDatabase.LoadModuleDatabaseFromFile(path);

                var crossModule = typeDatabase.GetCrossModuleSuppressedProxyClassNames();
                Assert.Equal(3, crossModule.Count);
                Assert.Contains(("RealityFoundation", "HasCollisionProxy"), crossModule);
                Assert.Contains(("RealityFoundation", "HasAnchoringProxy"), crossModule);
                Assert.Contains(("RealityFoundation", "ComponentProxy"), crossModule);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task Emit_NoSuppressedProxies_OmitsElement()
        {
            // When a module's emission suppresses zero proxies, the XML must NOT contain
            // an empty <suppressedProxies> element — that would be carrying noise into the
            // dependency contract. Older databases predate this element; the XML schema
            // treats absence as "no cross-module suppressions to thread through".
            var dir = CreateTempDir();
            try
            {
                var module = new ModuleTypeDatabase("CleanModule", "/fake/CleanModule.dylib");
                var swiftName = SwiftTypeName.FromModuleQualifiedName("CleanModule.Widget");
                module.RegisterType(swiftName, new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("CleanModule", "Widget"),
                    SwiftTypeName = swiftName,
                    MetadataAccessor = "$s11CleanModule6WidgetV",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct,
                });

                var path = ModuleDatabaseEmitter.Emit(module, dir, NullLogger.Instance, suppressedProxyClassNames: null);
                Assert.NotNull(path);

                var xml = File.ReadAllText(path!);
                Assert.DoesNotContain("suppressedProxies", xml);

                var typeDatabase = new TypeDatabase();
                await typeDatabase.LoadModuleDatabaseFromFile(path);
                Assert.Empty(typeDatabase.GetCrossModuleSuppressedProxyClassNames());
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task Emit_SuppressedProxies_UnionsAcrossLoadedDependencies()
        {
            // The cross-module set returned by TypeDatabase concatenates pairs across all
            // loaded module databases. Two dependencies (RealityFoundation, CoreFoundation-like)
            // each contribute their own suppressed proxies; downstream RealityKit picks
            // up the union and uses it for post-pass body stripping. Provenance is
            // preserved per-pair — a proxy class name shared across two dependencies
            // appears as TWO distinct (module, proxy) entries, not deduplicated, so the
            // post-pass can match the qualified form against the originating module only.
            var dirA = CreateTempDir();
            var dirB = CreateTempDir();
            try
            {
                var moduleA = new ModuleTypeDatabase("ModuleA", "/fake/ModuleA.dylib");
                moduleA.RegisterType(
                    SwiftTypeName.FromModuleQualifiedName("ModuleA.WidgetA"),
                    new TypeRecord
                    {
                        CSharpTypeName = CSharpTypeName.FromNamespaceAndName("ModuleA", "WidgetA"),
                        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("ModuleA.WidgetA"),
                        MetadataAccessor = "$s7ModuleA7WidgetAV",
                        Kind = TypeRecordKind.Struct,
                        Flags = TypeRecordFlags.Frozen,
                    });

                var moduleB = new ModuleTypeDatabase("ModuleB", "/fake/ModuleB.dylib");
                moduleB.RegisterType(
                    SwiftTypeName.FromModuleQualifiedName("ModuleB.WidgetB"),
                    new TypeRecord
                    {
                        CSharpTypeName = CSharpTypeName.FromNamespaceAndName("ModuleB", "WidgetB"),
                        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("ModuleB.WidgetB"),
                        MetadataAccessor = "$s7ModuleB7WidgetBV",
                        Kind = TypeRecordKind.Struct,
                        Flags = TypeRecordFlags.Frozen,
                    });

                // ModuleA uses the default-pattern namespace (matches Swift module name).
                // ModuleB uses a CUSTOM C# namespace ("Custom.NestedNs") that diverges from
                // the Swift module name — exercising the schema's `namespace` attribute.
                // The qualified form the post-pass needs to match is keyed on the C#
                // namespace, NOT the Swift module name.
                var pathA = ModuleDatabaseEmitter.Emit(
                    moduleA, dirA, NullLogger.Instance,
                    new HashSet<string>(StringComparer.Ordinal) { "ProxyOne", "ProxyShared" },
                    suppressedProxyNamespace: "ModuleA");
                var pathB = ModuleDatabaseEmitter.Emit(
                    moduleB, dirB, NullLogger.Instance,
                    new HashSet<string>(StringComparer.Ordinal) { "ProxyTwo", "ProxyShared" },
                    suppressedProxyNamespace: "Custom.NestedNs");
                Assert.NotNull(pathA);
                Assert.NotNull(pathB);

                var typeDatabase = new TypeDatabase();
                await typeDatabase.LoadModuleDatabaseFromFile(pathA);
                await typeDatabase.LoadModuleDatabaseFromFile(pathB);

                var crossModule = typeDatabase.GetCrossModuleSuppressedProxyClassNames();
                // ProxyShared appears in both XMLs and is preserved as two distinct
                // (namespace, proxy) pairs — provenance matters because the post-pass only
                // matches the cross-module qualified form `{Namespace}.SwiftInterop.{Proxy}(`,
                // and ModuleA's and ModuleB's namespaces are different (the latter custom).
                Assert.Equal(4, crossModule.Count);
                Assert.Contains(("ModuleA", "ProxyOne"), crossModule);
                Assert.Contains(("Custom.NestedNs", "ProxyTwo"), crossModule);
                Assert.Contains(("ModuleA", "ProxyShared"), crossModule);
                Assert.Contains(("Custom.NestedNs", "ProxyShared"), crossModule);
            }
            finally
            {
                Directory.Delete(dirA, true);
                Directory.Delete(dirB, true);
            }
        }

        [Fact]
        public async Task Emit_SuppressedProxies_NamespaceAttributeOmitted_FallsBackToModuleName()
        {
            // Older databases predate the `namespace` attribute. On read, an absent attribute
            // must fall back to the Swift module name — preserving the default-pattern
            // equivalence under which the schema was originally introduced.
            var dir = CreateTempDir();
            try
            {
                var module = new ModuleTypeDatabase("LegacyDep", "/fake/LegacyDep.dylib");
                module.RegisterType(
                    SwiftTypeName.FromModuleQualifiedName("LegacyDep.Anchor"),
                    new TypeRecord
                    {
                        CSharpTypeName = CSharpTypeName.FromNamespaceAndName("LegacyDep", "Anchor"),
                        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("LegacyDep.Anchor"),
                        MetadataAccessor = "$s9LegacyDep6AnchorC",
                        Flags = TypeRecordFlags.RequiresMemoryManagement,
                        Kind = TypeRecordKind.Class,
                    });

                // Emit WITHOUT a namespace argument — simulates the legacy schema shape.
                var path = ModuleDatabaseEmitter.Emit(
                    module, dir, NullLogger.Instance,
                    new HashSet<string>(StringComparer.Ordinal) { "LegacyProxy" });
                Assert.NotNull(path);

                var xml = File.ReadAllText(path!);
                // No namespace attribute on the element.
                Assert.DoesNotContain("namespace=", xml);
                Assert.Contains("<suppressedProxies>", xml);

                var typeDatabase = new TypeDatabase();
                await typeDatabase.LoadModuleDatabaseFromFile(path);

                var crossModule = typeDatabase.GetCrossModuleSuppressedProxyClassNames();
                // Fallback: namespace = Swift module name.
                Assert.Single(crossModule);
                Assert.Contains(("LegacyDep", "LegacyProxy"), crossModule);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task Emit_WithdrawnType_IsNotAdvertised()
        {
            // A type record is registered BEFORE emission runs, so a type that emission later
            // refuses to declare (malformed ingestion, containment denial, verify-recover
            // withdrawal) would still be advertised to downstream generators. The downstream
            // module then emits generated code naming a type this binding's assembly does not
            // contain — a compile break in code the consumer cannot edit. Withdrawn records must
            // not reach the serialized database.
            var dir = CreateTempDir();
            try
            {
                var module = new ModuleTypeDatabase("PartialModule", "/fake/PartialModule.dylib");
                var kept = SwiftTypeName.FromModuleQualifiedName("PartialModule.Kept");
                var pulled = SwiftTypeName.FromModuleQualifiedName("PartialModule.Pulled");
                module.RegisterType(kept, new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("PartialModule", "Kept"),
                    SwiftTypeName = kept,
                    MetadataAccessor = "$s13PartialModule4KeptV",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct,
                });
                module.RegisterType(pulled, new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("PartialModule", "Pulled"),
                    SwiftTypeName = pulled,
                    MetadataAccessor = "$s13PartialModule6PulledV",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct,
                });

                var path = ModuleDatabaseEmitter.Emit(
                    module, dir, NullLogger.Instance,
                    withdrawnTypeNames: new[] { "PartialModule.Pulled" });
                Assert.NotNull(path);

                var typeDatabase = new TypeDatabase();
                await typeDatabase.LoadModuleDatabaseFromFile(path);

                Assert.True(typeDatabase.TryGetTypeRecord(kept, out _));
                Assert.False(typeDatabase.TryGetTypeRecord(pulled, out _));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task Emit_ResolutionOnlyRecords_SurviveWithdrawalFilter()
        {
            // The filter keys on WITHDRAWAL, never on "did this emission declare a C# type".
            // Several records are resolution-only by design: a type owned by the supplement
            // package is declared there, and a view type is projected through the generated
            // bridge rather than declared. Both are declaration-less here yet remain resolvable
            // identities a downstream module must look up, so they must survive even when a
            // sibling type IS withdrawn in the same emission.
            var dir = CreateTempDir();
            try
            {
                var module = new ModuleTypeDatabase("MixedModule", "/fake/MixedModule.dylib");
                var resolutionOnly = SwiftTypeName.FromModuleQualifiedName("MixedModule.ExternallyDeclared");
                var withdrawnName = SwiftTypeName.FromModuleQualifiedName("MixedModule.Withdrawn");
                module.RegisterType(resolutionOnly, new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MixedModule", "ExternallyDeclared"),
                    SwiftTypeName = resolutionOnly,
                    MetadataAccessor = "$s11MixedModule18ExternallyDeclaredC",
                    Flags = TypeRecordFlags.RequiresMemoryManagement,
                    Kind = TypeRecordKind.Class,
                });
                module.RegisterType(withdrawnName, new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MixedModule", "Withdrawn"),
                    SwiftTypeName = withdrawnName,
                    MetadataAccessor = "$s11MixedModule9WithdrawnC",
                    Flags = TypeRecordFlags.RequiresMemoryManagement,
                    Kind = TypeRecordKind.Class,
                });

                var path = ModuleDatabaseEmitter.Emit(
                    module, dir, NullLogger.Instance,
                    withdrawnTypeNames: new[] { "MixedModule.Withdrawn" });
                Assert.NotNull(path);

                var typeDatabase = new TypeDatabase();
                await typeDatabase.LoadModuleDatabaseFromFile(path);

                Assert.True(typeDatabase.TryGetTypeRecord(resolutionOnly, out _));
                Assert.False(typeDatabase.TryGetTypeRecord(withdrawnName, out _));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task Emit_NoWithdrawals_EmitsEveryRecord()
        {
            // The common path: nothing was withdrawn, so the filter must be a strict no-op —
            // null and empty must both behave identically to the pre-filter emission.
            foreach (var withdrawals in new[] { (IReadOnlyCollection<string>)null!, Array.Empty<string>() })
            {
                var dir = CreateTempDir();
                try
                {
                    var module = new ModuleTypeDatabase("HealthyModule", "/fake/HealthyModule.dylib");
                    var a = SwiftTypeName.FromModuleQualifiedName("HealthyModule.Alpha");
                    var b = SwiftTypeName.FromModuleQualifiedName("HealthyModule.Beta");
                    module.RegisterType(a, new TypeRecord
                    {
                        CSharpTypeName = CSharpTypeName.FromNamespaceAndName("HealthyModule", "Alpha"),
                        SwiftTypeName = a,
                        MetadataAccessor = "$s13HealthyModule5AlphaV",
                        Flags = TypeRecordFlags.Frozen,
                        Kind = TypeRecordKind.Struct,
                    });
                    module.RegisterType(b, new TypeRecord
                    {
                        CSharpTypeName = CSharpTypeName.FromNamespaceAndName("HealthyModule", "Beta"),
                        SwiftTypeName = b,
                        MetadataAccessor = "$s13HealthyModule4BetaV",
                        Flags = TypeRecordFlags.Frozen,
                        Kind = TypeRecordKind.Struct,
                    });

                    var path = ModuleDatabaseEmitter.Emit(
                        module, dir, NullLogger.Instance, withdrawnTypeNames: withdrawals);
                    Assert.NotNull(path);

                    var typeDatabase = new TypeDatabase();
                    await typeDatabase.LoadModuleDatabaseFromFile(path);
                    Assert.True(typeDatabase.TryGetTypeRecord(a, out _));
                    Assert.True(typeDatabase.TryGetTypeRecord(b, out _));
                }
                finally { Directory.Delete(dir, true); }
            }
        }

        [Fact]
        public async Task Emit_UnknownWithdrawnName_DoesNotDropUnrelatedRecords()
        {
            // Withdrawal names are matched on the module-qualified Swift name. A name that
            // matches no record (a withdrawal in a different module, or a bare type name) must
            // drop nothing — a loose match would silently amputate the dependency contract.
            var dir = CreateTempDir();
            try
            {
                var module = new ModuleTypeDatabase("StableModule", "/fake/StableModule.dylib");
                var widget = SwiftTypeName.FromModuleQualifiedName("StableModule.Widget");
                module.RegisterType(widget, new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("StableModule", "Widget"),
                    SwiftTypeName = widget,
                    MetadataAccessor = "$s12StableModule6WidgetV",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct,
                });

                var path = ModuleDatabaseEmitter.Emit(
                    module, dir, NullLogger.Instance,
                    withdrawnTypeNames: new[] { "Widget", "OtherModule.Widget" });
                Assert.NotNull(path);

                var typeDatabase = new TypeDatabase();
                await typeDatabase.LoadModuleDatabaseFromFile(path);
                Assert.True(typeDatabase.TryGetTypeRecord(widget, out _));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_EveryRecordWithdrawn_EmitsNoDatabase()
        {
            // If emission withdrew everything, there is no surface left to advertise. Emitting an
            // empty database would still assert "this module was processed" to a downstream
            // resolver; returning null matches the existing zero-record contract instead.
            var dir = CreateTempDir();
            try
            {
                var module = new ModuleTypeDatabase("EmptiedModule", "/fake/EmptiedModule.dylib");
                var only = SwiftTypeName.FromModuleQualifiedName("EmptiedModule.Only");
                module.RegisterType(only, new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("EmptiedModule", "Only"),
                    SwiftTypeName = only,
                    MetadataAccessor = "$s13EmptiedModule4OnlyV",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct,
                });

                var path = ModuleDatabaseEmitter.Emit(
                    module, dir, NullLogger.Instance,
                    withdrawnTypeNames: new[] { "EmptiedModule.Only" });

                Assert.Null(path);
                Assert.Empty(Directory.GetFiles(dir));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task Emit_RenamedMembers_RoundTripIsLossless()
        {
            // A member rename is a function of the whole sibling set, which a downstream module
            // cannot see — so the producing module's decision has to survive the XML verbatim
            // rather than be recomputed on load.
            var dir = CreateTempDir();
            try
            {
                var module = new ModuleTypeDatabase("RenameModule", "/fake/RenameModule.dylib");
                var swiftName = SwiftTypeName.FromModuleQualifiedName("RenameModule.Endpoint");
                module.RegisterType(swiftName, new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("RenameModule", "Endpoint"),
                    SwiftTypeName = swiftName,
                    MetadataAccessor = "$s12RenameModule8EndpointV",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct,
                    RenamedMembers = new[]
                    {
                        new RenamedMember(RenamedMemberKind.Property, "URL", false, "Url2",
                            NameCollisionScheme.CaseOnlyMemberCollision.ToString()),
                        new RenamedMember(RenamedMemberKind.Property, "endpoint", true, "EndpointValue",
                            NameCollisionScheme.PropertyValueSuffix.ToString()),
                    },
                });

                var path = ModuleDatabaseEmitter.Emit(module, dir, NullLogger.Instance);
                var typeDatabase = new TypeDatabase();
                await typeDatabase.LoadModuleDatabaseFromFile(path!);

                Assert.True(typeDatabase.TryGetTypeRecord(swiftName, out var loaded));
                var renames = loaded!.RenamedMembers;
                Assert.NotNull(renames);
                Assert.Equal(2, renames!.Count);

                var caseOnly = renames.Single(r => r.SwiftName == "URL");
                Assert.Equal(RenamedMemberKind.Property, caseOnly.Kind);
                Assert.Equal("Url2", caseOnly.CSharpName);
                Assert.False(caseOnly.IsStatic);
                Assert.Equal(NameCollisionScheme.CaseOnlyMemberCollision.ToString(), caseOnly.Scheme);

                // Staticness is part of the identity: Swift allows a static and an instance member
                // to share an identifier, so dropping it would make the entry ambiguous.
                var valueSuffixed = renames.Single(r => r.SwiftName == "endpoint");
                Assert.True(valueSuffixed.IsStatic);
                Assert.Equal("EndpointValue", valueSuffixed.CSharpName);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task Emit_NoRenamedMembers_RoundTripsAsEmptyNotNull()
        {
            // Empty and null carry different claims: empty means "this type was processed and
            // renamed nothing", null means "written by a generator that predates the ledger".
            // Collapsing them would let a downstream module read silence as a fact.
            var dir = CreateTempDir();
            try
            {
                var module = new ModuleTypeDatabase("QuietModule", "/fake/QuietModule.dylib");
                var swiftName = SwiftTypeName.FromModuleQualifiedName("QuietModule.Plain");
                module.RegisterType(swiftName, new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("QuietModule", "Plain"),
                    SwiftTypeName = swiftName,
                    MetadataAccessor = "$s11QuietModule5PlainV",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct,
                    RenamedMembers = Array.Empty<RenamedMember>(),
                });

                var path = ModuleDatabaseEmitter.Emit(module, dir, NullLogger.Instance);
                var typeDatabase = new TypeDatabase();
                await typeDatabase.LoadModuleDatabaseFromFile(path!);

                Assert.True(typeDatabase.TryGetTypeRecord(swiftName, out var loaded));
                Assert.NotNull(loaded!.RenamedMembers);
                Assert.Empty(loaded.RenamedMembers!);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task Load_DatabaseWithoutRenamedMembersElement_YieldsNull()
        {
            // The legacy shape: a database emitted before the ledger existed. It must load as
            // "unknown", not as the positive claim "nothing was renamed".
            var dir = CreateTempDir();
            try
            {
                var module = new ModuleTypeDatabase("LegacyModule", "/fake/LegacyModule.dylib");
                var swiftName = SwiftTypeName.FromModuleQualifiedName("LegacyModule.Old");
                module.RegisterType(swiftName, new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("LegacyModule", "Old"),
                    SwiftTypeName = swiftName,
                    MetadataAccessor = "$s12LegacyModule3OldV",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct,
                });

                var path = ModuleDatabaseEmitter.Emit(module, dir, NullLogger.Instance);
                Assert.DoesNotContain("renamedMembers", await File.ReadAllTextAsync(path!));

                var typeDatabase = new TypeDatabase();
                await typeDatabase.LoadModuleDatabaseFromFile(path!);

                Assert.True(typeDatabase.TryGetTypeRecord(swiftName, out var loaded));
                Assert.Null(loaded!.RenamedMembers);
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
