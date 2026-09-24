using System;
using System.Collections.Generic;

namespace MiYue.Core.Engine
{
    /// <summary>
    /// Shared services for every player and browser in one program: the strand, timers, HTTP transport,
    /// logging, the last-good-port memory and the player registry used by sync groups and the browse module.
    /// The Crestron adapter keeps ONE static Runtime per program slot.
    /// </summary>
    public sealed class Runtime
    {
        public Strand Strand { get; private set; }
        public IScheduler Scheduler { get; private set; }
        public IHttpTransport Transport { get; private set; }
        public ILog Log { get; private set; }
        public GroupManager Groups { get; private set; }

        private readonly Dictionary<string, int> _lastGoodPort = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly List<PlayerEngine> _players = new List<PlayerEngine>();

        public Runtime(IHttpTransport transport, IScheduler scheduler, IWorkDispatcher dispatcher, ILog log)
        {
            if (transport == null) throw new ArgumentNullException("transport");
            if (scheduler == null) throw new ArgumentNullException("scheduler");
            Transport = transport;
            Scheduler = scheduler;
            Log = log ?? NullLog.Instance;
            Strand = new Strand(dispatcher, Log);
            Groups = new GroupManager(this);
        }

        /// <summary>One-shot timer whose callback runs on the strand. Cancel() is safe from the strand.</summary>
        public ITimerHandle After(int ms, Action action)
        {
            var handle = new StrandTimer();
            handle.Inner = Scheduler.Schedule(Math.Max(1, ms), () => Strand.Post(() =>
            {
                if (handle.Cancelled) return;
                handle.Cancelled = true;
                action();
            }));
            return handle;
        }

        public long NowMs
        {
            get { return Scheduler.NowMs; }
        }

        // -- last good port (per IP, survives module restarts within the program) ---------------------
        public int LastGoodPort(string ip)
        {
            int p;
            return ip != null && _lastGoodPort.TryGetValue(ip, out p) ? p : 0;
        }

        public void RememberPort(string ip, int port)
        {
            if (!string.IsNullOrEmpty(ip)) _lastGoodPort[ip] = port;
        }

        // -- player registry (strand only) -------------------------------------------------------
        public IList<PlayerEngine> Players
        {
            get { return _players.AsReadOnly(); }
        }

        internal void Register(PlayerEngine p)
        {
            if (!_players.Contains(p)) _players.Add(p);
        }

        internal void Unregister(PlayerEngine p)
        {
            _players.Remove(p);
        }

        /// <summary>Find a registered player by its configured IP address (or its device IP).</summary>
        public PlayerEngine FindPlayer(string ipOrKey)
        {
            if (string.IsNullOrEmpty(ipOrKey)) return null;
            var key = ipOrKey.Trim();
            foreach (var p in _players)
                if (string.Equals(p.Ip, key, StringComparison.OrdinalIgnoreCase)) return p;
            foreach (var p in _players)
                if (string.Equals(p.DeviceName, key, StringComparison.Ordinal)) return p;
            return null;
        }

        private sealed class StrandTimer : ITimerHandle
        {
            public ITimerHandle Inner;
            public volatile bool Cancelled;

            public void Cancel()
            {
                Cancelled = true;
                var i = Inner;
                if (i != null)
                {
                    try
                    {
                        i.Cancel();
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }
    }
}
