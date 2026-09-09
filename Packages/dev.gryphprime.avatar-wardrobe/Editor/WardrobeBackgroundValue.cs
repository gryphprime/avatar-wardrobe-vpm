using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace OutfitToggleGenerator
{
    // Single caller owns this cache; only its pure loader runs on a worker.
    // Retain the last good value on failure and never block a repaint waiting for I/O.
    internal sealed class WardrobeBackgroundValue<T>
    {
        private Task<T> pending;
        private T value;
        private long next;
        internal Exception Error { get; private set; }
        internal T Get(Func<T> load, double refreshSeconds = 1)
        {
            if (pending != null && pending.IsCompleted)
            {
                if (pending.IsFaulted) Error = pending.Exception.GetBaseException();
                else if (!pending.IsCanceled) { value = pending.Result; Error = null; }
                pending = null;
                next = Stopwatch.GetTimestamp() + (long)(refreshSeconds * Stopwatch.Frequency);
            }
            if (pending == null && Stopwatch.GetTimestamp() >= next) pending = Task.Run(load);
            return value;
        }
    }
}
