// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Security;

namespace BindingsGeneration
{
    /// <summary>
    /// Writes a binding package copy's declaration, <c>buildTransitive/&lt;tfm&gt;/&lt;PackageId&gt;.props</c>.
    /// </summary>
    /// <remarks>
    /// A binding package carries one copy per toolchain, each keyed by its full pack TFM:
    /// <c>lib/&lt;tfm&gt;/</c>, <c>buildTransitive/&lt;tfm&gt;/</c> and <c>native/&lt;tfm&gt;/</c>. NuGet picks
    /// <c>lib/</c> and <c>buildTransitive/</c> by the same nearest-framework rule, so a well-formed
    /// package lands both on one copy, and that copy's consumer targets reference only its own
    /// natives. The declaration names the copy (one <c>SwiftBindingPackageCopy</c> item) so
    /// SwiftBindings.Runtime's copy check can prove, in every consumer build, that the assembly the
    /// build resolved comes from the same copy (SWIFTBIND136), announce which copy was selected and on
    /// which toolchain it was built, and report an older-copy fallback (SWIFTBIND139). The merge of
    /// per-toolchain contributions reads it too. SwiftBindings.Sdk writes the same item at pack time
    /// (it also knows the .NET SDK and Xcode the copy was built with); this writer serves the
    /// generator-emitted standalone project, which records the generator and the platform SDK.
    /// </remarks>
    public static class PackageCopyDeclarationEmitter
    {
        /// <summary>The item type the declaration adds.</summary>
        public const string ItemType = "SwiftBindingPackageCopy";

        /// <summary>The declaration's file name for <paramref name="packageId"/>.</summary>
        public static string FileName(string packageId) => $"{packageId}.props";

        /// <summary>The declaration text of the copy <paramref name="packTfm"/> of <paramref name="packageId"/>.</summary>
        public static string Compose(string packageId, string packTfm, string toolchain, string platformSdkVersion, string generator)
        {
            string X(string value) => SecurityElement.Escape(value) ?? "";
            return $"""
                <Project>
                  <!-- The copy of {X(packageId)} this buildTransitive/{X(packTfm)}/ folder belongs to. Read by
                       SwiftBindings.Runtime's copy check (SWIFTBIND136, SWIFTBIND139) and by the
                       multi-toolchain package merge. Paths resolve from this file. -->
                  <ItemGroup>
                    <{ItemType} Include="{X(packageId)}">
                      <CopyTfm>{X(packTfm)}</CopyTfm>
                      <Toolchain>{X(toolchain)}</Toolchain>
                      <PlatformSdkVersion>{X(platformSdkVersion)}</PlatformSdkVersion>
                      <Generator>{X(generator)}</Generator>
                      <LibDirectory>$(MSBuildThisFileDirectory)../../lib/{X(packTfm)}/</LibDirectory>
                      <NativeDirectory>$(MSBuildThisFileDirectory)../../native/{X(packTfm)}/</NativeDirectory>
                      <TargetsFile>$(MSBuildThisFileDirectory){X(packageId)}.targets</TargetsFile>
                    </{ItemType}>
                  </ItemGroup>
                </Project>

                """;
        }
    }
}
