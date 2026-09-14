// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

/// <summary>
/// Shared fail-closed policy for ActivityKit push-token capability evidence.
/// The build harness checks the final signature plus embedded profile; the app
/// rechecks its own signed entitlement before issuing the request.
/// </summary>
internal static class ActivityKitEntitlementGate
{
    internal const string RequiredEntitlement = "aps-environment";

    internal sealed record Verdict(
        bool Success,
        string Message,
        string? Environment = null,
        string? ApplicationIdentifier = null,
        string? TeamIdentifier = null);

    internal static string RequireRuntimeEnvironment(string? environment)
    {
        if (string.IsNullOrWhiteSpace(environment))
            throw new InvalidOperationException(
                "ActivityKit push-token request is not qualified: the running app is missing signed entitlement " +
                $"'{RequiredEntitlement}'. Without it ActivityKit reports SessionCore.PermissionsError Code=3.");
        if (!IsAcceptedEnvironment(environment))
            throw new InvalidOperationException(
                $"ActivityKit push-token request is not qualified: signed entitlement '{RequiredEntitlement}' " +
                $"has unsupported value '{environment}'. Expected 'development' or 'production'.");
        return environment;
    }

    internal static Verdict InspectProvisioningProfile(
        string provisioningProfilePlist,
        string expectedBundleId,
        string? expectedDeviceUdid,
        DateTimeOffset now)
    {
        if (!TryReadRootDictionary(provisioningProfilePlist, out var profile, out var profileError))
            return Fail($"provisioning profile is unreadable: {profileError}");
        if (!TryDictionaryValue(profile!, "Entitlements", out var entitlements))
            return Fail($"provisioning profile has no Entitlements dictionary; required entitlement '{RequiredEntitlement}' is unavailable");

        var environment = StringValue(entitlements!, RequiredEntitlement);
        if (string.IsNullOrWhiteSpace(environment))
            return Fail($"provisioning profile is missing required entitlement '{RequiredEntitlement}'");
        if (!IsAcceptedEnvironment(environment))
            return Fail($"provisioning profile has unsupported entitlement '{RequiredEntitlement}'='{environment}'");
        var applicationIdentifier = StringValue(entitlements!, "application-identifier");
        if (string.IsNullOrWhiteSpace(applicationIdentifier))
            return Fail("provisioning profile is missing 'application-identifier'");
        if (applicationIdentifier!.Contains('*', StringComparison.Ordinal))
            return Fail($"provisioning profile uses wildcard application-identifier '{applicationIdentifier}'; push notifications require an explicit App ID for '{expectedBundleId}'");
        if (!applicationIdentifier.EndsWith("." + expectedBundleId, StringComparison.Ordinal))
            return Fail($"provisioning profile application-identifier '{applicationIdentifier}' does not match expected bundle id '{expectedBundleId}'");

        var team = StringValue(entitlements!, "com.apple.developer.team-identifier")
            ?? ArrayValues(profile!, "TeamIdentifier").SingleOrDefault();
        if (string.IsNullOrWhiteSpace(team))
            return Fail("provisioning profile is missing the team identifier");
        if (!string.Equals(applicationIdentifier, team + "." + expectedBundleId, StringComparison.Ordinal))
            return Fail($"provisioning profile application-identifier '{applicationIdentifier}' does not use team '{team}' and bundle id '{expectedBundleId}'");
        var expiration = DateValue(profile!, "ExpirationDate");
        if (expiration is null)
            return Fail("provisioning profile has no readable ExpirationDate");
        if (expiration <= now)
            return Fail($"provisioning profile expired at {expiration:O}");

        if (!string.IsNullOrWhiteSpace(expectedDeviceUdid))
        {
            var devices = ArrayValues(profile!, "ProvisionedDevices");
            if (devices.Count == 0)
                return Fail("provisioning profile contains no ProvisionedDevices list");
            if (!devices.Contains(expectedDeviceUdid!, StringComparer.OrdinalIgnoreCase))
                return Fail($"provisioning profile does not include device '{expectedDeviceUdid}'");
        }

        return new Verdict(true,
            $"provisioning profile grants '{RequiredEntitlement}'='{environment}' for '{expectedBundleId}'",
            environment, applicationIdentifier, team);
    }

    internal static Verdict Inspect(
        string signedEntitlementsPlist,
        string provisioningProfilePlist,
        string expectedBundleId,
        string? expectedDeviceUdid,
        DateTimeOffset now)
    {
        if (!TryReadRootDictionary(signedEntitlementsPlist, out var signed, out var signedError))
            return Fail($"signed app entitlements are unreadable: {signedError}");
        if (!TryReadRootDictionary(provisioningProfilePlist, out var profile, out var profileError))
            return Fail($"embedded provisioning profile is unreadable: {profileError}");

        var signedEnvironment = StringValue(signed!, RequiredEntitlement);
        if (string.IsNullOrWhiteSpace(signedEnvironment))
            return Fail($"signed app is missing required entitlement '{RequiredEntitlement}'");
        if (!IsAcceptedEnvironment(signedEnvironment))
            return Fail($"signed app has unsupported entitlement '{RequiredEntitlement}'='{signedEnvironment}'");
        if (!TryDictionaryValue(profile!, "Entitlements", out var profileEntitlements))
            return Fail($"embedded provisioning profile has no Entitlements dictionary; required entitlement '{RequiredEntitlement}' is unavailable");
        var profileEnvironment = StringValue(profileEntitlements!, RequiredEntitlement);
        if (string.IsNullOrWhiteSpace(profileEnvironment))
            return Fail($"embedded provisioning profile is missing required entitlement '{RequiredEntitlement}'");
        if (!IsAcceptedEnvironment(profileEnvironment))
            return Fail($"embedded provisioning profile has unsupported entitlement '{RequiredEntitlement}'='{profileEnvironment}'");
        if (!string.Equals(signedEnvironment, profileEnvironment, StringComparison.Ordinal))
            return Fail($"entitlement '{RequiredEntitlement}' disagrees between signed app ('{signedEnvironment}') and embedded profile ('{profileEnvironment}')");

        var signedApplicationIdentifier = StringValue(signed!, "application-identifier");
        var profileApplicationIdentifier = StringValue(profileEntitlements!, "application-identifier");
        if (string.IsNullOrWhiteSpace(signedApplicationIdentifier))
            return Fail("signed app is missing 'application-identifier'");
        if (string.IsNullOrWhiteSpace(profileApplicationIdentifier))
            return Fail("embedded provisioning profile is missing 'application-identifier'");
        if (profileApplicationIdentifier!.Contains('*', StringComparison.Ordinal))
            return Fail($"embedded provisioning profile uses wildcard application-identifier '{profileApplicationIdentifier}'; push notifications require an explicit App ID for '{expectedBundleId}'");
        if (!string.Equals(signedApplicationIdentifier, profileApplicationIdentifier, StringComparison.Ordinal))
            return Fail($"application-identifier disagrees between signed app ('{signedApplicationIdentifier}') and embedded profile ('{profileApplicationIdentifier}')");
        if (!signedApplicationIdentifier!.EndsWith("." + expectedBundleId, StringComparison.Ordinal))
            return Fail($"signed application-identifier '{signedApplicationIdentifier}' does not match expected bundle id '{expectedBundleId}'");

        var signedTeam = StringValue(signed!, "com.apple.developer.team-identifier");
        var profileTeam = StringValue(profileEntitlements!, "com.apple.developer.team-identifier")
            ?? ArrayValues(profile!, "TeamIdentifier").SingleOrDefault();
        if (string.IsNullOrWhiteSpace(signedTeam) || string.IsNullOrWhiteSpace(profileTeam))
            return Fail("signed app or embedded provisioning profile is missing the team identifier");
        if (!string.Equals(signedTeam, profileTeam, StringComparison.Ordinal))
            return Fail($"team identifier disagrees between signed app ('{signedTeam}') and embedded profile ('{profileTeam}')");
        if (!string.Equals(signedApplicationIdentifier, signedTeam + "." + expectedBundleId, StringComparison.Ordinal))
            return Fail($"signed application-identifier '{signedApplicationIdentifier}' does not use team '{signedTeam}' and bundle id '{expectedBundleId}'");

        var expiration = DateValue(profile!, "ExpirationDate");
        if (expiration is null)
            return Fail("embedded provisioning profile has no readable ExpirationDate");
        if (expiration <= now)
            return Fail($"embedded provisioning profile expired at {expiration:O}");

        if (!string.IsNullOrWhiteSpace(expectedDeviceUdid))
        {
            var devices = ArrayValues(profile!, "ProvisionedDevices");
            if (devices.Count == 0)
                return Fail("embedded provisioning profile contains no ProvisionedDevices list");
            if (!devices.Contains(expectedDeviceUdid!, StringComparer.OrdinalIgnoreCase))
                return Fail($"embedded provisioning profile does not include device '{expectedDeviceUdid}'");
        }

        return new Verdict(
            true,
            $"signed app and embedded profile both grant '{RequiredEntitlement}'='{signedEnvironment}' for '{expectedBundleId}'",
            signedEnvironment,
            signedApplicationIdentifier,
            signedTeam);
    }

    private static Verdict Fail(string detail) =>
        new(false, $"ActivityKit push-token capability preflight failed: {detail}.");

    private static bool IsAcceptedEnvironment(string environment) =>
        environment is "development" or "production";

    private static bool TryReadRootDictionary(string xml, out XElement? dictionary, out string? error)
    {
        dictionary = null;
        error = null;
        try
        {
            var document = XDocument.Parse(xml, LoadOptions.None);
            dictionary = document.Root?.Element("dict");
            if (dictionary is null) error = "plist root does not contain a dict";
            return dictionary is not null;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string? StringValue(XElement dictionary, string key)
    {
        var value = ValueElement(dictionary, key);
        return value?.Name.LocalName == "string" ? value.Value : null;
    }

    private static DateTimeOffset? DateValue(XElement dictionary, string key)
    {
        var value = ValueElement(dictionary, key);
        if (value?.Name.LocalName != "date") return null;
        return DateTimeOffset.TryParse(value.Value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }

    private static bool TryDictionaryValue(XElement dictionary, string key, out XElement? value)
    {
        value = ValueElement(dictionary, key);
        return value?.Name.LocalName == "dict";
    }

    private static IReadOnlyList<string> ArrayValues(XElement dictionary, string key)
    {
        var value = ValueElement(dictionary, key);
        return value?.Name.LocalName == "array"
            ? value.Elements("string").Select(e => e.Value).ToArray()
            : Array.Empty<string>();
    }

    private static XElement? ValueElement(XElement dictionary, string key)
    {
        var children = dictionary.Elements().ToArray();
        for (var index = 0; index + 1 < children.Length; index++)
            if (children[index].Name.LocalName == "key" && children[index].Value == key)
                return children[index + 1];
        return null;
    }
}
