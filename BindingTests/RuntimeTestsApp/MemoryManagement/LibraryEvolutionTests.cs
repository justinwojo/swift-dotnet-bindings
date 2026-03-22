// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.MemoryManagement;

/// <summary>
/// Tests for non-frozen struct evolution patterns (EvolvingConfig, EvolvingService,
/// EvolvingStatus, EvolvingRequest). Verifies opaque struct layout handling.
/// </summary>
public class LibraryEvolutionTests : TestBase
{
    public LibraryEvolutionTests(TestResults results) : base(results) { }

    #region EvolvingConfig (Non-Frozen Struct)

    public void TestMakeDefaultConfig()
    {
        using var config = TestLibFunctions.MakeDefaultConfig();
        AssertNotNull(config, "Default config created");
    }

    public void TestGetConfigTimeout()
    {
        using var config = TestLibFunctions.MakeDefaultConfig();
        var timeout = TestLibFunctions.GetConfigTimeout(config);
        AssertTrue(timeout >= 0, "Config timeout is non-negative");
    }

    public void TestWithTimeoutCreatesNew()
    {
        using var config = TestLibFunctions.MakeDefaultConfig();
        using var updated = TestLibFunctions.WithTimeout(config, 60);
        var newTimeout = TestLibFunctions.GetConfigTimeout(updated);
        AssertEqual(60, newTimeout, "Updated config has new timeout");
    }

    #endregion

    #region EvolvingService (Class)

    public void TestEvolvingServiceCreation()
    {
        using var service = new EvolvingService("test-svc", true);
        AssertEqual("test-svc", service.Name.ToString(), "Service name preserved");
        AssertTrue(service.IsEnabled, "Service initially enabled");
    }

    public void TestEvolvingServiceDescribe()
    {
        using var service = new EvolvingService("my-service", true);
        var desc = service.GetDescribe();
        AssertTrue(desc.Contains("my-service"), "Describe contains service name");
    }

    public void TestDescribeServiceFunction()
    {
        using var service = new EvolvingService("func-test", false);
        var desc = TestLibFunctions.DescribeService(service);
        AssertTrue(desc.Contains("func-test"), "DescribeService contains name");
    }

    public void TestIsActiveFunction()
    {
        using var active = new EvolvingService("active-svc", true);
        AssertTrue(TestLibFunctions.IsActive(active), "Active service reports true");

        using var inactive = new EvolvingService("inactive-svc", false);
        AssertFalse(TestLibFunctions.IsActive(inactive), "Inactive service reports false");
    }

    #endregion

    #region EvolvingStatus (Enum)

    public void TestEvolvingStatusValues()
    {
        AssertEqual(0, (int)EvolvingStatus.Active, "Active = 0");
        AssertEqual(1, (int)EvolvingStatus.Inactive, "Inactive = 1");
        AssertEqual(2, (int)EvolvingStatus.Maintenance, "Maintenance = 2");
    }

    #endregion

    #region EvolvingRequest (Non-Frozen Struct)

    public void TestProcessRequest()
    {
        using var request = new EvolvingRequest("https://api.example.com", null);
        var result = TestLibFunctions.ProcessRequest(request);
        AssertTrue(result.Contains("api.example.com"), "ProcessRequest contains endpoint");
    }

    #endregion
}
