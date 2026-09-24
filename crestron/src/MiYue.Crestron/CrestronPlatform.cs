using System;
using System.Collections.Generic;
using System.Text;
using Crestron.SimplSharp;
using Crestron.SimplSharp.Net.Http;
using MiYue.Core.Engine;

namespace MiYue.Crestron
{
    /// <summary>
    /// HTTP transport on Crestron.SimplSharp.Net.Http: one HttpClient per request (KeepAlive off -
    /// pupnp drops POSTs that ride a half-closed keep-alive connection), DispatchAsync so the calling
    /// thread never blocks. The core's RequestQueue caps in-flight requests at 2 per device.
    /// </summary>
    internal sealed class CrestronHttpTransport : IHttpTransport
    {
        private readonly ILog _log;

        public CrestronHttpTransport(ILog log)
        {
            _log = log;
        }

        public void Send(HttpRequestData req, Action<HttpResponseData> done)
        {
            HttpClient client = null;
            int finished = 0;
            Action<HttpResponseData> finish = r =>
            {
                if (System.Threading.Interlocked.Exchange(ref finished, 1) != 0) return;
                var c = client;
                if (c != null)
                {
                    // never dispose the client on its own callback thread
                    CrestronInvoke.BeginInvoke(o =>
                    {
                        try
                        {
                            c.Dispose();
                        }
                        catch (Exception)
                        {
                        }
                    });
                }
                done(r);
            };

            try
            {
                client = new HttpClient();
                client.KeepAlive = false;
                client.TimeoutEnabled = true;
                client.Timeout = Math.Max(1, (req.TimeoutMs + 999) / 1000); // seconds
                var hr = new HttpClientRequest();
                hr.Url.Parse(req.Url);
                hr.RequestType = string.Equals(req.Method, "POST", StringComparison.OrdinalIgnoreCase) ? RequestType.Post : RequestType.Get;
                foreach (var kv in req.Headers)
                    hr.Header.SetHeaderValue(kv.Key, kv.Value);
                if (req.Body != null)
                {
                    hr.ContentBytes = Encoding.UTF8.GetBytes(req.Body);
                    hr.ContentSource = ContentSource.ContentBytes;
                }
                var rc = client.DispatchAsync(hr, (resp, err) =>
                {
                    try
                    {
                        // A non-2xx answer (SOAP fault = HTTP 500, hidden renderer = 404) is still an HTTP
                        // response: keep it whenever a status code arrived, whatever the callback error says.
                        if (resp != null && resp.Code > 0)
                        {
                            byte[] body = null;
                            try
                            {
                                body = resp.ContentBytes;
                            }
                            catch (Exception)
                            {
                            }
                            if (body == null)
                            {
                                string s = null;
                                try
                                {
                                    s = resp.ContentString;
                                }
                                catch (Exception)
                                {
                                }
                                body = s != null ? Encoding.UTF8.GetBytes(s) : new byte[0];
                            }
                            finish(new HttpResponseData { Completed = true, Status = resp.Code, Body = body });
                            return;
                        }
                        finish(HttpResponseData.Failed("HTTP " + err));
                    }
                    catch (Exception e)
                    {
                        finish(HttpResponseData.Failed("callback: " + e.Message));
                    }
                });
                if (rc != HttpClient.DISPATCHASYNC_ERROR.PENDING)
                    finish(HttpResponseData.Failed("DispatchAsync " + rc));
            }
            catch (Exception e)
            {
                finish(HttpResponseData.Failed(e.GetType().Name + ": " + e.Message));
            }
        }
    }

    /// <summary>One-shot CTimer per scheduled callback.</summary>
    internal sealed class CrestronScheduler : IScheduler
    {
        private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

        public long NowMs
        {
            get { return Clock.ElapsedMilliseconds; }
        }

        public ITimerHandle Schedule(int dueMs, Action callback)
        {
            var h = new Handle();
            h.Timer = new CTimer(o =>
            {
                if (h.Cancelled) return;
                h.Cancelled = true;
                h.Release();
                callback();
            }, null, Math.Max(1, dueMs));
            return h;
        }

        private sealed class Handle : ITimerHandle
        {
            public CTimer Timer;
            public volatile bool Cancelled;

            public void Cancel()
            {
                Cancelled = true;
                Release();
            }

            public void Release()
            {
                var t = Timer;
                Timer = null;
                if (t == null) return;
                try
                {
                    t.Stop();
                    t.Dispose();
                }
                catch (Exception)
                {
                }
            }
        }
    }

    /// <summary>Drains the core's strand on a Crestron thread-pool thread - never on the SIMPL+ thread.</summary>
    internal sealed class CrestronDispatcher : IWorkDispatcher
    {
        public void Dispatch(Action work)
        {
            CrestronInvoke.BeginInvoke(o => work());
        }
    }

    /// <summary>Console + error log. Debug lines print only while a module has Debug enabled.</summary>
    internal sealed class CrestronLog : ILog
    {
        private static int _debugModules;

        public static void SetDebug(bool on, ref bool registered)
        {
            if (on && !registered)
            {
                System.Threading.Interlocked.Increment(ref _debugModules);
                registered = true;
            }
            else if (!on && registered)
            {
                System.Threading.Interlocked.Decrement(ref _debugModules);
                registered = false;
            }
        }

        public void Debug(string message)
        {
            if (System.Threading.Volatile.Read(ref _debugModules) <= 0) return;
            try
            {
                CrestronConsole.PrintLine("[MiYue] " + message);
            }
            catch (Exception)
            {
            }
        }

        public void Error(string message)
        {
            try
            {
                CrestronConsole.PrintLine("[MiYue] ERROR " + message);
                ErrorLog.Error("MiYue: {0}", message);
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>The one Runtime per program slot shared by every player / browser module instance
    /// (sync groups and the browse module find players through its registry).</summary>
    internal static class Shared
    {
        private static readonly object Lock = new object();
        private static Runtime _runtime;
        public static readonly CrestronLog Log = new CrestronLog();

        public static Runtime Runtime
        {
            get
            {
                lock (Lock)
                {
                    if (_runtime == null)
                        _runtime = new Runtime(new CrestronHttpTransport(Log), new CrestronScheduler(), new CrestronDispatcher(), Log);
                    return _runtime;
                }
            }
        }
    }
}
