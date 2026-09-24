using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MiYue.Core.Engine;

namespace MiYue.Core.Tests.Support
{
    public static class Fixtures
    {
        public static string Dir => Path.Combine(AppContext.BaseDirectory, "Fixtures");

        public static string Text(string name) => File.ReadAllText(Path.Combine(Dir, name), Encoding.UTF8);

        public static byte[] Bytes(string name) => File.ReadAllBytes(Path.Combine(Dir, name));

        static Fixtures()
        {
            // GBK for mojibake repair / mixed decoding (.NET Framework on Windows has it natively)
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            MiYue.Core.Util.TextDecoder.ResetGbkProbe();
        }

        public static void EnsureGbk()
        {
            _ = Dir;
        }
    }

    /// <summary>Virtual clock: timers fire only when the test advances time.</summary>
    public sealed class ManualScheduler : IScheduler
    {
        private readonly List<Entry> _timers = new List<Entry>();
        private long _seq;

        public long NowMs { get; private set; } = 1_000_000;

        public ITimerHandle Schedule(int dueMs, Action callback)
        {
            var e = new Entry { Due = NowMs + Math.Max(1, dueMs), Callback = callback, Seq = _seq++ };
            _timers.Add(e);
            return e;
        }

        public int PendingTimers => _timers.Count(t => !t.Cancelled);

        /// <summary>Advance virtual time, firing due timers in (due, creation) order.</summary>
        public void Advance(int ms)
        {
            long target = NowMs + ms;
            while (true)
            {
                var next = _timers.Where(t => !t.Cancelled && t.Due <= target).OrderBy(t => t.Due).ThenBy(t => t.Seq).FirstOrDefault();
                if (next == null) break;
                _timers.Remove(next);
                NowMs = Math.Max(NowMs, next.Due);
                next.Callback();
            }
            _timers.RemoveAll(t => t.Cancelled);
            NowMs = target;
        }

        private sealed class Entry : ITimerHandle
        {
            public long Due;
            public long Seq;
            public Action Callback;
            public bool Cancelled;

            public void Cancel() => Cancelled = true;
        }
    }

    public sealed class ListLog : ILog
    {
        public readonly List<string> Lines = new List<string>();
        public void Debug(string message) => Lines.Add("D " + message);
        public void Error(string message) => Lines.Add("E " + message);
    }

    /// <summary>Records every output change the engine emits.</summary>
    public sealed class RecordingSink : IOutputSink
    {
        public readonly Dictionary<ushort, bool> D = new Dictionary<ushort, bool>();
        public readonly Dictionary<ushort, ushort> A = new Dictionary<ushort, ushort>();
        public readonly Dictionary<ushort, string> S = new Dictionary<ushort, string>();
        public readonly List<string> Events = new List<string>();

        public void Digital(ushort index, bool value)
        {
            D[index] = value;
            Events.Add("D" + index + "=" + (value ? 1 : 0));
        }

        public void Analog(ushort index, ushort value)
        {
            A[index] = value;
            Events.Add("A" + index + "=" + value);
        }

        public void Serial(ushort index, string value)
        {
            S[index] = value;
            Events.Add("S" + index + "=" + value);
        }

        public bool Dig(ushort i) => D.TryGetValue(i, out var v) && v;
        public ushort Ana(ushort i) => A.TryGetValue(i, out var v) ? v : (ushort)0;
        public string Ser(ushort i) => S.TryGetValue(i, out var v) ? v : null;
    }

    /// <summary>A runtime wired to fakes: inline strand, manual clock, fake transport with simulated players.</summary>
    public sealed class Rig
    {
        public readonly ManualScheduler Clock = new ManualScheduler();
        public readonly FakeNetwork Net = new FakeNetwork();
        public readonly ListLog Log = new ListLog();
        public readonly Runtime Rt;

        public Rig()
        {
            Fixtures.EnsureGbk();
            Rt = new Runtime(Net, Clock, InlineDispatcher.Instance, Log);
        }

        public FakeMiyue AddDevice(string ip, int port = 49495, string name = "餐厅")
        {
            var d = new FakeMiyue(ip, port, name);
            Net.Devices[ip] = d;
            return d;
        }

        public (PlayerEngine player, RecordingSink sink) StartPlayer(string ip, int pollSeconds = 3)
        {
            var p = new PlayerEngine(Rt);
            var sink = new RecordingSink();
            p.Start(ip, pollSeconds, 5, sink);
            Settle();
            return (p, sink);
        }

        /// <summary>Let resolve + one full poll cycle complete.</summary>
        public void Settle(int ms = 1000) => Clock.Advance(ms);
    }
}
