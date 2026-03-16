// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;

namespace RuntimeTestsApp.Metadata;

public class ExistentialMetadataTests : TestBase
{
    public ExistentialMetadataTests(TestResults results) : base(results) { }

    public void TestGetExistentialTypeMetadata_ZeroProtocols()
    {
        var metadata = TypeMetadata.GetExistentialTypeMetadata(0);
        AssertTrue(metadata.IsValid, "Zero-protocol existential metadata should be valid");
        AssertEqual(TypeMetadataKind.Existential, metadata.Kind,
            "Metadata kind should be Existential");
        TestLogger.Info($"ExistentialTypeMetadata(0) kind={metadata.Kind}");
    }

    public void TestTryGetTypeMetadata_ExistentialContainer0()
    {
        var success = TypeMetadata.TryGetTypeMetadata<ExistentialContainer0>(out var result);
        AssertTrue(success, "TryGetTypeMetadata<ExistentialContainer0> should succeed");
        AssertTrue(result!.Value.IsValid, "ExistentialContainer0 metadata should be valid");
        TestLogger.Info("TryGetTypeMetadata<ExistentialContainer0> succeeded via wrapper");
    }
}
