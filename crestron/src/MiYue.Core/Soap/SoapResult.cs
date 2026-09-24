using System;
using System.Collections.Generic;
using System.Globalization;
using MiYue.Core.Util;

namespace MiYue.Core.Soap
{
    public enum SoapOutcome
    {
        /// <summary>200 with a parseable &lt;ActionResponse&gt; (or an empty 200 body).</summary>
        Ok,
        /// <summary>A SOAP fault with a UPnP errorCode (the action ran and was refused, or is unknown).</summary>
        Fault,
        /// <summary>Non-200 without a fault body (e.g. 404 from a hidden MediaRenderer) or an unparseable body.</summary>
        HttpError,
        /// <summary>Network failure / timeout: the action may or may not have executed.</summary>
        Transport,
        /// <summary>The device is not resolved; nothing was sent.</summary>
        Offline,
    }

    /// <summary>Outcome of one SOAP call. Out-args are already XML-unescaped, so DIDL payloads
    /// come back as real XML strings ready to parse again.</summary>
    public sealed class SoapResult
    {
        private static readonly Dictionary<string, string> Empty = new Dictionary<string, string>();

        public SoapOutcome Outcome { get; private set; }
        public Dictionary<string, string> Args { get; private set; }
        public int FaultCode { get; private set; }
        public int HttpStatus { get; private set; }
        public string Message { get; private set; }

        public bool Ok
        {
            get { return Outcome == SoapOutcome.Ok; }
        }

        /// <summary>A genuine SOAP fault (UPnPError errorCode present). Only this proves "the firmware
        /// refused / does not know the action"; a transport error may mean it DID run (HA _skip).</summary>
        public bool IsFault
        {
            get { return Outcome == SoapOutcome.Fault; }
        }

        /// <summary>Unknown action: 501 on Android (pupnp rewrites), 401 on Linux.</summary>
        public bool IsUnsupportedAction
        {
            get { return Outcome == SoapOutcome.Fault && (FaultCode == 401 || FaultCode == 501); }
        }

        public bool IsTransport
        {
            get { return Outcome == SoapOutcome.Transport; }
        }

        public string Get(string name)
        {
            return Get(name, string.Empty);
        }

        public string Get(string name, string fallback)
        {
            string v;
            if (Args != null && Args.TryGetValue(name, out v) && v != null) return v;
            return fallback;
        }

        public int GetInt(string name, int fallback)
        {
            int v;
            var s = Get(name, null);
            if (s != null && int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
            return fallback;
        }

        public override string ToString()
        {
            switch (Outcome)
            {
                case SoapOutcome.Ok: return "OK";
                case SoapOutcome.Fault: return "fault " + FaultCode + " " + Message;
                case SoapOutcome.HttpError: return "HTTP " + HttpStatus + (string.IsNullOrEmpty(Message) ? "" : " " + Message);
                case SoapOutcome.Transport: return "transport: " + Message;
                default: return "offline";
            }
        }

        public static SoapResult Success(Dictionary<string, string> args, int httpStatus)
        {
            return new SoapResult { Outcome = SoapOutcome.Ok, Args = args ?? Empty, HttpStatus = httpStatus, Message = string.Empty };
        }

        public static SoapResult FaultOf(int code, string message, int httpStatus)
        {
            return new SoapResult { Outcome = SoapOutcome.Fault, Args = Empty, FaultCode = code, Message = message ?? string.Empty, HttpStatus = httpStatus };
        }

        public static SoapResult HttpErrorOf(int status, string message)
        {
            return new SoapResult { Outcome = SoapOutcome.HttpError, Args = Empty, HttpStatus = status, Message = message ?? string.Empty };
        }

        public static SoapResult TransportError(string message)
        {
            return new SoapResult { Outcome = SoapOutcome.Transport, Args = Empty, Message = message ?? "transport error" };
        }

        public static SoapResult OfflineError()
        {
            return new SoapResult { Outcome = SoapOutcome.Offline, Args = Empty, Message = "offline" };
        }
    }

    /// <summary>Lenient response / fault parser (string scanning, namespace-prefix tolerant).</summary>
    public static class SoapResponse
    {
        public static SoapResult Parse(string action, int httpStatus, string body)
        {
            if (body == null || body.Trim().Length == 0)
            {
                if (httpStatus == 200) return SoapResult.Success(null, 200); // some builds answer no-out-arg actions with an empty 200
                return SoapResult.HttpErrorOf(httpStatus, "empty body");
            }

            // SOAP Fault?
            int openEnd;
            string prefix;
            bool selfClosing;
            if (XmlText.FindElement(body, "Fault", 0, out openEnd, out prefix, out selfClosing) >= 0)
            {
                string codeText = XmlText.ElementText(body, "errorCode");
                string desc = XmlText.ElementText(body, "errorDescription") ?? XmlText.ElementText(body, "faultstring") ?? "SOAP Fault";
                var digits = codeText != null ? ExtractInt(codeText) : null;
                // Only a UPnPError errorCode makes it a genuine fault (HA SoapError.fault_code);
                // a bare <Fault> without one is reported as an HTTP-level error.
                if (!digits.HasValue) return SoapResult.HttpErrorOf(httpStatus, desc.Trim());
                return SoapResult.FaultOf(digits.Value, desc.Trim(), httpStatus);
            }

            string responseName = action + "Response";
            int start = XmlText.FindElement(body, responseName, 0, out openEnd, out prefix, out selfClosing);
            if (start < 0)
            {
                // tolerate a differently named response element (single one inside Body)
                responseName = FindAnyResponseName(body);
                if (responseName != null)
                    start = XmlText.FindElement(body, responseName, 0, out openEnd, out prefix, out selfClosing);
            }
            if (start < 0)
            {
                if (httpStatus != 200) return SoapResult.HttpErrorOf(httpStatus, "no response element");
                return SoapResult.HttpErrorOf(httpStatus, "malformed response: no <" + action + "Response>");
            }
            if (selfClosing) return SoapResult.Success(null, httpStatus);

            int close = XmlText.IndexOfClose(body, "</" + prefix + responseName, openEnd);
            string inner;
            if (close >= 0)
            {
                inner = body.Substring(openEnd, close - openEnd);
            }
            else
            {
                inner = body.Substring(openEnd);
                int bodyEnd = inner.IndexOf("Body>", StringComparison.Ordinal);
                if (bodyEnd > 0)
                {
                    int lt = inner.LastIndexOf('<', bodyEnd);
                    if (lt >= 0) inner = inner.Substring(0, lt);
                }
            }
            return SoapResult.Success(ParseChildren(inner), httpStatus);
        }

        private static int? ExtractInt(string s)
        {
            int i = 0;
            while (i < s.Length && !(char.IsDigit(s[i]) || s[i] == '-')) i++;
            int j = i;
            if (j < s.Length && s[j] == '-') j++;
            while (j < s.Length && char.IsDigit(s[j])) j++;
            int v;
            if (j > i && int.TryParse(s.Substring(i, j - i), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
            return null;
        }

        private static string FindAnyResponseName(string body)
        {
            int pos = 0;
            while (pos < body.Length)
            {
                int lt = body.IndexOf('<', pos);
                if (lt < 0) return null;
                int j = lt + 1;
                while (j < body.Length && (char.IsLetterOrDigit(body[j]) || body[j] == '_' || body[j] == '-' || body[j] == ':')) j++;
                if (j > lt + 1)
                {
                    string qn = body.Substring(lt + 1, j - lt - 1);
                    int colon = qn.LastIndexOf(':');
                    string local = colon >= 0 ? qn.Substring(colon + 1) : qn;
                    if (local.Length > 8 && local.EndsWith("Response", StringComparison.Ordinal)) return local;
                }
                pos = lt + 1;
            }
            return null;
        }

        /// <summary>Children of a response element -> name (local, first wins) -> unescaped text.</summary>
        public static Dictionary<string, string> ParseChildren(string inner)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            int pos = 0;
            int len = inner.Length;
            while (pos < len)
            {
                int lt = inner.IndexOf('<', pos);
                if (lt < 0) break;
                if (lt + 1 < len && (inner[lt + 1] == '/' || inner[lt + 1] == '!' || inner[lt + 1] == '?'))
                {
                    pos = lt + 1;
                    continue;
                }
                int gt = inner.IndexOf('>', lt);
                if (gt < 0) break;
                int j = lt + 1;
                while (j < gt && !char.IsWhiteSpace(inner[j]) && inner[j] != '/') j++;
                string qn = inner.Substring(lt + 1, j - lt - 1);
                if (qn.Length == 0)
                {
                    pos = lt + 1;
                    continue;
                }
                int colon = qn.LastIndexOf(':');
                string key = colon >= 0 ? qn.Substring(colon + 1) : qn;
                bool self = inner[gt - 1] == '/';
                if (self)
                {
                    if (!result.ContainsKey(key)) result[key] = string.Empty;
                    pos = gt + 1;
                    continue;
                }
                int close = XmlText.IndexOfClose(inner, "</" + qn, gt + 1);
                string raw;
                if (close < 0)
                {
                    raw = inner.Substring(gt + 1);
                    if (!result.ContainsKey(key)) result[key] = XmlText.InnerText(raw);
                    break;
                }
                raw = inner.Substring(gt + 1, close - gt - 1);
                if (!result.ContainsKey(key)) result[key] = XmlText.InnerText(raw);
                int closeGt = inner.IndexOf('>', close);
                pos = closeGt < 0 ? len : closeGt + 1;
            }
            return result;
        }
    }
}
