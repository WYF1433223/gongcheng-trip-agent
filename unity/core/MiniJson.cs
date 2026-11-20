// MiniJson: 只读 JSON 解析器 + 轻量写出器。
// 不依赖 Newtonsoft/System.Text.Json，控制台与 Unity 都能直接用。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TravelCore
{
    public enum JType { Null, Bool, Num, Str, Arr, Obj }

    public sealed class JVal
    {
        public JType Type;
        public bool Bool;
        public double Num;
        public string Str;
        public List<JVal> Arr;
        public Dictionary<string, JVal> Obj;

        public static readonly JVal Null = new JVal();

        public bool IsNull { get { return Type == JType.Null; } }

        public JVal this[string key]
        {
            get
            {
                JVal v;
                if (Obj != null && Obj.TryGetValue(key, out v)) return v;
                return Null;
            }
        }

        public double AsDouble() { return Type == JType.Num ? Num : 0.0; }
        public int AsInt() { return (int)Math.Round(Num, MidpointRounding.AwayFromZero); }
        public int? AsIntOrNull() { return Type == JType.Null ? (int?)null : AsInt(); }
        public string AsStr() { return Str ?? ""; }
        public bool AsBool() { return Type == JType.Bool && Bool; }
        public int Count { get { return Arr == null ? 0 : Arr.Count; } }

        public List<JVal> Items()
        {
            return Arr ?? new List<JVal>();
        }

        public JVal At(int i)
        {
            if (Arr == null || i < 0 || i >= Arr.Count) return Null;
            return Arr[i];
        }
    }

    public static class MiniJson
    {
        public static JVal Parse(string text)
        {
            int i = 0;
            JVal v = ParseValue(text, ref i);
            SkipWs(text, ref i);
            return v;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
        }

        private static JVal ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) return JVal.Null;
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i);
            if (c == '[') return ParseArray(s, ref i);
            if (c == '"') return new JVal { Type = JType.Str, Str = ParseString(s, ref i) };
            if (c == 't' || c == 'f')
            {
                bool b = c == 't';
                i += b ? 4 : 5;
                return new JVal { Type = JType.Bool, Bool = b };
            }
            if (c == 'n') { i += 4; return JVal.Null; }
            return ParseNumber(s, ref i);
        }

        private static JVal ParseObject(string s, ref int i)
        {
            var o = new JVal { Type = JType.Obj, Obj = new Dictionary<string, JVal>() };
            i++; // {
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return o; }
            while (i < s.Length)
            {
                SkipWs(s, ref i);
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ':') i++;
                JVal v = ParseValue(s, ref i);
                o.Obj[key] = v;
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; break; }
                break;
            }
            return o;
        }

        private static JVal ParseArray(string s, ref int i)
        {
            var a = new JVal { Type = JType.Arr, Arr = new List<JVal>() };
            i++; // [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return a; }
            while (i < s.Length)
            {
                a.Arr.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; break; }
                break;
            }
            return a;
        }

        private static string ParseString(string s, ref int i)
        {
            var sb = new StringBuilder();
            while (i < s.Length && s[i] != '"') i++; // 到起始引号
            i++;
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') break;
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
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
                        {
                            if (i + 4 <= s.Length)
                            {
                                int code = int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                                sb.Append((char)code);
                                i += 4;
                            }
                            break;
                        }
                    default: sb.Append(e); break;
                }
            }
            return sb.ToString();
        }

        private static JVal ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length)
            {
                char c = s[i];
                if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') i++;
                else break;
            }
            double d;
            if (!double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) d = 0;
            return new JVal { Type = JType.Num, Num = d };
        }
    }

    /// <summary>极简 JSON 写出器（只处理字典/列表/字符串/数字/布尔）。</summary>
    public sealed class JsonWriter
    {
        private readonly StringBuilder sb = new StringBuilder();
        private sealed class Frame { public bool First = true; public bool AfterKey; }
        private readonly Stack<Frame> frames = new Stack<Frame>();

        public override string ToString() { return sb.ToString(); }

        // 自动补逗号：调用方不必关心“是否第一个元素”
        private void BeforeValue()
        {
            if (frames.Count == 0) return;
            Frame f = frames.Peek();
            if (f.AfterKey) { f.AfterKey = false; return; }
            if (f.First) f.First = false; else sb.Append(',');
        }

        public void BeginObject() { BeforeValue(); sb.Append('{'); frames.Push(new Frame()); }
        public void EndObject() { sb.Append('}'); if (frames.Count > 0) frames.Pop(); }
        public void BeginArray() { BeforeValue(); sb.Append('['); frames.Push(new Frame()); }
        public void EndArray() { sb.Append(']'); if (frames.Count > 0) frames.Pop(); }

        public void Key(string k)
        {
            BeforeValue();
            WriteString(k);
            sb.Append(':');
            if (frames.Count > 0) frames.Peek().AfterKey = true;
        }

        public void Sep() { /* 逗号由 BeforeValue 统一处理，保留此方法仅为兼容旧调用 */ }

        public void Str(string v) { BeforeValue(); WriteString(v ?? ""); }
        public void Num(double v) { BeforeValue(); sb.Append(v.ToString("0.####", CultureInfo.InvariantCulture)); }
        public void Int(int v) { BeforeValue(); sb.Append(v.ToString(CultureInfo.InvariantCulture)); }
        public void Bool(bool v) { BeforeValue(); sb.Append(v ? "true" : "false"); }
        public void Null() { BeforeValue(); sb.Append("null"); }

        private void WriteString(string v)
        {
            sb.Append('"');
            foreach (char c in v)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}