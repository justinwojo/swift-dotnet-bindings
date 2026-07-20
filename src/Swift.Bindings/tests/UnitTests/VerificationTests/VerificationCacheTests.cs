// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// The local, content-addressed cache of C# verification verdicts. Two properties matter: a hit
    /// reproduces a miss <em>exactly</em> (so the loop's decisions, the published artifacts, and the
    /// report are byte-identical whether reused or recomputed), and the store persists across cache
    /// instances (modelling a second generator run over the same local cache directory). Reads and
    /// writes are best-effort — a corrupt or unknown entry is a miss, never a throw.
    /// </summary>
    public sealed class VerificationCacheTests : IDisposable
    {
        private readonly string _dir;

        public VerificationCacheTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "vcache-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
        }

        private static CSharpVerificationResult Clean() =>
            new(CSharpVerificationOutcome.Clean, Array.Empty<CSharpCompileDiagnostic>());

        private static CSharpVerificationResult CompileErrors() => new(
            CSharpVerificationOutcome.CompileErrors,
            new[]
            {
                new CSharpCompileDiagnostic(
                    "CS0246", CSharpDiagnosticSeverity.Error, "/out/Foo.cs", 12, 5, 12, 20, "type or namespace not found"),
                // A project-level diagnostic with a null file path and zero span — the round-trip must
                // preserve the null, not coerce it to "".
                new CSharpCompileDiagnostic(
                    "NU1101", CSharpDiagnosticSeverity.Error, null, 0, 0, 0, 0, "unable to resolve package"),
            });

        private static void AssertIdentical(CSharpVerificationResult expected, CSharpVerificationResult actual)
        {
            Assert.Equal(expected.Outcome, actual.Outcome);
            Assert.Equal(expected.InconclusiveReason, actual.InconclusiveReason);
            // CSharpCompileDiagnostic is a record → value equality; xUnit compares the lists element-wise.
            Assert.Equal(expected.Diagnostics, actual.Diagnostics);
        }

        [Fact]
        public void Store_ThenTryGet_RoundTripsCleanVerdictExactly()
        {
            var cache = new VerificationCache(_dir);
            cache.Store("fp-clean", Clean());
            Assert.True(cache.TryGet("fp-clean", out var got));
            AssertIdentical(Clean(), got);
        }

        [Fact]
        public void Store_ThenTryGet_RoundTripsCompileErrorsExactly()
        {
            var cache = new VerificationCache(_dir);
            var original = CompileErrors();
            cache.Store("fp-errs", original);
            Assert.True(cache.TryGet("fp-errs", out var got));
            AssertIdentical(original, got);

            // Field-level spot check on the null-path diagnostic, since that is the easiest thing for a
            // serializer to get wrong.
            var nu = got.Diagnostics.Single(d => d.Id == "NU1101");
            Assert.Null(nu.FilePath);
            Assert.Equal(0, nu.Line);
        }

        [Fact]
        public void Store_InconclusiveVerdict_IsNotPersisted_SoItReRunsRatherThanStickingClosed()
        {
            var cache = new VerificationCache(_dir);
            var inconclusive = new CSharpVerificationResult(
                CSharpVerificationOutcome.Inconclusive, Array.Empty<CSharpCompileDiagnostic>(),
                "verifier timed out");
            cache.Store("fp-inc", inconclusive);

            // An inconclusive verdict is a transient infrastructure fault (a restore failure, a timeout, an
            // IO error), not a property of the fingerprinted inputs. Persisting it would make a one-off blip
            // sticky: a later run whose denylist is already non-empty would hit the cached Inconclusive and
            // fail the module closed deterministically. Storing it must be a no-op — a miss recomputes.
            Assert.False(cache.TryGet("fp-inc", out _));
            Assert.Empty(Directory.EnumerateFiles(_dir, "*.json"));
        }

        [Fact]
        public void CreateIfEnabled_ReturnsNullUntilAnExplicitRootIsSet_ThenHonorsTheDisableSwitch()
        {
            // The opt-in gate: the cache is constructed ONLY when the operator points
            // SWIFTBINDINGS_VERIFY_CACHE at a root they control — it is not default-on, because the
            // fingerprint does not provably cover every inherited MSBuild input. No explicit root ⇒ no
            // cache; an explicit root ⇒ a cache, unless the disable switch is also set, which must still win.
            var savedRoot = Environment.GetEnvironmentVariable("SWIFTBINDINGS_VERIFY_CACHE");
            var savedDisable = Environment.GetEnvironmentVariable("SWIFTBINDINGS_NO_VERIFY_CACHE");
            try
            {
                Environment.SetEnvironmentVariable("SWIFTBINDINGS_NO_VERIFY_CACHE", null);

                Environment.SetEnvironmentVariable("SWIFTBINDINGS_VERIFY_CACHE", null);
                Assert.Null(VerificationCache.CreateIfEnabled());

                Environment.SetEnvironmentVariable("SWIFTBINDINGS_VERIFY_CACHE", _dir);
                Assert.NotNull(VerificationCache.CreateIfEnabled());

                // The opt-out switch still wins over an explicit root.
                Environment.SetEnvironmentVariable("SWIFTBINDINGS_NO_VERIFY_CACHE", "1");
                Assert.Null(VerificationCache.CreateIfEnabled());
            }
            finally
            {
                Environment.SetEnvironmentVariable("SWIFTBINDINGS_VERIFY_CACHE", savedRoot);
                Environment.SetEnvironmentVariable("SWIFTBINDINGS_NO_VERIFY_CACHE", savedDisable);
            }
        }

        [Fact]
        public void TryGet_UnknownFingerprint_IsMiss()
        {
            var cache = new VerificationCache(_dir);
            Assert.False(cache.TryGet("never-stored", out _));
        }

        [Fact]
        public void TryGet_CorruptEntry_IsTreatedAsMiss_NotThrown()
        {
            var cache = new VerificationCache(_dir);
            File.WriteAllText(Path.Combine(_dir, "torn.json"), "{ this is not valid json ]");
            Assert.False(cache.TryGet("torn", out _));
        }

        [Fact]
        public void Store_OverExistingFingerprint_ReplacesVerdict()
        {
            var cache = new VerificationCache(_dir);
            cache.Store("fp", CompileErrors());
            cache.Store("fp", Clean());
            Assert.True(cache.TryGet("fp", out var got));
            AssertIdentical(Clean(), got);
        }

        /// <summary>
        /// The mandated economics property, hermetically: run a verification twice over the same local
        /// cache directory (second time via a fresh cache instance, i.e. a second generator run). The
        /// first run misses and computes; the second hits and does NOT recompute; and the two verdicts
        /// are byte-identical — so a hit produces an identical report to a miss.
        /// </summary>
        [Fact]
        public void RunTwice_SecondRunIsCacheHot_ReusesIdenticalVerdictWithoutRecomputing()
        {
            const string fp = "fp-run-twice";
            var computeCalls = 0;
            CSharpVerificationResult Verify()
            {
                computeCalls++;
                return CompileErrors();
            }

            CSharpVerificationResult GetOrVerify(VerificationCache cache)
            {
                if (cache.TryGet(fp, out var hit))
                    return hit;
                var fresh = Verify();
                cache.Store(fp, fresh);
                return fresh;
            }

            // Run 1: cold cache instance → miss → compute + store.
            var run1 = GetOrVerify(new VerificationCache(_dir));
            Assert.Equal(1, computeCalls);

            // Run 2: a fresh instance over the SAME local cache dir (a second process) → hit, no recompute.
            var run2 = GetOrVerify(new VerificationCache(_dir));
            Assert.Equal(1, computeCalls);

            AssertIdentical(run1, run2);
        }

        [Fact]
        public void Store_LeavesNoTempFileBehind()
        {
            var cache = new VerificationCache(_dir);
            cache.Store("fp", Clean());
            Assert.Empty(Directory.EnumerateFiles(_dir, "*.tmp"));
            Assert.Single(Directory.EnumerateFiles(_dir, "*.json"));
        }

        [Fact]
        public void Constructor_NullRoot_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new VerificationCache(null!));
        }
    }
}
