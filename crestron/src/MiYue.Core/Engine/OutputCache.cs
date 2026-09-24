using System;
using System.Collections.Generic;

namespace MiYue.Core.Engine
{
    /// <summary>
    /// Change detection for SIMPL+ outputs: the engine writes the full desired output state as often
    /// as it likes, only values that actually changed reach the sink (and so the SIMPL+ thread).
    /// <see cref="Resync"/> re-sends everything once (after a reconnect or a Refresh press).
    /// Strand-only.
    /// </summary>
    public sealed class OutputCache
    {
        private readonly Dictionary<ushort, bool> _digital = new Dictionary<ushort, bool>();
        private readonly Dictionary<ushort, ushort> _analog = new Dictionary<ushort, ushort>();
        private readonly Dictionary<ushort, string> _serial = new Dictionary<ushort, string>();
        private readonly ILog _log;
        private IOutputSink _sink;

        public OutputCache(ILog log)
        {
            _log = log ?? NullLog.Instance;
        }

        public IOutputSink Sink
        {
            get { return _sink; }
            set { _sink = value; }
        }

        public void Digital(ushort index, bool value)
        {
            bool old;
            if (_digital.TryGetValue(index, out old) && old == value) return;
            _digital[index] = value;
            var s = _sink;
            if (s == null) return;
            try
            {
                s.Digital(index, value);
            }
            catch (Exception e)
            {
                _log.Error("MiYue: digital output " + index + " error: " + e.Message);
            }
        }

        public void Analog(ushort index, int value)
        {
            ushort v = (ushort)Math.Max(0, Math.Min(65535, value));
            ushort old;
            if (_analog.TryGetValue(index, out old) && old == v) return;
            _analog[index] = v;
            var s = _sink;
            if (s == null) return;
            try
            {
                s.Analog(index, v);
            }
            catch (Exception e)
            {
                _log.Error("MiYue: analog output " + index + " error: " + e.Message);
            }
        }

        public void Serial(ushort index, string value)
        {
            value = value ?? string.Empty;
            string old;
            if (_serial.TryGetValue(index, out old) && string.Equals(old, value, StringComparison.Ordinal)) return;
            _serial[index] = value;
            var s = _sink;
            if (s == null) return;
            try
            {
                s.Serial(index, value);
            }
            catch (Exception e)
            {
                _log.Error("MiYue: serial output " + index + " error: " + e.Message);
            }
        }

        /// <summary>Forget every cached value so the next write of each output is sent again.</summary>
        public void Resync()
        {
            _digital.Clear();
            _analog.Clear();
            _serial.Clear();
        }

        public bool TryGetDigital(ushort index, out bool value)
        {
            return _digital.TryGetValue(index, out value);
        }

        public bool TryGetAnalog(ushort index, out ushort value)
        {
            return _analog.TryGetValue(index, out value);
        }

        public bool TryGetSerial(ushort index, out string value)
        {
            return _serial.TryGetValue(index, out value);
        }
    }
}
