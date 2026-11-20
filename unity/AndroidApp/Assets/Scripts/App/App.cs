// 拱程（Travel Agent）手机端：单场景、纯代码搭 UI，不依赖任何预制体与贴图。
// 数据来自 StreamingAssets/cities/*.json（由 unity/make_streaming.py 导出）。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Networking;
using UnityEngine.UI;
using TravelCore;

public class App : MonoBehaviour
{
    // ---------- 设计 token ----------
    static Color Hex(int rgb) => new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1f);
    static Color Alpha(Color c, float a) => new Color(c.r, c.g, c.b, a);

    static readonly Color Bg = Hex(0xF3F5F8);
    static readonly Color Surface = Hex(0xFFFFFF);
    static readonly Color Ink = Hex(0x101828);
    static readonly Color Ink2 = Hex(0x667085);
    static readonly Color Ink3 = Hex(0x98A2B3);
    static readonly Color Line = Hex(0xEAECF0);
    static readonly Color Brand = Hex(0x0E7C6B);
    static readonly Color BrandDeep = Hex(0x0A5C4F);
    static readonly Color BrandSoft = Hex(0xE7F4F1);
    static readonly Color Warn = Hex(0xB54708);
    static readonly Color WarnSoft = Hex(0xFFF6E5);
    static readonly Color Bad = Hex(0xB42318);
    static readonly Color BadSoft = Hex(0xFEE4E2);
    static readonly Color ChipBg = Hex(0xF2F4F7);

    static readonly string[] PREF_TAGS = { "历史人文", "自然风光", "美食", "购物", "艺术展馆", "夜景", "亲子" };

    static readonly Dictionary<int, Sprite> roundCache = new Dictionary<int, Sprite>();
    static Font appFont;

    // ---------- 状态 ----------
    Font font;
    Canvas canvas;
    RectTransform safeRoot;
    CanvasGroup homeCg, resultCg;
    Text toastText;
    GameObject homePanel, resultPanel;
    ScrollRect cityScroll;
    LayoutElement cityListLe;
    Text statusText;

    readonly List<string> cityKeys = new List<string>();
    readonly List<string> recentPicked = new List<string>();
    Coroutine csrCo;
    readonly Dictionary<string, string> cityNames = new Dictionary<string, string>();
    readonly Dictionary<string, int> cityPoiCount = new Dictionary<string, int>();
    readonly Dictionary<string, string> cityText = new Dictionary<string, string>();
    readonly List<string> picked = new List<string>();
    readonly List<int> pickedDays = new List<int>();
    readonly HashSet<string> prefs = new HashSet<string> { "历史人文" };

    class CityRow
    {
        public string Key, Name, Province;
        public double Lat, Lng;
        public bool HasData;
    }
    readonly List<CityRow> cities = new List<CityRow>();
    bool onlineWeather = true;
    string weatherNote = "内置天气";

    int days = 3;
    int budget = 3000;
    int startOffset = 0;
    string mode = "closed_loop";
    bool loading = true;
    ScrollRect resultScroll;
    readonly List<RectTransform> dayCards = new List<RectTransform>();

    string party = "情侣";
    string pace = "normal";
    string transport = "auto";      // auto = 按距离与花费自动推荐（含组合方案）
    string originKey = "";          // 出发地城市 key（空 = 就在首城）
    string originName = "";
    InputField originInput;
    Text originHint;
    int restNoon = 90;      // 午间休息分钟数（0 = 不安排）
    int restEve = 60;       // 傍晚休息分钟数

    class ChoiceChip : MonoBehaviour
    {
        public string Group;
        public string Value;
        public Image Bg;
        public Text Lab;
    }
    readonly List<ChoiceChip> choiceChips = new List<ChoiceChip>();
    Transform tripBar;
    LayoutElement tripRowLe;
    LayoutElement selCardLe;
    GameObject lastCardGO;
    Text lastLab;
    string lastSummary = "";
    int poiCount;
    readonly HashSet<int> openDays = new HashSet<int>();
    bool dayStateInit;

    Text daysText, budgetText, dateText, selectedText, weatherLabel;
    Image weatherKnob;
    InputField searchBox;
    string cityQuery = "";
    readonly List<PrefTag> prefChips = new List<PrefTag>();
    RunResult shown;

    bool autoMode;

    // ---------- 启动 ----------
    void Awake()
    {
        Application.targetFrameRate = 60;
        autoMode = HasArg("-auto");
        if (!autoMode) LoadState();
        font = PickFont();
        appFont = font;
        BuildCanvas();
        BuildHome();
        BuildResult();
        Show(homePanel);
        StartCoroutine(LoadData());
    }

    static bool HasArg(string name)
    {
        foreach (var a in Environment.GetCommandLineArgs())
            if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // 自动演示：截 5 张图（首页 / 全国城市搜索 / 实时天气 / 闭环 / 一次性）
    IEnumerator AutoShot()
    {
        while (loading) yield return null;
        onlineWeather = false;
        picked.Clear();
        pickedDays.Clear();
        prefs.Clear();
        picked.Add("nanchang");
        picked.Add("jiujiang");
        picked.Add("jingdezhen");
        pickedDays.Add(2); pickedDays.Add(2); pickedDays.Add(2);
        days = 6;
        prefs.Clear();
        prefs.Add("历史人文");
        prefs.Add("美食");
        PaintCities();
        UpdateForm();
        yield return new WaitForSecondsRealtime(1.2f);
        yield return Shot("shot_app_home.png");

        Generate("closed_loop");
        yield return new WaitForSecondsRealtime(1.2f);
        yield return Shot("shot_app_loop.png");
        if (resultScroll != null) resultScroll.verticalNormalizedPosition = 0.42f;
        yield return new WaitForSecondsRealtime(0.8f);
        yield return Shot("shot_app_trace.png");
        if (resultScroll != null) resultScroll.verticalNormalizedPosition = 0.18f;
        yield return new WaitForSecondsRealtime(0.8f);
        yield return Shot("shot_app_days.png");
        JumpDay(3);
        yield return new WaitForSecondsRealtime(0.6f);
        yield return Shot("shot_app_jump.png");
        if (resultScroll != null) resultScroll.verticalNormalizedPosition = 1f;

        Generate("single_pass");
        yield return new WaitForSecondsRealtime(1.2f);
        yield return Shot("shot_app_single.png");

        Show(homePanel);
        picked.Clear();
        pickedDays.Clear();
        picked.Add("luoyang");
        picked.Add("zhengzhou");
        pickedDays.Add(2); pickedDays.Add(2);
        days = 4;
        onlineWeather = true;
        cityQuery = "";
        if (searchBox != null) searchBox.text = "";
        PaintCities();
        UpdateForm();
        yield return new WaitForSecondsRealtime(1.0f);
        yield return Shot("shot_app_cities.png");
        Generate("closed_loop");
        yield return new WaitForSecondsRealtime(10.0f);
        yield return Shot("shot_app_live.png");

        Show(homePanel);
        UpdateForm();
        yield return new WaitForSecondsRealtime(1.0f);
        yield return Shot("shot_app_resume.png");

        Show(homePanel);
        picked.Clear();
        pickedDays.Clear();
        picked.Add("nanchang");
        picked.Add("jiujiang");
        picked.Add("jingdezhen");
        pickedDays.Add(2); pickedDays.Add(2); pickedDays.Add(2);
        days = 6;
        onlineWeather = false;
        originKey = "changsha";
        originName = "长沙";
        if (originInput != null) originInput.text = originName;
        UpdateOriginHint();
        transport = "auto";
        PaintCities();
        UpdateForm();
        yield return new WaitForSecondsRealtime(1.0f);
        yield return Shot("shot_app_origin.png");
        Generate("closed_loop");
        yield return new WaitForSecondsRealtime(1.6f);
        yield return Shot("shot_app_trip.png");
        if (resultScroll != null)
        {
            Canvas.ForceUpdateCanvases();
            resultScroll.verticalNormalizedPosition = 0f;
            Canvas.ForceUpdateCanvases();
            yield return new WaitForSecondsRealtime(1.2f);
            yield return Shot("shot_app_share.png");
        }

        Debug.Log("[App] auto shots done -> " + Application.persistentDataPath);
        yield return new WaitForSecondsRealtime(1.5f);
        Application.Quit();
    }

    IEnumerator Shot(string name)
    {
        string path = Path.Combine(Application.persistentDataPath, name);
        ScreenCapture.CaptureScreenshot(path);
        Debug.Log("[App] shot " + path);
        yield return new WaitForEndOfFrame();
        yield return null;
    }

    static Font PickFont()
    {
        string[] prefer =
        {
            "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "PingFang SC",
            "Noto Sans CJK SC", "Source Han Sans SC", "Droid Sans Fallback", "Arial"
        };
        Font f = null;
        try { f = Font.CreateDynamicFontFromOSFont(prefer, 42); } catch { }
        if (f != null) return f;
        try { f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { }
        return f;
    }

    // ---------- 数据加载 ----------
    readonly Dictionary<string, string> routeText = new Dictionary<string, string>();
    readonly HashSet<string> routeTried = new HashSet<string>();

    IEnumerator LoadData()
    {
        string dirText = null;
        yield return ReadStreaming("directory.json", t => dirText = t);
        if (dirText == null)
        {
            statusText.text = "数据读取失败：directory.json 缺失";
            yield break;
        }

        var root = MiniJson.Parse(dirText);
        foreach (var prov in root.Items())
        {
            foreach (var c in prov["cities"].Items())
            {
                bool has = c["has_data"].AsBool();
                string key = c["key"].IsNull ? null : c["key"].AsStr();
                if (string.IsNullOrEmpty(key)) continue;
                var row = new CityRow
                {
                    Key = key,
                    Name = c["name"].AsStr(),
                    Province = prov["name"].AsStr(),
                    Lat = c["lat"].AsDouble(),
                    Lng = c["lng"].AsDouble(),
                    HasData = has,
                };
                cities.Add(row);
                Data.CityPositions[key] = new[] { row.Lat, row.Lng };
                if (has)
                {
                    if (!cityKeys.Contains(key)) cityKeys.Add(key);
                    cityNames[key] = row.Name;
                }
            }
        }
        cityKeys.Sort(StringComparer.Ordinal);

        int loaded = 0;
        foreach (var key in cityKeys)
        {
            loaded++;
            if (loaded % 20 == 0 || loaded == cityKeys.Count)
                statusText.text = "正在载入景点数据　" + loaded + " / " + cityKeys.Count;
            string text = null;
            yield return ReadStreaming("cities/" + key + ".json", t => text = t);
            if (text == null) continue;
            cityText[key] = text;
            var spec = MiniJson.Parse(text);
            cityNames[key] = spec["name"].AsStr();
            cityPoiCount[key] = spec["pois"].Items().Count;
        }

        Data.LoadCityText = k => cityText.ContainsKey(k) ? cityText[k] : null;
        Data.LoadRouteText = k => routeText.ContainsKey(k) ? routeText[k] : null;
        Data.FixedCityKeys = new List<string>(cityKeys);
        SanitizePicked();

        loading = false;
        PaintCities();
        int pois = 0;
        foreach (var kv in cityPoiCount) pois += kv.Value;
        statusText.text = cities.Count + " 个城市 · " + pois + " 个真实景点";
        poiCount = pois;
        UpdateForm();
        if (autoMode) StartCoroutine(AutoShot());
    }

    /// <summary>生成前把所选城市的路网矩阵读进来（每个城市约 6 KB）。</summary>
    string TransportLabel()
    {
        if (transport == "auto") return "自动推荐";
        if (transport == "walk") return "步行";
        if (transport == "taxi") return "打车";
        return "公交/地铁";
    }

    /// <summary>把输入框里的城市名解析成出发地 key（支持“长沙市”这种写法）。</summary>
    void ResolveOrigin(string text)
    {
        text = (text ?? "").Trim();
        originKey = "";
        originName = "";
        if (text.Length > 0)
        {
            foreach (var c in cities)
            {
                if (c.Name == text || c.Key == text || c.Name + "市" == text || text == c.Name + "地区")
                {
                    originKey = c.Key;
                    originName = c.Name;
                    break;
                }
            }
        }
        UpdateOriginHint();
        SaveState();
    }

    void UpdateOriginHint()
    {
        if (originHint == null) return;
        if (string.IsNullOrEmpty(originKey))
        {
            originHint.text = "未设置出发地 · 只排目的地行程";
            originHint.color = Ink3;
        }
        else
        {
            originHint.text = "出发地：" + originName + " · 已计入去程与返程高铁时间";
            originHint.color = Brand;
        }
    }

    /// <summary>说明哪些城市用的是 OSM 实测路网、哪些是估算。</summary>
    string RouteNote()
    {
        var real = new List<string>();
        var est = new List<string>();
        foreach (var k in picked)
        {
            string nm = cityNames.ContainsKey(k) ? cityNames[k] : k;
            if (routeText.ContainsKey(k)) real.Add(nm); else est.Add(nm);
        }
        if (real.Count == 0) return "路网：所选城市暂无实测数据，按路网系数估算";
        string head = "路网：" + string.Join("、", real) + " 走 OSM 实测路线";
        return est.Count == 0 ? head : head + "；" + string.Join("、", est) + " 为估算";
    }

    IEnumerator LoadRoutes(List<string> keys)
    {
        foreach (var k in keys)
        {
            if (routeText.ContainsKey(k) || routeTried.Contains(k)) continue;
            routeTried.Add(k);
            string text = null;
            yield return ReadStreaming("routes/" + k + ".json", t => text = t);
            if (!string.IsNullOrEmpty(text)) routeText[k] = text;
        }
    }

    IEnumerator ReadStreaming(string rel, Action<string> done)
    {
        string path = Path.Combine(Application.streamingAssetsPath, rel);
#if UNITY_ANDROID && !UNITY_EDITOR
        using (var req = UnityWebRequest.Get(path))
        {
            yield return req.SendWebRequest();
            done(req.result == UnityWebRequest.Result.Success ? req.downloadHandler.text : null);
        }
#else
        done(File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null);
        yield break;
#endif
    }

    // ---------- 首页 ----------
    void BuildHome()
    {
        homePanel = Panel(safeRoot, "Home", Bg);
        Stretch(homePanel.GetComponent<RectTransform>());
        homeCg = homePanel.AddComponent<CanvasGroup>();

        BuildBottomBar(homePanel.transform);

        var scrollGO = new GameObject("Scroll", typeof(RectTransform), typeof(ScrollRect));
        scrollGO.transform.SetParent(homePanel.transform, false);
        var srt = scrollGO.GetComponent<RectTransform>();
        srt.anchorMin = new Vector2(0, 0);
        srt.anchorMax = new Vector2(1, 1);
        srt.offsetMin = new Vector2(0, 196);
        srt.offsetMax = Vector2.zero;
        var scroll = scrollGO.GetComponent<ScrollRect>();
        scroll.horizontal = false;
        scroll.scrollSensitivity = 26;
        resultScroll = scroll;

        var viewport = Panel(scrollGO.transform, "Viewport", new Color(1, 1, 1, 0));
        Stretch(viewport.GetComponent<RectTransform>());
        viewport.AddComponent<RectMask2D>();
        scroll.viewport = viewport.GetComponent<RectTransform>();

        var content = VContent(viewport.transform, new RectOffset(28, 28, 20, 40), 18);
        scroll.content = content.GetComponent<RectTransform>();
        var t = content.transform;

        // 顶部品牌卡
        var hero = Card(t, Brand, 30, 0);
        Lbl(hero.transform, "拱　程", 52, Color.white, TextAnchor.MiddleLeft, 74, true);
        Lbl(hero.transform, "把「看起来合理」的行程，排成「照着就能走」的样子", 23, Alpha(Color.white, 0.82f), TextAnchor.MiddleLeft, 40);
        var line = Box(hero.transform, "divider", Alpha(Color.white, 0.22f), 2);
        var lineLe = line.gameObject.AddComponent<LayoutElement>();
        lineLe.minHeight = 2; lineLe.preferredHeight = 2;
        statusText = Lbl(hero.transform, "正在读取内置数据…", 22, Alpha(Color.white, 0.78f), TextAnchor.MiddleLeft, 36);

        // 已选城市
        var selCard = Card(t, Surface, 26, 0);
        selCard.GetComponent<VerticalLayoutGroup>().padding = new RectOffset(26, 26, 20, 22);
        selCardLe = selCard.gameObject.GetComponent<LayoutElement>();
        if (selCardLe == null) selCardLe = selCard.gameObject.AddComponent<LayoutElement>();
        var selHead = new GameObject("selhead", typeof(RectTransform), typeof(LayoutElement), typeof(HorizontalLayoutGroup));
        selHead.transform.SetParent(selCard.transform, false);
        selHead.GetComponent<LayoutElement>().preferredHeight = 44;
        var selH = selHead.GetComponent<HorizontalLayoutGroup>();
        selH.childAlignment = TextAnchor.MiddleLeft;
        selH.childControlWidth = true; selH.childControlHeight = true;
        selH.childForceExpandWidth = false; selH.childForceExpandHeight = false;
        var selLab = Lbl(selHead.transform, "当前行程", 22, Ink3, TextAnchor.MiddleLeft, 44, true);
        selLab.GetComponent<LayoutElement>().flexibleWidth = 1;
        var clearBtn = Btn(selHead.transform, "清空", 21, ChipBg, Ink2, 44);
        clearBtn.GetComponent<LayoutElement>().preferredWidth = 100;
        clearBtn.onClick.AddListener(() => { picked.Clear(); pickedDays.Clear(); PaintCities(); });

        var tripRow = new GameObject("tripRow", typeof(RectTransform), typeof(LayoutElement), typeof(VerticalLayoutGroup));
        tripRow.transform.SetParent(selCard.transform, false);
        tripRowLe = tripRow.GetComponent<LayoutElement>();
        tripRowLe.preferredHeight = 76;
        var trh = tripRow.GetComponent<VerticalLayoutGroup>();
        trh.spacing = 8;
        trh.childAlignment = TextAnchor.UpperLeft;
        trh.childControlWidth = true; trh.childControlHeight = true;
        trh.childForceExpandWidth = true; trh.childForceExpandHeight = false;
        tripBar = tripRow.transform;

        // 上次行程
        var lastCard = Card(t, Surface, 26, 0);
        lastCard.GetComponent<VerticalLayoutGroup>().padding = new RectOffset(26, 26, 18, 18);
        lastCardGO = lastCard.gameObject;
        Lbl(lastCard.transform, "上次生成的行程", 21, Ink3, TextAnchor.MiddleLeft, 32, true);
        lastLab = Lbl(lastCard.transform, "", 23, Ink, TextAnchor.MiddleLeft, 34);
        var lastBtn = Btn(lastCard.transform, "重新打开（不用再算一遍参数）", 22, BrandSoft, BrandDeep, 62);
        lastBtn.onClick.AddListener(() => { if (!string.IsNullOrEmpty(lastSummary)) Generate(mode); });
        lastCardGO.SetActive(!string.IsNullOrEmpty(lastSummary));
        if (!string.IsNullOrEmpty(lastSummary)) lastLab.text = lastSummary;

        // 搜索 + 城市列表
        Section(t, "选择城市", "最多 3 座，按选择顺序串联");
        searchBox = SearchBox(t, v => { cityQuery = v; PaintCities(); });

        var listCard = Card(t, Surface, 26, 0);
        listCard.GetComponent<VerticalLayoutGroup>().padding = new RectOffset(18, 18, 16, 16);
        listCard.GetComponent<VerticalLayoutGroup>().spacing = 12;
        var listBox = new GameObject("CityBox", typeof(RectTransform), typeof(LayoutElement));
        listBox.transform.SetParent(listCard.transform, false);
        cityListLe = listBox.GetComponent<LayoutElement>();
        cityListLe.preferredHeight = 560;
        var cityInner = new GameObject("CityScroll", typeof(RectTransform), typeof(ScrollRect));
        cityInner.transform.SetParent(listBox.transform, false);
        Stretch(cityInner.GetComponent<RectTransform>());
        var cs = cityInner.GetComponent<ScrollRect>();
        cs.horizontal = false;
        cs.scrollSensitivity = 20;
        var cview = Panel(cityInner.transform, "Viewport", new Color(1, 1, 1, 0));
        Stretch(cview.GetComponent<RectTransform>());
        cview.AddComponent<RectMask2D>();
        cs.viewport = cview.GetComponent<RectTransform>();
        var ccontent = VContent(cview.transform, new RectOffset(0, 0, 0, 6), 10);
        cs.content = ccontent.GetComponent<RectTransform>();
        cityScroll = cs;

        // 参数
        Section(t, "行程参数", "天数 · 预算 · 出发日");
        var paramCard = Card(t, Surface, 26, 0);
        paramCard.GetComponent<VerticalLayoutGroup>().spacing = 6;
        daysText = StepperReadonly(paramCard.transform, "总天数");
        HLine(paramCard.transform);
        budgetText = Stepper(paramCard.transform, "预算", () => { budget = Mathf.Max(300, budget - 500); UpdateForm(); },
                             () => { budget = Mathf.Min(20000, budget + 500); UpdateForm(); });
        HLine(paramCard.transform);
        dateText = Stepper(paramCard.transform, "出发日", () => { startOffset -= 1; UpdateForm(); },
                           () => { startOffset += 1; UpdateForm(); });
        HLine(paramCard.transform);
        ChoiceRow(paramCard.transform, "同行人", "party",
                  new[] { "独行", "情侣", "家庭", "朋友" }, new[] { "独行", "情侣", "家庭", "朋友" });
        HLine(paramCard.transform);
        ChoiceRow(paramCard.transform, "节奏", "pace",
                  new[] { "轻松", "适中", "紧凑" }, new[] { "relaxed", "normal", "intense" });
        HLine(paramCard.transform);
        ChoiceRow(paramCard.transform, "交通方式", "transport",
                  new[] { "自动推荐", "走路", "公交/地铁", "打车" },
                  new[] { "auto", "walk", "transit", "taxi" });
        HLine(paramCard.transform);
        Lbl(paramCard.transform, "出发地", 26, Ink2, TextAnchor.MiddleLeft, 34);
        originInput = SearchBox(paramCard.transform, v => { }, "出发地（如：长沙；留空则从首座城市开始）");
        originInput.onEndEdit.AddListener(ResolveOrigin);
        originHint = Lbl(paramCard.transform, "未设置出发地 · 只排目的地行程", 19, Ink3,
                         TextAnchor.MiddleLeft, 40);
        originHint.horizontalOverflow = HorizontalWrapMode.Wrap;
        HLine(paramCard.transform);
        ChoiceRow(paramCard.transform, "午间休息（11:30–14:00）", "restNoon",
                  new[] { "不休息", "60 分", "90 分", "120 分" }, new[] { "0", "60", "90", "120" });
        HLine(paramCard.transform);
        ChoiceRow(paramCard.transform, "傍晚休息（18:00–20:00）", "restEve",
                  new[] { "不休息", "30 分", "60 分", "90 分" }, new[] { "0", "30", "60", "90" });
        Lbl(paramCard.transform, "出发日可选：最近 90 天 ~ 未来 15 天（天气预报覆盖未来 16 天，更远的日期用内置模板）",
            19, Ink3, TextAnchor.MiddleLeft, 44).horizontalOverflow = HorizontalWrapMode.Wrap;

        // 偏好
        Section(t, "旅行偏好", "影响景点排序与偏好得分");
        var prefCard = Card(t, Surface, 26, 0);
        prefCard.GetComponent<VerticalLayoutGroup>().spacing = 12;
        var grid = new GameObject("PrefGrid", typeof(RectTransform), typeof(GridLayoutGroup), typeof(LayoutElement));
        grid.transform.SetParent(prefCard.transform, false);
        var glg = grid.GetComponent<GridLayoutGroup>();
        glg.cellSize = new Vector2(206, 82);
        glg.spacing = new Vector2(12, 12);
        glg.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        glg.constraintCount = 3;
        grid.GetComponent<LayoutElement>().preferredHeight = 3 * 82 + 24;
        prefChips.Clear();
        foreach (var tag in PREF_TAGS)
        {
            string local = tag;
            var chip = Box(grid.transform, "pref_" + local, ChipBg, 20);
            var cb = chip.gameObject.AddComponent<Button>();
            cb.targetGraphic = chip;
            var lab = Lbl(chip.transform, tag, 24, Ink2, TextAnchor.MiddleCenter, 0);
            Stretch(lab.rectTransform);
            Destroy(lab.GetComponent<LayoutElement>());
            cb.onClick.AddListener(() =>
            {
                if (prefs.Contains(local)) prefs.Remove(local); else prefs.Add(local);
                UpdateForm();
            });
            var pt = chip.gameObject.AddComponent<PrefTag>();
            pt.Tag = local;
            prefChips.Add(pt);
        }

        // 天气开关
        var wCard = Card(t, Surface, 26, 0);
        var wRow = new GameObject("wrow", typeof(RectTransform), typeof(LayoutElement), typeof(HorizontalLayoutGroup));
        wRow.transform.SetParent(wCard.transform, false);
        wRow.GetComponent<LayoutElement>().preferredHeight = 56;
        var wh = wRow.GetComponent<HorizontalLayoutGroup>();
        wh.childAlignment = TextAnchor.MiddleLeft;
        wh.childControlWidth = true; wh.childControlHeight = true;
        wh.childForceExpandWidth = false; wh.childForceExpandHeight = false;
        var wLab = Lbl(wRow.transform, "在线天气 · 实时预报", 27, Ink, TextAnchor.MiddleLeft, 56, true);
        wLab.GetComponent<LayoutElement>().flexibleWidth = 1;
        var wb = Box(wRow.transform, "switch", Brand, 26);
        wb.gameObject.AddComponent<Button>().onClick.AddListener(() =>
        {
            onlineWeather = !onlineWeather;
            UpdateForm();
        });
        var wle = wb.gameObject.AddComponent<LayoutElement>();
        wle.preferredWidth = 104; wle.preferredHeight = 54;
        weatherKnob = Box(wb.transform, "knob", Color.white, 26);
        var kle = weatherKnob.gameObject.AddComponent<LayoutElement>();
        kle.ignoreLayout = true;
        var krt = weatherKnob.rectTransform;
        krt.anchorMin = new Vector2(1, 0.5f); krt.anchorMax = new Vector2(1, 0.5f);
        krt.sizeDelta = new Vector2(46, 46); krt.anchoredPosition = new Vector2(-4, 0);
        weatherLabel = Lbl(hero.transform, "", 22, Alpha(Color.white, 0.78f), TextAnchor.MiddleLeft, 0);

        modeTextHint(t);
        var foot = Lbl(t, "拱程 v1.0 · 2026-11 数据快照 · 携程公开页面抓取 + 13 城人工录入", 19, Hex(0xB0B8C4), TextAnchor.MiddleCenter, 40);
        foot.horizontalOverflow = HorizontalWrapMode.Wrap;
    }

    Text modeText;

    void modeTextHint(Transform t)
    {
        var card = Card(t, BrandSoft, 24, 0);
        modeText = Lbl(card.transform, "", 22, BrandDeep, TextAnchor.MiddleLeft, 0);
        modeText.horizontalOverflow = HorizontalWrapMode.Wrap;
    }

    void BuildBottomBar(Transform parent)
    {
        var bar = Panel(parent, "BottomBar", Surface);
        var rt = bar.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0, 0);
        rt.anchorMax = new Vector2(1, 0);
        rt.pivot = new Vector2(0.5f, 0);
        rt.sizeDelta = new Vector2(0, 196);
        rt.anchoredPosition = Vector2.zero;

        var top = Panel(bar.transform, "hair", Line);
        var trt = top.GetComponent<RectTransform>();
        trt.anchorMin = new Vector2(0, 1);
        trt.anchorMax = new Vector2(1, 1);
        trt.pivot = new Vector2(0.5f, 1);
        trt.sizeDelta = new Vector2(0, 2);

        var v = new GameObject("vb", typeof(RectTransform), typeof(VerticalLayoutGroup));
        v.transform.SetParent(bar.transform, false);
        Stretch(v.GetComponent<RectTransform>(), 28, 16);
        var vlg = v.GetComponent<VerticalLayoutGroup>();
        vlg.spacing = 10;
        vlg.childControlWidth = true; vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;

        var go = Btn(v.transform, "生成行程", 32, Brand, Color.white, 104);
        go.onClick.AddListener(() => Generate("closed_loop"));
        var alt = Btn(v.transform, "对比：一次性生成（无校验）", 23, ChipBg, Ink2, 60);
        alt.onClick.AddListener(() => Generate("single_pass"));
    }

    void PaintCities()
    {
        if (cityScroll == null) return;
        var content = cityScroll.content;
        for (int i = content.childCount - 1; i >= 0; i--)
        {
            var child = content.GetChild(i);
            child.SetParent(null, false);
            Destroy(child.gameObject);
        }

        string q = (cityQuery ?? "").Trim();
        var list = new List<CityRow>();
        if (q.Length > 0)
        {
            foreach (var row in cities)
                if (row.Name.IndexOf(q, StringComparison.Ordinal) >= 0 ||
                    row.Province.IndexOf(q, StringComparison.Ordinal) >= 0) list.Add(row);
        }
        else
        {
            // 已选的优先置顶，其余按目录顺序补满 40 条（更长的靠搜索）
            foreach (var k in picked)
            {
                var r = cities.Find(c => c.Key == k);
                if (r != null) list.Add(r);
            }
            int extra = 0;
            foreach (var k in recentPicked)
            {
                if (picked.Contains(k)) continue;
                var r2 = cities.Find(c => c.Key == k);
                if (r2 != null && !list.Contains(r2)) { list.Add(r2); extra++; }
            }
            foreach (var row in cities)
            {
                if (picked.Contains(row.Key) || list.Contains(row)) continue;
                if (extra >= 40) break;
                extra++;
                list.Add(row);
            }
        }
        if (list.Count == 0)
        {
            var empty = Lbl(content, "没有匹配的城市，换个关键词试试（如：洛阳 / 江西）", 23, Ink3, TextAnchor.MiddleCenter, 120);
            empty.horizontalOverflow = HorizontalWrapMode.Wrap;
        }
        foreach (var row in list) RowCity(content, row);
        if (cityListLe != null) cityListLe.preferredHeight = Mathf.Clamp(list.Count * 110 + 6, 150, 560);
        if (originInput != null) originInput.text = originName;
        UpdateOriginHint();
        UpdateForm();
    }

    void RowCity(Transform parent, CityRow cr)
    {
        string key = cr.Key;
        bool on = picked.Contains(key);
        var img = Box(parent, "row_" + key, on ? BrandSoft : Hex(0xF8FAFC), 20);
        var le = img.gameObject.AddComponent<LayoutElement>();
        le.minHeight = 100; le.preferredHeight = 100;
        var btn = img.gameObject.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.onClick.AddListener(() =>
        {
            if (picked.Contains(key))
            {
                int idxOld = picked.IndexOf(key);
                picked.RemoveAt(idxOld);
                if (idxOld < pickedDays.Count) pickedDays.RemoveAt(idxOld);
            }
            else if (picked.Count < 3)
            {
                picked.Add(key);
                pickedDays.Add(picked.Count == 1 ? Mathf.Clamp(days, 1, 7) : 2);
                recentPicked.Remove(key);
                recentPicked.Insert(0, key);
                if (recentPicked.Count > 12) recentPicked.RemoveAt(recentPicked.Count - 1);
            }
            else Toast("最多只能选 3 座城市");
            PaintCities();
        });

        var h = img.gameObject.AddComponent<HorizontalLayoutGroup>();
        h.padding = new RectOffset(18, 18, 0, 0);
        h.spacing = 16;
        h.childAlignment = TextAnchor.MiddleLeft;
        h.childControlWidth = true; h.childControlHeight = true;
        h.childForceExpandWidth = false; h.childForceExpandHeight = false;

        var av = Box(img.transform, "av", on ? Brand : BrandSoft, 31);
        var avLe = av.gameObject.AddComponent<LayoutElement>();
        avLe.preferredWidth = 62; avLe.preferredHeight = 62;
        var avLab = Lbl(av.transform, cr.Name.Substring(0, 1), 26, on ? Color.white : Brand, TextAnchor.MiddleCenter, 0, true);
        Stretch(avLab.rectTransform);
        Destroy(avLab.GetComponent<LayoutElement>());

        var col = new GameObject("col", typeof(RectTransform), typeof(LayoutElement), typeof(VerticalLayoutGroup));
        col.transform.SetParent(img.transform, false);
        col.GetComponent<LayoutElement>().flexibleWidth = 1;
        var cv = col.GetComponent<VerticalLayoutGroup>();
        cv.spacing = 2;
        cv.childControlWidth = true; cv.childControlHeight = true;
        cv.childForceExpandWidth = true; cv.childForceExpandHeight = false;
        Lbl(col.transform, cr.Name, 27, Ink, TextAnchor.MiddleLeft, 34, true);
        int n = cityPoiCount.ContainsKey(key) ? cityPoiCount[key] : 0;
        string sub = (cr.Province.Length > 0 && cr.Province != cr.Name ? cr.Province + " · " : "") + n + " 个景点";
        Lbl(col.transform, sub, 21, Ink3, TextAnchor.MiddleLeft, 28);

        var st = Box(img.transform, "st", on ? Brand : BrandSoft, 31);
        var stLe = st.gameObject.AddComponent<LayoutElement>();
        stLe.preferredWidth = 62; stLe.preferredHeight = 62;
        var stLab = Lbl(st.transform, on ? "✓" : "+", 30, on ? Color.white : Brand, TextAnchor.MiddleCenter, 0, true);
        Stretch(stLab.rectTransform);
        Destroy(stLab.GetComponent<LayoutElement>());
    }

    void ChoiceRow(Transform parent, string title, string group, string[] labels, string[] values)
    {
        var wrap = new GameObject("choice_" + group, typeof(RectTransform), typeof(LayoutElement), typeof(VerticalLayoutGroup));
        wrap.transform.SetParent(parent, false);
        var vlg = wrap.GetComponent<VerticalLayoutGroup>();
        vlg.spacing = 10;
        vlg.padding = new RectOffset(0, 0, 16, 12);
        vlg.childControlWidth = true; vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
        wrap.GetComponent<LayoutElement>().preferredHeight = 34 + 10 + 68 + 28;

        Lbl(wrap.transform, title, 24, Ink2, TextAnchor.MiddleLeft, 34, true);

        var row = new GameObject("row", typeof(RectTransform), typeof(LayoutElement), typeof(HorizontalLayoutGroup));
        row.transform.SetParent(wrap.transform, false);
        row.GetComponent<LayoutElement>().preferredHeight = 68;
        var h = row.GetComponent<HorizontalLayoutGroup>();
        h.spacing = 10;
        h.childControlWidth = true; h.childControlHeight = true;
        h.childForceExpandWidth = true; h.childForceExpandHeight = true;

        for (int i = 0; i < labels.Length; i++)
        {
            string val = values[i], lab = labels[i], grp = group;
            var chip = Box(row.transform, "c_" + grp + "_" + i, ChipBg, 18);
            var btn = chip.gameObject.AddComponent<Button>();
            btn.targetGraphic = chip;
            var txt = Lbl(chip.transform, lab, 23, Ink2, TextAnchor.MiddleCenter, 0);
            Stretch(txt.rectTransform);
            Destroy(txt.GetComponent<LayoutElement>());
            var cc = chip.gameObject.AddComponent<ChoiceChip>();
            cc.Group = grp; cc.Value = val; cc.Bg = chip; cc.Lab = txt;
            choiceChips.Add(cc);
            btn.onClick.AddListener(() =>
            {
                if (grp == "party") party = val;
                else if (grp == "transport") transport = val;
                else if (grp == "restNoon") restNoon = int.Parse(val, CultureInfo.InvariantCulture);
                else if (grp == "restEve") restEve = int.Parse(val, CultureInfo.InvariantCulture);
                else pace = val;
                UpdateForm();
            });
        }
    }

    void PaintTrip()
    {
        if (tripBar == null) return;
        for (int i = tripBar.childCount - 1; i >= 0; i--)
        {
            var ch = tripBar.GetChild(i);
            ch.SetParent(null, false);
            Destroy(ch.gameObject);
        }
        if (tripRowLe != null) tripRowLe.preferredHeight = 40;
        if (selCardLe != null) selCardLe.preferredHeight = 142;
        if (picked.Count == 0)
        {
            var hint = Lbl(tripBar, "还没有选城市 · 搜索后点一下加入", 23, Ink3, TextAnchor.MiddleLeft, 40);
            hint.horizontalOverflow = HorizontalWrapMode.Wrap;
            return;
        }
        if (tripRowLe != null) tripRowLe.preferredHeight = picked.Count * 80 - 8;
        if (selCardLe != null) selCardLe.preferredHeight = 142 + (picked.Count * 80 - 8);

        for (int i = 0; i < picked.Count; i++)
        {
            string key = picked[i];
            int idx = i;
            string name = cityNames.ContainsKey(key) ? cityNames[key] : key;
            int d = i < pickedDays.Count ? pickedDays[i] : 2;

            var chip = Box(tripBar, "trip_" + key, BrandSoft, 18);
            var le = chip.gameObject.AddComponent<LayoutElement>();
            le.preferredHeight = 72;
            var h = chip.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.padding = new RectOffset(22, 10, 0, 0);
            h.spacing = 8;
            h.childAlignment = TextAnchor.MiddleLeft;
            h.childControlWidth = true; h.childControlHeight = true;
            h.childForceExpandWidth = false; h.childForceExpandHeight = false;

            var nm = Lbl(chip.transform, name, 26, BrandDeep, TextAnchor.MiddleLeft, 52, true);
            nm.GetComponent<LayoutElement>().flexibleWidth = 1;

            var minus = Btn(chip.transform, "−", 26, Alpha(Color.white, 0.85f), BrandDeep, 52);
            minus.GetComponent<LayoutElement>().preferredWidth = 52;
            minus.onClick.AddListener(() => { BumpDays(idx, -1); });

            var dt = Lbl(chip.transform, d + " 天", 24, BrandDeep, TextAnchor.MiddleCenter, 52, true);
            dt.GetComponent<LayoutElement>().preferredWidth = 96;

            var plus = Btn(chip.transform, "+", 26, Alpha(Color.white, 0.85f), BrandDeep, 52);
            plus.GetComponent<LayoutElement>().preferredWidth = 52;
            plus.onClick.AddListener(() => { BumpDays(idx, +1); });

            var x = Btn(chip.transform, "×", 26, new Color(1, 1, 1, 0), Alpha(BrandDeep, 0.75f), 52);
            x.GetComponent<LayoutElement>().preferredWidth = 52;
            x.onClick.AddListener(() =>
            {
                if (idx < picked.Count) picked.RemoveAt(idx);
                if (idx < pickedDays.Count) pickedDays.RemoveAt(idx);
                PaintCities();
            });
        }
    }

    void BumpDays(int idx, int delta)
    {
        while (pickedDays.Count < picked.Count) pickedDays.Add(2);
        if (idx < 0 || idx >= pickedDays.Count) return;
        int total = 0;
        foreach (var v in pickedDays) total += v;
        int nv = Mathf.Clamp(pickedDays[idx] + delta, 1, 7);
        int nt = total - pickedDays[idx] + nv;
        if (nt > 12) { Toast("总天数最多 12 天"); return; }
        pickedDays[idx] = nv;
        days = nt;
        UpdateForm();
    }

    Text StepperReadonly(Transform parent, string label)
    {
        var row = new GameObject("ro_" + label, typeof(RectTransform), typeof(LayoutElement), typeof(HorizontalLayoutGroup));
        row.transform.SetParent(parent, false);
        row.GetComponent<LayoutElement>().preferredHeight = 88;
        var hlg = row.GetComponent<HorizontalLayoutGroup>();
        hlg.spacing = 14;
        hlg.childAlignment = TextAnchor.MiddleLeft;
        hlg.childControlWidth = true; hlg.childControlHeight = true;
        hlg.childForceExpandWidth = false; hlg.childForceExpandHeight = false;
        var lab = Lbl(row.transform, label, 26, Ink2, TextAnchor.MiddleLeft, 88);
        lab.GetComponent<LayoutElement>().preferredWidth = 130;
        var val = Lbl(row.transform, "", 26, Ink, TextAnchor.MiddleLeft, 88, true);
        val.GetComponent<LayoutElement>().flexibleWidth = 1;
        return val;
    }

    void UpdateForm()
    {
        int totalD = 0;
        foreach (var v in pickedDays) totalD += v;
        if (daysText != null) daysText.text = picked.Count == 0 ? "先选择城市" : totalD + " 天 · " + picked.Count + " 城";
        if (selectedText != null)
            selectedText.text = picked.Count == 0
                ? "还没有选城市，搜索后点一下加入"
                : string.Join("  →  ", picked.ConvertAll(k => cityNames.ContainsKey(k) ? cityNames[k] : k));
        if (budgetText != null) budgetText.text = "¥" + budget.ToString("N0", CultureInfo.InvariantCulture);
        var start = DateTime.Today.AddDays(startOffset);
        if (dateText != null) dateText.text = start.ToString("MM-dd", CultureInfo.InvariantCulture) + " " + WeekCn(start.DayOfWeek);
        if (modeText != null)
            modeText.text = mode == "closed_loop"
                ? "闭环模式：生成 → 校验 → 修复 → 复核，冲突会被逐条修掉"
                : "对比模式：一次性生成，跳过校验与修复，用来看差距";
        if (weatherLabel != null) weatherLabel.text = onlineWeather ? "实时天气：开" : "实时天气：关（用内置模板）";
        if (weatherKnob != null)
        {
            var wb = weatherKnob.transform.parent.GetComponent<Image>();
            if (wb != null) wb.color = onlineWeather ? Brand : Hex(0xD0D5DD);
            var krt = weatherKnob.rectTransform;
            krt.anchorMin = krt.anchorMax = new Vector2(onlineWeather ? 1 : 0, 0.5f);
            krt.anchoredPosition = new Vector2(onlineWeather ? -4 : 4, 0);
        }
        PaintTrip();
        foreach (var cc in choiceChips)
        {
            if (cc == null || cc.Bg == null) continue;
            bool on = cc.Group == "party" ? party == cc.Value
                    : cc.Group == "transport" ? transport == cc.Value
                    : cc.Group == "restNoon" ? restNoon.ToString(CultureInfo.InvariantCulture) == cc.Value
                    : cc.Group == "restEve" ? restEve.ToString(CultureInfo.InvariantCulture) == cc.Value
                    : pace == cc.Value;
            cc.Bg.color = on ? Brand : ChipBg;
            if (cc.Lab != null) cc.Lab.color = on ? Color.white : Ink2;
        }
        foreach (var pt in prefChips)
        {
            if (pt == null) continue;
            var img = pt.GetComponent<Image>();
            var txt = pt.GetComponentInChildren<Text>();
            bool on = prefs.Contains(pt.Tag);
            if (img != null) img.color = on ? Brand : ChipBg;
            if (txt != null) txt.color = on ? Color.white : Ink2;
        }
    }

    /// <summary>该天实际排出的休息总分钟数。</summary>
    static int RestMinutes(Day day, RunResult res)
    {
        if (day == null || day.RestDone == null || day.RestDone.Count == 0) return 0;
        var rests = res != null && res.Itinerary != null && res.Itinerary.Rests != null && res.Itinerary.Rests.Count > 0
            ? res.Itinerary.Rests : Data.DefaultRests();
        int sum = 0;
        for (int i = 0; i < rests.Count && i < day.RestDone.Count; i++)
            if (day.RestDone[i]) sum += rests[i].Minutes;
        return sum;
    }

    static string WeekCn(DayOfWeek w)
    {
        switch (w)
        {
            case DayOfWeek.Monday: return "周一";
            case DayOfWeek.Tuesday: return "周二";
            case DayOfWeek.Wednesday: return "周三";
            case DayOfWeek.Thursday: return "周四";
            case DayOfWeek.Friday: return "周五";
            case DayOfWeek.Saturday: return "周六";
            default: return "周日";
        }
    }

    // ---------- 结果界面 ----------
    void BuildResult()
    {
        resultPanel = Panel(safeRoot, "Result", Bg);
        Stretch(resultPanel.GetComponent<RectTransform>());
        resultCg = resultPanel.AddComponent<CanvasGroup>();
        resultPanel.SetActive(false);
    }

    void Generate(string m) { Generate(m, null); }

    void Generate(string m, string busy)
    {
        if (loading) { statusText.text = "数据还在加载…"; return; }
        if (!string.IsNullOrEmpty(busy)) Toast(busy);
        if (picked.Count == 0) { Toast("请先选择至少一座城市"); return; }
        while (pickedDays.Count < picked.Count) pickedDays.Add(2);
        days = 0;
        foreach (var d in pickedDays) days += d;
        if (days > 12) { Toast("总天数最多 12 天"); return; }

        mode = m;
        StartCoroutine(GenerateFlow(m));
    }

    IEnumerator GenerateFlow(string m)
    {
        var keys = new List<string>(picked);
        if (keys.Count == 0) yield break;
        if (weatherLabel != null) weatherLabel.text = "正在载入路网数据…";
        yield return LoadRoutes(keys);
        if (onlineWeather)
        {
            yield return GenerateWithLiveWeather(m);
            yield break;
        }
        weatherNote = "内置天气（离线）";
        RunAndRender(BuildProfile(m), m);
    }

    Profile BuildProfile(string m)
    {
        var profile = new Profile
        {
            Days = days,
            Budget = budget,
            Party = party,
            Pace = pace,
            Transport = transport,
            Origin = originKey,
            ReturnHome = !string.IsNullOrEmpty(originKey),
            StartDate = DateTime.Today.AddDays(startOffset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
        profile.Cities = new List<string>(picked);
        profile.CityDays = new List<int>(pickedDays);
        int totalDays = 0;
        foreach (var d in pickedDays) totalDays += d;
        profile.Days = Mathf.Clamp(totalDays, 1, 12);
        profile.MustVisit = new List<string>();
        profile.Hotel = "";
        profile.Prefs = prefs.Count > 0 ? prefs.ToList() : new List<string> { "历史人文" };
        profile.Rests = new List<RestSpec>();
        if (restNoon > 0)
            profile.Rests.Add(new RestSpec { Label = "午间休息", Start = 11 * 60 + 30, End = 14 * 60, Minutes = restNoon });
        if (restEve > 0)
            profile.Rests.Add(new RestSpec { Label = "傍晚休息", Start = 18 * 60, End = 20 * 60, Minutes = restEve });
        if (profile.Rests.Count == 0) profile.Rests = Data.DefaultRests();
        return profile;
    }

    // ---------- 在线天气 ----------
    static readonly Dictionary<int, string> WMO_TEXT = new Dictionary<int, string>
    {
        { 0, "晴" }, { 1, "晴间多云" }, { 2, "多云" }, { 3, "阴" }, { 45, "雾" }, { 48, "雾凇" },
        { 51, "毛毛雨" }, { 53, "毛毛雨" }, { 55, "毛毛雨" }, { 56, "冻雨" }, { 57, "冻雨" },
        { 61, "小雨" }, { 63, "中雨" }, { 65, "大雨" }, { 66, "冻雨" }, { 67, "冻雨" },
        { 71, "小雪" }, { 73, "中雪" }, { 75, "大雪" }, { 77, "米雪" },
        { 80, "阵雨" }, { 81, "阵雨" }, { 82, "强阵雨" }, { 85, "阵雪" }, { 86, "强阵雪" },
        { 95, "雷阵雨" }, { 96, "雷阵雨伴冰雹" }, { 99, "强雷暴" },
    };

    const string WC_PREFIX = "wc_";
    const long WC_TTL = 10800; // 3 小时

    bool TryCachedWeather(string key, int nDays, string sd, out List<Weather> list)
    {
        list = null;
        string raw = PlayerPrefs.GetString(WC_PREFIX + key + "_" + sd + "_" + nDays, "");
        if (string.IsNullOrEmpty(raw)) return false;
        try
        {
            var o = MiniJson.Parse(raw);
            long stamp = (long)o["stamp"].AsDouble();
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - stamp > WC_TTL) return false;
            var arr = o["days"].Items();
            if (arr.Count < nDays) return false;
            list = new List<Weather>();
            for (int i = 0; i < nDays; i++)
            {
                var d = arr[i];
                list.Add(new Weather
                {
                    Day = "第" + (i + 1) + "天",
                    Text = d["t"].AsStr(),
                    Low = (int)d["lo"].AsDouble(),
                    High = (int)d["hi"].AsDouble(),
                    Rain = (int)d["r"].AsDouble(),
                });
            }
            return true;
        }
        catch { list = null; return false; }
    }

    void SaveCachedWeather(string key, int nDays, string sd, List<Weather> list)
    {
        var w = new JsonWriter();
        w.BeginObject();
        w.Key("stamp"); w.Num(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        w.Key("days"); w.BeginArray();
        foreach (var d in list)
        {
            w.BeginObject();
            w.Key("t"); w.Str(d.Text);
            w.Key("lo"); w.Int(d.Low);
            w.Key("hi"); w.Int(d.High);
            w.Key("r"); w.Int(d.Rain);
            w.EndObject();
        }
        w.EndArray();
        w.EndObject();
        PlayerPrefs.SetString(WC_PREFIX + key + "_" + sd + "_" + nDays, w.ToString());
        PlayerPrefs.Save();
    }

    IEnumerator FetchCityWeather(string key, int nDays, string startDate)
    {
        List<Weather> cached;
        if (TryCachedWeather(key, nDays, startDate, out cached))
        {
            Data.SetWeatherFor(key, cached, "Open-Meteo 缓存");
            yield break;
        }
        double[] pos = Data.CityPosition(key);
        if (pos == null || nDays <= 0) yield break;
        var start = DateTime.ParseExact(startDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var end = start.AddDays(nDays - 1);
        string url = string.Format(CultureInfo.InvariantCulture,
            "https://api.open-meteo.com/v1/forecast?latitude={0:0.####}&longitude={1:0.####}" +
            "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max" +
            "&timezone=Asia%2FShanghai&start_date={2}&end_date={3}",
            pos[0], pos[1], start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        using (var req = UnityWebRequest.Get(url))
        {
            req.timeout = 10;
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success) yield break;
            JVal root;
            try { root = MiniJson.Parse(req.downloadHandler.text); } catch { yield break; }
            var daily = root["daily"];
            var times = daily["time"];
            if (times.Count == 0) yield break;
            var list = new List<Weather>();
            for (int i = 0; i < times.Count && i < nDays; i++)
            {
                int code = daily["weather_code"].At(i).AsInt();
                string text;
                if (!WMO_TEXT.TryGetValue(code, out text)) text = "多云";
                var pr = daily["precipitation_probability_max"].At(i);
                list.Add(new Weather
                {
                    Day = "第" + (i + 1) + "天",
                    Text = text,
                    Low = Num.RoundHalfEven(daily["temperature_2m_min"].At(i).AsDouble()),
                    High = Num.RoundHalfEven(daily["temperature_2m_max"].At(i).AsDouble()),
                    Rain = pr.IsNull ? 30 : pr.AsInt(),
                });
            }
            if (list.Count > 0)
            {
                Data.SetWeatherFor(key, list, "Open-Meteo 实时预报");
                SaveCachedWeather(key, nDays, startDate, list);
            }
        }
    }

    IEnumerator GenerateWithLiveWeather(string m)
    {
        if (weatherLabel != null) weatherLabel.text = "正在获取实时天气…";
        var profile = BuildProfile(m);
        string sd = profile.StartDate;
        var plan = profile.CityPlan();
        Data.ClearWeatherForAll();
        foreach (var kv in plan)
        {
            yield return FetchCityWeather(kv.Key, kv.Value, sd);
        }
        int ok = 0;
        foreach (var kv in plan)
        {
            string src;
            if (Data.WeatherSourceByCity.TryGetValue(kv.Key, out src) && src.StartsWith("Open-Meteo")) ok++;
        }
        weatherNote = ok > 0
            ? (ok == plan.Count ? "实时天气 · Open-Meteo" : "部分城市实时天气，其余内置")
            : "实时天气获取失败，已回退内置";
        if (weatherLabel != null)
            weatherLabel.text = "实时天气：" + (onlineWeather ? "开" : "关") + " · " + weatherNote;
        statusText.text = cities.Count + " 个城市 · " + poiCount + " 个真实景点";
        RunAndRender(profile, m);
    }

    void RunAndRender(Profile profile, string m)
    {
        if (!onlineWeather)
        {
            weatherNote = "内置天气（模板）";
            if (weatherLabel != null) weatherLabel.text = "实时天气：关 · 内置天气（模板）";
        }
        RunResult res;
        try
        {
            res = Engine.Run(profile, m);
        }
        catch (Exception e)
        {
            Toast("生成失败：" + e.Message);
            return;
        }
        shown = res;
        {
            string cname = "";
            for (int i = 0; i < picked.Count; i++)
            {
                if (i > 0) cname += " → ";
                cname += Data.CityName(picked[i]);
            }
            lastSummary = string.Format(CultureInfo.InvariantCulture,
                "{0} · {1} 天 · 满足率 {2:0.0}% · {3}",
                cname, res.Itinerary.Days.Count, res.Report.Csr * 100,
                res.Report.HardFailed > 0 ? res.Report.HardFailed + " 处硬冲突" : "无硬冲突");
            PlayerPrefs.SetString("lastSummary", lastSummary);
            PlayerPrefs.SetString("lastMode", mode);
            if (lastCardGO != null) lastCardGO.SetActive(true);
            if (lastLab != null) lastLab.text = lastSummary;
        }
        RunResult alt = null;
        string altMode = m == "closed_loop" ? "single_pass" : "closed_loop";
        try { alt = Engine.Run(profile, altMode); } catch { alt = null; }
        RenderResult(res, alt);
        Show(resultPanel);
    }

    void RenderResult(RunResult res, RunResult alt)
    {
        for (int i = resultPanel.transform.childCount - 1; i >= 0; i--)
            Destroy(resultPanel.transform.GetChild(i).gameObject);

        var scrollGO = new GameObject("Scroll", typeof(RectTransform), typeof(ScrollRect));
        scrollGO.transform.SetParent(resultPanel.transform, false);
        Stretch(scrollGO.GetComponent<RectTransform>());
        var scroll = scrollGO.GetComponent<ScrollRect>();
        scroll.horizontal = false;
        scroll.scrollSensitivity = 26;
        resultScroll = scroll;

        var viewport = Panel(scrollGO.transform, "Viewport", new Color(1, 1, 1, 0));
        Stretch(viewport.GetComponent<RectTransform>());
        viewport.AddComponent<RectMask2D>();
        scroll.viewport = viewport.GetComponent<RectTransform>();

        var content = VContent(viewport.transform, new RectOffset(28, 28, 20, 48), 16);
        scroll.content = content.GetComponent<RectTransform>();
        var t = content.transform;

        var rep = res.Report;
        var met = res.Metrics;

        // 顶栏
        var top = new GameObject("top", typeof(RectTransform), typeof(LayoutElement), typeof(HorizontalLayoutGroup));
        top.transform.SetParent(t, false);
        top.GetComponent<LayoutElement>().preferredHeight = 76;
        var th = top.GetComponent<HorizontalLayoutGroup>();
        th.spacing = 14; th.childAlignment = TextAnchor.MiddleLeft;
        th.childControlWidth = true; th.childControlHeight = true;
        th.childForceExpandWidth = false; th.childForceExpandHeight = false;
        var back = Box(top.transform, "back", Surface, 22);
        var ble = back.gameObject.AddComponent<LayoutElement>();
        ble.preferredWidth = 70; ble.preferredHeight = 70;
        back.gameObject.AddComponent<Button>().onClick.AddListener(() => Show(homePanel));
        var backLab = Lbl(back.transform, "←", 34, Ink, TextAnchor.MiddleCenter, 0);
        Stretch(backLab.rectTransform);
        Destroy(backLab.GetComponent<LayoutElement>());
        var title = Lbl(top.transform, mode == "closed_loop" ? "闭环规划结果" : "一次性生成结果", 32, Ink, TextAnchor.MiddleLeft, 76, true);
        title.GetComponent<LayoutElement>().flexibleWidth = 1;

        // 城市链
        if (res.Itinerary.Days.Count > 0)
        {
            var seq = new List<string>();
            var seqDays = new List<int>();
            foreach (var dy in res.Itinerary.Days)
            {
                string cn = Data.CityName(dy.City);
                int last = seq.Count - 1;
                if (last >= 0 && seq[last] == cn) seqDays[last]++;
                else { seq.Add(cn); seqDays.Add(1); }
            }
            string chainText = "";
            for (int ci = 0; ci < seq.Count; ci++)
            {
                if (ci > 0) chainText += "  →  ";
                chainText += seq[ci] + "　" + seqDays[ci] + " 天";
            }
            var chainCard = Card(t, BrandSoft, 20, 0);
            chainCard.GetComponent<VerticalLayoutGroup>().padding = new RectOffset(22, 22, 16, 16);
            Lbl(chainCard.transform, chainText, 23, BrandDeep, TextAnchor.MiddleLeft, 34, true);
            Lbl(chainCard.transform, seq.Count > 1
                ? "跨城行程 · 城际交通与到站放行李时间都已计入"
                : "单城行程 · 出发当天按所选日期重新排程", 20, Ink2, TextAnchor.MiddleLeft, 30);
        }

        // 往返交通（出发地 → 首城 → … → 出发地）
        {
            var oleg = res.Itinerary.OriginLeg;
            var rleg = res.Itinerary.ReturnLeg;
            if (oleg != null && !string.IsNullOrEmpty(oleg.Code))
            {
                var rt = Card(t, Surface, 22, 0);
                rt.GetComponent<VerticalLayoutGroup>().spacing = 10;
                rt.GetComponent<VerticalLayoutGroup>().padding = new RectOffset(22, 22, 16, 18);
                Lbl(rt.transform, "往返交通", 21, Ink3, TextAnchor.MiddleLeft, 30);
                var a1 = Lbl(rt.transform, string.Format(CultureInfo.InvariantCulture,
                    "🚄 去程 {0}　{1} {2} → {3} {4}", oleg.Code, oleg.DepStation, Rail.Fmt(oleg.Dep),
                    oleg.ArrStation, Rail.Fmt(oleg.Arr)), 21, Ink, TextAnchor.MiddleLeft, 32);
                a1.horizontalOverflow = HorizontalWrapMode.Wrap;
                Lbl(rt.transform, string.Format(CultureInfo.InvariantCulture,
                    "　 历时 {0} 分钟 · ¥{1} · 到站后 30 分钟放行李", oleg.Run, oleg.Fare),
                    19, Ink3, TextAnchor.MiddleLeft, 28).horizontalOverflow = HorizontalWrapMode.Wrap;
                if (rleg != null && !string.IsNullOrEmpty(rleg.Code))
                {
                    var a2 = Lbl(rt.transform, string.Format(CultureInfo.InvariantCulture,
                        "🚄 返程 {0}　{1} {2} → {3} {4}", rleg.Code, rleg.DepStation, Rail.Fmt(rleg.Dep),
                        rleg.ArrStation, Rail.Fmt(rleg.Arr)), 21, Ink, TextAnchor.MiddleLeft, 32);
                    a2.horizontalOverflow = HorizontalWrapMode.Wrap;
                    Lbl(rt.transform, string.Format(CultureInfo.InvariantCulture,
                        "　 历时 {0} 分钟 · ¥{1}", rleg.Run, rleg.Fare), 19, Ink3,
                        TextAnchor.MiddleLeft, 28).horizontalOverflow = HorizontalWrapMode.Wrap;
                    Lbl(rt.transform, "往返车票 ¥" + (oleg.Fare + rleg.Fare) + "，已计入总花费", 19, Ink3,
                        TextAnchor.MiddleLeft, 28).horizontalOverflow = HorizontalWrapMode.Wrap;
                }
            }
        }

        // 一句话行程摘要
        {
            int nCity = 0;
            string prevC = "";
            foreach (var dy in res.Itinerary.Days) { if (dy.City != prevC) { nCity++; prevC = dy.City; } }
            int nStop = res.Itinerary.Ids().Count;
            int nDay = Math.Max(1, res.Itinerary.Days.Count);
            double avg = (double)nStop / nDay;
            int totRide = 0, same = 0;
            foreach (var dy in res.Itinerary.Days)
            {
                if (dy.Leg != null && !dy.Leg.IsEmpty) totRide += (int)dy.Leg.Minutes;
                foreach (var st in dy.Stops) totRide += st.FromPrev;
                for (int k = 0; k + 1 < dy.Stops.Count; k++)
                    if (dy.Stops[k].PoiId == dy.Stops[k + 1].PoiId) same++;
            }
            string ride = (totRide / 60) + " 小时 " + (totRide % 60) + " 分";
            var sumLab = Lbl(t, nCity + " 座城市 · " + nDay + " 天 · " + nStop + " 处景点 · 日均 " + avg.ToString("0.0") + " 处 · 路上 " + ride,
                21, Ink2, TextAnchor.MiddleLeft, 56);
            sumLab.horizontalOverflow = HorizontalWrapMode.Wrap;
        }

        // 换交通方式重算
        {
            var mCard = Card(t, Surface, 22, 0);
            mCard.GetComponent<VerticalLayoutGroup>().spacing = 10;
            mCard.GetComponent<VerticalLayoutGroup>().padding = new RectOffset(22, 22, 16, 18);
            Lbl(mCard.transform, "换交通方式重算", 21, Ink3, TextAnchor.MiddleLeft, 30);
            var mrow = new GameObject("mtr", typeof(RectTransform), typeof(LayoutElement), typeof(HorizontalLayoutGroup));
            mrow.transform.SetParent(mCard.transform, false);
            mrow.GetComponent<LayoutElement>().preferredHeight = 62;
            var mh = mrow.GetComponent<HorizontalLayoutGroup>();
            mh.spacing = 10; mh.childAlignment = TextAnchor.MiddleLeft;
            mh.childControlWidth = true; mh.childControlHeight = true;
            mh.childForceExpandWidth = false; mh.childForceExpandHeight = false;
            string[] mvals = { "auto", "walk", "transit", "taxi" };
            string[] mlabs = { "✨ 推荐", "🚶 步行", "🚇 公交/地铁", "🚕 打车" };
            for (int mi = 0; mi < mvals.Length; mi++)
            {
                string mv = mvals[mi];
                bool on = transport == mv;
                var mb = Btn(mrow.transform, mlabs[mi], 21, on ? BrandSoft : ChipBg, on ? BrandDeep : Ink2, 58);
                var mle = mb.GetComponent<LayoutElement>();
                if (mle == null) mle = mb.gameObject.AddComponent<LayoutElement>();
                mle.preferredWidth = 196;
                mb.onClick.AddListener(() =>
                {
                    if (transport == mv) return;
                    transport = mv;
                    SaveState();
                    Generate(res.Mode, "正在按「" + TransportLabel() + "」重算…");
                    Toast("已按「" + TransportLabel() + "」重算");
                });
            }
        }

        // CSR 卡
        var hero = Card(t, Brand, 30, 0);
        hero.GetComponent<VerticalLayoutGroup>().spacing = 8;
        hero.GetComponent<VerticalLayoutGroup>().padding = new RectOffset(28, 28, 26, 28);
        var headRow = new GameObject("hr", typeof(RectTransform), typeof(LayoutElement), typeof(HorizontalLayoutGroup));
        headRow.transform.SetParent(hero.transform, false);
        headRow.GetComponent<LayoutElement>().preferredHeight = 44;
        var hrh = headRow.GetComponent<HorizontalLayoutGroup>();
        hrh.childAlignment = TextAnchor.MiddleLeft;
        hrh.childControlWidth = true; hrh.childControlHeight = true;
        hrh.childForceExpandWidth = false; hrh.childForceExpandHeight = false;
        var capLab = Lbl(headRow.transform, "约束满足率", 23, Alpha(Color.white, 0.85f), TextAnchor.MiddleLeft, 44);
        capLab.GetComponent<LayoutElement>().flexibleWidth = 1;
        var pill = Box(headRow.transform, "pill", Alpha(Color.white, 0.18f), 20);
        var ple = pill.gameObject.AddComponent<LayoutElement>();
        ple.preferredWidth = 168; ple.preferredHeight = 44;
        var pillLab = Lbl(pill.transform, rep.Feasible ? "行程可执行" : "仍有冲突", 21, Color.white, TextAnchor.MiddleCenter, 0);
        Stretch(pillLab.rectTransform);
        Destroy(pillLab.GetComponent<LayoutElement>());

        var csrLab = Lbl(hero.transform, string.Format(CultureInfo.InvariantCulture, "{0:0.0}%", rep.Csr * 100), 96, Color.white, TextAnchor.MiddleLeft, 120, true);

        var bar = Box(hero.transform, "progress", Alpha(Color.white, 0.24f), 7);
        var barLe = bar.gameObject.AddComponent<LayoutElement>();
        barLe.preferredHeight = 14;
        var fill = Box(bar.transform, "fill", Color.white, 7);
        var frt = fill.rectTransform;
        frt.anchorMin = new Vector2(0, 0);
        frt.anchorMax = new Vector2(Mathf.Clamp01((float)rep.Csr), 1);
        frt.offsetMin = Vector2.zero; frt.offsetMax = Vector2.zero;
        var fillLe = fill.gameObject.GetComponent<LayoutElement>();
        if (fillLe != null) Destroy(fillLe);

        // 三宫格指标
        var stats = new GameObject("stats", typeof(RectTransform), typeof(LayoutElement), typeof(HorizontalLayoutGroup));
        stats.transform.SetParent(t, false);
        stats.GetComponent<LayoutElement>().preferredHeight = 132;
        var sh = stats.GetComponent<HorizontalLayoutGroup>();
        sh.spacing = 12;
        sh.childControlWidth = true; sh.childControlHeight = true;
        sh.childForceExpandWidth = true; sh.childForceExpandHeight = true;
        Stat(stats.transform, rep.HardFailed.ToString(), "硬冲突", rep.HardFailed > 0 ? Bad : Brand);
        Stat(stats.transform, rep.SoftFailed.ToString(), "软提醒", rep.SoftFailed > 0 ? Warn : Brand);
        Stat(stats.transform, "¥" + met.Cost.ToString("N0", CultureInfo.InvariantCulture), "总花费", Ink);

        int rideMin = 0;
        foreach (var dd in res.Itinerary.Days)
        {
            if (dd.Leg != null && !dd.Leg.IsEmpty) rideMin += dd.Leg.Minutes;
            foreach (var ss in dd.Stops) rideMin += ss.FromPrev;
        }
        string rideText = rideMin >= 60 ? (rideMin / 60) + " 小时 " + (rideMin % 60) + " 分" : rideMin + " 分";
        Lbl(t, string.Format(CultureInfo.InvariantCulture,
              "{0} 个景点 · 修复 {1} 轮 · 工具调用 {2} 次 · {3:0.0} ms",
              met.Stops, met.RepairRounds, met.ToolCalls, met.LatencyMs), 21, Ink2, TextAnchor.MiddleLeft, 34);
        Lbl(t, string.Format(CultureInfo.InvariantCulture,
              "路上共 {0} · 平均每段 {1} 分钟 · 交通方式：{2}",
              rideText, met.Stops > 0 ? rideMin / met.Stops : 0,
              TransportLabel()), 21, Ink2, TextAnchor.MiddleLeft, 34);
        Lbl(t, "天气：" + weatherNote, 21, Ink2, TextAnchor.MiddleLeft, 34);
        Lbl(t, RouteNote(), 21, Ink3, TextAnchor.MiddleLeft, 34);

        if (mode == "closed_loop" && res.Profile != null)
        {
            RunResult baseRes = alt;
            if (baseRes != null)
            {
                Section(t, "与一次性生成相比", "同一组参数，两种排法");
                var cmp = Card(t, Surface, 26, 0);
                cmp.GetComponent<VerticalLayoutGroup>().spacing = 8;
                cmp.GetComponent<VerticalLayoutGroup>().padding = new RectOffset(24, 24, 18, 18);
                CmpRow(cmp.transform, "约束满足率",
                    (baseRes.Report.Csr * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%",
                    (res.Report.Csr * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%",
                    res.Report.Csr >= baseRes.Report.Csr);
                CmpRow(cmp.transform, "硬冲突",
                    baseRes.Report.HardFailed.ToString(), res.Report.HardFailed.ToString(),
                    res.Report.HardFailed <= baseRes.Report.HardFailed);
                CmpRow(cmp.transform, "总花费",
                    "¥" + baseRes.Metrics.Cost.ToString("N0", CultureInfo.InvariantCulture),
                    "¥" + res.Metrics.Cost.ToString("N0", CultureInfo.InvariantCulture),
                    res.Metrics.Cost <= baseRes.Metrics.Cost);
                CmpRow(cmp.transform, "景点数",
                    baseRes.Metrics.Stops.ToString(), res.Metrics.Stops.ToString(), true);
            }
        }

        if (mode == "single_pass" && res.Profile != null)
        {
            RunResult loopRes = alt;
            if (loopRes != null)
            {
                Section(t, "如果跑一遍闭环", "同样参数，加上校验与修复");
                var cmp2 = Card(t, Surface, 26, 0);
                cmp2.GetComponent<VerticalLayoutGroup>().spacing = 8;
                cmp2.GetComponent<VerticalLayoutGroup>().padding = new RectOffset(24, 24, 18, 18);
                CmpRow(cmp2.transform, "约束满足率",
                    (res.Report.Csr * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%",
                    (loopRes.Report.Csr * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%",
                    loopRes.Report.Csr >= res.Report.Csr);
                CmpRow(cmp2.transform, "硬冲突",
                    res.Report.HardFailed.ToString(), loopRes.Report.HardFailed.ToString(),
                    loopRes.Report.HardFailed <= res.Report.HardFailed);
                CmpRow(cmp2.transform, "总花费",
                    "¥" + res.Metrics.Cost.ToString("N0", CultureInfo.InvariantCulture),
                    "¥" + loopRes.Metrics.Cost.ToString("N0", CultureInfo.InvariantCulture),
                    loopRes.Metrics.Cost <= res.Metrics.Cost);
                CmpRow(cmp2.transform, "景点数",
                    res.Metrics.Stops.ToString(), loopRes.Metrics.Stops.ToString(), true);
                Lbl(cmp2.transform, string.Format(CultureInfo.InvariantCulture,
                    "代价：{0} 轮修复、工具调用 {1} 次（一次性 {2} 次）",
                    loopRes.Metrics.RepairRounds, loopRes.Metrics.ToolCalls, res.Metrics.ToolCalls),
                    20, Ink3, TextAnchor.MiddleLeft, 40);
            }
        }

        if (mode == "single_pass")
        {
            var cta = Card(t, BrandSoft, 24, 0);
            cta.GetComponent<VerticalLayoutGroup>().padding = new RectOffset(24, 24, 20, 22);
            Lbl(cta.transform, "这是一次性生成：只排顺序，没有校验", 24, BrandDeep, TextAnchor.MiddleLeft, 36, true);
            Lbl(cta.transform, "闭环模式会把上面的冲突逐条修掉，再看还剩什么——这是本项目要对比的差距。", 21,
                BrandDeep, TextAnchor.MiddleLeft, 60).horizontalOverflow = HorizontalWrapMode.Wrap;
            var ctaBtn = Btn(cta.transform, "用闭环模式重排一次 →", 24, Brand, Color.white, 76);
            ctaBtn.onClick.AddListener(() => Generate("closed_loop"));
        }

        // 冲突
        if (rep.Violations.Count > 0)
        {
            Section(t, "冲突与提醒", rep.Violations.Count + " 条");
            var vCard = Card(t, Surface, 26, 0);
            vCard.GetComponent<VerticalLayoutGroup>().spacing = 14;
            foreach (var v in rep.Violations)
            {
                var row = new GameObject("v", typeof(RectTransform), typeof(LayoutElement), typeof(HorizontalLayoutGroup));
                row.transform.SetParent(vCard.transform, false);
                var rh = row.GetComponent<HorizontalLayoutGroup>();
                rh.spacing = 12;
                rh.childAlignment = TextAnchor.UpperLeft;
                rh.childControlWidth = true; rh.childControlHeight = true;
                rh.childForceExpandWidth = false; rh.childForceExpandHeight = false;
                bool hard = v.Severity == "hard";
                var dotWrap = new GameObject("dw", typeof(RectTransform), typeof(LayoutElement));
                dotWrap.transform.SetParent(row.transform, false);
                dotWrap.GetComponent<LayoutElement>().preferredWidth = 16;
                dotWrap.GetComponent<LayoutElement>().preferredHeight = 16;
                var dot = Box(dotWrap.transform, "dot", hard ? Bad : Warn, 8);
                var dle = dot.gameObject.AddComponent<LayoutElement>();
                dle.ignoreLayout = true;
                dot.rectTransform.anchorMin = new Vector2(0, 1);
                dot.rectTransform.anchorMax = new Vector2(0, 1);
                dot.rectTransform.pivot = new Vector2(0, 1);
                dot.rectTransform.sizeDelta = new Vector2(14, 14);
                dot.rectTransform.anchoredPosition = new Vector2(0, -4);

                var txt = Lbl(row.transform, "第 " + ((v.Day ?? 0) + 1) + " 天　" + v.Message, 22, hard ? Ink : Ink2, TextAnchor.UpperLeft, 0);
                txt.GetComponent<LayoutElement>().flexibleWidth = 1;
                txt.horizontalOverflow = HorizontalWrapMode.Wrap;
            }
        }

        // 逐日行程
        // 修复轨迹
        if (res.Trace != null && res.Trace.Count > 1)
        {
            Section(t, "修复轨迹", res.Trace.Count + " 步");
            var trCard = Card(t, Surface, 26, 0);
            trCard.GetComponent<VerticalLayoutGroup>().spacing = 6;
            trCard.GetComponent<VerticalLayoutGroup>().padding = new RectOffset(22, 22, 16, 16);
            int showN = Math.Min(res.Trace.Count, 8);
            for (int ti = 0; ti < showN; ti++)
            {
                var rowT = res.Trace[ti];
                int rd = Convert.ToInt32(rowT["round"]);
                string act = Convert.ToString(rowT["action"]);
                int hardT = Convert.ToInt32(rowT["hard"]);
                double csrT = Convert.ToDouble(rowT["csr"]);

                var line = new GameObject("tr" + ti, typeof(RectTransform), typeof(LayoutElement), typeof(HorizontalLayoutGroup));
                line.transform.SetParent(trCard.transform, false);
                line.GetComponent<LayoutElement>().preferredHeight = 52;
                var lh = line.GetComponent<HorizontalLayoutGroup>();
                lh.spacing = 10; lh.childAlignment = TextAnchor.MiddleLeft;
                lh.childControlWidth = true; lh.childControlHeight = true;
                lh.childForceExpandWidth = false; lh.childForceExpandHeight = false;

                var pillT = Box(line.transform, "rp", rd == 0 ? ChipBg : BrandSoft, 16);
                var pleT = pillT.gameObject.AddComponent<LayoutElement>();
                pleT.preferredWidth = 108; pleT.preferredHeight = 40;
                var pl = Lbl(pillT.transform, rd == 0 ? "初稿" : "第 " + rd + " 轮", 19, rd == 0 ? Ink2 : BrandDeep, TextAnchor.MiddleCenter, 0);
                Stretch(pl.rectTransform);
                Destroy(pl.GetComponent<LayoutElement>());

                var at = Lbl(line.transform, act, 21, Ink2, TextAnchor.MiddleLeft, 52);
                at.GetComponent<LayoutElement>().flexibleWidth = 1;
                at.horizontalOverflow = HorizontalWrapMode.Wrap;

                double prevCsr = ti > 0 ? Convert.ToDouble(res.Trace[ti - 1]["csr"]) : csrT;
                double dCsr = (csrT - prevCsr) * 100;
                string csrText = (csrT * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";
                if (ti > 0 && Math.Abs(dCsr) >= 0.05)
                    csrText += (dCsr > 0 ? "  +" : "  −") + Math.Abs(dCsr).ToString("0.0", CultureInfo.InvariantCulture);
                var ct = Lbl(line.transform, csrText, 21,
                    csrT >= 0.999 ? Brand : (hardT > 0 ? Bad : Ink2), TextAnchor.MiddleRight, 52, true);
                ct.GetComponent<LayoutElement>().preferredWidth = 148;
                ct.horizontalOverflow = HorizontalWrapMode.Overflow;
            }
            if (res.Trace.Count > showN)
                Lbl(trCard.transform, "… 其余 " + (res.Trace.Count - showN) + " 步略", 20, Ink3, TextAnchor.MiddleLeft, 34);
        }

        dayCards.Clear();
        {
            var idxCard = Card(t, Surface, 26, 0);
            idxCard.GetComponent<VerticalLayoutGroup>().spacing = 10;
            Lbl(idxCard.transform, "按天跳转", 21, Ink3, TextAnchor.MiddleLeft, 30);
            var idxRow = new GameObject("idxrow", typeof(RectTransform), typeof(LayoutElement), typeof(HorizontalLayoutGroup));
            idxRow.transform.SetParent(idxCard.transform, false);
            idxRow.GetComponent<LayoutElement>().preferredHeight = 66;
            var irh = idxRow.GetComponent<HorizontalLayoutGroup>();
            irh.spacing = 8; irh.childAlignment = TextAnchor.MiddleLeft;
            irh.childControlWidth = true; irh.childControlHeight = true;
            irh.childForceExpandWidth = false; irh.childForceExpandHeight = false;
            for (int k = 0; k < res.Itinerary.Days.Count; k++)
            {
                int kk = k;
                var dyC = res.Itinerary.Days[k];
                var b = Btn(idxRow.transform, "D" + (k + 1) + "　" + Data.CityName(dyC.City), 20, ChipBg, Ink2, 60);
                var blx = b.GetComponent<LayoutElement>();
                if (blx == null) blx = b.gameObject.AddComponent<LayoutElement>();
                blx.preferredWidth = 150;
                b.onClick.AddListener(() => JumpDay(kk));
            }
        }
        Section(t, "逐日行程", res.Itinerary.Days.Count + " 天 · " + picked.Count + " 城");
        int di = 0;
        if (!dayStateInit)
        {
            dayStateInit = true;
            for (int od = 0; od < 3 && od < res.Itinerary.Days.Count; od++) openDays.Add(od);
        }
        foreach (var day in res.Itinerary.Days)
        {
            var card = Card(t, Surface, 26, 0);
            card.GetComponent<VerticalLayoutGroup>().spacing = 12;
            dayCards.Add(card.GetComponent<RectTransform>());

            var head = new GameObject("dh", typeof(RectTransform), typeof(LayoutElement), typeof(HorizontalLayoutGroup));
            head.transform.SetParent(card.transform, false);
            head.GetComponent<LayoutElement>().preferredHeight = 52;
            var hh = head.GetComponent<HorizontalLayoutGroup>();
            hh.spacing = 12; hh.childAlignment = TextAnchor.MiddleLeft;
            hh.childControlWidth = true; hh.childControlHeight = true;
            hh.childForceExpandWidth = false; hh.childForceExpandHeight = false;

            var dPill = Box(head.transform, "dp", BrandSoft, 20);
            var dple = dPill.gameObject.AddComponent<LayoutElement>();
            dple.preferredWidth = 122; dple.preferredHeight = 46;
            var dpl = Lbl(dPill.transform, "第 " + (di + 1) + " 天", 21, BrandDeep, TextAnchor.MiddleCenter, 0, true);
            Stretch(dpl.rectTransform);
            Destroy(dpl.GetComponent<LayoutElement>());

            DateTime dayDate = resItineraryDate(res, di);
            string headText = Data.CityName(day.City) + "　" + dayDate.ToString("MM-dd", CultureInfo.InvariantCulture)
                              + " " + WeekCn(dayDate.DayOfWeek);
            int dayRest = RestMinutes(day, res);
            if (dayRest > 0) headText += "　休息 " + dayRest + " 分钟";
            var cityLab = Lbl(head.transform, headText, 25, Ink, TextAnchor.MiddleLeft, 52, true);
            cityLab.GetComponent<LayoutElement>().flexibleWidth = 1;
            cityLab.horizontalOverflow = HorizontalWrapMode.Wrap;

            if (day.Weather != null && !string.IsNullOrEmpty(day.Weather.Text))
            {
                var wtxt = Lbl(head.transform, string.Format(CultureInfo.InvariantCulture, "{0} {1}°~{2}° {3}%",
                    day.Weather.Text, day.Weather.Low, day.Weather.High, day.Weather.Rain), 20, Ink3, TextAnchor.MiddleRight, 52);
                wtxt.GetComponent<LayoutElement>().preferredWidth = 210;
            }

            int dayRide = 0, dayFee = 0;
            foreach (var s2 in day.Stops)
            {
                dayRide += s2.FromPrev;
                Poi p2;
                if (Data.PoiById.TryGetValue(s2.PoiId, out p2) && p2 != null) dayFee += p2.Price;
            }
            if (day.Leg != null && !day.Leg.IsEmpty) dayRide += day.Leg.Minutes;
            string daySum = string.Format(CultureInfo.InvariantCulture,
                "{0} 处景点 · 门票 ¥{1} · 路上 {2}", day.Stops.Count, dayFee,
                dayRide >= 60 ? (dayRide / 60) + " 小时 " + (dayRide % 60) + " 分" : dayRide + " 分钟");
            if (day.Stops.Count > 0 && dayRide >= 120)
                daySum += "　⚠ 这天大半在路上";
            Lbl(card.transform, daySum, 19, dayRide >= 120 ? Warn : Ink3, TextAnchor.MiddleLeft, 30);

            var body = new GameObject("dbody", typeof(RectTransform), typeof(LayoutElement), typeof(VerticalLayoutGroup));
            body.transform.SetParent(card.transform, false);
            var bvg = body.GetComponent<VerticalLayoutGroup>();
            bvg.spacing = 12;
            bvg.childControlWidth = true; bvg.childControlHeight = true;
            bvg.childForceExpandWidth = true; bvg.childForceExpandHeight = false;

            var headImg = head.AddComponent<Image>();
            headImg.color = new Color(1, 1, 1, 0);
            var caret = Lbl(head.transform, di < 3 ? "▾" : "▸", 24, Ink3, TextAnchor.MiddleCenter, 40);
            caret.GetComponent<LayoutElement>().preferredWidth = 34;
            bool opened = openDays.Contains(di);
            body.SetActive(opened);
            caret.text = opened ? "▾" : "▸";
            int dayIdx = di;
            var hbtn = head.AddComponent<Button>();
            hbtn.onClick.AddListener(() =>
            {
                bool now = !body.activeSelf;
                body.SetActive(now);
                caret.text = now ? "▾" : "▸";
                if (now) openDays.Add(dayIdx); else openDays.Remove(dayIdx);
            });

            if (day.Leg != null && !day.Leg.IsEmpty)
            {
                var leg = Box(body.transform, "leg", BrandSoft, 16);
                var lv = leg.gameObject.AddComponent<VerticalLayoutGroup>();
                lv.padding = new RectOffset(18, 18, 8, 8);
                lv.spacing = 2;
                lv.childControlWidth = true; lv.childControlHeight = true;
                lv.childForceExpandWidth = true; lv.childForceExpandHeight = false;
                var lle = leg.gameObject.AddComponent<LayoutElement>();
                lle.preferredHeight = 92;
                var lt1 = Lbl(leg.transform, string.IsNullOrEmpty(day.Leg.Code)
                    ? string.Format(CultureInfo.InvariantCulture, "🚄  {0} → {1}", day.Leg.FromName, day.Leg.ToName)
                    : string.Format(CultureInfo.InvariantCulture, "🚄  {0}　{1} {2} → {3} {4}",
                        day.Leg.Code, day.Leg.DepStation, Rail.Fmt(day.Leg.Dep),
                        day.Leg.ArrStation, Rail.Fmt(day.Leg.Arr)),
                    20, BrandDeep, TextAnchor.MiddleLeft, 34);
                lt1.horizontalOverflow = HorizontalWrapMode.Wrap;
                var lt2 = Lbl(leg.transform, string.Format(CultureInfo.InvariantCulture,
                    "历时 {0} 分钟 · ¥{1} · 约 {2:0} km", day.Leg.Run, day.Leg.Fare, day.Leg.Km),
                    19, Ink2, TextAnchor.MiddleLeft, 30);
                lt2.horizontalOverflow = HorizontalWrapMode.Wrap;
            }

            if (day.Stops.Count == 0)
                Lbl(body.transform, "当天没有可安排的景点（城际交通占满或全部闭馆）", 21, Ink3, TextAnchor.MiddleLeft, 40);

            foreach (var s in day.Stops)
            {
                Poi poi;
                Data.PoiById.TryGetValue(s.PoiId, out poi);
                string name = poi != null ? poi.Name : s.PoiId;
                string ticket = poi != null ? (poi.Price == 0 ? "免费" : "¥" + poi.Price) : "";
                if (s.Via != null && s.Via.Minutes > 0)
                {
                    string em = "🚇 ";
                    if (s.Via.Segments.Count > 1) em = "🚕🚇 ";
                    else if (s.Via.Segments.Count > 0 && s.Via.Segments[0].Mode == "walk") em = "🚶 ";
                    else if (s.Via.Segments.Count > 0 && s.Via.Segments[0].Mode == "taxi") em = "🚕 ";
                    var legRow = Lbl(body.transform, "　" + em + s.Via.Headline, 19, Ink_alt(0x9AA4B2),
                                     TextAnchor.MiddleLeft, 30);
                    legRow.horizontalOverflow = HorizontalWrapMode.Wrap;
                    if (!string.IsNullOrEmpty(s.Via.Why))
                    {
                        var whyRow = Lbl(body.transform, "　　" + s.Via.Why, 17, Ink3, TextAnchor.MiddleLeft, 30);
                        whyRow.horizontalOverflow = HorizontalWrapMode.Wrap;
                    }
                }
                else if (s.FromPrev > 0)
                {
                    var legRow = Lbl(body.transform, "　" + s.FromPrev + " 分钟", 19, Ink_alt(0x9AA4B2),
                                     TextAnchor.MiddleLeft, 30);
                    legRow.horizontalOverflow = HorizontalWrapMode.Wrap;
                }
                var row = new GameObject("s", typeof(RectTransform), typeof(LayoutElement), typeof(HorizontalLayoutGroup));
                row.transform.SetParent(body.transform, false);
                row.GetComponent<LayoutElement>().preferredHeight = 54;
                var rh = row.GetComponent<HorizontalLayoutGroup>();
                rh.spacing = 12; rh.childAlignment = TextAnchor.MiddleLeft;
                rh.childControlWidth = true; rh.childControlHeight = true;
                rh.childForceExpandWidth = false; rh.childForceExpandHeight = false;

                var time = Lbl(row.transform, Fmt(s.Arrive) + "–" + Fmt(s.Depart), 21, Ink_alt(0x8A94A6), TextAnchor.MiddleLeft, 54);
                time.GetComponent<LayoutElement>().preferredWidth = 150;

                var nameLab = Lbl(row.transform, name, 24, Ink, TextAnchor.MiddleLeft, 54);
                nameLab.GetComponent<LayoutElement>().flexibleWidth = 1;
                nameLab.horizontalOverflow = HorizontalWrapMode.Wrap;

                var tk = Lbl(row.transform, ticket, 20, Ink3, TextAnchor.MiddleRight, 54);
                tk.GetComponent<LayoutElement>().preferredWidth = 90;
            }
            di++;
        }

        // 底部操作
        var backBtn = Btn(t, "← 返回修改", 27, ChipBg, Ink, 96);
        backBtn.onClick.AddListener(() => Show(homePanel));
        var other = Btn(t, mode == "closed_loop" ? "对比：一次性生成" : "对比：闭环规划", 27, Brand, Color.white, 96);
        other.onClick.AddListener(() => Generate(mode == "closed_loop" ? "single_pass" : "closed_loop"));
        var shareBtn = Btn(t, "分享给好友（微信 / QQ）", 26, BrandSoft, BrandDeep, 92);
        shareBtn.onClick.AddListener(() => DoShare(shown));
        var saveShot = Btn(t, "保存行程图到相册", 24, new Color(1, 1, 1, 0), Ink3, 72);
        saveShot.onClick.AddListener(SaveShot);
        var copyBtn = Btn(t, "复制行程文本", 24, new Color(1, 1, 1, 0), Ink3, 72);
        copyBtn.onClick.AddListener(() =>
        {
            GUIUtility.systemCopyBuffer = BuildShareText(shown);
            Toast("已复制，粘贴到微信即可发送");
        });

        if (csrLab != null && frt != null)
        {
            if (csrCo != null) StopCoroutine(csrCo);
            csrCo = StartCoroutine(CountUp(csrLab, (float)rep.Csr, frt));
        }
    }

    IEnumerator CountUp(Text label, float target, RectTransform fill)
    {
        float t = 0f;
        const float dur = 0.75f;
        while (t < dur)
        {
            t += Time.unscaledDeltaTime;
            float k = Mathf.Clamp01(t / dur);
            k = 1f - (1f - k) * (1f - k);
            float v = target * k;
            if (label != null) label.text = (v * 100f).ToString("0.0", CultureInfo.InvariantCulture) + "%";
            if (fill != null) fill.anchorMax = new Vector2(Mathf.Clamp01(v), 1f);
            yield return null;
        }
        if (label != null) label.text = (target * 100f).ToString("0.0", CultureInfo.InvariantCulture) + "%";
        if (fill != null) fill.anchorMax = new Vector2(Mathf.Clamp01(target), 1f);
    }

    static Color Ink_alt(int rgb) => Hex(rgb);

    void Stat(Transform parent, string value, string label, Color vcolor)
    {
        var card = Card(parent, Surface, 24, 0);
        card.GetComponent<VerticalLayoutGroup>().padding = new RectOffset(18, 18, 18, 18);
        card.GetComponent<VerticalLayoutGroup>().childAlignment = TextAnchor.MiddleCenter;
        var v = Lbl(card.transform, value, 42, vcolor, TextAnchor.MiddleCenter, 54, true);
        v.horizontalOverflow = HorizontalWrapMode.Overflow;
        Lbl(card.transform, label, 21, Ink3, TextAnchor.MiddleCenter, 30);
    }

    static DateTime resItineraryDate(RunResult res, int i)
    {
        var p = res.Profile;
        if (p != null)
        {
            try { return DateTime.ParseExact(p.DateOf(i), "yyyy-MM-dd", CultureInfo.InvariantCulture); }
            catch { }
        }
        return DateTime.Today.AddDays(i);
    }

    static string Fmt(int minutes)
    {
        return (minutes / 60).ToString("00", CultureInfo.InvariantCulture) + ":" +
               (minutes % 60).ToString("00", CultureInfo.InvariantCulture);
    }

    void Show(GameObject panel)
    {
        bool toHome = panel == homePanel;
        homePanel.SetActive(toHome);
        resultPanel.SetActive(!toHome);
        if (!isActiveAndEnabled) return;
        var cg = toHome ? homeCg : resultCg;
        if (cg != null) StartCoroutine(FadeIn(cg));
    }

    IEnumerator FadeIn(CanvasGroup cg)
    {
        cg.alpha = 0f;
        float t = 0f;
        const float dur = 0.18f;
        while (t < dur)
        {
            t += Time.unscaledDeltaTime;
            cg.alpha = Mathf.Clamp01(t / dur);
            yield return null;
        }
        cg.alpha = 1f;
    }

    void CmpRow(Transform parent, string label, string before, string after, bool good)
    {
        var row = new GameObject("cmp", typeof(RectTransform), typeof(LayoutElement), typeof(HorizontalLayoutGroup));
        row.transform.SetParent(parent, false);
        row.GetComponent<LayoutElement>().preferredHeight = 48;
        var h = row.GetComponent<HorizontalLayoutGroup>();
        h.spacing = 8; h.childAlignment = TextAnchor.MiddleLeft;
        h.childControlWidth = true; h.childControlHeight = true;
        h.childForceExpandWidth = false; h.childForceExpandHeight = false;
        var lb = Lbl(row.transform, label, 22, Ink2, TextAnchor.MiddleLeft, 48);
        lb.GetComponent<LayoutElement>().flexibleWidth = 1;
        var b1 = Lbl(row.transform, before, 22, Ink3, TextAnchor.MiddleRight, 48);
        b1.GetComponent<LayoutElement>().preferredWidth = 110;
        var ar = Lbl(row.transform, "→", 22, Ink3, TextAnchor.MiddleCenter, 48);
        ar.GetComponent<LayoutElement>().preferredWidth = 40;
        var b2 = Lbl(row.transform, after, 22, good ? Brand : Bad, TextAnchor.MiddleRight, 48, true);
        b2.GetComponent<LayoutElement>().preferredWidth = 110;
    }

    void JumpDay(int idx)
    {
        if (resultScroll == null || idx < 0 || idx >= dayCards.Count) return;
        var content = resultScroll.content;
        LayoutRebuilder.ForceRebuildLayoutImmediate(content);
        float contentH = content.rect.height;
        float viewH = resultScroll.viewport.rect.height;
        if (contentH <= viewH + 1f) return;
        float yTop = -dayCards[idx].anchoredPosition.y - 46f;
        float norm = 1f - Mathf.Clamp01(yTop / (contentH - viewH));
        resultScroll.verticalNormalizedPosition = Mathf.Clamp01(norm);
        Toast("跳到第 " + (idx + 1) + " 天");
    }

    string BuildShareText(RunResult res)
    {
        if (res == null) return "";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("拱程 · 行程单");
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "约束满足率 {0:0.0}% · {1} 个景点 · 硬冲突 {2} · 软提醒 {3} · 总花费 ¥{4}",
            res.Report.Csr * 100, res.Metrics.Stops, res.Report.HardFailed, res.Report.SoftFailed, res.Metrics.Cost));
        {
            var seq = new List<string>();
            var seqDays = new List<int>();
            foreach (var dy in res.Itinerary.Days)
            {
                string cn = Data.CityName(dy.City);
                int last = seq.Count - 1;
                if (last >= 0 && seq[last] == cn) seqDays[last]++;
                else { seq.Add(cn); seqDays.Add(1); }
            }
            string chain = "";
            for (int ci = 0; ci < seq.Count; ci++)
            {
                if (ci > 0) chain += " → ";
                chain += seq[ci] + " " + seqDays[ci] + " 天";
            }
            sb.AppendLine("路线：" + chain);
        }
        int dno = 0;
        foreach (var day in res.Itinerary.Days)
        {
            dno++;
            string w = day.Weather == null ? "" : string.Format(CultureInfo.InvariantCulture,
                "　{0} {1}~{2}℃ 降雨{3}%", day.Weather.Text, day.Weather.Low, day.Weather.High, day.Weather.Rain);
            var ddt = resItineraryDate(res, dno - 1);
            sb.AppendLine();
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "第 {0} 天 · {1} · {2} 周{3}{4}{5}",
                dno, Data.CityName(day.City), ddt.ToString("MM-dd", CultureInfo.InvariantCulture),
                "一二三四五六日"[((int)ddt.DayOfWeek + 6) % 7], w, RestMinutes(day, res) > 0 ? "　休息 " + RestMinutes(day, res) + " 分钟" : ""));
            if (day.Leg != null && !day.Leg.IsEmpty)
                sb.AppendLine(string.IsNullOrEmpty(day.Leg.Code)
                    ? string.Format(CultureInfo.InvariantCulture, "  🚄 {0} → {1} {2} 分钟 · ¥{3}",
                        day.Leg.FromName, day.Leg.ToName, day.Leg.Minutes, day.Leg.Fare)
                    : string.Format(CultureInfo.InvariantCulture, "  🚄 {0} {1} {2} → {3} {4} · ¥{5}",
                        day.Leg.Code, day.Leg.DepStation, Rail.Fmt(day.Leg.Dep),
                        day.Leg.ArrStation, Rail.Fmt(day.Leg.Arr), day.Leg.Fare));
            foreach (var st in day.Stops)
            {
                if (st.Via != null && st.Via.Minutes > 0)
                    sb.AppendLine("  （" + st.Via.Headline + "）");
                else if (st.FromPrev > 0)
                    sb.AppendLine("  （" + st.FromPrev + " 分钟）");
                Poi poiRow;
                Data.PoiById.TryGetValue(st.PoiId, out poiRow);
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  {0}-{1}　{2}{3}",
                    Verifier.Fmt(st.Arrive), Verifier.Fmt(st.Depart),
                    poiRow == null ? st.PoiId : poiRow.Name,
                    poiRow != null && poiRow.Price > 0 ? "　¥" + poiRow.Price : ""));
            }
            int rideSum = 0, feeSum = 0;
            foreach (var st2 in day.Stops)
            {
                rideSum += st2.FromPrev;
                Poi pz;
                if (Data.PoiById.TryGetValue(st2.PoiId, out pz) && pz != null) feeSum += pz.Price;
            }
            if (day.Leg != null && !day.Leg.IsEmpty) rideSum += day.Leg.Minutes;
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  小计：{0} 处 · 门票 ¥{1} · 路上 {2}",
                day.Stops.Count, feeSum,
                rideSum >= 60 ? (rideSum / 60) + " 小时 " + (rideSum % 60) + " 分" : rideSum + " 分钟"));
        }
        var olegS = res.Itinerary.OriginLeg;
        var rlegS = res.Itinerary.ReturnLeg;
        if (olegS != null && !string.IsNullOrEmpty(olegS.Code))
        {
            sb.AppendLine();
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "去程：{0} {1} {2} → {3} {4}（¥{5}）",
                olegS.Code, olegS.DepStation, Rail.Fmt(olegS.Dep), olegS.ArrStation, Rail.Fmt(olegS.Arr), olegS.Fare));
            if (rlegS != null && !string.IsNullOrEmpty(rlegS.Code))
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "返程：{0} {1} {2} → {3} {4}（¥{5}）",
                    rlegS.Code, rlegS.DepStation, Rail.Fmt(rlegS.Dep), rlegS.ArrStation, Rail.Fmt(rlegS.Arr), rlegS.Fare));
        }
        sb.AppendLine();
        sb.AppendLine("由「拱程」生成 · 景点信息来自公开页面整理，开放时间与票价请以现场为准");
        return sb.ToString();
    }

    void DoShare(RunResult res)
    {
        if (res == null) return;
        string text = BuildShareText(res);
        if (Share.SendText("拱程 · 行程单", text))
        {
            Toast("选微信即可转发给好友");
            return;
        }
        GUIUtility.systemCopyBuffer = text;
        Toast("已复制行程文本，粘贴到微信即可发送");
    }

    void SaveShot()
    {
        string name = "拱程_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".png";
        StartCoroutine(SaveShotRoutine(name));
    }

    IEnumerator SaveShotRoutine(string name)
    {
        string path = Path.Combine(Application.persistentDataPath, name);
        ScreenCapture.CaptureScreenshot(path);
        yield return new WaitForEndOfFrame();
        yield return new WaitForSecondsRealtime(0.5f);
        if (Share.SaveImageToGallery(path, name))
            Toast("已存到相册：" + name + "，可在微信里直接发图");
        else
            Toast("已保存：" + name);
    }

    void SanitizePicked()
    {
        for (int i = picked.Count - 1; i >= 0; i--)
        {
            if (!cityKeys.Contains(picked[i]))
            {
                picked.RemoveAt(i);
                if (i < pickedDays.Count) pickedDays.RemoveAt(i);
            }
        }
        while (pickedDays.Count < picked.Count) pickedDays.Add(2);
        while (pickedDays.Count > picked.Count) pickedDays.RemoveAt(pickedDays.Count - 1);
        for (int i = 0; i < pickedDays.Count; i++) pickedDays[i] = Mathf.Clamp(pickedDays[i], 1, 7);
        if (picked.Count == 0)
        {
            picked.Add("luoyang");
            pickedDays.Add(3);
        }
    }

    // ---------- 状态持久化 ----------
    void SaveState()
    {
        PlayerPrefs.SetInt("days", days);
        PlayerPrefs.SetInt("budget", budget);
        PlayerPrefs.SetInt("startOffset", startOffset);
        PlayerPrefs.SetString("party", party);
        PlayerPrefs.SetString("pace", pace);
        PlayerPrefs.SetString("transport", transport);
        PlayerPrefs.SetString("originKey", originKey);
        PlayerPrefs.SetString("originName", originName);
        PlayerPrefs.SetString("prefs", string.Join("|", prefs));
        PlayerPrefs.SetString("picked", string.Join("|", picked));
        PlayerPrefs.SetString("pickedDays", string.Join("|", pickedDays));
        PlayerPrefs.SetInt("onlineWeather", onlineWeather ? 1 : 0);
        PlayerPrefs.Save();
    }

    void LoadState()
    {
        days = Mathf.Clamp(PlayerPrefs.GetInt("days", 3), 1, 12);
        budget = Mathf.Clamp(PlayerPrefs.GetInt("budget", 3000), 300, 20000);
        startOffset = Mathf.Clamp(PlayerPrefs.GetInt("startOffset", 0), -90, 15);
        string pt = PlayerPrefs.GetString("party", "情侣");
        if (!string.IsNullOrEmpty(pt)) party = pt;
        string pc = PlayerPrefs.GetString("pace", "normal");
        if (!string.IsNullOrEmpty(pc)) pace = pc;
        string ptr = PlayerPrefs.GetString("transport", "auto");
        if (!string.IsNullOrEmpty(ptr)) transport = ptr;
        originKey = PlayerPrefs.GetString("originKey", "");
        originName = PlayerPrefs.GetString("originName", "");
        string pf = PlayerPrefs.GetString("prefs", "");
        if (!string.IsNullOrEmpty(pf))
        {
            prefs.Clear();
            foreach (var x in pf.Split('|')) if (x.Length > 0) prefs.Add(x);
        }
        lastSummary = PlayerPrefs.GetString("lastSummary", "");
        string pk = PlayerPrefs.GetString("picked", "");
        if (!string.IsNullOrEmpty(pk))
        {
            picked.Clear();
            foreach (var x in pk.Split('|')) if (x.Length > 0 && picked.Count < 3) picked.Add(x);
        }
        string pd = PlayerPrefs.GetString("pickedDays", "");
        pickedDays.Clear();
        if (!string.IsNullOrEmpty(pd))
        {
            foreach (var x in pd.Split('|'))
            {
                int v;
                if (int.TryParse(x, out v) && pickedDays.Count < picked.Count) pickedDays.Add(Mathf.Clamp(v, 1, 7));
            }
        }
        while (pickedDays.Count < picked.Count) pickedDays.Add(2);
        onlineWeather = PlayerPrefs.GetInt("onlineWeather", 1) == 1;
    }

    void OnApplicationPause(bool paused)
    {
        if (paused) SaveState();
    }

    void OnApplicationQuit()
    {
        SaveState();
    }

    void Update()
    {
        UpdateSafeArea();
    }

    void UpdateSafeArea()
    {
        if (safeRoot == null || Screen.width <= 0 || Screen.height <= 0) return;
        var sa = Screen.safeArea;
        Vector2 min = sa.position, max = sa.position + sa.size;
        min.x /= Screen.width; min.y /= Screen.height;
        max.x /= Screen.width; max.y /= Screen.height;
        safeRoot.anchorMin = min;
        safeRoot.anchorMax = max;
        safeRoot.offsetMin = Vector2.zero;
        safeRoot.offsetMax = Vector2.zero;
    }

    CanvasGroup toastCg;
    int toastSeq;

    void Toast(string msg)
    {
        if (canvas == null) return;
        if (toastText == null)
        {
            var box = Box(safeRoot, "toast", Alpha(Hex(0x101828), 0.94f), 20);
            var le = box.gameObject.AddComponent<LayoutElement>();
            le.ignoreLayout = true;
            var rt = box.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = new Vector2(0, 440);
            rt.sizeDelta = new Vector2(420, 84);
            toastText = Lbl(box.transform, "", 24, Color.white, TextAnchor.MiddleCenter, 84);
            toastText.horizontalOverflow = HorizontalWrapMode.Overflow;
            toastCg = box.gameObject.AddComponent<CanvasGroup>();
            toastCg.alpha = 0f;
            toastCg.blocksRaycasts = false;
        }
        toastText.text = msg;
        float wantW = Mathf.Clamp(toastText.preferredWidth + 76f, 240f, 640f);
        var trt = toastText.transform.parent.GetComponent<RectTransform>();
        trt.sizeDelta = new Vector2(wantW, 84);
        toastSeq++;
        StartCoroutine(ToastFade(toastSeq));
    }

    IEnumerator ToastFade(int seq)
    {
        toastCg.alpha = 1f;
        yield return new WaitForSecondsRealtime(1.6f);
        while (toastCg.alpha > 0f && seq == toastSeq)
        {
            toastCg.alpha = Mathf.Max(0f, toastCg.alpha - Time.unscaledDeltaTime * 3.6f);
            yield return null;
        }
        if (seq == toastSeq) toastCg.alpha = 0f;
    }

    // ---------- uGUI 小工具 ----------
    void BuildCanvas()
    {
        var go = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        go.transform.SetParent(transform, false);
        canvas = go.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = go.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1080, 1920);
        scaler.matchWidthOrHeight = 0.5f;

        var safe = new GameObject("SafeArea", typeof(RectTransform));
        safe.transform.SetParent(go.transform, false);
        safeRoot = safe.GetComponent<RectTransform>();
        safeRoot.anchorMin = Vector2.zero;
        safeRoot.anchorMax = Vector2.one;
        safeRoot.offsetMin = Vector2.zero;
        safeRoot.offsetMax = Vector2.zero;
        UpdateSafeArea();

        if (FindObjectOfType<EventSystem>() == null)
        {
            var es = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            es.transform.SetParent(transform, false);
        }

        var cam = Camera.main;
        if (cam == null)
        {
            var cgo = new GameObject("Camera", typeof(Camera));
            cgo.transform.SetParent(transform, false);
            var c = cgo.GetComponent<Camera>();
            c.clearFlags = CameraClearFlags.SolidColor;
            c.backgroundColor = Bg;
            c.orthographic = true;
            c.tag = "MainCamera";
        }
    }

    static Sprite Round(int radius)
    {
        int r = Mathf.Clamp(radius, 2, 31);
        Sprite cached;
        if (roundCache.TryGetValue(r, out cached)) return cached;
        const int size = 64;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;
        var px = new Color32[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float cx = x + 0.5f, cy = y + 0.5f;
                float a = 1f;
                if (cx < r && cy < r) a = Mathf.Clamp01(r - Mathf.Sqrt((r - cx) * (r - cx) + (r - cy) * (r - cy)));
                else if (cx > size - r && cy < r) a = Mathf.Clamp01(r - Mathf.Sqrt((cx - (size - r)) * (cx - (size - r)) + (r - cy) * (r - cy)));
                else if (cx < r && cy > size - r) a = Mathf.Clamp01(r - Mathf.Sqrt((r - cx) * (r - cx) + (cy - (size - r)) * (cy - (size - r))));
                else if (cx > size - r && cy > size - r) a = Mathf.Clamp01(r - Mathf.Sqrt((cx - (size - r)) * (cx - (size - r)) + (cy - (size - r)) * (cy - (size - r))));
                px[y * size + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(a) * 255));
            }
        }
        tex.SetPixels32(px);
        tex.Apply();
        var sp = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0,
            SpriteMeshType.FullRect, new Vector4(r, r, r, r));
        roundCache[r] = sp;
        return sp;
    }

    static GameObject Panel(Transform parent, string name, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        go.GetComponent<Image>().color = color;
        return go;
    }

    Image Box(Transform parent, string name, Color color, int radius = 24)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var img = go.GetComponent<Image>();
        img.sprite = Round(radius);
        img.type = Image.Type.Sliced;
        img.color = color;
        return img;
    }

    Image Card(Transform parent, Color color, int radius, float height)
    {
        var img = Box(parent, "card", color, radius);
        var sh = img.gameObject.AddComponent<Shadow>();
        sh.effectColor = new Color(0.06f, 0.09f, 0.13f, 0.055f);
        sh.effectDistance = new Vector2(0f, -4f);
        sh.useGraphicAlpha = true;
        var v = img.gameObject.AddComponent<VerticalLayoutGroup>();
        v.padding = new RectOffset(26, 26, 22, 24);
        v.spacing = 12;
        v.childControlWidth = true;
        v.childControlHeight = true;
        v.childForceExpandWidth = true;
        v.childForceExpandHeight = false;
        if (height > 0)
        {
            var le = img.gameObject.AddComponent<LayoutElement>();
            le.preferredHeight = height;
        }
        return img;
    }

    void HLine(Transform parent)
    {
        var img = Panel(parent, "line", Line);
        var le = img.AddComponent<LayoutElement>();
        le.minHeight = 2; le.preferredHeight = 2;
    }

    void Section(Transform parent, string title, string hint)
    {
        var row = new GameObject("sec", typeof(RectTransform), typeof(LayoutElement), typeof(HorizontalLayoutGroup));
        row.transform.SetParent(parent, false);
        row.GetComponent<LayoutElement>().preferredHeight = 46;
        var h = row.GetComponent<HorizontalLayoutGroup>();
        h.spacing = 8; h.childAlignment = TextAnchor.LowerLeft;
        h.childControlWidth = true; h.childControlHeight = true;
        h.childForceExpandWidth = false; h.childForceExpandHeight = false;
        var bar = Box(row.transform, "bar", Brand, 3);
        var ble = bar.gameObject.AddComponent<LayoutElement>();
        ble.preferredWidth = 7; ble.preferredHeight = 28;
        var tl = Lbl(row.transform, title, 27, Ink, TextAnchor.LowerLeft, 46, true);
        tl.GetComponent<LayoutElement>().flexibleWidth = 0;
        if (!string.IsNullOrEmpty(hint))
        {
            var hl = Lbl(row.transform, hint, 20, Ink3, TextAnchor.LowerRight, 46);
            hl.GetComponent<LayoutElement>().flexibleWidth = 1;
        }
    }

    static void Stretch(RectTransform rt, float padX = 0, float padY = 0)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(padX, padY);
        rt.offsetMax = new Vector2(-padX, -padY);
    }

    static GameObject VContent(Transform parent, RectOffset pad, float spacing)
    {
        var content = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        content.transform.SetParent(parent, false);
        var crt = content.GetComponent<RectTransform>();
        crt.anchorMin = new Vector2(0, 1);
        crt.anchorMax = new Vector2(1, 1);
        crt.pivot = new Vector2(0.5f, 1);
        crt.offsetMin = Vector2.zero;
        crt.offsetMax = Vector2.zero;
        var vlg = content.GetComponent<VerticalLayoutGroup>();
        vlg.padding = pad;
        vlg.spacing = spacing;
        vlg.childControlWidth = true;
        vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;
        content.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        return content;
    }

    Text Lbl(Transform parent, string text, int size, Color color, TextAnchor align,
             float height = 0, bool bold = false, bool wrap = false)
    {
        var go = new GameObject("txt", typeof(RectTransform), typeof(Text), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        var tx = go.GetComponent<Text>();
        tx.font = font;
        tx.text = text;
        tx.fontSize = size;
        tx.color = color;
        tx.alignment = align;
        tx.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
        tx.horizontalOverflow = wrap ? HorizontalWrapMode.Wrap : HorizontalWrapMode.Wrap;
        tx.verticalOverflow = VerticalWrapMode.Overflow;
        tx.supportRichText = false;
        tx.lineSpacing = 1.15f;
        var le = go.GetComponent<LayoutElement>();
        if (height > 0) { le.minHeight = height; le.preferredHeight = height; }
        return tx;
    }

    Button Btn(Transform parent, string text, int size, Color bg, Color fg, float height = 0)
    {
        var img = Box(parent, "btn", bg, 22);
        var b = img.gameObject.AddComponent<Button>();
        b.targetGraphic = img;
        var colors = b.colors;
        colors.highlightedColor = new Color(0.94f, 0.94f, 0.94f);
        colors.pressedColor = new Color(0.86f, 0.86f, 0.86f);
        b.colors = colors;
        var le = img.gameObject.AddComponent<LayoutElement>();
        if (height > 0) { le.minHeight = height; le.preferredHeight = height; }
        var t = Lbl(img.transform, text, size, fg, TextAnchor.MiddleCenter, 0);
        Stretch(t.rectTransform);
        Destroy(t.GetComponent<LayoutElement>());
        return b;
    }

    InputField SearchBox(Transform parent, UnityEngine.Events.UnityAction<string> onChange,
                         string placeholder = "搜索城市（如：洛阳 / 苏州 / 江西）")
    {
        var img = Box(parent, "search", Surface, 24);
        var le = img.gameObject.AddComponent<LayoutElement>();
        le.minHeight = 92; le.preferredHeight = 92;
        var f = img.gameObject.AddComponent<InputField>();

        var ph = Lbl(img.transform, placeholder, 24, Ink3, TextAnchor.MiddleLeft, 0);
        Stretch(ph.rectTransform, 28, 0);
        Destroy(ph.GetComponent<LayoutElement>());

        var txt = Lbl(img.transform, "", 26, Ink, TextAnchor.MiddleLeft, 0);
        Stretch(txt.rectTransform, 28, 0);
        Destroy(txt.GetComponent<LayoutElement>());
        txt.supportRichText = false;

        f.textComponent = txt;
        f.placeholder = ph;
        f.targetGraphic = img;
        f.lineType = InputField.LineType.SingleLine;
        f.characterLimit = 12;
        f.onValueChanged.AddListener(v => onChange(v));
        return f;
    }

    Text Stepper(Transform parent, string label, Action minus, Action plus)
    {
        var row = new GameObject("step_" + label, typeof(RectTransform), typeof(LayoutElement), typeof(HorizontalLayoutGroup));
        row.transform.SetParent(parent, false);
        row.GetComponent<LayoutElement>().preferredHeight = 96;
        var hlg = row.GetComponent<HorizontalLayoutGroup>();
        hlg.spacing = 14;
        hlg.childAlignment = TextAnchor.MiddleLeft;
        hlg.childControlWidth = true; hlg.childControlHeight = true;
        hlg.childForceExpandWidth = false; hlg.childForceExpandHeight = false;

        var lab = Lbl(row.transform, label, 26, Ink2, TextAnchor.MiddleLeft, 96);
        lab.GetComponent<LayoutElement>().preferredWidth = 130;
        lab.GetComponent<LayoutElement>().flexibleWidth = 0;

        var b1 = Btn(row.transform, "−", 34, ChipBg, Ink, 76);
        b1.GetComponent<LayoutElement>().preferredWidth = 84;
        b1.onClick.AddListener(() => minus());

        var val = Lbl(row.transform, "", 30, Ink, TextAnchor.MiddleCenter, 96, true);
        var vle = val.GetComponent<LayoutElement>();
        vle.preferredWidth = 260; vle.flexibleWidth = 1;

        var b2 = Btn(row.transform, "+", 34, ChipBg, Ink, 76);
        b2.GetComponent<LayoutElement>().preferredWidth = 84;
        b2.onClick.AddListener(() => plus());
        return val;
    }

    public class PrefTag : MonoBehaviour
    {
        public string Tag;
    }
}