// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;

namespace RuntimeTestsApp.Infrastructure;

/// <summary>
/// Per-class execution-lane witness for the .NET 11 runtime lanes (<c>--lane-identity</c>).
/// </summary>
/// <remarks>
/// Calls the Swift <c>laneProbeCallerAddress()</c> through the generated binding. Swift returns the
/// return address of the code that made that transition, and <c>dladdr</c> names its image and
/// symbol: the CoreCLR interpreter's call stub (<c>CallJittedMethod*</c>), the app's ReadyToRun
/// composite (<c>&lt;App&gt;.r2r.dylib</c>) or the NativeAOT executable. The host checks every
/// class's witness against the lane it built, because the process banner reads the same for the
/// interpreter and ReadyToRun.
/// </remarks>
internal static unsafe class LaneProbe
{
    [StructLayout(LayoutKind.Sequential)]
    struct DlInfo
    {
        public IntPtr FileName;
        public IntPtr FileBase;
        public IntPtr SymbolName;
        public IntPtr SymbolAddress;
    }

    [DllImport("/usr/lib/libSystem.dylib", EntryPoint = "dladdr")]
    static extern int DlAddr(IntPtr address, DlInfo* info);

    /// <param name="ImageBase">Where the caller's image is loaded. A device build strips local
    /// symbols, so <paramref name="Symbol"/> is only the nearest exported one there; the host
    /// re-resolves the caller's offset from this base against the unstripped build copy.</param>
    public readonly record struct Witness(string Image, string? Symbol, ulong Address, ulong ImageBase);

    public static Witness Capture()
    {
        var address = (ulong)SwiftBindingsTestLib.Functions.GetLaneProbeCallerAddress();
        if (address == 0)
            return new Witness("?", null, 0, 0);
        DlInfo info;
        // A return address can sit one past the end of its caller; resolve the byte before it.
        if (DlAddr((IntPtr)(long)(address - 1), &info) == 0)
            return new Witness("?", null, address, 0);
        var path = info.FileName == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(info.FileName);
        var image = path == null ? "?" : Path.GetFileName(path);
        var symbol = info.SymbolName == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(info.SymbolName);
        return new Witness(image, symbol, address, (ulong)(long)info.FileBase);
    }

    /// <summary>Records the witness for <paramref name="className"/> in the console and the JSONL results.</summary>
    public static void Record(string className, TestResults results)
    {
        Witness witness;
        try
        {
            witness = Capture();
        }
        catch (Exception ex)
        {
            // An unrecorded witness fails the class's identity on the host; say why here.
            TestLogger.Error($"Lane witness for {className} failed: {ex.GetType().Name}: {ex.Message}");
            return;
        }
        TestLogger.Info($"Lane witness: {className} {witness.Image}!{witness.Symbol ?? "-"} 0x{witness.Address:x} (image base 0x{witness.ImageBase:x})");
        results.RecordLaneWitness(className, witness.Image, witness.Symbol, witness.Address, witness.ImageBase);
    }
}
