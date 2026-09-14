// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Nuke.Common.IO;
using Serilog;

partial class Build
{
    const string ActivityKitPushTokenBundleId = "com.swiftbindings.activitykit.push.tests";

    string? EffectiveActivityKitProvisioningProfile =>
        !string.IsNullOrWhiteSpace(ActivityKitProvisioningProfile)
            ? ActivityKitProvisioningProfile
            : Environment.GetEnvironmentVariable("ACTIVITYKIT_PROVISIONING_PROFILE");

    sealed record ActivityKitProvisioningSelection(string Name, string Uuid, string Path, string Plist);

    ActivityKitProvisioningSelection RequireActivityKitProvisioningProfile(string? deviceUdid)
    {
        var requested = EffectiveActivityKitProvisioningProfile;
        if (string.IsNullOrWhiteSpace(requested))
            throw new Exception(
                "ActivityKit push-token capability preflight failed: no explicit provisioning profile was supplied; " +
                $"required entitlement '{ActivityKitEntitlementGate.RequiredEntitlement}' could not be proved.");

        var profileDirs = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Developer", "Xcode", "UserData", "Provisioning Profiles"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "MobileDevice", "Provisioning Profiles"),
        };
        var candidates = profileDirs
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.mobileprovision"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var requestedPath = File.Exists(requested) ? Path.GetFullPath(requested) : null;
        if (requestedPath is not null)
            candidates = candidates.Append(requestedPath).Distinct(StringComparer.Ordinal).ToArray();

        foreach (var path in candidates)
        {
            var decoded = CaptureActivityKitEvidence("/usr/bin/security", ["cms", "-D", "-i", path]);
            var plist = decoded.ExitCode == 0 ? ExtractActivityKitPlist(decoded.Combined) : null;
            if (plist is null) continue;
            var document = XDocument.Parse(plist, LoadOptions.None);
            var dictionary = document.Root?.Element("dict");
            if (dictionary is null) continue;
            var name = ActivityKitPlistString(dictionary, "Name");
            var uuid = ActivityKitPlistString(dictionary, "UUID");
            var matches = string.Equals(path, requestedPath, StringComparison.Ordinal)
                || string.Equals(Path.GetFileNameWithoutExtension(path), requested, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, requested, StringComparison.Ordinal)
                || string.Equals(uuid, requested, StringComparison.OrdinalIgnoreCase);
            if (!matches) continue;

            var verdict = ActivityKitEntitlementGate.InspectProvisioningProfile(
                plist, ActivityKitPushTokenBundleId, deviceUdid, DateTimeOffset.UtcNow);
            if (!verdict.Success)
                throw new Exception($"{verdict.Message} Profile: {name ?? path}");
            if (string.IsNullOrWhiteSpace(uuid))
                throw new Exception(
                    $"ActivityKit push-token capability preflight failed: provisioning profile '{name ?? path}' has no UUID.");
            Log.Information("ActivityKit provisioning profile preflight passed: {Name} ({Uuid})", name, uuid);
            return new ActivityKitProvisioningSelection(name ?? requested, uuid, path, plist);
        }

        throw new Exception(
            $"ActivityKit push-token capability preflight failed: provisioning profile '{requested}' was not found or " +
            $"could not be decoded; required entitlement '{ActivityKitEntitlementGate.RequiredEntitlement}' could not be proved.");
    }

    static string? ActivityKitPlistString(XElement dictionary, string key)
    {
        var children = dictionary.Elements().ToArray();
        for (var index = 0; index + 1 < children.Length; index++)
            if (children[index].Name.LocalName == "key" && children[index].Value == key
                && children[index + 1].Name.LocalName == "string")
                return children[index + 1].Value;
        return null;
    }

    /// <summary>
    /// The source entitlements file is an intent, not proof. Verify the final
    /// signature and the profile embedded by the .NET iOS build immediately
    /// before install, then retain the exact plists and their hashes.
    /// </summary>
    void VerifyActivityKitPushTokenCapability(string appPath, string deviceUdid)
    {
        if (!ActivityKitPushToken) return;

        var verify = CaptureActivityKitEvidence(
            "/usr/bin/codesign", ["--verify", "--strict", appPath]);
        if (verify.ExitCode != 0)
            throw new Exception(
                $"ActivityKit push-token capability preflight failed: final app signature verification failed for {appPath}: {verify.Combined.Trim()}");

        var signed = CaptureActivityKitEvidence(
            "/usr/bin/codesign", ["--display", "--entitlements", ":-", appPath]);
        if (signed.ExitCode != 0)
            throw new Exception(
                $"ActivityKit push-token capability preflight failed: could not read signed app entitlements from {appPath}: {signed.Combined.Trim()}");
        var signedPlist = ExtractActivityKitPlist(signed.Combined)
            ?? throw new Exception(
                $"ActivityKit push-token capability preflight failed: signed app entitlements contained no plist; required entitlement '{ActivityKitEntitlementGate.RequiredEntitlement}' could not be proved. App: {appPath}");

        var embeddedProfile = Path.Combine(appPath, "embedded.mobileprovision");
        if (!File.Exists(embeddedProfile))
            throw new Exception(
                $"ActivityKit push-token capability preflight failed: final app has no embedded.mobileprovision; required entitlement '{ActivityKitEntitlementGate.RequiredEntitlement}' could not be proved. App: {appPath}");
        var profile = CaptureActivityKitEvidence(
            "/usr/bin/security", ["cms", "-D", "-i", embeddedProfile]);
        if (profile.ExitCode != 0)
            throw new Exception(
                $"ActivityKit push-token capability preflight failed: embedded provisioning profile could not be decoded: {profile.Combined.Trim()}");
        var profilePlist = ExtractActivityKitPlist(profile.Combined)
            ?? throw new Exception(
                $"ActivityKit push-token capability preflight failed: embedded provisioning profile contained no plist; required entitlement '{ActivityKitEntitlementGate.RequiredEntitlement}' could not be proved. App: {appPath}");

        var provisioningDeviceUdid = ResolveActivityKitProvisioningDeviceUdid(deviceUdid);
        var verdict = ActivityKitEntitlementGate.Inspect(
            signedPlist,
            profilePlist,
            ActivityKitPushTokenBundleId,
            provisioningDeviceUdid,
            DateTimeOffset.UtcNow);
        if (!verdict.Success)
            throw new Exception($"{verdict.Message} App: {appPath}");

        var evidenceDir = RootDirectory / "artifacts" / "activitykit-capability" /
            $"{DateTime.UtcNow:yyyyMMdd-HHmmss.fffZ}-p{Environment.ProcessId}";
        Directory.CreateDirectory(evidenceDir);
        File.WriteAllText(evidenceDir / "signed-entitlements.plist", signedPlist);
        File.WriteAllText(evidenceDir / "embedded-profile.plist", profilePlist);
        var signedHash = ActivityKitEvidenceSha256(signedPlist);
        var profileHash = ActivityKitEvidenceSha256(profilePlist);
        File.WriteAllText(evidenceDir / "summary.txt",
            $"app={appPath}\n" +
            $"bundle_id={ActivityKitPushTokenBundleId}\n" +
            $"devicectl_identifier={deviceUdid}\n" +
            $"provisioning_device_udid={provisioningDeviceUdid}\n" +
            $"entitlement={ActivityKitEntitlementGate.RequiredEntitlement}\n" +
            $"environment={verdict.Environment}\n" +
            $"application_identifier={verdict.ApplicationIdentifier}\n" +
            $"team_identifier={verdict.TeamIdentifier}\n" +
            $"signed_entitlements_sha256={signedHash}\n" +
            $"embedded_profile_sha256={profileHash}\n");

        Log.Information("ActivityKit push-token capability verified: {Message}", verdict.Message);
        Log.Information("ActivityKit signed-entitlement/profile evidence: {Directory}", evidenceDir);
    }

    static (int ExitCode, string Combined) CaptureActivityKitEvidence(
        string fileName,
        IReadOnlyList<string> arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout + "\n" + stderr);
    }

    static string? ExtractActivityKitPlist(string text)
    {
        var start = text.IndexOf("<?xml", StringComparison.Ordinal);
        if (start < 0) start = text.IndexOf("<plist", StringComparison.Ordinal);
        var end = text.LastIndexOf("</plist>", StringComparison.Ordinal);
        return start >= 0 && end >= start
            ? text[start..(end + "</plist>".Length)]
            : null;
    }

    static string ActivityKitEvidenceSha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    static string ResolveActivityKitProvisioningDeviceUdid(string devicectlIdentifier)
    {
        var details = CaptureActivityKitEvidence(
            "/usr/bin/xcrun", ["devicectl", "device", "info", "details", "--device", devicectlIdentifier]);
        if (details.ExitCode != 0)
            throw new Exception(
                $"ActivityKit push-token capability preflight failed: could not resolve the provisioning UDID " +
                $"for device '{devicectlIdentifier}': {details.Combined.Trim()}");
        var match = Regex.Match(details.Combined, @"(?m)^\s*• udid:\s*(\S+)\s*$", RegexOptions.CultureInvariant);
        if (!match.Success)
            throw new Exception(
                $"ActivityKit push-token capability preflight failed: device details for '{devicectlIdentifier}' " +
                "did not contain a hardware provisioning UDID.");
        return match.Groups[1].Value;
    }
}
