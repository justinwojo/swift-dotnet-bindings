// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.IO;
using Xunit;

namespace BindingsGeneration.Tests
{
public class TypeDatabaseTests
{
    // Keep these fixtures aligned with TypeDatabase.ValidateXmlSchema:
    // - root node: <swifttypedatabase version="1.0" moduleName modulePath>
    // - child node: <entities> with at least one <entity>
    // - each entity must include a <typedeclaration> element
    private const string ValidXmlDatabase = """
        <swifttypedatabase version="1.0" moduleName="TestModule" modulePath="/tmp/TestModule.dylib">
          <entities>
            <entity managedTypeName="Widget" managedNameSpace="BindingsGeneration.Tests">
              <typedeclaration module="TestModule" name="Widget" mangledName="$s4Test6WidgetV" frozen="true" requiresMemoryManagement="false" />
            </entity>
          </entities>
        </swifttypedatabase>
        """;

    private const string InvalidXmlDatabase = """
        <swifttypedatabase version="1.0" moduleName="Broken" modulePath="/tmp/Broken.dylib">
          <entities />
        </swifttypedatabase>
        """;

    private const string CoreGraphicsOpaqueHandleXmlDatabase = """
        <swifttypedatabase version="1.0" moduleName="CoreGraphics" modulePath="/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics">
          <entities>
            <entity managedNameSpace="System" managedTypeName="IntPtr">
              <typedeclaration module="CoreGraphics" name="CGImage" mangledName="" frozen="true" requiresMemoryManagement="false" />
            </entity>
            <entity managedNameSpace="System" managedTypeName="IntPtr">
              <typedeclaration module="CoreGraphics" name="CGColor" mangledName="" frozen="true" requiresMemoryManagement="false" />
            </entity>
            <entity managedNameSpace="System" managedTypeName="IntPtr">
              <typedeclaration module="CoreGraphics" name="CGContext" mangledName="" frozen="true" requiresMemoryManagement="false" />
            </entity>
          </entities>
        </swifttypedatabase>
        """;

        [Fact]
        public void AddModuleDatabase_ModuleExists_Throws()
        {
            var typeDatabase = new TypeDatabase();
            var module = new ModuleTypeDatabase("MyModule", "/fake/path");
            typeDatabase.AddModuleDatabase(module);

            var ex = Assert.Throws<Exception>(() => typeDatabase.AddModuleDatabase(module));
            Assert.Contains("already exists in the database", ex.Message);
        }

        [Fact]
        public void IsModuleProcessed_ReturnsTrue_WhenModuleExists()
        {
            var typeDatabase = new TypeDatabase();
            var module = new ModuleTypeDatabase("MyModule", "/fake/path");
            typeDatabase.AddModuleDatabase(module);

            var result = typeDatabase.IsModuleProcessed("MyModule");

            Assert.True(result);
        }

        [Fact]
        public void IsModuleProcessed_ReturnsFalse_WhenModuleDoesNotExist()
        {
            var typeDatabase = new TypeDatabase();

            var result = typeDatabase.IsModuleProcessed("NonExistentModule");

            Assert.False(result);
        }

        [Fact]
        public void TryGetTypeRecord_ReturnsTrue_WhenTypeExists()
        {
            var typeDatabase = new TypeDatabase();
            var module = new ModuleTypeDatabase("TestModule", "/fake/path");
            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyType");
            var myType = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("BindingsGeneration.Tests", "MyType"),
                SwiftTypeName = swiftTypeName,
                MetadataAccessor = "mangledAccessor",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            };
            module.RegisterType(swiftTypeName, myType);
            typeDatabase.AddModuleDatabase(module);

            var found = typeDatabase.TryGetTypeRecord(swiftTypeName, out var record);

            Assert.True(found);
            Assert.NotNull(record);
            Assert.Equal("MyType", record!.CSharpTypeName.Name);
        }

        [Fact]
        public void TryGetTypeRecord_ReturnsFalse_WhenTypeDoesNotExist()
        {
            var typeDatabase = new TypeDatabase();
            var module = new ModuleTypeDatabase("TestModule", "/fake/path");
            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyType");
            typeDatabase.AddModuleDatabase(module);

            var found = typeDatabase.TryGetTypeRecord(swiftTypeName, out var record);

            Assert.False(found);
            Assert.Null(record);
        }

        [Fact]
        public void AddOutOfModuleTypes_TryGetTypeRecord_ReturnsTrue_WhenOutOfModuleTypeExists()
        {
            var typeDatabase = new TypeDatabase();
            var module = new ModuleTypeDatabase("TestModule", "/fake/path");
            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("AnotherModule.MyOutOfModuleType");
            typeDatabase.AddModuleDatabase(module);

            var outOfModuleRecord = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("BindingsGeneration.Tests", "MyOutOfModuleType"),
                SwiftTypeName = swiftTypeName,
                MetadataAccessor = "mangledOutOfModule",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            };

            typeDatabase.AddOutOfModuleTypes(new[]
            {
                (swiftTypeName, outOfModuleRecord)
            });

            var found = typeDatabase.TryGetTypeRecord(swiftTypeName, out var record);

            Assert.True(found);
            Assert.NotNull(record);
            Assert.Equal("MyOutOfModuleType", record!.CSharpTypeName.Name);
            Assert.Equal("AnotherModule", record.SwiftTypeName.Module);
        }

        [Fact]
        public void IsTypeProcessed_ReturnsTrue_WhenTypeProcessed()
        {
            // Arrange
            var typeDatabase = new TypeDatabase();
            var module = new ModuleTypeDatabase("TestModule", "/fake/path");
            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ProcessedType");
            module.RegisterType(swiftTypeName, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ProcessedType"),
                SwiftTypeName = swiftTypeName,
                MetadataAccessor = string.Empty,
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            });
            typeDatabase.AddModuleDatabase(module);

            var result = typeDatabase.IsTypeProcessed(swiftTypeName);

            Assert.True(result);
        }

        [Fact]
        public void IsTypeProcessed_ReturnsFalse_WhenTypeNotProcessed()
        {
            var typeDatabase = new TypeDatabase();
            var module = new ModuleTypeDatabase("TestModule", "/fake/path");
            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.UnprocessedType");

            typeDatabase.AddModuleDatabase(module);

            var result = typeDatabase.IsTypeProcessed(swiftTypeName);

            Assert.False(result);
        }

        [Fact]
        public void GetLibraryPath_ReturnsCorrectPath_WhenModuleExists()
        {
            var typeDatabase = new TypeDatabase();
            var module = new ModuleTypeDatabase("TestModule", "/fake/path");
            typeDatabase.AddModuleDatabase(module);

            var path = typeDatabase.GetLibraryPath("TestModule");

            Assert.Equal("/fake/path", path);
        }

        [Fact]
        public void GetLibraryPath_Throws_WhenModuleDoesNotExist()
        {
            var typeDatabase = new TypeDatabase();

            var ex = Assert.Throws<Exception>(() => typeDatabase.GetLibraryPath("NonExistentModule"));
            Assert.Contains("Module NonExistentModule does not exist in the database.", ex.Message);
        }

        [Fact]
        public void TryGetTypeRecord_UsesModuleAlias_WhenAliasModuleProvided()
        {
            var typeDatabase = new TypeDatabase();
            var module = new ModuleTypeDatabase("CoreGraphics", "/fake/path");
            var canonicalTypeName = SwiftTypeName.FromModuleQualifiedName("CoreGraphics.CGSize");
            var aliasTypeName = SwiftTypeName.FromModuleQualifiedName("CoreFoundation.CGSize");
            var record = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("CoreGraphics", "CGSize"),
                SwiftTypeName = canonicalTypeName,
                MetadataAccessor = "mangledAccessor",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            };
            module.RegisterType(canonicalTypeName, record);
            typeDatabase.AddModuleDatabase(module);

            var found = typeDatabase.TryGetTypeRecord(aliasTypeName, out var aliasedRecord);

            Assert.True(found);
            Assert.NotNull(aliasedRecord);
            Assert.Equal("CGSize", aliasedRecord!.CSharpTypeName.Name);
        }

        [Fact]
        public void IsTypeProcessed_UsesModuleAlias_WhenAliasModuleProvided()
        {
            var typeDatabase = new TypeDatabase();
            var module = new ModuleTypeDatabase("CoreGraphics", "/fake/path");
            var canonicalTypeName = SwiftTypeName.FromModuleQualifiedName("CoreGraphics.CGSize");
            var aliasTypeName = SwiftTypeName.FromModuleQualifiedName("CoreFoundation.CGSize");
            module.RegisterType(canonicalTypeName, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("CoreGraphics", "CGSize"),
                SwiftTypeName = canonicalTypeName,
                MetadataAccessor = "mangledAccessor",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
            typeDatabase.AddModuleDatabase(module);

            var result = typeDatabase.IsTypeProcessed(aliasTypeName);

            Assert.True(result);
        }

        [Fact]
        public void AddOutOfModuleTypes_DuplicateType_DoesNotOverrideExistingRecord()
        {
            var typeDatabase = new TypeDatabase();
            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("AnotherModule.SharedType");
            var firstRecord = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("BindingsGeneration.Tests", "First"),
                SwiftTypeName = swiftTypeName,
                MetadataAccessor = "first",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            };
            var secondRecord = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("BindingsGeneration.Tests", "Second"),
                SwiftTypeName = swiftTypeName,
                MetadataAccessor = "second",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            };

            typeDatabase.AddOutOfModuleTypes(new[] { (swiftTypeName, firstRecord) });
            typeDatabase.AddOutOfModuleTypes(new[] { (swiftTypeName, secondRecord) });

            var found = typeDatabase.TryGetTypeRecord(swiftTypeName, out var record);
            Assert.True(found);
            Assert.NotNull(record);
            Assert.Equal("First", record!.CSharpTypeName.Name);
            Assert.Equal("first", record.MetadataAccessor);
        }

        [Fact]
        public async Task LoadModuleDatabaseFromFile_ValidXml_LoadsModuleAndType()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                await File.WriteAllTextAsync(filePath, ValidXmlDatabase);

                var typeDatabase = new TypeDatabase();
                await typeDatabase.LoadModuleDatabaseFromFile(filePath);

                Assert.True(typeDatabase.IsModuleProcessed("TestModule"));
                var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Widget");
                Assert.True(typeDatabase.TryGetTypeRecord(swiftTypeName, out var record));
                Assert.NotNull(record);
                Assert.Equal("Widget", record!.CSharpTypeName.Name);
                Assert.Equal("/tmp/TestModule.dylib", typeDatabase.GetLibraryPath("TestModule"));
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public async Task LoadModuleDatabaseFromFile_InvalidXmlSchema_Throws()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                await File.WriteAllTextAsync(filePath, InvalidXmlDatabase);

                var typeDatabase = new TypeDatabase();
                var ex = await Assert.ThrowsAsync<Exception>(() => typeDatabase.LoadModuleDatabaseFromFile(filePath));
                Assert.Contains("Invalid XML schema", ex.Message);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public async Task LoadModuleDatabaseFromFile_CoreGraphicsOpaqueHandleTypes_MapToIntPtr()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                await File.WriteAllTextAsync(filePath, CoreGraphicsOpaqueHandleXmlDatabase);

                var typeDatabase = new TypeDatabase();
                await typeDatabase.LoadModuleDatabaseFromFile(filePath);

                var expectedTypes = new[] { "CGImage", "CGColor", "CGContext" };
                foreach (var typeName in expectedTypes)
                {
                    var swiftTypeName = SwiftTypeName.FromModuleQualifiedName($"CoreGraphics.{typeName}");
                    Assert.True(typeDatabase.TryGetTypeRecord(swiftTypeName, out var record));
                    Assert.NotNull(record);
                    Assert.Equal("System", record!.CSharpTypeName.Namespace);
                    Assert.Equal("IntPtr", record.CSharpTypeName.Name);
                }
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public async Task LoadModuleDatabaseFromFile_KindClass_MapsToTypeRecordKindClass()
        {
            var xml = """
                <swifttypedatabase version="1.0" moduleName="UIKit" modulePath="/System/Library/Frameworks/UIKit.framework/UIKit">
                  <entities>
                    <entity managedNameSpace="UIKit" managedTypeName="UIImage">
                      <typedeclaration kind="class" name="UIImage" module="UIKit" mangledName="$sSo7UIImageC" frozen="false" requiresMemoryManagement="true" objcBridged="true" />
                    </entity>
                  </entities>
                </swifttypedatabase>
                """;
            var filePath = Path.GetTempFileName();
            try
            {
                await File.WriteAllTextAsync(filePath, xml);
                var typeDatabase = new TypeDatabase();
                await typeDatabase.LoadModuleDatabaseFromFile(filePath);

                var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("UIKit.UIImage");
                Assert.True(typeDatabase.TryGetTypeRecord(swiftTypeName, out var record));
                Assert.Equal(TypeRecordKind.Class, record!.Kind);
                Assert.True((record.Flags & TypeRecordFlags.ObjCBridged) != 0);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public async Task LoadModuleDatabaseFromFile_KindEnum_MapsToTypeRecordKindEnum()
        {
            var xml = """
                <swifttypedatabase version="1.0" moduleName="Swift" modulePath="/usr/lib/swift/libswiftCore.dylib">
                  <entities>
                    <entity managedNameSpace="Swift" managedTypeName="Result">
                      <typedeclaration kind="enum" name="Result" module="Swift" mangledName="$ss6ResultO" frozen="true" requiresMemoryManagement="true" />
                    </entity>
                  </entities>
                </swifttypedatabase>
                """;
            var filePath = Path.GetTempFileName();
            try
            {
                await File.WriteAllTextAsync(filePath, xml);
                var typeDatabase = new TypeDatabase();
                await typeDatabase.LoadModuleDatabaseFromFile(filePath);

                var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Result");
                Assert.True(typeDatabase.TryGetTypeRecord(swiftTypeName, out var record));
                Assert.Equal(TypeRecordKind.Enum, record!.Kind);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public async Task LoadModuleDatabaseFromFile_KindMissing_DefaultsToStruct()
        {
            // The ValidXmlDatabase fixture has no kind attribute — should default to Struct
            var filePath = Path.GetTempFileName();
            try
            {
                await File.WriteAllTextAsync(filePath, ValidXmlDatabase);
                var typeDatabase = new TypeDatabase();
                await typeDatabase.LoadModuleDatabaseFromFile(filePath);

                var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Widget");
                Assert.True(typeDatabase.TryGetTypeRecord(swiftTypeName, out var record));
                Assert.Equal(TypeRecordKind.Struct, record!.Kind);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void TryGetTypeRecord_ResolvesRefSuffixAlias_BothDirections()
        {
            var typeDatabase = new TypeDatabase();
            var module = new ModuleTypeDatabase("CoreGraphics", "/fake/path");
            var refTypeName = SwiftTypeName.FromModuleQualifiedName("CoreGraphics.CGImageRef");
            module.RegisterType(refTypeName, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "IntPtr"),
                SwiftTypeName = refTypeName,
                MetadataAccessor = string.Empty,
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
            typeDatabase.AddModuleDatabase(module);

            var noRefName = SwiftTypeName.FromModuleQualifiedName("CoreGraphics.CGImage");
            Assert.True(typeDatabase.TryGetTypeRecord(noRefName, out var noRefRecord));
            Assert.NotNull(noRefRecord);
            Assert.Equal("IntPtr", noRefRecord!.CSharpTypeName.Name);

            Assert.True(typeDatabase.TryGetTypeRecord(refTypeName, out var refRecord));
            Assert.NotNull(refRecord);
            Assert.Equal("IntPtr", refRecord!.CSharpTypeName.Name);
        }

        [Fact]
        public void IsModuleLoaded_ReturnsTrueForLoadedModule()
        {
            var typeDatabase = new TypeDatabase();
            var module = new ModuleTypeDatabase("Foundation", "/fake/path");
            typeDatabase.AddModuleDatabase(module);

            Assert.True(typeDatabase.IsModuleLoaded("Foundation"));
        }

        [Fact]
        public void IsModuleLoaded_ReturnsFalseForUnknownModule()
        {
            var typeDatabase = new TypeDatabase();

            Assert.False(typeDatabase.IsModuleLoaded("Nonexistent"));
        }

        [Fact]
        public async Task LoadModuleDatabaseFromFile_KindProtocol_MapsToTypeRecordKindProtocol()
        {
            var xml = """
                <swifttypedatabase version="1.0" moduleName="MyLib" modulePath="/fake/MyLib.dylib">
                  <entities>
                    <entity managedNameSpace="MyLib" managedTypeName="IConfigurable">
                      <typedeclaration kind="protocol" name="Configurable" module="MyLib" mangledName="$s5MyLib12ConfigurableP" frozen="false" requiresMemoryManagement="false" hasAssociatedTypes="true" />
                    </entity>
                  </entities>
                </swifttypedatabase>
                """;
            var filePath = Path.GetTempFileName();
            try
            {
                await File.WriteAllTextAsync(filePath, xml);
                var typeDatabase = new TypeDatabase();
                await typeDatabase.LoadModuleDatabaseFromFile(filePath);

                var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("MyLib.Configurable");
                Assert.True(typeDatabase.TryGetTypeRecord(swiftTypeName, out var record));
                Assert.Equal(TypeRecordKind.Protocol, record!.Kind);
                Assert.True((record.Flags & TypeRecordFlags.HasAssociatedTypes) != 0);
            }
            finally { File.Delete(filePath); }
        }

        [Fact]
        public async Task LoadModuleDatabaseFromFile_KindExistential_MapsToTypeRecordKindExistential()
        {
            var xml = """
                <swifttypedatabase version="1.0" moduleName="MyLib" modulePath="/fake/MyLib.dylib">
                  <entities>
                    <entity managedNameSpace="MyLib" managedTypeName="AnyHashable">
                      <typedeclaration kind="existential" name="AnyHashable" module="MyLib" mangledName="" frozen="true" requiresMemoryManagement="false" />
                    </entity>
                  </entities>
                </swifttypedatabase>
                """;
            var filePath = Path.GetTempFileName();
            try
            {
                await File.WriteAllTextAsync(filePath, xml);
                var typeDatabase = new TypeDatabase();
                await typeDatabase.LoadModuleDatabaseFromFile(filePath);

                var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("MyLib.AnyHashable");
                Assert.True(typeDatabase.TryGetTypeRecord(swiftTypeName, out var record));
                Assert.Equal(TypeRecordKind.Existential, record!.Kind);
            }
            finally { File.Delete(filePath); }
        }

        [Fact]
        public async Task LoadModuleDatabaseFromFile_SimpleEnumFlag_ParsesCorrectly()
        {
            var xml = """
                <swifttypedatabase version="1.0" moduleName="MyLib" modulePath="/fake/MyLib.dylib">
                  <entities>
                    <entity managedNameSpace="MyLib" managedTypeName="Color">
                      <typedeclaration kind="enum" name="Color" module="MyLib" mangledName="" frozen="true" requiresMemoryManagement="false" simpleEnum="true" rawValueType="Int" />
                    </entity>
                  </entities>
                </swifttypedatabase>
                """;
            var filePath = Path.GetTempFileName();
            try
            {
                await File.WriteAllTextAsync(filePath, xml);
                var typeDatabase = new TypeDatabase();
                await typeDatabase.LoadModuleDatabaseFromFile(filePath);

                var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("MyLib.Color");
                Assert.True(typeDatabase.TryGetTypeRecord(swiftTypeName, out var record));
                Assert.Equal(TypeRecordKind.Enum, record!.Kind);
                Assert.True((record.Flags & TypeRecordFlags.SimpleEnum) != 0);
                Assert.Equal("Int", record.RawValueTypeName);
            }
            finally { File.Delete(filePath); }
        }

        [Fact]
        public async Task LoadModuleDatabaseFromFile_MissingOptionalFlags_DefaultsCorrectly()
        {
            // Existing databases don't have hasAssociatedTypes/simpleEnum/rawValueType — should default gracefully
            var filePath = Path.GetTempFileName();
            try
            {
                await File.WriteAllTextAsync(filePath, ValidXmlDatabase);
                var typeDatabase = new TypeDatabase();
                await typeDatabase.LoadModuleDatabaseFromFile(filePath);

                var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Widget");
                Assert.True(typeDatabase.TryGetTypeRecord(swiftTypeName, out var record));
                Assert.False((record!.Flags & TypeRecordFlags.HasAssociatedTypes) != 0);
                Assert.False((record.Flags & TypeRecordFlags.SimpleEnum) != 0);
                Assert.Null(record.RawValueTypeName);
            }
            finally { File.Delete(filePath); }
        }

        [Fact]
        public void GetAllTypeRecords_ReturnsRegisteredRecords()
        {
            var module = new ModuleTypeDatabase("TestModule", "/fake/path");
            var swiftName1 = SwiftTypeName.FromModuleQualifiedName("TestModule.Widget");
            var swiftName2 = SwiftTypeName.FromModuleQualifiedName("TestModule.Gadget");
            module.RegisterType(swiftName1, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Test", "Widget"),
                SwiftTypeName = swiftName1,
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
            module.RegisterType(swiftName2, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Test", "Gadget"),
                SwiftTypeName = swiftName2,
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Class
            });

            var records = module.GetAllTypeRecords().ToList();

            Assert.Equal(2, records.Count);
            Assert.Contains(records, r => r.Value.CSharpTypeName.Name == "Widget");
            Assert.Contains(records, r => r.Value.CSharpTypeName.Name == "Gadget");
        }

        [Fact]
        public void GetAllTypeRecords_EmptyModule_ReturnsEmpty()
        {
            var module = new ModuleTypeDatabase("Empty", "/fake/path");
            var records = module.GetAllTypeRecords().ToList();
            Assert.Empty(records);
        }

        [Theory]
        [InlineData("HealthKit.HKWorkoutActivityType", TypeRecordKind.Enum, true, "UInt")]
        [InlineData("HealthKit.HKWorkoutSessionLocationType", TypeRecordKind.Enum, true, "Int")]
        [InlineData("HealthKit.HKWorkoutSwimmingLocationType", TypeRecordKind.Enum, true, "Int")]
        public async Task HealthKitDatabase_EnumTypes_ResolvesCorrectly(
            string typeName, TypeRecordKind expectedKind, bool expectedSimpleEnum, string expectedRawValue)
        {
            var typeDatabase = new TypeDatabase();
            var dbPath = Path.Combine(TestDbDirectory, "HealthKitDatabase.xml");
            await typeDatabase.LoadModuleDatabaseFromFile(dbPath);

            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(typeName);
            Assert.True(typeDatabase.TryGetTypeRecord(swiftTypeName, out var record),
                $"Type {typeName} should be found in HealthKit database");
            Assert.Equal(expectedKind, record!.Kind);
            Assert.Equal(expectedSimpleEnum, record.Flags.HasFlag(TypeRecordFlags.SimpleEnum));
            Assert.Equal(expectedRawValue, record.RawValueTypeName);
        }

        [Theory]
        [InlineData("simd.simd_float4x4", TypeRecordKind.Struct, true, true, 64)]
        [InlineData("simd.simd_float3", TypeRecordKind.Struct, true, true, 16)]
        [InlineData("simd.simd_quatf", TypeRecordKind.Struct, true, true, 16)]
        public async Task SimdDatabase_StructTypes_ResolvesCorrectly(
            string typeName, TypeRecordKind expectedKind, bool expectedFrozen, bool expectedHasFloatFields, int expectedInlineSize)
        {
            var typeDatabase = new TypeDatabase();
            var dbPath = Path.Combine(TestDbDirectory, "SimdDatabase.xml");
            await typeDatabase.LoadModuleDatabaseFromFile(dbPath);

            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(typeName);
            Assert.True(typeDatabase.TryGetTypeRecord(swiftTypeName, out var record),
                $"Type {typeName} should be found in simd database");
            Assert.Equal(expectedKind, record!.Kind);
            Assert.Equal(expectedFrozen, record.Flags.HasFlag(TypeRecordFlags.Frozen));
            Assert.Equal(expectedHasFloatFields, record.Flags.HasFlag(TypeRecordFlags.HasFloatFields));
            Assert.Equal(expectedInlineSize, record.InlineSize);
        }

        [Fact]
        public async Task SimdDatabase_BoundGenericSIMD3Float_ResolvesToSimdFloat3()
        {
            var typeDatabase = new TypeDatabase();
            var dbPath = Path.Combine(TestDbDirectory, "SimdDatabase.xml");
            await typeDatabase.LoadModuleDatabaseFromFile(dbPath);

            // SIMD3<Float> appears in ABI JSON as a bound generic, but maps to the
            // C simd_float3 typedef. Verify the bound-generic alias resolves correctly.
            var typeSpec = new NamedTypeSpec("Swift.SIMD3");
            typeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Float"));
            var record = typeDatabase.GetTypeRecordOrAnyType(typeSpec);

            Assert.Equal(TypeRecordKind.Struct, record.Kind);
            Assert.True(record.Flags.HasFlag(TypeRecordFlags.Frozen));
            Assert.True(record.Flags.HasFlag(TypeRecordFlags.HasFloatFields));
            Assert.Equal(16, record.InlineSize);
        }

        [Fact]
        public async Task SimdBoundGenericAlias_RoutesThroughAllLookupPaths()
        {
            // Regression test: alias resolver must be wired into TryGetTypeRecord and
            // IsTypeProcessed, not just GetTypeRecordOrAnyType. Return mapping and
            // parameter mapping go through TryGetTypeRecord / IsTypeProcessed.
            var typeDatabase = new TypeDatabase();
            var dbPath = Path.Combine(TestDbDirectory, "SimdDatabase.xml");
            await typeDatabase.LoadModuleDatabaseFromFile(dbPath);

            var typeSpec = new NamedTypeSpec("Swift.SIMD3");
            typeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Float"));

            Assert.True(typeDatabase.TryGetTypeRecord(typeSpec, out var record),
                "TryGetTypeRecord must resolve Swift.SIMD3<Swift.Float> via the bound-generic alias.");
            Assert.Equal("simd.simd_float3", record!.SwiftTypeName.ModuleQualifiedName);

            Assert.True(typeDatabase.IsTypeProcessed(typeSpec),
                "IsTypeProcessed must recognise Swift.SIMD3<Swift.Float> via the bound-generic alias.");
        }

        [Fact]
        public async Task SimdDatabase_Quatf_ProjectsAsSystemNumericsQuaternion()
        {
            // RealityFoundation surfaces simd_quatf for orientation/rotation properties on
            // ForceEffect, GeometricPin, ConvexCast, SampledAnimation<simd_quatf>, etc.
            // Swift's simd_quatf wraps a simd_float4 with imaginary lanes (xi, yj, zk) at
            // indices 0–2 and the real lane (w) at index 3 — bit-compatible with
            // System.Numerics.Quaternion's (X, Y, Z, W) field order.
            var typeDatabase = new TypeDatabase();
            var dbPath = Path.Combine(TestDbDirectory, "SimdDatabase.xml");
            await typeDatabase.LoadModuleDatabaseFromFile(dbPath);

            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("simd.simd_quatf");
            Assert.True(typeDatabase.TryGetTypeRecord(swiftTypeName, out var record),
                "simd.simd_quatf should resolve via SimdDatabase.xml.");
            Assert.Equal("System.Numerics.Quaternion", record!.CSharpTypeName.FullyQualifiedName);
            Assert.Equal("System.Numerics", record.CSharpTypeName.Namespace);
            Assert.Equal("Quaternion", record.CSharpTypeName.Name);
        }

        [Fact]
        public async Task SimdDatabase_Float4x4_ProjectsAsSystemNumericsMatrix4x4()
        {
            // RoomPlan (CapturedRoom.Surface.transform, CapturedRoom.Object.transform) and
            // ARKit both expose simd_float4x4. Projecting onto System.Numerics.Matrix4x4 —
            // 16 contiguous floats in both representations — gives consumers a usable
            // managed type without a custom marshalling helper.
            var typeDatabase = new TypeDatabase();
            var dbPath = Path.Combine(TestDbDirectory, "SimdDatabase.xml");
            await typeDatabase.LoadModuleDatabaseFromFile(dbPath);

            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName("simd.simd_float4x4");
            Assert.True(typeDatabase.TryGetTypeRecord(swiftTypeName, out var record),
                "simd.simd_float4x4 should resolve via SimdDatabase.xml.");
            Assert.Equal("System.Numerics.Matrix4x4", record!.CSharpTypeName.FullyQualifiedName);
            Assert.Equal("System.Numerics", record.CSharpTypeName.Namespace);
            Assert.Equal("Matrix4x4", record.CSharpTypeName.Name);
        }

        [Theory]
        // Token<Kind> is pinned to SwiftBindings.Runtime via TypeOwnerRegistry legacy canonicals,
        // so resolution falls through to the XML module database and preserves the hand-tuned
        // "opaque generic struct" flags.
        [InlineData("ManagedSettings.Token", TypeRecordKind.Struct, false, true)]
        // Application/ActivityCategory/WebDomain are owned by SwiftBindings.Apple. TryGetTypeRecord
        // routes them through AppleSupplementResolver, which sets RequiresMemoryManagement
        // unconditionally and mirrors the manifest's frozen flag (all three manifest entries
        // record frozen: false for these phantom marker structs).
        [InlineData("ManagedSettings.Application", TypeRecordKind.Struct, false, true)]
        [InlineData("ManagedSettings.ActivityCategory", TypeRecordKind.Struct, false, true)]
        [InlineData("ManagedSettings.WebDomain", TypeRecordKind.Struct, false, true)]
        public async Task ManagedSettingsDatabase_TokenAndMarkerTypes_ResolvesCorrectly(
            string typeName, TypeRecordKind expectedKind, bool expectedFrozen, bool expectedRequiresMemory)
        {
            var typeDatabase = new TypeDatabase();
            var dbPath = Path.Combine(TestDbDirectory, "ManagedSettingsDatabase.xml");
            await typeDatabase.LoadModuleDatabaseFromFile(dbPath);

            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(typeName);
            Assert.True(typeDatabase.TryGetTypeRecord(swiftTypeName, out var record),
                $"Type {typeName} should be found in ManagedSettings database");
            Assert.Equal(expectedKind, record!.Kind);
            Assert.Equal(expectedFrozen, record.Flags.HasFlag(TypeRecordFlags.Frozen));
            Assert.Equal(expectedRequiresMemory, record.Flags.HasFlag(TypeRecordFlags.RequiresMemoryManagement));
        }

        [Theory]
        [InlineData("FamilyControls.ApplicationToken", "ManagedSettings.Token<ManagedSettings.Application>")]
        [InlineData("FamilyControls.ActivityCategoryToken", "ManagedSettings.Token<ManagedSettings.ActivityCategory>")]
        [InlineData("FamilyControls.WebDomainToken", "ManagedSettings.Token<ManagedSettings.WebDomain>")]
        public async Task CrossModuleTypeAlias_ResolvesToCanonicalType(string aliasName, string expectedCanonicalName)
        {
            var typeDatabase = new TypeDatabase();
            var dbPath = Path.Combine(TestDbDirectory, "ManagedSettingsDatabase.xml");
            await typeDatabase.LoadModuleDatabaseFromFile(dbPath);

            var aliasTypeName = SwiftTypeName.FromModuleQualifiedName(aliasName);
            Assert.True(typeDatabase.TryGetTypeRecord(aliasTypeName, out var record),
                $"Type alias {aliasName} should resolve via cross-module alias");
            Assert.Equal("Token", record!.CSharpTypeName.Name);

            // Verify alias resolution preserves generic type arguments
            var resolvedName = typeDatabase.TryResolveTypeAlias(aliasTypeName);
            Assert.NotNull(resolvedName);
            Assert.Equal(expectedCanonicalName, resolvedName);
        }

        [Fact]
        public async Task CrossModuleTypeAlias_IsTypeProcessed_ReturnsTrue()
        {
            var typeDatabase = new TypeDatabase();
            var dbPath = Path.Combine(TestDbDirectory, "ManagedSettingsDatabase.xml");
            await typeDatabase.LoadModuleDatabaseFromFile(dbPath);

            var aliasTypeName = SwiftTypeName.FromModuleQualifiedName("FamilyControls.ApplicationToken");
            Assert.True(typeDatabase.IsTypeProcessed(aliasTypeName),
                "Type alias should be recognized as processed via cross-module alias");
        }

        [Fact]
        public void CrossModuleTypeAlias_UnknownAlias_ReturnsFalse()
        {
            var typeDatabase = new TypeDatabase();
            var unknownAlias = SwiftTypeName.FromModuleQualifiedName("FamilyControls.NonExistentToken");
            Assert.False(typeDatabase.TryGetTypeRecord(unknownAlias, out _),
                "Unknown type alias should not resolve");
        }

        [Theory]
        // Two distinct classes from RealityFoundation prove the fallback is driven by
        // apple-frameworks.json's RealityFoundation→RealityKit compileImportModule entry,
        // not by an Entity-specific code path. If a third RF class is added later it
        // resolves through the same mechanism with no generator changes.
        [InlineData("Entity")]
        [InlineData("Scene")]
        public void TryGetTypeRecord_CompileImportModule_FallsBackToSourceModule(string typeName)
        {
            // Apple ships RealityFoundation under RealityKit's @_implementationOnly umbrella.
            // RealityFoundation's own ABI JSON prints canonical names like "RealityKit.Entity"
            // even though the type's declaration lives in RealityFoundation. The cross-module
            // lookup must rewrite the umbrella-qualified name back onto the source module.
            var typeDatabase = new TypeDatabase();
            var sourceModule = new ModuleTypeDatabase("RealityFoundation", "/fake/RealityFoundation.dylib");
            var declaredName = SwiftTypeName.FromModuleQualifiedName($"RealityFoundation.{typeName}");
            sourceModule.RegisterType(declaredName, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("RealityFoundation", typeName),
                SwiftTypeName = declaredName,
                MetadataAccessor = string.Empty,
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
            typeDatabase.AddModuleDatabase(sourceModule);

            // Lookup with the umbrella-qualified name (the form that appears in ABI JSON).
            var umbrellaName = SwiftTypeName.FromModuleQualifiedName($"RealityKit.{typeName}");
            Assert.True(typeDatabase.TryGetTypeRecord(umbrellaName, out var record),
                $"RealityKit.{typeName} must resolve to RealityFoundation.{typeName} via compileImportModule fallback.");
            Assert.NotNull(record);
            Assert.Equal(typeName, record!.CSharpTypeName.Name);
            Assert.Equal("RealityFoundation", record.SwiftTypeName.Module);
            Assert.Equal(TypeRecordKind.Class, record.Kind);

            Assert.True(typeDatabase.IsTypeProcessed(umbrellaName),
                $"IsTypeProcessed must mirror TryGetTypeRecord for RealityKit.{typeName}.");
        }

        [Fact]
        public void TryGetTypeRecord_CompileImportModule_UnknownSourceType_ReturnsFalse()
        {
            // Negative case: the umbrella module is registered as a compile-import target,
            // but the requested type is not declared in any source module. The fallback must
            // not invent a record — emission falls back to the existing AnyType / synthetic
            // ObjC-bridged paths (or null), preserving the prior behaviour for unrelated names.
            var typeDatabase = new TypeDatabase();
            var sourceModule = new ModuleTypeDatabase("RealityFoundation", "/fake/RealityFoundation.dylib");
            var declaredName = SwiftTypeName.FromModuleQualifiedName("RealityFoundation.Entity");
            sourceModule.RegisterType(declaredName, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("RealityFoundation", "Entity"),
                SwiftTypeName = declaredName,
                MetadataAccessor = string.Empty,
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
            typeDatabase.AddModuleDatabase(sourceModule);

            var unknownUmbrellaName = SwiftTypeName.FromModuleQualifiedName("RealityKit.NonExistentType");
            Assert.False(typeDatabase.TryGetTypeRecord(unknownUmbrellaName, out var record),
                "Unknown umbrella-qualified type names must not be invented by the compileImport fallback.");
            Assert.Null(record);
            Assert.False(typeDatabase.IsTypeProcessed(unknownUmbrellaName));
        }

        [Fact]
        public void TryGetTypeRecord_CompileImportModule_NoSourceLoaded_ReturnsFalse()
        {
            // When the source module is not present in the database at all, the umbrella
            // lookup must report missing rather than return a stale record from another path.
            // This mirrors what happens during cross-module emission when a downstream
            // consumer references a RealityKit umbrella name without RealityFoundation
            // being part of the current build's loaded modules.
            var typeDatabase = new TypeDatabase();
            var realityKitModule = new ModuleTypeDatabase("RealityKit", "/fake/RealityKit.dylib");
            typeDatabase.AddModuleDatabase(realityKitModule);

            var umbrellaName = SwiftTypeName.FromModuleQualifiedName("RealityKit.Entity");
            Assert.False(typeDatabase.TryGetTypeRecord(umbrellaName, out var record));
            Assert.Null(record);
        }

        [Fact]
        public void IsTypeProcessed_CompileImportModule_UmbrellaLoadedButTypeMissing_FallsBackToSourceModule()
        {
            // Real-world build state: BOTH the umbrella module (RealityKit) AND the source
            // module (RealityFoundation) are loaded, but the umbrella module's TypeDatabase
            // doesn't itself contain Entity (it's declared in RealityFoundation and re-exported
            // through RealityKit's @_implementationOnly umbrella). TryGetTypeRecord falls
            // through to the compileImportModule reverse map; IsTypeProcessed must agree —
            // otherwise downstream emitters that gate on "is this type processed?" treat
            // RealityKit.Entity as unknown and re-fall to the AnyType / SwiftOptional<IntPtr>
            // shape that Session 7 fixed.
            var typeDatabase = new TypeDatabase();
            var umbrellaModule = new ModuleTypeDatabase("RealityKit", "/fake/RealityKit.dylib");
            typeDatabase.AddModuleDatabase(umbrellaModule);

            var sourceModule = new ModuleTypeDatabase("RealityFoundation", "/fake/RealityFoundation.dylib");
            var declaredName = SwiftTypeName.FromModuleQualifiedName("RealityFoundation.Entity");
            sourceModule.RegisterType(declaredName, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("RealityFoundation", "Entity"),
                SwiftTypeName = declaredName,
                MetadataAccessor = string.Empty,
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
            typeDatabase.AddModuleDatabase(sourceModule);

            var umbrellaName = SwiftTypeName.FromModuleQualifiedName("RealityKit.Entity");
            // TryGetTypeRecord must resolve via compileImport reverse map.
            Assert.True(typeDatabase.TryGetTypeRecord(umbrellaName, out var record));
            Assert.NotNull(record);
            Assert.Equal("RealityFoundation", record!.SwiftTypeName.Module);
            // IsTypeProcessed must agree — that is the parity bug being asserted here.
            Assert.True(typeDatabase.IsTypeProcessed(umbrellaName),
                "IsTypeProcessed must fall through on a direct-module miss when the umbrella " +
                "module is loaded but the type lives in a compileImportModule source.");
        }

        [Fact]
        public async System.Threading.Tasks.Task LoadModuleDatabaseFromFile_DepDb_ParticipatesInUmbrellaFallback()
        {
            // Apple-framework dep threading: when GenerateAppleFrameworkTarget passes a
            // dep's emitted database (e.g. RealityFoundationDatabase.xml) as
            // --module-database to the primary RealityKit run, the file-loaded module
            // must participate in the same umbrella fallback that an in-process
            // AddModuleDatabase'd module participates in. This pins that the
            // file-load path and the in-process path reach the same lookup state —
            // breaking it would silently re-introduce the 29 cross-module CS0234s
            // Session 1 fixed even though the threading change in Build.Validation.cs
            // remained intact.
            var tempXml = Path.Combine(
                Path.GetTempPath(),
                $"RealityFoundationDatabase_{Guid.NewGuid():N}.xml");
            File.WriteAllText(tempXml, """
                <swifttypedatabase version="1.0" moduleName="RealityFoundation" modulePath="/fake/RealityFoundation.dylib">
                  <entities>
                    <entity managedTypeName="Entity" managedNameSpace="RealityFoundation">
                      <typedeclaration module="RealityFoundation" name="Entity" mangledName="" frozen="false" requiresMemoryManagement="true" kind="class" />
                    </entity>
                  </entities>
                </swifttypedatabase>
                """);
            try
            {
                var typeDatabase = new TypeDatabase();
                await typeDatabase.LoadModuleDatabaseFromFile(tempXml);

                // Lookup with the umbrella-qualified name (the form RealityKit's ABI JSON prints).
                var umbrellaName = SwiftTypeName.FromModuleQualifiedName("RealityKit.Entity");
                Assert.True(typeDatabase.TryGetTypeRecord(umbrellaName, out var record),
                    "File-loaded RealityFoundation DB must resolve umbrella-qualified RealityKit.Entity.");
                Assert.NotNull(record);
                Assert.Equal("RealityFoundation", record!.SwiftTypeName.Module);
                Assert.Equal("Entity", record.CSharpTypeName.Name);
            }
            finally
            {
                File.Delete(tempXml);
            }
        }

        [Fact]
        public void GetBuiltInDatabases_NullPlatform_IncludesAllOptionalDatabases()
        {
            var databases = BindingsGenerator.GetBuiltInDatabases(platform: null);
            Assert.Contains("HealthKitDatabase.xml", databases);
            Assert.Contains("UIKitDatabase.xml", databases);
            Assert.Contains("AppKitDatabase.xml", databases);
        }

        [Theory]
        [InlineData(ApplePlatform.iOS)]
        [InlineData(ApplePlatform.tvOS)]
        public void GetBuiltInDatabases_MobilePlatforms_IncludeHealthKitAndUIKit_ExcludeAppKit(ApplePlatform platform)
        {
            var databases = BindingsGenerator.GetBuiltInDatabases(platform);
            Assert.Contains("HealthKitDatabase.xml", databases);
            Assert.Contains("UIKitDatabase.xml", databases);
            Assert.DoesNotContain("AppKitDatabase.xml", databases);
        }

        [Fact]
        public void GetBuiltInDatabases_macOS_ExcludesHealthKitAndUIKit_IncludesAppKit()
        {
            var databases = BindingsGenerator.GetBuiltInDatabases(ApplePlatform.macOS);
            Assert.DoesNotContain("HealthKitDatabase.xml", databases);
            Assert.DoesNotContain("UIKitDatabase.xml", databases);
            Assert.Contains("AppKitDatabase.xml", databases);
        }

        [Fact]
        public void GetBuiltInDatabases_MacCatalyst_IncludesHealthKitUIKitAndAppKit()
        {
            var databases = BindingsGenerator.GetBuiltInDatabases(ApplePlatform.MacCatalyst);
            Assert.Contains("HealthKitDatabase.xml", databases);
            Assert.Contains("UIKitDatabase.xml", databases);
            Assert.Contains("AppKitDatabase.xml", databases);
        }

        [Theory]
        [InlineData(ApplePlatform.iOS)]
        [InlineData(ApplePlatform.tvOS)]
        [InlineData(ApplePlatform.macOS)]
        [InlineData(ApplePlatform.MacCatalyst)]
        public void GetBuiltInDatabases_AllPlatforms_IncludeSimdDatabase(ApplePlatform platform)
        {
            // simd types (simd_float4x4, simd_float3) are surfaced by ARKit, RoomPlan,
            // CoreMotion, SceneKit and other frameworks across every Apple platform.
            // Omitting the database causes the generator to skip transform properties
            // because simd_float4x4 has no TypeRecord at member emission time.
            var databases = BindingsGenerator.GetBuiltInDatabases(platform);
            Assert.Contains("SimdDatabase.xml", databases);
        }

        [Theory]
        [InlineData(ApplePlatform.iOS)]
        [InlineData(ApplePlatform.tvOS)]
        [InlineData(ApplePlatform.macOS)]
        [InlineData(ApplePlatform.MacCatalyst)]
        public void GetBuiltInDatabases_AllPlatforms_IncludeMatterDatabase(ApplePlatform platform)
        {
            // Matter (pure-ObjC Apple framework) is referenced by MatterSupport (Swift) on
            // every Apple platform that ships MatterSupport. Without this database the
            // generator degrades MatterSupport.MatterAddDeviceRequest.setupPayload and
            // WiFiScanResult.security/.band to AnyType.
            var databases = BindingsGenerator.GetBuiltInDatabases(platform);
            Assert.Contains("MatterDatabase.xml", databases);
        }

        /// <summary>
        /// Points to the runtime XML databases directory via relative path from the test output.
        /// </summary>
        private static string TestDbDirectory
        {
            get
            {
                // Walk up from test output to repo root, then into runtime
                var dir = AppContext.BaseDirectory;
                while (dir != null && !Directory.Exists(Path.Combine(dir, ".nuke")))
                    dir = Path.GetDirectoryName(dir);
                if (dir == null)
                    throw new InvalidOperationException(
                        $"Cannot find repo root (.nuke directory) walking up from {AppContext.BaseDirectory}");
                return Path.Combine(dir, "src", "Swift.Runtime", "src", "Swift");
            }
        }
    }
}
