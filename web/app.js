// 拱程 Web 演示：把 Python 引擎搬到浏览器里，纯静态、无后端。
import { Data, PREF_TAGS } from "./engine/data.js";
import { Profile, distribute } from "./engine/model.js";
import * as engine from "./engine/engine.js";
import { fmt, fmtDur } from "./engine/util.js";

const D = new Data();
const $ = (id) => document.getElementById(id);

const state = {
  ready: false,
  originKey: "",
  originName: "",
  picked: [],
  pickedDays: [],
  days: 3,
  budget: 3000,
  party: "情侣",
  pace: "normal",
  transport: "auto",
  prefs: new Set(["历史人文", "美食"]),
  startDate: "",
  online: true,
  cities: [],
  search: "",
};

const WEEK_CN = ["周一", "周二", "周三", "周四", "周五", "周六", "周日"];

const PRESETS = [
  {
    label: "洛阳 + 郑州 · 4 天",
    cities: ["luoyang", "zhengzhou"], cityDays: [2, 2], budget: 3000,
    prefs: ["历史人文", "美食"], transport: "auto", origin: "",
  },
  {
    label: "长沙出发 · 南昌九江景德镇 6 天",
    cities: ["nanchang", "jiujiang", "jingdezhen"], cityDays: [2, 2, 2], budget: 4500,
    prefs: ["历史人文", "美食"], transport: "auto", origin: "changsha",
  },
  {
    label: "成都 · 3 天（单城）",
    cities: ["chengdu"], cityDays: [3], budget: 3000,
    prefs: ["历史人文", "美食"], transport: "auto", origin: "",
  },
];

// ---------------- 启动 ----------------
async function boot() {
  initDates();
  bindControls();
  try {
    const dir = await fetch("agent/directory.json").then((r) => r.json());
    D.setDirectory(dir);
    state.cities = [];
    for (const prov of dir) {
      for (const c of prov.cities) state.cities.push(c);
    }
    const dl = $("citylist");
    dl.innerHTML = state.cities.map((c) => `<option value="${esc(c.name)}">`).join("");
    state.ready = true;
    paintCities();
    $("status").textContent =
      `共 ${state.cities.length} 座城市可选 · 已在浏览器里就绪（数据按需加载）`;
  } catch (e) {
    $("status").textContent = "城市目录加载失败：" + e.message;
  }
  paintPrefs();
  paintPresets();
  paintPicked();
  await maybeAutoDemo();
}

// ?demo=1 直接跑一组示例（便于截图与分享链接）
async function maybeAutoDemo() {
  const q = new URLSearchParams(location.search);
  if (!q.has("demo")) return;
  const keys = (q.get("cities") || "luoyang,zhengzhou").split(",").filter(Boolean);
  state.picked = keys.slice(0, 3);
  state.days = Number(q.get("days")) || Math.max(2, state.picked.length * 2);
  state.pickedDays = distribute(state.days, state.picked.length);
  state.originKey = q.get("origin") || "";
  state.originName = state.originKey ? D.cityName(state.originKey) : "";
  $("origin").value = state.originName;
  state.budget = Number(q.get("budget")) || 3000;
  $("budgetVal").textContent = state.budget;
  $("daysVal").textContent = state.days;
  if (q.get("transport")) {
    state.transport = q.get("transport");
    $("transport").value = state.transport;
  }
  if (q.get("prefs")) {
    state.prefs = new Set(q.get("prefs").split(","));
    paintPrefs();
  }
  paintPicked();
  paintCities();
  if (q.get("offline")) {
    state.online = false;
    $("online").checked = false;
  }
  await generate();
}

function initDates() {
  const t = new Date();
  const toYmd = (d) => d.toISOString().slice(0, 10);
  const tomorrow = new Date(t.getTime() + 86400000);
  state.startDate = toYmd(tomorrow);
  const el = $("start");
  el.value = state.startDate;
  const min = new Date(Date.now() - 90 * 86400000);
  const max = new Date(Date.now() + 15 * 86400000);
  el.min = toYmd(min);
  el.max = toYmd(max);
  el.addEventListener("change", () => {
    if (el.value) state.startDate = el.value;
  });
}

function bindControls() {
  $("search").addEventListener("input", (e) => {
    state.search = e.target.value.trim();
    paintCities();
  });

  $("origin").addEventListener("change", (e) => {
    const v = e.target.value.trim();
    const hit = state.cities.find((c) => c.name === v || c.key === v || c.name === v.replace(/市$/, ""));
    state.originKey = hit ? hit.key : "";
    state.originName = hit ? hit.name : "";
    if (v && !hit) {
      e.target.value = "";
      $("status").textContent = `没找到「${v}」这个城市，换一个试试（直接写城市名，如 长沙）。`;
    } else {
      $("status").textContent = hit
        ? `出发地：${hit.name} · 会计算去程与返程高铁`
        : "未设置出发地 · 只排目的地行程";
    }
  });

  document.querySelectorAll("[data-step]").forEach((btn) => {
    btn.addEventListener("click", () => {
      const [k, d] = btn.dataset.step.split(":");
      const delta = Number(d);
      if (k === "days") {
        state.days = Math.min(12, Math.max(1, state.days + delta));
        redistDays();
        $("daysVal").textContent = state.days;
        paintPicked();
      } else {
        state.budget = Math.min(20000, Math.max(300, state.budget + delta));
        $("budgetVal").textContent = state.budget;
      }
    });
  });

  $("party").addEventListener("change", (e) => { state.party = e.target.value; });
  $("pace").addEventListener("change", (e) => { state.pace = e.target.value; });
  $("transport").addEventListener("change", (e) => { state.transport = e.target.value; });
  $("online").addEventListener("change", (e) => { state.online = e.target.checked; });
  $("run").addEventListener("click", () => generate());
}

function redistDays() {
  if (!state.picked.length) return;
  state.pickedDays = distribute(state.days, state.picked.length);
}

// ---------------- 城市选择 ----------------
function paintCities() {
  const grid = $("citygrid");
  const q = state.search;
  const pickedSet = new Set(state.picked);
  let list;
  if (q) {
    list = state.cities.filter((c) => c.name.includes(q) || c.province.includes(q));
  } else {
    const rest = state.cities.filter((c) => !pickedSet.has(c.key));
    list = rest.slice(0, 60);
  }
  if (!list.length) {
    grid.innerHTML = `<div class="cityempty">没有匹配「${esc(q)}」的城市</div>`;
    return;
  }
  grid.innerHTML = list
    .map((c) => {
      const on = pickedSet.has(c.key);
      return `<div class="cityrow${on ? " on" : ""}" data-key="${esc(c.key)}" role="option" aria-selected="${on}">
        <span class="n">${esc(c.name)}</span><span class="p">${c.province === c.name ? "" : esc(c.province)}</span>
        <span style="flex:1"></span><span class="p">${on ? "已选" : ""}</span>
      </div>`;
    })
    .join("");
  grid.querySelectorAll(".cityrow").forEach((row) => {
    row.addEventListener("click", () => toggleCity(row.dataset.key));
  });
}

function toggleCity(key) {
  const i = state.picked.indexOf(key);
  if (i >= 0) {
    state.picked.splice(i, 1);
    state.pickedDays.splice(i, 1);
  } else {
    if (state.picked.length >= 3) {
      $("status").textContent = "最多选 3 座城市（跨城当天要占掉不少时间）。";
      return;
    }
    state.picked.push(key);
    redistDays();
  }
  if (!state.picked.length) state.pickedDays = [];
  paintPicked();
  paintCities();
}

function paintPicked() {
  const box = $("picked");
  if (!state.picked.length) {
    box.innerHTML = "";
    return;
  }
  box.innerHTML = state.picked
    .map((key, i) => {
      const c = state.cities.find((x) => x.key === key) || { name: key };
      return `<div class="picked-row">
        <span class="name">${i + 1}. ${esc(c.name)}</span>
        <span class="spacer"></span>
        <button type="button" data-dn="${i}" aria-label="减少天数">−</button>
        <span class="daysTag">${state.pickedDays[i] || 0} 天</span>
        <button type="button" data-dp="${i}" aria-label="增加天数">+</button>
        <button type="button" data-rm="${i}" aria-label="移除">✕</button>
      </div>`;
    })
    .join("");
  box.querySelectorAll("[data-dn]").forEach((b) =>
    b.addEventListener("click", () => bumpDays(Number(b.dataset.dn), -1)));
  box.querySelectorAll("[data-dp]").forEach((b) =>
    b.addEventListener("click", () => bumpDays(Number(b.dataset.dp), 1)));
  box.querySelectorAll("[data-rm]").forEach((b) =>
    b.addEventListener("click", () => toggleCity(state.picked[Number(b.dataset.rm)])));
}

function bumpDays(i, delta) {
  const next = state.pickedDays[i] + delta;
  if (next < 0) return;
  state.pickedDays[i] = next;
  state.days = state.pickedDays.reduce((a, b) => a + b, 0);
  $("daysVal").textContent = state.days;
  paintPicked();
}

function paintPrefs() {
  const box = $("prefs");
  box.innerHTML = PREF_TAGS.map(
    (t) => `<button type="button" class="chip" data-pref="${esc(t)}" aria-pressed="${state.prefs.has(t)}">${esc(t)}</button>`
  ).join("");
  box.querySelectorAll("[data-pref]").forEach((b) => {
    b.addEventListener("click", () => {
      const t = b.dataset.pref;
      if (state.prefs.has(t)) state.prefs.delete(t);
      else state.prefs.add(t);
      b.setAttribute("aria-pressed", state.prefs.has(t));
    });
  });
}

// ---------------- 数据加载 ----------------
async function ensureCity(key) {
  if (D.hasCity(key)) return;
  const specRes = await fetch(`agent/cities/${encodeURIComponent(key)}.json`);
  if (!specRes.ok) throw new Error(`城市数据缺失：${key}`);
  const spec = await specRes.json();
  let route = null;
  try {
    const r = await fetch(`agent/routes/${encodeURIComponent(key)}.json`);
    if (r.ok) route = await r.json();
  } catch (_) { /* 没有路网就用直线估算 */ }
  D.addCity(key, spec, route);
}

const WMO = {
  0: "晴", 1: "晴间多云", 2: "多云", 3: "阴", 45: "雾", 48: "雾凇",
  51: "毛毛雨", 53: "毛毛雨", 55: "毛毛雨", 61: "小雨", 63: "中雨", 65: "大雨",
  66: "冻雨", 67: "冻雨", 71: "小雪", 73: "中雪", 75: "大雪", 77: "雪粒",
  80: "阵雨", 81: "阵雨", 82: "强阵雨", 85: "阵雪", 86: "强阵雪",
  95: "雷阵雨", 96: "雷阵雨伴冰雹", 99: "雷阵雨伴冰雹",
};

async function applyLiveWeather(keys) {
  const start = state.startDate;
  const end = plusDays(start, state.days - 1);
  let okCount = 0;
  const notes = [];
  for (const key of keys) {
    const pos = D.cityPosition(key);
    if (!pos) continue;
    const url = "https://api.open-meteo.com/v1/forecast" +
      `?latitude=${pos[0].toFixed(3)}&longitude=${pos[1].toFixed(3)}` +
      "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max" +
      `&timezone=Asia%2FShanghai&start_date=${start}&end_date=${end}`;
    try {
      const res = await fetch(url, { mode: "cors" });
      if (!res.ok) throw new Error("HTTP " + res.status);
      const j = await res.json();
      const d = j.daily || {};
      if (!d.time || !d.time.length) throw new Error("empty");
      const days = d.time.map((t, i) => ({
        day: "第" + (i + 1) + "天",
        text: WMO[d.weather_code[i]] || "多云",
        low: Math.round(d.temperature_2m_min[i]),
        high: Math.round(d.temperature_2m_max[i]),
        rain: Math.round(d.precipitation_probability_max[i] ?? 0),
      }));
      D.setWeatherFor(key, days, "Open-Meteo 实时预报");
      okCount += 1;
    } catch (e) {
      notes.push(D.cityName(key));
    }
  }
  return { okCount, failed: notes };
}

function plusDays(ymd, n) {
  const d = new Date(ymd + "T00:00:00Z");
  d.setUTCDate(d.getUTCDate() + n);
  return d.toISOString().slice(0, 10);
}

// ---------------- 生成 ----------------
async function generate() {
  if (!state.ready) return;
  if (!state.picked.length) {
    $("status").textContent = "先在下面选 1–3 座城市。";
    return;
  }
  const btn = $("run");
  btn.disabled = true;
  btn.querySelector(".btn-label").textContent = "计算中…";
  btn.querySelector(".btn-spinner").hidden = false;

  try {
    const keys = state.picked.slice();
    const all = state.originKey && !keys.includes(state.originKey) ? keys.concat(state.originKey) : keys;
    $("status").textContent = "加载城市数据与路网…";
    for (const k of all) await ensureCity(k);

    let weatherNote = "内置天气（模板）";
    if (state.online) {
      $("status").textContent = "获取实时天气…";
      D.clearWeather();
      const r = await applyLiveWeather(keys);
      if (r.okCount === keys.length) weatherNote = "Open-Meteo 实时预报";
      else if (r.okCount > 0) weatherNote = `实时预报（${keys.length - r.okCount} 座城市回退内置）`;
    }

    $("status").textContent = "规划 → 校验 → 修复 …";
    const profile = new Profile({
      days: state.days,
      budget: state.budget,
      party: state.party,
      pace: state.pace,
      transport: state.transport,
      prefs: [...state.prefs],
      must_visit: [],
      hotel: "",
      start_date: state.startDate,
      cities: keys,
      city_days: state.pickedDays.slice(),
      origin: state.originKey,
      return_home: !!state.originKey,
    });

    const sp = engine.run(D, profile, "single_pass");
    const cl = engine.run(D, profile, "closed_loop");
    render(cl, sp, weatherNote);
    $("status").textContent =
      `完成 · 闭环修复 ${cl.metrics.repair_rounds} 轮 · 工具调用 ${cl.metrics.tool_calls} 次 · ` +
      `耗时 ${msText(cl.metrics.latency_ms)}（全部在浏览器里算）`;
  } catch (e) {
    $("output").hidden = false;
    $("empty").hidden = true;
    $("output").innerHTML = `<div class="notice">出错了：${esc(e.message)}</div>`;
    $("status").textContent = "运行失败，看右侧提示。";
  } finally {
    btn.disabled = false;
    btn.querySelector(".btn-label").textContent = "生成行程";
    btn.querySelector(".btn-spinner").hidden = true;
  }
}

// ---------------- 渲染 ----------------
function render(cl, sp, weatherNote) {
  const it = cl.itinerary;
  const rep = cl.report;
  const out = $("output");
  $("empty").hidden = true;
  out.hidden = false;

  const ride = totalRide(it);
  const cityChain = chainOf(it);

  const html = [];
  html.push(`<h3 class="sec">结果对比</h3>`);
  html.push(cmpTable(sp, cl));
  html.push(scoreCards(cl, ride));

  html.push(`<ul class="meta">
    <li>${esc(cityChain)}</li>
    <li>${it.days.length} 天</li>
    <li>${cl.metrics.stops} 处景点</li>
    <li>路上 ${fmtDur(ride)}</li>
    <li>修复 ${cl.metrics.repair_rounds} 轮 / 调用 ${cl.metrics.tool_calls} 次</li>
    <li>天气：${esc(weatherNote)}</li>
  </ul>`);

  const legs = railCards(it);
  if (legs) html.push(legs);

  html.push(`<h3 class="sec">逐日行程</h3>`);
  html.push(it.days.map((d, i) => dayCard(d, i, it.days.length === 1, it.rests)).join(""));

  html.push(`<h3 class="sec">约束检查</h3>`);
  if (!rep.violations.length) {
    html.push(`<div class="vitem soft" style="background:var(--ok-soft);border-color:#abefc6;color:#05603a">
      <span class="txt">全部 ${rep.hard_total} 项硬约束与 ${rep.soft_total} 项软约束都满足，行程可以直接照着走。</span></div>`);
  } else {
    html.push(`<ul class="vlist">` + rep.violations.map(vitem).join("") + `</ul>`);
  }

  if (sp.report.violations.length) {
    html.push(`<details style="margin-top:14px">
      <summary style="cursor:pointer;font-size:13.5px;color:var(--ink2)">
        一次性生成原来的问题（${sp.report.violations.length} 项）
      </summary>
      <ul class="vlist" style="margin-top:10px">` +
      sp.report.violations.slice(0, 12).map(vitem).join("") + `</ul></details>`);
  }

  if (cl.trace.length > 1) {
    html.push(`<h3 class="sec">修复轨迹</h3>`);
    html.push(`<ul class="trace">` + cl.trace.map(traceItem).join("") + `</ul>`);
  }

  out.innerHTML = html.join("");
  out.querySelectorAll(".day-head").forEach((h) => {
    h.addEventListener("click", () => h.parentElement.classList.toggle("collapsed"));
  });
}

function cmpTable(sp, cl) {
  const row = (label, a, b, goodWhenLower, neutral) => {
    const na = num(a), nb = num(b);
    const better = goodWhenLower ? nb < na : nb > na;
    const cls = neutral || na === nb ? "" : better ? "good" : "bad";
    return `<div class="label">${label}</div>
      <div class="val">${a}</div>
      <div class="val ${cls}">${b}</div>`;
  };
  return `<div class="cmp">
    <div class="head">指标</div>
    <div class="head">一次性生成</div>
    <div class="head last">校验—修复闭环</div>
    ${row("约束满足率", (sp.report.csr * 100).toFixed(1) + "%", (cl.report.csr * 100).toFixed(1) + "%", false)}
    ${row("硬冲突", sp.report.hard_failed, cl.report.hard_failed, true)}
    ${row("软提醒", sp.report.soft_failed, cl.report.soft_failed, true)}
    ${row("总花费", "¥" + sp.metrics.cost, "¥" + cl.metrics.cost, true)}
    ${row("景点数", sp.metrics.stops, cl.metrics.stops, false, true)}
  </div>`;
}

const num = (v) => Number(String(v).replace(/[^\d.]/g, "")) || 0;

function scoreCards(cl, ride) {
  const rep = cl.report;
  return `<div class="scoreCards">
    <div class="score ${rep.feasible ? "ok" : ""}">
      <div class="k">约束满足率</div>
      <div class="v">${(rep.csr * 100).toFixed(1)}<small>%</small></div>
    </div>
    <div class="score"><div class="k">硬冲突 / 软提醒</div>
      <div class="v">${rep.hard_failed}<small> / ${rep.soft_failed}</small></div></div>
    <div class="score"><div class="k">总花费 · 预算 ${state.budget}</div>
      <div class="v">¥${cl.metrics.cost}</div></div>
    <div class="score"><div class="k">路上总时长</div>
      <div class="v">${fmtDur(ride)}</div></div>
  </div>`;
}

function railCards(it) {
  const o = it.origin_leg, b = it.return_leg;
  if ((!o || !o.code) && (!b || !b.code)) return "";
  const line = (leg, label) => `<div class="legbar">
      <b>${label}</b> ${esc(leg.code)}　${esc(leg.dep_station)} ${fmt(leg.dep)} → ${esc(leg.arr_station)} ${fmt(leg.arr)}
      <br>历时 ${leg.run} 分钟 · ¥${leg.fare} · 约 ${Math.round(leg.km)} km
    </div>`;
  let html = `<h3 class="sec">往返交通</h3>`;
  if (o && o.code) html += line(o, "去程");
  if (b && b.code) html += line(b, "返程");
  const total = ((o && o.fare) || 0) + ((b && b.fare) || 0);
  if (total) html += `<p class="fineprint">往返车票 ¥${total}，已计入总花费。</p>`;
  return html;
}

function dayCard(day, i, singleCity, rests) {
  const w = day.weather || {};
  const date = plusDays(state.startDate, i);
  const wd = WEEK_CN[(new Date(date + "T00:00:00Z").getUTCDay() + 6) % 7];
  const leg = day.leg && day.leg.code ? day.leg : null;

  let body = "";
  if (leg) {
    body += `<div class="legbar">
      <b>城际</b> ${esc(leg.code)}　${esc(leg.dep_station)} ${fmt(leg.dep)} → ${esc(leg.arr_station)} ${fmt(leg.arr)}
      <br>${leg.run} 分钟 · ¥${leg.fare} · 约 ${Math.round(leg.km)} km · 到站后按 30 分钟放行李再出发
    </div>`;
  }

  let feet = 0, fee = 0;
  for (const s of day.stops) {
    const p = D.poiById.get(s.poi_id);
    feet += s.from_prev || 0;
    fee += p.price;
    body += `<div class="stop">
      <div class="time">${fmt(s.arrive)}–${fmt(s.depart)}</div>
      <div>
        <div class="nm">${esc(p.name)}</div>
        <div class="via">${viaText(s)}</div>
      </div>
      <div class="price">${p.price ? "¥" + p.price : "免费"}</div>
    </div>`;
  }
  const ride = feet + (leg ? leg.run : 0);
  const longDay = ride >= 120;
  let restMin = 0;
  (day.rest_done || []).forEach((done, ri) => {
    if (done) restMin += (rests[ri] || {}).minutes || 0;
  });
  body += `<div class="subtotal ${longDay ? "long" : ""}">
    ${day.stops.length} 处景点 · 门票 ¥${fee} · 路上 ${fmtDur(ride)}${restMin ? ` · 含休息 ${restMin} 分钟` : ""}${longDay ? " · 这天大半在路上" : ""}
  </div>`;

  const weatherTag = singleCity ? ` · ${esc(w.text || "")} ${w.low ?? ""}–${w.high ?? ""}℃ 降雨 ${w.rain ?? 0}%` : "";

  return `<div class="day">
    <div class="day-head">
      <span class="d">第 ${i + 1} 天</span>
      <span class="city">${esc(D.cityName(day.city))}</span>
      <span class="date">${date.slice(5)} ${wd}${weatherTag}</span>
      <span class="spacer"></span>
      <span class="caret">点击折叠</span>
    </div>
    <div class="day-body">${body}</div>
  </div>`;
}

function viaText(s) {
  const via = s.via || {};
  if (!via.headline) return "";
  const icon = via.segments && via.segments.some((x) => x.mode === "taxi" && via.segments.some((y) => y.mode === "metro"))
    ? "🚕🚇" : (via.segments && via.segments[0] ? modeIcon(via.segments[0].mode) : "🚇");
  let txt = `${icon} ${esc(via.headline)}`;
  if (via.why) txt += `<br>${esc(via.why)}`;
  return txt;
}

const modeIcon = (m) => ({ walk: "🚶", taxi: "🚕", transit: "🚇", metro: "🚇" }[m] || "🚇");

function msText(ms) {
  return ms < 0.1 ? "< 0.1 ms" : ms.toFixed(1) + " ms";
}

function vitem(v) {
  const day = v.day !== null && v.day !== undefined ? `第 ${v.day + 1} 天 · ` : "";
  return `<li class="vitem ${v.severity}">
    <span class="code">${esc(v.code)}</span>
    <span class="txt">${day}${esc(v.message)}${v.fix ? `<span class="fix">建议：${esc(v.fix)}</span>` : ""}</span>
  </li>`;
}

function traceItem(t) {
  if (t.round === 0) {
    return `<li><span class="r">初稿</span> · 硬冲突 ${t.hard} · 软提醒 ${t.soft} ·
      <span class="n">${(t.csr * 100).toFixed(1)}%</span> · 未做外部校验</li>`;
  }
  return `<li><span class="r">第 ${t.round} 轮</span> · ${esc(t.action)}<br>
    硬冲突 ${t.hard} · 软提醒 ${t.soft} · <span class="n">${(t.csr * 100).toFixed(1)}%</span></li>`;
}

function totalRide(it) {
  let s = 0;
  for (const d of it.days) {
    for (const st of d.stops) s += st.from_prev || 0;
    if (d.leg && d.leg.run) s += d.leg.run;
  }
  return s;
}

function chainOf(it) {
  const parts = [];
  for (const d of it.days) {
    const last = parts[parts.length - 1];
    if (last && last.key === d.city) last.n += 1;
    else parts.push({ key: d.city, n: 1 });
  }
  return parts.map((p) => `${D.cityName(p.key)} ${p.n} 天`).join(" → ");
}

function esc(s) {
  return String(s == null ? "" : s).replace(/[&<>"']/g, (c) => (
    { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]
  ));
}

function paintPresets() {
  const box = $("presets");
  box.innerHTML = `<span class="presets-label">一键试试：</span>` +
    PRESETS.map((p, i) => `<button type="button" data-preset="${i}">${esc(p.label)}</button>`).join("");
  box.querySelectorAll("[data-preset]").forEach((b) => {
    b.addEventListener("click", () => applyPreset(PRESETS[Number(b.dataset.preset)]));
  });
}

function applyPreset(p) {
  state.picked = p.cities.slice();
  state.pickedDays = p.cityDays.slice();
  state.days = state.pickedDays.reduce((a, b) => a + b, 0);
  state.budget = p.budget;
  state.prefs = new Set(p.prefs);
  state.transport = p.transport;
  state.originKey = p.origin;
  const oc = state.cities.find((c) => c.key === p.origin);
  state.originName = oc ? oc.name : p.origin;
  $("daysVal").textContent = state.days;
  $("budgetVal").textContent = state.budget;
  $("transport").value = p.transport;
  $("origin").value = state.originName;
  paintPrefs();
  paintPicked();
  paintCities();
  generate();
}

boot();