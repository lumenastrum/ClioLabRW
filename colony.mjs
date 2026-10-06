#!/usr/bin/env node
// Clio plays a colony: compact views over lab.mjs's Overseer commands. Zero deps.
//
//   node colony.mjs state             one screen: date, colonists, stock, research, threats, letters, windows
//   node colony.mjs go [hours] [speed]  advance until time is up or something wants a player; prints why + new talk
//   node colony.mjs map [x z w h] [step]  ASCII map (default: whole map at step 5)
//   node colony.mjs talk [n]          RimTalk lines since the last read (or the last n)
//   node colony.mjs <cmd> [json]      anything else goes straight to the lab (build, zone, designate, …)
//
// RimTalk lines come from Clio Couch's feed, which the colony profile loads alongside the lab.

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const LAB_DATA = process.env.RW_LAB_DATA || path.join(os.homedir(), 'RWLab');
const COUCH_FEED = path.join(LAB_DATA, 'ClioCouchRW', 'feed.ndjson');
const TALK_MARK = path.join(LAB_DATA, 'ClioLab', 'talk.mark');

function lab(cmd, args = {}, timeoutMs) {
  const env = { ...process.env };
  if (timeoutMs) env.LAB_TIMEOUT = String(timeoutMs);
  let out;
  try {
    out = execFileSync('node', [path.join(HERE, 'lab.mjs'), 'cmd', cmd, JSON.stringify(args)], { encoding: 'utf8', env, maxBuffer: 64 << 20 });
  } catch (e) {
    out = e.stdout || '';
  }
  // lab.mjs prints one JSON object (newgame/load print two); take the first.
  const end = out.indexOf('\n}\n');
  return JSON.parse(end >= 0 ? out.slice(0, end + 2) : out);
}

const pct = (n) => (n < 0 ? '-' : `${n}`);

function state() {
  const s = lab('state');
  if (!s.ok) return console.log(`ERR ${s.error}`);
  console.log(`${s.colony} · ${s.date} · ${s.season} · ${s.weather} ${s.tempC}°C · day ${s.day} · ${s.speed} · wealth ${s.wealth} · grow: ${s.growing}`);
  for (const c of s.colonists) {
    if (c.away) { console.log(`  ${c.name.padEnd(9)} AWAY`); continue; }
    const flags = [c.downed && 'DOWNED', c.mental && `MENTAL:${c.mental}`, c.drafted && 'drafted', c.hp < 100 && `hp${c.hp}`].filter(Boolean).join(' ');
    console.log(`  ${c.name.padEnd(9)} @${c.pos.padEnd(8)} mood ${pct(c.mood)}/${c.breakAt} food ${pct(c.food)} rest ${pct(c.rest)} joy ${pct(c.joy)} ${flags} — ${c.job ?? ''}${c.hurts.length ? ` [${c.hurts.join('; ')}]` : ''}`);
  }
  const res = Object.entries(s.resources).map(([k, v]) => `${k} ${v}`).join(', ');
  console.log(`  stock: ${res || '(nothing in storage)'}`);
  console.log(`  research: ${s.research.current ?? 'NONE'} ${s.research.pct}% · animals ${JSON.stringify(s.animals)} · bp ${s.blueprints} frames ${s.frames}`);
  console.log(`  zones: ${s.zones.map((z) => `${z.label}(${z.cells}${z.plant ? ' ' + z.plant : ''})`).join(', ') || '-'}`);
  const hostiles = Object.entries(s.hostiles);
  if (hostiles.length) console.log(`  !! HOSTILES: ${hostiles.map(([k, v]) => `${v}× ${k}`).join(', ')}`);
  if (s.alerts.length) console.log(`  alerts: ${s.alerts.map((a) => a.label).join(' | ')}`);
  if (s.letters.length) console.log(`  letters: ${s.letters.map((l) => `#${l.i} ${l.label}${l.choices ? ` {${l.choices.join(' / ')}}` : ''}`).join(' | ')}`);
  if (s.windows.length) console.log(`  WINDOWS: ${JSON.stringify(s.windows)}`);
}

function talk(n) {
  if (!fs.existsSync(COUCH_FEED)) return console.log('(no couch feed)');
  const lines = fs.readFileSync(COUCH_FEED, 'utf8').split('\n').filter(Boolean);
  let from = 0;
  if (n) from = Math.max(0, lines.length - n);
  else {
    try { from = Number(fs.readFileSync(TALK_MARK, 'utf8')) || 0; } catch { /* first read */ }
    if (from > lines.length) from = 0;   // feed was truncated / new game
  }
  const out = [];
  for (const l of lines.slice(from)) {
    let j; try { j = JSON.parse(l); } catch { continue; }
    if (j.ev === 'talk') out.push(`  💬 ${j.speaker}${j.to && j.to !== j.speaker ? ` → ${j.to}` : ''}: ${j.text}`);
    else if (j.ev === 'letter') out.push(`  ✉ ${j.label}`);
    else if (j.ev === 'battle' || j.ev === 'down' || j.ev === 'kill') out.push(`  ⚔ ${j.text ?? JSON.stringify(j)}`);
  }
  fs.writeFileSync(TALK_MARK, String(lines.length));
  // A chatty day can be long: keep the newest.
  const max = Number(process.env.TALK_MAX || 25);
  if (out.length > max) console.log(`  (… ${out.length - max} earlier lines)`);
  console.log(out.slice(-max).join('\n') || '  (quiet)');
}

function go(hours = 6, speed = 3) {
  const r = lab('advance', { hours: Number(hours), speed: Number(speed), wallSec: 240 }, 300000);
  if (!r.ok) return console.log(`ERR ${r.error}`);
  console.log(`⏱ +${r.hours}h → ${r.date} · stop: ${r.stop}`);
  for (const l of r.newLetters) console.log(`  ✉ #${l.i} [${l.def}] ${l.label}${l.choices ? ` {${l.choices.join(' / ')}}` : ''}\n     ${(l.text ?? '').replace(/\s+/g, ' ').slice(0, 500)}`);
  for (const w of r.windows) console.log(`  🪟 ${JSON.stringify(w)}`);
  if (r.hostiles.length) console.log(`  !! ${r.hostiles.length} hostiles: ${r.hostiles.map((h) => `${h.what}@${h.pos}${h.weapon ? ' ' + h.weapon : ''}`).join(', ')}`);
  talk();
}

function map(args) {
  const nums = args.map(Number);
  const req = nums.length >= 4 ? { rect: nums.slice(0, 4), step: nums[4] || 1 } : { step: nums[0] || 5 };
  const r = lab('map', req);
  if (!r.ok) return console.log(`ERR ${r.error}`);
  console.log(r.rows.join('\n'));
}

const [, , verb, ...rest] = process.argv;
switch (verb) {
  case 'state': state(); break;
  case 'go': go(rest[0], rest[1]); break;
  case 'map': map(rest); break;
  case 'talk': talk(Number(rest[0]) || 0); break;
  case 'legend': console.log(lab('map', { rect: [0, 0, 1, 1] }).legend); break;
  default: {
    if (!verb) { console.log(fs.readFileSync(fileURLToPath(import.meta.url), 'utf8').split('\n').slice(1, 11).join('\n')); break; }
    const r = lab(verb, rest[0] ? JSON.parse(rest[0]) : {});
    console.log(JSON.stringify(r));
  }
}
