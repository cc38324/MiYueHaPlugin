using System;
using System.Collections.Generic;
using MiYue.Core.Soap;
using MiYue.Core.Upnp;
using MiYue.Core.Util;

namespace MiYue.Core.Engine
{
    /// <summary>
    /// Connection to one player: port probe + description.xml, the SOAP call path, online/offline
    /// tracking and backoff re-resolve (C4 upnp.lua). Strand-only.
    ///
    ///   Start(ip)       probe last-good port first, then 49495..49520; retry with 5/15/30/60 s backoff
    ///   Call(...)       POST through the per-device RequestQueue (max 2 in flight)
    ///   3 consecutive transport failures -> offline -> re-resolve (the port may have drifted)
    /// </summary>
    public sealed class DeviceLink
    {
        public const int SoapTimeoutMs = 6000;
        public const int ProbeTimeoutMs = 2000;
        public const int MaxTransportFailures = 3;

        private readonly Runtime _rt;
        private readonly RequestQueue _queue;
        private ITimerHandle _retryTimer;
        private int _resolveAttempts;
        private int _resolveGeneration;
        private bool _stopped = true;
        private int _failures;

        public string Ip { get; private set; }
        public int Port { get; private set; }
        public bool Online { get; private set; }
        public bool Resolving { get; private set; }
        public DeviceDescription Description { get; private set; }
        public string Status { get; private set; }

        /// <summary>Raised on the strand when the device (re)appears.</summary>
        public event Action WentOnline;
        /// <summary>Raised on the strand when the device is lost.</summary>
        public event Action WentOffline;
        /// <summary>Human-readable status text changed.</summary>
        public event Action<string> StatusChanged;

        public DeviceLink(Runtime rt)
        {
            _rt = rt;
            _queue = new RequestQueue(rt);
            Status = "未配置 / Not configured";
        }

        public string BaseUrl
        {
            get { return "http://" + Ip + ":" + Port; }
        }

        public RequestQueue Queue
        {
            get { return _queue; }
        }

        public void Start(string ip)
        {
            Stop();
            Ip = (ip ?? string.Empty).Trim();
            _stopped = false;
            _resolveAttempts = 0;
            if (Ip.Length == 0)
            {
                SetStatus("未配置 IP / No IP address configured");
                return;
            }
            Resolve();
        }

        public void Stop()
        {
            _stopped = true;
            _resolveGeneration++;
            Resolving = false;
            CancelRetry();
            _queue.Reset("stopped");
            if (Online)
            {
                Online = false;
                Raise(WentOffline);
            }
        }

        /// <summary>Force a new probe now (e.g. a "Reconnect" button).</summary>
        public void Reresolve()
        {
            if (_stopped || string.IsNullOrEmpty(Ip)) return;
            CancelRetry();
            if (Resolving) return;
            Resolve();
        }

        // -- resolve -----------------------------------------------------------------------
        private void Resolve()
        {
            if (Resolving || _stopped) return;
            Resolving = true;
            int gen = ++_resolveGeneration;
            SetStatus("正在连接 / Connecting " + Ip + " ...");
            var ports = PortPolicy.ProbeOrder(_rt.LastGoodPort(Ip));
            ProbeNext(gen, ports, 0);
        }

        private void ProbeNext(int gen, List<int> ports, int idx)
        {
            if (gen != _resolveGeneration || _stopped) return;
            if (idx >= ports.Count)
            {
                Resolving = false;
                _resolveAttempts++;
                int delay = PortPolicy.BackoffFor(_resolveAttempts);
                SetStatus("未找到设备 / Not found at " + Ip + " (retry in " + delay + " s)");
                _rt.Log.Debug("MiYue " + Ip + ": no description.xml on ports " + PortPolicy.FirstPort + "-" + PortPolicy.LastPort + ", retry in " + delay + " s");
                ScheduleRetry(delay);
                return;
            }
            int port = ports[idx];
            var req = new HttpRequestData
            {
                Method = "GET",
                Url = PortPolicy.DescriptionUrl(Ip, port),
                TimeoutMs = ProbeTimeoutMs,
            };
            req.Headers["Connection"] = "close";
            _queue.Enqueue(req, resp =>
            {
                if (gen != _resolveGeneration || _stopped) return;
                if (resp.Completed && resp.Status == 200)
                {
                    var desc = DeviceDescription.Parse(TextDecoder.DecodeMixed(resp.Body));
                    if (desc != null)
                    {
                        OnResolved(desc, port);
                        return;
                    }
                }
                ProbeNext(gen, ports, idx + 1);
            });
        }

        private void OnResolved(DeviceDescription desc, int port)
        {
            Resolving = false;
            Description = desc;
            Port = port;
            _failures = 0;
            _resolveAttempts = 0;
            _rt.RememberPort(Ip, port);
            bool was = Online;
            Online = true;
            SetStatus("已连接 / Online " + (desc.FriendlyName.Length > 0 ? desc.FriendlyName + " " : "") + Ip + ":" + port);
            _rt.Log.Debug("MiYue " + Ip + ": resolved " + desc.Udn + " on port " + port);
            if (!was) Raise(WentOnline);
        }

        private void ScheduleRetry(int seconds)
        {
            CancelRetry();
            if (_stopped) return;
            _retryTimer = _rt.After(seconds * 1000, () =>
            {
                _retryTimer = null;
                if (!_stopped && !Resolving) Resolve();
            });
        }

        private void CancelRetry()
        {
            if (_retryTimer != null)
            {
                _retryTimer.Cancel();
                _retryTimer = null;
            }
        }

        private void GoOffline(string reason)
        {
            if (!Online) return;
            Online = false;
            _rt.Log.Error("MiYue " + Ip + ": offline (" + reason + ")");
            SetStatus("离线 / Offline " + Ip + " (" + reason + ")");
            Raise(WentOffline);
            _resolveAttempts = 0;
            ScheduleRetry(PortPolicy.BackoffFor(1));
        }

        // -- SOAP -----------------------------------------------------------------------------
        public void Call(string service, string action, SoapArgs args, Action<SoapResult> done)
        {
            if (!Online || Description == null)
            {
                Deliver(done, SoapResult.OfflineError());
                return;
            }
            Services.Endpoint ep;
            if (!Description.Endpoints.TryGetValue(service, out ep))
            {
                Deliver(done, SoapResult.HttpErrorOf(0, "unknown service " + service));
                return;
            }
            var req = new HttpRequestData
            {
                Method = "POST",
                Url = BaseUrl + ep.ControlPath,
                Body = SoapEnvelope.Build(ep.Type, action, args),
                TimeoutMs = SoapTimeoutMs,
            };
            req.Headers["Content-Type"] = SoapEnvelope.ContentType;
            req.Headers["SOAPACTION"] = SoapEnvelope.SoapActionHeader(ep.Type, action);
            // pupnp drops large POSTs riding a half-closed keep-alive connection (HA soap.py): one fresh
            // connection per request.
            req.Headers["Connection"] = "close";
            int portAtSend = Port;
            _rt.Log.Debug("MiYue " + Ip + " -> " + service + "#" + action);
            _queue.Enqueue(req, resp =>
            {
                SoapResult result;
                if (!resp.Completed)
                {
                    result = SoapResult.TransportError(resp.Error);
                    if (portAtSend == Port && Online)
                    {
                        _failures++;
                        if (_failures >= MaxTransportFailures) GoOffline(_failures + " transport failures: " + resp.Error);
                    }
                }
                else
                {
                    _failures = 0;
                    result = SoapResponse.Parse(action, resp.Status, TextDecoder.DecodeMixed(resp.Body));
                }
                if (!result.Ok) _rt.Log.Debug("MiYue " + Ip + " " + service + "#" + action + " failed: " + result);
                Deliver(done, result);
            });
        }

        private void Deliver(Action<SoapResult> done, SoapResult r)
        {
            if (done == null) return;
            try
            {
                done(r);
            }
            catch (Exception e)
            {
                _rt.Log.Error("MiYue: SOAP callback error: " + e);
            }
        }

        private void SetStatus(string text)
        {
            Status = text;
            var h = StatusChanged;
            if (h != null) h(text);
        }

        private void Raise(Action a)
        {
            if (a == null) return;
            try
            {
                a();
            }
            catch (Exception e)
            {
                _rt.Log.Error("MiYue: link event error: " + e);
            }
        }
    }
}
