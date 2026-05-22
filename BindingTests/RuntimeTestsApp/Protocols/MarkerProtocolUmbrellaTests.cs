// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

#pragma warning disable SB0001 // CallConvSwift P/Invoke warning

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Marker-protocol + extension-default + umbrella struct pattern.
///
/// Mirrors AppIntents AssistantSchemas: empty marker protocols (no body
/// requirements), protocol-extension default vars/funcs (isFromExtension=true,
/// protocolReq=false in ABI JSON), and an umbrella struct conforming to
/// multiple such markers via empty conformance-only extensions.
///
/// The primary gate is COMPILE: without the parser-level filter that drops
/// `isFromExtension && !IsProtocolRequirement` members from a protocol's
/// abstract contract, IMarkerBooksEnum/IMarkerCameraEnum would carry abstract
/// requirements that MarkerUmbrellaSchema does not implement, producing
/// CS0535 errors across every conforming umbrella struct.
/// </summary>
public class MarkerProtocolUmbrellaTests : TestBase
{
    public MarkerProtocolUmbrellaTests(TestResults results) : base(results) { }

    public void TestUmbrellaSchemaConstructs()
    {
        var schema = SwiftBindingsTestLib.Functions.MakeMarkerUmbrella();
        TestLogger.Info($"MarkerUmbrellaSchema constructed: {schema}");
    }
}
