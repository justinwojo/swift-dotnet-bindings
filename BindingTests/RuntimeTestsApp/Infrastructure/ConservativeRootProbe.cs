// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Swift.Runtime;

namespace RuntimeTestsApp.Infrastructure;

/// <summary>
/// DIAGNOSTIC. Finds pointer-shaped words on thread stacks (and in other threads' registers) that
/// fall inside a given managed object's memory, which is exactly what a conservative stack scan
/// treats as a root. Used on a leak probe's failure path to say which thread, if any, pins a
/// survivor. Only meaningful on Mono's conservative collector; a precise, moving GC (CoreCLR,
/// NativeAOT) has no conservative roots, and an object's address can change during the scan.
/// </summary>
internal static unsafe class ConservativeRootProbe
{
    private const string LibSystem = "/usr/lib/libSystem.dylib";

    [DllImport(LibSystem, EntryPoint = "pthread_self")]
    private static extern IntPtr PthreadSelf();

    [DllImport(LibSystem, EntryPoint = "pthread_get_stackaddr_np")]
    private static extern IntPtr PthreadGetStackAddr(IntPtr thread);

    [DllImport(LibSystem, EntryPoint = "pthread_get_stacksize_np")]
    private static extern nuint PthreadGetStackSize(IntPtr thread);

    [DllImport(LibSystem, EntryPoint = "pthread_getname_np")]
    private static extern int PthreadGetName(IntPtr thread, byte* buffer, nuint length);

    [DllImport(LibSystem, EntryPoint = "pthread_from_mach_thread_np")]
    private static extern IntPtr PthreadFromMachThread(uint port);

    [DllImport(LibSystem, EntryPoint = "pthread_mach_thread_np")]
    private static extern uint PthreadMachThread(IntPtr thread);

    [DllImport(LibSystem, EntryPoint = "task_threads")]
    private static extern int TaskThreads(uint task, IntPtr* threads, uint* count);

    [DllImport(LibSystem, EntryPoint = "thread_get_state")]
    private static extern int ThreadGetState(uint thread, int flavor, nuint* state, uint* count);

    [DllImport(LibSystem, EntryPoint = "mach_port_deallocate")]
    private static extern int MachPortDeallocate(uint task, uint name);

    [DllImport(LibSystem, EntryPoint = "vm_deallocate")]
    private static extern int VmDeallocate(uint task, nuint address, nuint size);

    [DllImport(LibSystem, EntryPoint = "vm_read_overwrite")]
    private static extern int VmReadOverwrite(uint task, nuint address, nuint size, nuint data, nuint* outSize);

    /// <summary>The managed object's address. The caller must not hold the object across a GC.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static nint AddressOf(object obj) => *(nint*)Unsafe.AsPointer(ref obj);

    /// <summary>
    /// Counts words in [scanFrom, stack top) of the current thread that point into
    /// [lo, lo + span). Returns a short description with the hit offsets below the stack top.
    /// </summary>
    internal static string FindHits(nint scanFrom, nint lo, int span)
    {
        try
        {
            nint top = PthreadGetStackAddr(PthreadSelf());
            nint start = scanFrom & ~(nint)7;
            if (start <= 0 || start >= top || top - start > 64 * 1024 * 1024)
                return $"scan unavailable (start=0x{start:X}, top=0x{top:X})";
            var hits = new List<string>();
            for (nint p = start; p < top; p += sizeof(nint))
            {
                nint v = *(nint*)p;
                if (v >= lo && v < lo + span)
                    hits.Add($"top-0x{(top - p):X}(+{v - lo})");
            }
            return hits.Count == 0
                ? $"no hits in {top - start} bytes"
                : $"{hits.Count} hit(s) in {top - start} bytes: {string.Join(",", hits.Take(8))}";
        }
        catch (Exception ex)
        {
            return $"scan failed: {ex.Message}";
        }
    }

    // How far below another thread's stack pointer to look. Words there are outside the thread's
    // live frames, but they can still be roots: when a Mono thread blocks (an idle pool worker
    // waiting for work, for one) the runtime saves a copy of the stack just below its stack
    // pointer and scans that copy at every collection until the thread runs again. The scan here
    // reads the live stack, which for a parked thread still matches the copy. Such hits are
    // labelled belowSP so they are distinguishable from one inside a live frame.
    private const int BelowSpWindow = 16 * 1024;

    /// <summary>
    /// Scans every thread in the process for words pointing into each target range: the current
    /// thread from <paramref name="selfScanFrom"/> up (its own frames below that are this
    /// diagnostic's), every other thread from a window below its stack pointer up, plus that
    /// thread's general-purpose registers. A hit at or above the stack pointer, or in a register,
    /// is a word a conservative collector treats as a root. A hit below it is reported as
    /// <c>belowSP</c>, and is a root only while that thread is parked with its state saved (see
    /// <c>BelowSpWindow</c>). The other threads keep running during the scan, so a hit on a busy
    /// thread can be stale by the time it is reported. Best effort: never throws.
    /// </summary>
    internal static string FindHitsOnAllThreads(IReadOnlyList<(string label, nint lo, int span)> targets, nint selfScanFrom)
    {
        if (targets.Count == 0)
            return "all-thread scan: no targets";
        try
        {
            uint task = TaskSelf();
            uint selfPort = PthreadMachThread(PthreadSelf());
            IntPtr list;
            uint count;
            if (TaskThreads(task, &list, &count) != 0)
                return "all-thread scan: task_threads failed";
            var hits = new List<string>[targets.Count];
            for (int i = 0; i < hits.Length; i++)
                hits[i] = new List<string>();
            var threadNames = new List<string>();
            nuint* regs = stackalloc nuint[48];
            try
            {
                for (uint t = 0; t < count; t++)
                {
                    uint port = ((uint*)list)[t];
                    IntPtr pthread = PthreadFromMachThread(port);
                    string name = ThreadName(pthread, port);
                    threadNames.Add(name);
                    if (pthread == IntPtr.Zero)
                        continue;
                    nint top = PthreadGetStackAddr(pthread);
                    nint bottom = top - (nint)PthreadGetStackSize(pthread);
                    bool self = port == selfPort;
                    nint sp;
                    int regCount = 0;
                    if (self)
                    {
                        sp = selfScanFrom & ~(nint)7;
                        threadNames[^1] += $"(self, scanned from top-0x{top - sp:X})";
                    }
                    else if (!TryGetRegisters(port, regs, out sp, out regCount))
                    {
                        threadNames[^1] += "(no-state)";
                        continue;
                    }
                    if (sp <= bottom || sp >= top)
                    {
                        threadNames[^1] += $"(sp-out-of-stack 0x{sp:X})";
                        continue;
                    }
                    nint start = self ? sp : Math.Max(bottom, sp - BelowSpWindow);
                    // Another thread can exit and unmap its stack mid-scan, so its stack is
                    // copied through the kernel, which fails cleanly instead of faulting.
                    nuint length = (nuint)(top - start);
                    nint* copy = (nint*)NativeMemory.Alloc(length);
                    try
                    {
                        nuint copied = 0;
                        if (VmReadOverwrite(task, (nuint)start, length, (nuint)copy, &copied) != 0 || copied != length)
                        {
                            threadNames[^1] += "(stack-unreadable)";
                            continue;
                        }
                        for (nint offset = 0; offset < (nint)length; offset += sizeof(nint))
                        {
                            nint p = start + offset;
                            nint v = copy[offset / sizeof(nint)];
                            for (int i = 0; i < targets.Count; i++)
                            {
                                var (_, lo, span) = targets[i];
                                if (v >= lo && v < lo + span)
                                    hits[i].Add($"{name}:{(p >= sp ? "live" : "belowSP")}:top-0x{top - p:X}/sp+0x{p - sp:X}(+{v - lo})");
                            }
                        }
                    }
                    finally
                    {
                        NativeMemory.Free(copy);
                    }
                    for (int r = 0; r < regCount; r++)
                    {
                        nint v = (nint)regs[r];
                        for (int i = 0; i < targets.Count; i++)
                        {
                            var (_, lo, span) = targets[i];
                            if (v >= lo && v < lo + span)
                                hits[i].Add($"{name}:reg{r}(+{v - lo})");
                        }
                    }
                }
            }
            finally
            {
                for (uint t = 0; t < count; t++)
                    MachPortDeallocate(task, ((uint*)list)[t]);
                VmDeallocate(task, (nuint)list, (nuint)(count * sizeof(uint)));
            }

            var parts = new List<string>();
            for (int i = 0; i < targets.Count; i++)
            {
                var (label, lo, _) = targets[i];
                parts.Add(hits[i].Count == 0
                    ? $"{label}@0x{lo:X}: no hits"
                    : $"{label}@0x{lo:X}: {hits[i].Count} hit(s) {string.Join(",", hits[i].Take(8))}");
            }
            return $"all-thread scan over {count} threads [{string.Join(",", threadNames)}]: {string.Join("; ", parts)}";
        }
        catch (Exception ex)
        {
            return $"all-thread scan failed: {ex.Message}";
        }
    }

    private static uint TaskSelf()
    {
        var lib = NativeLibrary.Load(LibSystem);
        return *(uint*)NativeLibrary.GetExport(lib, "mach_task_self_");
    }

    private static string ThreadName(IntPtr pthread, uint port)
    {
        if (pthread == IntPtr.Zero)
            return $"#{port}<no-pthread>";
        byte* buffer = stackalloc byte[64];
        if (PthreadGetName(pthread, buffer, 64) != 0 || buffer[0] == 0)
            return $"#{port}";
        return $"#{port}'{Marshal.PtrToStringUTF8((IntPtr)buffer)}'";
    }

    // General-purpose registers and stack pointer of a thread that is not the caller.
    private static bool TryGetRegisters(uint port, nuint* regs, out nint sp, out int regCount)
    {
        sp = 0;
        regCount = 0;
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            // ARM_THREAD_STATE64: x0..x28, fp, lr, sp, pc, cpsr+pad — 68 32-bit words.
            uint words = 68;
            if (ThreadGetState(port, 6, regs, &words) != 0)
                return false;
            sp = (nint)regs[31];
            regCount = 31; // x0..x28, fp, lr
            return true;
        }
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            // x86_THREAD_STATE64: rax, rbx, rcx, rdx, rdi, rsi, rbp, rsp, r8..r15, rip, ... — 42 words.
            uint words = 42;
            if (ThreadGetState(port, 4, regs, &words) != 0)
                return false;
            sp = (nint)regs[7];
            regCount = 16;
            return true;
        }
        return false;
    }
}

/// <summary>
/// DIAGNOSTIC. Weak, per-iteration observations of the managed objects a leak probe's churn
/// abandons: the wrapper and the SafeHandle that owns its native buffer. Each is held through a
/// weak GC handle, so the observation keeps nothing alive, and the handle slots are allocated
/// up front by the caller, so recording an observation inside the churn allocates nothing on the
/// managed heap and leaves the churn's object layout unchanged. The SafeHandle is watched twice —
/// short (cleared once the object is unreachable) and long (cleared only once it is actually
/// collected, after its finalizer) — so a failure readout separates a ROOTED survivor (short
/// handle still alive) from a finalization-pending one (short cleared, long alive). Read only on
/// the failure path, after the drain.
/// </summary>
internal sealed class LeakObservations : IDisposable
{
    private readonly GCHandle[] _wrappers;
    private readonly GCHandle[] _handles;
    private readonly GCHandle[] _handlesLong;
    private int _count;

    internal LeakObservations(int capacity)
    {
        _wrappers = new GCHandle[capacity];
        _handles = new GCHandle[capacity];
        _handlesLong = new GCHandle[capacity];
    }

    internal void Add(object wrapper, SafeHandle handle)
    {
        _wrappers[_count] = GCHandle.Alloc(wrapper, GCHandleType.Weak);
        _handles[_count] = GCHandle.Alloc(handle, GCHandleType.Weak);
        _handlesLong[_count] = GCHandle.Alloc(handle, GCHandleType.WeakTrackResurrection);
        _count++;
    }

    /// <summary>Counts and indices of every still-observable object.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal string Summarize()
    {
        var rootedWrappers = new List<int>();
        var rootedHandles = new List<int>();
        var pendingHandles = new List<int>();
        for (int i = 0; i < _count; i++)
        {
            if (_wrappers[i].Target is not null)
                rootedWrappers.Add(i);
            if (_handles[i].Target is not null)
                rootedHandles.Add(i);
            else if (_handlesLong[i].Target is not null)
                pendingHandles.Add(i);
        }
        return $"weak observations over {_count} iterations: rootedWrappers={Describe(rootedWrappers)}, "
            + $"rootedHandles={Describe(rootedHandles)}, finalizationPendingHandles={Describe(pendingHandles)}";
    }

    /// <summary>
    /// All-thread conservative-root scan for every wrapper/handle still strongly reachable.
    /// <paramref name="scanFrom"/> is an address in the caller's frame: the current thread is
    /// scanned from there up, so the diagnostic's own frames are excluded.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal string DescribeRoots(nint scanFrom)
    {
        // A precise collector has no conservative roots, and a moving one can relocate the
        // targets while the scan runs.
        if (!SwiftRuntimeInfo.IsMonoRuntime)
            return "roots: conservative scan not applicable (precise collector)";
        var targets = new List<(string, nint, int)>();
        for (int i = 0; i < _count && targets.Count < 8; i++)
        {
            // Spans are upper bounds on the object sizes; each hit reports its offset, so +0 is an
            // exact reference and a large offset may belong to a neighbouring allocation.
            if (_wrappers[i].Target is { } w)
                targets.Add(($"#{i}.wrapper", ConservativeRootProbe.AddressOf(w), 64));
            if (_handles[i].Target is { } h)
                targets.Add(($"#{i}.handle", ConservativeRootProbe.AddressOf(h), 96));
        }
        return targets.Count == 0 ? "roots: no strongly reachable observation" : ConservativeRootProbe.FindHitsOnAllThreads(targets, scanFrom);
    }

    public void Dispose()
    {
        for (int i = 0; i < _count; i++)
        {
            _wrappers[i].Free();
            _handles[i].Free();
            _handlesLong[i].Free();
        }
        _count = 0;
    }

    private static string Describe(List<int> indices)
        => indices.Count == 0 ? "0" : $"{indices.Count} ({string.Join(",", indices.Take(8))}{(indices.Count > 8 ? ",…" : "")})";
}
