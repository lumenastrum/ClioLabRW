using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

// The command set. Each inbox file is {"id": "...", "cmd": "...", ...args}. A handler returns its
// payload Json (written immediately as results/<id>.json with ok:true) or null when it finishes on
// a later frame and writes its own result (render, shot). Any exception becomes ok:false + error.
//
// Pawn references ("pawn": ...) accept a ThingID ("Human1234"), a thingIDNumber, or a short name.

namespace ClioLabRW
{
    internal static class Commands
    {
        /// <summary>Pawns the lab spawned this session, so "clear" can tidy up after itself.</summary>
        private static readonly List<Pawn> LabPawns = new List<Pawn>();
        private static int _spawnSlot;

        public static void Handle(string file)
        {
            string id = Path.GetFileNameWithoutExtension(file);
            string text;
            try
            {
                text = File.ReadAllText(file);
                File.Delete(file);
            }
            catch (Exception)
            {
                return;   // still being written or locked; next poll gets it
            }

            string cmd = "?";
            try
            {
                Args a = Args.Of(Json.Parse(text));
                id = a.Str("id", id);
                cmd = a.Str("cmd", "?");
                Json payload = Dispatch(id, cmd, a);
                if (payload != null) Finish(id, cmd, payload);
            }
            catch (Exception e)
            {
                Fail(id, cmd, e is LabError ? e.Message : e.GetType().Name + ": " + e.Message, e is LabError ? null : e.StackTrace);
            }
        }

        public static void Finish(string id, string cmd, Json payload)
        {
            string body = payload.Str("id", id).Str("cmd", cmd).Bool("ok", true).ToString();
            LabIO.WriteResult(id, body);
            LabIO.Feed(new Json().Str("ev", "cmd").Str("id", id).Str("cmd", cmd).Bool("ok", true).ToString());
        }

        public static void Fail(string id, string cmd, string error, string stack = null)
        {
            LabIO.WriteResult(id, new Json().Str("id", id).Str("cmd", cmd).Bool("ok", false).Str("error", error).Str("stack", stack).ToString());
            LabIO.Feed(new Json().Str("ev", "cmd").Str("id", id).Str("cmd", cmd).Bool("ok", false).Str("error", error).ToString());
        }

        private static Json Dispatch(string id, string cmd, Args a)
        {
            switch (cmd)
            {
                case "ping": return Status();
                case "status": return Status();
                case "defs": return Defs(a);
                case "spawn": return Spawn(a);
                case "pawn": return PawnInfo(FindPawn(a.Str("pawn")));
                case "pawns": return Pawns();
                case "dress": return Dress(a);
                case "equip": return Equip(a);
                case "hediff": return Hediffs(a);
                case "ability": return UseAbility(a);
                case "render": Render.Portraits(id, cmd, FindPawn(a.Str("pawn")), a); return null;
                case "shot": Render.Shot(id, cmd, a); return null;
                case "camera": return Camera(a);
                case "incident": return FireIncident(a);
                case "care": return Care(a);
                case "letters": return Letters(a);
                case "speed": return Speed(a);
                case "clear": return Clear(a);
                case "windows": return Windows(a);

                // Overseer: playing a colony (Colony.cs / Orders.cs / Clock.cs)
                case "newgame": return Colony.NewGame(a);
                case "state": return Colony.State(a);
                case "colonist": return Colony.ColonistFull(a);
                case "map": return Colony.MapAscii(a);
                case "things": return Colony.Things(a);
                case "buildables": return Colony.Buildables(a);
                case "build": return Orders.Build(a);
                case "room": return Orders.Room(a);
                case "designate": return Orders.Designate(a);
                case "zone": return Orders.ZoneCmd(a);
                case "work": return Orders.Work(a);
                case "research": return Orders.Research(a);
                case "bill": return Orders.Bill(a);
                case "draft": return Orders.Draft(a);
                case "order": return Orders.Order(a);
                case "forbid": return Orders.Forbid(a);
                case "advance": Clock.Start(id, cmd, a); return null;
                case "letter": return Clock.Letters(a);
                case "dialog": return Clock.Dialog(a);
                case "save": return Clock.Save(a);
                case "load": return Clock.Load(a);
                case "saves": return Clock.Saves();
                case "trade": return Trade.Run(a);
                case "rename": return Trade.Rename(a);
                case "bed": return Orders.Bed(a);
                case "roominfo": return Orders.RoomInfo(a);
                case "prisoner": return Orders.Prisoner(a);
                case "storage": return Orders.Storage(a);
                case "skill": return Orders.Skill(a);
                case "inspect": return Orders.Inspect(a);
                case "quit":
                    LabDriver.After(10, Root.Shutdown);
                    return new Json().Str("note", "shutting down");
                default: throw new LabError("unknown cmd '" + cmd + "'");
            }
        }

        // ------------------------------------------------------------------ state

        private static Json Status()
        {
            Map map = Current.ProgramState == ProgramState.Playing ? Find.CurrentMap : null;
            Json j = new Json()
                .Str("modver", LabBoot.Version)
                .Str("dll", LabBoot.DllStamp())
                .Str("state", Current.ProgramState.ToString())
                .Bool("map", map != null)
                .Num("errors", LogTap.Errors)
                .Num("warnings", LogTap.Warnings)
                .Num("frame", Time.frameCount)
                .Str("playerFaction", Current.ProgramState == ProgramState.Playing ? Faction.OfPlayerSilentFail?.def.defName : null);
            if (map != null)
            {
                j.Num("tick", Find.TickManager.TicksGame)
                 .Str("speed", Find.TickManager.CurTimeSpeed.ToString())
                 .Str("mapSize", map.Size.x + "x" + map.Size.z)
                 .Num("pawnsOnMap", map.mapPawns.AllPawnsSpawnedCount)
                 .Num("labPawns", LabPawns.Count(p => p != null && p.Spawned));
            }
            j.Strs("mods", LoadedModManager.RunningModsListForReading.Select(m => m.PackageId));
            return j;
        }

        private static Json Defs(Args a)
        {
            string type = a.Str("type", "ThingDef");
            string filter = a.Str("filter", "");
            bool apparelOnly = a.Bool("apparel");
            int limit = a.Int("limit", 400);
            IEnumerable<Def> all;
            switch (type)
            {
                case "PawnKindDef": all = DefDatabase<PawnKindDef>.AllDefs; break;
                case "HediffDef": all = DefDatabase<HediffDef>.AllDefs; break;
                case "AbilityDef": all = DefDatabase<AbilityDef>.AllDefs; break;
                case "BodyTypeDef": all = DefDatabase<BodyTypeDef>.AllDefs; break;
                case "ApparelLayerDef": all = DefDatabase<ApparelLayerDef>.AllDefs; break;
                case "ThingDef": all = DefDatabase<ThingDef>.AllDefs.Where(d => !apparelOnly || d.IsApparel); break;
                default: throw new LabError("defs type must be ThingDef/PawnKindDef/HediffDef/AbilityDef/BodyTypeDef/ApparelLayerDef");
            }
            List<string> rows = new List<string>();
            foreach (Def d in all)
            {
                if (filter.Length > 0 && d.defName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0
                    && (d.modContentPack == null || d.modContentPack.PackageId.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                if (rows.Count >= limit) break;
                Json r = new Json().Str("def", d.defName).Str("label", d.label).Str("mod", d.modContentPack?.PackageId);
                if (d is ThingDef td && td.IsApparel)
                {
                    r.Strs("layers", td.apparel.layers.Select(l => l.defName))
                     .Strs("groups", td.apparel.bodyPartGroups.Select(g => g.defName))
                     .Str("worn", td.apparel.wornGraphicPath)
                     .Bool("stuff", td.MadeFromStuff);
                }
                if (d is PawnKindDef pk) r.Str("race", pk.race?.defName);
                rows.Add(r.ToString());
            }
            return new Json().Str("type", type).Num("count", rows.Count).Raw("defs", Json.Array(rows));
        }

        private static Json Pawns()
        {
            Map map = RequireMap();
            List<string> rows = new List<string>();
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
            {
                if (!p.RaceProps.Humanlike) continue;
                rows.Add(new Json().Str("id", p.ThingID).Str("name", p.LabelShort).Str("race", p.def.defName)
                    .Str("kind", p.kindDef?.defName).Bool("lab", LabPawns.Contains(p)).ToString());
            }
            return new Json().Raw("pawns", Json.Array(rows));
        }

        // ------------------------------------------------------------------ pawns

        private static Json Spawn(Args a)
        {
            Map map = RequireMap();
            PawnKindDef kind = Need<PawnKindDef>(a.Str("kind"), "kind");
            Faction faction = ResolveFaction(a.Str("faction", "player"));

            Gender? gender = null;
            if (a.Has("gender")) gender = a.Str("gender") == "male" ? Gender.Male : Gender.Female;
            float? age = a.Has("age") ? a.Num("age") : (float?)null;
            DevelopmentalStage stage = age.HasValue && age.Value < 13f
                ? (age.Value < 3f ? DevelopmentalStage.Baby : DevelopmentalStage.Child)
                : DevelopmentalStage.Adult;

            PawnGenerationRequest req = new PawnGenerationRequest(kind, faction,
                context: PawnGenerationContext.NonPlayer,
                forceGenerateNewPawn: true,
                canGeneratePawnRelations: false,
                allowAddictions: false,
                fixedBiologicalAge: age,
                fixedChronologicalAge: age,
                fixedGender: gender,
                developmentalStages: stage,
                dontGiveWeapon: a.Bool("noWeapon"),
                forceNoGear: a.Bool("naked"));
            Pawn pawn = PawnGenerator.GeneratePawn(req);

            IntVec3 cell = SpawnCell(map, a);
            GenSpawn.Spawn(pawn, cell, map, Rot4.South);
            LabPawns.Add(pawn);

            if (a.Bool("naked")) pawn.apparel?.DestroyAll();
            ApplyApparel(pawn, a.List("apparel"));
            if (a.Has("weapon")) SetWeapon(pawn, a.Str("weapon"), a.Str("weaponStuff"));
            foreach (object h in a.List("hediffs")) AddHediff(pawn, Convert.ToString(h));
            if (a.Has("name") && pawn.Name is NameTriple nt)
                pawn.Name = new NameTriple(nt.First, a.Str("name"), nt.Last);

            // Drafted colonists stand still for the camera instead of wandering off to haul.
            if (a.Bool("hold", true) && pawn.drafter != null && pawn.Faction == Faction.OfPlayer)
                pawn.drafter.Drafted = true;
            pawn.Rotation = Rot4.South;

            Dirty(pawn);
            return PawnInfo(pawn);
        }

        private static IntVec3 SpawnCell(Map map, Args a)
        {
            List<object> at = a.List("at");
            if (at.Count == 2)
                return new IntVec3(Convert.ToInt32(at[0]), 0, Convert.ToInt32(at[1]));
            // A tidy row near the map centre, 3 cells apart, so lab pawns never overlap in shots.
            IntVec3 centre = map.Center;
            for (int tries = 0; tries < 60; tries++)
            {
                int slot = _spawnSlot++;
                IntVec3 c = centre + new IntVec3((slot % 10) * 3 - 15, 0, -(slot / 10) * 4);
                if (c.InBounds(map) && c.Standable(map) && c.GetFirstPawn(map) == null) return c;
            }
            if (CellFinder.TryFindRandomCellNear(centre, map, 20, c => c.Standable(map) && c.GetFirstPawn(map) == null, out IntVec3 r))
                return r;
            throw new LabError("no free standable cell near the map centre");
        }

        private static Json Dress(Args a)
        {
            Pawn pawn = FindPawn(a.Str("pawn"));
            if (pawn.apparel == null) throw new LabError(pawn.LabelShort + " cannot wear apparel");
            if (a.Bool("strip")) pawn.apparel.DestroyAll();
            foreach (object r in a.List("remove"))
            {
                string defName = Convert.ToString(r);
                foreach (Apparel ap in pawn.apparel.WornApparel.Where(x => x.def.defName == defName).ToList())
                {
                    pawn.apparel.Remove(ap);
                    ap.Destroy();
                }
            }
            ApplyApparel(pawn, a.List("add"));
            Dirty(pawn);
            return PawnInfo(pawn);
        }

        /// <summary>Items are "DefName" or {"def","stuff","color":[r,g,b] (0-1 or 0-255)}.</summary>
        private static void ApplyApparel(Pawn pawn, List<object> items)
        {
            foreach (object item in items)
            {
                Args spec = item is string s ? new Args(new Dictionary<string, object> { { "def", s } }) : Args.Of(item);
                ThingDef def = Need<ThingDef>(spec.Str("def"), "apparel def");
                if (!def.IsApparel) throw new LabError(def.defName + " is not apparel");
                ThingDef stuff = def.MadeFromStuff ? (spec.Has("stuff") ? Need<ThingDef>(spec.Str("stuff"), "stuff") : GenStuff.DefaultStuffFor(def)) : null;
                Apparel ap = (Apparel)ThingMaker.MakeThing(def, stuff);
                if (spec.Has("color")) ap.SetColor(ParseColor(spec.List("color")));
                // Wear() does NOT bypass HAR: PawnCanWear is patched with the race whitelist (10/4: "Lab tried to
                // wear Heyra_PilotUnderwear_SVG but is not allowed to"). New race apparel must be whitelisted.
                pawn.apparel.Wear(ap, dropReplacedApparel: false);
                // Wear() refuses with a log warning, not an exception; a refused garment must fail
                // the command, never return ok with nothing on.
                if (!pawn.apparel.WornApparel.Contains(ap))
                {
                    if (!ap.Destroyed) ap.Destroy();
                    throw new LabError(pawn.LabelShort + " could not wear " + def.defName
                        + " (refused by PawnCanWear: race whitelist, body parts or life stage)");
                }
            }
        }

        private static Json Equip(Args a)
        {
            Pawn pawn = FindPawn(a.Str("pawn"));
            SetWeapon(pawn, a.Str("weapon"), a.Str("stuff"));
            Dirty(pawn);
            return PawnInfo(pawn);
        }

        private static void SetWeapon(Pawn pawn, string defName, string stuffName)
        {
            if (pawn.equipment == null) throw new LabError(pawn.LabelShort + " has no equipment tracker");
            pawn.equipment.DestroyAllEquipment();
            if (string.IsNullOrEmpty(defName) || defName == "none") return;
            ThingDef def = Need<ThingDef>(defName, "weapon");
            ThingDef stuff = def.MadeFromStuff ? (stuffName != null ? Need<ThingDef>(stuffName, "stuff") : GenStuff.DefaultStuffFor(def)) : null;
            pawn.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(def, stuff));
        }

        private static Json Hediffs(Args a)
        {
            Pawn pawn = FindPawn(a.Str("pawn"));
            foreach (object h in a.List("add"))
            {
                // "Def" or {"def","part"}: part = BodyPartDef defName or label ("horn", "Head").
                if (h is string s) { AddHediff(pawn, s); continue; }
                Args spec = Args.Of(h);
                HediffDef def = Need<HediffDef>(spec.Str("def"), "hediff");
                BodyPartRecord part = null;
                if (spec.Has("part"))
                {
                    string want = spec.Str("part");
                    part = pawn.health.hediffSet.GetNotMissingParts().FirstOrDefault(r =>
                        string.Equals(r.def.defName, want, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(r.Label, want, StringComparison.OrdinalIgnoreCase));
                    if (part == null) throw new LabError(pawn.LabelShort + " has no (unmissing) part '" + want + "'");
                }
                pawn.health.AddHediff(def, part);
            }
            foreach (object h in a.List("remove"))
            {
                HediffDef def = Need<HediffDef>(Convert.ToString(h), "hediff");
                foreach (Hediff hd in pawn.health.hediffSet.hediffs.Where(x => x.def == def).ToList())
                    pawn.health.RemoveHediff(hd);
            }
            Dirty(pawn);
            return PawnInfo(pawn);
        }

        private static void AddHediff(Pawn pawn, string defName)
        {
            pawn.health.AddHediff(Need<HediffDef>(defName, "hediff"));
        }

        private static Json UseAbility(Args a)
        {
            Pawn pawn = FindPawn(a.Str("pawn"));
            AbilityDef def = Need<AbilityDef>(a.Str("def"), "ability");
            if (pawn.abilities == null) throw new LabError(pawn.LabelShort + " has no ability tracker");
            Ability ability = pawn.abilities.GetAbility(def);
            if (ability == null)
            {
                if (!a.Bool("grant")) throw new LabError(pawn.LabelShort + " lacks " + def.defName + " (pass grant:true to add it)");
                pawn.abilities.GainAbility(def);
                ability = pawn.abilities.GetAbility(def);
            }
            LocalTargetInfo target = a.Has("target") ? new LocalTargetInfo(FindPawn(a.Str("target"))) : new LocalTargetInfo(pawn);
            // Activate() skips the warmup job and runs the comps directly: the same effect code a
            // player's click ends in, minus the walking and aiming.
            bool fired = ability.Activate(target, target);
            Dirty(pawn);
            return PawnInfo(pawn).Bool("fired", fired);
        }

        private static Json Clear(Args a)
        {
            Map map = RequireMap();
            int n = 0;
            List<Pawn> doomed = a.Bool("all")
                ? map.mapPawns.AllPawnsSpawned.Where(p => p.RaceProps.Humanlike).ToList()
                : LabPawns.Where(p => p != null && !p.Destroyed).ToList();
            foreach (Pawn p in doomed)
            {
                p.Destroy();
                n++;
            }
            LabPawns.Clear();
            _spawnSlot = 0;
            return new Json().Num("removed", n);
        }

        /// <summary>List open windows; `close` = "log" (dev debug log, auto-opens on any startup
        /// error and sits over the middle of every shot) or "all".</summary>
        private static Json Windows(Args a)
        {
            string close = a.Str("close", "");
            var open = new List<string>();
            var closed = new List<string>();
            foreach (Window w in Find.WindowStack.Windows.ToList())
            {
                bool hit = close == "all" || (close == "log" && w is LudeonTK.EditWindow_Log);
                if (hit && Find.WindowStack.TryRemove(w, doCloseSound: false)) closed.Add(w.GetType().Name);
                else open.Add(w.GetType().Name);
            }
            return new Json().Strs("open", open.ToArray()).Strs("closed", closed.ToArray());
        }

        /// <summary>Shots are useless with the debug log parked on top (10/4, Andres's list).</summary>
        internal static void CloseLog()
        {
            foreach (Window w in Find.WindowStack.Windows.ToList())
                if (w is LudeonTK.EditWindow_Log) Find.WindowStack.TryRemove(w, doCloseSound: false);
        }

        // ------------------------------------------------------------------ world

        private static Json Camera(Args a)
        {
            RequireMap();
            Vector3 at = TargetPos(a);
            float size = a.Num("size", 12f);
            Aim(at, size);
            return new Json().Num("x", at.x).Num("z", at.z).Num("size", size);
        }

        /// <summary>
        /// Deeper zoom, the Camera+ idea done lab-locally (Andres, 10/4): vanilla's closest zoom is just
        /// CameraMapConfig.sizeRange.min (11; 7.2 on Steam Deck) and CameraDriver.Update clamps
        /// desiredSize to it every frame, which is why a plain SetRootPosAndSize(…, 6) snapped back.
        /// Lower the floor to what was asked (never below 1) before aiming.
        /// </summary>
        internal static void Aim(Vector3 at, float size)
        {
            CameraDriver cam = Find.CameraDriver;
            size = Mathf.Max(1f, size);
            if (size < cam.config.sizeRange.min)
                cam.config.sizeRange = new FloatRange(size, cam.config.sizeRange.max);
            cam.SetRootPosAndSize(at, size);
        }

        internal static Vector3 TargetPos(Args a)
        {
            if (a.Has("pawn")) return FindPawn(a.Str("pawn")).DrawPos;
            List<object> at = a.List("at");
            if (at.Count == 2) return new Vector3(Convert.ToSingle(at[0]) + 0.5f, 0f, Convert.ToSingle(at[1]) + 0.5f);
            return Find.CurrentMap.Center.ToVector3Shifted();
        }

        /// <summary>
        /// Feed the test subjects (Andres, 10/4: "babe feed your test subjects, they are going CRAZY").
        /// Tops every need to full and ends any mental state, for one pawn or every humanlike on the map.
        /// </summary>
        private static Json Care(Args a)
        {
            Map map = RequireMap();
            List<Pawn> who = a.Has("pawn")
                ? new List<Pawn> { FindPawn(a.Str("pawn")) }
                : map.mapPawns.AllPawnsSpawned.Where(p => p.RaceProps.Humanlike).ToList();
            int n = 0;
            foreach (Pawn p in who)
            {
                if (p.needs != null)
                    foreach (Need need in p.needs.AllNeeds) need.CurLevelPercentage = 1f;
                p.MentalState?.RecoverFromState();
                n++;
            }
            return new Json().Num("cared", n);
        }

        /// <summary>Fire a real incident through its worker, forced (ignores storyteller gates).</summary>
        private static Json FireIncident(Args a)
        {
            Map map = RequireMap();
            IncidentDef def = Need<IncidentDef>(a.Str("def"), "incident");
            IncidentParms parms = StorytellerUtility.DefaultParmsNow(def.category, map);
            parms.forced = true;
            if (a.Has("points")) parms.points = a.Num("points");
            int before = Find.LetterStack.LettersListForReading.Count;
            // pawnKind: run the real worker with a different IncidentDef.pawnKind (e.g. a Heyra
            // wanderer, which HAR otherwise rolls at 0.5 PERCENT), restored right after.
            PawnKindDef savedKind = def.pawnKind;
            bool fired;
            try
            {
                if (a.Has("pawnKind")) def.pawnKind = Need<PawnKindDef>(a.Str("pawnKind"), "pawnKind");
                fired = def.Worker.TryExecute(parms);
            }
            finally
            {
                def.pawnKind = savedKind;
            }
            List<string> newLetters = new List<string>();
            List<Letter> all = Find.LetterStack.LettersListForReading;
            for (int i = before; i < all.Count; i++) newLetters.Add(LetterJson(all[i]));
            return new Json().Str("incident", def.defName).Bool("fired", fired).Raw("letters", Json.Array(newLetters));
        }

        private static Json Letters(Args a)
        {
            RequireMap();
            List<Letter> all = Find.LetterStack.LettersListForReading;
            int n = a.Int("last", 5);
            List<string> rows = new List<string>();
            for (int i = Math.Max(0, all.Count - n); i < all.Count; i++) rows.Add(LetterJson(all[i]));
            return new Json().Raw("letters", Json.Array(rows));
        }

        /// <summary>A letter's label + body exactly as a player reads it, plus the pawns it points at
        /// with their CURRENT names, so "letter says X, pawn is Y" is one comparison.</summary>
        private static string LetterJson(Letter l)
        {
            string text = l is ChoiceLetter cl ? cl.Text.Resolve() : null;
            List<string> pawns = new List<string>();
            HashSet<Pawn> seen = new HashSet<Pawn>();
            if (l.lookTargets != null)
            {
                foreach (GlobalTargetInfo t in l.lookTargets.targets)
                {
                    if (t.Thing is Pawn p && seen.Add(p)) pawns.Add(LetterPawn(p, "lookTarget"));
                }
            }
            // An unaccepted joiner lives in world limbo and the letter may not point at it: find the
            // pawn whose name the letter label actually prints. This is the "letter says X" side.
            string label = l.Label.Resolve();
            string plain = label.StripTags();
            if (pawns.Count == 0)
            {
                foreach (Pawn p in PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead)
                {
                    if (p?.Name == null || !p.RaceProps.Humanlike || seen.Contains(p)) continue;
                    string shortName = p.Name.ToStringShort;
                    if (shortName.Length > 1 && plain.Contains(shortName) && seen.Add(p)) pawns.Add(LetterPawn(p, "nameInLabel"));
                }
            }
            return new Json().Str("def", l.def.defName).Str("class", l.GetType().Name).Str("label", label).Str("text", text).Raw("pawns", Json.Array(pawns)).ToString();
        }

        private static string LetterPawn(Pawn p, string how)
        {
            return new Json().Str("how", how).Str("pawn", p.ThingID).Str("race", p.def.defName).Str("kind", p.kindDef?.defName)
                .Str("nameShort", p.LabelShort).Str("nameFull", p.Name?.ToStringFull).Str("nameType", p.Name?.GetType().Name).ToString();
        }

        private static Json Speed(Args a)
        {
            RequireMap();
            int s = Mathf.Clamp(a.Int("speed", 0), 0, 4);
            Find.TickManager.CurTimeSpeed = (TimeSpeed)s;
            return new Json().Str("speed", Find.TickManager.CurTimeSpeed.ToString());
        }

        // ------------------------------------------------------------------ helpers

        internal static Json PawnInfo(Pawn p)
        {
            List<string> apparel = new List<string>();
            if (p.apparel != null)
            {
                foreach (Apparel ap in p.apparel.WornApparel)
                {
                    apparel.Add(new Json().Str("def", ap.def.defName).Str("label", ap.LabelCap)
                        .Strs("layers", ap.def.apparel.layers.Select(l => l.defName))
                        .Strs("groups", ap.def.apparel.bodyPartGroups.Select(g => g.defName))
                        .Str("stuff", ap.Stuff?.defName).ToString());
                }
            }
            List<string> hediffs = new List<string>();
            foreach (Hediff h in p.health.hediffSet.hediffs)
                hediffs.Add(new Json().Str("def", h.def.defName).Str("label", h.LabelCap).Str("part", h.Part?.Label).ToString());

            return new Json()
                .Str("pawn", p.ThingID)
                .Str("name", p.LabelShort)
                .Str("race", p.def.defName)
                .Str("kind", p.kindDef?.defName)
                .Str("gender", p.gender.ToString())
                .Num("age", p.ageTracker != null ? p.ageTracker.AgeBiologicalYearsFloat : 0f, 1)
                .Str("bodyType", p.story?.bodyType?.defName)
                .Str("headType", p.story?.headType?.defName)
                .Str("faction", p.Faction?.Name)
                .Str("pos", p.Spawned ? p.Position.x + "," + p.Position.z : null)
                .Bool("drafted", p.Drafted)
                .Str("weapon", p.equipment?.Primary?.def.defName)
                .Raw("apparel", Json.Array(apparel))
                .Raw("hediffs", Json.Array(hediffs))
                .Strs("abilities", p.abilities?.AllAbilitiesForReading.Select(x => x.def.defName));
        }

        internal static Pawn FindPawn(string key)
        {
            if (string.IsNullOrEmpty(key)) throw new LabError("missing 'pawn'");
            Map map = RequireMap();
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
            {
                if (p.ThingID == key || p.thingIDNumber.ToString() == key) return p;
            }
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
            {
                if (string.Equals(p.LabelShort, key, StringComparison.OrdinalIgnoreCase)) return p;
            }
            throw new LabError("no spawned pawn '" + key + "'");
        }

        /// <summary>Graphics and portraits are cached; after any change mark both stale.</summary>
        internal static void Dirty(Pawn p)
        {
            p.Drawer?.renderer?.SetAllGraphicsDirty();
            PortraitsCache.SetDirty(p);
        }

        private static Map RequireMap()
        {
            if (Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null)
                throw new LabError("no map loaded (state " + Current.ProgramState + ")");
            return Find.CurrentMap;
        }

        private static Faction ResolveFaction(string key)
        {
            switch (key)
            {
                case "player": return Faction.OfPlayer;
                case "none": return null;
            }
            FactionDef def = Need<FactionDef>(key, "faction");
            Faction f = Find.FactionManager.FirstFactionOfDef(def);
            if (f == null) throw new LabError("no faction of def " + key + " in this world");
            return f;
        }

        private static T Need<T>(string defName, string what) where T : Def
        {
            if (string.IsNullOrEmpty(defName)) throw new LabError("missing " + what);
            T def = DefDatabase<T>.GetNamedSilentFail(defName);
            if (def == null) throw new LabError("unknown " + what + " '" + defName + "'");
            return def;
        }

        private static Color ParseColor(List<object> c)
        {
            if (c.Count < 3) throw new LabError("color needs [r,g,b]");
            float r = Convert.ToSingle(c[0]), g = Convert.ToSingle(c[1]), b = Convert.ToSingle(c[2]);
            if (r > 1f || g > 1f || b > 1f) { r /= 255f; g /= 255f; b /= 255f; }
            return new Color(r, g, b);
        }
    }

    /// <summary>An expected failure (bad def name, no map): reported without a stack trace.</summary>
    internal sealed class LabError : Exception
    {
        public LabError(string msg) : base(msg) { }
    }
}
