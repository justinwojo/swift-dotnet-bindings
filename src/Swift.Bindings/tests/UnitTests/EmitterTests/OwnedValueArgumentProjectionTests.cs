// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
using Xunit;

namespace BindingsGeneration.Tests;

public class OwnedValueArgumentProjectionTests
{
    [Theory]
    [InlineData("string", "SwiftString", "valueSwift.Payload")]
    [InlineData("frozen", "TestModule.Frozen", "value.Payload")]
    [InlineData("array", "SwiftArray<SwiftString>", "valueSwift.Payload")]
    [InlineData("set", "SwiftSet<SwiftString>", "valueSwift.Payload")]
    [InlineData("dictionary", "SwiftDictionary<SwiftString, SwiftString>", "valueSwift.Payload")]
    [InlineData("optional", "SwiftOptional<SwiftString>", "valueSwift.Payload")]
    public void ValueCarrierPlan_SpecifiesMetadataAndLivePayloadWithoutRawDonation(
        string shape, string carrier, string payload)
    {
        ITypeProjection projection = shape switch
        {
            "string" => new StringProjection(),
            "frozen" => new FrozenWithMemoryProjection("TestModule.Frozen"),
            "array" => new ArrayProjection(new StringProjection(), isParameter: true),
            "set" => new SetProjection(new StringProjection(), isParameter: true),
            "dictionary" => new DictionaryProjection(new StringProjection(), new StringProjection(), isParameter: true),
            _ => new OptionalProjection(new StringProjection()),
        };
        var plan = projection.GetParameterPlan("value");
        Assert.Equal(new OwnedValueArgument(carrier, payload), plan.OwnedValueArgument);
        Assert.Null(plan.OwnedHandOverStatement);
        // The type that describes the copy is the owning carrier, never its projected C# type
        // (string/list/nullable) or a borrowed ABI Buffer. Setup retains its own normal disposal.
        Assert.NotEmpty(plan.SetupStatements);
    }
}
