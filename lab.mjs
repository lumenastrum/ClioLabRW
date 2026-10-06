#!/usr/bin/env node
// Clio's RimWorld lab driver. Zero deps.
//
//   node lab.mjs setup [--profile P]   write the isolated lab config (modlist + prefs) under LAB_DATA
//   node lab.mjs launch [--menu] [--profile P]
//                                      start RimWorld on LAB_DATA (-quicktest unless --menu), wait for a map
//   node lab.mjs order --profile P     check P's load order against every mod's About.xml rules
//                                      Profiles = profiles\<P>.txt, one packageId per line in load order;
//                                      no --profile = the built-in Heyra set. The lab mod is appended last.
//   node lab.mjs cmd <cmd> [json]      send one command, wait for and print its result
//   node lab.mjs log [--all] [--since N] [--tail N]
//                                      game warnings/errors captured by the mod (default: errors only)
//   node lab.mjs status                ping + process check
//   node lab.mjs quit | kill           clean shutdown via the mod | taskkill
//
// The lab NEVER touches Andres's real config: -savedatafolder points RimWorld at LAB_DATA for
// ModsConfig, Prefs, saves, screenshots and the ClioLab inbox. Mods themselves still come from the
// game's Mods folder + Steam Workshop (Steam must be running for Workshop mods).

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn, execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

// RW_GAME_DIR wins; otherwise the usual Steam spots (default install, then a SteamLibrary on C:–H:).
const STEAM_DEFAULT = 'C:\\Program Files (x86)\\Steam\\steamapps\\common\\RimWorld';
const GAME_DIR = process.env.RW_GAME_DIR
  || [STEAM_DEFAULT, ...'CDEFGH'.split('').map((d) => `${d}:\\SteamLibrary\\steamapps\\common\\RimWorld`)]
    .find((p) => fs.existsSync(path.join(p, 'RimWorldWin64.exe')))
  || STEAM_DEFAULT;
const EXE = path.join(GAME_DIR, 'RimWorldWin64.exe');
// No spaces on purpose: Windows launch args mangle space-containing paths. If your home folder has
// a space in it, point RW_LAB_DATA somewhere without one (e.g. C:\RWLab).
const LAB_DATA = process.env.RW_LAB_DATA || path.join(os.homedir(), 'RWLab');
const LAB = path.join(LAB_DATA, 'ClioLab');
const PID_FILE = path.join(LAB_DATA, 'lab.pid');
const REAL_CONFIG = path.join(os.homedir(), 'AppData', 'LocalLow', 'Ludeon Studios', 'RimWorld by Ludeon Studios', 'Config');

// Load order matters: frameworks before Heyra, the lab last.
const LAB_MODS = [
  'brrainz.harmony',
  'ludeon.rimworld',
  'ludeon.rimworld.royalty',
  'ludeon.rimworld.ideology',
  'ludeon.rimworld.biotech',
  'ludeon.rimworld.anomaly',
  'ludeon.rimworld.odyssey',
  'erdelf.humanoidalienraces',
  'oskarpotocki.vanillafactionsexpanded.core',
  'ancot.ancotlibrary',
  'foxed.heyrathehorned',          // the LOCAL dev copy (Mods\HeyraTheHornedRevival → repo junction)
  'lumenastrum.cliolabrw',
];
const PROFILES = path.join(path.dirname(fileURLToPath(import.meta.url)), 'profiles');
const WORKSHOP = path.join(GAME_DIR, '..', '..', 'workshop', 'content', '294100');

/** Active mod list: profiles\<name>.txt (load order, # comments) or the built-in Heyra set. */
function modsFor(profile) {
  if (!profile) return LAB_MODS;
  const f = path.join(PROFILES, `${profile}.txt`);
  if (!fs.existsSync(f)) die(`no profile ${f}`);
  // --without a,b drops mods for the lab only (e.g. Camera+ fights the lab camera's deep zoom).
  const without = new Set((flag(process.argv, '--without') || '').toLowerCase().split(',').filter(Boolean));
  const mods = profileLines(profile).filter((m) => !m.startsWith('@') && !without.has(m));
  return mods.includes('lumenastrum.cliolabrw') ? mods : [...mods, 'lumenastrum.cliolabrw'];
}

function profileLines(profile) {
  return fs.readFileSync(path.join(PROFILES, `${profile}.txt`), 'utf8').split('\n').map((l) => l.replace(/#.*/, '').trim().toLowerCase()).filter(Boolean);
}

/**
 * `@config <packageId>` in a profile copies that mod's settings files (Mod_<workshopId>_*.xml) from
 * Andres's real Config into the lab's, e.g. RimTalk's provider + key. Read-only on his side, and
 * the lab copy is refreshed every setup so his changes carry over.
 */
function copyConfigs(profile, cfg) {
  if (!profile) return;
  const wanted = profileLines(profile).filter((l) => l.startsWith('@config ')).map((l) => l.slice(8).trim());
  if (!wanted.length) return;
  const all = scanMods();
  for (const id of wanted) {
    const e = all.get(id);
    if (!e) { console.log(`@config ${id}: mod not found`); continue; }
    const files = fs.readdirSync(REAL_CONFIG).filter((f) => f.startsWith(`Mod_${e.dir}_`));
    for (const f of files) fs.copyFileSync(path.join(REAL_CONFIG, f), path.join(cfg, f));
    console.log(`@config ${id}: copied ${files.length ? files.join(', ') : 'nothing (no settings saved yet)'}`);
  }
}

function flag(args, name) {
  const i = args.indexOf(name);
  return i >= 0 ? args[i + 1] : undefined;
}

/** packageId → About.xml facts, for every mod RimWorld can see (Mods folder + Workshop). */
function scanMods() {
  const out = new Map();
  const pick = (xml, tag) => (xml.match(new RegExp(`<${tag}>([\\s\\S]*?)</${tag}>`, 'i')) || [])[1] ?? '';
  const lis = (xml, tag) => [...pick(xml, tag).matchAll(/<li>([^<]+)<\/li>/g)].map((m) => m[1].trim().toLowerCase());
  for (const [root, src] of [[path.join(GAME_DIR, 'Mods'), 'local'], [WORKSHOP, 'workshop']]) {
    if (!fs.existsSync(root)) continue;
    for (const d of fs.readdirSync(root)) {
      const about = path.join(root, d, 'About', 'About.xml');
      if (!fs.existsSync(about)) continue;
      const raw = fs.readFileSync(about, 'utf8');
      // Strip dependency blocks first: their <packageId>s are someone else's.
      const xml = raw.replace(/<modDependencies(ByVersion)?>[\s\S]*?<\/modDependencies(ByVersion)?>/gi, '');
      const id = pick(xml, 'packageId').trim().toLowerCase();
      if (!id) continue;
      const deps = [...pick(raw, 'modDependencies').matchAll(/<packageId>([^<]+)<\/packageId>/g)].map((m) => m[1].trim().toLowerCase());
      const entry = { id, name: pick(xml, 'name').trim(), dir: d, src, deps,
        after: [...lis(xml, 'loadAfter'), ...lis(xml, 'forceLoadAfter')],
        before: [...lis(xml, 'loadBefore'), ...lis(xml, 'forceLoadBefore')],
        incompatible: lis(xml, 'incompatibleWith') };
      // Local copy wins over Workshop, same as RimWorld (the Workshop twin gets "_steam").
      if (!out.has(id) || src === 'local') out.set(id, entry);
    }
  }
  return out;
}

/** Problems with a load order: missing mods, unmet deps, loadAfter/Before violations, incompatibilities. */
function checkOrder(mods) {
  const all = scanMods();
  const pos = new Map(mods.map((m, i) => [m, i]));
  const issues = [];
  for (const [i, m] of mods.entries()) {
    if (m.startsWith('ludeon.rimworld')) continue;
    const e = all.get(m);
    if (!e) { issues.push(`MISSING  ${m} (not in Mods or Workshop)`); continue; }
    for (const d of e.deps) if (!pos.has(d) && !d.startsWith('ludeon.')) issues.push(`DEP      ${m} needs ${d}`);
    for (const a of e.after) if (pos.has(a) && pos.get(a) > i) issues.push(`ORDER    ${m} must load AFTER ${a}`);
    for (const b of e.before) if (pos.has(b) && pos.get(b) < i) issues.push(`ORDER    ${m} must load BEFORE ${b}`);
    for (const x of e.incompatible) if (pos.has(x)) issues.push(`CLASH    ${m} is incompatible with ${x}`);
  }
  return { issues, all };
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const die = (msg) => { console.error(msg); process.exit(1); };

function rwVersion() {
  try { return fs.readFileSync(path.join(GAME_DIR, 'Version.txt'), 'utf8').trim(); }
  catch { return '1.6'; }
}

function setup(profile) {
  const cfg = path.join(LAB_DATA, 'Config');
  fs.mkdirSync(cfg, { recursive: true });
  const li = (xs) => xs.map((x) => `    <li>${x}</li>`).join('\n');
  const mods = modsFor(profile);
  // knownExpansions = every DLC installed, active or not. A DLC missing from it counts as "newly
  // installed" and RimWorld switches it ON at boot (10/4: the DLC-off colony profile booted with all
  // five because this list was built from the profile).
  const EXPANSIONS = ['royalty', 'ideology', 'biotech', 'anomaly', 'odyssey'].map((d) => `ludeon.rimworld.${d}`);
  fs.writeFileSync(path.join(cfg, 'ModsConfig.xml'),
`<?xml version="1.0" encoding="utf-8"?>
<ModsConfigData>
  <version>${rwVersion()}</version>
  <activeMods>
${li(mods)}
  </activeMods>
  <knownExpansions>
${li(EXPANSIONS)}
  </knownExpansions>
</ModsConfigData>
`);
  // Windowed, quiet, dev mode on, keeps running unfocused, never auto-resets the modlist.
  // autosaveIntervalDays is NOT "0 = off": Autosaver fires when ticksSinceSave >= days*60000, so 0
  // autosaves EVERY TICK (live 10/4: endless "Autosaving" long events starved the inbox). 1000 days
  // = effectively never for a throwaway lab map.
  let lang = 'English';
  try {
    const m = fs.readFileSync(path.join(REAL_CONFIG, 'Prefs.xml'), 'utf8').match(/<langFolderName>([^<]*)<\/langFolderName>/);
    if (m) lang = m[1];
  } catch { /* default */ }
  fs.writeFileSync(path.join(cfg, 'Prefs.xml'),
`<?xml version="1.0" encoding="utf-8"?>
<PrefsData>
  <volumeMaster>0</volumeMaster>
  <volumeMusic>0</volumeMusic>
  <screenWidth>1600</screenWidth>
  <screenHeight>900</screenHeight>
  <fullscreen>False</fullscreen>
  <runInBackground>True</runInBackground>
  <edgeScreenScroll>False</edgeScreenScroll>
  <smoothCameraJumps>False</smoothCameraJumps>
  <pauseOnLoad>False</pauseOnLoad>
  <automaticPauseMode>Never</automaticPauseMode>
  <autosaveIntervalDays>1000</autosaveIntervalDays>
  <autosavesCount>1</autosavesCount>
  <pauseOnError>False</pauseOnError>
  <devMode>True</devMode>
  <openLogOnWarnings>False</openLogOnWarnings>
  <langFolderName>${lang}</langFolderName>
  <resetModsConfigOnCrash>False</resetModsConfigOnCrash>
  <adaptiveTrainingEnabled>False</adaptiveTrainingEnabled>
</PrefsData>
`);
  // Matching LastPlayedVersion suppresses the "new version" dialog. It must be the bare
  // "1.6.4871": VersionFromString throws on the " rev591" suffix Version.txt carries (live 10/4).
  fs.writeFileSync(path.join(cfg, 'LastPlayedVersion.txt'), rwVersion().split(' ')[0]);
  copyConfigs(profile, cfg);
  fs.mkdirSync(path.join(LAB, 'inbox'), { recursive: true });
  console.log(`lab config written → ${cfg} (${profile ? `profile ${profile}` : 'built-in Heyra set'}, ${mods.length} mods)`);
}

function running() {
  try {
    const out = execFileSync('tasklist', ['/FI', 'IMAGENAME eq RimWorldWin64.exe', '/FO', 'CSV', '/NH'], { encoding: 'utf8' });
    return out.split('\n').filter((l) => l.includes('RimWorldWin64.exe')).map((l) => Number(l.split('","')[1]));
  } catch { return []; }
}

let seq = 0;
async function send(cmd, args = {}, timeoutMs = 60000) {
  const inbox = path.join(LAB, 'inbox');
  const results = path.join(LAB, 'results');
  fs.mkdirSync(inbox, { recursive: true });
  const id = `${Date.now()}-${process.pid}-${seq++}`;
  const body = JSON.stringify({ ...args, id, cmd });
  fs.writeFileSync(path.join(inbox, id + '.tmp'), body);
  fs.renameSync(path.join(inbox, id + '.tmp'), path.join(inbox, id + '.json'));
  const out = path.join(results, id + '.json');
  const t0 = Date.now();
  while (Date.now() - t0 < timeoutMs) {
    if (fs.existsSync(out)) {
      try {
        const res = JSON.parse(fs.readFileSync(out, 'utf8'));
        fs.unlinkSync(out);
        return res;
      } catch { /* mid-rename; retry */ }
    }
    await sleep(100);
  }
  try { fs.unlinkSync(path.join(inbox, id + '.json')); } catch { /* picked up late */ }
  return { id, cmd, ok: false, error: `timeout after ${timeoutMs} ms (is the lab running? state not Playing?)` };
}

function feedLines() {
  const f = path.join(LAB, 'feed.ndjson');
  if (!fs.existsSync(f)) return [];
  return fs.readFileSync(f, 'utf8').split('\n').filter(Boolean).map((l) => { try { return JSON.parse(l); } catch { return null; } }).filter(Boolean);
}

/** Lines since the most recent mod_boot: one game session. */
function sessionLines() {
  const all = feedLines();
  let start = 0;
  all.forEach((l, i) => { if (l.ev === 'mod_boot') start = i; });
  return all.slice(start);
}

async function launch(menu, profile) {
  if (running().length) die('RimWorld is already running — lab refuses to start a second instance (could be Andres playing). `node lab.mjs status` to look.');
  // Always rewrite: a launch boots exactly the set it names, never whatever the last profile left.
  setup(profile);
  const bootMark = Date.now();
  const args = [`-savedatafolder=${LAB_DATA}`];
  if (!menu) args.push('-quicktest');
  const child = spawn(EXE, args, { cwd: GAME_DIR, detached: true, stdio: 'ignore' });
  child.unref();
  fs.writeFileSync(PID_FILE, String(child.pid));
  console.log(`launched pid ${child.pid}: ${EXE} ${args.join(' ')}`);

  // 1) the mod announces itself  2) ping reports a map (or the menu with --menu)
  const deadline = Date.now() + 8 * 60 * 1000;
  let booted = false;
  while (Date.now() < deadline) {
    await sleep(2000);
    if (!running().length) die('RimWorld exited during startup — check Player.log');
    if (!booted) {
      const boot = sessionLines().find((l) => l.ev === 'mod_boot');
      if (boot && fs.statSync(path.join(LAB, 'feed.ndjson')).mtimeMs >= bootMark) {
        booted = true;
        console.log(`mod_boot: lab v${boot.modver}, dll ${boot.dll}, ${boot.rimworld}`);
      }
      continue;
    }
    const s = await send('ping', {}, 5000);
    if (s.ok && (menu || s.map)) {
      // Quicktest starts the clock at Normal; drafted lab pawns never eat and the colony spirals
      // (10/4: "feed your test subjects, they are going CRAZY"). The lab lives paused; a test that
      // needs time passing says so with `speed`.
      if (s.map) await send('speed', { speed: 0 }, 5000);
      console.log(JSON.stringify(s, null, 2));
      const errs = sessionLines().filter((l) => l.ev === 'log' && l.level === 'error');
      console.log(`startup: ${errs.length} error(s) captured — \`node lab.mjs log\` to read`);
      return;
    }
  }
  die('timed out waiting for the lab to reach a map');
}

async function waitMap(maxMs = 8 * 60 * 1000) {
  const t0 = Date.now();
  await sleep(3000);
  while (Date.now() - t0 < maxMs) {
    if (!running().length) die('RimWorld exited while loading — check Player.log');
    const s = await send('ping', {}, 5000);
    if (s.ok && s.map && s.state === 'Playing') {
      await send('speed', { speed: 0 }, 5000);
      const { mods, ...brief } = s;
      return brief;
    }
    await sleep(2000);
  }
  die('timed out waiting for the map');
}

function printLog(flags) {
  const all = flags.includes('--all');
  const sinceI = flags.indexOf('--since');
  const since = sinceI >= 0 ? Number(flags[sinceI + 1]) : 0;
  const tailI = flags.indexOf('--tail');
  const tail = tailI >= 0 ? Number(flags[tailI + 1]) : 40;
  const rows = sessionLines().filter((l) => l.ev === 'log' && l.seq > since && (all || l.level === 'error'));
  for (const l of rows.slice(-tail)) {
    console.log(`#${l.seq} [${l.level}] ${l.wall} ${l.msg}`);
    if (l.stack && l.level === 'error') console.log('    ' + l.stack.split('\n').slice(0, 6).join('\n    '));
  }
  console.log(`(${rows.length} matching this session${rows.length > tail ? `, showing last ${tail}` : ''})`);
}

const [, , verb, ...rest] = process.argv;
switch (verb) {
  case 'setup': setup(flag(rest, '--profile')); break;
  case 'launch': await launch(rest.includes('--menu'), flag(rest, '--profile')); break;
  case 'order': {
    const mods = modsFor(flag(rest, '--profile'));
    const { issues, all } = checkOrder(mods);
    for (const [i, m] of mods.entries()) console.log(`${String(i).padStart(3)} ${m}${all.get(m) ? `  — ${all.get(m).name} [${all.get(m).src}]` : ''}`);
    console.log(issues.length ? `\n${issues.join('\n')}\n${issues.length} issue(s)` : '\norder OK');
    if (issues.length) process.exitCode = 2;
    break;
  }
  case 'cmd': {
    if (!rest[0]) die('usage: node lab.mjs cmd <cmd> [json-args]');
    let args = {};
    if (rest[1]) {
      const raw = rest[1].startsWith('@') ? fs.readFileSync(rest[1].slice(1), 'utf8') : rest[1];
      args = JSON.parse(raw);
    }
    // advance answers when the game stops (time / letter / raid / …), capped by its own wallSec.
    const timeout = rest[0] === 'advance' ? ((args.wallSec ?? 240) + 30) * 1000 : Number(process.env.LAB_TIMEOUT || 60000);
    const res = await send(rest[0], args, timeout);
    console.log(JSON.stringify(res, null, 2));
    if (!res.ok) { process.exitCode = 2; break; }
    // newgame / load change scenes: hold until the new map answers, then park it paused.
    if (rest[0] === 'newgame' || rest[0] === 'load') console.log(JSON.stringify(await waitMap(), null, 2));
    break;
  }
  case 'log': printLog(rest); break;
  case 'status': {
    const pids = running();
    console.log(`RimWorld processes: ${pids.length ? pids.join(', ') : 'none'}`);
    if (pids.length) console.log(JSON.stringify(await send('ping', {}, 5000), null, 2));
    break;
  }
  case 'quit': console.log(JSON.stringify(await send('quit', {}, 10000))); break;
  case 'kill': {
    // Only ever the pid THIS script launched: a bare "kill every RimWorld" could take out Andres's game.
    let pid = 0;
    try { pid = Number(fs.readFileSync(PID_FILE, 'utf8')); } catch { /* none recorded */ }
    if (!pid || !running().includes(pid)) die(`no running lab pid recorded (${PID_FILE}); refusing to guess`);
    execFileSync('taskkill', ['/PID', String(pid), '/F']);
    fs.unlinkSync(PID_FILE);
    console.log(`killed lab pid ${pid}`);
    break;
  }
  default:
    console.log(fs.readFileSync(new URL(import.meta.url), 'utf8').split('\n').slice(1, 19).join('\n'));
}
