using System;
using System.Collections.Generic;

namespace MiYue.Core.Engine
{
    /// <summary>
    /// Per-device FIFO of HTTP transfers with at most <see cref="MaxInflight"/> in flight (the C4 Soap
    /// queue, MaxInflight = 2). A transfer whose completion never arrives is failed by a watchdog so the
    /// poll chain always gets its callback. Strand-only.
    /// </summary>
    public sealed class RequestQueue
    {
        public const int DefaultMaxInflight = 2;
        /// <summary>Extra grace on top of the request timeout before the watchdog declares a transfer lost.</summary>
        public const int WatchdogGraceMs = 10000;

        private readonly Runtime _rt;
        private readonly Queue<Job> _waiting = new Queue<Job>();
        private int _inflight;

        public int MaxInflight { get; set; }

        public RequestQueue(Runtime rt)
        {
            _rt = rt;
            MaxInflight = DefaultMaxInflight;
        }

        public int Inflight
        {
            get { return _inflight; }
        }

        public int Waiting
        {
            get { return _waiting.Count; }
        }

        public void Enqueue(HttpRequestData req, Action<HttpResponseData> done)
        {
            var job = new Job { Request = req, Done = done };
            if (_inflight < MaxInflight) Start(job);
            else _waiting.Enqueue(job);
        }

        /// <summary>Fail everything still waiting (callbacks run now with a transport failure);
        /// transfers already in flight complete normally.</summary>
        public void Reset(string reason)
        {
            var pending = new List<Job>(_waiting);
            _waiting.Clear();
            foreach (var j in pending) SafeInvoke(j, HttpResponseData.Failed(reason));
        }

        private void Start(Job job)
        {
            _inflight++;
            job.Watchdog = _rt.After(job.Request.TimeoutMs + WatchdogGraceMs, () =>
                Finish(job, HttpResponseData.Failed("stale transfer (no completion)")));
            try
            {
                _rt.Transport.Send(job.Request, resp => _rt.Strand.Post(() => Finish(job, resp ?? HttpResponseData.Failed("null response"))));
            }
            catch (Exception e)
            {
                _rt.Strand.Post(() => Finish(job, HttpResponseData.Failed("transport exception: " + e.Message)));
            }
        }

        private void Finish(Job job, HttpResponseData resp)
        {
            if (job.Finished) return; // late completion after the watchdog (or vice versa)
            job.Finished = true;
            if (job.Watchdog != null) job.Watchdog.Cancel();
            _inflight--;
            if (_inflight < 0) _inflight = 0;
            // keep FIFO: queued jobs start before the callback issues new ones
            while (_inflight < MaxInflight && _waiting.Count > 0) Start(_waiting.Dequeue());
            SafeInvoke(job, resp);
        }

        private void SafeInvoke(Job job, HttpResponseData resp)
        {
            try
            {
                if (job.Done != null) job.Done(resp);
            }
            catch (Exception e)
            {
                _rt.Log.Error("MiYue: request callback error: " + e);
            }
        }

        private sealed class Job
        {
            public HttpRequestData Request;
            public Action<HttpResponseData> Done;
            public ITimerHandle Watchdog;
            public bool Finished;
        }
    }
}
