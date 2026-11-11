// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests
{
    public class ThunkAssemblyEmitterTests
    {
        #region File Header/Footer

        [Fact]
        public void EmitFileHeader_ContainsSectionDirectives()
        {
            var header = ThunkAssemblyEmitter.EmitFileHeader("TestModule");

            Assert.Contains(".text", header);
            Assert.Contains(".align 4", header);
            Assert.Contains("TestModule", header);
        }

        [Fact]
        public void EmitFileFooter_ReturnsEmpty()
        {
            var footer = ThunkAssemblyEmitter.EmitFileFooter();
            Assert.Equal(string.Empty, footer);
        }

        #endregion

        #region Tail Call (Trivial Forward)

        [Fact]
        public void EmitThunk_TrivialForward_EmitsTailCall()
        {
            // Free function, return ≤ 16 bytes, no self, no throws → tail call (b instruction)
            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "thunk_test_simple",
                SwiftSymbol: "_$s4Test6simpleyyF",
                ReturnLowering: null, // void
                SelfLowering: null,
                ParameterCount: 0,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: false,
                Throws: false,
                MetadataAccessorSymbol: null);

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            Assert.Contains(".globl _thunk_test_simple", asm);
            Assert.Contains("_thunk_test_simple:", asm);
            Assert.Contains("b       _$s4Test6simpleyyF", asm);
            // Tail call should NOT have ret, stp, ldp
            Assert.DoesNotContain("ret", asm);
            Assert.DoesNotContain("stp", asm);
        }

        [Fact]
        public void EmitThunk_ScalarReturn_EmitsTailCall()
        {
            // Free function returning Int (≤ 16 bytes, single register) → tail call
            var returnLowering = new TypeLoweringResult(
                new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                IsIndirect: false,
                TotalByteSize: 8);

            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "thunk_test_getInt",
                SwiftSymbol: "_$s4Test6getIntSiyF",
                ReturnLowering: returnLowering,
                SelfLowering: null,
                ParameterCount: 0,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: false,
                Throws: false,
                MetadataAccessorSymbol: null);

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            // ≤ 16 bytes return, no self/throws → tail call
            Assert.Contains("b       _$s4Test6getIntSiyF", asm);
            Assert.DoesNotContain("ret", asm);
        }

        [Fact]
        public void EmitThunk_PointReturn_16Bytes_EmitsTailCall()
        {
            // Point { x: Int, y: Int } = 16 bytes → cdecl returns in x0+x1, same as swiftcc → tail call
            var returnLowering = new TypeLoweringResult(
                new[] {
                    new RegisterSlot(RegisterFile.Integer, 0, 8),
                    new RegisterSlot(RegisterFile.Integer, 1, 8)
                },
                IsIndirect: false,
                TotalByteSize: 16);

            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "thunk_test_makePoint",
                SwiftSymbol: "_$s4Test9makePointAA0D0VyF",
                ReturnLowering: returnLowering,
                SelfLowering: null,
                ParameterCount: 2,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: false,
                Throws: false,
                MetadataAccessorSymbol: null);

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            // 16 bytes ≤ 16 limit → tail call
            Assert.Contains("b       ", asm);
            Assert.DoesNotContain("mov     x19, x8", asm);
        }

        #endregion

        #region Struct Return Bridge (17-32 bytes)

        [Fact]
        public void EmitThunk_RectReturn_32Bytes_BridgesViaX8()
        {
            // Rect { x, y, w, h: Int } = 32 bytes → Swift uses x0-x3, cdecl uses x8
            var returnLowering = new TypeLoweringResult(
                new[] {
                    new RegisterSlot(RegisterFile.Integer, 0, 8),
                    new RegisterSlot(RegisterFile.Integer, 1, 8),
                    new RegisterSlot(RegisterFile.Integer, 2, 8),
                    new RegisterSlot(RegisterFile.Integer, 3, 8)
                },
                IsIndirect: false,
                TotalByteSize: 32);

            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "thunk_test_makeRect",
                SwiftSymbol: "_$s4Test8makeRectAA0D0VyF",
                ReturnLowering: returnLowering,
                SelfLowering: null,
                ParameterCount: 4,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: false,
                Throws: false,
                MetadataAccessorSymbol: null);

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            // Must save x8 (return buffer) to x19
            Assert.Contains("mov     x19, x8", asm);
            // Must call, not tail-call
            Assert.Contains("bl      _$s4Test8makeRectAA0D0VyF", asm);
            // Must store return registers to buffer
            Assert.Contains("str     x0, [x19]", asm);
            Assert.Contains("str     x1, [x19, #8]", asm);
            Assert.Contains("str     x2, [x19, #16]", asm);
            Assert.Contains("str     x3, [x19, #24]", asm);
            // Must have prologue/epilogue
            Assert.Contains("stp     x20, x19, [sp, #-32]!", asm);
            Assert.Contains("ldp     x20, x19, [sp], #32", asm);
            Assert.Contains("ret", asm);
        }

        [Fact]
        public void EmitThunk_Vec3Return_24Bytes_BridgesViaX8()
        {
            // Vec3 { x, y, z: Int } = 24 bytes → Swift uses x0-x2, cdecl uses x8
            var returnLowering = new TypeLoweringResult(
                new[] {
                    new RegisterSlot(RegisterFile.Integer, 0, 8),
                    new RegisterSlot(RegisterFile.Integer, 1, 8),
                    new RegisterSlot(RegisterFile.Integer, 2, 8)
                },
                IsIndirect: false,
                TotalByteSize: 24);

            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "thunk_test_makeVec3",
                SwiftSymbol: "_$s4Test8makeVec3AA0D0VyF",
                ReturnLowering: returnLowering,
                SelfLowering: null,
                ParameterCount: 3,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: false,
                Throws: false,
                MetadataAccessorSymbol: null);

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            Assert.Contains("mov     x19, x8", asm);
            Assert.Contains("bl      ", asm);
            Assert.Contains("str     x0, [x19]", asm);
            Assert.Contains("str     x1, [x19, #8]", asm);
            Assert.Contains("str     x2, [x19, #16]", asm);
        }

        [Fact]
        public void EmitThunk_MixedIntFloat_InterleavedStores()
        {
            // MixedIntFloat { intVal: Int, floatVal: Double, intVal2: Int, floatVal2: Double }
            // Swift returns: x0=intVal, d0=floatVal, x1=intVal2, d1=floatVal2
            var returnLowering = new TypeLoweringResult(
                new[] {
                    new RegisterSlot(RegisterFile.Integer, 0, 8),
                    new RegisterSlot(RegisterFile.Float, 0, 8),
                    new RegisterSlot(RegisterFile.Integer, 1, 8),
                    new RegisterSlot(RegisterFile.Float, 1, 8)
                },
                IsIndirect: false,
                TotalByteSize: 32);

            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "thunk_test_makeMixed",
                SwiftSymbol: "_$s4Test9makeMixedAA0D0VyF",
                ReturnLowering: returnLowering,
                SelfLowering: null,
                ParameterCount: 4,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: false,
                Throws: false,
                MetadataAccessorSymbol: null);

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            // Verify interleaved int/float stores at correct offsets
            Assert.Contains("str     x0, [x19]", asm);        // intVal at offset 0
            Assert.Contains("str     d0, [x19, #8]", asm);    // floatVal at offset 8
            Assert.Contains("str     x1, [x19, #16]", asm);   // intVal2 at offset 16
            Assert.Contains("str     d1, [x19, #24]", asm);   // floatVal2 at offset 24
        }

        [Fact]
        public void EmitThunk_MixedWidthStruct_NaturalOffsetsAndWidths()
        {
            // Mixed { i: Int32, f: Float, j: Int64, d: Double } (24 bytes, natural offsets 0,4,8,16).
            // Swift returns field-wise: w0=Int32, s0=Float, x1=Int64, d1=Double. The store must use
            // width-matched instructions at the packed offsets — not an 8-byte stride.
            var returnLowering = new TypeLoweringResult(
                new[] {
                    new RegisterSlot(RegisterFile.Integer, 0, 4),
                    new RegisterSlot(RegisterFile.Float, 0, 4),
                    new RegisterSlot(RegisterFile.Integer, 1, 8),
                    new RegisterSlot(RegisterFile.Float, 1, 8)
                },
                IsIndirect: false,
                TotalByteSize: 24);

            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "thunk_test_makeMixedWidth",
                SwiftSymbol: "_$s4Test14makeMixedWidthAA0E0VyF",
                ReturnLowering: returnLowering,
                SelfLowering: null,
                ParameterCount: 0,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: false,
                Throws: false,
                MetadataAccessorSymbol: null);

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            Assert.Contains("str     w0, [x19]", asm);       // Int32 — 32-bit store at offset 0
            Assert.Contains("str     s0, [x19, #4]", asm);   // Float — 32-bit float store at offset 4
            Assert.Contains("str     x1, [x19, #8]", asm);   // Int64 — 64-bit store at offset 8
            Assert.Contains("str     d1, [x19, #16]", asm);  // Double — 64-bit float store at offset 16
            // The buggy 8-byte stride would have written the Float/Int64 at #8/#16 with full-width str.
            Assert.DoesNotContain("str     d0, [x19, #8]", asm);
        }

        [Fact]
        public void EmitThunk_NarrowIntegerFields_ByteAndHalfStores()
        {
            // Packed integer fields of 1/2/4/8 bytes at natural offsets 0,2,4,8.
            var returnLowering = new TypeLoweringResult(
                new[] {
                    new RegisterSlot(RegisterFile.Integer, 0, 1),
                    new RegisterSlot(RegisterFile.Integer, 1, 2),
                    new RegisterSlot(RegisterFile.Integer, 2, 4),
                    new RegisterSlot(RegisterFile.Integer, 3, 8)
                },
                IsIndirect: false,
                TotalByteSize: 24);

            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "thunk_test_makeNarrow",
                SwiftSymbol: "_$s4Test10makeNarrowAA0D0VyF",
                ReturnLowering: returnLowering,
                SelfLowering: null,
                ParameterCount: 0,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: false,
                Throws: false,
                MetadataAccessorSymbol: null);

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            Assert.Contains("strb    w0, [x19]", asm);      // 1 byte at offset 0
            Assert.Contains("strh    w1, [x19, #2]", asm);  // 2 bytes at offset 2
            Assert.Contains("str     w2, [x19, #4]", asm);  // 4 bytes at offset 4
            Assert.Contains("str     x3, [x19, #8]", asm);  // 8 bytes at offset 8
        }

        [Fact]
        public void EmitThunk_IndirectReturn_NoBridge()
        {
            // Struct > 32 bytes → indirect via x8 in BOTH conventions → no bridge needed → tail call
            var returnLowering = new TypeLoweringResult(
                new[] {
                    new RegisterSlot(RegisterFile.Integer, 0, 8),
                    new RegisterSlot(RegisterFile.Integer, 1, 8),
                    new RegisterSlot(RegisterFile.Integer, 2, 8),
                    new RegisterSlot(RegisterFile.Integer, 3, 8),
                    new RegisterSlot(RegisterFile.Integer, 4, 8)
                },
                IsIndirect: true,
                TotalByteSize: 40);

            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "thunk_test_makeBig",
                SwiftSymbol: "_$s4Test7makeBigAA0D0VyF",
                ReturnLowering: returnLowering,
                SelfLowering: null,
                ParameterCount: 0,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: false,
                Throws: false,
                MetadataAccessorSymbol: null);

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            // Indirect return: both conventions use x8 → tail call
            Assert.Contains("b       _$s4Test7makeBigAA0D0VyF", asm);
            Assert.DoesNotContain("mov     x19, x8", asm);
        }

        #endregion

        #region Instance Method (self in x20)

        [Fact]
        public void EmitThunk_InstanceMethod_MovesSelfToX20()
        {
            // Instance method with 1 param: cdecl order is [value, self]
            // CdeclSignatureContract: [Arguments] [Self] → x0=value, x1=self
            // Thunk must: mov x20, x1 (self at x{ParameterCount}), value stays in x0
            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "thunk_test_counter_add",
                SwiftSymbol: "_$s4Test7CounterC3addyS2iF",
                ReturnLowering: new TypeLoweringResult(
                    new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                    IsIndirect: false,
                    TotalByteSize: 8),
                SelfLowering: new TypeLoweringResult(
                    new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                    IsIndirect: false,
                    TotalByteSize: 8),
                ParameterCount: 1,
                FloatParameterCount: 0,
                IsInstanceMethod: true,
                IsStaticMethod: false,
                IsConstructor: false,
                Throws: false,
                MetadataAccessorSymbol: null);

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            // Must move self from x1 (last param) to x20
            Assert.Contains("mov     x20, x1", asm);
            // Value param stays in x0 — no parameter shift needed
            Assert.DoesNotContain("mov     x0, x1", asm);
            // Must use bl (not tail call, since we modify x20)
            Assert.Contains("bl      _$s4Test7CounterC3addyS2iF", asm);
            Assert.Contains("ret", asm);
        }

        [Fact]
        public void EmitThunk_InstanceMethodMultipleParams_SelfAtLastPosition()
        {
            // Instance method with 3 params: cdecl order is [p0, p1, p2, self]
            // x0=p0, x1=p1, x2=p2, x3=self → self at x{ParameterCount}=x3
            // No parameter shift — params already in x0-x2 for swiftcc
            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "thunk_test_method3",
                SwiftSymbol: "_$s4Test3FooCmethod3yyF",
                ReturnLowering: null,
                SelfLowering: new TypeLoweringResult(
                    new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                    IsIndirect: false,
                    TotalByteSize: 8),
                ParameterCount: 3,
                FloatParameterCount: 0,
                IsInstanceMethod: true,
                IsStaticMethod: false,
                IsConstructor: false,
                Throws: false,
                MetadataAccessorSymbol: null);

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            // Self at x3 (x{ParameterCount}=x{3}) → x20
            Assert.Contains("mov     x20, x3", asm);
            // No parameter shifting — values stay in x0, x1, x2
            Assert.DoesNotContain("mov     x0, x1", asm);
            Assert.DoesNotContain("mov     x1, x2", asm);
            Assert.DoesNotContain("mov     x2, x3", asm);
        }

        [Fact]
        public void EmitThunk_InstanceMethodWithStructReturn_BothBridges()
        {
            // Instance method returning a 24-byte struct: needs BOTH self bridge AND return bridge
            var returnLowering = new TypeLoweringResult(
                new[] {
                    new RegisterSlot(RegisterFile.Integer, 0, 8),
                    new RegisterSlot(RegisterFile.Integer, 1, 8),
                    new RegisterSlot(RegisterFile.Integer, 2, 8)
                },
                IsIndirect: false,
                TotalByteSize: 24);

            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "thunk_test_getVec",
                SwiftSymbol: "_$s4Test3FooC6getVecAA4Vec3VyF",
                ReturnLowering: returnLowering,
                SelfLowering: new TypeLoweringResult(
                    new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                    IsIndirect: false,
                    TotalByteSize: 8),
                ParameterCount: 0,
                FloatParameterCount: 0,
                IsInstanceMethod: true,
                IsStaticMethod: false,
                IsConstructor: false,
                Throws: false,
                MetadataAccessorSymbol: null);

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            // Both bridges present
            Assert.Contains("mov     x19, x8", asm);    // return buffer save
            Assert.Contains("mov     x20, x0", asm);    // self → x20
            Assert.Contains("str     x0, [x19]", asm);  // return store
        }

        [Fact]
        public void EmitThunk_InstanceMethodZeroParams_SelfAtX0()
        {
            // Instance method with 0 params (e.g., getter): self is x0
            // cdecl: x0=self → thunk: mov x20, x0
            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "thunk_test_getter",
                SwiftSymbol: "_$s4Test3FooC8getValueSiyF",
                ReturnLowering: new TypeLoweringResult(
                    new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                    IsIndirect: false,
                    TotalByteSize: 8),
                SelfLowering: new TypeLoweringResult(
                    new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                    IsIndirect: false,
                    TotalByteSize: 8),
                ParameterCount: 0,
                FloatParameterCount: 0,
                IsInstanceMethod: true,
                IsStaticMethod: false,
                IsConstructor: false,
                Throws: false,
                MetadataAccessorSymbol: null);

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            // Self at x0 (x{ParameterCount=0}) → x20
            Assert.Contains("mov     x20, x0", asm);
            Assert.Contains("bl      _$s4Test3FooC8getValueSiyF", asm);
        }

        [Fact]
        public void EmitThunk_PropertySetter_ValueInX0SelfInX1()
        {
            // Property setter: cdecl order [newValue, self]
            // x0=newValue (Int), x1=self → thunk: mov x20, x1, x0 stays
            // This is the exact pattern that previously caused SIGSEGV (test #30)
            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "thunk_test_setIntValue",
                SwiftSymbol: "_$s4Test19FinalPropertyHolderC8intValueSivs",
                ReturnLowering: null, // setter returns void
                SelfLowering: new TypeLoweringResult(
                    new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                    IsIndirect: false,
                    TotalByteSize: 8),
                ParameterCount: 1, // the newValue
                FloatParameterCount: 0,
                IsInstanceMethod: true,
                IsStaticMethod: false,
                IsConstructor: false,
                Throws: false,
                MetadataAccessorSymbol: null);

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            // Self at x1 (after 1 value param) → x20
            Assert.Contains("mov     x20, x1", asm);
            // Value (x0) must NOT be moved — it's already correct for swiftcc
            Assert.DoesNotContain("mov     x20, x0", asm);
            Assert.DoesNotContain("mov     x0, x1", asm);
            Assert.Contains("bl      _$s4Test19FinalPropertyHolderC8intValueSivs", asm);
            Assert.Contains("ret", asm);
        }

        [Fact]
        public void EmitThunk_PropertySetterWithFloatValue_SelfStillInIntReg()
        {
            // Setter for a Double property: newValue in d0, self in x0
            // Float params go in d-registers, so self is at x{intParamCount=0}=x0
            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "thunk_test_setDoubleValue",
                SwiftSymbol: "_$s4Test19FinalPropertyHolderC11doubleValueSdvs",
                ReturnLowering: null,
                SelfLowering: new TypeLoweringResult(
                    new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                    IsIndirect: false,
                    TotalByteSize: 8),
                ParameterCount: 0, // float param counted separately
                FloatParameterCount: 1, // the newValue (Double)
                IsInstanceMethod: true,
                IsStaticMethod: false,
                IsConstructor: false,
                Throws: false,
                MetadataAccessorSymbol: null);

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            // Self at x0 (0 int params, float params don't occupy x-registers)
            Assert.Contains("mov     x20, x0", asm);
            Assert.Contains("bl      _$s4Test19FinalPropertyHolderC11doubleValueSdvs", asm);
        }

        #endregion

        #region Constructor (metatype in x20)

        [Fact]
        public void EmitThunk_Constructor_CallsMetadataAccessor()
        {
            // Constructor: calls metadata accessor → x0, moves to x20, then calls init
            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "thunk_test_Foo_init",
                SwiftSymbol: "_$s4Test3FooCACycfC",
                ReturnLowering: new TypeLoweringResult(
                    new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                    IsIndirect: false,
                    TotalByteSize: 8),
                SelfLowering: null,
                ParameterCount: 0,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: true,
                Throws: false,
                MetadataAccessorSymbol: "_$s4Test3FooCMa");

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            // Must call metadata accessor
            Assert.Contains("bl      _$s4Test3FooCMa", asm);
            // Must move metatype to x20
            Assert.Contains("mov     x20, x0", asm);
            // Request = 0 (complete metadata)
            Assert.Contains("mov     x0, #0", asm);
            // Must call the allocating init
            Assert.Contains("bl      _$s4Test3FooCACycfC", asm);
        }

        [Fact]
        public void EmitThunk_ConstructorWithParams_SavesAndRestoresParams()
        {
            // Constructor with params: must save params before metadata accessor call,
            // then restore them before the init call
            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "thunk_test_Foo_initVal",
                SwiftSymbol: "_$s4Test3FooC5valueSi_tcfC",
                ReturnLowering: new TypeLoweringResult(
                    new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                    IsIndirect: false,
                    TotalByteSize: 8),
                SelfLowering: null,
                ParameterCount: 1,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: true,
                Throws: false,
                MetadataAccessorSymbol: "_$s4Test3FooCMa");

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            // Must save parameter to stack before metadata accessor
            Assert.Contains("str     x0, [sp, #0]", asm);
            // Must call metadata accessor
            Assert.Contains("bl      _$s4Test3FooCMa", asm);
            // Must restore parameter from stack
            Assert.Contains("ldr     x0, [sp, #0]", asm);
        }

        #endregion

        #region Static Method (metatype in x20)

        [Fact]
        public void EmitThunk_StaticMethod_CallsMetadataAccessor()
        {
            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "thunk_test_Foo_create",
                SwiftSymbol: "_$s4Test3FooC6createACyFZ",
                ReturnLowering: new TypeLoweringResult(
                    new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                    IsIndirect: false,
                    TotalByteSize: 8),
                SelfLowering: null,
                ParameterCount: 0,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: true,
                IsConstructor: false,
                Throws: false,
                MetadataAccessorSymbol: "_$s4Test3FooCMa");

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            Assert.Contains("bl      _$s4Test3FooCMa", asm);
            Assert.Contains("mov     x20, x0", asm);
            Assert.Contains("bl      _$s4Test3FooC6createACyFZ", asm);
        }

        #endregion

        #region Throwing Functions (swifterror in x21)

        [Fact]
        public void EmitThunk_ThrowingFunction_ClearsAndCapturesX21()
        {
            // Throwing free function: error out pointer is the last cdecl parameter
            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "thunk_test_divide",
                SwiftSymbol: "_$s4Test6divideyS2i_SitKF",
                ReturnLowering: new TypeLoweringResult(
                    new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                    IsIndirect: false,
                    TotalByteSize: 8),
                SelfLowering: null,
                ParameterCount: 2,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: false,
                Throws: true,
                MetadataAccessorSymbol: null);

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            // Must clear swifterror on entry
            Assert.Contains("mov     x21, xzr", asm);
            // Must save error out pointer (x2 = last param after 2 regular params)
            Assert.Contains("mov     x19, x2", asm);
            // Must capture swifterror after call
            Assert.Contains("str     x21, [x19]", asm);
            Assert.Contains("bl      _$s4Test6divideyS2i_SitKF", asm);
            // x21 is callee-saved in AAPCS64 — must save/restore
            Assert.Contains("str     x21, [sp, #32]", asm);
            Assert.Contains("ldr     x21, [sp, #32]", asm);
            // Throwing frame is 48 bytes (non-throwing is 32)
            Assert.Contains("[sp, #-48]!", asm);
            Assert.Contains("[sp], #48", asm);
        }

        [Fact]
        public void EmitThunk_ThrowingInstanceMethod_ErrorAfterSelf()
        {
            // Instance method that throws: cdecl order [param0, self, error_out]
            // x0=param, x1=self, x2=error_out
            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "thunk_test_throwing_method",
                SwiftSymbol: "_$s4Test3FooC6methodySiSiKF",
                ReturnLowering: new TypeLoweringResult(
                    new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                    IsIndirect: false,
                    TotalByteSize: 8),
                SelfLowering: new TypeLoweringResult(
                    new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                    IsIndirect: false,
                    TotalByteSize: 8),
                ParameterCount: 1,
                FloatParameterCount: 0,
                IsInstanceMethod: true,
                IsStaticMethod: false,
                IsConstructor: false,
                Throws: true,
                MetadataAccessorSymbol: null);

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            // Self at x1 (x{ParameterCount=1}) → x20
            Assert.Contains("mov     x20, x1", asm);
            Assert.Contains("mov     x21, xzr", asm);
            // Error out at x2 (baseIndex=1 + ParameterCount=1 = 2)
            Assert.Contains("mov     x19, x2", asm);
            Assert.Contains("str     x21, [x19]", asm);
            // x21 save/restore
            Assert.Contains("str     x21, [sp, #32]", asm);
            Assert.Contains("ldr     x21, [sp, #32]", asm);
        }

        [Fact]
        public void EmitThunk_ThrowingWithStructReturn_BothBridges()
        {
            // Throwing function with struct return > 16 bytes: needs both return bridge AND error bridge
            var returnLowering = new TypeLoweringResult(
                new[] {
                    new RegisterSlot(RegisterFile.Integer, 0, 8),
                    new RegisterSlot(RegisterFile.Integer, 1, 8),
                    new RegisterSlot(RegisterFile.Integer, 2, 8)
                },
                IsIndirect: false,
                TotalByteSize: 24);

            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "thunk_test_throwingReturnStruct",
                SwiftSymbol: "_$s4Test9getVec3OryAA0D0VyKF",
                ReturnLowering: returnLowering,
                SelfLowering: null,
                ParameterCount: 0,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: false,
                Throws: true,
                MetadataAccessorSymbol: null);

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            // Must save x8 return buffer to x19
            Assert.Contains("mov     x19, x8", asm);
            // Must clear swifterror
            Assert.Contains("mov     x21, xzr", asm);
            // Must store return values to buffer
            Assert.Contains("str     x0, [x19]", asm);
            // Error out pointer saved on stack (since x19 is used for return buffer)
            // After call, error stored via stack-restored pointer
            Assert.Contains("str     x21, [x9]", asm);
            // x21 save/restore
            Assert.Contains("str     x21, [sp, #32]", asm);
            Assert.Contains("ldr     x21, [sp, #32]", asm);
        }

        #endregion

        #region Throwing class constructor — error-out leads (SysV)

        [Fact]
        public void EmitThunk_X86_64_ThrowingClassConstructor_ErrorLeadsAndShiftsArgs()
        {
            // SysV mirror of the ARM64 env test: a throwing class constructor is the one
            // thunked shape whose error-out pointer LEADS the value arguments. CdeclSignatureContract
            // orders it [ErrorOut][Arguments][Metadata], so the error-out lands in %rdi and the two
            // value args occupy %rsi/%rdx on the cdecl side. The thunk must capture error-out from the
            // LEADING %rdi (not a trailing register) and shift the value args back down to %rdi/%rsi for
            // swiftcc. The earlier bug stashed error-out from a trailing register and emitted no shift,
            // so the init read the error pointer as an argument and swifterror was never captured.
            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "thunk_test_MyClass_init",
                SwiftSymbol: "_$s4Test7MyClassC1a1bACs5Int32V_AGtKcfC",
                // Class constructor returns the instance pointer directly (≤16 bytes, no sret).
                ReturnLowering: new TypeLoweringResult(
                    new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                    IsIndirect: false,
                    TotalByteSize: 8),
                SelfLowering: null,
                ParameterCount: 2,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: true,
                Throws: true,
                MetadataAccessorSymbol: "_$s4Test7MyClassCMa");

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor, ThunkTargetArch.X86_64);

            // Error-out captured from the LEADING %rdi into the error slot.
            Assert.Contains("movq    %rdi, -32(%rbp)", asm);
            // Value args shift back down for swiftcc: %rsi->%rdi, %rdx->%rsi.
            Assert.Contains("movq    %rsi, %rdi", asm);
            Assert.Contains("movq    %rdx, %rsi", asm);
            // Old bug stashed the error-out from the TRAILING value register (%rdx here).
            Assert.DoesNotContain("movq    %rdx, -32(%rbp)", asm);
            // Metatype accessor still called; swifterror cleared then written back.
            Assert.Contains("callq   _$s4Test7MyClassCMa", asm);
            Assert.Contains("xorl    %r12d, %r12d", asm);
            Assert.Contains("movq    %r12, (%r10)", asm);
        }

        #endregion

        #region Indirect-return metatype accessor — sret survives

        [Fact]
        public void EmitThunk_StaticMethodIndirectReturn_PreservesX8AcrossAccessor()
        {
            // ARM64: a static method returning an address-only / >32-byte struct carries the
            // caller's sret buffer pointer in x8 into the swiftcc call. The metatype accessor is an
            // ordinary `bl` that clobbers x8 (caller-saved, outside the AAPCS64 callee-saved set), so
            // the thunk must spill x8 before the accessor and reload it after — otherwise Swift writes
            // its result through a clobbered pointer (silent heap corruption).
            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "thunk_test_Foo_makeBuffer",
                SwiftSymbol: "_$s4Test3FooC10makeBufferAA6BufferVyFZ",
                ReturnLowering: new TypeLoweringResult(
                    new[] {
                        new RegisterSlot(RegisterFile.Integer, 0, 8),
                        new RegisterSlot(RegisterFile.Integer, 1, 8),
                        new RegisterSlot(RegisterFile.Integer, 2, 8),
                        new RegisterSlot(RegisterFile.Integer, 3, 8),
                        new RegisterSlot(RegisterFile.Integer, 4, 8)
                    },
                    IsIndirect: true,
                    TotalByteSize: 40),
                SelfLowering: null,
                ParameterCount: 0,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: true,
                IsConstructor: false,
                Throws: false,
                MetadataAccessorSymbol: "_$s4Test3FooCMa");

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor, ThunkTargetArch.Arm64);

            // x8 spilled, accessor called, x8 reloaded — in that order.
            Assert.Contains("str     x8, [sp, #0]", asm);
            Assert.Contains("ldr     x8, [sp, #0]", asm);
            var spill = asm.IndexOf("str     x8, [sp, #0]", System.StringComparison.Ordinal);
            var accessor = asm.IndexOf("bl      _$s4Test3FooCMa", System.StringComparison.Ordinal);
            var reload = asm.IndexOf("ldr     x8, [sp, #0]", System.StringComparison.Ordinal);
            Assert.True(spill >= 0 && accessor > spill && reload > accessor,
                $"expected x8 spill < accessor < reload; got {spill}/{accessor}/{reload}");
        }

        [Fact]
        public void EmitThunk_StaticMethodDirectReturn_DoesNotSpillX8()
        {
            // Contrast: a static method returning a register-resident value never carries an sret
            // pointer in x8, so the metatype accessor needs no x8 spill. Guards against over-spilling.
            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "thunk_test_Foo_makeSmall",
                SwiftSymbol: "_$s4Test3FooC9makeSmallSiyFZ",
                ReturnLowering: new TypeLoweringResult(
                    new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                    IsIndirect: false,
                    TotalByteSize: 8),
                SelfLowering: null,
                ParameterCount: 0,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: true,
                IsConstructor: false,
                Throws: false,
                MetadataAccessorSymbol: "_$s4Test3FooCMa");

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor, ThunkTargetArch.Arm64);

            Assert.Contains("bl      _$s4Test3FooCMa", asm);
            Assert.DoesNotContain("str     x8", asm);
        }

        [Fact]
        public void EmitThunk_X86_64_StaticMethodIndirectReturn_StashesSretInRbx()
        {
            // SysV: the indirect return's sret buffer arrives in %rdi on the cdecl side and
            // must reach swiftcc in %rax. The metatype accessor clobbers %rax and the caller-saved
            // arg registers, so the thunk stashes the sret pointer in callee-saved %rbx across the
            // accessor and moves it into %rax immediately before the swiftcc call.
            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "thunk_test_Foo_makeBuffer",
                SwiftSymbol: "_$s4Test3FooC10makeBufferAA6BufferVyFZ",
                ReturnLowering: new TypeLoweringResult(
                    new[] {
                        new RegisterSlot(RegisterFile.Integer, 0, 8),
                        new RegisterSlot(RegisterFile.Integer, 1, 8),
                        new RegisterSlot(RegisterFile.Integer, 2, 8),
                        new RegisterSlot(RegisterFile.Integer, 3, 8),
                        new RegisterSlot(RegisterFile.Integer, 4, 8)
                    },
                    IsIndirect: true,
                    TotalByteSize: 40),
                SelfLowering: null,
                ParameterCount: 0,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: true,
                IsConstructor: false,
                Throws: false,
                MetadataAccessorSymbol: "_$s4Test3FooCMa");

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor, ThunkTargetArch.X86_64);

            // sret stashed into callee-saved %rbx, accessor called, then %rbx → %rax before swiftcc.
            Assert.Contains("movq    %rdi, %rbx", asm);
            Assert.Contains("callq   _$s4Test3FooCMa", asm);
            Assert.Contains("movq    %rbx, %rax", asm);
            var stash = asm.IndexOf("movq    %rdi, %rbx", System.StringComparison.Ordinal);
            var accessor = asm.IndexOf("callq   _$s4Test3FooCMa", System.StringComparison.Ordinal);
            Assert.True(stash >= 0 && accessor > stash,
                $"expected sret stash before accessor; got {stash}/{accessor}");
        }

        #endregion

        #region Symbol Generation

        [Fact]
        public void GenerateThunkSymbol_ContainsModuleName()
        {
            var symbol = ThunkAssemblyEmitter.GenerateThunkSymbol("ImagePipeline", "$s13ImagePipeline5ImageC");

            Assert.StartsWith("thunk_ImagePipeline_", symbol);
        }

        [Fact]
        public void GenerateThunkSymbol_DeterministicForSameInput()
        {
            var symbol1 = ThunkAssemblyEmitter.GenerateThunkSymbol("Test", "$s4Test6methodyyF");
            var symbol2 = ThunkAssemblyEmitter.GenerateThunkSymbol("Test", "$s4Test6methodyyF");

            Assert.Equal(symbol1, symbol2);
        }

        [Fact]
        public void GenerateThunkSymbol_DifferentForDifferentMethods()
        {
            var symbol1 = ThunkAssemblyEmitter.GenerateThunkSymbol("Test", "$s4Test6methodAyyF");
            var symbol2 = ThunkAssemblyEmitter.GenerateThunkSymbol("Test", "$s4Test6methodByyF");

            Assert.NotEqual(symbol1, symbol2);
        }

        [Fact]
        public void GenerateThunkSymbol_SanitizesModuleName()
        {
            var symbol = ThunkAssemblyEmitter.GenerateThunkSymbol("My-Module.Name", "$s4Test");

            Assert.StartsWith("thunk_My_Module_Name_", symbol);
            Assert.DoesNotContain("-", symbol);
            Assert.DoesNotContain(".", symbol);
        }

        #endregion

        #region SwiftCallTargetResolver

        [Fact]
        public void Resolve_FreeFunction_NoSuffix()
        {
            var method = CreateMethod("doWork", "$s4Test6doWorkyyF");
            var symbol = SwiftCallTargetResolver.Resolve(method, null);
            Assert.Equal("$s4Test6doWorkyyF", symbol);
        }

        [Fact]
        public void Resolve_NonFinalClassInstanceMethod_AppendsTj()
        {
            var classDecl = CreateClassDecl("Counter", isFinal: false);
            var method = CreateMethod("add", "$s4Test7CounterC3addyS2iF",
                methodType: MethodType.Instance, parentDecl: classDecl);

            var symbol = SwiftCallTargetResolver.Resolve(method, classDecl);

            Assert.EndsWith("Tj", symbol);
            Assert.Equal("$s4Test7CounterC3addyS2iFTj", symbol);
        }

        [Fact]
        public void Resolve_FinalClassMethod_NoTj()
        {
            var classDecl = CreateClassDecl("Manager", isFinal: true);
            var method = CreateMethod("run", "$s4Test7ManagerC3runyyF",
                methodType: MethodType.Instance, parentDecl: classDecl);

            var symbol = SwiftCallTargetResolver.Resolve(method, classDecl);

            Assert.DoesNotContain("Tj", symbol);
        }

        [Fact]
        public void Resolve_FinalMethodOnNonFinalClass_NoTj()
        {
            var classDecl = CreateClassDecl("Counter", isFinal: false);
            var method = CreateMethod("getValue", "$s4Test7CounterC8getValueSiyF",
                methodType: MethodType.Instance, parentDecl: classDecl, isFinal: true);

            var symbol = SwiftCallTargetResolver.Resolve(method, classDecl);

            Assert.DoesNotContain("Tj", symbol);
        }

        [Fact]
        public void Resolve_Constructor_NoTj()
        {
            var classDecl = CreateClassDecl("Counter", isFinal: false);
            var method = CreateMethod("init", "$s4Test7CounterCACycfC",
                methodType: MethodType.Instance, parentDecl: classDecl, isConstructor: true);

            var symbol = SwiftCallTargetResolver.Resolve(method, classDecl);

            Assert.DoesNotContain("Tj", symbol);
        }

        [Fact]
        public void Resolve_StaticMethodOfClass_NoTj()
        {
            // A `static` member of a class is a final `class` member; the ABI marks it Final.
            var classDecl = CreateClassDecl("Counter", isFinal: false);
            var method = CreateMethod("create", "$s4Test7CounterC6createACyFZ",
                methodType: MethodType.Static, parentDecl: classDecl, isFinal: true);

            var symbol = SwiftCallTargetResolver.Resolve(method, classDecl);

            Assert.Equal("$s4Test7CounterC6createACyFZ", symbol);
        }

        [Fact]
        public void Resolve_ClassFuncOnNonFinalClass_AppendsTj()
        {
            // An overridable `class func` has a vtable entry; only its dispatch thunk is exported.
            var classDecl = CreateClassDecl("Counter", isFinal: false);
            var method = CreateMethod("report", "$s4Test7CounterC6reportSiyFZ",
                methodType: MethodType.Static, parentDecl: classDecl);

            var symbol = SwiftCallTargetResolver.Resolve(method, classDecl);

            Assert.Equal("$s4Test7CounterC6reportSiyFZTj", symbol);
        }

        [Fact]
        public void Resolve_ClassFuncOnFinalClass_NoTj()
        {
            var classDecl = CreateClassDecl("Counter", isFinal: true);
            var method = CreateMethod("report", "$s4Test7CounterC6reportSiyFZ",
                methodType: MethodType.Static, parentDecl: classDecl);

            var symbol = SwiftCallTargetResolver.Resolve(method, classDecl);

            Assert.Equal("$s4Test7CounterC6reportSiyFZ", symbol);
        }

        [Fact]
        public void Resolve_ExportedImplementationWithoutThunk_BindsImplementation()
        {
            // The library's exports outrank the declaration's attributes.
            var module = CreateModule("$s4Test7CounterC6reportSiyFZ");
            var classDecl = CreateClassDecl("Counter", isFinal: false);
            var method = CreateMethod("report", "$s4Test7CounterC6reportSiyFZ",
                methodType: MethodType.Static, parentDecl: classDecl);
            method.ModuleDecl = module;

            Assert.Equal("$s4Test7CounterC6reportSiyFZ", SwiftCallTargetResolver.Resolve(method, classDecl));
        }

        [Theory]
        [InlineData(MethodType.Static, "$s4Test4BaseC6reportSiyFZ", "$s4Test7DerivedC6reportSiyFZ")]
        [InlineData(MethodType.Instance, "$s4Test4BaseC6reportSiyF", "$s4Test7DerivedC6reportSiyF")]
        public void Resolve_OverrideWithoutThunkOfItsOwn_BindsTheIntroducersThunk(
            MethodType methodType, string baseSymbol, string derivedSymbol)
        {
            // An override with the overridden member's ABI reuses its vtable entry and gets no
            // dispatch thunk; the introducer's thunk dispatches on the derived self.
            var module = CreateModule(baseSymbol + "Tj");
            var (baseMethod, derivedMethod, derivedClass) = CreateOverridePair(methodType, baseSymbol, derivedSymbol, module);

            Assert.Equal(baseSymbol + "Tj", SwiftCallTargetResolver.Resolve(derivedMethod, derivedClass));
            Assert.Equal(baseSymbol + "Tj", SwiftCallTargetResolver.Resolve(baseMethod, baseMethod.ParentDecl));
        }

        [Fact]
        public void Resolve_OverrideWithThunkOfItsOwn_BindsItsOwnThunk()
        {
            var module = CreateModule("$s4Test4BaseC6reportSiyFZTj", "$s4Test7DerivedC6reportSiyFZTj");
            var (_, derivedMethod, derivedClass) = CreateOverridePair(
                MethodType.Static, "$s4Test4BaseC6reportSiyFZ", "$s4Test7DerivedC6reportSiyFZ", module);

            Assert.Equal("$s4Test7DerivedC6reportSiyFZTj", SwiftCallTargetResolver.Resolve(derivedMethod, derivedClass));
        }

        [Fact]
        public void Resolve_OverrideChain_BindsTheThunkOfTheMemberThatIntroducedTheEntry()
        {
            var module = CreateModule("$s4Test4BaseC6reportSiyFZTj");
            var (_, middleMethod, middleClass) = CreateOverridePair(
                MethodType.Static, "$s4Test4BaseC6reportSiyFZ", "$s4Test7DerivedC6reportSiyFZ", module);
            var leafClass = CreateClassDecl("Leaf", isFinal: false);
            leafClass.ResolvedSuperclass = middleClass;
            var leafMethod = CreateMethod("report", "$s4Test4LeafC6reportSiyFZ",
                methodType: MethodType.Static, parentDecl: leafClass);
            leafMethod.IsOverride = true;
            leafMethod.ModuleDecl = module;
            leafMethod.CSSignature.Add(IntReturn());
            leafClass.Methods.Add(leafMethod);

            Assert.True(middleMethod.IsOverride);
            Assert.Equal("$s4Test4BaseC6reportSiyFZTj", SwiftCallTargetResolver.Resolve(leafMethod, leafClass));
        }

        [Theory]
        [InlineData("Swift.Int", "$s4Test4BaseC6reportSiyFZTj")]
        [InlineData("Swift.String", "$s4Test7DerivedC6reportSSyFZTj")]
        public void Resolve_OverrideWithUnknownExports_RedirectsOnlyAnIdenticalSignature(string derivedReturn, string expected)
        {
            // Without the export list only an identical signature is certain to share the entry.
            var (_, derivedMethod, derivedClass) = CreateOverridePair(
                MethodType.Static, "$s4Test4BaseC6reportSiyFZ",
                derivedReturn == "Swift.Int" ? "$s4Test7DerivedC6reportSiyFZ" : "$s4Test7DerivedC6reportSSyFZ",
                module: null!, derivedReturn: derivedReturn);

            Assert.Equal(expected, SwiftCallTargetResolver.Resolve(derivedMethod, derivedClass));
        }

        [Fact]
        public void Resolve_OverrideOfOneOfTwoSameLabelOverloads_BindsTheOverloadWithItsTypes()
        {
            // `transform(_: Box)` and `transform(_: Box?)` share a name and labels. An override of the
            // optional one must bind the optional one's entry; the other's dispatches to another body.
            const string plainSymbol = "$s4Test4BaseC9transformySiAA3BoxCF";
            const string optionalSymbol = "$s4Test4BaseC9transformySiAA3BoxCSgF";
            var module = CreateModule(plainSymbol + "Tj", optionalSymbol + "Tj");
            var (baseClass, derivedClass) = CreateTransformOverloads(module, "Test.Box", "Swift.Optional<Test.Box>", plainSymbol, optionalSymbol);
            var derivedMethod = AddTransform(derivedClass, "$s4Test7DerivedC9transformySiAA3BoxCSgF", "Swift.Optional<Test.Box>", module);
            derivedMethod.IsOverride = true;

            Assert.Equal(optionalSymbol + "Tj", SwiftCallTargetResolver.Resolve(derivedMethod, derivedClass));
            Assert.Equal(2, baseClass.Methods.Count);
        }

        [Theory]
        [InlineData("Swift.Double", false)]
        [InlineData("Test.Box", true)]
        public void Resolve_OverrideBelowAnUnrelatedSameLabelOverload_BindsTheFartherExactMatch(string midType, bool midIsInOut)
        {
            // Mid adds `transform(_: Double)`, or `transform(_: inout Box)`, beside the inherited
            // `transform(_: Box)`; Leaf overrides the inherited one. Binding Mid's thunk would call
            // another body with another argument (an inout one takes a pointer). Swift picks the exact
            // match wherever in the chain it is declared.
            const string baseSymbol = "$s4Test4BaseC9transformySiAA3BoxCF";
            const string midSymbol = "$s4Test3MidC9transformySiSdF";
            var module = CreateModule(baseSymbol + "Tj", midSymbol + "Tj");
            var baseClass = CreateClassDecl("Base", isFinal: false);
            AddTransform(baseClass, baseSymbol, "Test.Box", module);
            var midClass = CreateClassDecl("Mid", isFinal: false);
            midClass.ResolvedSuperclass = baseClass;
            AddTransform(midClass, midSymbol, midType, module, isInOut: midIsInOut);
            var leafClass = CreateClassDecl("Leaf", isFinal: false);
            leafClass.ResolvedSuperclass = midClass;
            var leafMethod = AddTransform(leafClass, "$s4Test4LeafC9transformySiAA3BoxCF", "Test.Box", module);
            leafMethod.IsOverride = true;

            Assert.Equal(baseSymbol + "Tj", SwiftCallTargetResolver.Resolve(leafMethod, leafClass));
        }

        [Fact]
        public void Resolve_OverrideBelowAMoreConstrainedGenericOverload_BindsTheFartherExactMatch()
        {
            // Base declares `transform<T>(_: T)`, Mid adds `transform<T: P>(_: T)`, and Leaf overrides
            // Base's. The parameter types read the same, but Mid's member is another entry and its thunk
            // takes a witness table Leaf's call does not pass.
            const string baseSymbol = "$s4Test4BaseC9transformySixlF";
            const string midSymbol = "$s4Test3MidC9transformySixAA1PRzlF";
            var module = CreateModule(baseSymbol + "Tj", midSymbol + "Tj");
            var baseClass = CreateClassDecl("Base", isFinal: false);
            AddTransform(baseClass, baseSymbol, "τ_0_0", module, genericSignature: "<τ_0_0>");
            var midClass = CreateClassDecl("Mid", isFinal: false);
            midClass.ResolvedSuperclass = baseClass;
            AddTransform(midClass, midSymbol, "τ_0_0", module, genericSignature: "<τ_0_0 where τ_0_0 : Test.P>");
            var leafClass = CreateClassDecl("Leaf", isFinal: false);
            leafClass.ResolvedSuperclass = midClass;
            var leafMethod = AddTransform(leafClass, "$s4Test4LeafC9transformySixlF", "τ_0_0", module, genericSignature: "<τ_0_0>");
            leafMethod.IsOverride = true;

            Assert.Equal(baseSymbol + "Tj", SwiftCallTargetResolver.Resolve(leafMethod, leafClass));
        }

        [Fact]
        public void Resolve_OverrideBelowANearerVarianceMatch_BindsTheFartherExactMatch()
        {
            // Base declares `transform(_: Box?)`, Mid adds the unrelated `transform(_: Box)`, and Leaf
            // overrides Base's. Mid's member matches Leaf by Optional variance, but an exact match
            // outranks it, as in Swift's own override matching.
            const string baseSymbol = "$s4Test4BaseC9transformySiAA3BoxCSgF";
            const string midSymbol = "$s4Test3MidC9transformySiAA3BoxCF";
            var module = CreateModule(baseSymbol + "Tj", midSymbol + "Tj");
            var baseClass = CreateClassDecl("Base", isFinal: false);
            AddTransform(baseClass, baseSymbol, "Swift.Optional<Test.Box>", module);
            var midClass = CreateClassDecl("Mid", isFinal: false);
            midClass.ResolvedSuperclass = baseClass;
            AddTransform(midClass, midSymbol, "Test.Box", module);
            var leafClass = CreateClassDecl("Leaf", isFinal: false);
            leafClass.ResolvedSuperclass = midClass;
            var leafMethod = AddTransform(leafClass, "$s4Test4LeafC9transformySiAA3BoxCSgF", "Swift.Optional<Test.Box>", module);
            leafMethod.IsOverride = true;

            Assert.Equal(baseSymbol + "Tj", SwiftCallTargetResolver.Resolve(leafMethod, leafClass));
        }

        [Fact]
        public void Resolve_VarianceOverrideAmongSeveralCandidates_KeepsItsOwnThunk()
        {
            // Leaf widens `transform(_: Box)` to `Box?` with Mid's unrelated `transform(_: Double)` also
            // in view. Without an exact match, a candidate that matches neither way might still be the
            // overridden one through a variance not modelled here, so Leaf keeps its own symbol and the
            // export check decides the route.
            const string baseSymbol = "$s4Test4BaseC9transformySiAA3BoxCF";
            const string midSymbol = "$s4Test3MidC9transformySiSdF";
            var module = CreateModule(baseSymbol + "Tj", midSymbol + "Tj");
            var baseClass = CreateClassDecl("Base", isFinal: false);
            AddTransform(baseClass, baseSymbol, "Test.Box", module);
            var midClass = CreateClassDecl("Mid", isFinal: false);
            midClass.ResolvedSuperclass = baseClass;
            AddTransform(midClass, midSymbol, "Swift.Double", module);
            var leafClass = CreateClassDecl("Leaf", isFinal: false);
            leafClass.ResolvedSuperclass = midClass;
            var leafMethod = AddTransform(leafClass, "$s4Test4LeafC9transformySiAA3BoxCSgF", "Swift.Optional<Test.Box>", module);
            leafMethod.IsOverride = true;

            Assert.Equal("$s4Test4LeafC9transformySiAA3BoxCSgFTj", SwiftCallTargetResolver.Resolve(leafMethod, leafClass));
        }

        [Fact]
        public void Resolve_OverrideOfAnOverrideThatOwnsAnEntry_BindsThatOverridesThunk()
        {
            // Mid narrows the result from `Box?` to `Box`, gets an entry and thunk of its own, and Leaf
            // repeats Mid's signature without one. Leaf's entry is Mid's, not Base's older one.
            const string baseSymbol = "$s4Test4BaseC9transformAA3BoxCSgyF";
            const string midSymbol = "$s4Test3MidC9transformAA3BoxCyF";
            var module = CreateModule(baseSymbol + "Tj", midSymbol + "Tj");
            var baseClass = CreateClassDecl("Base", isFinal: false);
            AddNullary(baseClass, baseSymbol, "Swift.Optional<Test.Box>", module);
            var midClass = CreateClassDecl("Mid", isFinal: false);
            midClass.ResolvedSuperclass = baseClass;
            AddNullary(midClass, midSymbol, "Test.Box", module).IsOverride = true;
            var leafClass = CreateClassDecl("Leaf", isFinal: false);
            leafClass.ResolvedSuperclass = midClass;
            var leafMethod = AddNullary(leafClass, "$s4Test4LeafC9transformAA3BoxCyF", "Test.Box", module);
            leafMethod.IsOverride = true;

            Assert.Equal(midSymbol + "Tj", SwiftCallTargetResolver.Resolve(leafMethod, leafClass));
        }

        [Fact]
        public void Resolve_OverrideWideningItsOnlyCandidatesParameter_BindsThatCandidatesThunk()
        {
            // A lone same-selector ancestor member is the overridden one even when the override widens
            // a parameter to its Optional, which keeps the ABI and so the entry.
            const string plainSymbol = "$s4Test4BaseC9transformySiAA3BoxCF";
            var module = CreateModule(plainSymbol + "Tj");
            var baseClass = CreateClassDecl("Base", isFinal: false);
            AddTransform(baseClass, plainSymbol, "Test.Box", module);
            var derivedClass = CreateClassDecl("Derived", isFinal: false);
            derivedClass.ResolvedSuperclass = baseClass;
            var derivedMethod = AddTransform(derivedClass, "$s4Test7DerivedC9transformySiAA3BoxCSgF", "Swift.Optional<Test.Box>", module);
            derivedMethod.IsOverride = true;

            Assert.Equal(plainSymbol + "Tj", SwiftCallTargetResolver.Resolve(derivedMethod, derivedClass));
        }

        private static (ClassDecl Base, ClassDecl Derived) CreateTransformOverloads(
            ModuleDecl module, string firstType, string secondType, string firstSymbol, string secondSymbol)
        {
            var baseClass = CreateClassDecl("Base", isFinal: false);
            AddTransform(baseClass, firstSymbol, firstType, module);
            AddTransform(baseClass, secondSymbol, secondType, module);
            var derivedClass = CreateClassDecl("Derived", isFinal: false);
            derivedClass.ResolvedSuperclass = baseClass;
            return (baseClass, derivedClass);
        }

        private static MethodDecl AddNullary(ClassDecl parent, string symbol, string resultType, ModuleDecl module)
        {
            var method = CreateMethod("transform", symbol, methodType: MethodType.Instance, parentDecl: parent);
            method.ModuleDecl = module;
            var result = IntReturn();
            result.SwiftTypeSpec = TypeSpecParser.Parse(resultType)!;
            method.CSSignature.Add(result);
            parent.Methods.Add(method);
            return method;
        }

        private static MethodDecl AddTransform(ClassDecl parent, string symbol, string parameterType, ModuleDecl module,
            bool isInOut = false, string genericSignature = null)
        {
            var method = CreateMethod("transform", symbol, methodType: MethodType.Instance, parentDecl: parent);
            method.ModuleDecl = module;
            method.RawGenericSig = genericSignature;
            method.CSSignature.Add(IntReturn());
            method.CSSignature.Add(new ArgumentDecl
            {
                SwiftTypeSpec = TypeSpecParser.Parse(parameterType)!,
                Name = "_",
                PrivateName = "w",
                IsInOut = isInOut,
                IsGeneric = false,
                ParentDecl = null,
                ModuleDecl = null,
            });
            parent.Methods.Add(method);
            return method;
        }

        private static ModuleDecl CreateModule(params string[] exportedSymbols) => new()
        {
            Name = "Test",
            Dependencies = new List<string>(),
            Types = new List<TypeDecl>(),
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            ExportedSymbols = new HashSet<string>(exportedSymbols),
        };

        private static ArgumentDecl IntReturn(string type = "Swift.Int") => new()
        {
            SwiftTypeSpec = new NamedTypeSpec(type),
            Name = string.Empty,
            PrivateName = string.Empty,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null,
        };

        private static (MethodDecl Base, MethodDecl Derived, ClassDecl DerivedClass) CreateOverridePair(
            MethodType methodType, string baseSymbol, string derivedSymbol, ModuleDecl module,
            string derivedReturn = "Swift.Int")
        {
            var baseClass = CreateClassDecl("Base", isFinal: false);
            var baseMethod = CreateMethod("report", baseSymbol, methodType: methodType, parentDecl: baseClass);
            baseMethod.ModuleDecl = module;
            baseMethod.CSSignature.Add(IntReturn());
            baseClass.Methods.Add(baseMethod);

            var derivedClass = CreateClassDecl("Derived", isFinal: false);
            derivedClass.ResolvedSuperclass = baseClass;
            var derivedMethod = CreateMethod("report", derivedSymbol, methodType: methodType, parentDecl: derivedClass);
            derivedMethod.IsOverride = true;
            derivedMethod.ModuleDecl = module;
            derivedMethod.CSSignature.Add(IntReturn(derivedReturn));
            derivedClass.Methods.Add(derivedMethod);
            return (baseMethod, derivedMethod, derivedClass);
        }

        [Fact]
        public void Resolve_ExtensionMethod_NoTj()
        {
            var classDecl = CreateClassDecl("Counter", isFinal: false);
            var method = CreateMethod("extMethod", "$s4Test7CounterC9extMethodyyF",
                methodType: MethodType.Instance, parentDecl: classDecl, isExtensionMethod: true);

            var symbol = SwiftCallTargetResolver.Resolve(method, classDecl);

            Assert.DoesNotContain("Tj", symbol);
        }

        [Fact]
        public void ResolveWithPrefix_AddsUnderscore()
        {
            var method = CreateMethod("doWork", "$s4Test6doWorkyyF");
            var symbol = SwiftCallTargetResolver.ResolveWithPrefix(method, null);
            Assert.Equal("_$s4Test6doWorkyyF", symbol);
        }

        #endregion

        #region Edge Cases

        [Fact]
        public void EmitThunk_VoidReturnNoParams_TailCall()
        {
            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "thunk_test_noop",
                SwiftSymbol: "_$s4Test4noopyyF",
                ReturnLowering: null,
                SelfLowering: null,
                ParameterCount: 0,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: false,
                Throws: false,
                MetadataAccessorSymbol: null);

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            Assert.Contains("b       _$s4Test4noopyyF", asm);
        }

        [Fact]
        public void EmitThunk_ConstructorNoMetadataSymbol_SkipsMetatypeSetup()
        {
            // Edge case: constructor without metadata accessor symbol (shouldn't happen,
            // but must not crash)
            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "thunk_test_init",
                SwiftSymbol: "_$s4Test3FooCACycfC",
                ReturnLowering: new TypeLoweringResult(
                    new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                    IsIndirect: false,
                    TotalByteSize: 8),
                SelfLowering: null,
                ParameterCount: 0,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: true,
                Throws: false,
                MetadataAccessorSymbol: null); // No metadata accessor

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            // Should not crash; no metadata accessor call
            Assert.DoesNotContain("bl      null", asm);
            Assert.Contains("bl      _$s4Test3FooCACycfC", asm);
        }

        [Fact]
        public void EmitThunk_FloatOnlyReturn_CorrectRegisters()
        {
            // FloatPair { x: Double, y: Double } = 16 bytes → both conventions use d0+d1 → tail call
            var returnLowering = new TypeLoweringResult(
                new[] {
                    new RegisterSlot(RegisterFile.Float, 0, 8),
                    new RegisterSlot(RegisterFile.Float, 1, 8)
                },
                IsIndirect: false,
                TotalByteSize: 16);

            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "thunk_test_makeFloatPair",
                SwiftSymbol: "_$s4Test13makeFloatPairAA0dE0VyF",
                ReturnLowering: returnLowering,
                SelfLowering: null,
                ParameterCount: 2,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: false,
                Throws: false,
                MetadataAccessorSymbol: null);

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            // 16 bytes with 2 float slots → tail call (no bridge needed)
            Assert.Contains("b       ", asm);
            Assert.DoesNotContain("str     d0", asm);
        }

        [Fact]
        public void EmitThunk_SymbolAlignment_HasP2Align()
        {
            // All thunks should have .p2align 2 for ARM64 instruction alignment
            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "thunk_test_aligned",
                SwiftSymbol: "_$s4Test7alignedyyF",
                ReturnLowering: null,
                SelfLowering: null,
                ParameterCount: 0,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: false,
                Throws: false,
                MetadataAccessorSymbol: null);

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            Assert.Contains(".p2align 2", asm);
        }

        #endregion

        #region Constructor with Multiple Params

        [Fact]
        public void EmitThunk_ConstructorWith2Params_SavesAndRestores()
        {
            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "thunk_test_Foo_init2",
                SwiftSymbol: "_$s4Test3FooC1x1ySi_SitcfC",
                ReturnLowering: new TypeLoweringResult(
                    new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                    IsIndirect: false,
                    TotalByteSize: 8),
                SelfLowering: null,
                ParameterCount: 2,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: true,
                Throws: false,
                MetadataAccessorSymbol: "_$s4Test3FooCMa");

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            // Must save both params
            Assert.Contains("str     x0, [sp, #0]", asm);
            Assert.Contains("str     x1, [sp, #8]", asm);
            // Must restore both params
            Assert.Contains("ldr     x0, [sp, #0]", asm);
            Assert.Contains("ldr     x1, [sp, #8]", asm);
            // Stack alignment: 2 params × 8 = 16 bytes (already 16-aligned)
            Assert.Contains("sub     sp, sp, #16", asm);
            Assert.Contains("add     sp, sp, #16", asm);
        }

        [Fact]
        public void EmitThunk_ConstructorWith3Params_StackAligned()
        {
            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "thunk_test_Foo_init3",
                SwiftSymbol: "_$s4Test3FooC3init3yyF",
                ReturnLowering: new TypeLoweringResult(
                    new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                    IsIndirect: false,
                    TotalByteSize: 8),
                SelfLowering: null,
                ParameterCount: 3,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: true,
                Throws: false,
                MetadataAccessorSymbol: "_$s4Test3FooCMa");

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            // 3 params × 8 = 24 bytes → aligned to 32 bytes
            Assert.Contains("sub     sp, sp, #32", asm);
            Assert.Contains("add     sp, sp, #32", asm);
        }

        [Fact]
        public void EmitThunk_ConstructorWithFloatParams_SavesAndRestoresFloatRegs()
        {
            // Constructor with 1 int param + 1 float param — must save/restore both
            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "thunk_test_Foo_initMixed",
                SwiftSymbol: "_$s4Test3FooC5value6factorSi_SdtcfC",
                ReturnLowering: new TypeLoweringResult(
                    new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                    IsIndirect: false,
                    TotalByteSize: 8),
                SelfLowering: null,
                ParameterCount: 1,
                FloatParameterCount: 1,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: true,
                Throws: false,
                MetadataAccessorSymbol: "_$s4Test3FooCMa");

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            // Must save both int and float params
            Assert.Contains("str     x0, [sp, #0]", asm);
            Assert.Contains("str     d0, [sp, #8]", asm);
            // Must restore both
            Assert.Contains("ldr     x0, [sp, #0]", asm);
            Assert.Contains("ldr     d0, [sp, #8]", asm);
            // Must call metadata accessor
            Assert.Contains("bl      _$s4Test3FooCMa", asm);
        }

        #endregion

        #region ReturnBufferSlots Natural Offsets

        [Fact]
        public void ReturnBufferSlots_MixedWidthFields_UsesNaturalAlignment()
        {
            // {Int32, Float, Int64, Double} ("i4,f4,i8,f8"): the 4-byte fields pack into the first
            // eightbyte, then the 8-byte fields align to 8 and 16. A uniform 8-byte stride would place
            // the Float at offset 8 and corrupt the following Int64 — these offsets must match the
            // C/Swift Sequential layout the managed struct reads.
            var ret = new TypeLoweringResult(
                new[]
                {
                    new RegisterSlot(RegisterFile.Integer, 0, 4),
                    new RegisterSlot(RegisterFile.Float, 0, 4),
                    new RegisterSlot(RegisterFile.Integer, 1, 8),
                    new RegisterSlot(RegisterFile.Float, 1, 8),
                },
                IsIndirect: false,
                TotalByteSize: 24);

            var offsets = ThunkAssemblyEmitter.ReturnBufferSlots(ret).Select(s => s.Offset).ToList();

            Assert.Equal(new[] { 0, 4, 8, 16 }, offsets);
        }

        [Fact]
        public void ReturnBufferSlots_AllEightByteFields_UsesUniformStride()
        {
            // {Double, Double} ("f,f"): each field owns a full eightbyte at 0 and 8.
            var ret = new TypeLoweringResult(
                new[]
                {
                    new RegisterSlot(RegisterFile.Float, 0, 8),
                    new RegisterSlot(RegisterFile.Float, 1, 8),
                },
                IsIndirect: false,
                TotalByteSize: 16);

            var offsets = ThunkAssemblyEmitter.ReturnBufferSlots(ret).Select(s => s.Offset).ToList();

            Assert.Equal(new[] { 0, 8 }, offsets);
        }

        #endregion

        #region Test Helpers

        private static MethodDecl CreateMethod(
            string name,
            string mangledName,
            MethodType methodType = MethodType.Static,
            BaseDecl parentDecl = null!,
            bool isConstructor = false,
            bool isFinal = false,
            bool isExtensionMethod = false)
        {
            return new MethodDecl
            {
                Name = name,
                MangledName = mangledName,
                MethodType = methodType,
                IsConstructor = isConstructor,
                IsFinal = isFinal,
                IsExtensionMethod = isExtensionMethod,
                CSSignature = new List<ArgumentDecl>(),
                GenericParameters = new List<GenericArgumentDecl>(),
                Throws = false,
                IsAsync = false,
                IsSynthesizedAccessor = false,
                ParentDecl = parentDecl,
                ModuleDecl = null,
            };
        }

        private static ClassDecl CreateClassDecl(string name, bool isFinal = false)
        {
            return new ClassDecl
            {
                Name = name,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"Test.{name}"),
                MangledName = $"$s4Test{name.Length}{name}CN",
                IsFinal = isFinal,
                Conformances = new List<TypeConformance>(),
                Properties = new List<PropertyDecl>(),
                Methods = new List<MethodDecl>(),
                Types = new List<TypeDecl>(),
                Operators = new List<OperatorDecl>(),
                Subscripts = new List<SubscriptDecl>(),
                GenericParameters = new List<GenericArgumentDecl>(),
                ParentDecl = null,
                ModuleDecl = null,
            };
        }

        #endregion
    }
}
