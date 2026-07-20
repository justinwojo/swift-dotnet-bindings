// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// The content-addressed key for a cached verification verdict. The load-bearing property is
    /// completeness: the verdict is a pure function of (input ABI facts, toolchain versions, generator
    /// version, settled plan, denylist), so flipping <em>any</em> one of those five must change the key
    /// (force a cache miss), and holding all five fixed must reproduce the key exactly (a hit). These
    /// tests pin both directions plus canonicalization and the file-hash helper.
    /// </summary>
    public class VerificationFingerprintTests
    {
        // A fixed baseline set of the five components; each test flips exactly one.
        private static readonly byte[] Abi = Encoding.UTF8.GetBytes("{\"abi\":\"facts\"}");
        private const string Toolchain = "10.0.100|.NET 10.0.0";
        private const string Generator = "11112222-3333-4444-5555-666677778888";
        private static readonly byte[] Plan = Encoding.UTF8.GetBytes("public static class Foo {}");
        private static readonly string[] Denylist = { "unit.a (leaf)", "unit.b (accessor-group)" };

        private static string Baseline() =>
            VerificationFingerprint.Compute(Abi, Toolchain, Generator, Plan, Denylist);

        [Fact]
        public void Compute_IsDeterministic_ForIdenticalComponents()
        {
            Assert.Equal(Baseline(), Baseline());
        }

        [Fact]
        public void Compute_ReturnsLowercaseHex_Sha256Width()
        {
            var fp = Baseline();
            Assert.Equal(64, fp.Length); // SHA-256 → 32 bytes → 64 hex chars
            Assert.All(fp, c => Assert.True(Uri.IsHexDigit(c) && !char.IsUpper(c)));
        }

        [Fact]
        public void Compute_FlippingAbiFacts_ForcesMiss()
        {
            var flipped = VerificationFingerprint.Compute(
                Encoding.UTF8.GetBytes("{\"abi\":\"DIFFERENT\"}"), Toolchain, Generator, Plan, Denylist);
            Assert.NotEqual(Baseline(), flipped);
        }

        [Fact]
        public void Compute_FlippingToolchain_ForcesMiss()
        {
            var flipped = VerificationFingerprint.Compute(Abi, "10.0.200|.NET 10.0.1", Generator, Plan, Denylist);
            Assert.NotEqual(Baseline(), flipped);
        }

        [Fact]
        public void Compute_FlippingGeneratorVersion_ForcesMiss()
        {
            var flipped = VerificationFingerprint.Compute(
                Abi, Toolchain, "99998888-7777-6666-5555-444433332222", Plan, Denylist);
            Assert.NotEqual(Baseline(), flipped);
        }

        [Fact]
        public void Compute_FlippingSettledPlan_ForcesMiss()
        {
            var flipped = VerificationFingerprint.Compute(
                Abi, Toolchain, Generator, Encoding.UTF8.GetBytes("public static class Bar {}"), Denylist);
            Assert.NotEqual(Baseline(), flipped);
        }

        [Fact]
        public void Compute_FlippingDenylist_ForcesMiss()
        {
            var flipped = VerificationFingerprint.Compute(
                Abi, Toolchain, Generator, Plan, new[] { "unit.a (leaf)" });
            Assert.NotEqual(Baseline(), flipped);
        }

        [Fact]
        public void Compute_DenylistOrder_DoesNotAffectKey()
        {
            // The denylist is a set; a reordering is the same set and must yield the same key.
            var reordered = VerificationFingerprint.Compute(
                Abi, Toolchain, Generator, Plan, new[] { "unit.b (accessor-group)", "unit.a (leaf)" });
            Assert.Equal(Baseline(), reordered);
        }

        [Fact]
        public void Compute_IsDomainSeparated_SoComponentBytesCannotBleed()
        {
            // Moving a byte-boundary between two adjacent components must change the key: "ab"+"c" and
            // "a"+"bc" would collide under naive concatenation but not under length-prefixed feeds.
            var a = VerificationFingerprint.Compute(
                Encoding.UTF8.GetBytes("ab"), "c", Generator, Plan, Denylist);
            var b = VerificationFingerprint.Compute(
                Encoding.UTF8.GetBytes("a"), "bc", Generator, Plan, Denylist);
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void Compute_NullArguments_Throw()
        {
            Assert.Throws<ArgumentNullException>(() =>
                VerificationFingerprint.Compute(Abi, null!, Generator, Plan, Denylist));
            Assert.Throws<ArgumentNullException>(() =>
                VerificationFingerprint.Compute(Abi, Toolchain, null!, Plan, Denylist));
            Assert.Throws<ArgumentNullException>(() =>
                VerificationFingerprint.Compute(Abi, Toolchain, Generator, Plan, null!));
        }

        [Fact]
        public void HashFiles_IsOrderIndependent_ButContentSensitive()
        {
            var dir = NewTempDir();
            try
            {
                var f1 = Path.Combine(dir, "a.cs");
                var f2 = Path.Combine(dir, "b.cs");
                File.WriteAllText(f1, "class A {}");
                File.WriteAllText(f2, "class B {}");

                var forward = VerificationFingerprint.HashFiles(new[] { f1, f2 });
                var reverse = VerificationFingerprint.HashFiles(new[] { f2, f1 });
                Assert.Equal(forward, reverse); // ordinal-sorted internally

                File.WriteAllText(f2, "class B { int x; }");
                var afterEdit = VerificationFingerprint.HashFiles(new[] { f1, f2 });
                Assert.NotEqual(forward, afterEdit); // content change → different digest
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void HashFiles_MissingFile_IsSkipped_NotThrown()
        {
            var dir = NewTempDir();
            try
            {
                var present = Path.Combine(dir, "present.cs");
                File.WriteAllText(present, "class P {}");
                var missing = Path.Combine(dir, "gone.cs");

                var withMissing = VerificationFingerprint.HashFiles(new[] { present, missing });
                var withoutMissing = VerificationFingerprint.HashFiles(new[] { present });
                Assert.Equal(withMissing, withoutMissing);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        private static string NewTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "vfp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
