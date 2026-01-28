// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Swift;
using BindingsGeneration.Tests;
using Swift;
using Swift.Runtime;
using Swift.Runtime.InteropServices;
using Xunit;

using Bindings = Swift.ClosuresTests;

namespace BindingsGeneration.FunctionalTests
{
    public class ClosuresTests : IClassFixture<ClosuresTests.TestFixture>
    {
        private readonly TestFixture _fixture;

        public ClosuresTests(TestFixture fixture)
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

        // MARK: - @escaping Closure Tests (Primitive Types)

        [Fact]
        public void TestInt32Callback()
        {
            // Pass a C# delegate that doubles the input
            int result = Bindings.ClosuresTests.callWithInt32(x => x * 2);
            Assert.Equal(84, result); // 42 * 2 = 84
        }

        [Fact]
        public void TestVoidCallback()
        {
            bool wasCalled = false;
            Bindings.ClosuresTests.callVoidCallback(() => { wasCalled = true; });
            Assert.True(wasCalled);
        }

        [Fact]
        public void TestMultiArgCallback()
        {
            // Swift calls callback(10, 20)
            int result = Bindings.ClosuresTests.callMultiArg((a, b) => a + b);
            Assert.Equal(30, result); // 10 + 20 = 30
        }

        [Fact]
        public void TestBoolCallback()
        {
            // Swift calls callback(true), we negate it
            bool result = Bindings.ClosuresTests.callBoolCallback(b => !b);
            Assert.False(result);
        }

        [Fact]
        public void TestDoubleCallback()
        {
            double result = Bindings.ClosuresTests.callDoubleCallback(d => d * 2.0);
            Assert.Equal(6.28318, result, precision: 4); // 3.14159 * 2
        }

        // MARK: - Struct with Closure Methods

        [Fact]
        public void TestClosureConsumer_InstanceMethod()
        {
            var consumer = new Bindings.ClosureConsumer(multiplier: 3);
            // value * multiplier = 5 * 3 = 15, then transform adds 10
            int result = consumer.applyToValue(5, x => x + 10);
            Assert.Equal(25, result); // (5 * 3) + 10 = 25
        }

        [Fact]
        public void TestClosureConsumer_StaticMethod()
        {
            int result = Bindings.ClosureConsumer.processWithClosure(7, x => x * x);
            Assert.Equal(49, result); // 7 * 7 = 49
        }

        // MARK: - Closure Called Multiple Times

        [Fact]
        public void TestClosureCalledMultipleTimes()
        {
            // callback is called with 1, 2, 3, 4, 5 and results are summed
            // If callback doubles input: 2 + 4 + 6 + 8 + 10 = 30
            int result = Bindings.ClosuresTests.callMultipleTimes(x => x * 2, times: 5);
            Assert.Equal(30, result);
        }

        [Fact]
        public void TestClosureWithStateCaptured()
        {
            int counter = 0;
            // Each call increments counter and returns it
            int result = Bindings.ClosuresTests.callMultipleTimes(x => { counter++; return x + counter; }, times: 3);
            // i=1: counter=1, return 1+1=2
            // i=2: counter=2, return 2+2=4
            // i=3: counter=3, return 3+3=6
            // sum = 2 + 4 + 6 = 12
            Assert.Equal(12, result);
            Assert.Equal(3, counter);
        }
    }
}
