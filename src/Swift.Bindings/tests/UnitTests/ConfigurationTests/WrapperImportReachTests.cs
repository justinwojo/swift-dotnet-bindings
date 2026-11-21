// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// Which imports each export of a linked wrapper reaches, read from its disassembly, symbols and
    /// fixups: calls through stubs, local calls and tail branches, GOT loads, data objects with binds,
    /// and the fail-closed list of imports no export reaches.
    /// </summary>
    public class WrapperImportReachTests
    {
        private const string Sections = """
            Sections:
            Idx Name             Size     VMA              Type
              0 __text           00000100 0000000000001000 TEXT
              1 __stubs          00000020 0000000000001100 TEXT
              2 __cstring        00000020 0000000000002000 DATA
              3 __got            00000020 0000000000003000 DATA
              4 __const          00000040 0000000000004000 DATA
            """;

        private const string Symbols = """
            0000000000001000 T _SBW_direct
            0000000000001010 T _SBW_viaLocal
            0000000000001020 t _local
            0000000000001030 T _SBW_got
            0000000000001040 T _SBW_table
            0000000000001050 t _orphan
            0000000000001060 T _SBW_string
            0000000000001070 T _SBW_tail
            0000000000004000 s _table
                             U _$sAbsentA
            """;

        private const string Disassembly = """
            /path/Wrapper:
            (__TEXT,__text) section
            _SBW_direct:
                1000:	bl	0x1100 ; symbol stub for: _$sAbsentA
                1004:	ret
            _SBW_viaLocal:
                1010:	bl	_local
                1014:	ret
            _local:
                1020:	bl	0x1108 ; symbol stub for: _$sAbsentB
                1024:	ret
            _SBW_got:
                1030:	adrp	x8, 2 ; 0x3000
                1034:	ldr	x8, [x8, #0x8]
                1038:	blr	x8
            _SBW_table:
                1040:	adrp	x0, 3 ; 0x4000
                1044:	add	x0, x0, #0x8
                1048:	ret
            _orphan:
                1050:	adrp	x8, 2 ; 0x3000
                1054:	ldr	x8, [x8, #0x10]
                1058:	blr	x8
            _SBW_string:
                1060:	adrp	x1, 1 ; 0x2000
                1064:	add	x1, x1, #0x4 ; literal pool for: "hello"
                1068:	ret
            _SBW_tail:
                1070:	b	0x1020
            """;

        private const string Fixups = """
            /path/Wrapper [arm64]:
                -fixups:
                    segment         section          address             type   target
                    __DATA_CONST    __got            0x00003008           bind  Lib/_$sAbsentC [weak-import]
                    __DATA_CONST    __got            0x00003010           bind  Lib/_$sAbsentD [weak-import]
                    __DATA_CONST    __got            0x00003018           bind  Lib/_$sAbsentE [weak-import]
                    __DATA_CONST    __const          0x00004010           bind  Lib/_$sAbsentF [weak-import]
                    __DATA_CONST    __const          0x00004018         rebase  0x00001020
            """;

        private static WrapperImportReach Reach() =>
            WrapperImportReach.Parse(Lines(Sections), Lines(Symbols), Lines(Disassembly), Lines(Fixups));

        private static string[] Lines(string text) => text.Split('\n');

        [Theory]
        [InlineData("_SBW_direct", "_$sAbsentA")]
        [InlineData("_SBW_viaLocal", "_$sAbsentB")]
        [InlineData("_SBW_tail", "_$sAbsentB")]
        [InlineData("_SBW_got", "_$sAbsentC")]
        [InlineData("_SBW_table", "_$sAbsentF")]
        [InlineData("_SBW_table", "_$sAbsentB")]
        public void AnExportReachesTheImportsOfEverythingItCallsLoadsOrPointsAt(string export, string import)
        {
            Assert.Contains(import, Reach().ImportsReachedFrom(export));
        }

        [Fact]
        public void AnExportReachesNothingItDoesNotReference()
        {
            var reach = Reach();
            Assert.Equal(new[] { "_$sAbsentA" }, reach.ImportsReachedFrom("_SBW_direct").ToArray());
            // A string literal after the last function is not part of it.
            Assert.Empty(reach.ImportsReachedFrom("_SBW_string"));
            Assert.Empty(reach.ImportsReachedFrom("_SBW_missing"));
        }

        [Fact]
        public void AnImportNoExportReaches_IsUnattributed_WhetherACodePathNothingOrNoReadReferencesIt()
        {
            // _$sAbsentG is imported, but nothing the read of code and fixups follows names it.
            var unattributed = Reach().Unattributed(new[]
            {
                "_$sAbsentA", "_$sAbsentB", "_$sAbsentC", "_$sAbsentD", "_$sAbsentE", "_$sAbsentF", "_$sAbsentG",
            });

            Assert.Equal(new[] { "_$sAbsentD", "_$sAbsentE", "_$sAbsentG" }, unattributed.Select(u => u.Import).ToArray());
            Assert.Equal(new[] { "_orphan" }, unattributed[0].Holders.ToArray());
            Assert.Equal(new[] { "<unloaded GOT>" }, unattributed[1].Holders.ToArray());
            Assert.Empty(unattributed[2].Holders);
        }

        [Fact]
        public void ExportsAreTheExternalSymbols()
        {
            var reach = Reach();
            Assert.Contains("_SBW_direct", reach.Exports);
            Assert.DoesNotContain("_local", reach.Exports);
            Assert.DoesNotContain("_table", reach.Exports);
        }
    }
}
