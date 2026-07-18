// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BindingsGeneration;

/// <summary>
/// Records who owned each region of a writer's buffer, without writing anything itself.
/// </summary>
/// <remarks>
/// <para>
/// An emitter opens a scope before dispatching a member and closes it after; the recorder stores
/// only the owner and the buffer offset at each boundary. Emission therefore produces byte-identical
/// output whether or not anything is recording — the recorder cannot perturb the text because it
/// never touches the writer.
/// </para>
/// <para>
/// Two views come out of the same event list. <see cref="BuildScopes"/> pairs opens with closes to
/// give each scope its own interval — that is "the fragment" a later pass withdraws or re-renders,
/// and it is the source of truth for what was emitted. <see cref="BuildTiling"/> projects the same
/// tree onto a partition of the whole buffer, attributing every character (including the text
/// between and around scopes) to the innermost scope that was open when it was written. The tiling
/// is what an offset-addressed diagnostic resolves against, and it is total by construction: no
/// character belongs to nothing.
/// </para>
/// <para>
/// All offsets are UTF-16 character offsets into the writer's buffer, matching
/// <see cref="System.Text.StringBuilder.Length"/>.
/// </para>
/// </remarks>
public sealed class FragmentRecorder
{
    private readonly List<Event> _events = new();
    private readonly List<OpenScope> _open = new();
    private int _nextToken = 1;

    /// <summary>An open or close boundary at a buffer offset.</summary>
    internal readonly record struct Event(FragmentOwner Owner, int Offset, bool IsOpen, int Token);

    /// <summary>A currently-open scope and the token whose holder is entitled to close it.</summary>
    internal readonly record struct OpenScope(FragmentOwner Owner, int Token);

    /// <summary>Number of scopes currently open.</summary>
    public int OpenDepth => _open.Count;

    /// <summary>True when nothing has been recorded — the recorder is inert for this render.</summary>
    public bool IsEmpty => _events.Count == 0;

    /// <summary>
    /// Opens a scope owned by <paramref name="owner"/> at <paramref name="offset"/> and returns the
    /// token that closes it.
    /// </summary>
    /// <remarks>
    /// The token exists because a rollback can erase a scope that something is still holding a
    /// handle to. Speculative member emission opens a scope, writes into both buffers, and on a
    /// contract trip truncates the buffer back — at which point the scope's open boundary is gone but
    /// its <c>using</c> block has yet to run. Closing "the innermost open scope" there would close
    /// whatever unrelated scope happened to be beneath it. A token makes that case detectable, so the
    /// stale close is dropped instead of corrupting its neighbour.
    /// </remarks>
    public int Open(in FragmentOwner owner, int offset)
    {
        RejectRegression(offset);
        var token = _nextToken++;
        _events.Add(new Event(owner, offset, IsOpen: true, token));
        _open.Add(new OpenScope(owner, token));
        return token;
    }

    /// <summary>
    /// Closes the scope identified by <paramref name="token"/> at <paramref name="offset"/>, and
    /// returns whether anything was closed.
    /// </summary>
    /// <remarks>
    /// A token that is no longer open was rolled back out of existence along with the text it
    /// covered; there is nothing left to close and nothing was lost, so this is a no-op rather than
    /// an error. A token that is open but not innermost means scopes are being disposed out of
    /// order — the scopes nested inside it are closed here too, in the order a correctly nested
    /// <c>using</c> would have closed them, so the event list stays laminar.
    /// </remarks>
    public bool Close(int token, int offset)
    {
        var index = _open.FindLastIndex(o => o.Token == token);
        if (index < 0)
            return false;

        RejectRegression(offset);
        for (var i = _open.Count - 1; i >= index; i--)
        {
            _events.Add(new Event(_open[i].Owner, offset, IsOpen: false, _open[i].Token));
        }
        _open.RemoveRange(index, _open.Count - index);
        return true;
    }

    /// <summary>
    /// Replaces the owner of the innermost open scope, for the case where the declaration being
    /// emitted is swapped for another after its scope was opened.
    /// </summary>
    /// <remarks>
    /// The trailing-default-parameter recovery path substitutes a different <c>MethodDecl</c>
    /// mid-iteration; without a re-tag the emitted text would carry the identity of the declaration
    /// that was rejected rather than the one that was actually rendered.
    /// </remarks>
    public bool Retag(in FragmentOwner owner)
    {
        if (_open.Count == 0)
            return false;

        var token = _open[^1].Token;
        _open[^1] = new OpenScope(owner, token);
        for (var i = _events.Count - 1; i >= 0; i--)
        {
            if (_events[i].IsOpen && _events[i].Token == token)
            {
                _events[i] = _events[i] with { Owner = owner };
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The recorder position paired with a writer checkpoint, so rolling the buffer back also
    /// discards the boundaries recorded since. Keeping the open-scope stack (not just its depth) is
    /// what lets a scope that was opened before the checkpoint and closed after it be restored to
    /// open rather than silently lost.
    /// </summary>
    public readonly struct RecorderCheckpoint
    {
        internal RecorderCheckpoint(int eventCount, OpenScope[] openScopes)
        {
            EventCount = eventCount;
            OpenScopes = openScopes;
        }

        internal int EventCount { get; }
        internal OpenScope[]? OpenScopes { get; }
    }

    /// <summary>Captures the current recorder position.</summary>
    public RecorderCheckpoint Checkpoint() => new(_events.Count, _open.ToArray());

    /// <summary>
    /// Discards every boundary recorded after <paramref name="checkpoint"/> and restores the
    /// open-scope stack it was taken with.
    /// </summary>
    /// <remarks>
    /// Restoring the stack by value (tokens included) is what makes the token check in
    /// <see cref="Close"/> work: a scope open at checkpoint time comes back closable by its original
    /// holder, while a scope opened after it does not come back at all.
    /// </remarks>
    public void RollbackTo(in RecorderCheckpoint checkpoint)
    {
        if (checkpoint.EventCount < _events.Count)
            _events.RemoveRange(checkpoint.EventCount, _events.Count - checkpoint.EventCount);

        _open.Clear();
        if (checkpoint.OpenScopes != null)
            _open.AddRange(checkpoint.OpenScopes);
    }

    /// <summary>
    /// Boundaries lifted out of the recorder by a rollback that intends to re-append the text they
    /// covered, rebased so they can be replayed at whatever offset the text lands at.
    /// </summary>
    public readonly struct CapturedFragments
    {
        internal CapturedFragments(Event[] events, bool balanced)
        {
            Events = events;
            Balanced = balanced;
        }

        internal Event[]? Events { get; }

        /// <summary>
        /// Whether the captured region opened and closed every scope it contains. An unbalanced
        /// capture straddles a scope boundary and cannot be rebased, so it is dropped rather than
        /// replayed into the wrong nesting.
        /// </summary>
        internal bool Balanced { get; }
    }

    /// <summary>
    /// Removes the boundaries recorded after <paramref name="checkpoint"/> and returns them rebased
    /// to the start of the region, for <see cref="ReplayCaptured"/> to re-apply once the text is
    /// re-appended somewhere else.
    /// </summary>
    /// <remarks>
    /// The capture-and-re-append path exists so an emitter can inject a prefix ahead of a member
    /// whose body is already written. Dropping the boundaries instead would collapse that member to
    /// one opaque run owned by whatever encloses it, which is exactly the resolution loss the
    /// per-member map is for — so they are carried across the move instead.
    /// </remarks>
    public CapturedFragments RollbackToAndCapture(in RecorderCheckpoint checkpoint, int regionStart)
    {
        var captured = Array.Empty<Event>();
        var balanced = true;

        if (checkpoint.EventCount < _events.Count)
        {
            captured = new Event[_events.Count - checkpoint.EventCount];
            var depth = 0;
            for (var i = 0; i < captured.Length; i++)
            {
                var ev = _events[checkpoint.EventCount + i];
                captured[i] = ev with { Offset = ev.Offset - regionStart };
                depth += ev.IsOpen ? 1 : -1;
                if (depth < 0)
                    balanced = false;
            }
            if (depth != 0)
                balanced = false;
        }

        RollbackTo(checkpoint);
        return new CapturedFragments(captured, balanced);
    }

    /// <summary>
    /// Re-applies boundaries taken by <see cref="RollbackToAndCapture"/>, shifted to start at
    /// <paramref name="regionStart"/>. An unbalanced capture is discarded: the enclosing scope still
    /// owns the re-appended text, so provenance degrades to that scope rather than going wrong.
    /// </summary>
    public void ReplayCaptured(in CapturedFragments captured, int regionStart)
    {
        if (captured.Events is not { Length: > 0 } events || !captured.Balanced)
            return;

        foreach (var ev in events)
        {
            var token = _nextToken++;
            _events.Add(ev with { Offset = ev.Offset + regionStart, Token = token });
        }
    }

    /// <summary>
    /// Appends every boundary recorded by <paramref name="other"/>, shifted by
    /// <paramref name="shift"/>, as if that writer's text had been written here.
    /// </summary>
    /// <remarks>
    /// Several emitters build a body in a private writer and then dump the finished string into the
    /// main one in a single write. To the main recorder that is one opaque run, so without merging
    /// the side recorder every member emitted through those paths collapses onto its container.
    /// Only a balanced side recorder is merged — an unbalanced one would inject opens with no
    /// matching closes into the middle of the main event list and skew everything after it.
    /// </remarks>
    public bool AbsorbFrom(FragmentRecorder other, int shift)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other.IsEmpty)
            return true;
        if (other.OpenDepth != 0)
            return false;

        foreach (var ev in other._events)
        {
            var offset = ev.Offset + shift;
            RejectRegression(offset);
            _events.Add(ev with { Offset = offset, Token = _nextToken++ });
        }
        return true;
    }

    /// <summary>
    /// A recorded scope and the half-open buffer interval it covers, plus its nesting depth.
    /// </summary>
    public readonly record struct RecordedScope(FragmentOwner Owner, int Start, int End, int Depth);

    /// <summary>
    /// Pairs each open with its close, in the order the scopes were opened. Scopes still open when
    /// the buffer ended are closed at <paramref name="bufferLength"/> rather than dropped — an
    /// unbalanced emitter should lose provenance precision, not whole regions of output.
    /// </summary>
    public IReadOnlyList<RecordedScope> BuildScopes(int bufferLength)
    {
        var scopes = new List<RecordedScope>(_events.Count / 2 + 1);
        var stack = new Stack<(FragmentOwner Owner, int Start, int Index)>();

        foreach (var ev in _events)
        {
            if (ev.IsOpen)
            {
                stack.Push((ev.Owner, ev.Offset, scopes.Count));
                scopes.Add(new RecordedScope(ev.Owner, ev.Offset, ev.Offset, stack.Count - 1));
            }
            else if (stack.Count > 0)
            {
                var (owner, start, index) = stack.Pop();
                scopes[index] = new RecordedScope(owner, start, ev.Offset, stack.Count);
            }
        }

        while (stack.Count > 0)
        {
            var (owner, start, index) = stack.Pop();
            scopes[index] = new RecordedScope(owner, start, Math.Max(start, bufferLength), stack.Count);
        }

        return scopes;
    }

    /// <summary>
    /// Partitions <c>[0, bufferLength)</c> into leaves, each owned by the innermost scope open when
    /// its text was written. <paramref name="root"/> owns everything written with no scope open.
    /// </summary>
    /// <remarks>
    /// A leaf is flagged <c>IsWholeScope</c> when it spans exactly one scope from its open to its
    /// close, i.e. that scope had no children and no interstitial text. Everything else — a type's
    /// declaration header ahead of its first member, the separator a dispatch loop writes between
    /// members, the module trailer — is interstitial text of the enclosing scope.
    /// </remarks>
    public IReadOnlyList<(FragmentOwner Owner, int Start, int End, bool IsWholeScope, int Depth)> BuildTiling(
        int bufferLength, in FragmentOwner root)
    {
        var leaves = new List<(FragmentOwner, int, int, bool, int)>(_events.Count + 1);
        var stack = new List<FragmentOwner>();
        var cursor = 0;

        for (var i = 0; i < _events.Count; i++)
        {
            var ev = _events[i];
            var boundary = Math.Min(ev.Offset, bufferLength);
            if (boundary > cursor)
            {
                var owner = stack.Count > 0 ? stack[^1] : root;
                // Whole-scope exactly when this run is bracketed by its own open and close: the
                // previous event opened the scope now on top, and this event closes it.
                var wholeScope = !ev.IsOpen
                    && stack.Count > 0
                    && i > 0
                    && _events[i - 1].IsOpen
                    && _events[i - 1].Owner == owner;
                leaves.Add((owner, cursor, boundary, wholeScope, stack.Count));
                cursor = boundary;
            }

            if (ev.IsOpen)
                stack.Add(ev.Owner);
            else if (stack.Count > 0)
                stack.RemoveAt(stack.Count - 1);
        }

        if (bufferLength > cursor)
            leaves.Add((stack.Count > 0 ? stack[^1] : root, cursor, bufferLength, false, stack.Count));

        return leaves;
    }

    /// <summary>
    /// Every distinct owner recorded this render, in first-open order. Used to cross-check emission
    /// against the registries that independently track the same artifacts.
    /// </summary>
    public IReadOnlyList<FragmentOwner> RecordedOwners() =>
        _events.Where(e => e.IsOpen).Select(e => e.Owner).Distinct().ToList();

    /// <summary>
    /// Rejects a boundary that moves backwards. Offsets come from a buffer that only ever grows or
    /// is truncated by a rollback (which also truncates this event list), so a regression means the
    /// recorder and the writer have gone out of sync and every later interval would be wrong.
    /// </summary>
    private void RejectRegression(int offset)
    {
        if (_events.Count > 0 && offset < _events[^1].Offset)
            throw new InvalidOperationException(
                $"FragmentRecorder boundary at {offset} precedes the previous boundary at {_events[^1].Offset}; "
                + "the recorder is out of sync with the writer buffer.");
    }
}
