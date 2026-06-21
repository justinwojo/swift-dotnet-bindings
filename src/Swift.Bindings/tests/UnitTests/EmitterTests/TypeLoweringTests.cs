// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests
{
    public class TypeLoweringTests
    {
        /// <summary>
        /// Creates a minimal type database with the given type records registered.
        /// </summary>
        private static TypeDatabase CreateTypeDb(params (string moduleName, string path, SwiftTypeName name, TypeRecord record)[] entries)
        {
            var db = new TypeDatabase();
            foreach (var (moduleName, path, name, record) in entries)
            {
                var moduleDb = new ModuleTypeDatabase(moduleName, path);
                moduleDb.RegisterType(name, record);
                if (!db.IsModuleLoaded(moduleName))
                    db.AddModuleDatabase(moduleDb);
                else
                    db.UpdateTypeRecord(name, record);
            }
            return db;
        }

        private static TypeDatabase CreateTypeDbWithModule(string moduleName, params (SwiftTypeName name, TypeRecord record)[] types)
        {
            var db = new TypeDatabase();
            var moduleDb = new ModuleTypeDatabase(moduleName, $"/fake/{moduleName}.dylib");
            foreach (var (name, record) in types)
            {
                moduleDb.RegisterType(name, record);
            }
            db.AddModuleDatabase(moduleDb);
            return db;
        }

        #region Scalar Types

        [Theory]
        [InlineData("Swift.Int", RegisterFile.Integer, 8)]
        [InlineData("Swift.UInt", RegisterFile.Integer, 8)]
        [InlineData("Swift.Int8", RegisterFile.Integer, 1)]
        [InlineData("Swift.UInt8", RegisterFile.Integer, 1)]
        [InlineData("Swift.Int16", RegisterFile.Integer, 2)]
        [InlineData("Swift.UInt16", RegisterFile.Integer, 2)]
        [InlineData("Swift.Int32", RegisterFile.Integer, 4)]
        [InlineData("Swift.UInt32", RegisterFile.Integer, 4)]
        [InlineData("Swift.Int64", RegisterFile.Integer, 8)]
        [InlineData("Swift.UInt64", RegisterFile.Integer, 8)]
        [InlineData("Swift.Bool", RegisterFile.Integer, 1)]
        public void LowerReturnType_IntegerScalars_SingleIntegerSlot(string typeName, RegisterFile expectedFile, int expectedSize)
        {
            var typeSpec = new NamedTypeSpec(typeName);
            var db = new TypeDatabase();

            var result = TypeLowering.LowerReturnType(typeSpec, db);

            Assert.NotNull(result);
            Assert.False(result!.IsIndirect);
            Assert.Single(result.Slots);
            Assert.Equal(expectedFile, result.Slots[0].File);
            Assert.Equal(0, result.Slots[0].Index);
            Assert.Equal(expectedSize, result.Slots[0].ByteSize);
            Assert.Equal(expectedSize, result.TotalByteSize);
        }

        [Theory]
        [InlineData("Swift.Float", 4)]
        [InlineData("Swift.Double", 8)]
        [InlineData("CoreFoundation.CGFloat", 8)]
        [InlineData("CoreGraphics.CGFloat", 8)]
        public void LowerReturnType_FloatScalars_SingleFloatSlot(string typeName, int expectedSize)
        {
            var typeSpec = new NamedTypeSpec(typeName);
            var db = new TypeDatabase();

            var result = TypeLowering.LowerReturnType(typeSpec, db);

            Assert.NotNull(result);
            Assert.False(result!.IsIndirect);
            Assert.Single(result.Slots);
            Assert.Equal(RegisterFile.Float, result.Slots[0].File);
            Assert.Equal(0, result.Slots[0].Index);
            Assert.Equal(expectedSize, result.Slots[0].ByteSize);
        }

        [Theory]
        [InlineData("Swift.OpaquePointer")]
        [InlineData("Swift.UnsafeRawPointer")]
        [InlineData("Swift.UnsafeMutableRawPointer")]
        public void LowerReturnType_PointerTypes_SingleIntegerSlot(string typeName)
        {
            var typeSpec = new NamedTypeSpec(typeName);
            var db = new TypeDatabase();

            var result = TypeLowering.LowerReturnType(typeSpec, db);

            Assert.NotNull(result);
            Assert.False(result!.IsIndirect);
            Assert.Single(result.Slots);
            Assert.Equal(RegisterFile.Integer, result.Slots[0].File);
            Assert.Equal(8, result.TotalByteSize);
        }

        #endregion

        #region Typed Pointers

        [Fact]
        public void LowerReturnType_UnsafePointerOfInt_SingleIntegerSlot()
        {
            var typeSpec = new NamedTypeSpec("Swift.UnsafePointer", new NamedTypeSpec("Swift.Int"));
            var db = new TypeDatabase();

            var result = TypeLowering.LowerReturnType(typeSpec, db);

            Assert.NotNull(result);
            Assert.False(result!.IsIndirect);
            Assert.Single(result.Slots);
            Assert.Equal(RegisterFile.Integer, result.Slots[0].File);
            Assert.Equal(8, result.TotalByteSize);
        }

        [Fact]
        public void LowerReturnType_UnsafeMutablePointerOfDouble_SingleIntegerSlot()
        {
            var typeSpec = new NamedTypeSpec("Swift.UnsafeMutablePointer", new NamedTypeSpec("Swift.Double"));
            var db = new TypeDatabase();

            var result = TypeLowering.LowerReturnType(typeSpec, db);

            Assert.NotNull(result);
            Assert.Single(result.Slots);
            Assert.Equal(RegisterFile.Integer, result.Slots[0].File);
        }

        #endregion

        #region Struct Types

        [Fact]
        public void LowerReturnType_FrozenStruct2Int_TwoIntegerSlots()
        {
            // Point { x: Int, y: Int } → 2 integer slots, direct
            var name = SwiftTypeName.FromModuleQualifiedName("MyLib.Point");
            var record = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MyLib", "Point"),
                SwiftTypeName = name,
                MetadataAccessor = "$s5MyLib5PointV",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
                InlineSize = 16,
                AbiFieldLayout = "i,i"
            };
            var db = CreateTypeDbWithModule("MyLib", (name, record));
            var typeSpec = new NamedTypeSpec("MyLib.Point");

            var result = TypeLowering.LowerReturnType(typeSpec, db);

            Assert.NotNull(result);
            Assert.False(result!.IsIndirect);
            Assert.Equal(2, result.Slots.Count);
            Assert.All(result.Slots, s => Assert.Equal(RegisterFile.Integer, s.File));
            Assert.Equal(0, result.Slots[0].Index);
            Assert.Equal(1, result.Slots[1].Index);
            Assert.Equal(16, result.TotalByteSize);
        }

        [Fact]
        public void LowerReturnType_MixedIntFloatInOneEightbyte_DeclinesToCdecl()
        {
            // Mixed { i: Int32, f: Float, j: Int64, d: Double } → "i4,f4,i8,f8".
            // The first eightbyte (bytes 0-7) holds BOTH the Int32 (0-3) and the Float (4-7).
            // swiftcc keeps them in separate registers (a GPR + an FP register); the System V C
            // ABI coalesces that eightbyte into a single INTEGER register holding both halves.
            // The field-wise register bridge cannot reproduce both lowerings, so TypeLowering must
            // decline (null) and route the type to the @_cdecl wrapper, whose C-ABI call is correct
            // by construction. The old field-count model produced four slots and mis-bridged.
            var name = SwiftTypeName.FromModuleQualifiedName("MyLib.Mixed");
            var record = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MyLib", "Mixed"),
                SwiftTypeName = name,
                MetadataAccessor = "$s5MyLib5MixedV",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.HasFloatFields,
                Kind = TypeRecordKind.Struct,
                InlineSize = 24,
                AbiFieldLayout = "i4,f4,i8,f8"
            };
            var db = CreateTypeDbWithModule("MyLib", (name, record));

            var result = TypeLowering.LowerReturnType(new NamedTypeSpec("MyLib.Mixed"), db);

            Assert.Null(result);
        }

        [Fact]
        public void LowerReturnType_SeparateEightbytes_PreserveFloatWidth()
        {
            // { j: Int64, f: Float } → "i8,f4". The Int64 fills eightbyte 0; the Float sits alone in
            // eightbyte 1, so neither eightbyte mixes register files and the type lowers directly.
            // The lone-float slot must preserve its 4-byte width (a 32-bit float returns in s0, not
            // d0) — the width-suffixed fragment is what carries that distinction.
            var name = SwiftTypeName.FromModuleQualifiedName("MyLib.IntFloat");
            var record = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MyLib", "IntFloat"),
                SwiftTypeName = name,
                MetadataAccessor = "$s5MyLib8IntFloatV",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.HasFloatFields,
                Kind = TypeRecordKind.Struct,
                InlineSize = 16,
                AbiFieldLayout = "i8,f4"
            };
            var db = CreateTypeDbWithModule("MyLib", (name, record));

            var result = TypeLowering.LowerReturnType(new NamedTypeSpec("MyLib.IntFloat"), db);

            Assert.NotNull(result);
            Assert.False(result!.IsIndirect);
            Assert.Equal(2, result.Slots.Count);
            Assert.Equal(RegisterFile.Integer, result.Slots[0].File);
            Assert.Equal(8, result.Slots[0].ByteSize);
            Assert.Equal(RegisterFile.Float, result.Slots[1].File);
            Assert.Equal(0, result.Slots[1].Index);
            Assert.Equal(4, result.Slots[1].ByteSize);
        }

        [Fact]
        public void LowerReturnType_Int8x5Int64Int64_CoalescesToThreeIntegerSlots()
        {
            // { a,b,c,d,e: Int8, f: Int64, g: Int64 } → "i1,i1,i1,i1,i1,i8,i8".
            // The five Int8s share eightbyte 0 (bytes 0-4) and coalesce into ONE general-purpose
            // register; the two Int64s occupy eightbytes 1 and 2. swiftcc returns this 24-byte
            // aggregate DIRECTLY in three GPRs — it is NOT seven slots forced indirect. The old
            // field-count model counted seven slots (> the 4-slot limit) and returned the value
            // indirectly, reading silent garbage. This is the headline case and the
            // {Int8×5,Int64,Int64} done-when fixture's unit-level mirror.
            var name = SwiftTypeName.FromModuleQualifiedName("MyLib.Packed7");
            var record = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MyLib", "Packed7"),
                SwiftTypeName = name,
                MetadataAccessor = "$s5MyLib7Packed7V",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
                InlineSize = 24,
                AbiFieldLayout = "i1,i1,i1,i1,i1,i8,i8"
            };
            var db = CreateTypeDbWithModule("MyLib", (name, record));

            var result = TypeLowering.LowerReturnType(new NamedTypeSpec("MyLib.Packed7"), db);

            Assert.NotNull(result);
            Assert.False(result!.IsIndirect);
            Assert.Equal(3, result.Slots.Count);
            Assert.All(result.Slots, s => Assert.Equal(RegisterFile.Integer, s.File));
            Assert.Equal(0, result.Slots[0].Index);
            Assert.Equal(1, result.Slots[1].Index);
            Assert.Equal(2, result.Slots[2].Index);
            Assert.Equal(24, result.TotalByteSize);
        }

        [Fact]
        public void LowerReturnType_TwoFloatsInOneEightbyte_DeclinesToCdecl()
        {
            // { x: Float, y: Float } packed into one eightbyte → "f4,f4". swiftcc keeps each Float in
            // its own FP register; the System V C ABI coalesces two 4-byte floats into a single SSE
            // register. Divergent, so TypeLowering declines a lowering that keeps one float per slot
            // (Contrast LowerReturnType_FloatPair_TwoFloatSlots, where each Double fills its
            // own eightbyte and the two-FP-slot lowering is valid.)
            var name = SwiftTypeName.FromModuleQualifiedName("MyLib.Float2");
            var record = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MyLib", "Float2"),
                SwiftTypeName = name,
                MetadataAccessor = "$s5MyLib6Float2V",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.HasFloatFields,
                Kind = TypeRecordKind.Struct,
                InlineSize = 8,
                AbiFieldLayout = "f4,f4"
            };
            var db = CreateTypeDbWithModule("MyLib", (name, record));

            var result = TypeLowering.LowerReturnType(new NamedTypeSpec("MyLib.Float2"), db);

            Assert.Null(result);
        }

        [Fact]
        public void LowerReturnType_LegacyBareLetters_DefaultToEightByteSlots()
        {
            // A type database produced before widths were tracked stores bare letters. They must
            // still parse, defaulting to the historical 8-byte (1-byte for bool) field widths used
            // to compute offsets. Each field here lands in its own eightbyte: { i@0, f@8, b@16 }.
            var name = SwiftTypeName.FromModuleQualifiedName("MyLib.Legacy");
            var record = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MyLib", "Legacy"),
                SwiftTypeName = name,
                MetadataAccessor = "$s5MyLib6LegacyV",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.HasFloatFields,
                Kind = TypeRecordKind.Struct,
                InlineSize = 24,
                AbiFieldLayout = "i,f,b"
            };
            var db = CreateTypeDbWithModule("MyLib", (name, record));

            var result = TypeLowering.LowerReturnType(new NamedTypeSpec("MyLib.Legacy"), db);

            Assert.NotNull(result);
            Assert.Equal(3, result.Slots.Count);
            Assert.Equal(RegisterFile.Integer, result.Slots[0].File);
            Assert.Equal(8, result.Slots[0].ByteSize); // eb0 {bare "i"} → 8
            Assert.Equal(RegisterFile.Float, result.Slots[1].File); // eb1 {bare "f"} → its own FP register
            Assert.Equal(8, result.Slots[1].ByteSize); // bare "f" → 8
            Assert.Equal(RegisterFile.Integer, result.Slots[2].File);
            // eb2 {bare "b"} — the store width is the eightbyte span (8), not the 1-byte field. The
            // bool's 1-byte width still drives offset computation, but the lone-bool eightbyte
            // occupies a full integer register on return.
            Assert.Equal(8, result.Slots[2].ByteSize);
        }

        [Fact]
        public void LowerReturnType_FrozenStruct4Int_DirectReturn()
        {
            // Rect { x: Int, y: Int, w: Int, h: Int } → 4 integer slots, direct (at the limit)
            var name = SwiftTypeName.FromModuleQualifiedName("MyLib.Rect");
            var record = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MyLib", "Rect"),
                SwiftTypeName = name,
                MetadataAccessor = "$s5MyLib4RectV",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
                InlineSize = 32,
                AbiFieldLayout = "i,i,i,i"
            };
            var db = CreateTypeDbWithModule("MyLib", (name, record));
            var typeSpec = new NamedTypeSpec("MyLib.Rect");

            var result = TypeLowering.LowerReturnType(typeSpec, db);

            Assert.NotNull(result);
            Assert.False(result!.IsIndirect);
            Assert.Equal(4, result.Slots.Count);
            Assert.Equal(32, result.TotalByteSize);
        }

        [Fact]
        public void LowerReturnType_FrozenStruct5Int_Indirect()
        {
            // BigStruct { a, b, c, d, e: Int } → 5 integer slots → exceeds limit → indirect
            var name = SwiftTypeName.FromModuleQualifiedName("MyLib.BigStruct");
            var record = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MyLib", "BigStruct"),
                SwiftTypeName = name,
                MetadataAccessor = "$s5MyLib9BigStructV",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
                InlineSize = 40,
                AbiFieldLayout = "i,i,i,i,i"
            };
            var db = CreateTypeDbWithModule("MyLib", (name, record));
            var typeSpec = new NamedTypeSpec("MyLib.BigStruct");

            var result = TypeLowering.LowerReturnType(typeSpec, db);

            Assert.NotNull(result);
            Assert.True(result!.IsIndirect);
            Assert.Equal(5, result.Slots.Count);
            Assert.Equal(40, result.TotalByteSize);
        }

        [Fact]
        public void LowerReturnType_MixedIntFloat_2Int2Float_Direct()
        {
            // MixedStruct { x: Int, y: Double, z: Int, w: Double } → 2 int + 2 float = 4 total → direct
            var name = SwiftTypeName.FromModuleQualifiedName("MyLib.Mixed");
            var record = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MyLib", "Mixed"),
                SwiftTypeName = name,
                MetadataAccessor = "$s5MyLib5MixedV",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.HasFloatFields,
                Kind = TypeRecordKind.Struct,
                InlineSize = 32,
                AbiFieldLayout = "i,f,i,f"
            };
            var db = CreateTypeDbWithModule("MyLib", (name, record));
            var typeSpec = new NamedTypeSpec("MyLib.Mixed");

            var result = TypeLowering.LowerReturnType(typeSpec, db);

            Assert.NotNull(result);
            Assert.False(result!.IsIndirect);
            Assert.Equal(4, result.Slots.Count);
            // Verify interleaved int/float slots
            Assert.Equal(RegisterFile.Integer, result.Slots[0].File);
            Assert.Equal(0, result.Slots[0].Index);
            Assert.Equal(RegisterFile.Float, result.Slots[1].File);
            Assert.Equal(0, result.Slots[1].Index);
            Assert.Equal(RegisterFile.Integer, result.Slots[2].File);
            Assert.Equal(1, result.Slots[2].Index);
            Assert.Equal(RegisterFile.Float, result.Slots[3].File);
            Assert.Equal(1, result.Slots[3].Index);
        }

        [Fact]
        public void LowerReturnType_Mixed5Slots_3Int2Float_Indirect()
        {
            // { Int, Double, Int, Double, Int } → 3 int + 2 float = 5 total → indirect
            var name = SwiftTypeName.FromModuleQualifiedName("MyLib.Mixed5");
            var record = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MyLib", "Mixed5"),
                SwiftTypeName = name,
                MetadataAccessor = "$s5MyLib6Mixed5V",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.HasFloatFields,
                Kind = TypeRecordKind.Struct,
                InlineSize = 40,
                AbiFieldLayout = "i,f,i,f,i"
            };
            var db = CreateTypeDbWithModule("MyLib", (name, record));
            var typeSpec = new NamedTypeSpec("MyLib.Mixed5");

            var result = TypeLowering.LowerReturnType(typeSpec, db);

            Assert.NotNull(result);
            Assert.True(result!.IsIndirect);
            Assert.Equal(5, result.Slots.Count);
        }

        [Fact]
        public void LowerReturnType_FloatPair_TwoFloatSlots()
        {
            // FloatPair { x: Double, y: Double } → 2 float slots, direct
            var name = SwiftTypeName.FromModuleQualifiedName("MyLib.FloatPair");
            var record = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MyLib", "FloatPair"),
                SwiftTypeName = name,
                MetadataAccessor = "$s5MyLib9FloatPairV",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.HasFloatFields,
                Kind = TypeRecordKind.Struct,
                InlineSize = 16,
                AbiFieldLayout = "f,f"
            };
            var db = CreateTypeDbWithModule("MyLib", (name, record));
            var typeSpec = new NamedTypeSpec("MyLib.FloatPair");

            var result = TypeLowering.LowerReturnType(typeSpec, db);

            Assert.NotNull(result);
            Assert.False(result!.IsIndirect);
            Assert.Equal(2, result.Slots.Count);
            Assert.All(result.Slots, s => Assert.Equal(RegisterFile.Float, s.File));
            Assert.Equal(0, result.Slots[0].Index);
            Assert.Equal(1, result.Slots[1].Index);
        }

        [Fact]
        public void LowerReturnType_EmptyStruct_ZeroSlots()
        {
            // EmptyStruct {} → 0 slots, not indirect
            var name = SwiftTypeName.FromModuleQualifiedName("MyLib.EmptyStruct");
            var record = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MyLib", "EmptyStruct"),
                SwiftTypeName = name,
                MetadataAccessor = "$s5MyLib11EmptyStructV",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
                InlineSize = 0,
                AbiFieldLayout = null
            };
            var db = CreateTypeDbWithModule("MyLib", (name, record));
            var typeSpec = new NamedTypeSpec("MyLib.EmptyStruct");

            var result = TypeLowering.LowerReturnType(typeSpec, db);

            Assert.NotNull(result);
            Assert.False(result!.IsIndirect);
            Assert.Empty(result.Slots);
            Assert.Equal(0, result.TotalByteSize);
        }

        [Fact]
        public void LowerReturnType_NonFrozenStruct_Indirect()
        {
            // Non-frozen struct → always indirect (unknown layout)
            var name = SwiftTypeName.FromModuleQualifiedName("MyLib.DynamicStruct");
            var record = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MyLib", "DynamicStruct"),
                SwiftTypeName = name,
                MetadataAccessor = "$s5MyLib13DynamicStructV",
                Flags = TypeRecordFlags.None, // Not frozen
                Kind = TypeRecordKind.Struct,
            };
            var db = CreateTypeDbWithModule("MyLib", (name, record));
            var typeSpec = new NamedTypeSpec("MyLib.DynamicStruct");

            var result = TypeLowering.LowerReturnType(typeSpec, db);

            Assert.NotNull(result);
            Assert.True(result!.IsIndirect);
            Assert.Empty(result.Slots);
        }

        [Fact]
        public void LowerReturnType_PackedStructStraddlingEightbyte_DeclinesToCdecl()
        {
            // A PACKED { flag: Bool, value: Int } reported as "b,i" with InlineSize=9: the Int sits at
            // byte offset 1 (no alignment padding), straddling the eightbyte boundary (bytes 1-8).
            // TypeLowering reconstructs offsets from natural alignment (Bool@0, Int@8 → size 16) and
            // cross-checks against InlineSize; 16 ≠ 9 reveals the packing, so it declines (null) and
            // routes to the @_cdecl wrapper whose C-ABI call handles the straddle correctly.
            // The old model naively produced two slots assuming natural offsets and mis-bridged the
            // straddling Int.
            var name = SwiftTypeName.FromModuleQualifiedName("MyLib.BoolStruct");
            var record = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MyLib", "BoolStruct"),
                SwiftTypeName = name,
                MetadataAccessor = "$s5MyLib10BoolStructV",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.HasBoolFields,
                Kind = TypeRecordKind.Struct,
                InlineSize = 9,
                AbiFieldLayout = "b,i"
            };
            var db = CreateTypeDbWithModule("MyLib", (name, record));
            var typeSpec = new NamedTypeSpec("MyLib.BoolStruct");

            var result = TypeLowering.LowerReturnType(typeSpec, db);

            Assert.Null(result);
        }

        [Fact]
        public void LowerReturnType_NaturalBoolStruct_TwoIntegerSlots()
        {
            // A naturally-aligned { flag: Bool, value: Int } → "b,i" with InlineSize=16 (the Int's
            // 8-byte alignment pads the Bool out to offset 8). Same layout string as the packed test
            // above — only InlineSize disambiguates the two. The Bool fills eightbyte 0 alone and the
            // Int eightbyte 1; reconstructed size (16) matches InlineSize, so it lowers directly into
            // two integer registers. Each slot's store width is the eightbyte span (8), the lone Bool
            // no longer reported as a 1-byte slot.
            var name = SwiftTypeName.FromModuleQualifiedName("MyLib.BoolStructNatural");
            var record = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MyLib", "BoolStructNatural"),
                SwiftTypeName = name,
                MetadataAccessor = "$s5MyLib17BoolStructNaturalV",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.HasBoolFields,
                Kind = TypeRecordKind.Struct,
                InlineSize = 16,
                AbiFieldLayout = "b,i"
            };
            var db = CreateTypeDbWithModule("MyLib", (name, record));
            var typeSpec = new NamedTypeSpec("MyLib.BoolStructNatural");

            var result = TypeLowering.LowerReturnType(typeSpec, db);

            Assert.NotNull(result);
            Assert.False(result!.IsIndirect);
            Assert.Equal(2, result.Slots.Count);
            Assert.Equal(RegisterFile.Integer, result.Slots[0].File);
            Assert.Equal(8, result.Slots[0].ByteSize);
            Assert.Equal(RegisterFile.Integer, result.Slots[1].File);
            Assert.Equal(8, result.Slots[1].ByteSize);
        }

        [Fact]
        public void LowerReturnType_NullInlineSize_NoCrossCheck_ProceedsOnReconstruction()
        {
            // Null-InlineSize hazard pin. The SAME "b,i" frozen struct that DECLINES when InlineSize=9 reveals
            // its packing (LowerReturnType_PackedStructStraddlingEightbyte_DeclinesToCdecl) instead LOWERS
            // when InlineSize is null — the authoritative size the cross-check needs is populated from the
            // live value-witness table, which is absent on a cross-compile / device slice. With no size to
            // disagree with, the natural-aligned reconstruction (Bool@0, Int@8 → size 16) is taken on faith
            // and the struct lowers into two integer slots, byte-identical to the naturally-aligned
            // InlineSize=16 case. This pins the documented blind spot: a packed frozen struct would slip
            // through this path undetected — and pins that we do NOT add a spurious decline for the common
            // null case, since a second size operand recomputed from this same AbiFieldLayout would be
            // tautological and catch nothing.
            var name = SwiftTypeName.FromModuleQualifiedName("MyLib.BoolStructNoSize");
            var record = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MyLib", "BoolStructNoSize"),
                SwiftTypeName = name,
                MetadataAccessor = "$s5MyLib16BoolStructNoSizeV",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.HasBoolFields,
                Kind = TypeRecordKind.Struct,
                InlineSize = null, // no live metadata cross-compile → cross-check necessarily skipped
                AbiFieldLayout = "b,i"
            };
            var db = CreateTypeDbWithModule("MyLib", (name, record));
            var typeSpec = new NamedTypeSpec("MyLib.BoolStructNoSize");

            var result = TypeLowering.LowerReturnType(typeSpec, db);

            Assert.NotNull(result);
            Assert.False(result!.IsIndirect);
            Assert.Equal(16, result.TotalByteSize); // reconstruction, not a default — the size is real
            Assert.Equal(2, result.Slots.Count);
            Assert.Equal(RegisterFile.Integer, result.Slots[0].File);
            Assert.Equal(8, result.Slots[0].ByteSize);
            Assert.Equal(RegisterFile.Integer, result.Slots[1].File);
            Assert.Equal(8, result.Slots[1].ByteSize);
        }

        [Fact]
        public void LowerReturnType_StructWithPointerField_IntegerSlot()
        {
            // PointerStruct { ptr: OpaquePointer, value: Int } → 2 integer slots
            var name = SwiftTypeName.FromModuleQualifiedName("MyLib.PtrStruct");
            var record = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MyLib", "PtrStruct"),
                SwiftTypeName = name,
                MetadataAccessor = "$s5MyLib9PtrStructV",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
                InlineSize = 16,
                AbiFieldLayout = "p,i"
            };
            var db = CreateTypeDbWithModule("MyLib", (name, record));
            var typeSpec = new NamedTypeSpec("MyLib.PtrStruct");

            var result = TypeLowering.LowerReturnType(typeSpec, db);

            Assert.NotNull(result);
            Assert.False(result!.IsIndirect);
            Assert.Equal(2, result.Slots.Count);
            Assert.All(result.Slots, s => Assert.Equal(RegisterFile.Integer, s.File));
        }

        #endregion

        #region Class Types

        [Fact]
        public void LowerReturnType_Class_SingleIntegerSlot()
        {
            var name = SwiftTypeName.FromModuleQualifiedName("MyLib.Manager");
            var record = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MyLib", "Manager"),
                SwiftTypeName = name,
                MetadataAccessor = "$s5MyLib7ManagerC",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class,
            };
            var db = CreateTypeDbWithModule("MyLib", (name, record));
            var typeSpec = new NamedTypeSpec("MyLib.Manager");

            var result = TypeLowering.LowerReturnType(typeSpec, db);

            Assert.NotNull(result);
            Assert.False(result!.IsIndirect);
            Assert.Single(result.Slots);
            Assert.Equal(RegisterFile.Integer, result.Slots[0].File);
            Assert.Equal(8, result.TotalByteSize);
        }

        #endregion

        #region Enum Types

        [Fact]
        public void LowerReturnType_SimpleEnum_SingleIntegerSlot()
        {
            var name = SwiftTypeName.FromModuleQualifiedName("MyLib.Color");
            var record = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MyLib", "Color"),
                SwiftTypeName = name,
                MetadataAccessor = "$s5MyLib5ColorO",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum,
                Kind = TypeRecordKind.Enum,
                RawValueTypeName = "Int",
                InlineSize = 8,
            };
            var db = CreateTypeDbWithModule("MyLib", (name, record));
            var typeSpec = new NamedTypeSpec("MyLib.Color");

            var result = TypeLowering.LowerReturnType(typeSpec, db);

            Assert.NotNull(result);
            Assert.False(result!.IsIndirect);
            Assert.Single(result.Slots);
            Assert.Equal(RegisterFile.Integer, result.Slots[0].File);
        }

        [Fact]
        public void LowerReturnType_ComplexEnum_ReturnsNull()
        {
            // Enum with associated values — can't lower
            var name = SwiftTypeName.FromModuleQualifiedName("MyLib.Result");
            var record = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MyLib", "Result"),
                SwiftTypeName = name,
                MetadataAccessor = "$s5MyLib6ResultO",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Enum,
            };
            var db = CreateTypeDbWithModule("MyLib", (name, record));
            var typeSpec = new NamedTypeSpec("MyLib.Result");

            var result = TypeLowering.LowerReturnType(typeSpec, db);

            Assert.Null(result);
        }

        [Fact]
        public void LowerReturnType_SimpleFrozenEnum_NullInlineSize_DeclinesToCdecl()
        {
            // A simple frozen enum whose InlineSize is unknown (null — e.g. the
            // iOS-on-macOS cross-compile path where the metadata accessor can't be dlopened)
            // must DECLINE (return null → route to @_cdecl) rather than fabricate an 8-byte
            // default. The old `record.InlineSize ?? 8` shipped a wrong 8-byte TotalByteSize
            // for what is usually a 1-byte enum — a fabricated layout, not a measured one.
            var name = SwiftTypeName.FromModuleQualifiedName("MyLib.Tag");
            var record = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MyLib", "Tag"),
                SwiftTypeName = name,
                MetadataAccessor = "$s5MyLib3TagO",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum,
                Kind = TypeRecordKind.Enum,
                RawValueTypeName = "Int",
                InlineSize = null, // size unknown — must not default to 8
            };
            var db = CreateTypeDbWithModule("MyLib", (name, record));
            var typeSpec = new NamedTypeSpec("MyLib.Tag");

            var result = TypeLowering.LowerReturnType(typeSpec, db);

            Assert.Null(result);
        }

        [Fact]
        public void LowerReturnType_NonFrozenSimpleEnum_ReturnsNull()
        {
            // Non-frozen simple enums are passed indirectly in Swift ABI (resilient layout).
            // TypeLowering must NOT lower them as direct register values — the thunk would
            // pass the value directly in x0 but the Swift function dereferences x0 as a
            // pointer → SIGSEGV. (Non-frozen simple-enum indirect-ABI crash.)
            var name = SwiftTypeName.FromModuleQualifiedName("MyLib.Accessibility");
            var record = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MyLib", "Accessibility"),
                SwiftTypeName = name,
                MetadataAccessor = "$s5MyLib13AccessibilityO",
                Flags = TypeRecordFlags.SimpleEnum, // SimpleEnum but NOT Frozen
                Kind = TypeRecordKind.Enum,
                InlineSize = 1,
            };
            var db = CreateTypeDbWithModule("MyLib", (name, record));
            var typeSpec = new NamedTypeSpec("MyLib.Accessibility");

            var result = TypeLowering.LowerReturnType(typeSpec, db);

            Assert.Null(result);
        }

        #endregion

        #region Optional Types

        [Fact]
        public void LowerReturnType_OptionalInt_TwoIntegerSlots()
        {
            // Optional<Int> = Int (1 slot) + tag (1 slot) = 2 integer slots
            var typeSpec = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.Int"));
            var db = new TypeDatabase();

            var result = TypeLowering.LowerReturnType(typeSpec, db);

            Assert.NotNull(result);
            Assert.False(result!.IsIndirect);
            Assert.Equal(2, result.Slots.Count);
            Assert.All(result.Slots, s => Assert.Equal(RegisterFile.Integer, s.File));
            // First slot is the Int value, second is the tag
            Assert.Equal(8, result.Slots[0].ByteSize);
            Assert.Equal(1, result.Slots[1].ByteSize); // tag byte
        }

        [Fact]
        public void LowerReturnType_OptionalClass_SingleIntegerSlot()
        {
            // Optional<class> = nullable pointer, no tag needed
            var name = SwiftTypeName.FromModuleQualifiedName("MyLib.Widget");
            var record = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MyLib", "Widget"),
                SwiftTypeName = name,
                MetadataAccessor = "$s5MyLib6WidgetC",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class,
            };
            var db = CreateTypeDbWithModule("MyLib", (name, record));
            var typeSpec = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("MyLib.Widget"));

            var result = TypeLowering.LowerReturnType(typeSpec, db);

            Assert.NotNull(result);
            Assert.False(result!.IsIndirect);
            Assert.Single(result.Slots);
            Assert.Equal(RegisterFile.Integer, result.Slots[0].File);
            Assert.Equal(8, result.TotalByteSize);
        }

        [Fact]
        public void LowerReturnType_OptionalDouble_IntAndFloatSlots()
        {
            // Optional<Double> = Double (1 float slot) + tag (1 int slot)
            var typeSpec = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.Double"));
            var db = new TypeDatabase();

            var result = TypeLowering.LowerReturnType(typeSpec, db);

            Assert.NotNull(result);
            Assert.False(result!.IsIndirect);
            Assert.Equal(2, result.Slots.Count);
            Assert.Equal(RegisterFile.Float, result.Slots[0].File);
            Assert.Equal(RegisterFile.Integer, result.Slots[1].File);
        }

        // R6-4: the register oracle (LowerOptional) used to fabricate an over-wide
        // `{inner}+tag` layout for EVERY non-class value payload. But Swift only appends a
        // 1-byte discriminator tag to fixed-width int/float scalars (covered above) — Bool,
        // pointers, and frozen simple enums fold `.none` into a spare inhabitant, keeping the
        // inner type's size with NO tag. The thunk path forwards raw registers and has no
        // spare-inhabitant encode/decode layer, so these must DECLINE (null → route to a
        // @_cdecl wrapper that does encode them), matching the field oracle
        // (ModuleProcessor.ClassifyFieldType) via the shared SwiftValueLayout. Before the
        // fix each of these returned a 2-slot result, inflating the size by a slot and a byte.

        [Fact]
        public void LowerReturnType_OptionalBool_DeclinesToCdecl()
        {
            // Optional<Bool> keeps Bool's 1-byte size (`.none` == byte 2, a spare inhabitant) —
            // no appended tag. The register oracle must decline rather than fabricate {b,i1}.
            var typeSpec = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.Bool"));
            var db = new TypeDatabase();

            var result = TypeLowering.LowerReturnType(typeSpec, db);

            Assert.Null(result);
        }

        [Theory]
        [InlineData("Swift.OpaquePointer")]
        [InlineData("Swift.UnsafeRawPointer")]
        [InlineData("Swift.UnsafeMutableRawPointer")]
        public void LowerReturnType_OptionalPointer_DeclinesToCdecl(string pointerType)
        {
            // A nullable pointer uses 0 for `.none` (a spare inhabitant) and keeps the 8-byte
            // pointer size — no tag. SwiftOptional models nullable pointers with an appended
            // tag (nint?-style), which does NOT match Swift's nullable-pointer ABI, so the
            // thunk path can't lower it; decline.
            var typeSpec = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec(pointerType));
            var db = new TypeDatabase();

            var result = TypeLowering.LowerReturnType(typeSpec, db);

            Assert.Null(result);
        }

        [Fact]
        public void LowerReturnType_OptionalSimpleEnum_DeclinesToCdecl()
        {
            // Optional<frozen simple enum> uses the case-count sentinel for `.none` (a spare
            // inhabitant) and keeps the enum's size — no tag. Decline.
            var name = SwiftTypeName.FromModuleQualifiedName("MyLib.Color");
            var record = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("MyLib", "Color"),
                SwiftTypeName = name,
                MetadataAccessor = "$s5MyLib5ColorO",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum,
                Kind = TypeRecordKind.Enum,
                RawValueTypeName = "Int",
                InlineSize = 1,
            };
            var db = CreateTypeDbWithModule("MyLib", (name, record));
            var typeSpec = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("MyLib.Color"));

            var result = TypeLowering.LowerReturnType(typeSpec, db);

            Assert.Null(result);
        }

        [Fact]
        public void LowerReturnType_OptionalInt32_TagAddingScalarStillLowers()
        {
            // Contrast guard: a fixed-width scalar that genuinely DOES gain a tag must still
            // lower (Int32 = 4 bytes + 1-byte tag = 2 slots). The fix narrows the path to
            // tag-adding scalars; it must not over-decline them.
            var typeSpec = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.Int32"));
            var db = new TypeDatabase();

            var result = TypeLowering.LowerReturnType(typeSpec, db);

            Assert.NotNull(result);
            Assert.False(result!.IsIndirect);
            Assert.Equal(2, result.Slots.Count);
            Assert.All(result.Slots, s => Assert.Equal(RegisterFile.Integer, s.File));
            Assert.Equal(4, result.Slots[0].ByteSize);
            Assert.Equal(1, result.Slots[1].ByteSize); // tag byte
            Assert.Equal(5, result.TotalByteSize);
        }

        #endregion

        #region Edge Cases

        [Fact]
        public void LowerReturnType_UnknownType_ReturnsNull()
        {
            var typeSpec = new NamedTypeSpec("Unknown.Module.Type");
            var db = new TypeDatabase();

            var result = TypeLowering.LowerReturnType(typeSpec, db);

            Assert.Null(result);
        }

        [Fact]
        public void LowerReturnType_GenericTypeWithoutSpecialHandling_ReturnsNull()
        {
            var typeSpec = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.Int"));
            var db = new TypeDatabase();

            var result = TypeLowering.LowerReturnType(typeSpec, db);

            Assert.Null(result);
        }

        [Fact]
        public void LowerReturnType_FrozenStructNoLayout_ReturnsNull()
        {
            // Frozen struct without AbiFieldLayout (e.g., cross-module with no persisted layout)
            // Must return null so the function falls back to @_cdecl
            var name = SwiftTypeName.FromModuleQualifiedName("Ext.Token");
            var record = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Ext", "Token"),
                SwiftTypeName = name,
                MetadataAccessor = "$s3Ext5TokenV",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
                InlineSize = 16,
                AbiFieldLayout = null // No layout info
            };
            var db = CreateTypeDbWithModule("Ext", (name, record));
            var typeSpec = new NamedTypeSpec("Ext.Token");

            var result = TypeLowering.LowerReturnType(typeSpec, db);

            Assert.Null(result);
        }

        [Fact]
        public void LowerParameterType_SameAsReturn_ForScalar()
        {
            // LowerParameterType follows same rules as LowerReturnType
            var typeSpec = new NamedTypeSpec("Swift.Int");
            var db = new TypeDatabase();

            var returnResult = TypeLowering.LowerReturnType(typeSpec, db);
            var paramResult = TypeLowering.LowerParameterType(typeSpec, db);

            Assert.NotNull(returnResult);
            Assert.NotNull(paramResult);
            Assert.Equal(returnResult!.Slots.Count, paramResult!.Slots.Count);
            Assert.Equal(returnResult.IsIndirect, paramResult.IsIndirect);
            Assert.Equal(returnResult.TotalByteSize, paramResult.TotalByteSize);
        }

        [Fact]
        public void LowerReturnType_ExistentialType_ReturnsNull()
        {
            // any Protocol — existential container, can't be lowered to registers
            var typeSpec = new NamedTypeSpec("MyLib.SomeProtocol") { IsAny = true };
            var db = new TypeDatabase();

            var result = TypeLowering.LowerReturnType(typeSpec, db);

            Assert.Null(result);
        }

        [Theory]
        [InlineData("Self")]
        [InlineData("T")]
        [InlineData("Element")]
        public void LowerReturnType_UnqualifiedTypeName_ReturnsNull(string typeName)
        {
            // Unqualified type names like "Self", "T", "Element" have no module prefix
            // and must return null (not crash) since they can't be resolved
            var typeSpec = new NamedTypeSpec(typeName);
            var db = new TypeDatabase();

            var result = TypeLowering.LowerReturnType(typeSpec, db);

            Assert.Null(result);
        }

        [Fact]
        public void LowerReturnType_OptionalSelf_ReturnsNull()
        {
            // Optional<Self> — unqualified inner type must not crash
            var typeSpec = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Self"));
            var db = new TypeDatabase();

            var result = TypeLowering.LowerReturnType(typeSpec, db);

            Assert.Null(result);
        }

        #endregion

        #region ABI Field Layout Round-Trip (ModuleDatabase)

        [Fact]
        public async Task AbiFieldLayout_PersistsAndRoundTrips()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"abi_layout_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var module = new ModuleTypeDatabase("TestLib", "/fake/TestLib.dylib");
                var swiftName = SwiftTypeName.FromModuleQualifiedName("TestLib.Point");
                var record = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestLib", "Point"),
                    SwiftTypeName = swiftName,
                    MetadataAccessor = "$s7TestLib5PointV",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct,
                    InlineSize = 16,
                    AbiFieldLayout = "i,i"
                };
                module.RegisterType(swiftName, record);

                var path = ModuleDatabaseEmitter.Emit(module, dir, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
                Assert.NotNull(path);

                var typeDatabase = new TypeDatabase();
                await typeDatabase.LoadModuleDatabaseFromFile(path!);

                Assert.True(typeDatabase.TryGetTypeRecord(swiftName, out var loaded));
                Assert.Equal("i,i", loaded!.AbiFieldLayout);

                // Verify the loaded layout can be used for type lowering
                var typeSpec = new NamedTypeSpec("TestLib.Point");
                var result = TypeLowering.LowerReturnType(typeSpec, typeDatabase);

                Assert.NotNull(result);
                Assert.False(result!.IsIndirect);
                Assert.Equal(2, result.Slots.Count);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task AbiFieldLayout_MixedIntFloat_RoundTrips()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"abi_mixed_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var module = new ModuleTypeDatabase("TestLib", "/fake/TestLib.dylib");
                var swiftName = SwiftTypeName.FromModuleQualifiedName("TestLib.Particle");
                var record = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestLib", "Particle"),
                    SwiftTypeName = swiftName,
                    MetadataAccessor = "$s7TestLib8ParticleV",
                    Flags = TypeRecordFlags.Frozen | TypeRecordFlags.HasFloatFields,
                    Kind = TypeRecordKind.Struct,
                    InlineSize = 32,
                    AbiFieldLayout = "f,f,i,i"
                };
                module.RegisterType(swiftName, record);

                var path = ModuleDatabaseEmitter.Emit(module, dir, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
                Assert.NotNull(path);

                var typeDatabase = new TypeDatabase();
                await typeDatabase.LoadModuleDatabaseFromFile(path!);

                Assert.True(typeDatabase.TryGetTypeRecord(swiftName, out var loaded));
                Assert.Equal("f,f,i,i", loaded!.AbiFieldLayout);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task AbiFieldLayout_NullForNonFrozen_NoAttribute()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"abi_nonfrozen_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var module = new ModuleTypeDatabase("TestLib", "/fake/TestLib.dylib");
                var swiftName = SwiftTypeName.FromModuleQualifiedName("TestLib.DynObj");
                var record = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestLib", "DynObj"),
                    SwiftTypeName = swiftName,
                    MetadataAccessor = "$s7TestLib6DynObjV",
                    Flags = TypeRecordFlags.None,
                    Kind = TypeRecordKind.Struct,
                    AbiFieldLayout = null // Not frozen, no layout
                };
                module.RegisterType(swiftName, record);

                var path = ModuleDatabaseEmitter.Emit(module, dir, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
                Assert.NotNull(path);

                var typeDatabase = new TypeDatabase();
                await typeDatabase.LoadModuleDatabaseFromFile(path!);

                Assert.True(typeDatabase.TryGetTypeRecord(swiftName, out var loaded));
                Assert.Null(loaded!.AbiFieldLayout);
            }
            finally { Directory.Delete(dir, true); }
        }

        #endregion

        #region MaxDirectSlots Boundary

        [Theory]
        [InlineData("i,i,i,i", false)]      // 4 slots = direct
        [InlineData("i,i,i,i,i", true)]      // 5 slots = indirect
        [InlineData("f,f,f,f", false)]        // 4 float slots = direct
        [InlineData("f,f,f,f,f", true)]       // 5 float slots = indirect
        [InlineData("i,f,i,f", false)]        // 2+2 = 4 = direct
        [InlineData("i,f,i,f,i", true)]       // 3+2 = 5 = indirect
        public void LowerReturnType_SlotBoundary_CorrectIndirectness(string layout, bool expectedIndirect)
        {
            var name = SwiftTypeName.FromModuleQualifiedName("Test.S");
            int fieldCount = layout.Split(',').Length;
            var record = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Test", "S"),
                SwiftTypeName = name,
                MetadataAccessor = "$s4Test1SV",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
                InlineSize = fieldCount * 8,
                AbiFieldLayout = layout
            };
            var db = CreateTypeDbWithModule("Test", (name, record));
            var typeSpec = new NamedTypeSpec("Test.S");

            var result = TypeLowering.LowerReturnType(typeSpec, db);

            Assert.NotNull(result);
            Assert.Equal(expectedIndirect, result!.IsIndirect);
            Assert.Equal(fieldCount, result.Slots.Count);
        }

        #endregion
    }

    // Extension for test readability
    internal static class TypeLoweringResultExtensions
    {
        internal static int InlineSize(this TypeLoweringResult result)
        {
            return result.Slots.Sum(s => s.ByteSize);
        }
    }
}
