// 城市数据与通勤估算（对应 Python 的 agent/data.py）。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Linq;

namespace TravelCore
{
    public sealed class Poi
    {
        public string Id = "";
        public string Name = "";
        public string District = "";
        public double Lat;
        public double Lng;
        public int OpenMin;
        public int CloseMin;
        public int Duration;
        public int Price;
        public double Rating;
        public List<string> Tags = new List<string>();
        public bool Outdoor;
        public int? ClosedWeekday;
    }

    public sealed class CityMeta
    {
        public string Key = "";
        public string Name = "";
        public double[] Center = new double[] { 0, 0 };
        public Dictionary<string, double[]> Hotels = new Dictionary<string, double[]>();
        public string DefaultHotel = "";
        public List<Weather> Weather = new List<Weather>();
        public List<Poi> Pois = new List<Poi>();
        public HashSet<string> FarIds = new HashSet<string>();
        public int FarMinutes;
        public int FarBothMinutes;
        public double? FarKm;
    }

    public static class Data
    {
        public static readonly string[] PrefTags = { "历史人文", "自然风光", "美食", "购物", "艺术展馆", "夜景", "亲子" };

        /// <summary>默认休息时段：午间 90 分钟、傍晚 60 分钟。</summary>
        public static List<RestSpec> DefaultRests()
        {
            return new List<RestSpec>
            {
                new RestSpec { Label = "午间休息", Start = 11 * 60 + 30, End = 14 * 60, Minutes = 90 },
                new RestSpec { Label = "傍晚休息", Start = 18 * 60, End = 20 * 60, Minutes = 60 },
            };
        }

        /// <summary>整理外部传入的休息时段：丢弃非法项，空则回落默认值。</summary>
        public static List<RestSpec> NormalizeRests(IEnumerable<RestSpec> raw)
        {
            var outp = new List<RestSpec>();
            if (raw != null)
            {
                foreach (var item in raw)
                {
                    if (item == null) continue;
                    int st = item.Start, en = item.End, mi = item.Minutes;
                    if (mi <= 0 || en <= st) continue;
                    st = Math.Max(6 * 60, Math.Min(23 * 60, st));
                    en = Math.Max(6 * 60, Math.Min(23 * 60, en));
                    if (en <= st) continue;
                    mi = Math.Max(10, Math.Min(en - st, mi));
                    outp.Add(new RestSpec { Label = string.IsNullOrEmpty(item.Label) ? "休息" : item.Label, Start = st, End = en, Minutes = mi });
                }
            }
            outp.Sort((a, b) => a.Start.CompareTo(b.Start));
            return outp.Count > 0 ? outp : DefaultRests();
        }
        public const int FoodPerDay = 120;
        public const int TransitPerDay = 40;
        public static readonly Dictionary<string, int> TransitCost = new Dictionary<string, int>
        {
            { "walk", 10 }, { "transit", 40 }, { "taxi", 140 }
        };
        public static readonly Dictionary<string, string> TransportLabel = new Dictionary<string, string>
        {
            { "walk", "步行" }, { "transit", "公交/地铁" }, { "taxi", "打车" }
        };

        public static string DataDir = "cities";

        /// <summary>Unity/Android 用：城市 json 的文字加载器（返回 null 表示未收录）。</summary>
        public static Func<string, string> LoadCityText = null;

        /// <summary>Unity/Android 用：可枚举的城市 key 列表（文件系统不可枚举时使用）。</summary>
        public static List<string> FixedCityKeys = null;

        /// <summary>目录里所有城市的中心坐标（key -> [lat,lng]），供通用模板城市使用。</summary>
        public static Dictionary<string, double[]> CityPositions = new Dictionary<string, double[]>();

        public static List<Poi> Pois = new List<Poi>();
        public static Dictionary<string, Poi> PoiById = new Dictionary<string, Poi>();
        public static Dictionary<string, string> PoiCity = new Dictionary<string, string>();
        public static List<CityMeta> CityMetas = new List<CityMeta>();
        public static List<string> CityKeys = new List<string>();
        public static string City = "chengdu";
        public static Dictionary<string, double[]> HotelZones = new Dictionary<string, double[]>();
        public static string DefaultHotel = "市中心";
        public static string WeatherSource = "内置演示数据";

        private static List<Weather> _weather = new List<Weather>();
        private static List<Weather> _weatherOverride;
        private static readonly Dictionary<string, List<Weather>> _weatherByCity = new Dictionary<string, List<Weather>>();
        private static HashSet<string> _farIds = new HashSet<string>();
        private static int _farMinutes, _farBoth;
        private static double? _farKm;

        public static CityMeta Cur
        {
            get { return CityMetas.Count > 0 ? CityMetas[0] : null; }
        }

        public static List<string> AvailableCities()
        {
            var keys = new SortedSet<string>(StringComparer.Ordinal);
            if (FixedCityKeys != null)
            {
                // Android/Unity：文件系统不可枚举，由 App 预置城市列表并提供文字加载器
                foreach (var k in FixedCityKeys) keys.Add(k);
                return keys.ToList();
            }
            if (Directory.Exists(DataDir))
            {
                foreach (var f in Directory.GetFiles(DataDir, "*.json"))
                {
                    string name = Path.GetFileNameWithoutExtension(f);
                    if (!name.StartsWith(".")) keys.Add(name);
                }
            }
            return keys.ToList();
        }

        public static void UseCities(IEnumerable<string> keys)
        {
            var list = (keys ?? new string[0]).Where(k => !string.IsNullOrEmpty(k)).ToList();
            if (list.Count == 0) list.Add("chengdu");
            var metas = list.Select(LoadCity).ToList();
            Apply(metas, list);
        }

        public static void UseCity(string key) { UseCities(new[] { key }); }

        private static CityMeta LoadCity(string key)
        {
            string text = null;
            if (LoadCityText != null) text = LoadCityText(key);
            else
            {
                string path = Path.Combine(DataDir, key + ".json");
                if (File.Exists(path)) text = File.ReadAllText(path, System.Text.Encoding.UTF8);
            }
            if (string.IsNullOrEmpty(text))
            {
                double[] pos;
                if (CityPositions.TryGetValue(key, out pos))
                    text = Synth.BuildJson(key, pos[0], pos[1]);   // 通用模板城市
                else
                    throw new InvalidOperationException("未收录的城市：" + key);
            }
            JVal root = MiniJson.Parse(text);
            var meta = new CityMeta { Key = key, Name = root["name"].AsStr() };
            JVal c = root["center"];
            if (c.Count >= 2) meta.Center = new[] { c.At(0).AsDouble(), c.At(1).AsDouble() };
            foreach (var kv in root["hotels"].Obj ?? new Dictionary<string, JVal>())
            {
                meta.Hotels[kv.Key] = new[] { kv.Value.At(0).AsDouble(), kv.Value.At(1).AsDouble() };
            }
            meta.DefaultHotel = meta.Hotels.Count > 0 ? meta.Hotels.Keys.First() : "市中心";
            foreach (var w in root["weather"].Items())
            {
                meta.Weather.Add(new Weather
                {
                    Day = w["day"].AsStr(),
                    Text = w["text"].AsStr(),
                    Low = w["low"].AsInt(),
                    High = w["high"].AsInt(),
                    Rain = w["rain"].AsInt(),
                });
            }
            foreach (var f in root["far_ids"].Items()) meta.FarIds.Add(f.AsStr());
            meta.FarMinutes = root["far_minutes"].AsInt();
            meta.FarBothMinutes = root["far_both_minutes"].AsInt();
            if (!root["far_km"].IsNull) meta.FarKm = root["far_km"].AsDouble();
            foreach (var p in root["pois"].Items())
            {
                var poi = new Poi
                {
                    Id = p["id"].AsStr(),
                    Name = p["name"].AsStr(),
                    District = p["district"].AsStr(),
                    Lat = p["lat"].AsDouble(),
                    Lng = p["lng"].AsDouble(),
                    OpenMin = p["open_min"].AsInt(),
                    CloseMin = p["close_min"].AsInt(),
                    Duration = p["duration"].AsInt(),
                    Price = p["price"].AsInt(),
                    Rating = p["rating"].AsDouble(),
                    Outdoor = p["outdoor"].AsBool(),
                    ClosedWeekday = p["closed_weekday"].IsNull ? (int?)null : p["closed_weekday"].AsInt(),
                };
                foreach (var t in p["tags"].Items()) poi.Tags.Add(t.AsStr());
                meta.Pois.Add(poi);
            }
            return meta;
        }

        private static void Apply(List<CityMeta> metas, List<string> keys)
        {
            Pois.Clear();
            PoiById.Clear();
            PoiCity.Clear();
            CityMetas = metas;
            for (int i = 0; i < metas.Count; i++)
            {
                foreach (var poi in metas[i].Pois)
                {
                    Pois.Add(poi);
                    PoiById[poi.Id] = poi;
                    PoiCity[poi.Id] = metas[i].Key;
                }
            }
            CityKeys = new List<string>(keys);
            City = keys[0];
            var first = metas[0];
            HotelZones = new Dictionary<string, double[]>(first.Hotels);
            DefaultHotel = first.DefaultHotel;
            _weather = first.Weather;
            _farIds = new HashSet<string>(first.FarIds);
            _farMinutes = first.FarMinutes;
            _farBoth = first.FarBothMinutes;
            _farKm = first.FarKm;
        }

        public static string CityOfPoi(string pid)
        {
            string c;
            return PoiCity.TryGetValue(pid, out c) ? c : City;
        }

        public static string HotelOf(string city)
        {
            var m = CityMetas.FirstOrDefault(x => x.Key == city);
            return m != null ? m.DefaultHotel : DefaultHotel;
        }

        public static Dictionary<string, double[]> HotelsOf(string city)
        {
            var m = CityMetas.FirstOrDefault(x => x.Key == city);
            return m != null ? m.Hotels : HotelZones;
        }

        static readonly Dictionary<string, string> _nameCache = new Dictionary<string, string>();
        static readonly Dictionary<string, double[]> _posCache = new Dictionary<string, double[]>();

        static string ReadCityText(string key)
        {
            if (LoadCityText != null) return LoadCityText(key);
            string path = Path.Combine(DataDir, key + ".json");
            return File.Exists(path) ? File.ReadAllText(path, System.Text.Encoding.UTF8) : null;
        }

        /// <summary>未载入内存的城市：直接读它的 json 拿 name / center（带缓存）。</summary>
        static JVal SpecMeta(string key)
        {
            if (_nameCache.ContainsKey(key)) return null;
            string text = ReadCityText(key);
            if (string.IsNullOrEmpty(text)) return null;
            JVal root;
            try { root = MiniJson.Parse(text); } catch (Exception) { return null; }
            var nm = root["name"];
            _nameCache[key] = (nm != null && nm.Type == JType.Str) ? nm.AsStr() : key;
            var c = root["center"];
            if (c != null && c.Count >= 2)
                _posCache[key] = new[] { c.At(0).AsDouble(), c.At(1).AsDouble() };
            return root;
        }

        public static string CityName(string key)
        {
            var m = CityMetas.FirstOrDefault(x => x.Key == key);
            if (m != null) return m.Name;
            string n;
            if (_nameCache.TryGetValue(key, out n)) return n;
            var spec = SpecMeta(key);
            if (spec != null && _nameCache.TryGetValue(key, out n)) return n;
            return key;
        }

        /// <summary>城市中心坐标：已加载的读 meta，其余读省—市目录或城市文件。</summary>
        public static double[] CityPosition(string key)
        {
            var m = CityMetas.FirstOrDefault(x => x.Key == key);
            if (m != null) return m.Center;
            double[] p;
            if (_posCache.TryGetValue(key, out p)) return p;
            if (CityPositions.TryGetValue(key, out p)) return p;
            SpecMeta(key);
            return _posCache.TryGetValue(key, out p) ? p : null;
        }

        // ---------- 费用与任意两点（含住宿区）的移动 ----------
        public static int MetroFare(double km)
        {
            if (km <= 6) return 2;
            return (int)Math.Min(8, 2 + (int)((km - 6) / 8) + 1);
        }

        public static int TaxiFare(double km) => (int)Math.Max(13, Num.RoundHalfEven(10 + Math.Max(0.0, km - 3) * 2.6));

        /// <summary>POI id 或 hotel:区名 → (城市 key, lat, lng, 显示名)</summary>
        public static Tuple<string, double, double, string> Coords(string id)
        {
            if (id.StartsWith("hotel:"))
            {
                string zone = id.Substring(6);
                foreach (var key in (CityKeys.Count > 0 ? CityKeys : new List<string> { City }))
                {
                    var zones = HotelsOf(key);
                    double[] z;
                    if (zones.TryGetValue(zone, out z)) return Tuple.Create(key, z[0], z[1], zone + "住宿区");
                }
                return Tuple.Create(City, 0.0, 0.0, zone);
            }
            var p = PoiById[id];
            return Tuple.Create(CityOfPoi(id), p.Lat, p.Lng, p.Name);
        }

        public static double IdKm(string aId, string bId)
        {
            var a = Coords(aId);
            var b = Coords(bId);
            if (a.Item1 != b.Item1) return Num.Round4(Intercity(a.Item1, b.Item1).Item3);
            if (!aId.StartsWith("hotel:") && !bId.StartsWith("hotel:"))
            {
                var k = RouteKm(a.Item1, aId, bId);
                if (k.HasValue) return k.Value;
            }
            return Math.Round(Haversine(a.Item2, a.Item3, b.Item2, b.Item3), 1);
        }

        public static int IdMinutes(string aId, string bId, string mode = "transit")
        {
            if (aId == bId) return 0;
            if (aId.StartsWith("hotel:")) return HotelTravelMinutes(aId.Substring(6), bId, mode);
            if (bId.StartsWith("hotel:")) return HotelTravelMinutes(bId.Substring(6), aId, mode);
            return TravelMinutes(aId, bId, mode);
        }

        public static void SetWeather(List<Weather> days, string source)
        {
            if (days != null && days.Count > 0)
            {
                _weatherOverride = days;
                WeatherSource = source;
            }
        }

        /// <summary>按城市设天气（多城市行程时每个城市各取各的预报）。</summary>
        public static void SetWeatherFor(string key, List<Weather> days, string source)
        {
            if (days == null || days.Count == 0) return;
            _weatherByCity[key] = days;
            WeatherSourceByCity[key] = source;
            if (CityKeys.Count > 0 && CityKeys[0] == key) SetWeather(days, source);
        }

        public static Dictionary<string, string> WeatherSourceByCity = new Dictionary<string, string>();

        public static void ClearWeatherForAll()
        {
            _weatherByCity.Clear();
            WeatherSourceByCity.Clear();
            _weatherOverride = null;
        }

        public static List<Weather> WeatherList() { return _weatherOverride ?? _weather; }

        public static Weather WeatherAt(string key, int idx)
        {
            string first = CityKeys.Count > 0 ? CityKeys[0] : City;
            List<Weather> seq;
            _weatherByCity.TryGetValue(key, out seq);
            if (seq == null && key == first) seq = WeatherList();
            if (seq == null)
            {
                var m = CityMetas.FirstOrDefault(x => x.Key == key);
                seq = (m != null && m.Weather.Count > 0) ? m.Weather : WeatherList();
            }
            if (seq == null || seq.Count == 0) return new Weather();
            var w = seq[idx % seq.Count].Clone();
            w.Day = "第" + (idx + 1) + "天";
            return w;
        }

        public static double Haversine(double lat1, double lng1, double lat2, double lng2)
        {
            const double r = 6371.0;
            double a1 = lat1 * Math.PI / 180, b1 = lng1 * Math.PI / 180;
            double a2 = lat2 * Math.PI / 180, b2 = lng2 * Math.PI / 180;
            double dlat = a2 - a1, dlng = b2 - b1;
            double h = Math.Sin(dlat / 2) * Math.Sin(dlat / 2) +
                       Math.Cos(a1) * Math.Cos(a2) * Math.Sin(dlng / 2) * Math.Sin(dlng / 2);
            return 2 * r * Math.Asin(Math.Sqrt(h));
        }

        /// <summary>城际交通：（分钟，票价，公里数）。</summary>
        public static Tuple<int, int, double> Intercity(string aKey, string bKey)
        {
            if (aKey == bKey) return Tuple.Create(0, 0, 0.0);
            var ca = CityPosition(aKey);
            var cb = CityPosition(bKey);
            if (ca == null || cb == null) return Tuple.Create(120, 120, 300.0);
            double km = Haversine(ca[0], ca[1], cb[0], cb[1]);
            int minutes = Num.RoundHalfEven(45 + km / 2.6);
            int fare = Num.RoundHalfEven(km * 0.45 + 10);
            return Tuple.Create(minutes, fare, km);
        }

        /// <summary>按交通方式估算同城移动时间（已计入等车、换乘与步行接驳）。</summary>
        public static int RideMinutes(double km, string mode = "transit")
        {
            if (mode == "walk") return Math.Max(3, Num.RoundHalfEven(km * 12.5 + 1));
            if (mode == "taxi") return Math.Max(5, Num.RoundHalfEven(6 + km * 2.6));
            return Math.Max(9, Num.RoundHalfEven(14 + km * 3.1));
        }

        static readonly Dictionary<string, double> ModeFactor = new Dictionary<string, double>
        {
            { "walk", 1.70 }, { "taxi", 0.70 }, { "transit", 1.00 },
        };

        public const double TransitCityKm = 10.0;
        public const double TransitCitySpeed = 20.0;
        public const double TransitFastSpeed = 32.0;

        /// <summary>公交/地铁在车时间：市区 20km/h，超过 10km 后按 32km/h。</summary>
        public static double TransitRide(double km)
        {
            double cityPart = Math.Min(km, TransitCityKm) / TransitCitySpeed * 60.0;
            if (km <= TransitCityKm) return cityPart;
            return cityPart + (km - TransitCityKm) / TransitFastSpeed * 60.0;
        }

        /// <summary>远郊/长途段的固定分钟数按交通方式缩放（步行更慢、打车更快）。</summary>
        public static int ScaleMinutes(int minutes, string mode)
        {
            double f;
            if (!ModeFactor.TryGetValue(mode ?? "transit", out f)) f = 1.0;
            return Math.Max(1, Num.RoundHalfEven(minutes * f));
        }

        /// <summary>mock 地图 API：直线距离换算地铁/步行时间。</summary>
        /// <summary>真实路网矩阵：由 OSM/OSRM 生成，随应用离线分发（routes/&lt;city&gt;.json）。</summary>
        public sealed class RouteTable
        {
            public Dictionary<string, int> Index = new Dictionary<string, int>();
            public double[][] Foot;
            public double[][] Drive;
            public double[][] Dist;
        }

        /// <summary>平台注入：Android 上从 StreamingAssets 预读路网矩阵文本。</summary>
        public static Func<string, string> LoadRouteText;

        static readonly Dictionary<string, RouteTable> _routeCache = new Dictionary<string, RouteTable>();
        static readonly Dictionary<string, bool> _routeMiss = new Dictionary<string, bool>();

        public static RouteTable Routes(string cityKey)
        {
            if (cityKey == null) return null;
            if (_routeCache.ContainsKey(cityKey)) return _routeCache[cityKey];
            if (_routeMiss.ContainsKey(cityKey)) return null;
            string txt = null;
            try
            {
                if (LoadRouteText != null) txt = LoadRouteText(cityKey);
                if (string.IsNullOrEmpty(txt))
                {
                    // 桌面/测试：routes 可能和 cities 同级（agent/routes）也可能在 cities 里
                    var cand = new List<string>
                    {
                        Path.Combine(DataDir, "routes", cityKey + ".json"),
                        Path.Combine(Path.GetDirectoryName(DataDir) ?? ".", "routes", cityKey + ".json"),
                    };
                    foreach (var path in cand)
                    {
                        if (File.Exists(path)) { txt = File.ReadAllText(path, Encoding.UTF8); break; }
                    }
                }
            }
            catch (Exception) { txt = null; }
            if (string.IsNullOrEmpty(txt)) { _routeMiss[cityKey] = true; return null; }

            RouteTable tab = null;
            try
            {
                var root = MiniJson.Parse(txt);
                tab = new RouteTable();
                int i = 0;
                foreach (var n in root["nodes"].Items()) tab.Index[n.AsStr()] = i++;
                tab.Foot = ToMatrix(root["foot"]["duration"]);
                tab.Drive = ToMatrix(root["drive"]["duration"]);
                tab.Dist = ToMatrix(root["drive"]["distance"]);
            }
            catch (Exception) { tab = null; }

            if (tab == null) { _routeMiss[cityKey] = true; return null; }
            _routeCache[cityKey] = tab;
            return tab;
        }

        static double[][] ToMatrix(JVal v)
        {
            var rows = v.Items();
            var mat = new double[rows.Count][];
            for (int r = 0; r < rows.Count; r++)
            {
                var cols = rows[r].Items();
                mat[r] = new double[cols.Count];
                for (int c = 0; c < cols.Count; c++)
                {
                    var cell = cols[c];
                    mat[r][c] = (cell.Type == JType.Null) ? -1 : cell.AsDouble();
                }
            }
            return mat;
        }

        /// <summary>真实驾车路网距离（公里），无矩阵时返回 null。</summary>
        public static double? RouteKm(string cityKey, string aId, string bId)
        {
            var tab = Routes(cityKey);
            if (tab == null) return null;
            int i, j;
            if (!tab.Index.TryGetValue(aId, out i) || !tab.Index.TryGetValue(bId, out j)) return null;
            double m = tab.Dist[i][j];
            if (m < 0) return null;
            return Math.Round(m / 1000.0, 1, MidpointRounding.ToEven);
        }

        /// <summary>用真实路网求解同城出行分钟数；缺数据返回 null 由估算兜底。</summary>
        static int? MatrixMinutes(string cityKey, string aId, string bId, string mode)
        {
            var tab = Routes(cityKey);
            if (tab == null) return null;
            int i, j;
            if (!tab.Index.TryGetValue(aId, out i) || !tab.Index.TryGetValue(bId, out j)) return null;
            if (i == j) return null;
            if (mode == "walk")
            {
                double sec = tab.Foot[i][j];
                if (sec < 0) return null;
                return Math.Max(2, Num.RoundHalfEven(sec / 60.0));
            }
            double dist = tab.Dist[i][j];
            if (dist < 0) return null;
            double km = dist / 1000.0;
            if (mode == "taxi")
            {
                double sec = tab.Drive[i][j];
                if (sec < 0) sec = km / 26.0 * 3600.0;
                return Math.Max(4, Num.RoundHalfEven(sec * 1.35 / 60.0 + 3));
            }
            return Math.Max(8, Num.RoundHalfEven(8 + TransitRide(km) + 2));
        }

        public static int TravelMinutes(string aId, string bId, string mode = "transit")
        {
            if (aId == bId) return 0;
            var a = PoiById[aId];
            var b = PoiById[bId];
            if (CityOfPoi(aId) != CityOfPoi(bId))
            {
                return Intercity(CityOfPoi(aId), CityOfPoi(bId)).Item1;
            }
            var real = MatrixMinutes(CityOfPoi(aId), aId, bId, mode);
            if (real.HasValue) return real.Value;
            if (_farIds.Count > 0)
            {
                bool fa = _farIds.Contains(aId), fb = _farIds.Contains(bId);
                if (fa ^ fb) return ScaleMinutes(_farMinutes, mode);
                if (fa && fb) return ScaleMinutes(_farBoth, mode);
            }
            double km = Haversine(a.Lat, a.Lng, b.Lat, b.Lng);
            if (_farKm.HasValue && km > _farKm.Value) return ScaleMinutes(Num.RoundHalfEven(20 + km * 1.4), mode);
            return RideMinutes(km, mode);
        }

        public static int HotelTravelMinutes(string zone, string poiId, string mode = "transit")
        {
            string city = CityOfPoi(poiId);
            var zones = HotelsOf(city);
            if (string.IsNullOrEmpty(zone) || !zones.ContainsKey(zone)) zone = HotelOf(city);
            double[] z = zones.ContainsKey(zone) ? zones[zone] : zones.Values.First();
            var p = PoiById[poiId];
            var real = MatrixMinutes(city, "hotel:" + zone, p.Id, mode);
            if (real.HasValue) return real.Value;
            double km = Haversine(z[0], z[1], p.Lat, p.Lng);
            if (_farIds.Contains(p.Id)) return ScaleMinutes(_farMinutes, mode);
            if (_farKm.HasValue && km > _farKm.Value) return ScaleMinutes(Num.RoundHalfEven(20 + km * 1.4), mode);
            return RideMinutes(km + 0.2, mode);
        }
    }
}