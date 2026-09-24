using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MiYue.Core.Util
{
    /// <summary>
    /// Tiny tolerant JSON reader for the firmware's JSON out-args (GetCollectedSonglists,
    /// GetCollectedBoards, ListSensors ...). Objects become Dictionary&lt;string, object&gt;,
    /// arrays List&lt;object&gt;, numbers double, plus string / bool / null.
    /// No dependency so the core stays free of Newtonsoft (which Crestron ships in its own version).
    /// </summary>
    public static class MiniJson
    {
        public static object Parse(string json)
        {
            if (json == null) return null;
            var p = new Parser(json);
            p.SkipWs();
            var v = p.ReadValue();
            return v;
        }

        /// <summary>Parse a JSON array of objects; returns an empty list on null/""/"null"/error.
        /// A single object is wrapped into a one-element list (C4 Didl.jsonList).</summary>
        public static List<Dictionary<string, object>> ParseObjectList(string json)
        {
            var result = new List<Dictionary<string, object>>();
            if (string.IsNullOrEmpty(json)) return result;
            json = json.Trim();
            if (json.Length == 0 || json == "null") return result;
            object v;
            try
            {
                v = Parse(json);
            }
            catch (FormatException)
            {
                return result;
            }
            var list = v as List<object>;
            if (list != null)
            {
                foreach (var o in list)
                {
                    var d = o as Dictionary<string, object>;
                    if (d != null) result.Add(d);
                }
                return result;
            }
            var single = v as Dictionary<string, object>;
            if (single != null) result.Add(single);
            return result;
        }

        /// <summary>Field as a string ("" when absent/null; numbers formatted invariantly, integral without decimals).</summary>
        public static string Str(Dictionary<string, object> obj, string key)
        {
            object v;
            if (obj == null || !obj.TryGetValue(key, out v) || v == null) return string.Empty;
            if (v is string) return (string)v;
            if (v is double)
            {
                double d = (double)v;
                if (Math.Abs(d - Math.Round(d)) < 1e-9 && Math.Abs(d) < 1e15)
                    return ((long)Math.Round(d)).ToString(CultureInfo.InvariantCulture);
                return d.ToString(CultureInfo.InvariantCulture);
            }
            if (v is bool) return (bool)v ? "true" : "false";
            return v.ToString();
        }

        /// <summary>Field as an int (strings are parsed too); fallback when absent/unparseable.</summary>
        public static int Int(Dictionary<string, object> obj, string key, int fallback)
        {
            object v;
            if (obj == null || !obj.TryGetValue(key, out v) || v == null) return fallback;
            if (v is double) return (int)Math.Round((double)v);
            if (v is bool) return (bool)v ? 1 : 0;
            int r;
            if (int.TryParse(v.ToString().Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out r)) return r;
            return fallback;
        }

        private sealed class Parser
        {
            private readonly string _s;
            private int _i;

            public Parser(string s)
            {
                _s = s;
            }

            public void SkipWs()
            {
                while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
            }

            private FormatException Error(string what)
            {
                return new FormatException("JSON: " + what + " at " + _i);
            }

            public object ReadValue()
            {
                SkipWs();
                if (_i >= _s.Length) throw Error("unexpected end");
                char c = _s[_i];
                switch (c)
                {
                    case '{': return ReadObject();
                    case '[': return ReadArray();
                    case '"': return ReadString();
                    case 't':
                        Expect("true");
                        return true;
                    case 'f':
                        Expect("false");
                        return false;
                    case 'n':
                        Expect("null");
                        return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber();
                        throw Error("unexpected '" + c + "'");
                }
            }

            private void Expect(string word)
            {
                if (string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0) throw Error("expected " + word);
                _i += word.Length;
            }

            private Dictionary<string, object> ReadObject()
            {
                var d = new Dictionary<string, object>();
                _i++; // {
                SkipWs();
                if (_i < _s.Length && _s[_i] == '}')
                {
                    _i++;
                    return d;
                }
                while (true)
                {
                    SkipWs();
                    if (_i >= _s.Length || _s[_i] != '"') throw Error("expected key");
                    string key = ReadString();
                    SkipWs();
                    if (_i >= _s.Length || _s[_i] != ':') throw Error("expected ':'");
                    _i++;
                    object v = ReadValue();
                    d[key] = v;
                    SkipWs();
                    if (_i >= _s.Length) throw Error("unterminated object");
                    if (_s[_i] == ',')
                    {
                        _i++;
                        continue;
                    }
                    if (_s[_i] == '}')
                    {
                        _i++;
                        return d;
                    }
                    throw Error("expected ',' or '}'");
                }
            }

            private List<object> ReadArray()
            {
                var list = new List<object>();
                _i++; // [
                SkipWs();
                if (_i < _s.Length && _s[_i] == ']')
                {
                    _i++;
                    return list;
                }
                while (true)
                {
                    list.Add(ReadValue());
                    SkipWs();
                    if (_i >= _s.Length) throw Error("unterminated array");
                    if (_s[_i] == ',')
                    {
                        _i++;
                        continue;
                    }
                    if (_s[_i] == ']')
                    {
                        _i++;
                        return list;
                    }
                    throw Error("expected ',' or ']'");
                }
            }

            private string ReadString()
            {
                _i++; // opening quote
                var sb = new StringBuilder();
                while (_i < _s.Length)
                {
                    char c = _s[_i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }
                    if (_i >= _s.Length) break;
                    char e = _s[_i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (_i + 4 > _s.Length) throw Error("bad \\u escape");
                            int cp;
                            if (!int.TryParse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out cp))
                                throw Error("bad \\u escape");
                            sb.Append((char)cp);
                            _i += 4;
                            break;
                        default:
                            sb.Append(e);
                            break;
                    }
                }
                throw Error("unterminated string");
            }

            private object ReadNumber()
            {
                int start = _i;
                if (_s[_i] == '-') _i++;
                while (_i < _s.Length && "0123456789+-.eE".IndexOf(_s[_i]) >= 0) _i++;
                double d;
                if (!double.TryParse(_s.Substring(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                    throw Error("bad number");
                return d;
            }
        }
    }
}
