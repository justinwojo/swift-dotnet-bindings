// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.IO;

public static class PlistGenerator
{
    public static void WriteFrameworkPlist(
        string outputPath, string bundleId, string bundleName,
        string executableName, string minOs, string plistPlatform)
    {
        var content = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
                "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>CFBundleExecutable</key>
                <string>{executableName}</string>
                <key>CFBundleIdentifier</key>
                <string>{bundleId}</string>
                <key>CFBundleInfoDictionaryVersion</key>
                <string>6.0</string>
                <key>CFBundleName</key>
                <string>{bundleName}</string>
                <key>CFBundlePackageType</key>
                <string>FMWK</string>
                <key>CFBundleVersion</key>
                <string>1.0</string>
                <key>CFBundleShortVersionString</key>
                <string>1.0</string>
                <key>MinimumOSVersion</key>
                <string>{minOs}</string>
                <key>CFBundleSupportedPlatforms</key>
                <array>
                    <string>{plistPlatform}</string>
                </array>
            </dict>
            </plist>
            """;
        File.WriteAllText(outputPath, content);
    }
}
