// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.IO;
using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// Ranking behaviour of the probe's ref-pack version parse. The probe picks the newest installed
    /// pack with a strict "greater-than" walk, so the parsed version must order packs the way SemVer
    /// does: a stable pack outranks a prerelease of the same numeric core, and a higher numeric core
    /// outranks any lower core regardless of prerelease status. This is a probe-only heuristic (not the
    /// publication gate), but a mis-order silently binds the wrong reference assemblies and interop
    /// generator, so the ordering is pinned here.
    /// </summary>
    public class CSharpProbeReferenceSetVersionTests
    {
        // Reproduces the on-disk shape the parser climbs: {pack}/{version}/ref/{tfm}/System.Runtime.dll.
        private static Version Parse(string versionSegment)
        {
            var path = Path.Combine(
                Path.GetTempPath(), "packs", "Microsoft.NETCore.App.Ref",
                versionSegment, "ref", "net10.0", "System.Runtime.dll");
            return CSharpProbeReferenceSet.VersionFromRefPath(path);
        }

        [Fact]
        public void Stable_OutranksPrerelease_OfSameCore()
        {
            // The regression this guards: stripping the prerelease suffix collapsed both to the same
            // core, so a first-wins tie could bind the rc pack over the released one.
            Assert.True(Parse("10.0.0") > Parse("10.0.0-rc.1"));
            Assert.True(Parse("10.0.0") > Parse("10.0.0-preview.3.24"));
        }

        [Fact]
        public void HigherPatch_OutranksLowerPatch()
        {
            Assert.True(Parse("10.0.7") > Parse("10.0.0"));
            Assert.True(Parse("10.0.0") > Parse("9.0.5"));
        }

        [Fact]
        public void HigherCore_OutranksLowerCore_EvenWhenPrerelease()
        {
            // The prerelease rank lives in the revision slot, below Major/Minor/Build, so the numeric
            // core always dominates: a prerelease of a newer core still beats a stable older core.
            Assert.True(Parse("10.1.0-rc.1") > Parse("10.0.9"));
        }

        [Fact]
        public void Unparseable_SegmentSortsLowest()
        {
            Assert.True(Parse("10.0.0-rc.1") > Parse("not-a-version"));
        }
    }
}
