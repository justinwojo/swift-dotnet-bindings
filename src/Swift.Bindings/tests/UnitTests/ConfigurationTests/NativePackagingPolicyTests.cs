// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// Tests for <see cref="NativePackagingPolicy"/>, the single source of truth for the
    /// "reference the source xcframework's native?" decision (Gap 2). This formula was
    /// previously inlined at every emitter/pack site, which is how the carrier term drifted
    /// (one site requiring a wrapper, others not). The contract: drop the source ONLY for a
    /// static archive that has a wrapper carrier; keep it for everything else — including a
    /// static source with no wrapper (the source is then the sole carrier and dropping it
    /// would leave the binding with no native at all).
    /// </summary>
    public class NativePackagingPolicyTests
    {
        [Theory]
        // Static + a wrapper carrier present => force-loaded into the wrapper, drop the source.
        [InlineData(NativeLinkage.Static, true, false)]
        // Static + NO wrapper carrier => the static source is the sole carrier, keep it.
        [InlineData(NativeLinkage.Static, false, true)]
        // Dynamic always keeps the source, regardless of wrapper presence.
        [InlineData(NativeLinkage.Dynamic, true, true)]
        [InlineData(NativeLinkage.Dynamic, false, true)]
        public void ShouldIncludeSourceXcframework_MatchesTruthTable(
            NativeLinkage linkage, bool wrapperCarrierPresent, bool expectedInclude)
        {
            Assert.Equal(
                expectedInclude,
                NativePackagingPolicy.ShouldIncludeSourceXcframework(linkage, wrapperCarrierPresent));
        }

        [Fact]
        public void ShouldIncludeSourceXcframework_DropsSource_OnlyWhenStaticAndCarrierPresent()
        {
            // The only false case is the static-with-carrier corner; assert it is the sole drop.
            Assert.False(NativePackagingPolicy.ShouldIncludeSourceXcframework(NativeLinkage.Static, true));
            Assert.True(NativePackagingPolicy.ShouldIncludeSourceXcframework(NativeLinkage.Static, false));
            Assert.True(NativePackagingPolicy.ShouldIncludeSourceXcframework(NativeLinkage.Dynamic, true));
        }

        [Theory]
        // Static + a wrapper carrier => the source is a fallback gated on the wrapper's absence,
        // so a soft-failed/skipped wrapper compile still leaves the source as the sole carrier.
        [InlineData(NativeLinkage.Static, true, SourceReferenceMode.WrapperAbsentFallback)]
        // Static + NO wrapper => the source is the sole carrier, referenced unconditionally.
        [InlineData(NativeLinkage.Static, false, SourceReferenceMode.Always)]
        // Dynamic always references the source unconditionally (wrapper and source coexist).
        [InlineData(NativeLinkage.Dynamic, true, SourceReferenceMode.Always)]
        [InlineData(NativeLinkage.Dynamic, false, SourceReferenceMode.Always)]
        public void ResolveConsumerSourceReferenceMode_MatchesTruthTable(
            NativeLinkage linkage, bool wrapperCarrierPresent, SourceReferenceMode expectedMode)
        {
            Assert.Equal(
                expectedMode,
                NativePackagingPolicy.ResolveConsumerSourceReferenceMode(linkage, wrapperCarrierPresent));
        }

        [Fact]
        public void ResolveConsumerSourceReferenceMode_NeverDropsSource()
        {
            // The consumer-targets decision never omits the source — that would gamble on a wrapper
            // the deferred compile may never produce. The static-with-wrapper corner is the only
            // one that downgrades to a wrapper-absent fallback; everything else is unconditional.
            Assert.Equal(SourceReferenceMode.WrapperAbsentFallback,
                NativePackagingPolicy.ResolveConsumerSourceReferenceMode(NativeLinkage.Static, true));
            Assert.Equal(SourceReferenceMode.Always,
                NativePackagingPolicy.ResolveConsumerSourceReferenceMode(NativeLinkage.Static, false));
            Assert.Equal(SourceReferenceMode.Always,
                NativePackagingPolicy.ResolveConsumerSourceReferenceMode(NativeLinkage.Dynamic, true));
        }
    }
}
