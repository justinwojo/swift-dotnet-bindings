// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;

namespace Swift.Runtime
{
    /// <summary>
    /// The single audit point for the Swift mangled-name symbolic-reference byte grammar used by
    /// the metadata field-record parser (<see cref="FieldRecord"/>).
    ///
    /// A Swift mangled name embedded in type metadata can splice in a "symbolic reference": a
    /// control byte that stands in for a relative or absolute pointer to a context descriptor,
    /// followed by that pointer's bytes, instead of the spelled-out name. Per
    /// https://github.com/apple/swift/blob/main/docs/ABI/Mangling.rst a control byte in
    /// <c>0x01–0x17</c> introduces a 4-byte RELATIVE reference and one in <c>0x18–0x1F</c>
    /// introduces a pointer-sized ABSOLUTE reference; every other non-NUL byte is an ordinary
    /// name character and <c>0x00</c> terminates.
    ///
    /// Before this type those four magic bounds were duplicated as bare literals across four
    /// sites in <see cref="FieldRecord"/> (component classification, byte-stride, and the two
    /// resolution walks), so an Apple grammar change had no single place to audit. Folding them
    /// into named constants here mirrors the Finding 60 mangling-probe consolidation and lets a
    /// host-side golden test (<c>SymbolicReferenceGrammarTests</c>) pin the whole classification
    /// against synthetic blobs without walking live metadata.
    ///
    /// NOTE: this is the metadata field-record grammar, NOT the demangler's symbolic-reference
    /// handling. <c>Swift5Demangler.DemangleOperator</c> dispatches control bytes <c>\x01–\x04</c>
    /// to a separate resolver keyed on symbolic-reference KIND (context direct/indirect); that is
    /// a different concern and intentionally stays independent of this byte-range classifier.
    /// </summary>
    internal static class SymbolicReferenceGrammar
    {
        /// <summary>Inclusive low bound of the relative (4-byte) symbolic-reference control bytes.</summary>
        internal const byte RelativeRangeMin = 0x01;

        /// <summary>Inclusive high bound of the relative (4-byte) symbolic-reference control bytes.</summary>
        internal const byte RelativeRangeMax = 0x17;

        /// <summary>Inclusive low bound of the absolute (pointer-sized) symbolic-reference control bytes.</summary>
        internal const byte AbsoluteRangeMin = 0x18;

        /// <summary>Inclusive high bound of the absolute (pointer-sized) symbolic-reference control bytes.</summary>
        internal const byte AbsoluteRangeMax = 0x1F;

        /// <summary>
        /// Component classification of a mangled-name byte, matching the three cases the
        /// field-record walk branches on.
        /// </summary>
        internal enum Component
        {
            /// <summary>The <c>0x00</c> terminator.</summary>
            Null = 0,

            /// <summary>A symbolic-reference control byte (relative or absolute).</summary>
            SymbolicReference = 1,

            /// <summary>An ordinary mangled-name character.</summary>
            Normal = 2,
        }

        /// <summary>True if <paramref name="b"/> introduces a relative (4-byte) symbolic reference.</summary>
        internal static bool IsRelative(byte b) => b >= RelativeRangeMin && b <= RelativeRangeMax;

        /// <summary>True if <paramref name="b"/> introduces an absolute (pointer-sized) symbolic reference.</summary>
        internal static bool IsAbsolute(byte b) => b >= AbsoluteRangeMin && b <= AbsoluteRangeMax;

        /// <summary>True if <paramref name="b"/> introduces a symbolic reference of either form.</summary>
        internal static bool IsSymbolicReference(byte b) => IsRelative(b) || IsAbsolute(b);

        /// <summary>
        /// Classifies a mangled-name byte as terminator, symbolic reference, or normal character.
        /// </summary>
        internal static Component ComponentOf(byte b)
        {
            if (b == 0)
                return Component.Null;
            if (IsSymbolicReference(b))
                return Component.SymbolicReference;
            return Component.Normal;
        }

        /// <summary>
        /// The number of bytes a single mangled-name component occupies, including the control
        /// byte: <c>0</c> for the terminator, <c>1 + 4</c> for a relative reference,
        /// <c>1 + sizeof(pointer)</c> for an absolute reference, and <c>1</c> for a normal byte.
        /// The absolute width tracks the runtime pointer size (8 on the 64-bit targets we ship).
        /// </summary>
        internal static int ByteLength(byte b)
        {
            switch (ComponentOf(b))
            {
                case Component.Null:
                    return 0;
                case Component.SymbolicReference:
                    return IsRelative(b) ? 1 + sizeof(int) : 1 + IntPtr.Size;
                default:
                    return 1;
            }
        }
    }
}
