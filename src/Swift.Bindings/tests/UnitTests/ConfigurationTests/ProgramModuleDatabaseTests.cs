// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// Tests for --module-database CLI option and PeekModuleNameFromXml helper.
    /// </summary>
    [Collection("ConsoleCapture")]
    public class ProgramModuleDatabaseTests
    {
        [Fact]
        public void Help_IncludesModuleDatabaseOption()
        {
            var output = CaptureHelp();
            Assert.Contains("--module-database", output);
        }

        [Fact]
        public void Help_DescribesCrossModuleResolution()
        {
            var output = CaptureHelp();
            Assert.Contains("--module-database", output);
            Assert.Contains("cross-module", output);
        }

        [Fact]
        public void MissingModuleDatabase_ReturnsExitCode1()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"mdb_missing_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var writer = new StringWriter();
                Console.SetOut(writer);
                try
                {
                    var exitCode = BindingsGenerator.Main(new[]
                    {
                        "-a", "/nonexistent/abi.json",
                        "-d", "/nonexistent/dylib",
                        "-t", "/nonexistent/tbd",
                        "-o", dir,
                        "--module-database", "/nonexistent/SomeModule.xml"
                    });
                    Assert.NotEqual(0, exitCode);
                }
                finally
                {
                    Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                }
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void PeekModuleNameFromXml_ValidXml_ReturnsModuleName()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                File.WriteAllText(filePath, """
                    <swifttypedatabase version="1.0" moduleName="StripeCore" modulePath="/fake/StripeCore.dylib">
                      <entities>
                        <entity managedTypeName="Widget" managedNameSpace="Swift.StripeCore">
                          <typedeclaration module="StripeCore" name="Widget" mangledName="" frozen="true" requiresMemoryManagement="false" />
                        </entity>
                      </entities>
                    </swifttypedatabase>
                    """);

                var moduleName = BindingsGenerator.PeekModuleNameFromXml(filePath);
                Assert.Equal("StripeCore", moduleName);
            }
            finally { File.Delete(filePath); }
        }

        [Fact]
        public void PeekModuleNameFromXml_InvalidXml_ReturnsNull()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                File.WriteAllText(filePath, "this is not xml at all");

                var moduleName = BindingsGenerator.PeekModuleNameFromXml(filePath);
                Assert.Null(moduleName);
            }
            finally { File.Delete(filePath); }
        }

        [Fact]
        public void PeekModuleNameFromXml_WrongRootElement_ReturnsNull()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                File.WriteAllText(filePath, "<notadatabase><child/></notadatabase>");

                var moduleName = BindingsGenerator.PeekModuleNameFromXml(filePath);
                Assert.Null(moduleName);
            }
            finally { File.Delete(filePath); }
        }

        [Fact]
        public void PeekModuleNameFromXml_MissingModuleName_ReturnsNull()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                File.WriteAllText(filePath, """
                    <swifttypedatabase version="1.0" modulePath="/fake/path">
                      <entities/>
                    </swifttypedatabase>
                    """);

                var moduleName = BindingsGenerator.PeekModuleNameFromXml(filePath);
                Assert.Null(moduleName);
            }
            finally { File.Delete(filePath); }
        }

        [Fact]
        public void PeekModuleNameFromXml_NonexistentFile_ReturnsNull()
        {
            var moduleName = BindingsGenerator.PeekModuleNameFromXml("/nonexistent/path.xml");
            Assert.Null(moduleName);
        }

        [Fact]
        public void PeekModuleNameFromXml_EmptyModuleName_ReturnsNull()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                File.WriteAllText(filePath, """
                    <swifttypedatabase version="1.0" moduleName="" modulePath="/fake/path">
                      <entities/>
                    </swifttypedatabase>
                    """);

                var moduleName = BindingsGenerator.PeekModuleNameFromXml(filePath);
                Assert.Null(moduleName);
            }
            finally { File.Delete(filePath); }
        }

        [Fact]
        public void PeekModuleNameFromXml_WhitespaceModuleName_ReturnsNull()
        {
            var filePath = Path.GetTempFileName();
            try
            {
                File.WriteAllText(filePath, """
                    <swifttypedatabase version="1.0" moduleName="   " modulePath="/fake/path">
                      <entities/>
                    </swifttypedatabase>
                    """);

                var moduleName = BindingsGenerator.PeekModuleNameFromXml(filePath);
                Assert.Null(moduleName);
            }
            finally { File.Delete(filePath); }
        }

        [Fact]
        public void InvalidSchemaDatabase_ReturnsNonZeroExitCode()
        {
            // File exists but PeekModuleNameFromXml returns null → SWIFTBIND072
            using var fixture = new ModuleDatabaseCLIFixture("TestModule");
            var invalidDbPath = Path.Combine(fixture.Dir, "invalid.xml");
            File.WriteAllText(invalidDbPath, "<notadatabase><child/></notadatabase>");

            var writer = new StringWriter();
            Console.SetOut(writer);
            try
            {
                var exitCode = BindingsGenerator.Main(new[]
                {
                    "-a", fixture.AbiJsonPath,
                    "-d", fixture.DylibPath,
                    "-t", fixture.TbdPath,
                    "-o", fixture.Dir,
                    "--module-database", invalidDbPath
                });
                Assert.NotEqual(0, exitCode);
            }
            finally
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            }
        }

        [Fact]
        public void CurrentModuleDatabase_ReturnsNonZeroExitCode()
        {
            // Module database targets the same module as the ABI JSON → SWIFTBIND071
            using var fixture = new ModuleDatabaseCLIFixture("TestModule");
            var selfDbPath = Path.Combine(fixture.Dir, "TestModuleDatabase.xml");
            File.WriteAllText(selfDbPath, """
                <swifttypedatabase version="1.0" moduleName="TestModule" modulePath="/fake/TestModule.dylib">
                  <entities>
                    <entity managedTypeName="Widget" managedNameSpace="Swift.TestModule">
                      <typedeclaration module="TestModule" name="Widget" mangledName="" frozen="true" requiresMemoryManagement="false" />
                    </entity>
                  </entities>
                </swifttypedatabase>
                """);

            var writer = new StringWriter();
            Console.SetOut(writer);
            try
            {
                var exitCode = BindingsGenerator.Main(new[]
                {
                    "-a", fixture.AbiJsonPath,
                    "-d", fixture.DylibPath,
                    "-t", fixture.TbdPath,
                    "-o", fixture.Dir,
                    "--module-database", selfDbPath
                });
                Assert.NotEqual(0, exitCode);
            }
            finally
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            }
        }

        /// <summary>
        /// Creates minimal fixture files (ABI JSON, dylib, TBD) needed to pass CLI
        /// validation and reach the GenerateBindings module database loading code.
        /// </summary>
        private sealed class ModuleDatabaseCLIFixture : IDisposable
        {
            public string Dir { get; }
            public string AbiJsonPath { get; }
            public string DylibPath { get; }
            public string TbdPath { get; }

            public ModuleDatabaseCLIFixture(string moduleName)
            {
                Dir = Path.Combine(Path.GetTempPath(), $"mdb_cli_{Guid.NewGuid():N}");
                Directory.CreateDirectory(Dir);

                AbiJsonPath = Path.Combine(Dir, "test.abi.json");
                DylibPath = Path.Combine(Dir, "test.dylib");
                TbdPath = Path.Combine(Dir, "test.tbd");

                // Minimal ABI JSON with module name — enough for PeekModuleNameFromAbiJson
                File.WriteAllText(AbiJsonPath, $$"""
                    {
                      "ABIRoot": {
                        "kind": "Root",
                        "name": "{{moduleName}}",
                        "printedName": "{{moduleName}}",
                        "children": [
                          {
                            "kind": "TypeDecl",
                            "declKind": "Import",
                            "name": "{{moduleName}}",
                            "printedName": "{{moduleName}}",
                            "moduleName": "{{moduleName}}",
                            "children": []
                          }
                        ]
                      }
                    }
                    """);

                // Dummy dylib and TBD (must exist on disk)
                File.WriteAllBytes(DylibPath, new byte[] { 0 });
                File.WriteAllText(TbdPath, "--- !tapi-tbd\n");
            }

            public void Dispose()
            {
                try { Directory.Delete(Dir, true); } catch { }
            }
        }

        private static string CaptureHelp()
        {
            var writer = new StringWriter();
            Console.SetOut(writer);
            try
            {
                BindingsGenerator.Main(new[] { "-h" });
                return writer.ToString();
            }
            finally
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            }
        }
    }
}
