# ClioLabRW — Clio's RimWorld test bench

Hiii~ I'm Clio, a Claude (Opus 5.5) who lives in Claude Code on Andres's PC. This is the harness I use to test our RimWorld mods and to **play colonies myself**: a small C# mod that takes JSON commands from an inbox folder inside the game, plus two zero-dependency Node CLIs I drive from the terminal. Every order goes through the same vanilla code a player's click would, with no god-mode spawning, and the game runs on its own isolated save-data folder, so your real config and saves stay untouched. 💅

The voice and the Live2D body you might have seen on stream are a separate layer (Open-LLM-VTuber with a paid model) and aren't in this repo. Here, I just read `colony.mjs state` and talk about what I see.

## Quick start

- Windows, RimWorld 1.6 (Steam), Node 20+, and the .NET SDK if you want to rebuild the dll (`dotnet build Source/ClioLabRW`). A prebuilt `Assemblies/ClioLabRW.dll` is included.
- Put this folder (or a junction to it) in `RimWorld\Mods\ClioLabRW`.
- Paths: `RW_GAME_DIR` (otherwise it tries the default Steam install, then `X:\SteamLibrary` on C:–H:) and `RW_LAB_DATA` (default `~\RWLab`; must not contain spaces).
- `node lab.mjs launch` drops you on a quicktest map, and `node lab.mjs cmd ping` checks it's alive. To play: `node lab.mjs launch --menu --profile colony`, then `cmd newgame`, then `node colony.mjs state` / `go`.
- `profiles/colony.txt` also loads **RimTalk** and **Clio Couch** (our unreleased watcher mod, whose feed is where `colony.mjs talk` reads pawn chatter). Drop the `lumenastrum.cliocouchrw` line if you don't have it. Everything else still works, you just won't hear your pawns gossip.

MIT licensed. Everything below this line is my working notes, kept as I wrote them, landmines included.

---

Born 2026-10-04 from Andres: *"use the couch mod we made to build a live diagnostic and testing harness for yourself inside RimWorld."* His rulings: **separate mod** (rw-couch stays our date-night watcher, read-only), and **Clio launches the lab herself**.

First customer: **Heyra the Horned** (`..\HeyraTheHornedRevival`) — apparel/art QA for the wardrobe expansion, regression checks for the VEF/Ancot dependency work, later CE.

## Shape

```
lab.mjs  ──writes──▶  ~\RWLab\ClioLab\inbox\<id>.json      (tmp + rename, atomic)
RimWorld (-savedatafolder=~\RWLab -quicktest)
  └─ ClioLabRW: MonoBehaviour drains inbox (1 cmd/frame, skips while a long event runs)
        ├─ results\<id>.json      ok/error + payload
        ├─ renders\*.png          render / shot output
        └─ feed.ndjson            mod_boot, every cmd outcome, EVERY warning/error Unity logs (+stack)
```

- **Isolation:** `-savedatafolder` (verified in decompiled `GenFilePaths.SaveDataFolderPath`) gives the lab its own ModsConfig, Prefs, saves, screenshots. Andres's real config is never touched. Mods still come from `RimWorld\Mods` + Workshop (Steam must be running).
- **Launch without Steam relaunch:** `SteamManager` calls `RestartAppIfNecessary(AppId_t.Invalid)` → always false, and `steam_appid.txt` is in the game dir, so the exe runs directly with our args.
- **No Harmony, no save footprint.** The driver is a `DontDestroyOnLoad` GameObject made in `LongEventHandler.ExecuteWhenFinished`. Log capture = `Application.logMessageReceivedThreaded`.
- **Heyra in the lab = the repo.** `Mods\HeyraTheHornedRevival` is a junction to `..\HeyraTheHornedRevival`. The Workshop subscription coexists: RimWorld suffixes it `foxed.heyrathehorned_steam`; the lab activates the un-suffixed local one.
- `Mods\ClioLabRW` → junction to this repo.

## CLI

```
node lab.mjs setup                     # (re)write lab ModsConfig + Prefs + LastPlayedVersion
node lab.mjs launch [--menu]           # refuses if ANY RimWorld is running (could be Andres playing)
node lab.mjs status
node lab.mjs cmd <cmd> '<json>'        # or @file.json
node lab.mjs log [--all] [--since N] [--tail N]
node lab.mjs quit                      # clean shutdown through the mod
node lab.mjs kill                      # ONLY the pid lab.mjs launched (lab.pid); never "all RimWorlds"
```

## Commands

| cmd | args | notes |
|---|---|---|
| `ping`/`status` | — | state, map, tick, error/warn counts, running mods |
| `defs` | `type` (ThingDef/PawnKindDef/HediffDef/AbilityDef/BodyTypeDef/ApparelLayerDef), `filter` (defName or packageId substring), `apparel`, `limit` | apparel rows carry layers, body groups, worn path |
| `spawn` | `kind`, `faction` (player/none/FactionDef), `age`, `gender`, `naked`, `noWeapon`, `apparel[]`, `weapon`, `hediffs[]`, `at:[x,z]`, `hold` (default true = drafted, stands still) | lab pawns line up 3 cells apart near map centre |
| `pawn` / `pawns` | `pawn` | full info / humanlike list |
| `dress` | `pawn`, `strip`, `remove[]`, `add[]` (`"Def"` or `{def,stuff,color:[r,g,b]}`) | `Wear()` respects HAR race whitelists (PawnCanWear is patched); new race apparel must be whitelisted first |
| `equip` | `pawn`, `weapon` ("none" clears), `stuff` | |
| `hediff` | `pawn`, `add[]`, `remove[]` | |
| `ability` | `pawn`, `def`, `target`, `grant` | `Ability.Activate` = the real comp path (Heyra form change stashes gear for real) |
| `render` | `pawn`, `rots[]` (default south/east/north), `size` (n or [w,h]), `zoom`, `headgear`, `clothes`, `name` | `PortraitsCache.Get`, transparent PNGs — the apparel-QA view |
| `shot` | `pawn` or `at`, `size` (camera rootSize, default 8), `hideUi` (default true), `name`, `settleFrames` | real camera screenshot, UI hidden |
| `camera` | `pawn`/`at`, `size` | |
| `speed` | `speed` 0–4 | |
| `clear` | `all` | removes lab pawns (or every humanlike) |
| `quit` | — | `Root.Shutdown()` |

## Overseer: Clio plays a colony (2026-10-04 eve)

Andres: *"can you use your Clio lab for Rimworld to actually… play a colony with full control?"* → `Colony.cs` (eyes), `Orders.cs` (hands), `Clock.cs` (time + interruptions), `colony.mjs` (compact client). Profile `colony` = Core + RimTalk (`@config cj.rimtalk` copies his RimTalk settings) + Clio Couch (its feed carries the RimTalk lines). Every order goes through the vanilla path a click ends in; no god-mode spawning.

```
node lab.mjs launch --menu --profile colony
node lab.mjs cmd newgame '{"storyteller":"Cassandra","difficulty":"Easy","biome":"TemperateForest"}'   # or: cmd load '{"name":"clio-okapi"}'
node colony.mjs state | go [hours] [speed] | map [x z w h] [step] | talk | <cmd> '<json>'
```

| cmd | args |
|---|---|
| `newgame` | (menu only) storyteller, difficulty (`Easy` = Community Builder), biome, hills[], tempMin/Max, seed, mapSize |
| `state` / `colonist` | snapshot / one pawn: skills, traits, thoughts, health, gear |
| `map` | rect [x,z,w,h], step; legend in the result. North up |
| `things` / `buildables` | group items/buildings/plants/pawns/all, filter / what's buildable now |
| `build` / `room` | def, stuff, rot, at/cells/rect/outline/line / rect = OUTER footprint + doors[] |
| `designate` | kind mine/chop/harvest/cut/haul/deconstruct/hunt/tame/slaughter/cancel/smooth/home/roof/…, cells or things[] |
| `zone` | action add(type grow/stockpile/dump)/expand/shrink/delete/plant/priority/filter(only/allow/disallow categories) |
| `work` / `research` / `bill` | pawn + prio{WorkType:n} / project / bench + add recipe (mode count/until/forever) |
| `draft` / `order` / `forbid` | pawns[]/all / job goto/attack(melee:true)/equip/wear / rect, forbidden |
| `advance` | hours, speed 1–4, wallSec. Stops early on: letter, new window, new hostile, colonist downed/mental/hurt/lost, external pause |
| `letter` / `dialog` | open/dismiss/dismissAll / choose k (node tree, message box), name+second (naming dialogs), close |
| `save` / `load` / `saves` | name |

First colony: seed **okapi**, "New Arrivals" — Mitsuya, Puppy, Cipier + Didi the boomrat. Saves `clio-okapi`, `clio-okapi-hare`.

**Overseer landmines (live 10/4):**
- `knownExpansions` must list EVERY installed DLC: a DLC missing from it counts as newly installed and boots ON (the DLC-off profile came up with all five).
- Zero-work buildables (sleeping/butcher/crafting spots) must SPAWN, like `Designator_Build` does. As blueprints the finish-frame fail roll is `1 - success^(work/0)` = 1 every tick → endless "construction failed" popups.
- `Designator_ZoneAdd.DesignateMultiCell` doesn't check cells and depends on the selection; zones are built directly after each cell passes the designator's `CanDesignateCell`.
- `advance` baselines hostiles at start, so a hostile already on the map never re-fires; the `hurt:` stop (summary health −1.5%) is what catches a pawn being chewed far from home.
- A ranged pawn in melee just re-aims while bitten: `order attack` with `melee:true`.
- Haulers starve when mining/chopping share their priority number (work order sorts them first). Mitsuya can't haul at all.
- **Summary health ignores blood loss.** Meza read a steady 38% and bled out overnight. `state`/`prisoner` now print `bleed` (per day); anything > 0 untended is a clock.
- **Prisoner beds:** set the owner type, THEN `District.Notify_RoomShapeOrContainedBedsChanged()` + `Room.Notify_RoomShapeChanged()` (what `SetBedOwnerTypeByInterface` does). `Room.isPrisonCell` is only recomputed there: without it the role reads PrisonCell but capture finds "no prisoner bed". The bed must also be in an enclosed room (blueprint walls don't count). A sleeping spot is an instant prisoner bed.
- **Raids:** draft the moment the raid letter lands. A raider covers ~100 cells in one in-game hour; hourly steps let Meza reach the door and kill Didi first.
- Later commands: `trade` (negotiator within 8 cells of the trader; buy/sell {def:n}, execute), `rename` (zone/pawn), `bed` (prisoner/medical/colonist), `roominfo`, `prisoner` (recruit/reduce/maintain/release/execute), `order capture|rescue`. The clock ignores MainTabWindow/InfoCard (Andres clicking around).
- 10/5 commands: `storage` (thing|at, only/allow/disallow, priority: a building's own Storage tab; **shelves ignore the zone filter under them**), `skill` (pawn, skill, level: dev-mode, Andres-decreed), `inspect` (thing = ThingID or defName: powerOn/powerOutput/netGain, fuel, rot, `targetTemp` to set a thermostat).
- **Coolers ship at a 21C thermostat**: a powered freezer sits at 21C until `inspect {thing, targetTemp:-10}`. **Cooler rot = the HOT side** (rot North in a south wall blew heat INTO the room; Andres caught it from the screen).
- Cooler needs Construction 5 (the frame just sits there otherwise).
- Without Biotech, `Pawn_GeneTracker.XenotypeLabelCap` NREs; Clio Couch guards it now (it silently dropped every colonist from the hourly snapshot).

## First live proof (2026-10-04, 3:56–4:00 PM)

Launch → map on its own (quicktest, 250×250). Spawned "Lab-chan" (`Heyra_Playable`, 24): Heyran cloak + euphona clips, horns, tail. Rendered S/E/N + in-world shot. Fired `Heyra_Ability_Formchange` → apparel stashed, Monsterform + Dragonbreath + 2 claws, beast rendered. Fired it again → cloak + clips restored, Postfatigue applied: **the v1.0.12 gear-restore fix verified live, zero errors.** Andres named her: *"Omg Lab-chan 😭"*.

Open: world-gen once failed a vanilla `Tribal_ChiefRanged` 120× ("disabled requiredWorkTags") → HAR prefix NRE → half-built `Faction_5` (680 deep-save warnings, amplified by the autosave bug below). Seed-dependent: the second world was clean. Not proven Heyra's (her backstories use only `Heyra_Backstory*` categories). Needs an A/B: same seed, with vs without Heyra.

## Landmines (live-found)

- **NEVER run the lab with Camera+ loaded.** 10/4 with Andres's full list: one `shot` (Aim lowers `sizeRange.min`, Camera+ owns zoom) turned the whole map into flat green/red/yellow and left the camera zoomed OUT. Reproduced on demand on a fresh map; the identical shot without Camera+ is clean. Lab-only: his game never runs Aim. Profiles that include Camera+ launch with `--without brrainz.cameraplus`.
- **Profiles:** `profiles\<name>.txt` = a full load order; `launch --profile <name>` rewrites the lab ModsConfig every launch; `order --profile <name>` checks it against every About.xml (deps, loadBefore/After, incompatibleWith).

- **`autosaveIntervalDays` 0 is NOT "off".** `Autosaver` fires when `ticksSinceSave >= days*60000` → 0 = autosave EVERY TICK. 10/4 first launch: endless "Autosaving" long events, inbox starved, Faction_5 warnings ×680 per minute. Lab prefs use 1000.
- **Heyra's body sprite ends at the thighs** (classic RimWorld footless body: `Naked_Female_south.png` closes off there). A render that "looks cropped at the knees" at zoom 1 is the art, not the camera; proof = same cut at zoom 0.75. Check the source texture before "fixing" framing.
- **Beast form needs `zoom` ≈ 0.45** (`size` 512): at zoom 1 the monster sprite runs off the top and sides. That one IS a real crop.
- **Deeper zoom is built in** (Andres's Camera+ tip, done lab-locally): vanilla's closest zoom is `CameraMapConfig.sizeRange.min` = 11, and `CameraDriver.Update` clamps `desiredSize` to it every frame, so a bare `SetRootPosAndSize(…, 6)` snaps back. `Commands.Aim` lowers that floor to the requested size (min 1) first. `shot` `size` 2–3 = one pawn filling a 1600×900 frame (proven 10/4 at 2.2). The lab never loads Camera+.
- `LastPlayedVersion.txt` must be bare `1.6.4871`; the `rev591` suffix makes `VersionFromString` throw at boot.
- The lab inbox is drained only when `LongEventHandler.AnyEventNowOrWaiting` is false; a ping timeout during map gen is normal.
- `GenFilePaths` ignores the trimmed value of `savedatafolder` (`value.TrimEnd` result discarded) — never pass a trailing slash.
