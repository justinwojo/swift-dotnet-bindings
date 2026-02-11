// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Swift;
using BindingsGeneration.Tests;
using Swift;
using Swift.Runtime;
using Swift.Runtime.InteropServices;
using Swift.StructsTests;
using Xunit;

using Bindings = Swift.AsyncTests;


namespace BindingsGeneration.FunctionalTests
{
    public class AsyncTests : IClassFixture<AsyncTests.TestFixture>
    {
        private readonly TestFixture _fixture;

        public AsyncTests(TestFixture fixture)
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
                // Initialize Swift concurrency via shared runtime library
                SwiftConcurrency.Initialize();
            }
        }

        [Fact]
        public async Task TestInstanceMethods()
        {
            var myStruct = new Bindings.AsyncStruct(42);

            var stopwatch = Stopwatch.StartNew();
            await myStruct.VoidAsync();
            stopwatch.Stop();
            Assert.True(stopwatch.Elapsed.TotalSeconds >= 1);

            stopwatch.Restart();
            ulong seconds = 2;
            ulong result = await myStruct.GetNonVoidAsync(seconds);
            stopwatch.Stop();
            Assert.Equal(seconds, result);
            Assert.True(stopwatch.Elapsed.TotalSeconds >= seconds);
        }

        [Fact]
        public async Task TestStaticMethods()
        {
            var stopwatch = Stopwatch.StartNew();
            await Bindings.AsyncStruct.VoidStaticAsync();
            stopwatch.Stop();
            Assert.True(stopwatch.Elapsed.TotalSeconds >= 1);

            stopwatch.Restart();
            ulong seconds = 2;
            ulong result = await Bindings.AsyncStruct.GetNonVoidStaticAsync(seconds);
            stopwatch.Stop();
            Assert.Equal(seconds, result);
            Assert.True(stopwatch.Elapsed.TotalSeconds >= 1);
        }

        [Fact(Skip = "Primitives cannot implement ISwiftObject interface - requires wrapper types")]
        public async Task TestGenericUnconstrained()
        {
            // Test disabled - primitives don't implement ISwiftObject
            await Task.CompletedTask;
        }

        [Fact(Skip = "Protocol witness table not generated for Collection constraint")]
        public async Task TestGenericCollectionConstraint()
        {
            // Test disabled - generated code missing protocol witness table variable
            await Task.CompletedTask;
        }

        [Fact]
        public async Task TestArray()
        {
            var myStruct = new Bindings.AsyncStruct(0);
            var input = new string[] { "one", "two", "three" };

            var stopwatch = Stopwatch.StartNew();
            IReadOnlyList<string> result = await myStruct.GetArrayPassThroughAsync(input);
            stopwatch.Stop();

            Assert.True(stopwatch.Elapsed.TotalSeconds >= 1);
            Assert.Equal(3, result.Count);
            Assert.Equal("one", result[0]);
            Assert.Equal("two", result[1]);
            Assert.Equal("three", result[2]);
        }

        [Fact]
        public async Task TestString()
        {
            var myStruct = new Bindings.AsyncStruct(0);
            string input = "test string";

            var stopwatch = Stopwatch.StartNew();
            string result = await myStruct.GetStringPassThroughAsync(input);
            stopwatch.Stop();

            Assert.True(stopwatch.Elapsed.TotalSeconds >= 1);
            Assert.Equal(input, result);
        }
    }
}
