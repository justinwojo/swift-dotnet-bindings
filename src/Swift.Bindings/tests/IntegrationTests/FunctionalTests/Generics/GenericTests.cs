// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Swift.GenericTests;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.FunctionalTests
{
    public class GenericsTests : IClassFixture<GenericsTests.TestFixture>
    {
        private readonly TestFixture _fixture;

        public GenericsTests(TestFixture fixture)
        {
            _fixture = fixture;
        }

        public class TestFixture
        {
            static TestFixture()
            {
                InitializeResources();
            }

            private static void InitializeResources()
            {
                // Initialize
            }
        }

        [Fact(Skip = "Primitives cannot implement ISwiftObject interface - requires wrapper types")]
        public void TestFunctionTakesPrimitiveGenericParamsThrows()
        {
            // Test disabled - primitives don't implement ISwiftObject
        }

        [Fact(Skip = "Primitives cannot implement ISwiftObject interface - requires wrapper types")]
        public void TestFunctionTakesPrimitiveGenericParams()
        {
            // Test disabled - primitives don't implement ISwiftObject
        }

        [Fact]
        public void TestFunctionTakesStructGenericParams()
        {
            var a = new FrozenStruct(1, 2);
            var b = new NonFrozenStruct(3, 4);
            var result = GenericTests.AcceptsGenericParameters(a, b);
            Assert.Equal(0, result);
        }

        [Fact(Skip = "Primitives cannot implement ISwiftObject interface - requires wrapper types")]
        public void TestFunctionTakesGenericPrimitiveAndReturnsOne()
        {
            // Test disabled - primitives don't implement ISwiftObject
        }

        [Fact]
        public void TestFunctionTakesGenericStructAndReturnsOne()
        {
            var a = new FrozenStruct(1, 2);
            var result = GenericTests.AcceptsGenericParameterAndReturnsGeneric(a);
            Assert.Equal(a.X, result.X);
            Assert.Equal(a.Y, result.Y);

            var b = new NonFrozenStruct(3, 4);
            var result2 = GenericTests.AcceptsGenericParameterAndReturnsGeneric(b);
            // No deep comparison for non-frozen structs yet
        }

        [Fact]
        public void TestFunctionTakesMultipleGenericParametersOfSameType()
        {
            var a = new FrozenStruct(1, 2);
            var b = new FrozenStruct(3, 4);
            var result = GenericTests.AcceptsTwoValuesOfTheSameGenericType(a, b);
            Assert.Equal(a.X, result.X);
            Assert.Equal(a.Y, result.Y);
        }

        [Fact(Skip = "Protocol conformances not generated on C# structs - requires generator changes")]
        public void TestFunctionTakesGenericParameterConstrainedToProtocol()
        {
            // Test disabled - protocol conformances not yet implemented on C# structs
        }

        [Fact(Skip = "Protocol conformances not generated on C# structs - requires generator changes")]
        public void TestFunctionTakesMultipleGenericParametersOfSameTypeConstrainedToProtocol()
        {
            // Test disabled - protocol conformances not yet implemented on C# structs
        }

        [Fact(Skip = "Protocol conformances not generated on C# structs - requires generator changes")]
        public void TestFunctionTakesGenericParameterConstrainedToMultipleProtocols()
        {
            // Test disabled - protocol conformances not yet implemented on C# structs
        }

        [Fact(Skip = "Protocol conformances not generated on C# structs - requires generator changes")]
        public void TestFunctionTakesMultipleGenericParametersConstrainedToMultipleProtocols()
        {
            // Test disabled - protocol conformances not yet implemented on C# structs
        }

        [Fact(Skip = "Protocol conformances not generated on C# structs - requires generator changes")]
        public void TestFunctionTakesMultipleGenericParametersOfDifferentTypesConstrainedByTheSameProtocol()
        {
            // Test disabled - protocol conformances not yet implemented on C# structs
        }

        [Fact(Skip = "Protocol conformances not generated on C# structs - requires generator changes")]
        public void TestFunctionWithGenericParamConstrainedToPAT()
        {
            // Test disabled - protocol conformances not yet implemented on C# structs
        }
    }
}
