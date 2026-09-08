using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace OutfitToggleGenerator
{
    // No Unity dependencies: only Pump executes work; callers may wait on other threads.
    internal sealed class WardrobeWorkQueue
    {
        private interface IWork { void Execute(); void Cancel(Exception reason); }
        private sealed class Work<T> : IWork
        {
            internal readonly TaskCompletionSource<T> Completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly Func<T> run;
            private readonly long deadline;
            private int state; // 0 queued, 1 running, 2 completed/cancelled
            internal Work(Func<T> run, TimeSpan timeout)
            {
                this.run = run;
                deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
            }
            public void Execute()
            {
                if (Stopwatch.GetTimestamp() > deadline) { Cancel(new TimeoutException("Unity did not start the command in time; nothing was changed.")); return; }
                if (Interlocked.CompareExchange(ref state, 1, 0) != 0) return;
                try { Completion.TrySetResult(run()); }
                catch (Exception error) { Completion.TrySetException(error); }
                finally { Volatile.Write(ref state, 2); }
            }
            public void Cancel(Exception reason)
            {
                if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
                    Completion.TrySetException(reason);
            }
        }
        private readonly object gate = new object();
        private readonly Queue<IWork> interactive = new Queue<IWork>();
        private readonly Queue<IWork> operations = new Queue<IWork>();
        private readonly Queue<IWork> background = new Queue<IWork>();
        private bool closed;
        internal int Count { get { lock (gate) return interactive.Count + operations.Count + background.Count; } }

        // Accept without holding an HTTP request open. Only Pump touches Unity.
        internal Task<T> EnqueueOperation<T>(Func<T> run, TimeSpan? queueTimeout = null)
        {
            var work = new Work<T>(run, queueTimeout ?? TimeSpan.FromMinutes(5));
            lock (gate)
            {
                if (closed) throw new OperationCanceledException("Wardrobe server stopped.");
                if (interactive.Count + operations.Count + background.Count >= 128)
                    throw new InvalidOperationException("Wardrobe is busy. Keep the operation in the desktop queue.");
                operations.Enqueue(work);
            }
            return work.Completion.Task;
        }

        internal T Invoke<T>(Func<T> run, bool isBackground = false, TimeSpan? queueTimeout = null)
        {
            var timeout = queueTimeout ?? TimeSpan.FromSeconds(30);
            var work = new Work<T>(run, timeout);
            lock (gate)
            {
                if (closed) throw new OperationCanceledException("Wardrobe server stopped.");
                if (interactive.Count + operations.Count + background.Count >= 128)
                    throw new InvalidOperationException("Wardrobe is busy. Try again after current work finishes.");
                (isBackground ? background : interactive).Enqueue(work);
            }
            // Expire only work that has NOT started. A running edit cannot be cancelled safely
            // by an HTTP timeout. Exceptions are propagated to the request, never converted to null.
            using (var timer = new CancellationTokenSource())
            {
                if (Task.WhenAny(work.Completion.Task, Task.Delay(timeout, timer.Token)).GetAwaiter().GetResult() != work.Completion.Task)
                    work.Cancel(new TimeoutException("Unity did not start the command in time; nothing was changed."));
                timer.Cancel();
                return work.Completion.Task.GetAwaiter().GetResult();
            }
        }

        internal void Pump(bool allowBackground, double budgetMs = 4, int maxInteractive = 8, Action idleBackground = null)
        {
            var watch = Stopwatch.StartNew();
            for (var n = 0; n < maxInteractive; n++)
            {
                IWork work;
                lock (gate)
                {
                    if (interactive.Count == 0) break;
                    work = interactive.Dequeue();
                }
                work.Execute();
                if (watch.Elapsed.TotalMilliseconds >= budgetMs) return;
            }
            IWork operation = null;
            lock (gate)
                if (allowBackground && operations.Count > 0) operation = operations.Dequeue();
            if (operation != null) { operation.Execute(); return; }
            IWork low = null;
            lock (gate)
                if (allowBackground && interactive.Count == 0 && background.Count > 0)
                    low = background.Dequeue();
            // At most one expensive preview per update. An individual Unity API call is not preemptible.
            if (low != null) low.Execute();
            else if (allowBackground && watch.Elapsed.TotalMilliseconds < budgetMs)
            {
                bool idle;
                lock (gate) idle = !closed && interactive.Count == 0 && operations.Count == 0 && background.Count == 0;
                if (idle) idleBackground?.Invoke();
            }
        }
        internal void Close()
        {
            lock (gate)
            {
                closed = true;
                var reason = new OperationCanceledException("Wardrobe server stopped or reloaded.");
                while (interactive.Count > 0) interactive.Dequeue().Cancel(reason);
                while (operations.Count > 0) operations.Dequeue().Cancel(reason);
                while (background.Count > 0) background.Dequeue().Cancel(reason);
            }
        }
    }
}
