// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Security.Cryptography;
using BindingsGeneration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

public class WrapperFailureEvidenceTests
{
    [Fact]
    public void Capture_PreservesFirstSourceAndCompilerIdentityAcrossRecovery()
    {
        var root = Path.Combine(Path.GetTempPath(), "wrapper-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "input.swift");
            File.WriteAllText(source, "first failing wrapper");
            var evidence = Path.Combine(root, "evidence");
            WrapperFailureEvidence.Capture(evidence, "Module", "arm64-apple-ios15.0-simulator",
                "swiftc -target arm64-apple-ios15.0-simulator", "first diagnostic", new[] { source }, NullLogger.Instance);
            File.WriteAllText(source, "recovered wrapper");
            WrapperFailureEvidence.Capture(evidence, "Module", "arm64-apple-ios15.0-simulator",
                "later command", "later diagnostic", new[] { source }, NullLogger.Instance);
            var receipt = Path.Combine(evidence, "Module-arm64-apple-ios15.0-simulator");
            var copy = Path.Combine(receipt, "0-input.swift");
            Assert.Equal("first failing wrapper", File.ReadAllText(copy));
            Assert.Equal("first diagnostic", File.ReadAllText(Path.Combine(receipt, "stderr.txt")));
            Assert.Equal("arm64-apple-ios15.0-simulator", File.ReadAllText(Path.Combine(receipt, "target.txt")));
            Assert.Equal("xcrun swiftc -target arm64-apple-ios15.0-simulator", File.ReadAllText(Path.Combine(receipt, "command.txt")));
            Assert.Contains(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(copy))), File.ReadAllText(Path.Combine(receipt, "source-sha256.txt")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Capture_DisabledOrUnreadableInputDoesNotChangeCompilerOutcome()
    {
        WrapperFailureEvidence.Capture(null, "Module", "target", "args", "error", new[] { "/missing.swift" }, NullLogger.Instance);
        var root = Path.Combine(Path.GetTempPath(), "wrapper-evidence-" + Guid.NewGuid().ToString("N"));
        try
        {
            WrapperFailureEvidence.Capture(root, "Module", "target", "args", "error", new[] { "/missing.swift" }, NullLogger.Instance);
            Assert.False(Directory.Exists(Path.Combine(root, "Module-target")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
