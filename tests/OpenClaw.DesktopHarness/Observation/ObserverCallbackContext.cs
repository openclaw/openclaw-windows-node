#nullable enable
using System;
using System.Collections.Generic;

// Used on the hook-installing thread. Reentrant callbacks must not replace their parent's context.
public sealed class ObserverCallbackContext
{
    private readonly Stack<Frame> active = new();
    public Frame Enter(long sequence)
    {
        var frame = new Frame(this, sequence, active.Count + 1,
            active.Count == 0 ? null : active.Peek().Sequence);
        active.Push(frame);
        return frame;
    }
    public sealed class Frame : IDisposable
    {
        private readonly ObserverCallbackContext owner;
        private bool disposed;
        public long Sequence { get; }
        public int Depth { get; }
        public long? ParentSequence { get; }
        internal Frame(ObserverCallbackContext owner, long sequence, int depth, long? parent)
        {
            this.owner = owner;
            Sequence = sequence;
            Depth = depth;
            ParentSequence = parent;
        }
        public void Dispose()
        {
            if (disposed) return;
            if (owner.active.Count == 0 || !ReferenceEquals(owner.active.Peek(), this))
                throw new InvalidOperationException("Callback context completion is not nested.");
            owner.active.Pop();
            disposed = true;
        }
    }
}
