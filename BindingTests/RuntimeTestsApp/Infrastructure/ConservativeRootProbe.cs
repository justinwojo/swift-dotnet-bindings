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

    // How far below another thread's stack pointer to look. Words there are outside the thread's
    // live frames, but they can still be roots: when a Mono thread blocks (an idle pool worker
    // waiting for work, for one) the runtime saves a copy of the stack just below its stack
    // pointer and scans that copy at every collection until the thread runs again. The scan here
    // reads the live stack, which for a parked thread still matches the copy. Such hits are
    // labelled belowSP so they are distinguishable from one inside a live frame.
    private const int BelowSpWindow = 16 * 1024;

    // One-hop holder search: the window read around each candidate word, and caps that keep the
    // failure path bounded however many threads and words there are.
    private const int HolderWindowBefore = 64;
    private const int HolderWindowAfter = 128;
    private const int MaxHolderReads = 200_000;
    private const long HolderRange = 8L * 1024 * 1024 * 1024;

    // One word a conservative collector could treat as a root. Kept compact (no per-word string),
    // since a scan collects every non-zero word of every thread's stack.
    private readonly struct ScannedWord
    {
        internal readonly int Thread;
        internal readonly int Register; // -1 for a stack word
        internal readonly nint Address;
        internal readonly nint Sp;
        internal readonly nint Top;
        internal readonly nint Value;

        internal ScannedWord(int thread, int register, nint address, nint sp, nint top, nint value)
        {
            Thread = thread;
            Register = register;
            Address = address;
            Sp = sp;
            Top = top;
            Value = value;
        }

        internal string Describe(List<string> threadNames)
        {
            var name = threadNames[Thread];
            int cut = name.IndexOf('(');
            if (cut > 0)
                name = name.Substring(0, cut);
            return Register >= 0
                ? $"{name}:reg{Register}"
                : $"{name}:{(Address >= Sp ? "live" : "belowSP")}:top-0x{Top - Address:X}/sp{SignedHex(Address - Sp)}";
        }
    }

    /// <summary>
    /// Scans every thread in the process for words pointing into each target range: the current
    /// thread from <paramref name="selfScanFrom"/> up (its own frames below that are this
    /// diagnostic's), every other thread from a window below its stack pointer up, plus that
    /// thread's general-purpose registers. A hit at or above the stack pointer, or in a register,
    /// is a word a conservative collector treats as a root. A hit below it is reported as
    /// <c>belowSP</c>, and is a root only while that thread is parked with its state saved (see
    /// <c>BelowSpWindow</c>).
    /// <para>
    /// With <paramref name="oneHop"/>, a word that points at none of the targets is also followed
    /// once: if the memory around the address it names holds a pointer into a target, the word is
    /// reported as a <c>holder</c>, which is how a pinned owner of a target (a wrapper over a
    /// handle, say) shows up without the probe having to know the owner's type. A holder hit names
    /// the word, the address it points at and the offset from there to the slot holding the target.
    /// </para>
    /// The other threads keep running during the scan, so a hit on a busy thread can be stale by
    /// the time it is reported. Best effort: never throws.
    /// </summary>
    internal static string FindHitsOnAllThreads(IReadOnlyList<(string label, nint lo, int span)> targets, nint selfScanFrom, bool oneHop = false)
    {
        if (targets.Count == 0)
            return "all-thread scan: no targets";
        try
        {
            uint task = TaskSelf();
            var threadNames = new List<string>();
            var words = new List<ScannedWord>();
            var stacks = new List<(nint bottom, nint top)>();
            CollectWords(task, selfScanFrom, threadNames, words, stacks);

            var hits = new List<string>[targets.Count];
            for (int i = 0; i < hits.Length; i++)
                hits[i] = new List<string>();
            // Each distinct candidate value once: the first word that held it, and how many did.
            var others = new Dictionary<nint, (int first, int count)>();
            for (int n = 0; n < words.Count; n++)
            {
                var word = words[n];
                bool direct = false;
                for (int i = 0; i < targets.Count; i++)
                {
                    var (_, lo, span) = targets[i];
                    if (word.Value >= lo && word.Value < lo + span)
                    {
                        hits[i].Add($"{word.Describe(threadNames)}(+{word.Value - lo})");
                        direct = true;
                    }
                }
                if (!direct && oneHop && IsHolderCandidate(word.Value, targets, stacks))
                {
                    others[word.Value] = others.TryGetValue(word.Value, out var seen)
                        ? (seen.first, seen.count + 1)
                        : (n, 1);
                }
            }

            var holders = new List<string>[targets.Count];
            for (int i = 0; i < holders.Length; i++)
                holders[i] = new List<string>();
            int reads = 0;
            if (oneHop)
            {
                nint* window = stackalloc nint[(HolderWindowBefore + HolderWindowAfter) / sizeof(nint)];
                foreach (var (value, seen) in others)
                {
                    if (reads++ >= MaxHolderReads)
                        break;
                    nint start = (value & ~(nint)7) - HolderWindowBefore;
                    int windowWords = (HolderWindowBefore + HolderWindowAfter) / sizeof(nint);
                    if (!TryRead(task, start, window, windowWords))
                    {
                        // The window may straddle an unmapped page; fall back to the part at and
                        // after the word, which is where an object's fields are.
                        start = value & ~(nint)7;
                        windowWords = HolderWindowAfter / sizeof(nint);
                        if (!TryRead(task, start, window, windowWords))
                            continue;
                    }
                    for (int w = 0; w < windowWords; w++)
                    {
                        nint slot = start + w * sizeof(nint);
                        nint v = window[w];
                        for (int i = 0; i < targets.Count; i++)
                        {
                            var (_, lo, span) = targets[i];
                            if (v >= lo && v < lo + span)
                                holders[i].Add($"{words[seen.first].Describe(threadNames)}{(seen.count > 1 ? $"(x{seen.count})" : "")}->0x{value:X}[{SignedHex(slot - value)}](+{v - lo})");
                        }
                    }
                }
            }

            var parts = new List<string>();
            for (int i = 0; i < targets.Count; i++)
            {
                var (label, lo, _) = targets[i];
                var text = hits[i].Count == 0
                    ? $"{label}@0x{lo:X}: no direct hits"
                    : $"{label}@0x{lo:X}: {hits[i].Count} direct hit(s) {string.Join(",", hits[i].Take(8))}";
                if (oneHop)
                    text += holders[i].Count == 0
                        ? ", no holders"
                        : $", {holders[i].Count} holder(s) {string.Join(",", holders[i].Take(8))}";
                parts.Add(text);
            }
            var holderNote = oneHop ? $", {reads} holder reads" : "";
            return $"all-thread scan over {threadNames.Count} threads{holderNote} [{string.Join(",", threadNames)}]: {string.Join("; ", parts)}";
        }
        catch (Exception ex)
        {
            return $"all-thread scan failed: {ex.Message}";
        }
    }

    // A word worth following one hop: a plausible user-space address near some target. Managed
    // objects that hold a target live in the same managed heap, so a value gigabytes away from
    // every target, or a small integer, is not one of them. A word pointing into a thread stack (a
    // frame pointer, say) is not followed either: the stack words around it are scanned directly.
    private static bool IsHolderCandidate(nint value, IReadOnlyList<(string label, nint lo, int span)> targets, List<(nint bottom, nint top)> stacks)
    {
        if (value < 0x1_0000_0000 || value > 0x7FFF_FFFF_FFFF)
            return false;
        foreach (var (bottom, top) in stacks)
        {
            if (value >= bottom && value < top)
                return false;
        }
        for (int i = 0; i < targets.Count; i++)
        {
            long distance = (long)value - targets[i].lo;
            if (distance > -HolderRange && distance < HolderRange)
                return true;
        }
        return false;
    }

    // Copies `count` words at `address` through the kernel, which fails cleanly on unmapped memory
    // instead of faulting.
    private static bool TryRead(uint task, nint address, nint* buffer, int count)
    {
        nuint length = (nuint)(count * sizeof(nint));
        nuint copied = 0;
        return VmReadOverwrite(task, (nuint)address, length, (nuint)buffer, &copied) == 0 && copied == length;
    }

    // Every word the conservative collector could treat as a root, labelled with where it was
    // found: each thread's stack from a window below its stack pointer (the caller's own from
    // selfScanFrom) up to its top, and each other thread's general-purpose registers.
    private static void CollectWords(uint task, nint selfScanFrom, List<string> threadNames, List<ScannedWord> words, List<(nint bottom, nint top)> stacks)
    {
        uint selfPort = PthreadMachThread(PthreadSelf());
        IntPtr list;
        uint count;
        if (TaskThreads(task, &list, &count) != 0)
            throw new InvalidOperationException("task_threads failed");
        nuint* regs = stackalloc nuint[48];
        try
        {
            for (uint t = 0; t < count; t++)
            {
                uint port = ((uint*)list)[t];
                IntPtr pthread = PthreadFromMachThread(port);
                int threadIndex = threadNames.Count;
                threadNames.Add(ThreadName(pthread, port));
                if (pthread == IntPtr.Zero)
                    continue;
                nint top = PthreadGetStackAddr(pthread);
                nint bottom = top - (nint)PthreadGetStackSize(pthread);
                stacks.Add((bottom, top));
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
                // Another thread can exit and unmap its stack mid-scan, so its stack is copied
                // through the kernel, which fails cleanly instead of faulting.
                int stackWords = (int)((top - start) / sizeof(nint));
                nint* copy = (nint*)NativeMemory.Alloc((nuint)(stackWords * sizeof(nint)));
                try
                {
                    // An unreadable stack still leaves the registers below worth scanning.
                    if (!TryRead(task, start, copy, stackWords))
                        threadNames[^1] += "(stack-unreadable)";
                    else
                    {
                        for (int w = 0; w < stackWords; w++)
                        {
                            nint p = start + w * sizeof(nint);
                            nint v = copy[w];
                            if (v != 0)
                                words.Add(new ScannedWord(threadIndex, -1, p, sp, top, v));
                        }
                    }
                }
                finally
                {
                    NativeMemory.Free(copy);
                }
                for (int r = 0; r < regCount; r++)
                {
                    if (regs[r] != 0)
                        words.Add(new ScannedWord(threadIndex, r, 0, 0, 0, (nint)regs[r]));
                }
            }
        }
        finally
        {
            for (uint t = 0; t < count; t++)
                MachPortDeallocate(task, ((uint*)list)[t]);
            VmDeallocate(task, (nuint)list, (nuint)(count * sizeof(uint)));
        }
    }

    private static string SignedHex(nint offset) => offset < 0 ? $"-0x{-offset:X}" : $"+0x{offset:X}";

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
/// DIAGNOSTIC. Weak observations of the managed objects a leak probe's churn abandons: the owning
/// SafeHandle of each native value and, when the probe records it, the wrapper over that handle.
/// Each is held through a weak GC handle, so the observation keeps nothing alive. Slots live in
/// fixed-size chunks; the chunks covering the preallocated count are allocated up front, so
/// recording within it allocates nothing on the managed heap and leaves the churn's object layout
/// unchanged, and further chunks are added on demand up to the capacity. The SafeHandle is watched
/// twice — short (cleared once the object is unreachable) and long (cleared only once it is
/// actually collected, after its finalizer) — so a failure readout separates a ROOTED survivor
/// (short handle still alive) from a finalization-pending one (short cleared, long alive).
/// Recording is safe from any thread, including concurrently with disposal. Read only on the
/// failure path, after the drain.
/// </summary>
internal sealed class LeakObservations : IDisposable
{
    private const int ChunkSize = 1024;

    private sealed class Chunk
    {
        internal readonly GCHandle[] Wrappers = new GCHandle[ChunkSize];
        internal readonly GCHandle[] Handles = new GCHandle[ChunkSize];
        internal readonly GCHandle[] HandlesLong = new GCHandle[ChunkSize];
        internal readonly int[] Published = new int[ChunkSize];
    }

    private readonly Chunk?[] _chunks;
    private readonly int _capacity;
    private int _reserved;
    private int _dropped;
    private volatile bool _disposed;

    /// <param name="capacity">The most observations recorded; later ones are only counted.</param>
    /// <param name="preallocate">How many slots to allocate up front (default: all of them).</param>
    internal LeakObservations(int capacity, int preallocate = int.MaxValue)
    {
        _capacity = capacity;
        _chunks = new Chunk?[(capacity + ChunkSize - 1) / ChunkSize];
        int upFront = Math.Min(capacity, preallocate);
        for (int c = 0; c * ChunkSize < upFront; c++)
            _chunks[c] = new Chunk();
    }

    /// <summary>Records a wrapper and the SafeHandle that owns its native value.</summary>
    internal void Add(object wrapper, SafeHandle handle) => Record(wrapper, handle);

    /// <summary>Records an owning SafeHandle whose wrapper the caller does not know.</summary>
    internal void AddHandle(SafeHandle handle) => Record(null, handle);

    private void Record(object? wrapper, SafeHandle handle)
    {
        if (_disposed)
            return;
        int i = Interlocked.Increment(ref _reserved) - 1;
        if (i >= _capacity)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }
        var chunk = ChunkAt(i / ChunkSize, create: true)!;
        int j = i % ChunkSize;
        if (wrapper is not null)
            chunk.Wrappers[j] = GCHandle.Alloc(wrapper, GCHandleType.Weak);
        chunk.Handles[j] = GCHandle.Alloc(handle, GCHandleType.Weak);
        chunk.HandlesLong[j] = GCHandle.Alloc(handle, GCHandleType.WeakTrackResurrection);
        // A full fence between publishing and re-reading the disposed flag: either Dispose sees
        // this slot published, or this call sees Dispose's flag, and the slot is freed exactly
        // once by whichever side takes it.
        Interlocked.Exchange(ref chunk.Published[j], 1);
        if (_disposed)
            Free(chunk, j);
    }

    private Chunk? ChunkAt(int c, bool create = false)
    {
        var chunk = Volatile.Read(ref _chunks[c]);
        if (chunk is null && create)
        {
            Interlocked.CompareExchange(ref _chunks[c], new Chunk(), null);
            chunk = Volatile.Read(ref _chunks[c]);
        }
        return chunk;
    }

    private static void Free(Chunk chunk, int j)
    {
        if (Interlocked.Exchange(ref chunk.Published[j], 0) == 0)
            return;
        if (chunk.Wrappers[j].IsAllocated)
            chunk.Wrappers[j].Free();
        chunk.Handles[j].Free();
        chunk.HandlesLong[j].Free();
    }

    private int Count => Math.Min(Volatile.Read(ref _reserved), _capacity);

    // The slot of observation i when it has been published, otherwise null.
    private Chunk? Published(int i, out int j)
    {
        j = i % ChunkSize;
        var chunk = ChunkAt(i / ChunkSize);
        return chunk is not null && Volatile.Read(ref chunk.Published[j]) != 0 ? chunk : null;
    }

    /// <summary>Counts and indices of every still-observable object.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal string Summarize()
    {
        var rootedWrappers = new List<int>();
        var rootedHandles = new List<int>();
        var pendingHandles = new List<int>();
        var rootedTypes = new SortedSet<string>();
        int count = Count;
        for (int i = 0; i < count; i++)
        {
            if (Published(i, out int j) is not { } chunk)
                continue;
            if (chunk.Wrappers[j].IsAllocated && chunk.Wrappers[j].Target is not null)
                rootedWrappers.Add(i);
            if (chunk.Handles[j].Target is { } h)
            {
                rootedHandles.Add(i);
                if (rootedTypes.Count < 4)
                    rootedTypes.Add(DescribeType(h.GetType()));
            }
            else if (chunk.HandlesLong[j].Target is not null)
            {
                pendingHandles.Add(i);
            }
        }
        var dropped = Volatile.Read(ref _dropped);
        return $"weak observations over {count} handles{(dropped > 0 ? $" (+{dropped} unrecorded)" : "")}: "
            + $"rootedWrappers={Describe(rootedWrappers)}, rootedHandles={Describe(rootedHandles)}"
            + (rootedTypes.Count > 0 ? $" [{string.Join(", ", rootedTypes)}]" : "")
            + $", finalizationPendingHandles={Describe(pendingHandles)}";
    }

    /// <summary>
    /// All-thread conservative-root scan for every wrapper/handle still strongly reachable,
    /// following each stack word one hop so a pinned owner of a handle is found too.
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
        var targets = RootedTargets();
        return targets.Count == 0
            ? "roots: no strongly reachable observation"
            : ConservativeRootProbe.FindHitsOnAllThreads(targets, scanFrom, oneHop: true);
    }

    /// <summary>
    /// Address ranges of up to <paramref name="max"/> still strongly reachable observed objects.
    /// The addresses are only meaningful until the next collection.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal List<(string label, nint lo, int span)> RootedTargets(int max = 8)
    {
        var targets = new List<(string, nint, int)>();
        int count = Count;
        for (int i = 0; i < count && targets.Count < max; i++)
        {
            if (Published(i, out int j) is not { } chunk)
                continue;
            // Spans are upper bounds on the object sizes; each hit reports its offset, so +0 is an
            // exact reference and a large offset may belong to a neighbouring allocation. The
            // generation separates a nursery survivor (g0) from a promoted one.
            if (chunk.Wrappers[j].IsAllocated && chunk.Wrappers[j].Target is { } w)
                targets.Add(($"#{i}.wrapper(g{GC.GetGeneration(w)})", ConservativeRootProbe.AddressOf(w), 64));
            if (chunk.Handles[j].Target is { } h)
                targets.Add(($"#{i}.handle(g{GC.GetGeneration(h)})", ConservativeRootProbe.AddressOf(h), 96));
        }
        return targets;
    }

    public void Dispose()
    {
        _disposed = true;
        Interlocked.MemoryBarrier();
        int count = Count;
        for (int i = 0; i < count; i++)
        {
            if (ChunkAt(i / ChunkSize) is { } chunk)
                Free(chunk, i % ChunkSize);
        }
    }

    private static string DescribeType(Type type)
        => type.IsGenericType
            ? $"{type.Name.Split('`')[0]}<{string.Join(",", type.GetGenericArguments().Select(a => a.Name))}>"
            : type.Name;

    private static string Describe(List<int> indices)
        => indices.Count == 0 ? "0" : $"{indices.Count} ({string.Join(",", indices.Take(8))}{(indices.Count > 8 ? ",…" : "")})";
}
