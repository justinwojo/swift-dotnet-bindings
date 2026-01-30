// Copyright (c) Microsoft Corporation.
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


namespace BindingsGeneration.FunctionalTests
{
    public class StructTests : IClassFixture<StructTests.TestFixture>
    {
        private readonly TestFixture _fixture;

        public StructTests(TestFixture fixture)
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

        [Fact]
        public void TestFrozenStructCreation()
        {
            IntPtr x = 1;
            IntPtr y = 2;

            var frozen = new FrozenStruct(x, y);
            var gotX = frozen.GetX();
            var gotY = frozen.GetY();

            Assert.Equal(x, gotX);
            Assert.Equal(y, gotY);
        }

        [Fact]
        public void TestNonFrozenStructCreation()
        {
            IntPtr x = 1;
            IntPtr y = 2;

            var nonFrozen = new NonFrozenStruct(x, y);
            var gotX = nonFrozen.GetX();
            var gotY = nonFrozen.GetY();

            Assert.Equal(x, gotX);
            Assert.Equal(y, gotY);
        }

        [Fact]
        public void TestNonFrozenStructWithNonFrozenMemberCreation()
        {
            IntPtr frozenX = 1;
            IntPtr frozenY = 2;
            IntPtr nonFrozenX = 30;
            IntPtr nonFrozenY = 40;

            var frozen = new FrozenStruct(frozenX, frozenY);
            var nonFrozen = new NonFrozenStruct(nonFrozenX, nonFrozenY);

            var complexStruct = new NonFrozenStructWithNonFrozenMember(frozen, nonFrozen);
            var gotF = complexStruct.GetX();
            var gotNF = complexStruct.GetY();

            Assert.Equal(frozenX, gotF.GetX());
            Assert.Equal(frozenY, gotF.GetY());
            Assert.Equal(nonFrozenX, gotNF.GetX());
            Assert.Equal(nonFrozenY, gotNF.GetY());
        }

        [Fact]
        public void TestFrozenStructWithNonFrozenMemberCreation()
        {
            IntPtr frozenX = 1;
            IntPtr frozenY = 2;
            IntPtr nonFrozenX = 30;
            IntPtr nonFrozenY = 40;

            var frozen = new FrozenStruct(frozenX, frozenY);
            var nonFrozen = new NonFrozenStruct(nonFrozenX, nonFrozenY);

            var complexStruct = new FrozenStructWithNonFrozenMember(frozen, nonFrozen);
            var gotF = complexStruct.GetX();
            var gotNF = complexStruct.GetY();

            Assert.Equal(frozenX, gotF.GetX());
            Assert.Equal(frozenY, gotF.GetY());
            Assert.Equal(nonFrozenX, gotNF.GetX());
            Assert.Equal(nonFrozenY, gotNF.GetY());
        }

        [Fact]
        public void TestFrozenStructWithNonFrozenMemberDeclaredWithinTheStruct()
        {
            IntPtr innerFieldValue = 123;

            var innerStruct = new FrozenStructWithNonFrozenMemberDeclaredWithinTheStruct.InnerStruct(innerFieldValue);
            var outerStruct = new FrozenStructWithNonFrozenMemberDeclaredWithinTheStruct(innerStruct);

            var gotInner = outerStruct.GetInnerFieldValue();

            Assert.Equal(innerFieldValue, gotInner);
        }

        [Fact]
        public void TestInstanceMethodOnFrozenStruct()
        {
            IntPtr x = 1;
            IntPtr y = 2;

            var frozen = new FrozenStruct(x, y);

            var result = frozen.Sum();

            Assert.Equal(1 + 2, result);
        }

        [Fact]
        public void TestInstanceMethodOnNonFrozenStruct()
        {
            IntPtr x = 1;
            IntPtr y = 2;

            var nonFrozen = new NonFrozenStruct(x, y);

            var result = nonFrozen.Sum();

            Assert.Equal(1 + 2, result);
        }

        [Fact]
        public void TestInstanceMethodOnNonFrozenStructWithNonFrozenMember()
        {
            IntPtr frozenX = 1;
            IntPtr frozenY = 2;
            IntPtr nonFrozenX = 30;
            IntPtr nonFrozenY = 40;

            var frozen = new FrozenStruct(frozenX, frozenY);
            var nonFrozen = new NonFrozenStruct(nonFrozenX, nonFrozenY);
            var complexStruct = new NonFrozenStructWithNonFrozenMember(frozen, nonFrozen);

            var result = complexStruct.Sum();

            Assert.Equal(1 + 2 + 30 + 40, result);
        }

        [Fact]
        public void TestInstanceMethodOnFrozenStructWithNonFrozenMember()
        {
            IntPtr frozenX = 1;
            IntPtr frozenY = 2;
            IntPtr nonFrozenX = 30;
            IntPtr nonFrozenY = 40;

            var frozen = new FrozenStruct(frozenX, frozenY);
            var nonFrozen = new NonFrozenStruct(nonFrozenX, nonFrozenY);
            var complexStruct = new FrozenStructWithNonFrozenMember(frozen, nonFrozen);

            var result = complexStruct.Sum();

            Assert.Equal(1 + 2 + 30 + 40, result);
        }

        [Fact]
        public void TestModuleFuncWithFrozenAndNonFrozenParameters()
        {
            IntPtr frozenX = 1;
            IntPtr frozenY = 2;
            IntPtr nonFrozenX = 30;
            IntPtr nonFrozenY = 40;

            var frozen = new FrozenStruct(frozenX, frozenY);
            var nonFrozen = new NonFrozenStruct(nonFrozenX, nonFrozenY);

            var result = StructsTests.SumFrozenAndNonFrozen(frozen, nonFrozen);

            Assert.Equal(1 + 2 + 30 + 40, result);
        }

        [Fact]
        public void TestModuleFuncReturningFrozenStruct()
        {
            IntPtr x = 1;
            IntPtr y = 2;

            var result = StructsTests.CreateFrozenStruct(x, y);

            Assert.Equal(x, result.GetX());
            Assert.Equal(y, result.GetY());
        }

        [Fact]
        public void TestModuleFuncReturningNonFrozenStruct()
        {
            IntPtr x = 1;
            IntPtr y = 2;

            var result = StructsTests.CreateNonFrozenStruct(x, y);

            Assert.Equal(x, result.GetX());
            Assert.Equal(y, result.GetY());
        }

        [Fact]
        public void TestInstanceMethodReturningFrozenStruct()
        {
            IntPtr x = 1;
            IntPtr y = 2;
            var structBuilder = new StructBuilder(x, y);

            var result = structBuilder.CreateFrozenStruct();

            Assert.Equal(x, result.GetX());
            Assert.Equal(y, result.GetY());
        }

        [Fact]
        public void TestInstanceMethodReturningNonFrozenStruct()
        {
            IntPtr x = 1;
            IntPtr y = 2;
            var structBuilder = new StructBuilder(x, y);

            var result = structBuilder.CreateNonFrozenStruct();

            Assert.Equal(x, result.GetX());
            Assert.Equal(y, result.GetY());
        }

        [Fact]
        public void TestStaticMethodReturningFrozenStruct()
        {
            IntPtr x = 1;
            IntPtr y = 2;

            var result = StructBuilder.CreateFrozenStruct(x, y);

            Assert.Equal(x, result.GetX());
            Assert.Equal(y, result.GetY());
        }

        [Fact]
        public void TestStaticMethodReturningNonFrozenStruct()
        {
            IntPtr x = 1;
            IntPtr y = 2;

            var result = StructBuilder.CreateNonFrozenStruct(x, y);

            Assert.Equal(x, result.GetX());
            Assert.Equal(y, result.GetY());
        }

        [Fact]
        public void TestInitMethodThrowingError()
        {
            Assert.Throws<SwiftRuntimeException>(() => new StructWithThrowingInit(0, 0));
        }

        [Fact]
        public void TestInstanceMethodThrowingError()
        {
            var structWithThrowingMethods = new StructWithThrowingMethods(0, 0);
            Assert.Throws<SwiftRuntimeException>(() => structWithThrowingMethods.Sum());
        }

        [Fact]
        public void TestStaticMethodThrowingError()
        {
            Assert.Throws<SwiftRuntimeException>(() => StructWithThrowingMethods.Sum(0, 0));
        }

        [Fact]
        public void TestFrozenStructProperties()
        {
            var staticPropertyValue = PropertiesTestStruct.StaticLetProperty;
            Assert.Equal(42, staticPropertyValue);

            var struct1 = new PropertiesTestStruct(letValue: 10, varValue: 20, multiplier: 3);

            Assert.Equal(10, struct1.LetProperty);

            Assert.Equal(20, struct1.VarProperty);

            Assert.Equal(30, struct1.ComputedProperty);
        }

        [Fact]
        public void TestNonFrozenStructProperties()
        {
            var staticPropertyValue = NonFrozenPropertiesTestStruct.StaticLetProperty;
            Assert.Equal(42, staticPropertyValue);

            var struct1 = new NonFrozenPropertiesTestStruct(letValue: 10, varValue: 20, multiplier: 3);

            Assert.Equal(10, struct1.LetProperty);

            Assert.Equal(20, struct1.VarProperty);

            Assert.Equal(30, struct1.ComputedProperty);
        }

        [Fact]
        public void TestFrozenEquatableStruct()
        {
            var struct1 = new FrozenEquatableStruct(10, 20);
            var struct2 = new FrozenEquatableStruct(10, 20);
            var struct3 = new FrozenEquatableStruct(30, 40);

            // Verify that two identical structs are equal
            Assert.Equal(struct1, struct2);

            // Verify that two different structs are not equal
            Assert.NotEqual(struct1, struct3);
        }

        [Fact]
        public void TestNonFrozenEquatableStruct()
        {
            var struct1 = new NonFrozenEquatableStruct(10, 20);
            var struct2 = new NonFrozenEquatableStruct(10, 20);
            var struct3 = new NonFrozenEquatableStruct(30, 40);

            // Verify that two identical structs are equal
            Assert.Equal(struct1, struct2);

            // Verify that two different structs are not equal
            Assert.NotEqual(struct1, struct3);
        }

        [Fact]
        public void TestCustomEquatableStruct()
        {
            var struct1 = new CustomEquatableStruct(10);
            var struct2 = new CustomEquatableStruct(13);
            var struct3 = new CustomEquatableStruct(30);

            // Verify that two structures with absolute difference less than 5 are equal
            Assert.Equal(struct1, struct2);

            // Verify that two structures with absolute difference greater than 5 are not equal
            Assert.NotEqual(struct1, struct3);
        }
    }
}
