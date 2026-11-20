using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TravelCore
{
    /// <summary>
    /// 通用城市模板：与 agent/synth.py 一一对应。
    /// 没有手工数据的城市，按目录经纬度现场生成 6 个通用点位（博物馆/老街/公园/
    /// 观景台/文创园/夜市），供演示用；不是真实 POI。
    /// </summary>
    public static class Synth
    {
        private sealed class Row
        {
            public string Name, District;
            public int Open, Close, Duration, Price;
            public double Rating;
            public string[] Tags;
            public bool Outdoor;
            public int? ClosedWeekday;
        }

        private static readonly Row[] Template =
        {
            new Row { Name = "城市博物馆", District = "中心城区", Open = 9 * 60, Close = 17 * 60, Duration = 90, Price = 0, Rating = 4.6, Tags = new[] { "历史人文", "艺术展馆" }, Outdoor = false, ClosedWeekday = 0 },
            new Row { Name = "老城历史街区", District = "老城区", Open = 8 * 60, Close = 21 * 60, Duration = 90, Price = 0, Rating = 4.4, Tags = new[] { "历史人文", "美食" }, Outdoor = true },
            new Row { Name = "城市公园", District = "城中", Open = 6 * 60, Close = 21 * 60, Duration = 75, Price = 0, Rating = 4.3, Tags = new[] { "自然风光", "亲子" }, Outdoor = true },
            new Row { Name = "滨江观景平台", District = "滨江", Open = 10 * 60, Close = 21 * 60, Duration = 60, Price = 30, Rating = 4.2, Tags = new[] { "夜景" }, Outdoor = true },
            new Row { Name = "文创艺术园区", District = "新区", Open = 10 * 60, Close = 20 * 60, Duration = 75, Price = 0, Rating = 4.1, Tags = new[] { "艺术展馆", "购物" }, Outdoor = false },
            new Row { Name = "夜市小吃街", District = "老城区", Open = 17 * 60, Close = 23 * 60, Duration = 60, Price = 0, Rating = 4.5, Tags = new[] { "美食", "夜景" }, Outdoor = true },
        };

        private static readonly double[,] Offsets =
        {
            { 0.006, -0.004 }, { -0.010, 0.008 }, { 0.004, 0.011 },
            { 0.013, 0.002 }, { -0.006, -0.012 }, { -0.012, 0.001 },
        };

        private static readonly (string Zone, double Lat, double Lng)[] Hotels =
        {
            ("市中心", 0.0, 0.0), ("老城区", -0.008, 0.004), ("滨江", 0.010, 0.003),
        };

        private static readonly (string Text, int Low, int High, int Rain)[] WeatherTemplate =
        {
            ("多云", 12, 21, 15), ("晴", 13, 23, 5), ("小雨", 11, 17, 70),
            ("阴", 10, 18, 30), ("晴间多云", 12, 22, 10),
        };

        private static string F(double v) { return v.ToString("0.####", CultureInfo.InvariantCulture); }
        private static string Q(string s) { return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }

        /// <summary>按模板生成与 cities/*.json 同结构的 JSON 文本。</summary>
        public static string BuildJson(string key, double lat, double lng)
        {
            var sb = new StringBuilder();
            sb.Append("{\"name\":").Append(Q(key));
            sb.Append(",\"center\":[").Append(F(lat)).Append(',').Append(F(lng)).Append(']');
            sb.Append(",\"hotels\":{");
            for (int i = 0; i < Hotels.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Q(Hotels[i].Zone)).Append(":[").Append(F(lat + Hotels[i].Lat))
                  .Append(',').Append(F(lng + Hotels[i].Lng)).Append(']');
            }
            sb.Append("},\"default_hotel\":").Append(Q(Hotels[0].Zone));
            sb.Append(",\"weather\":[");
            for (int i = 0; i < WeatherTemplate.Length; i++)
            {
                if (i > 0) sb.Append(',');
                var w = WeatherTemplate[i];
                sb.Append("{\"day\":\"").Append("第").Append(i + 1).Append("天\"")
                  .Append(",\"text\":").Append(Q(w.Text))
                  .Append(",\"low\":").Append(w.Low)
                  .Append(",\"high\":").Append(w.High)
                  .Append(",\"rain\":").Append(w.Rain).Append('}');
            }
            sb.Append("],\"far_ids\":[],\"far_minutes\":60,\"far_both_minutes\":90,\"far_km\":null");
            sb.Append(",\"synthetic\":true,\"pois\":[");
            for (int i = 0; i < Template.Length; i++)
            {
                var r = Template[i];
                double dLat = Offsets[i, 0], dLng = Offsets[i, 1];
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(Q(key + "_" + (i + 1).ToString("00")))
                  .Append(",\"name\":").Append(Q(r.Name))
                  .Append(",\"district\":").Append(Q(r.District))
                  .Append(",\"lat\":").Append(F(lat + dLat))
                  .Append(",\"lng\":").Append(F(lng + dLng))
                  .Append(",\"open_min\":").Append(r.Open)
                  .Append(",\"close_min\":").Append(r.Close)
                  .Append(",\"duration\":").Append(r.Duration)
                  .Append(",\"price\":").Append(r.Price)
                  .Append(",\"rating\":").Append(F(r.Rating))
                  .Append(",\"tags\":[");
                for (int t = 0; t < r.Tags.Length; t++)
                {
                    if (t > 0) sb.Append(',');
                    sb.Append(Q(r.Tags[t]));
                }
                sb.Append("],\"outdoor\":").Append(r.Outdoor ? "true" : "false")
                  .Append(",\"closed_weekday\":")
                  .Append(r.ClosedWeekday.HasValue ? r.ClosedWeekday.Value.ToString() : "null")
                  .Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }
    }
}