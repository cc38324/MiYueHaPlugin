using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MiYue.Core.Engine;
using MiYue.Core.Soap;
using MiYue.Core.Util;

namespace MiYue.Core.Tests.Support
{
    /// <summary>One recorded SOAP call.</summary>
    public sealed class Call
    {
        public string Ip;
        public int Port;
        public string Path;
        public string Service;
        public string Action;
        public List<KeyValuePair<string, string>> Args;

        public string Arg(string name) => Args.FirstOrDefault(a => a.Key == name).Value;
        public override string ToString() => Ip + " " + Action + "(" + string.Join(",", Args.Select(a => a.Key + "=" + a.Value)) + ")";
    }

    /// <summary>
    /// Fake HTTP network: routes requests to simulated players by IP; unknown host/port = connection
    /// refused. Responses are delivered synchronously unless an action is "held".
    /// </summary>
    public sealed class FakeNetwork : IHttpTransport
    {
        public readonly Dictionary<string, FakeMiyue> Devices = new Dictionary<string, FakeMiyue>();
        public readonly List<Call> Calls = new List<Call>();
        public readonly List<string> Gets = new List<string>();
        /// <summary>Actions whose responses are parked until Release() - to test non-overlapping polls.</summary>
        public readonly HashSet<string> Hold = new HashSet<string>();
        public readonly List<Action> Parked = new List<Action>();
        public int MaxConcurrent;
        private int _concurrent;

        public void Send(HttpRequestData req, Action<HttpResponseData> done)
        {
            var uri = new Uri(req.Url);
            string ip = uri.Host;
            int port = uri.Port;
            _concurrent++;
            MaxConcurrent = Math.Max(MaxConcurrent, _concurrent);
            Action<HttpResponseData> finish = r =>
            {
                _concurrent--;
                done(r);
            };
            if (!Devices.TryGetValue(ip, out var dev) || !dev.Up || dev.Port != port)
            {
                if (req.Method == "GET") Gets.Add(req.Url);
                finish(HttpResponseData.Failed("connection refused"));
                return;
            }
            if (req.Method == "GET")
            {
                Gets.Add(req.Url);
                finish(dev.Get(uri.AbsolutePath));
                return;
            }
            string soapAction = req.Headers.TryGetValue("SOAPACTION", out var h) ? h.Trim('"') : "";
            string action = soapAction.Substring(soapAction.IndexOf('#') + 1);
            string service = soapAction.Split(':').Length > 3 ? soapAction.Split(':')[3] : "";
            var call = new Call
            {
                Ip = ip,
                Port = port,
                Path = uri.AbsolutePath,
                Service = service,
                Action = action,
                Args = ParseArgs(req.Body, action),
            };
            Calls.Add(call);
            if (dev.TransportDown)
            {
                finish(HttpResponseData.Failed("timeout"));
                return;
            }
            var resp = dev.Handle(call);
            if (Hold.Contains(action)) Parked.Add(() => finish(resp));
            else finish(resp);
        }

        public void ReleaseAll()
        {
            var list = Parked.ToList();
            Parked.Clear();
            foreach (var a in list) a();
        }

        public static List<KeyValuePair<string, string>> ParseArgs(string body, string action)
        {
            var list = new List<KeyValuePair<string, string>>();
            int openEnd;
            string prefix;
            bool self;
            int s = XmlText.FindElement(body, action, 0, out openEnd, out prefix, out self);
            if (s < 0 || self) return list;
            int c = XmlText.IndexOfClose(body, "</" + prefix + action, openEnd);
            string inner = body.Substring(openEnd, c - openEnd);
            // ordered parse
            int pos = 0;
            while (pos < inner.Length)
            {
                int lt = inner.IndexOf('<', pos);
                if (lt < 0) break;
                int gt = inner.IndexOf('>', lt);
                string name = inner.Substring(lt + 1, gt - lt - 1);
                int close = inner.IndexOf("</" + name + ">", gt, StringComparison.Ordinal);
                list.Add(new KeyValuePair<string, string>(name, XmlText.Unescape(inner.Substring(gt + 1, close - gt - 1))));
                pos = close + name.Length + 3;
            }
            return list;
        }

        public IEnumerable<Call> CallsTo(string ip) => Calls.Where(c => c.Ip == ip);
        public IEnumerable<Call> Named(string action) => Calls.Where(c => c.Action == action);
    }

    /// <summary>A simulated MiYue player (state machine good enough for the engine's contract).</summary>
    public sealed class FakeMiyue
    {
        public string Ip;
        public int Port;
        public bool Up = true;
        public bool TransportDown;
        public string Name;
        public string Udn;

        public string Transport = "PAUSED_PLAYBACK";
        public int Volume = 25;
        public bool Mute;
        public string PlayMode = "REPEAT_ALL";
        public string Role = "None";
        public string GroupId;
        public string MasterIp = "";
        public string Multicast;
        public int CurrentIndex = 0;
        public List<(string title, string artist, int src)> Queue = new List<(string, string, int)>
        {
            ("Go Your Own Way", "Fleetwood Mac", 15),
            ("I Feel It Coming", "The Weeknd", 15),
            ("The Look Of Love", "Diana Krall", 15),
        };
        public bool SupportsNext = true;
        public bool RendererHidden;   // older-firmware slave: /MediaRenderer/* -> 404
        public bool OpenSpdif;
        public string SensorsJson;
        public string CollectedMusicDidl;
        public string CollectedRadiosDidl;
        public string SonglistsJson;
        public string BoardsJson;
        public Dictionary<string, string> SonglistTracks = new Dictionary<string, string>();
        public int RelSeconds = 85;
        public int DurSeconds = 260;

        public FakeMiyue(string ip, int port, string name)
        {
            Ip = ip;
            Port = port;
            Name = name;
            Udn = "uuid:" + Guid.NewGuid();
            GroupId = Guid.NewGuid().ToString("N").Substring(0, 28);
            Multicast = "239.10." + (ip.GetHashCode() & 0x7f) + ".1";
            SensorsJson = Fixtures.Text("MiyueSensor.ListSensors.xml");
            SensorsJson = SoapResponse.Parse("ListSensors", 200, SensorsJson).Get("Sensors");
        }

        public HttpResponseData Get(string path)
        {
            if (path != "/description.xml") return Http(404, "");
            string xml = Fixtures.Text("android_description.xml")
                .Replace("uuid:064c6961-9ceb-4fe3-93a7-95de075feb9c", Udn)
                .Replace("<friendlyName>餐厅</friendlyName>", "<friendlyName>" + Name + "</friendlyName>");
            return Http(200, xml);
        }

        private static HttpResponseData Http(int status, string body) =>
            new HttpResponseData { Completed = true, Status = status, Body = Encoding.UTF8.GetBytes(body) };

        public static string Envelope(string action, string serviceType, IEnumerable<(string, string)> outArgs)
        {
            var sb = new StringBuilder();
            sb.Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>\n");
            sb.Append("<u:" + action + "Response xmlns:u=\"" + serviceType + "\">\n");
            foreach (var (k, v) in outArgs) sb.Append("<" + k + ">" + XmlText.Escape(v) + "</" + k + ">\n");
            sb.Append("</u:" + action + "Response>\n</s:Body> </s:Envelope>");
            return sb.ToString();
        }

        public static HttpResponseData Fault(int code, string desc) => Http(500,
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>" +
            "<s:Fault><faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring><detail>" +
            "<UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\"><errorCode>" + code + "</errorCode><errorDescription>" + desc +
            "</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>");

        public string QueueDidl(int start, int count)
        {
            var sb = new StringBuilder(MiYue.Core.Didl.DidlDoc.Header);
            for (int i = start; i < Math.Min(Queue.Count, start + count); i++)
            {
                var q = Queue[i];
                sb.Append("<item id=\"" + (1000 + i) + "\" parentID=\"-1\" restricted=\"1\"><dc:title>" + XmlText.Escape(q.title) +
                          "</dc:title><dc:creator>" + XmlText.Escape(q.artist) + "</dc:creator><upnp:artist>" + XmlText.Escape(q.artist) +
                          "</upnp:artist><upnp:album>A</upnp:album><upnp:albumArtURI>http://art/" + i +
                          ".jpg</upnp:albumArtURI><upnp:class>object.item.audioItem.musicTrack</upnp:class><miyue:songSrc>" + q.src +
                          "</miyue:songSrc><miyue:songId>" + (1000 + i) + "</miyue:songId><res protocolInfo=\"http-get:*:audio/mpeg:*\" duration=\"0:03:43\"></res></item>");
            }
            sb.Append(MiYue.Core.Didl.DidlDoc.Footer);
            return sb.ToString();
        }

        private static string T(int s) => TimeText.ToHms(s).PadLeft(8, '0');

        public HttpResponseData Handle(Call c)
        {
            string st = c.Path.StartsWith("/MediaRenderer/")
                ? "urn:schemas-upnp-org:service:" + c.Service + ":1"
                : "urn:miyue-hk:service:" + c.Service + ":1";
            if (RendererHidden && c.Path.StartsWith("/MediaRenderer/")) return Http(404, "");
            HttpResponseData Ok(params (string, string)[] outs) => Http(200, Envelope(c.Action, st, outs));
            switch (c.Action)
            {
                case "GetTransportInfo":
                    return Ok(("CurrentTransportState", Transport), ("CurrentTransportStatus", "OK"), ("CurrentSpeed", "1"));
                case "GetVolume": return Ok(("CurrentVolume", Volume.ToString()));
                case "GetMute": return Ok(("CurrentMute", Mute ? "1" : "0"));
                case "GetPositionInfo":
                {
                    string meta = "";
                    if (CurrentIndex >= 0 && CurrentIndex < Queue.Count)
                    {
                        var d = QueueDidl(CurrentIndex, 1).Replace("id=\"" + (1000 + CurrentIndex) + "\"", "id=\"nowplaying\"");
                        meta = d;
                    }
                    return Ok(("Track", "1"), ("TrackDuration", T(DurSeconds)), ("TrackMetaData", meta), ("TrackURI", "local://current"),
                        ("RelTime", T(RelSeconds)), ("AbsTime", T(RelSeconds)), ("RelCount", "0"), ("AbsCount", "0"));
                }
                case "GetQueue":
                {
                    int start = int.Parse(c.Arg("StartIndex"));
                    int count = int.Parse(c.Arg("RequestedCount"));
                    if (count == 0) count = Queue.Count;
                    string didl = QueueDidl(start, count);
                    int n = Math.Max(0, Math.Min(Queue.Count, start + count) - start);
                    return Ok(("Result", didl), ("Count", n.ToString()), ("Total", Queue.Count.ToString()), ("CurrentIndex", CurrentIndex.ToString()));
                }
                case "GetPlayMode": return Ok(("PlayMode", PlayMode));
                case "SetPlayMode":
                    PlayMode = Model.PlayModes.Normalize(c.Arg("PlayMode"));
                    return Ok();
                case "GetExternalInputs":
                    return Ok(("WithAux", "0"), ("OpenAux", "0"), ("WithSpdif", "1"), ("OpenSpdif", OpenSpdif ? "1" : "0"),
                        ("WithBluetooth", "1"), ("OpenBluetooth", "0"), ("BluetoothStatus", "0"));
                case "GetGroupInfo":
                    return Ok(("Role", Role), ("GroupID", GroupId), ("MasterIp", MasterIp), ("MulticastAddr", Multicast),
                        ("MulticastPort", "23125"), ("SyncPort", "23126"), ("ControlPort", "23127"), ("DeviceUUID", Udn), ("DeviceName", Name));
                case "GetDeviceInfo":
                    return Ok(("Name", Name), ("Manufacturer", "MiYue"), ("Model", "M100"), ("ModelNumber", "M100"), ("SoftwareVersion", "3.3"),
                        ("IPAddress", Ip), ("Role", Role), ("GroupID", GroupId), ("DeviceUUID", Udn));
                case "ListSensors": return Ok(("Sensors", SensorsJson ?? "[]"));
                case "ExecuteSensor": return Ok();
                case "Play":
                    Transport = "PLAYING";
                    return Ok();
                case "Pause":
                case "Stop":
                    Transport = "PAUSED_PLAYBACK";
                    return Ok();
                case "Seek":
                    RelSeconds = TimeText.ToSeconds(c.Arg("Target"));
                    return Ok();
                case "Next":
                case "Previous":
                    if (!SupportsNext) return Fault(401, "Invalid Action");
                    CurrentIndex = (CurrentIndex + (c.Action == "Next" ? 1 : Queue.Count - 1)) % Queue.Count;
                    return Ok();
                case "SeekToTrack":
                {
                    int i = int.Parse(c.Arg("Index"));
                    if (i >= 0 && i < Queue.Count)
                    {
                        CurrentIndex = i;
                        Transport = "PLAYING";
                    }
                    return Ok();
                }
                case "ReplaceQueue":
                {
                    var items = MiYue.Core.Didl.DidlDoc.Parse(c.Arg("Items"));
                    Queue = items.Select(i => (i.Title, i.Artist, i.SongSrc ?? 0)).ToList();
                    CurrentIndex = int.Parse(c.Arg("StartingIndex"));
                    Transport = "PLAYING";
                    return Ok(("NewQueueLength", Queue.Count.ToString()));
                }
                case "SetVolume":
                    Volume = int.Parse(c.Arg("DesiredVolume"));
                    return Ok();
                case "SetMute":
                    Mute = c.Arg("DesiredMute") == "1";
                    return Ok();
                case "SelectSource":
                case "SetInputOpen":
                case "PlayTTS":
                case "SetAVTransportURI":
                    return Ok();
                case "SetGroupConfig":
                    if (c.Arg("Role") == "Master")
                    {
                        Role = "Master";
                        MasterIp = Ip;
                    }
                    else if (c.Arg("Role") == "Slave")
                    {
                        Role = "Slave";
                        GroupId = c.Arg("GroupID");
                        MasterIp = c.Arg("MasterIp");
                        Multicast = c.Arg("MulticastAddr");
                    }
                    return Ok();
                case "LeaveGroup":
                    Role = "None";
                    MasterIp = "";
                    return Ok();
                case "GetCollectedMusic": return Ok(("Result", CollectedMusicDidl ?? QueueDidl(0, Queue.Count)));
                case "GetCollectedRadios": return Ok(("Result", CollectedRadiosDidl ?? ""));
                case "GetCollectedSonglists": return Ok(("Songlists", SonglistsJson ?? "[]"));
                case "GetCollectedBoards": return Ok(("Boards", BoardsJson ?? "[]"));
                case "GetSonglistTracks":
                    return Ok(("Result", SonglistTracks.TryGetValue(c.Arg("SonglistId"), out var t) ? t : ""));
                default:
                    return Fault(401, "Invalid Action");
            }
        }
    }
}
