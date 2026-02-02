// Copyright (c) Microsoft Corporation.
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
    }
}
