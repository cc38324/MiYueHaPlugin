using System;
using System.Collections.Generic;

namespace MiYue.Core.Engine
{
    /// <summary>
    /// Serial executor: every state mutation of every player/browser runs here, one action at a time,
    /// in FIFO order. SIMPL+ entry points only Post() and return at once (nothing may block the SIMPL+
    /// thread); HTTP completions and timer ticks are posted here too, so the engine needs no locks.
    /// Draining happens on a work-dispatcher thread (CrestronInvoke on the processor, inline in tests).
    /// </summary>
    public sealed class Strand
    {
        private readonly object _lock = new object();
        private readonly Queue<Action> _queue = new Queue<Action>();
        private readonly IWorkDispatcher _dispatcher;
        private readonly ILog _log;
        private bool _draining;

        public Strand(IWorkDispatcher dispatcher, ILog log)
        {
            _dispatcher = dispatcher ?? InlineDispatcher.Instance;
            _log = log ?? NullLog.Instance;
        }

        public void Post(Action action)
        {
            if (action == null) return;
            bool start;
            lock (_lock)
            {
                _queue.Enqueue(action);
                start = !_draining;
                if (start) _draining = true;
            }
            if (!start) return;
            try
            {
                _dispatcher.Dispatch(Drain);
            }
            catch (Exception e)
            {
                _log.Error("Strand: dispatch failed, draining inline: " + e.Message);
                Drain();
            }
        }

        private void Drain()
        {
            while (true)
            {
                Action next;
                lock (_lock)
                {
                    if (_queue.Count == 0)
                    {
                        _draining = false;
                        return;
                    }
                    next = _queue.Dequeue();
                }
                try
                {
                    next();
                }
                catch (Exception e)
                {
                    _log.Error("MiYue: unhandled error: " + e);
                }
            }
        }
    }
}
