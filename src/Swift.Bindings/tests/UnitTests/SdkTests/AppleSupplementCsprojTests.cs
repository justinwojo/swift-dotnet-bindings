// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// Content validation for SwiftBindings.Apple.csproj. The supplement is the only
    /// shipped package whose Runtime dependency comes through a ProjectReference, and
    /// NuGet pack does NOT honor &lt;Version&gt;/&lt;VersionOverride&gt; metadata on
    /// ProjectReference items — it calls _GetProjectVersion on the referenced project,
    /// which returns bare $(PackageVersion). Without an override target the supplement
    /// nupkg ships an unbounded min-only Runtime dep, defeating the whole purpose of
    /// SwiftRuntimePackageVersionRange. These tests pin the override structure so a
    /// future edit can't silently remove it.
    /// </summary>
    public class AppleSupplementCsprojContentTests
    {
        private static readonly string CsprojPath = Path.Combine(
            FindRepoRoot(), "src", "Swift.Bindings.Apple", "Swift.Bindings.Apple.csproj");

        private static readonly string CsprojContent = File.ReadAllText(CsprojPath);

        [Fact]
        public void Csproj_DeclaresSwiftRuntimePackageVersionRangeProperty()
        {
            // The pack pipeline passes this property in as the floor [MAJOR.MINOR.PATCH,) so the
            // supplement nupkg declares its Runtime dependency at the right floor. The in-tree
            // value is a self-conditional default (only applied when the property is unset), so a
            // passed value wins; that is what keeps a stray local `dotnet pack` coherent without
            // overriding the pipeline's floor.
            Assert.Contains("<SwiftRuntimePackageVersionRange", CsprojContent);
            Assert.Contains("</SwiftRuntimePackageVersionRange>", CsprojContent);
            Assert.Contains("Condition=\"'$(SwiftRuntimePackageVersionRange)' == ''\"", CsprojContent);
        }

        [Fact]
        public void Csproj_StampPackedRangeTargetIsPresent()
        {
            // The override target is the actual mechanism that makes the bounded range
            // reach the packed nuspec. Renaming or removing it silently regresses the
            // whole minor-floor guarantee.
            Assert.Contains("_StampSwiftRuntimePackedVersionRange", CsprojContent);
            Assert.Contains("AfterTargets=\"_GetProjectReferenceVersions\"", CsprojContent);
        }

        [Fact]
        public void Csproj_StampPackedRangeTargetMatchesSwiftRuntime()
        {
            // The metadata override must match Swift.Runtime by Filename so an
            // accidental rename of the target reference (or another future
            // ProjectReference being added) doesn't silently mismatch.
            Assert.Contains("_ProjectReferencesWithVersions", CsprojContent);
            Assert.Contains("'%(Filename)' == 'Swift.Runtime'", CsprojContent);
            Assert.Contains("<ProjectVersion>$(SwiftRuntimePackageVersionRange)</ProjectVersion>", CsprojContent);
        }

        [Fact]
        public void Csproj_DoesNotUseBrokenVersionMetadataOnProjectReference()
        {
            // <ProjectReference><Version>...</Version></ProjectReference> is read by neither
            // pack nor build — it's a no-op the previous shape relied on, leading to the
            // unbounded-min-only dep regression. Keep the ProjectReference free of Version
            // metadata so a future edit can't accidentally restore the broken pattern.
            Assert.DoesNotContain("<Version>$(SwiftRuntimePackageVersionRange)</Version>", CsprojContent);
            Assert.DoesNotContain("<VersionOverride>$(SwiftRuntimePackageVersionRange)</VersionOverride>", CsprojContent);
        }

        private static string FindRepoRoot()
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            while (dir != null)
            {
                var gitPath = Path.Combine(dir, ".git");
                if (Directory.Exists(gitPath) || File.Exists(gitPath))
                    return dir;
                dir = Path.GetDirectoryName(dir);
            }
            throw new InvalidOperationException("Cannot find repo root.");
        }
    }
}
