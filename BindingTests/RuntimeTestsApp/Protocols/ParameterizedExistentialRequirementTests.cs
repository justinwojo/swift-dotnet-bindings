// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.Versioning;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// A protocol whose requirements take and return a parameterized protocol existential
/// (<c>any RequestTask&lt;Int, RequestTaskError&gt;</c>). Swift only has runtime metadata for that
/// type from iOS 16 / macOS 13, so the binding raises those members' floor and guards the witnesses
/// Swift calls back into. The protocol's other requirements are not affected and must still reach a
/// C# conformer.
/// </summary>
public class ParameterizedExistentialRequirementTests : TestBase
{
    public ParameterizedExistentialRequirementTests(TestResults results) : base(results) { }

    [SupportedOSPlatform("ios16.0")]
    [SupportedOSPlatform("macos13.0")]
    public void TestUngatedRequirementDispatchesToCSharpConformer()
    {
        var client = new NamedRequestTaskClient("from-csharp");
        using var api = new RequestTaskApi();

        AssertEqual("from-csharp", api.ClientName(client),
            "the requirement that names no parameterized existential dispatches into the C# conformer");

        GC.KeepAlive(client);
    }

    // The initializer of a generic struct whose parameter is constrained to Error takes that
    // conformance's witness table after the type arguments' metadata; the value it stores must
    // come back out when Swift reads the struct.
#pragma warning disable SB0001
    public void TestErrorConstrainedGenericStructInitializerStoresValue()
    {
        using var task = new PendingRequestTask<nint, RequestTaskError>(42);

        AssertEqual((nint)42, TestLibFunctions.PendingRequestTaskValue(task),
            "the initializer stored the value it was given");
    }
#pragma warning restore SB0001

    // The request-task interface has no bindable members; the conformer only has to name it to
    // satisfy the client's requirements.
#pragma warning disable SB0004
    [SupportedOSPlatform("ios16.0")]
    [SupportedOSPlatform("macos13.0")]
    private sealed class NamedRequestTaskClient(string name) : IRequestTaskClient
    {
        public string Name => name;

        public IRequestTask<nint, RequestTaskError> Fetch(nint id) =>
            throw new NotSupportedException("not reached by this test");

        public nint Submit(IRequestTask<nint, RequestTaskError> request) =>
            throw new NotSupportedException("not reached by this test");
    }
#pragma warning restore SB0004
}
