using System;
using Crestron.SimplSharp;
using MiYue.Core.Engine;
using MiYue.Core.Util;

namespace MiYue.Crestron
{
    /// <summary>SIMPL+ callback: digital output [index] changed (value 0/1).</summary>
    public delegate void DigitalOutputHandler(ushort index, ushort value);

    /// <summary>SIMPL+ callback: analog output [index] changed.</summary>
    public delegate void AnalogOutputHandler(ushort index, ushort value);

    /// <summary>SIMPL+ callback: serial output [index] changed.</summary>
    public delegate void SerialOutputHandler(ushort index, SimplSharpString value);

    /// <summary>
    /// Shared SIMPL+ plumbing: turns engine output changes into the three delegates and converts strings
    /// for the program's encoding. Every method returns immediately (the engine only posts to its strand).
    /// </summary>
    public abstract class SimplModuleBase : IOutputSink
    {
        /// <summary>Longest string sent to SIMPL+ (cover URLs can be long; titles are far shorter).</summary>
        public const int MaxSerialLength = 250;

        private bool _unicode = true;
        private bool _debugRegistered;

        public DigitalOutputHandler OnDigital { get; set; }
        public AnalogOutputHandler OnAnalog { get; set; }
        public SerialOutputHandler OnSerial { get; set; }

        protected void Configure(ushort debug, ushort unicodeText)
        {
            _unicode = unicodeText != 0;
            CrestronLog.SetDebug(debug != 0, ref _debugRegistered);
        }

        protected void ReleaseDebug()
        {
            CrestronLog.SetDebug(false, ref _debugRegistered);
        }

        void IOutputSink.Digital(ushort index, bool value)
        {
            var h = OnDigital;
            if (h != null) h(index, (ushort)(value ? 1 : 0));
        }

        void IOutputSink.Analog(ushort index, ushort value)
        {
            var h = OnAnalog;
            if (h != null) h(index, value);
        }

        void IOutputSink.Serial(ushort index, string value)
        {
            var h = OnSerial;
            if (h == null) return;
            value = SimplText.Clip(value ?? string.Empty, MaxSerialLength);
            // Unicode_Text=1: real UTF-16 (the SIMPL program / module runs with UTF-16 string encoding).
            // Unicode_Text=0: UTF-8 bytes packed one per char for ASCII-encoded programs.
            h(index, _unicode
                ? new SimplSharpString(value, CrestronStringEncoding.eEncodingUTF16)
                : new SimplSharpString(SimplText.ToUtf8Bytes(value)));
        }
    }

    /// <summary>
    /// SIMPL+ facade of one MiYue player ("MiYue Player v1.0.usp"). Signal indices: MiYue.Core.Engine.PlayerSignals.
    /// </summary>
    public class MiYuePlayer : SimplModuleBase
    {
        private PlayerEngine _engine;

        /// <summary>SIMPL+ requires a default constructor.</summary>
        public MiYuePlayer()
        {
        }

        /// <summary>Start (or restart) the player. Returns at once; connection happens in the background.</summary>
        public void Initialize(string ipAddress, ushort pollSeconds, ushort volumeStep, ushort debug, ushort unicodeText)
        {
            try
            {
                Configure(debug, unicodeText);
                if (_engine == null) _engine = new PlayerEngine(Shared.Runtime);
                _engine.Start(SimplText.FromSimpl(ipAddress ?? string.Empty).Trim(), pollSeconds, volumeStep, this);
            }
            catch (Exception e)
            {
                Shared.Log.Error("MiYuePlayer.Initialize: " + e);
            }
        }

        public void SetDigital(ushort index, ushort value)
        {
            var e = _engine;
            if (e != null) e.Digital(index, value != 0);
        }

        public void SetAnalog(ushort index, ushort value)
        {
            var e = _engine;
            if (e != null) e.Analog(index, value);
        }

        public void SetSerial(ushort index, string value)
        {
            var e = _engine;
            if (e != null) e.Serial(index, SimplText.FromSimpl(value ?? string.Empty));
        }

        public void Shutdown()
        {
            var e = _engine;
            if (e != null) e.Stop();
            ReleaseDebug();
        }
    }

    /// <summary>
    /// SIMPL+ facade of the list / browse module ("MiYue Browser v1.0.usp"), attached to the MiYuePlayer
    /// with the same IP address in this program. Signal indices: MiYue.Core.Engine.BrowserSignals.
    /// </summary>
    public class MiYueBrowser : SimplModuleBase
    {
        private BrowserEngine _engine;

        public MiYueBrowser()
        {
        }

        public void Initialize(string playerIpAddress, ushort pageSize, ushort debug, ushort unicodeText)
        {
            try
            {
                Configure(debug, unicodeText);
                if (_engine == null) _engine = new BrowserEngine(Shared.Runtime);
                _engine.Start(SimplText.FromSimpl(playerIpAddress ?? string.Empty).Trim(), pageSize, this);
            }
            catch (Exception e)
            {
                Shared.Log.Error("MiYueBrowser.Initialize: " + e);
            }
        }

        public void SetDigital(ushort index, ushort value)
        {
            var e = _engine;
            if (e != null) e.Digital(index, value != 0);
        }

        public void SetAnalog(ushort index, ushort value)
        {
            var e = _engine;
            if (e != null) e.Analog(index, value);
        }

        public void Shutdown()
        {
            var e = _engine;
            if (e != null) e.Stop();
            ReleaseDebug();
        }
    }
}
