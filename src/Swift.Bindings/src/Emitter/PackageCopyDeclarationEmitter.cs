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

        /// <summary>
        /// <c>NativePayload</c>: the copy's natives are under <c>native/&lt;tfm&gt;/</c>, which its consumer
        /// targets reference (a Swift or mixed binding). A declaration that names no payload predates the
        /// field and means this.
        /// </summary>
        public const string NativePayloadNative = "native";

        /// <summary>
        /// <c>NativePayload</c>: the copy ships no natives at all. A pure Objective-C binding of an Apple
        /// framework binds what the OS provides.
        /// </summary>
        public const string NativePayloadNone = "none";

        /// <summary>
        /// <c>NativePayload</c>: the copy's natives travel inside its <c>lib/&lt;tfm&gt;/</c> folder, as the
        /// binding project's resource package (<c>&lt;Assembly&gt;.resources.zip</c>, or the
        /// <c>&lt;Assembly&gt;.resources/</c> directory) the consumer's build extracts. A pure Objective-C
        /// binding of a library ships them so.
        /// </summary>
        public const string NativePayloadResources = "resources";

        /// <summary>Whether <paramref name="payload"/> is a <c>NativePayload</c> this format defines.</summary>
        public static bool IsNativePayload(string payload) =>
            payload is NativePayloadNative or NativePayloadNone or NativePayloadResources;

        /// <summary>
        /// The declaration text of the copy <paramref name="packTfm"/> of <paramref name="packageId"/>, whose
        /// natives are <paramref name="nativePayload"/> and which ships consumer targets when
        /// <paramref name="hasConsumerTargets"/>. A copy with no <c>native/&lt;tfm&gt;/</c> payload declares an
        /// empty <c>NativeDirectory</c>, and one with no consumer targets an empty <c>TargetsFile</c>.
        /// </summary>
        public static string Compose(string packageId, string packTfm, string toolchain, string platformSdkVersion, string generator,
            string nativePayload = NativePayloadNative, bool hasConsumerTargets = true)
        {
            string X(string value) => SecurityElement.Escape(value) ?? "";
            var nativeDirectory = nativePayload == NativePayloadNative ? $"$(MSBuildThisFileDirectory)../../native/{X(packTfm)}/" : "";
            var targetsFile = hasConsumerTargets ? $"$(MSBuildThisFileDirectory){X(packageId)}.targets" : "";
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
                      <NativePayload>{X(nativePayload)}</NativePayload>
                      <LibDirectory>$(MSBuildThisFileDirectory)../../lib/{X(packTfm)}/</LibDirectory>
                      <NativeDirectory>{nativeDirectory}</NativeDirectory>
                      <TargetsFile>{targetsFile}</TargetsFile>
                    </{ItemType}>
                  </ItemGroup>
                </Project>

                """;
        }
    }
}
