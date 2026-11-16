'use strict';
// Number, time and model-name formatting (same rules as the Windows app).

function tokens(n) {
  if (n < 1000) return String(n);
  if (n < 10000) return `${trim1(n / 1000)}k`;
  if (n < 1e6) return `${Math.floor(n / 1000)}k`;
  if (n < 1e7) return `${trim2(n / 1e6)}M`;
  if (n < 1e9) return `${trim1(n / 1e6)}M`;
  return `${trim2(n / 1e9)}B`;
}

function trim1(x) { return String(Math.round(x * 10) / 10); }
function trim2(x) { return String(Math.round(x * 100) / 100); }

function money(usd) {
  if (!(usd > 0)) return '$0';
  if (usd < 0.01) return '<$0.01';
  if (usd < 1000) return `$${usd.toFixed(2)}`;
  if (usd < 100000) return `$${Math.round(usd).toLocaleString('en-US')}`;
  return `$${Math.round(usd / 1000)}k`;
}

function duration(ms) {
  const s = Math.max(0, Math.floor(ms / 1000));
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m`;
  const h = Math.floor(m / 60);
  if (h < 24) return `${h}h ${m % 60}m`;
  return `${Math.floor(h / 24)}d ${h % 24}h`;
}

function ago(ms) { return ms < 60000 ? 'just now' : `${duration(ms)} ago`; }

const DAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];

/** "in 2h 10m" when soon, "Thu 09:45" (local time) when more than a day away. */
function resetsIn(resetsAtSeconds, nowMs) {
  const ms = resetsAtSeconds * 1000 - nowMs;
  if (ms <= 0) return 'now';
  if (ms < 24 * 3600 * 1000) return `in ${duration(ms)}`;
  const d = new Date(resetsAtSeconds * 1000);
  return `${DAYS[d.getDay()]} ${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
}

function baseModel(model) {
  if (!model) return null;
  const b = model.indexOf('[');
  return (b >= 0 ? model.slice(0, b) : model).trim().toLowerCase();
}

/** "claude-opus-5-5" -> "Opus 5.5", "claude-3-5-sonnet-20241022" -> "Sonnet 3.5" */
function modelLabel(model) {
  let m = baseModel(model);
  if (!m) return null;
  if (m.startsWith('claude-')) m = m.slice(7);
  let name = null;
  const nums = [];
  for (const part of m.split('-')) {
    if (!part) continue;
    if (/^\d+$/.test(part)) { if (part.length <= 2) nums.push(part); } else if (!name) name = part[0].toUpperCase() + part.slice(1);
  }
  if (!name) return model;
  return nums.length ? `${name} ${nums.join('.')}` : name;
}

function oneLine(s, max) {
  if (!s || !s.trim()) return '';
  const r = s.trim().replace(/\s+/g, ' ');
  return r.length > max ? `${r.slice(0, max - 1)}…` : r;
}

function localDay(ms) {
  const d = new Date(ms);
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}

module.exports = { tokens, money, duration, ago, resetsIn, baseModel, modelLabel, oneLine, localDay, DAYS };
