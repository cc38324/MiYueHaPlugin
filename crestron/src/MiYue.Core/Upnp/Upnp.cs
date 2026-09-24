using System;
using System.Collections.Generic;
using System.Globalization;
using MiYue.Core.Util;

namespace MiYue.Core.Upnp
{
    /// <summary>Service names used by this driver and their default type / control path
    /// (verified in C4 contract_verified.json "description" and HA docs/CONTRACT.md).</summary>
    public static class Services
    {
        public const string AVTransport = "AVTransport";
        public const string RenderingControl = "RenderingControl";
        public const string MiyueQueue = "MiyueQueue";
        public const string MiyueLibrary = "MiyueLibrary";
        public const string MiyueAudioSource = "MiyueAudioSource";
        public const string MiyueSystem = "MiyueSystem";
        public const string MiyueSensor = "MiyueSensor";
        public const string MiyueGroup = "MiyueGroup";

        public sealed class Endpoint
        {
            public string Type;
            public string ControlPath;
        }

        public static readonly Dictionary<string, Endpoint> Defaults = new Dictionary<string, Endpoint>
        {
            { AVTransport, new Endpoint { Type = "urn:schemas-upnp-org:service:AVTransport:1", ControlPath = "/MediaRenderer/AVTransport/Control" } },
            { RenderingControl, new Endpoint { Type = "urn:schemas-upnp-org:service:RenderingControl:1", ControlPath = "/MediaRenderer/RenderingControl/Control" } },
            { MiyueQueue, new Endpoint { Type = "urn:miyue-hk:service:MiyueQueue:1", ControlPath = "/_control/MiyueQueue" } },
            { MiyueLibrary, new Endpoint { Type = "urn:miyue-hk:service:MiyueLibrary:1", ControlPath = "/_control/MiyueLibrary" } },
            { MiyueAudioSource, new Endpoint { Type = "urn:miyue-hk:service:MiyueAudioSource:1", ControlPath = "/_control/MiyueAudioSource" } },
            { MiyueSystem, new Endpoint { Type = "urn:miyue-hk:service:MiyueSystem:1", ControlPath = "/_control/MiyueSystem" } },
            { MiyueSensor, new Endpoint { Type = "urn:miyue-hk:service:MiyueSensor:1", ControlPath = "/_control/MiyueSensor" } },
            { MiyueGroup, new Endpoint { Type = "urn:miyue-hk:service:MiyueGroup:1", ControlPath = "/_control/MiyueGroup" } },
        };
    }

    /// <summary>The parts of description.xml the driver uses.</summary>
    public sealed class DeviceDescription
    {
        public string Udn = string.Empty;
        public string FriendlyName = string.Empty;
        public string ModelName = string.Empty;
        public string RoomName = string.Empty;
        public string SoftwareVersion = string.Empty;
        public string DeviceType = string.Empty;
        /// <summary>Service name ("MiyueQueue") -> (type, control path). Always contains every entry of
        /// <see cref="Services.Defaults"/> (declared or not).</summary>
        public Dictionary<string, Services.Endpoint> Endpoints = new Dictionary<string, Services.Endpoint>();

        /// <summary>Parse description.xml. Returns null when the text is not a UPnP description (no UDN).
        /// Control URLs are made relative to the probed port - URLBase is not trusted over the port that answered.</summary>
        public static DeviceDescription Parse(string xml)
        {
            if (string.IsNullOrEmpty(xml) || xml.IndexOf("UDN>", StringComparison.Ordinal) < 0) return null;
            string root = xml;
            int dl = xml.IndexOf("deviceList", StringComparison.Ordinal);
            if (dl > 0)
            {
                int lt = xml.LastIndexOf('<', dl);
                if (lt > 0) root = xml.Substring(0, lt);
            }
            var d = new DeviceDescription
            {
                Udn = (XmlText.ElementText(root, "UDN") ?? string.Empty).Trim(),
                FriendlyName = (XmlText.ElementText(root, "friendlyName") ?? string.Empty).Trim(),
                ModelName = (XmlText.ElementText(root, "modelName") ?? string.Empty).Trim(),
                RoomName = (XmlText.ElementText(root, "roomName") ?? string.Empty).Trim(),
                SoftwareVersion = (XmlText.ElementText(root, "softwareVersion") ?? string.Empty).Trim(),
                DeviceType = (XmlText.ElementText(root, "deviceType") ?? string.Empty).Trim(),
            };
            if (d.Udn.Length == 0) return null;

            // every <service> block; first occurrence per service name wins
            int pos = 0;
            while (pos < xml.Length)
            {
                int openEnd;
                string prefix;
                bool self;
                int s = XmlText.FindElement(xml, "service", pos, out openEnd, out prefix, out self);
                if (s < 0) break;
                if (self)
                {
                    pos = openEnd;
                    continue;
                }
                int c = XmlText.IndexOfClose(xml, "</" + prefix + "service", openEnd);
                if (c < 0) break;
                string block = xml.Substring(openEnd, c - openEnd);
                pos = c + 1;
                string type = (XmlText.ElementText(block, "serviceType") ?? string.Empty).Trim();
                string name = ServiceNameOf(type);
                if (name == null || d.Endpoints.ContainsKey(name)) continue;
                string control = (XmlText.ElementText(block, "controlURL") ?? string.Empty).Trim();
                control = ToPath(control);
                if (control.Length == 0) continue;
                d.Endpoints[name] = new Services.Endpoint { Type = type, ControlPath = control };
            }
            foreach (var kv in Services.Defaults)
                if (!d.Endpoints.ContainsKey(kv.Key))
                    d.Endpoints[kv.Key] = new Services.Endpoint { Type = kv.Value.Type, ControlPath = kv.Value.ControlPath };
            return d;
        }

        /// <summary>"urn:miyue-hk:service:MiyueQueue:1" -> "MiyueQueue".</summary>
        public static string ServiceNameOf(string serviceType)
        {
            if (string.IsNullOrEmpty(serviceType)) return null;
            var parts = serviceType.Split(':');
            // urn : domain : service : Name : ver
            for (int i = 0; i + 1 < parts.Length; i++)
                if (parts[i] == "service") return parts[i + 1];
            return null;
        }

        private static string ToPath(string url)
        {
            if (url.Length == 0) return url;
            int scheme = url.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0)
            {
                int slash = url.IndexOf('/', scheme + 3);
                return slash >= 0 ? url.Substring(slash) : "/";
            }
            return url[0] == '/' ? url : "/" + url;
        }
    }

    /// <summary>
    /// Port probe and re-resolve policy. The player's HTTP port starts at 49495 and drifts upward
    /// (pupnp FD leaks across reboots) - probe 49495..49520 with the last good port first; retry a failed
    /// resolve with 5/15/30/60 s backoff (C4 upnp.lua).
    /// </summary>
    public static class PortPolicy
    {
        public const int FirstPort = 49495;
        public const int LastPort = 49520;
        public static readonly int[] BackoffSeconds = { 5, 15, 30, 60 };

        public static List<int> ProbeOrder(int lastGood)
        {
            var list = new List<int>(LastPort - FirstPort + 2);
            if (lastGood > 0 && lastGood < 65536) list.Add(lastGood);
            for (int p = FirstPort; p <= LastPort; p++)
                if (p != lastGood) list.Add(p);
            return list;
        }

        /// <summary>Backoff for the n-th consecutive failed resolve (n starts at 1); sticks at the last value.</summary>
        public static int BackoffFor(int attempt)
        {
            if (attempt < 1) attempt = 1;
            int i = Math.Min(attempt, BackoffSeconds.Length) - 1;
            return BackoffSeconds[i];
        }

        public static string DescriptionUrl(string ip, int port)
        {
            return "http://" + ip + ":" + port.ToString(CultureInfo.InvariantCulture) + "/description.xml";
        }
    }
}
