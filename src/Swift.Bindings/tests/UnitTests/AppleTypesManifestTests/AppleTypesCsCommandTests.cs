// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using BindingsGeneration.AppleTypesManifest;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

// Command-level coverage for --emit-apple-types-cs. Unit tests on AppleTypesCsEmitter
// verify per-entry emission; these tests lock down the exit-code contract the build
// relies on (structural failures must fail the command, not silently drop types).
public class AppleTypesCsCommandTests
{
    [Fact]
    public void Run_BlankMetadataAccessorSymbol_FailsWithNonZeroExit()
    {
        // The JSON schema rejects `minLength: 1` on metadata_accessor.symbol at the
        // generator, but a stale or hand-edited manifest can still reach the emitter.
        // The emitter already skips the type (defense in depth) — this test locks in
        // the CLI behavior: the run must fail, not print a skip and exit 0.
        var manifestJson = """
            {
              "schema_version": "1",
              "sdk_train": { "major": 26 },
              "modules": {
                "Foundation": {
                  "types": [
                    {
                      "swift_identity": "Foundation.Locale.Language",
                      "kind": "struct",
                      "frozen": false,
                      "storage_strategy": "vwt_opaque",
                      "managed_projection": {
                        "namespace": "Swift.Foundation",
                        "declaration_path": ["Locale", "Language"]
                      },
                      "abi_carrier": {
                        "namespace": "Swift.Foundation",
                        "declaration_path": ["Locale", "Language"]
                      },
                      "metadata_accessor": {
                        "symbol": "",
                        "library": "Foundation",
                        "availability": {}
                      }
                    }
                  ]
                }
              }
            }
            """;

        var exitCode = RunWithManifest(manifestJson);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void Run_BlankMetadataAccessorLibrary_FailsWithNonZeroExit()
    {
        var manifestJson = """
            {
              "schema_version": "1",
              "sdk_train": { "major": 26 },
              "modules": {
                "Foundation": {
                  "types": [
                    {
                      "swift_identity": "Foundation.Locale.Language",
                      "kind": "struct",
                      "frozen": false,
                      "storage_strategy": "vwt_opaque",
                      "managed_projection": {
                        "namespace": "Swift.Foundation",
                        "declaration_path": ["Locale", "Language"]
                      },
                      "abi_carrier": {
                        "namespace": "Swift.Foundation",
                        "declaration_path": ["Locale", "Language"]
                      },
                      "metadata_accessor": {
                        "symbol": "$s10Foundation6LocaleV8LanguageVMa",
                        "library": "",
                        "availability": {}
                      }
                    }
                  ]
                }
              }
            }
            """;

        var exitCode = RunWithManifest(manifestJson);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void Run_WellFormedManifest_ExitsZero()
    {
        // Baseline: a single valid entry emits successfully. Guards against regressions
        // in the new structural-skip gate that would fail runs on benign input.
        var manifestJson = """
            {
              "schema_version": "1",
              "sdk_train": { "major": 26 },
              "modules": {
                "Foundation": {
                  "types": [
                    {
                      "swift_identity": "Foundation.Locale.Language",
                      "kind": "struct",
                      "frozen": false,
                      "storage_strategy": "vwt_opaque",
                      "managed_projection": {
                        "namespace": "Swift.Foundation",
                        "declaration_path": ["Locale", "Language"]
                      },
                      "abi_carrier": {
                        "namespace": "Swift.Foundation",
                        "declaration_path": ["Locale", "Language"]
                      },
                      "metadata_accessor": {
                        "symbol": "$s10Foundation6LocaleV8LanguageVMa",
                        "library": "Foundation",
                        "availability": {}
                      }
                    }
                  ]
                }
              }
            }
            """;

        var exitCode = RunWithManifest(manifestJson);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Run_RuntimeOwnedTypeSkipped_DoesNotFail()
    {
        // Foundation.Date is owned by Swift.Runtime per TypeOwnerRegistry. The emitter
        // records it in the benign _skipped list. That MUST NOT fail the command — the
        // structural-skip gate only covers malformed entries.
        var manifestJson = """
            {
              "schema_version": "1",
              "sdk_train": { "major": 26 },
              "modules": {
                "Foundation": {
                  "types": [
                    {
                      "swift_identity": "Foundation.Date",
                      "kind": "struct",
                      "frozen": false,
                      "storage_strategy": "vwt_opaque",
                      "managed_projection": {
                        "namespace": "Swift.Foundation",
                        "declaration_path": ["Date"]
                      },
                      "abi_carrier": {
                        "namespace": "Swift.Foundation",
                        "declaration_path": ["Date"]
                      },
                      "metadata_accessor": {
                        "symbol": "$s10Foundation4DateVMa",
                        "library": "Foundation",
                        "availability": {}
                      }
                    }
                  ]
                }
              }
            }
            """;

        var exitCode = RunWithManifest(manifestJson);

        Assert.Equal(0, exitCode);
    }

    private static int RunWithManifest(string manifestJson)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "apple-cs-cmd-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var manifestPath = Path.Combine(tempDir, "manifest.json");
            var outputDir = Path.Combine(tempDir, "out");
            Directory.CreateDirectory(outputDir);
            File.WriteAllText(manifestPath, manifestJson);

            return AppleTypesCsCommand.Run(
                manifestPath,
                whitelistPath: null,
                outputDir,
                NullLogger.Instance);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
