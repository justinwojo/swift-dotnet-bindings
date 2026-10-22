// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Swift;

public static class PackageSurface
{
    // These six assignments require the platform Runtime DLL. The neutral DLL
    // deliberately has none of these conversions, so a NuGet fallback fails here.
    public static Swift.CGPoint ToSwift(CoreGraphics.CGPoint value) => value;
    public static CoreGraphics.CGPoint ToApple(Swift.CGPoint value) => value;
    public static Swift.CGSize ToSwift(CoreGraphics.CGSize value) => value;
    public static CoreGraphics.CGSize ToApple(Swift.CGSize value) => value;
    public static Swift.CGRect ToSwift(CoreGraphics.CGRect value) => value;
    public static CoreGraphics.CGRect ToApple(Swift.CGRect value) => value;

    // The referenced Identity assembly and this consumer both expose the public
    // canonical Apple type. Assignment must compile without local stand-in types.
    public static Swift.Foundation.Locale.Language CanonicalIdentity(
        Swift.Foundation.Locale.Language value) => CanonicalAppleIdentity.Echo(value);
    public static SwiftArray<Swift.Foundation.Locale.Language> CanonicalArray(
        SwiftArray<Swift.Foundation.Locale.Language> value) => value;
    public static SwiftString RuntimeSurface(SwiftString value) => value;
}
