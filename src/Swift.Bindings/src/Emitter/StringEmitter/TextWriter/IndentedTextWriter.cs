// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;

namespace BindingsGeneration
{
    /// <summary>
    /// Shared base for the generator's source writers: an <see cref="IndentedTextWriter"/> over an
    /// in-memory <see cref="StringWriter"/> whose buffer can be checkpointed and truncated. An
    /// emitter uses that to write a member speculatively and erase it byte-for-byte when a
    /// contract trips mid-emission.
    /// <para>Both output languages need this, not just C#: a member is emitted into the C# and the
    /// Swift buffer by the same call, so rolling back only one of them leaves the other holding a
    /// block with no counterpart — a wrapper function nothing calls, or a half-written one that
    /// does not compile.</para>
    /// </summary>
    public abstract class BufferedSourceWriter : IndentedTextWriter
    {
        private readonly StringWriter _innerWriter;

        /// <summary>
        /// Boundaries lifted by the most recent <see cref="RollbackToAndCapture"/>, waiting for the
        /// matching <see cref="AppendCaptured"/> to say what offset the text was re-appended at.
        /// </summary>
        private FragmentRecorder.CapturedFragments _capturedFragments;

        /// <summary>
        /// Initializes a new instance of the <see cref="BufferedSourceWriter"/> class.
        /// </summary>
        /// <param name="writer">The backing in-memory writer.</param>
        protected BufferedSourceWriter(StringWriter writer) : base(writer)
        {
            _innerWriter = writer;
        }

        /// <summary>
        /// Records which artifact owned each region of this buffer. Writing is unaffected — the
        /// recorder only stores boundary offsets — so instrumented and uninstrumented emission
        /// produce the same bytes. Travels with <see cref="Checkpoint"/>/<see cref="RollbackTo"/>
        /// so a rolled-back member leaves no provenance behind for text that no longer exists.
        /// </summary>
        public FragmentRecorder Fragments { get; } = new();

        /// <summary>
        /// Opens a fragment scope over everything written until the returned handle is disposed.
        /// The scope emits nothing; it only brackets the buffer.
        /// </summary>
        public FragmentScope BeginFragment(in FragmentOwner owner) =>
            new(this, Fragments.Open(owner, CurrentOffset));

        /// <summary>
        /// Closes the fragment scope it was returned from. A struct so the common <c>using</c> in a
        /// hot emission loop allocates nothing.
        /// </summary>
        /// <remarks>
        /// The scope is closed by token rather than by "innermost open", because a rollback between
        /// the open and the dispose can erase this scope while leaving an outer one open. Closing
        /// positionally there would end the outer scope early and hand the rest of its text to
        /// whatever encloses it; closing by token makes the erased case a no-op instead.
        /// </remarks>
        public readonly struct FragmentScope : IDisposable
        {
            private readonly BufferedSourceWriter? _writer;
            private readonly int _token;

            internal FragmentScope(BufferedSourceWriter writer, int token)
            {
                _writer = writer;
                _token = token;
            }

            /// <summary>Closes the scope at the current end of buffer.</summary>
            public void Dispose() => _writer?.Fragments.Close(_token, _writer.CurrentOffset);
        }

        /// <summary>
        /// Re-labels the innermost open fragment scope, for an emitter that substitutes a different
        /// declaration after the scope was opened.
        /// </summary>
        public void RetagFragment(in FragmentOwner owner) => Fragments.Retag(owner);

        /// <summary>
        /// Writes <paramref name="text"/> verbatim (no indent processing) and merges the fragment
        /// boundaries <paramref name="source"/> recorded for it, so text built in a private writer
        /// and dumped here keeps its per-member provenance instead of arriving as one opaque run.
        /// </summary>
        public void WriteAbsorbing(string text, BufferedSourceWriter source)
        {
            ArgumentNullException.ThrowIfNull(text);
            ArgumentNullException.ThrowIfNull(source);
            var start = CurrentOffset;
            InnerWriter.Write(text);

            // A refused merge (source left a scope open) is deliberately not an error: the text is
            // already written, and the absorbed run simply stays attributed to whatever scope is open
            // here. Provenance degrades to the enclosing owner; it never becomes wrong. Failing the
            // emit instead would turn a bookkeeping gap into a dropped binding.
            Fragments.AbsorbFrom(source.Fragments, start);
        }

        /// <summary>
        /// The current end-of-buffer character offset, flushed so pending output is
        /// materialized. Recorded at namespace and top-level-type boundaries so
        /// <see cref="StringEmitter"/> can slice the byte-identical combined output into
        /// one file per top-level type without changing a single character of what each
        /// handler emitted. Pending indentation for the next (not-yet-started) line is
        /// not counted — the same convention <see cref="Checkpoint"/> relies on — so
        /// boundaries captured at WriteLine points are exact.
        /// </summary>
        public int CurrentOffset
        {
            get
            {
                Flush();
                return _innerWriter.GetStringBuilder().Length;
            }
        }

        /// <summary>
        /// A position in a writer's output buffer that <see cref="RollbackTo"/> can return to.
        /// Captures the buffer length and the indent depth so a rolled-back writer resumes
        /// exactly where it was when the checkpoint was taken, plus the writer that issued it —
        /// now that C# and Swift checkpoints are the same type and travel together through the
        /// same rollback sites, handing one writer the other's checkpoint would otherwise
        /// silently truncate to an unrelated offset.
        /// </summary>
        public readonly struct WriterCheckpoint
        {
            internal WriterCheckpoint(
                BufferedSourceWriter owner, int length, int indent, FragmentRecorder.RecorderCheckpoint fragments)
            {
                Owner = owner;
                Length = length;
                Indent = indent;
                Fragments = fragments;
            }

            internal BufferedSourceWriter? Owner { get; }
            internal int Length { get; }
            internal int Indent { get; }

            /// <summary>
            /// The fragment-recorder position at the same instant. Erasing text without erasing the
            /// boundaries recorded inside it would leave intervals pointing past the truncated
            /// buffer, and the next boundary would appear to move backwards.
            /// </summary>
            internal FragmentRecorder.RecorderCheckpoint Fragments { get; }
        }

        /// <summary>
        /// Captures the current end-of-buffer position so a later <see cref="RollbackTo"/>
        /// can discard everything written in between. The wrapper-symbol contract path uses
        /// this to emit a member into the live buffers and then erase it byte-for-byte when
        /// the contract trips mid-emission — an in-emission transactional rollback that
        /// replaces the old generate-then-regex-strip recovery. Required for the method
        /// site because async <c>@_cdecl</c> wrappers register their symbol *inside*
        /// <c>EmitMethod</c> (after the public signature is already written), so a
        /// predict-before-emit gate cannot tell a valid async method from a silent bail.
        /// </summary>
        public WriterCheckpoint Checkpoint()
        {
            Flush();
            return new WriterCheckpoint(
                this, _innerWriter.GetStringBuilder().Length, Indent, Fragments.Checkpoint());
        }

        /// <summary>
        /// Truncates the output buffer back to <paramref name="checkpoint"/> and restores
        /// the indent depth, discarding everything written since the checkpoint was taken.
        /// </summary>
        public void RollbackTo(WriterCheckpoint checkpoint)
        {
            EnsureOwned(checkpoint);
            Flush();
            var builder = _innerWriter.GetStringBuilder();
            if (builder.Length > checkpoint.Length)
            {
                builder.Length = checkpoint.Length;
            }
            Indent = checkpoint.Indent;
            Fragments.RollbackTo(checkpoint.Fragments);
        }

        /// <summary>
        /// Truncates the buffer back to <paramref name="checkpoint"/> exactly like
        /// <see cref="RollbackTo"/>, but first returns everything written since. A caller uses this
        /// to inject a prefix ahead of an already-emitted member — e.g. a compile-time-visible
        /// <c>[Obsolete(error: true)]</c> attribute in front of a member whose signature (and, for
        /// async, whose faulting body) is already written and cannot be re-emitted without
        /// duplicating its Swift side. Re-append the returned text verbatim with
        /// <see cref="AppendCaptured"/> after emitting the prefix.
        /// </summary>
        public string RollbackToAndCapture(WriterCheckpoint checkpoint)
        {
            EnsureOwned(checkpoint);
            Flush();
            var builder = _innerWriter.GetStringBuilder();
            string captured = string.Empty;
            if (builder.Length > checkpoint.Length)
            {
                captured = builder.ToString(checkpoint.Length, builder.Length - checkpoint.Length);
                builder.Length = checkpoint.Length;
            }
            Indent = checkpoint.Indent;
            // The captured text is re-appended verbatim after a prefix is written, so it lands at a
            // different offset than it was recorded at. Lift its boundaries out rebased to the start
            // of the captured region and hold them until AppendCaptured says where the text went;
            // dropping them instead would collapse the whole re-appended member onto its container,
            // which is precisely the resolution this map exists to provide.
            _capturedFragments = Fragments.RollbackToAndCapture(checkpoint.Fragments, checkpoint.Length);
            return captured;
        }

        /// <summary>
        /// Appends text captured by <see cref="RollbackToAndCapture"/> verbatim, bypassing indent
        /// processing — the captured text already carries its own leading indentation, so re-running
        /// it through the indenting writer would double-indent every line.
        /// </summary>
        public void AppendCaptured(string captured)
        {
            Flush();
            var start = _innerWriter.GetStringBuilder().Length;
            _innerWriter.GetStringBuilder().Append(captured);
            Fragments.ReplayCaptured(_capturedFragments, start);
            _capturedFragments = default;
        }

        /// <summary>
        /// Offsets of lines already written through <see cref="TryWriteLineOnce"/>, keyed by exact
        /// line text (indentation excluded). Lets a caller emit a line at most once per buffer —
        /// e.g. two sibling declarations that both skip for the same reason would otherwise leave
        /// adjacent identical <c>// Unsupported:</c> tombstones.
        /// </summary>
        private readonly Dictionary<string, int> _onceLines = new();

        /// <summary>
        /// Writes <paramref name="line"/> unless this buffer already contains an identical line
        /// written through this method. Returns true when the line was written, false when it was
        /// suppressed as a duplicate. A recorded line can be erased by <see cref="RollbackTo"/>, so
        /// suppression re-verifies the text is still present at its recorded offset — a rolled-back
        /// line is re-emitted, never silently dropped.
        /// </summary>
        public bool TryWriteLineOnce(string line)
        {
            ArgumentNullException.ThrowIfNull(line);
            Flush();
            var builder = _innerWriter.GetStringBuilder();
            if (_onceLines.TryGetValue(line, out var offset)
                && offset + line.Length <= builder.Length
                && MatchesAt(builder, line, offset))
            {
                return false;
            }
            WriteLine(line);
            Flush();
            // The line text sits just before the trailing newline; indentation (written ahead of
            // the text) is excluded so the recorded offset points at the text itself.
            _onceLines[line] = builder.Length - CoreNewLine.Length - line.Length;
            return true;
        }

        private static bool MatchesAt(System.Text.StringBuilder builder, string text, int offset)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (builder[offset + i] != text[i])
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Rejects a checkpoint issued by a different writer (or a default-constructed one).
        /// Truncating to a foreign offset would silently delete unrelated output — a corrupted
        /// file rather than a dropped member — so this fails loudly instead.
        /// </summary>
        private void EnsureOwned(in WriterCheckpoint checkpoint)
        {
            if (!ReferenceEquals(checkpoint.Owner, this))
            {
                throw new InvalidOperationException(
                    $"A {GetType().Name} checkpoint was rolled back on a different writer " +
                    $"({checkpoint.Owner?.GetType().Name ?? "<uninitialized>"}). Checkpoints are " +
                    "per-writer positions and are not interchangeable.");
            }
        }
    }

    /// <summary>
    /// Represents an class for writing C# source code.
    /// </summary>
    public class CSharpWriter : BufferedSourceWriter
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="CSharpWriter"/> class.
        /// </summary>
        /// <param name="writer">The writer.</param>
        public CSharpWriter(StringWriter writer) : base(writer)
        {
        }
    }

    /// <summary>
    /// Represents an class for writing Swift source code.
    /// </summary>
    public class SwiftWriter : BufferedSourceWriter
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SwiftWriter"/> class.
        /// </summary>
        /// <param name="writer">The writer.</param>
        public SwiftWriter(StringWriter writer) : base(writer)
        {
        }

        /// <summary>
        /// <para>
        /// True when everything written to this writer is thrown away — the writer exists only so
        /// the C# emission paths can run unchanged for a type whose Swift wrapper source must not
        /// be produced (a module-internal type the separately-compiled wrapper module cannot name).
        /// </para>
        /// <para>
        /// It is the machine-checkable form of the plan/emit contract: a C# <c>[LibraryImport]</c>
        /// whose entry point is an <c>SBW_</c> wrapper symbol may only be planned when the Swift
        /// plane that would define that symbol is live. Emission paths that claim a wrapper symbol
        /// must therefore consult this — a discarding writer is otherwise indistinguishable from a
        /// real one (it is non-null, and every Write call succeeds), which is exactly how a planner
        /// comes to emit externs for wrappers that were silently dropped.
        /// </para>
        /// <para>
        /// Only a writer constructed purely to swallow output sets this. The deferred/merge
        /// <c>StringWriter</c> buffers used by ordinary emission paths are real output that is
        /// spliced back into the module later, and must NOT be marked discarding.
        /// </para>
        /// </summary>
        public bool IsDiscarding { get; init; }
    }
}
