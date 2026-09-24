using System.Collections.Generic;
using System.Text;
using MiYue.Core.Util;

namespace MiYue.Core.Soap
{
    /// <summary>Ordered SOAP arguments (argument order follows the SCPD; SetGroupConfig relies on it).</summary>
    public sealed class SoapArgs : List<KeyValuePair<string, string>>
    {
        public static SoapArgs None
        {
            get { return new SoapArgs(); }
        }

        public SoapArgs Add(string name, string value)
        {
            base.Add(new KeyValuePair<string, string>(name, value ?? string.Empty));
            return this;
        }

        public SoapArgs Add(string name, int value)
        {
            return Add(name, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        public static SoapArgs Of(string name, string value)
        {
            return new SoapArgs().Add(name, value);
        }

        /// <summary>{InstanceID=0} for AVTransport calls.</summary>
        public static SoapArgs Instance()
        {
            return new SoapArgs().Add("InstanceID", "0");
        }

        /// <summary>{InstanceID=0, Channel=Master} for RenderingControl calls.</summary>
        public static SoapArgs Master()
        {
            return new SoapArgs().Add("InstanceID", "0").Add("Channel", "Master");
        }
    }

    /// <summary>
    /// Builds the exact envelope + headers the firmware accepts (HA soap.py / C4 soap.lua):
    ///   Content-Type: text/xml; charset="utf-8"
    ///   SOAPACTION: "&lt;serviceType&gt;#&lt;action&gt;"
    /// </summary>
    public static class SoapEnvelope
    {
        public const string ContentType = "text/xml; charset=\"utf-8\"";

        public static string Build(string serviceType, string action, IEnumerable<KeyValuePair<string, string>> args)
        {
            var sb = new StringBuilder(256);
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"");
            sb.Append(" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">");
            sb.Append("<s:Body>");
            sb.Append("<u:").Append(action).Append(" xmlns:u=\"").Append(XmlText.Escape(serviceType)).Append("\">");
            if (args != null)
            {
                foreach (var kv in args)
                {
                    sb.Append('<').Append(kv.Key).Append('>');
                    sb.Append(XmlText.Escape(kv.Value));
                    sb.Append("</").Append(kv.Key).Append('>');
                }
            }
            sb.Append("</u:").Append(action).Append('>');
            sb.Append("</s:Body></s:Envelope>");
            return sb.ToString();
        }

        public static string SoapActionHeader(string serviceType, string action)
        {
            return "\"" + serviceType + "#" + action + "\"";
        }
    }
}
