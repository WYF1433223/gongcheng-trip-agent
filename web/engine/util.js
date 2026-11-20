// 小工具：与 Python round() 一致的银行家舍入、时间格式化。
export function roundHalfEven(x) {
  const f = Math.floor(x);
  const d = x - f;
  if (d > 0.5) return f + 1;
  if (d < 0.5) return f;
  return f % 2 === 0 ? f : f + 1;
}

// Python int(round(x))
export function iround(x) {
  return Math.trunc(roundHalfEven(x));
}

// Python round(x, 1)
export function round1(x) {
  return roundHalfEven(x * 10) / 10;
}

// Python round(x, 4)
export function round4(x) {
  return Math.round(x * 10000) / 10000;
}

export function fmt(minutes) {
  let m = Math.trunc(minutes) % 1440;
  if (m < 0) m += 1440;
  const h = Math.floor(m / 60);
  const mm = m % 60;
  return String(h).padStart(2, "0") + ":" + String(mm).padStart(2, "0");
}

export function fmtDur(minutes) {
  const m = Math.max(0, Math.trunc(minutes));
  const h = Math.floor(m / 60);
  const mm = m % 60;
  if (h <= 0) return mm + " 分钟";
  if (mm === 0) return h + " 小时";
  return h + " 小时 " + mm + " 分";
}