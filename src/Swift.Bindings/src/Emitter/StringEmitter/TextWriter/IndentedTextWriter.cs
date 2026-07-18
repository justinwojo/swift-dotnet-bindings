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
        /// Initializes a new instance of the <see cref="BufferedSourceWriter"/> class.
        /// </summary>
        /// <param name="writer">The backing in-memory writer.</param>
        protected BufferedSourceWriter(StringWriter writer) : base(writer)
        {
            _innerWriter = writer;
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
            internal WriterCheckpoint(BufferedSourceWriter owner, int length, int indent)
            {
                Owner = owner;
                Length = length;
                Indent = indent;
            }

            internal BufferedSourceWriter? Owner { get; }
            internal int Length { get; }
            internal int Indent { get; }
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
            return new WriterCheckpoint(this, _innerWriter.GetStringBuilder().Length, Indent);
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
            _innerWriter.GetStringBuilder().Append(captured);
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
    }
}
