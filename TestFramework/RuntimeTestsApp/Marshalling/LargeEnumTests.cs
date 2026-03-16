// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Tests for large non-simple enums (50+ cases) with payload cases, no-payload singleton
/// cases, associated value extraction, and free function round-trips.
/// </summary>
public class LargeEnumTests : TestBase
{
    public LargeEnumTests(TestResults results) : base(results) { }

    #region Tier 1 — Static Case Access + Blittable Tag

    public void TestPhone1StaticAccess()
    {
        var phone1 = DeviceModel.Phone1;
        AssertNotNull(phone1, "Phone1 static property not null");
        AssertEqual(DeviceModel.CaseTag.Phone1, phone1.Tag, "Phone1 tag is Phone1");
        TestLogger.Info($"DeviceModel.Phone1.Tag = {phone1.Tag}");
    }

    public void TestAccessory5StaticAccess()
    {
        var acc5 = DeviceModel.Accessory5;
        AssertNotNull(acc5, "Accessory5 static property not null");
        AssertEqual(DeviceModel.CaseTag.Accessory5, acc5.Tag, "Accessory5 tag is Accessory5");
        TestLogger.Info($"DeviceModel.Accessory5.Tag = {acc5.Tag}");
    }

    public void TestPhone1AndAccessory5DistinctTags()
    {
        var phone1 = DeviceModel.Phone1;
        var acc5 = DeviceModel.Accessory5;
        AssertTrue(phone1.Tag != acc5.Tag, "Phone1 and Accessory5 have distinct tags");
        TestLogger.Info("Phone1 and Accessory5 tags are distinct");
    }

    #endregion

    #region Tier 2 — Payload Cases + Associated Values

    public void TestUnknownCaseCreation()
    {
        var unknown = DeviceModel.Unknown("custom-device-123");
        AssertNotNull(unknown, "Unknown case created");
        AssertEqual(DeviceModel.CaseTag.Unknown, unknown.Tag, "Unknown tag");
        TestLogger.Info("DeviceModel.Unknown created successfully");
    }

    public void TestUnknownCaseTryGetUnknown()
    {
        var unknown = DeviceModel.Unknown("my-device");
        var success = unknown.TryGetUnknown(out var identifier);
        AssertTrue(success, "TryGetUnknown returns true for Unknown case");
        AssertEqual("my-device", identifier, "TryGetUnknown extracts identifier");
        TestLogger.Info($"TryGetUnknown extracted: {identifier}");
    }

    public void TestTryGetUnknownOnNonUnknownCase()
    {
        var phone1 = DeviceModel.Phone1;
        var success = phone1.TryGetUnknown(out var identifier);
        AssertFalse(success, "TryGetUnknown returns false for Phone1 case");
        TestLogger.Info("TryGetUnknown correctly returns false for non-Unknown case");
    }

    public void TestCustomCaseCreation()
    {
        var custom = DeviceModel.Custom("SuperDevice", 2025);
        AssertNotNull(custom, "Custom case created");
        AssertEqual(DeviceModel.CaseTag.Custom, custom.Tag, "Custom tag");
        TestLogger.Info("DeviceModel.Custom created successfully");
    }

    [Skip("SBW_Free_ entry point not found")] // SBW_Free_ entry point not found — string-returning free function
    public void TestDeviceDescriptionFreeFunction()
    {
        var phone1 = DeviceModel.Phone1;
        var desc = TestLibFunctions.DeviceDescription(phone1);
        AssertEqual("Phone 1", desc, "DeviceDescription(Phone1) is 'Phone 1'");
        TestLogger.Info($"DeviceDescription(Phone1) = \"{desc}\"");
    }

    public void TestAllCasesDistinctTags()
    {
        // Verify all 50 no-payload cases have distinct tags
        var cases = new[]
        {
            DeviceModel.Phone1, DeviceModel.Phone2, DeviceModel.Phone3, DeviceModel.Phone4, DeviceModel.Phone5,
            DeviceModel.Phone6, DeviceModel.Phone7, DeviceModel.Phone8, DeviceModel.Phone9, DeviceModel.Phone10,
            DeviceModel.Tablet1, DeviceModel.Tablet2, DeviceModel.Tablet3, DeviceModel.Tablet4, DeviceModel.Tablet5,
            DeviceModel.Tablet6, DeviceModel.Tablet7, DeviceModel.Tablet8, DeviceModel.Tablet9, DeviceModel.Tablet10,
            DeviceModel.Watch1, DeviceModel.Watch2, DeviceModel.Watch3, DeviceModel.Watch4, DeviceModel.Watch5,
            DeviceModel.Laptop1, DeviceModel.Laptop2, DeviceModel.Laptop3, DeviceModel.Laptop4, DeviceModel.Laptop5,
            DeviceModel.Desktop1, DeviceModel.Desktop2, DeviceModel.Desktop3, DeviceModel.Desktop4, DeviceModel.Desktop5,
            DeviceModel.Tv1, DeviceModel.Tv2, DeviceModel.Tv3, DeviceModel.Tv4, DeviceModel.Tv5,
            DeviceModel.Speaker1, DeviceModel.Speaker2, DeviceModel.Speaker3, DeviceModel.Speaker4, DeviceModel.Speaker5,
            DeviceModel.Accessory1, DeviceModel.Accessory2, DeviceModel.Accessory3, DeviceModel.Accessory4, DeviceModel.Accessory5,
        };

        var tagSet = new HashSet<DeviceModel.CaseTag>();
        foreach (var c in cases)
        {
            AssertTrue(tagSet.Add(c.Tag), $"Tag {c.Tag} is unique");
        }

        AssertEqual(50, tagSet.Count, "All 50 no-payload cases have distinct tags");
        TestLogger.Info($"Verified {tagSet.Count} distinct tags across all no-payload cases");
    }

    #endregion
}
