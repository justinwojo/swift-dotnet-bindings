// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    /// <summary>An Apple OS that an ABI descriptor's <c>intro_*</c> keys can describe.</summary>
    public enum AbiApplePlatform
    {
        iOS,
        macOS,
        tvOS,
        watchOS,
        visionOS,
        macCatalyst,
    }

    /// <summary>
    /// Reads the introduced versions an ABI descriptor node states, with Swift 6.4's umbrella
    /// <c>anyAppleOS</c> domain resolved the way the Swift 6.4 type checker resolves it:
    /// <list type="bullet">
    /// <item>A record naming a platform replaces the umbrella for that platform, whether its
    /// version is earlier or later.</item>
    /// <item>Mac Catalyst and visionOS follow a named iOS record. tvOS and watchOS do not.</item>
    /// <item>Application-extension domains replace nothing. The descriptor has no key for them,
    /// so they never reach this reader.</item>
    /// </list>
    /// A platform record with no introduced version (deprecated or unavailable only) leaves no
    /// <c>intro_*</c> key, so the umbrella floors that platform. That is stricter than Swift,
    /// never looser.
    /// </summary>
    internal static class AbiIntroducedAvailability
    {
        /// <summary>The introduced version of the record that names <paramref name="platform"/>.</summary>
        public static string? Named(Node node, AbiApplePlatform platform) => NonEmpty(platform switch
        {
            AbiApplePlatform.iOS => node.intro_iOS,
            AbiApplePlatform.macOS => node.intro_Macosx,
            AbiApplePlatform.tvOS => node.intro_tvOS,
            AbiApplePlatform.watchOS => node.intro_watchOS,
            AbiApplePlatform.visionOS => node.intro_visionOS,
            AbiApplePlatform.macCatalyst => node.intro_macCatalyst,
            _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, null),
        });

        /// <summary>
        /// The version the <c>anyAppleOS</c> umbrella supplies for <paramref name="platform"/>, or
        /// null when the node has no umbrella record or a named record replaces it.
        /// </summary>
        public static string? FromAnyAppleOS(Node node, AbiApplePlatform platform)
        {
            if (Named(node, platform) is not null)
                return null;
            if (FollowsIOS(platform) && Named(node, AbiApplePlatform.iOS) is not null)
                return null;
            return NonEmpty(node.intro_anyAppleOS);
        }

        /// <summary>
        /// The node's effective introduced version on <paramref name="platform"/>: the record
        /// naming it, else the named iOS record for a platform that follows iOS, else the
        /// umbrella. Null when the node states no floor for the platform.
        /// </summary>
        public static string? Resolve(Node node, AbiApplePlatform platform)
            => Named(node, platform)
               ?? (FollowsIOS(platform) ? Named(node, AbiApplePlatform.iOS) : null)
               ?? NonEmpty(node.intro_anyAppleOS);

        /// <summary>
        /// The platform a swift-api-digester dump describes, read from its <c>-target</c> triple
        /// (<c>arm64-apple-ios27.0-simulator</c>, <c>arm64-apple-ios27.0-macabi</c>,
        /// <c>arm64-apple-macos27.0</c>, ...). Null for a triple that names no Apple OS.
        /// </summary>
        public static AbiApplePlatform? FromTargetTriple(string? triple)
        {
            if (string.IsNullOrWhiteSpace(triple))
                return null;
            var parts = triple.Trim().Split('-');
            if (parts.Length < 3 || !string.Equals(parts[1], "apple", StringComparison.Ordinal))
                return null;
            var os = parts[2].TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.');
            var environment = parts.Length > 3 ? parts[3] : string.Empty;
            return os switch
            {
                "ios" => string.Equals(environment, "macabi", StringComparison.Ordinal)
                    ? AbiApplePlatform.macCatalyst
                    : AbiApplePlatform.iOS,
                "macos" or "macosx" => AbiApplePlatform.macOS,
                "tvos" => AbiApplePlatform.tvOS,
                "watchos" => AbiApplePlatform.watchOS,
                "xros" or "visionos" => AbiApplePlatform.visionOS,
                _ => null,
            };
        }

        private static bool FollowsIOS(AbiApplePlatform platform)
            => platform is AbiApplePlatform.macCatalyst or AbiApplePlatform.visionOS;

        private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
    }
}
