// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Swift.ProtocolsTests;
using Xunit;

// Alias the Swift module class to avoid collision with test class name
using SwiftProtocols = Swift.ProtocolsTests.ProtocolsTests;

namespace BindingsGeneration.FunctionalTests
{
    /// <summary>
    /// Runtime tests for protocol dispatch, conformance, and proxy lifecycle.
    /// These tests exercise actual Swift-to-C# interop behavior, not just compilation.
    /// </summary>
    public class ProtocolsTests : IClassFixture<ProtocolsTests.TestFixture>
    {
        private readonly TestFixture _fixture;

        public ProtocolsTests(TestFixture fixture)
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

        #region Compile Checks - Protocol Interfaces Exist

        [Fact]
        public void ProtocolIsProjected()
        {
            // Verify protocol interfaces exist
            Assert.True(typeof(ISwiftPrintable).IsInterface);
            Assert.True(typeof(ISwiftHasInt32Value).IsInterface);
            Assert.True(typeof(ISwiftComputable).IsInterface);
            Assert.True(typeof(ISwiftCounter).IsInterface);
            Assert.True(typeof(ISwiftResettableCounter).IsInterface);
        }

        [Fact]
        public void ConformingTypesImplementInterfaces()
        {
            // Verify concrete types implement protocol interfaces
            Assert.True(typeof(ISwiftHasInt32Value).IsAssignableFrom(typeof(IntHolder)));
            Assert.True(typeof(ISwiftComputable).IsAssignableFrom(typeof(Doubler)));
            Assert.True(typeof(ISwiftCounter).IsAssignableFrom(typeof(SimpleCounter)));
            Assert.True(typeof(ISwiftResettableCounter).IsAssignableFrom(typeof(AdvancedCounter)));
        }

        [Fact]
        public void MultiConformerImplementsAllInterfaces()
        {
            // Verify multi-conforming type implements all three interfaces
            Assert.True(typeof(ISwiftHasInt32Value).IsAssignableFrom(typeof(MultiConformer)));
            Assert.True(typeof(ISwiftComputable).IsAssignableFrom(typeof(MultiConformer)));
            Assert.True(typeof(ISwiftCounter).IsAssignableFrom(typeof(MultiConformer)));
        }

        [Fact]
        public void InheritedProtocolIncludesBaseInterface()
        {
            // AdvancedCounter implements both Counter and ResettableCounter protocols
            Assert.True(typeof(ISwiftCounter).IsAssignableFrom(typeof(AdvancedCounter)));
            Assert.True(typeof(ISwiftResettableCounter).IsAssignableFrom(typeof(AdvancedCounter)));
        }

        #endregion

        #region Runtime Behavior - Swift Types via Factory Functions

        [Fact]
        public void IntHolder_CreatedViaFactory_ReturnsCorrectValue()
        {
            // Create Swift type via factory function
            var holder = SwiftProtocols.CreateIntHolder(42);

            // Access property directly on concrete type
            Assert.Equal(42, holder.IntValue);
        }

        [Fact]
        public void Doubler_Compute_ReturnsMultipliedValue()
        {
            // Create Swift type via factory function
            var doubler = SwiftProtocols.CreateDoubler(3);

            // Call method directly on concrete type
            int result = doubler.Compute(7);

            Assert.Equal(21, result); // 7 * 3 = 21
        }

        [Fact]
        public void SimpleCounter_Increment_ReturnsCorrectSum()
        {
            // Create Swift type via factory function
            var counter = SwiftProtocols.CreateSimpleCounter(10);

            // Verify property
            Assert.Equal(10, counter.Count);

            // Verify method
            int result = counter.Increment(5);
            Assert.Equal(15, result); // 10 + 5 = 15
        }

        [Fact]
        public void AdvancedCounter_InheritedProtocol_WorksCorrectly()
        {
            // Create Swift type conforming to inherited protocol
            var counter = SwiftProtocols.CreateAdvancedCounter(20);

            // Verify base protocol method (from Counter)
            Assert.Equal(20, counter.Count);
            Assert.Equal(25, counter.Increment(5)); // 20 + 5 = 25

            // Verify derived protocol method (from ResettableCounter)
            Assert.Equal(0, counter.Reset());
        }

        [Fact]
        public void MultiConformer_AllProtocolMethods_WorkCorrectly()
        {
            // Create Swift type conforming to multiple protocols
            var multi = SwiftProtocols.CreateMultiConformer(100, 50);

            // Test ISwiftHasInt32Value
            Assert.Equal(100, multi.IntValue);

            // Test ISwiftComputable
            Assert.Equal(110, multi.Compute(10)); // 100 + 10 = 110

            // Test ISwiftCounter
            Assert.Equal(50, multi.Count);
            Assert.Equal(55, multi.Increment(5)); // 50 + 5 = 55
        }

        #endregion

        #region Runtime Behavior - Swift Types via Constructors

        [Fact]
        public void IntHolder_CreatedViaConstructor_ReturnsCorrectValue()
        {
            // Create Swift type via constructor
            var holder = new IntHolder(99);

            // Access property directly on concrete type
            Assert.Equal(99, holder.IntValue);
        }

        [Fact]
        public void Doubler_CreatedViaConstructor_ComputesCorrectly()
        {
            // Create Swift type via constructor
            var doubler = new Doubler(4);

            // Call method
            int result = doubler.Compute(6);

            Assert.Equal(24, result); // 6 * 4 = 24
        }

        [Fact]
        public void SimpleCounter_CreatedViaConstructor_WorksCorrectly()
        {
            // Create Swift type via constructor
            var counter = new SimpleCounter(25);

            // Verify property and method
            Assert.Equal(25, counter.Count);
            Assert.Equal(35, counter.Increment(10)); // 25 + 10 = 35
        }

        #endregion

        #region Runtime Behavior - Interface Casting and Method Dispatch

        [Fact]
        public void IntHolder_CastToInterface_CanAccessProperty()
        {
            // Create Swift type
            var holder = new IntHolder(77);

            // Cast to interface
            ISwiftHasInt32Value asInterface = holder;

            // Access property via interface
            Assert.Equal(77, asInterface.IntValue);
        }

        [Fact]
        public void Doubler_CastToInterface_CanCallMethod()
        {
            // Create Swift type
            var doubler = new Doubler(5);

            // Cast to interface
            ISwiftComputable asInterface = doubler;

            // Call method via interface
            int result = asInterface.Compute(9);
            Assert.Equal(45, result); // 9 * 5 = 45
        }

        [Fact]
        public void SimpleCounter_CastToInterface_PropertyAndMethodWork()
        {
            // Create Swift type
            var counter = new SimpleCounter(100);

            // Cast to interface
            ISwiftCounter asInterface = counter;

            // Access property and call method via interface
            Assert.Equal(100, asInterface.Count);
            Assert.Equal(150, asInterface.Increment(50)); // 100 + 50 = 150
        }

        [Fact]
        public void MultiConformer_CastToEachInterface_AllWork()
        {
            // Create multi-conforming Swift type
            var multi = new MultiConformer(300, 400);

            // Cast to each interface and verify behavior
            ISwiftHasInt32Value asHasValue = multi;
            Assert.Equal(300, asHasValue.IntValue);

            ISwiftComputable asComputable = multi;
            Assert.Equal(350, asComputable.Compute(50)); // 300 + 50 = 350

            ISwiftCounter asCounter = multi;
            Assert.Equal(400, asCounter.Count);
            Assert.Equal(410, asCounter.Increment(10)); // 400 + 10 = 410
        }

        [Fact]
        public void AdvancedCounter_CastToCounterInterface_Works()
        {
            // Create type conforming to derived protocol
            var advanced = new AdvancedCounter(500);

            // Cast to base interface (Counter)
            ISwiftCounter asCounter = advanced;
            Assert.Equal(500, asCounter.Count);
            Assert.Equal(520, asCounter.Increment(20));
        }

        [Fact]
        public void AdvancedCounter_CastToResettableInterface_Works()
        {
            // Create type conforming to derived protocol
            var advanced = new AdvancedCounter(500);

            // Cast to derived interface (ResettableCounter)
            ISwiftResettableCounter asResettable = advanced;
            Assert.Equal(0, asResettable.Reset());
        }

        #endregion

        #region Runtime Behavior - Generic Method with Interface Constraint

        [Fact]
        public void GenericMethodWithInterfaceConstraint_WorksWithConformingTypes()
        {
            // This tests that the type system allows generic methods constrained by protocol interfaces
            int result = GetValueFromHolder<IntHolder>(new IntHolder(123));
            Assert.Equal(123, result);
        }

        private static int GetValueFromHolder<T>(T holder) where T : ISwiftHasInt32Value
        {
            return holder.IntValue;
        }

        [Fact]
        public void GenericMethodWithCounterConstraint_WorksWithDifferentTypes()
        {
            // Test with SimpleCounter
            var simple = new SimpleCounter(10);
            int result1 = IncrementCounter<SimpleCounter>(simple, 5);
            Assert.Equal(15, result1);

            // Test with AdvancedCounter
            var advanced = new AdvancedCounter(20);
            int result2 = IncrementCounter<AdvancedCounter>(advanced, 7);
            Assert.Equal(27, result2);

            // Test with MultiConformer
            var multi = new MultiConformer(0, 30);
            int result3 = IncrementCounter<MultiConformer>(multi, 12);
            Assert.Equal(42, result3);
        }

        private static int IncrementCounter<T>(T counter, int by) where T : ISwiftCounter
        {
            return counter.Increment(by);
        }

        #endregion
    }
}
