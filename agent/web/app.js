const state = {
  cities: [], cityDays: {}, prefs: ["历史人文", "美食"], must: [],
  transport: "auto", origin: "",
  party: "情侣", pace: "normal", budget: 3000, start_date: "",
};
const CODE_LABEL = {
  OUT_OF_WINDOW: "开放时间冲突", CLOSED_WEEKDAY: "闭馆日", DUPLICATE: "重复安排",
  RAIN_OUTDOOR: "雨天户外", LONG_WAIT: "等待过长", MEAL_MISSING: "缺餐饮",
  PACE_EXCESS: "超出行程节奏", DAY_OVERRUN: "超过 22:00", BUDGET_EXCEEDED: "超出预算",
  MISSING_MUST_VISIT: "必去点缺失", PREF_LOW: "偏好匹配低", DAY_EMPTY: "空行程",
};
let META = { pois: {}, prefs: [], must: [], directory: [], available: [] };
let RESULT = null;
let TAB = "closed_loop";
let PROVINCE = "";

const $ = (s) => document.querySelector(s);
const el = (tag, cls, html) => { const n = document.createElement(tag); if (cls) n.className = cls; if (html != null) n.innerHTML = html; return n; };
const pct = (v) => (v * 100).toFixed(1) + "%";
const hhmm = (m) => String(Math.floor(m / 60) % 24).padStart(2, "0") + ":" + String(m % 60).padStart(2, "0");
const totalDays = () => state.cities.reduce((s, k) => s + (state.cityDays[k] || 1), 0);
const cityName = (key) => {
  const hit = (META.available || []).find((c) => c.key === key);
  if (hit) return hit.name;
  for (const p of META.directory || []) for (const c of p.cities) if (c.key === key) return c.name;
  return key;
};
const hasData = (key) => (META.available || []).some((c) => c.key === key);

function chip(text, on, onclick, cls) {
  const c = el("div", "chip" + (on ? " on" : "") + (cls ? " " + cls : ""), text);
  c.onclick = (ev) => { if (ev) ev.stopPropagation(); onclick(); };
  return c;
}

/* ---------- 城市选择 ---------- */
function renderProvince() {
  const sel = $("#province");
  sel.innerHTML = (META.directory || []).map((p) => `<option>${p.name}</option>`).join("");
  sel.value = PROVINCE;
}

function citiesForList() {
  const q = ($("#city_search").value || "").trim();
  if (q) {
    const out = [];
    (META.directory || []).forEach((p) => p.cities.forEach((c) => {
      if (c.name.includes(q)) out.push({ ...c, province: p.name });
    }));
    return out;
  }
  const p = (META.directory || []).find((x) => x.name === PROVINCE);
  return p ? p.cities.map((c) => ({ ...c, province: p.name })) : [];
}

function renderCityList() {
  const box = $("#city_list"); box.innerHTML = "";
  const list = citiesForList();
  $("#city_hint").textContent = list.length ? `${list.length} 个候选` : "无匹配城市";
  if (!list.length) { box.appendChild(el("div", "empty", "换个关键字试试，比如「九江」「敦煌」")); return; }
  list.forEach((c) => {
    const key = c.key;
    const on = !!key && state.cities.includes(key);
    const generic = !c.has_data;                  // 目录里有、没有手工数据 → 通用模板
    const label = c.name + (generic ? " ·通用" : "");
    const node = chip(label, on, () => toggleCity(key), generic ? "generic" : "");
    if (!key) node.classList.add("off");
    box.appendChild(node);
  });
}

function toggleCity(key) {
  if (state.cities.includes(key)) {
    state.cities = state.cities.filter((k) => k !== key);
    delete state.cityDays[key];
  } else {
    if (totalDays() >= META.max_days) { $("#city_hint").textContent = `最多 ${META.max_days} 天`; return; }
    state.cities.push(key);
    state.cityDays[key] = 1;
  }
  if (!state.cities.length) { state.cities.push(key); state.cityDays[key] = 1; }
  renderPicked(); renderCityList(); renderMust();
}

function renderPicked() {
  const box = $("#picked"); box.innerHTML = "";
  state.cities.forEach((key) => {
    const n = state.cityDays[key] || 1;
    const wrap = el("div", "chip on pick");
    wrap.innerHTML = `<span class="pick-name">${cityName(key)}</span>
      <span class="stepper"><button data-d="-1">−</button><b>${n}</b>天<button data-d="1">+</button></span>`;
    wrap.querySelectorAll("button").forEach((b) => {
      b.onclick = (ev) => {
        ev.stopPropagation();
        const d = Number(b.dataset.d);
        const next = n + d;
        if (next < 1) return toggleCity(key);
        if (totalDays() + d > META.max_days) return;
        state.cityDays[key] = next;
        renderPicked();
      };
    });
    box.appendChild(wrap);
  });
  $("#day_sum").textContent = `共 ${totalDays()} 天 · ${state.cities.length} 个城市`;
}

/* ---------- 其他表单 ---------- */
function renderForm() {
  const prefs = $("#prefs"); prefs.innerHTML = "";
  META.prefs.forEach((p) => prefs.appendChild(chip(p, state.prefs.includes(p), () => {
    state.prefs = state.prefs.includes(p) ? state.prefs.filter((x) => x !== p) : [...state.prefs, p];
    renderForm();
  })));
  $("#party").value = state.party;
  $("#pace").value = state.pace;
  $("#budget").value = state.budget;
  renderMust();
}

function renderMust() {
  const box = $("#must"); box.innerHTML = "";
  const pool = (META.must || []).filter((m) => state.cities.includes(m.city));
  state.must = state.must.filter((id) => pool.some((m) => m.id === id));
  if (!pool.length) { box.appendChild(el("div", "empty", "所选城市暂无必去点")); return; }
  pool.forEach((m) => box.appendChild(chip(
    `${m.name}<em>${cityName(m.city)}</em>`,
    state.must.includes(m.id),
    () => {
      state.must = state.must.includes(m.id) ? state.must.filter((x) => x !== m.id) : [...state.must, m.id];
      renderMust();
    })));
}

/* ---------- 结果渲染 ---------- */
function violationIndex(mode) {
  const map = new Map();
  (mode.report.violations || []).forEach((v) => {
    if (v.day == null || !v.poi_id) return;
    const k = v.day + "|" + v.poi_id;
    if (!map.has(k)) map.set(k, []);
    map.get(k).push(v);
  });
  return map;
}

function renderMetrics() {
  const a = RESULT.single_pass, b = RESULT.closed_loop;
  const box = $("#metrics"); box.innerHTML = "";
  const legFare = (m) => m.itinerary.days.reduce((s, d) => s + ((d.leg || {}).fare || 0), 0);
  const legKm = (m) => m.itinerary.days.reduce((s, d) => s + ((d.leg || {}).km || 0), 0);
  const specs = [
    { m: a, name: "一次性生成", desc: "LLM 直接输出，无外部校验", pill: ["bad", "参考基线"], color: "#d5443c" },
    { m: b, name: "规划—校验—修复", desc: "工具校验 + 定向修复闭环", pill: ["ok", "本文方法"], color: "#12915f" },
  ];
  specs.forEach((s) => {
    const m = s.m, csr = m.report.csr;
    const wrap = el("div", "mode");
    wrap.innerHTML = `
      <div class="mode-head">
        <span class="name">${s.name}</span>
        <span class="pill ${s.pill[0]}">${s.pill[1]}</span>
        <span class="desc">${s.desc}</span>
      </div>
      <div class="big">
        <span class="num" style="color:${s.color}">${pct(csr)}</span><span class="unit">硬约束满足率</span>
        <span class="cap">${m.report.feasible ? "可执行" : m.report.hard_failed + " 处冲突"}</span>
      </div>
      <div class="bar"><i style="width:${csr * 100}%;background:${s.color}"></i></div>
      <dl class="kv">
        <dt>覆盖城市</dt><dd>${(RESULT.cities || []).map((c) => c.name).join(" → ")}</dd>
        <dt>硬约束冲突</dt><dd>${m.report.hard_failed} <em>处</em></dd>
        <dt>软约束提醒</dt><dd>${m.report.soft_failed} <em>项</em></dd>
        <dt>偏好匹配</dt><dd>${pct(m.metrics.pref_score)}</dd>
        <dt>预估花费</dt><dd>¥${m.metrics.cost} <em>/ 预算 ¥${RESULT.profile.budget}</em></dd>
        <dt>城际交通</dt><dd>${legKm(m) ? legKm(m).toFixed(0) + " km · ¥" + legFare(m) : "同城"}</dd>
        <dt>工具调用</dt><dd>${m.metrics.tool_calls} 次</dd>
        <dt>修复轮次</dt><dd>${m.metrics.repair_rounds} 轮</dd>
        <dt>链路耗时</dt><dd>${m.metrics.latency_ms >= 0.1 ? Number(m.metrics.latency_ms).toFixed(1) + " ms" : "< 0.1 ms"}</dd>
      </dl>`;
    box.appendChild(wrap);
  });
  const d = (b.report.csr - a.report.csr) * 100;
  $("#compare-hint").textContent =
    `闭环修复：硬冲突 ${a.report.hard_failed} → ${b.report.hard_failed}，CSR ${d >= 0 ? "+" : ""}${d.toFixed(1)} pt，` +
    `调用 ${a.metrics.tool_calls} → ${b.metrics.tool_calls} 次`;
}

function renderItinerary() {
  const mode = RESULT[TAB];
  const box = $("#days-out"); box.innerHTML = "";
  const vmap = violationIndex(mode);
  $("#itin-hint").textContent = TAB === "closed_loop"
    ? `${mode.metrics.repair_rounds} 轮修复后 · ${mode.metrics.stops} 个站点`
    : `未校验初稿 · ${mode.metrics.stops} 个站点`;

  const rt = mode.itinerary || {};
  const og = rt.origin_leg && rt.origin_leg.code ? rt.origin_leg : null;
  const bk = rt.return_leg && rt.return_leg.code ? rt.return_leg : null;
  if (og || bk) {
    const card = el("div", "day");
    const head = el("div", "day-head");
    head.innerHTML = `<b>往返交通</b><span class="badge">${(og ? og.fare : 0) + (bk ? bk.fare : 0)} 元车票已计入</span>`;
    card.appendChild(head);
    if (og) card.appendChild(el("div", "leg",
      `🚄 去程 ${og.code}　${og.dep_station} ${hhmm(og.dep)} → ${og.arr_station} ${hhmm(og.arr)}`
      + ` · 历时 ${og.run} 分钟 · ¥${og.fare}`));
    if (bk) card.appendChild(el("div", "leg",
      `🚄 返程 ${bk.code}　${bk.dep_station} ${hhmm(bk.dep)} → ${bk.arr_station} ${hhmm(bk.arr)}`
      + ` · 历时 ${bk.run} 分钟 · ¥${bk.fare}`));
    box.appendChild(card);
  }

  mode.itinerary.days.forEach((day, di) => {
    const stops = day.stops;
    const dv = (mode.report.violations || []).filter((v) => v.day === di);
    const hard = dv.filter((v) => v.severity === "hard").length;
    const soft = dv.length - hard;
    const badge = hard ? { cls: "bad", txt: hard + " 处冲突" }
      : soft ? { cls: "warn", txt: soft + " 项提醒" }
      : { cls: "", txt: "全部满足" };
    const card = el("div", "day");
    const w = day.weather || {};
    const head = el("div", "day-head");
    const wd = w.date ? "日一二三四五六"[new Date(w.date + "T00:00:00").getDay()] : "";
    head.innerHTML = `
      <b>第 ${di + 1} 天 · ${day.city_name || cityName(day.city) || ""}${w.date ? " · " + w.date.slice(5) + " 周" + wd : ""}</b>
      <span class="weather">${w.text || ""} ${w.low}–${w.high}℃ · 降雨 ${w.rain}%</span>
      <span class="badge ${badge.cls}">${badge.txt}</span>`
      + (day.rest_done && day.rest_done.some(Boolean) ? `<span class="hint">休息</span>` : "");
    card.appendChild(head);

    const leg = day.leg || {};
    if (leg.to) {
      const t = leg.code
        ? `🚄 ${leg.code}　${leg.dep_station || ""} ${hhmm(leg.dep)} → ${leg.arr_station || ""} ${hhmm(leg.arr)}`
        : `🚄 城际交通：${leg.from_name || cityName(leg.from)} → ${leg.to_name || cityName(leg.to)}`;
      card.appendChild(el("div", "leg", t
        + ` · 历时 ${leg.run || leg.minutes} 分钟 · ¥${leg.fare} · 约 ${Math.round(leg.km)} km`));
    }
    if (!stops.length) card.appendChild(el("div", "empty", "当天无安排"));
    stops.forEach((s) => {
      const poi = META.pois[s.poi_id] || { name: s.poi_id, tags: [], price: 0, duration: 0 };
      const vs = vmap.get(di + "|" + s.poi_id) || [];
      const hardV = vs.find((v) => v.severity === "hard");
      const softV = vs.find((v) => v.severity === "soft");
      const row = el("div", "stop");
      const tag = poi.tags.length ? `<span class="tag">${poi.tags[0]}</span>` : "";
      const via = s.via && s.via.headline ? ` · ${s.via.headline}` : "";
      const travel = via || (s.from_prev ? ` · 上一站 ${s.from_prev} 分钟` : "");
      let flag = `<span class="flag ok">✓</span>`;
      if (hardV) flag = `<span class="flag bad">${CODE_LABEL[hardV.code] || hardV.code}</span>`;
      else if (softV) flag = `<span class="flag warn">${CODE_LABEL[softV.code] || softV.code}</span>`;
      row.innerHTML = `
        <div class="time">${hhmm(s.arrive)}<span>${s.depart ? "至 " + hhmm(s.depart) : ""}</span></div>
        <div>
          <div class="name">${poi.name}${tag}</div>
          <div class="sub">游览 ${poi.duration} 分钟 · 门票 ¥${poi.price}${travel}${s.wait ? " · 等待 " + s.wait + " 分钟" : ""}</div>
        </div>`;
      row.appendChild(el("div", "", flag));
      card.appendChild(row);
    });
    box.appendChild(card);
  });
}

function renderTrace() {
  const box = $("#trace"); box.innerHTML = "";
  const tr = RESULT.closed_loop.trace || [];
  if (!tr.length) { box.appendChild(el("div", "empty", "暂无记录")); return; }
  tr.forEach((t) => {
    const item = el("div", "trace-item");
    item.innerHTML = `
      <div class="trace-no">${t.round}</div>
      <div>
        <div class="act">${t.action}</div>
        <div class="sub">硬冲突 ${t.hard} · 软冲突 ${t.soft} · CSR ${pct(t.csr)}` +
      (t.codes && t.codes.length ? ` · 剩余 ${t.codes.map((c) => CODE_LABEL[c] || c).join("、")}` : "") + `</div>
      </div>`;
    box.appendChild(item);
  });
}

function renderAll() { renderMetrics(); renderItinerary(); renderTrace(); }

async function generate() {
  const go = $("#go"); go.disabled = true; go.textContent = "规划与校验中…";
  $("#compare-hint").textContent = "正在生成…";
  try {
    const res = await fetch("/api/plan", {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        cities: state.cities, city_days: state.cities.map((k) => state.cityDays[k] || 1),
        days: totalDays(), party: state.party, pace: state.pace, budget: state.budget,
        prefs: state.prefs, must: state.must, start_date: state.start_date,
        transport: state.transport, origin: state.origin,
      }),
    });
    RESULT = await res.json();
    if (RESULT.error) throw new Error(RESULT.error);
    if (RESULT.weather_source) {
      META.weather_source = RESULT.weather_source;
      paintMeta();
    }
    renderAll();
  } catch (e) {
    $("#compare-hint").textContent = "生成失败：" + e.message;
  } finally { go.disabled = false; go.textContent = "生成对比方案"; }
}

function addDays(iso, n) {
  const d = new Date(iso + "T00:00:00");
  d.setDate(d.getDate() + n);
  return d.toISOString().slice(0, 10);
}

function paintMeta() {
  const sub = document.querySelector("#subtitle");
  if (sub) sub.textContent = `规划 — 校验 — 修复 闭环 · ${state.cities.map(cityName).join(" → ")} · ${totalDays()} 日`;
  const ds = document.querySelector("#datasrc");
  if (ds) ds.textContent = `${(META.weather_source || "内置天气")} · 内置 POI ${(META.available || []).length} 城`;
}

async function refreshWeather() {
  const city = state.cities[0];
  try {
    const q = new URLSearchParams({ city, start_date: state.start_date, days: totalDays() });
    const res = await fetch("/api/weather?" + q.toString());
    const info = await res.json();
    if (info.error) throw new Error(info.error);
    META.weather_source = info.weather_source;
    paintMeta();
    $("#date_hint").textContent = "天气：" + info.weather_source;
    await generate();
  } catch (e) {
    $("#date_hint").textContent = "天气获取失败：" + e.message;
  }
}

async function boot() {
  META = await (await fetch("/api/meta")).json();
  state.start_date = META.today || new Date().toISOString().slice(0, 10);
  const hasD = (META.available || []).map((c) => c.key);
  const prefer = ["nanchang", "chengdu", "luoyang"];
  const first = prefer.find((k) => hasD.includes(k)) || hasD[0]
    || (META.city_keys && META.city_keys[0]);
  state.cities = [first];
  state.cityDays = { [first]: 3 };
  const prov = (META.directory || []).find((p) => p.cities.some((c) => c.key === first));
  PROVINCE = prov ? prov.name : (META.directory[0] || {}).name || "";
  state.must = (META.must && META.must[0]) ? [META.must[0].id] : [];

  $("#start_date").value = state.start_date;
  $("#start_date").min = addDays(state.start_date, -90);
  $("#start_date").max = addDays(state.start_date, 15);
  const q = new URLSearchParams(location.search);
  const qc = (q.get("cities") || "").split(",").map((x) => x.trim()).filter(hasData);
  const qd = (q.get("days") || "").split(",").map((x) => parseInt(x, 10));
  if (qc.length) {
    state.cities = qc.slice(0, 6);
    state.cityDays = {};
    state.cities.forEach((k, i) => { state.cityDays[k] = Math.max(1, qd[i] || 1); });
    const p2 = (META.directory || []).find((p) => p.cities.some((c) => c.key === state.cities[0]));
    if (p2) PROVINCE = p2.name;
  }
  if (q.get("tab") === "single_pass") {
    TAB = "single_pass";
    document.querySelectorAll("#tabs .tab").forEach((x) => x.classList.toggle("on", x.dataset.mode === TAB));
  }
  renderProvince(); renderPicked(); renderCityList(); renderForm(); paintMeta();

  $("#go").onclick = generate;
  document.querySelectorAll("#tabs .tab").forEach((t) => {
    t.onclick = () => {
      TAB = t.dataset.mode;
      document.querySelectorAll("#tabs .tab").forEach((x) => x.classList.toggle("on", x === t));
      if (RESULT) renderItinerary();
    };
  });
  ["party", "pace", "budget"].forEach((id) => {
    $("#" + id).onchange = (e) => { state[id] = id === "budget" ? Number(e.target.value) : e.target.value; };
  });
  $("#province").onchange = (e) => {
    PROVINCE = e.target.value;
    $("#city_search").value = "";
    renderCityList();
  };
  $("#city_search").oninput = renderCityList;
  $("#transport").onchange = (e) => { state.transport = e.target.value; };
  $("#origin").onchange = (e) => {
    const v = (e.target.value || "").trim();
    const hit = [];
    for (const p of (META.directory || [])) for (const c of p.cities) {
      if (c.name === v || c.name === v.replace(/市$/, "") || c.key === v) hit.push(c);
    }
    state.origin = hit.length ? hit[0].key : "";
    $("#origin_hint").textContent = state.origin
      ? `出发地：${hit[0].name} · 已计入去程与返程高铁时间`
      : "未设置出发地 · 只排目的地行程";
  };
  $("#start_date").onchange = async (e) => {
    state.start_date = e.target.value || META.today;
    await refreshWeather();
  };
  await generate();
}

boot();