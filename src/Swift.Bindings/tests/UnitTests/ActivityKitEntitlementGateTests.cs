// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using Xunit;

namespace Swift.Bindings.UnitTests;

public class ActivityKitEntitlementGateTests
{
    private const string BundleId = "com.swiftbindings.activitykit.push.tests";
    private const string DeviceUdid = "00008120-TEST";

    [Fact]
    public void RequireRuntimeEnvironment_Missing_ReproducesNamedPermissionsSignature()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => ActivityKitEntitlementGate.RequireRuntimeEnvironment(null));

        Assert.Contains("aps-environment", error.Message);
        Assert.Contains("SessionCore.PermissionsError Code=3", error.Message);
    }

    [Fact]
    public void Inspect_MissingSignedEntitlement_FailsClosedAndNamesEntitlement()
    {
        var verdict = ActivityKitEntitlementGate.Inspect(
            Signed(null), Profile("development"), BundleId, DeviceUdid, DateTimeOffset.UtcNow);

        Assert.False(verdict.Success);
        Assert.Contains("signed app is missing required entitlement 'aps-environment'", verdict.Message);
    }

    [Fact]
    public void Inspect_MissingProfileEntitlement_FailsClosedAndNamesEntitlement()
    {
        var verdict = ActivityKitEntitlementGate.Inspect(
            Signed("development"), Profile(null), BundleId, DeviceUdid, DateTimeOffset.UtcNow);

        Assert.False(verdict.Success);
        Assert.Contains("embedded provisioning profile is missing required entitlement 'aps-environment'", verdict.Message);
    }

    [Theory]
    [InlineData("production")]
    [InlineData("development")]
    public void Inspect_MatchingExplicitCapability_Passes(string environment)
    {
        var verdict = ActivityKitEntitlementGate.Inspect(
            Signed(environment), Profile(environment), BundleId, DeviceUdid, DateTimeOffset.UtcNow);

        Assert.True(verdict.Success);
        Assert.Equal(environment, verdict.Environment);
    }

    [Fact]
    public void Inspect_WildcardProfile_IsRejectedForPushCapability()
    {
        var verdict = ActivityKitEntitlementGate.Inspect(
            Signed("development", "TL2K6QUQEH.com.*"),
            Profile("development", "TL2K6QUQEH.com.*"),
            BundleId, DeviceUdid, DateTimeOffset.UtcNow);

        Assert.False(verdict.Success);
        Assert.Contains("wildcard application-identifier", verdict.Message);
    }

    [Fact]
    public void Inspect_UnsupportedEnvironment_IsRejected()
    {
        var verdict = ActivityKitEntitlementGate.Inspect(
            Signed("sandbox"), Profile("sandbox"), BundleId, DeviceUdid, DateTimeOffset.UtcNow);

        Assert.False(verdict.Success);
        Assert.Contains("unsupported entitlement 'aps-environment'", verdict.Message);
    }

    [Fact]
    public void Inspect_ApplicationIdentifierMustUseDeclaredTeam()
    {
        var verdict = ActivityKitEntitlementGate.Inspect(
            Signed("development", "OTHERTEAM." + BundleId),
            Profile("development", "OTHERTEAM." + BundleId),
            BundleId, DeviceUdid, DateTimeOffset.UtcNow);

        Assert.False(verdict.Success);
        Assert.Contains("does not use team", verdict.Message);
    }

    private static string Signed(string? environment, string appId = "TL2K6QUQEH." + BundleId) =>
        Plist(Entry("application-identifier", appId) +
              Entry("com.apple.developer.team-identifier", "TL2K6QUQEH") +
              (environment is null ? "" : Entry(ActivityKitEntitlementGate.RequiredEntitlement, environment)));

    private static string Profile(string? environment, string appId = "TL2K6QUQEH." + BundleId) =>
        Plist("<key>ExpirationDate</key><date>2099-01-01T00:00:00Z</date>" +
              $"<key>ProvisionedDevices</key><array><string>{DeviceUdid}</string></array>" +
              "<key>TeamIdentifier</key><array><string>TL2K6QUQEH</string></array>" +
              "<key>Entitlements</key><dict>" +
              Entry("application-identifier", appId) +
              Entry("com.apple.developer.team-identifier", "TL2K6QUQEH") +
              (environment is null ? "" : Entry(ActivityKitEntitlementGate.RequiredEntitlement, environment)) +
              "</dict>");

    private static string Entry(string key, string value) => $"<key>{key}</key><string>{value}</string>";
    private static string Plist(string body) => $"<?xml version=\"1.0\"?><plist version=\"1.0\"><dict>{body}</dict></plist>";
}
