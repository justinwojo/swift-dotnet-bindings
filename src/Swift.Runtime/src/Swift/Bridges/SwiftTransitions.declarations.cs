// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// The runtime's Swift-convention imports that pass a result indirectly. This file is not compiled:
// it is the input `nuke swift-bridge-products` routes, with the runtime's transition table and the
// swiftc lowering evidence, into SwiftTransitions.g.cs (compiled) and the bridge assemblies
// SwiftBindingsRuntimeBridges.{arm64,x86_64}.s, which build-runtime.sh links into every
// SwiftBindingsRuntime.framework slice. Each import the evidence proves becomes a managed shim with
// the import's own name and signature, calling a C-convention twin of its SBSR_ bridge, so no
// interpreter or JIT ever lowers the Swift convention for it. An import that is not proven, or that
// the shim cannot reproduce, is compiled exactly as declared here and reported.
//
// Edit the declarations here, never the generated file, and rerun the target.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Swift;
using Swift.Runtime;

namespace Swift;

internal static partial class SwiftArrayPInvokes
{
    // Array subscript getter: $sSayxSicig. Non-mutating: self is the storage pointer.
    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$sSayxSicig")]
    public static extern void Get(SwiftIndirectResult result, nint index, IntPtr handle, TypeMetadata elementMetadata);
}

internal static partial class SwiftDictionaryPInvokes
{
    // Dictionary subscript getter: $sSDyq_Sgxcig (subscript getter returns Optional<Value>)
    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$sSDyq_Sgxcig")]
    public static extern void Get(
        SwiftIndirectResult result,
        IntPtr key,
        IntPtr handle,
        TypeMetadata keyTypeMetadata,
        TypeMetadata valueTypeMetadata,
        ProtocolWitnessTable witnessTable);

    // Dictionary.makeIterator(): $sSD12makeIteratorSD0B0Vyxq__GyF
    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$sSD12makeIteratorSD0B0Vyxq__GyF")]
    public static extern void MakeIterator(
        SwiftIndirectResult result,
        IntPtr handle,
        TypeMetadata keyTypeMetadata,
        TypeMetadata valueTypeMetadata,
        ProtocolWitnessTable witnessTable);
}

internal static partial class SwiftSetPInvokes
{
    // Set.makeIterator(): $sSh12makeIteratorSh0B0Vyx_GyF
    // SIL: (@owned Set<Element>) -> @owned Set<Element>.Iterator
    // Non-mutating: self as handle, generic context as element metadata + witness table
    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$sSh12makeIteratorSh0B0Vyx_GyF")]
    public static extern void MakeIterator(SwiftIndirectResult result, IntPtr handle, TypeMetadata elementMetadata, ProtocolWitnessTable witnessTable);
}
