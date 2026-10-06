using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

// Overseer, part 2: the hands. Every order goes through the same vanilla path a player's click ends
// in: blueprints via GenConstruct (checked with CanPlaceBlueprintAt first), designations via the
// real Designator classes (their CanDesignateCell is the rulebook), work via Pawn_WorkSettings,
// bills via the bench's BillStack. No god-mode spawning: the colonists still have to do the work.

namespace ClioLabRW
{
    internal static class Orders
    {
        // ------------------------------------------------------------------ build

        /// <summary>
        /// Blueprints. def (ThingDef or floor TerrainDef), stuff, rot (north/east/south/west), and
        /// the cells: at [x,z] | cells [[x,z]..] | rect [x,z,w,h] filled | outline [x,z,w,h] perimeter
        /// | line [x1,z1,x2,z2]. Each cell reports placed or why not.
        /// </summary>
        public static Json Build(Args a)
        {
            Map map = Colony.Home();
            string name = a.Str("def");
            BuildableDef def = (BuildableDef)DefDatabase<ThingDef>.GetNamedSilentFail(name ?? "") ?? DefDatabase<TerrainDef>.GetNamedSilentFail(name ?? "");
            if (def == null) throw new LabError("unknown buildable '" + name + "'");
            if (!def.BuildableByPlayer) throw new LabError(name + " is not player-buildable");
            if (!def.IsResearchFinished) throw new LabError(name + " needs research first");
            ThingDef stuff = null;
            if (def is ThingDef td && td.MadeFromStuff)
                stuff = a.Has("stuff") ? Colony.Need<ThingDef>(a.Str("stuff"), "stuff") : GenStuff.DefaultStuffFor(td);
            Rot4 rot = ParseRot(a.Str("rot", "north"));
            List<IntVec3> cells = Cells(a, map);
            if (cells.Count == 0) throw new LabError("no cells (at/cells/rect/outline/line)");
            return Place(map, def, stuff, rot, cells);
        }

        private static Json Place(Map map, BuildableDef def, ThingDef stuff, Rot4 rot, List<IntVec3> cells)
        {
            int placed = 0;
            Dictionary<string, List<string>> fails = new Dictionary<string, List<string>>();
            foreach (IntVec3 c in cells)
            {
                AcceptanceReport ok = GenConstruct.CanPlaceBlueprintAt(def, c, rot, map, stuffDef: stuff);
                if (!ok.Accepted)
                {
                    string why = string.IsNullOrEmpty(ok.Reason) ? "blocked" : ok.Reason.StripTags();
                    if (!fails.TryGetValue(why, out List<string> l)) fails[why] = l = new List<string>();
                    if (l.Count < 8) l.Add(c.x + "," + c.z);
                    continue;
                }
                // Zero-work things (sleeping/butcher/crafting spots) spawn finished, exactly like
                // Designator_Build does. As a blueprint they could NEVER complete: the finish-frame
                // fail roll is 1 - successChance^(work/0) = 1, so every tick fails (live 10/4: a
                // sleeping-spot frame reborn under Mitsuya's hands for eight hours).
                if (def.GetStatValueAbstract(StatDefOf.WorkToBuild, stuff) == 0f && def is ThingDef instant)
                {
                    Thing thing = ThingMaker.MakeThing(instant, stuff);
                    thing.SetFactionDirect(Faction.OfPlayer);
                    GenSpawn.Spawn(thing, c, map, rot);
                }
                else GenConstruct.PlaceBlueprintForBuild(def, c, map, rot, Faction.OfPlayer, stuff);
                placed++;
            }
            Json fj = new Json();
            foreach (KeyValuePair<string, List<string>> kv in fails) fj.Strs(kv.Key, kv.Value);
            return new Json().Str("def", def.defName).Str("stuff", stuff?.defName).Num("placed", placed).Num("failed", cells.Count - placed).Raw("why", fj.ToString());
        }

        /// <summary>
        /// A walled room in one order: rect [x,z,w,h] is the OUTER footprint (walls on its edge),
        /// doors [[x,z]..] sit on the edge in place of wall. Natural rock on the edge already counts
        /// as wall and simply reports "blocked".
        /// </summary>
        public static Json Room(Args a)
        {
            Map map = Colony.Home();
            List<object> rect = a.List("rect");
            if (rect.Count != 4) throw new LabError("room needs rect [x,z,w,h]");
            CellRect r = new CellRect(Convert.ToInt32(rect[0]), Convert.ToInt32(rect[1]), Convert.ToInt32(rect[2]), Convert.ToInt32(rect[3]));
            ThingDef stuff = a.Has("stuff") ? Colony.Need<ThingDef>(a.Str("stuff"), "stuff") : ThingDefOf.WoodLog;
            ThingDef doorStuff = a.Has("doorStuff") ? Colony.Need<ThingDef>(a.Str("doorStuff"), "doorStuff") : stuff;
            HashSet<IntVec3> doors = new HashSet<IntVec3>(a.List("doors").Select(PointOf));
            List<IntVec3> wallCells = r.EdgeCells.Where(c => !doors.Contains(c)).ToList();
            Json walls = Place(map, ThingDefOf.Wall, stuff, Rot4.North, wallCells);
            Json doorRes = doors.Count > 0 ? Place(map, ThingDefOf.Door, doorStuff, Rot4.North, doors.ToList()) : new Json().Num("placed", 0);
            return new Json().Raw("walls", walls.ToString()).Raw("doors", doorRes.ToString())
                .Str("inside", (r.minX + 1) + "," + (r.minZ + 1) + " " + (r.Width - 2) + "x" + (r.Height - 2));
        }

        // ------------------------------------------------------------------ designations

        private static Designator MakeDesignator(string kind)
        {
            switch (kind)
            {
                case "mine": return new Designator_Mine();
                case "chop": return new Designator_PlantsHarvestWood();
                case "harvest": return new Designator_PlantsHarvest();
                case "cut": return new Designator_PlantsCut();
                case "haul": return new Designator_Haul();
                case "deconstruct": return new Designator_Deconstruct();
                case "hunt": return new Designator_Hunt();
                case "tame": return new Designator_Tame();
                case "slaughter": return new Designator_Slaughter();
                case "cancel": return new Designator_Cancel();
                case "smooth": return new Designator_SmoothSurface();
                case "claim": return new Designator_Claim();
                case "uninstall": return new Designator_Uninstall();
                case "strip": return new Designator_Strip();
                case "home": return new Designator_AreaHomeExpand();
                case "homeclear": return new Designator_AreaHomeClear();
                case "roof": return new Designator_AreaBuildRoof();
                case "noroof": return new Designator_AreaNoRoof();
                default:
                    throw new LabError("designate kind: mine chop harvest cut haul deconstruct hunt tame slaughter cancel smooth claim uninstall strip home homeclear roof noroof");
            }
        }

        /// <summary>kind + cells (rect/at/cells/outline/line) or things [ids]. `filter` keeps only
        /// cells holding a thing whose defName contains it (e.g. chop only "Oak").</summary>
        public static Json Designate(Args a)
        {
            Map map = Colony.Home();
            string kind = a.Str("kind");
            Designator d = MakeDesignator(kind);
            int done = 0;
            List<object> ids = a.List("things");
            if (ids.Count > 0)
            {
                foreach (object o in ids)
                {
                    string key = Convert.ToString(o);
                    Thing t = map.listerThings.AllThings.FirstOrDefault(x => x.ThingID == key);
                    if (t == null || !d.CanDesignateThing(t).Accepted) continue;
                    d.DesignateThing(t);
                    done++;
                }
                d.Finalize(done > 0);
                return new Json().Str("kind", kind).Num("designated", done).Num("asked", ids.Count);
            }
            List<IntVec3> cells = Cells(a, map);
            string filter = a.Str("filter", "");
            List<IntVec3> ok = new List<IntVec3>();
            foreach (IntVec3 c in cells)
            {
                if (filter.Length > 0 && !c.GetThingList(map).Any(t => t.def.defName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                if (d.CanDesignateCell(c).Accepted) ok.Add(c);
            }
            if (ok.Count > 0) d.DesignateMultiCell(ok);
            return new Json().Str("kind", kind).Num("designated", ok.Count).Num("cells", cells.Count);
        }

        // ------------------------------------------------------------------ zones

        /// <summary>
        /// action add (type grow|stockpile|dump, cells, plant, name) | expand (name, cells) |
        /// delete (name) | plant (name, plant) | priority (name, priority) |
        /// filter (name, only [cats] or allow/disallow [cats or defs]).
        /// Zones are built directly (Designator_ZoneAdd's drag logic depends on the selection), but
        /// every cell is first vetted by the matching designator's CanDesignateCell.
        /// </summary>
        public static Json ZoneCmd(Args a)
        {
            Map map = Colony.Home();
            string action = a.Str("action", "add");
            switch (action)
            {
                case "add":
                {
                    string type = a.Str("type", "grow");
                    Designator_ZoneAdd d;
                    Zone z;
                    switch (type)
                    {
                        case "grow": d = new Designator_ZoneAdd_Growing(); z = new Zone_Growing(map.zoneManager); break;
                        case "stockpile": d = new Designator_ZoneAddStockpile_Resources(); z = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager); break;
                        case "dump": d = new Designator_ZoneAddStockpile_Dumping(); z = new Zone_Stockpile(StorageSettingsPreset.DumpingStockpile, map.zoneManager); break;
                        default: throw new LabError("zone type grow|stockpile|dump");
                    }
                    List<IntVec3> cells = Cells(a, map).Where(c => map.zoneManager.ZoneAt(c) == null && d.CanDesignateCell(c).Accepted).ToList();
                    if (cells.Count == 0) throw new LabError("no zoneable cells there (fog, buildings, edge, wrong soil, or another zone)");
                    map.zoneManager.RegisterZone(z);
                    foreach (IntVec3 c in cells) z.AddCell(c);
                    if (a.Has("name")) z.label = a.Str("name");
                    if (z is Zone_Growing g && a.Has("plant")) g.SetPlantDefToGrow(Colony.Need<ThingDef>(a.Str("plant"), "plant"));
                    z.CheckContiguous();
                    return ZoneJson(z);
                }
                case "expand":
                {
                    Zone z = FindZone(map, a.Str("name"));
                    Designator_ZoneAdd d = z is Zone_Growing ? (Designator_ZoneAdd)new Designator_ZoneAdd_Growing() : new Designator_ZoneAddStockpile_Resources();
                    int n = 0;
                    foreach (IntVec3 c in Cells(a, map))
                    {
                        if (map.zoneManager.ZoneAt(c) != null || !d.CanDesignateCell(c).Accepted) continue;
                        z.AddCell(c);
                        n++;
                    }
                    z.CheckContiguous();
                    return ZoneJson(z).Num("added", n);
                }
                case "shrink":
                {
                    Zone z = FindZone(map, a.Str("name"));
                    int n = 0;
                    foreach (IntVec3 c in Cells(a, map))
                    {
                        if (map.zoneManager.ZoneAt(c) != z) continue;
                        z.RemoveCell(c);
                        n++;
                    }
                    return new Json().Str("zone", z.label).Num("removed", n);
                }
                case "delete":
                {
                    Zone z = FindZone(map, a.Str("name"));
                    z.Delete(false);
                    return new Json().Str("deleted", z.label);
                }
                case "plant":
                {
                    if (!(FindZone(map, a.Str("name")) is Zone_Growing g)) throw new LabError("not a growing zone");
                    g.SetPlantDefToGrow(Colony.Need<ThingDef>(a.Str("plant"), "plant"));
                    return ZoneJson(g);
                }
                case "priority":
                {
                    if (!(FindZone(map, a.Str("name")) is Zone_Stockpile s)) throw new LabError("not a stockpile");
                    s.settings.Priority = (StoragePriority)Enum.Parse(typeof(StoragePriority), a.Str("priority", "Normal"), true);
                    return ZoneJson(s);
                }
                case "filter":
                {
                    if (!(FindZone(map, a.Str("name")) is Zone_Stockpile s)) throw new LabError("not a stockpile");
                    ThingFilter f = s.settings.filter;
                    List<object> only = a.List("only");
                    if (only.Count > 0)
                    {
                        f.SetDisallowAll();
                        foreach (object o in only) Allow(f, Convert.ToString(o), true);
                    }
                    foreach (object o in a.List("allow")) Allow(f, Convert.ToString(o), true);
                    foreach (object o in a.List("disallow")) Allow(f, Convert.ToString(o), false);
                    return ZoneJson(s).Str("filter", f.Summary);
                }
                default: throw new LabError("zone action add|expand|shrink|delete|plant|priority|filter");
            }
        }

        /// <summary>Storage settings of a building (shelf etc.): filter only/allow/disallow + priority,
        /// the same knobs as the shelf's Storage tab. thing = ThingID, or a cell to pick the building there.</summary>
        public static Json Storage(Args a)
        {
            Map map = Colony.Home();
            Thing t = a.Has("thing")
                ? map.listerThings.AllThings.FirstOrDefault(x => x.ThingID == a.Str("thing"))
                : PointOf(a.List("at")).GetThingList(map).FirstOrDefault(x => x is IStoreSettingsParent);
            if (!(t is IStoreSettingsParent sp) || sp.GetStoreSettings() == null) throw new LabError("no storage building there");
            StorageSettings s = sp.GetStoreSettings();
            List<object> only = a.List("only");
            if (only.Count > 0)
            {
                s.filter.SetDisallowAll();
                foreach (object o in only) Allow(s.filter, Convert.ToString(o), true);
            }
            foreach (object o in a.List("allow")) Allow(s.filter, Convert.ToString(o), true);
            foreach (object o in a.List("disallow")) Allow(s.filter, Convert.ToString(o), false);
            if (a.Has("priority")) s.Priority = (StoragePriority)Enum.Parse(typeof(StoragePriority), a.Str("priority"), true);
            return new Json().Str("thing", t.ThingID).Str("priority", s.Priority.ToString()).Str("filter", s.filter.Summary);
        }

        /// <summary>What a building is doing: power (on/produced/net), fuel, rotation. thing = ThingID or defName.</summary>
        public static Json Inspect(Args a)
        {
            Map map = Colony.Home();
            string key = a.Str("thing");
            Thing t = map.listerThings.AllThings.FirstOrDefault(x => x.ThingID == key)
                ?? map.listerThings.AllThings.FirstOrDefault(x => x.def.defName == key);
            if (t == null) throw new LabError("no thing '" + key + "'");
            Json j = new Json().Str("thing", t.ThingID).Str("pos", t.Position.x + "," + t.Position.z).Str("rot", t.Rotation.ToStringHuman());
            CompPowerTrader pt = t.TryGetComp<CompPowerTrader>();
            if (pt != null) j.Bool("powerOn", pt.PowerOn).Num("powerOutput", pt.PowerOutput).Bool("connected", pt.PowerNet != null);
            CompPower cp = t.TryGetComp<CompPower>();
            if (cp?.PowerNet != null) j.Num("netGain", cp.PowerNet.CurrentEnergyGainRate() / CompPower.WattsToWattDaysPerTick);
            CompRefuelable rf = t.TryGetComp<CompRefuelable>();
            if (rf != null) j.Num("fuel", rf.Fuel).Num("fuelMax", rf.Props.fuelCapacity).Bool("hasFuel", rf.HasFuel);
            CompFlickable fl = t.TryGetComp<CompFlickable>();
            if (fl != null) j.Bool("switchOn", fl.SwitchIsOn);
            // Coolers/heaters: thermostat (default 21C, so a fresh cooler never makes a freezer).
            CompTempControl tc = t.TryGetComp<CompTempControl>();
            if (tc != null)
            {
                if (a.Has("targetTemp")) tc.targetTemperature = Mathf.Clamp(a.Num("targetTemp"), -270f, 2000f);
                j.Num("targetTemp", tc.targetTemperature);
            }
            return j;
        }

        /// <summary>Dev-mode skill set (Andres's decree, 10/5: "crank Mitsuya just enough").</summary>
        public static Json Skill(Args a)
        {
            Pawn p = Commands.FindPawn(a.Str("pawn"));
            SkillDef def = Colony.Need<SkillDef>(a.Str("skill"), "skill");
            SkillRecord r = p.skills?.GetSkill(def) ?? throw new LabError(p.LabelShort + " has no skills");
            if (r.TotallyDisabled) throw new LabError(p.LabelShort + " is incapable of " + def.label);
            int from = r.Level;
            r.Level = Mathf.Clamp((int)a.Num("level"), 0, 20);
            r.xpSinceLastLevel = 0f;
            return new Json().Str("pawn", p.LabelShort).Str("skill", def.defName).Num("from", from).Num("to", r.Level);
        }

        private static void Allow(ThingFilter f, string name, bool allow)
        {
            ThingCategoryDef cat = DefDatabase<ThingCategoryDef>.GetNamedSilentFail(name);
            if (cat != null) { f.SetAllow(cat, allow); return; }
            f.SetAllow(Colony.Need<ThingDef>(name, "category or thing"), allow);
        }

        private static Zone FindZone(Map map, string name)
        {
            Zone z = map.zoneManager.AllZones.FirstOrDefault(x => string.Equals(x.label, name, StringComparison.OrdinalIgnoreCase));
            if (z == null) throw new LabError("no zone '" + name + "' (have: " + string.Join(", ", map.zoneManager.AllZones.Select(x => x.label)) + ")");
            return z;
        }

        private static Json ZoneJson(Zone z)
        {
            CellRect b = CellRect.FromCellList(z.Cells);
            Json j = new Json().Str("zone", z.label).Num("cells", z.CellCount).Str("bounds", b.minX + "," + b.minZ + " " + b.Width + "x" + b.Height);
            if (z is Zone_Growing g) j.Str("plant", g.GetPlantDefToGrow()?.defName);
            if (z is Zone_Stockpile s) j.Str("priority", s.settings.Priority.ToString());
            return j;
        }

        // ------------------------------------------------------------------ work, research, bills

        /// <summary>
        /// set {pawn, prio {WorkType: 0-4}} with manual priorities switched on; no args = the grid.
        /// Disabled work is skipped (SetPriority logs an error on it), never forced.
        /// </summary>
        public static Json Work(Args a)
        {
            Map map = Colony.Home();
            List<string> skipped = new List<string>();
            if (a.Has("pawn"))
            {
                Pawn p = Commands.FindPawn(a.Str("pawn"));
                Find.PlaySettings.useWorkPriorities = true;
                if (a.Has("prio"))
                {
                    foreach (KeyValuePair<string, object> kv in a.Dict("prio"))
                    {
                        WorkTypeDef w = Colony.Need<WorkTypeDef>(kv.Key, "work type");
                        if (p.WorkTypeIsDisabled(w)) { skipped.Add(kv.Key); continue; }
                        p.workSettings.SetPriority(w, Mathf.Clamp(Convert.ToInt32(kv.Value), 0, 4));
                    }
                }
            }
            List<WorkTypeDef> types = DefDatabase<WorkTypeDef>.AllDefsListForReading.Where(w => w.visible).OrderByDescending(w => w.naturalPriority).ToList();
            List<string> rows = new List<string>();
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                Json j = new Json().Str("name", p.LabelShort);
                foreach (WorkTypeDef w in types)
                    j.Str(w.defName, p.WorkTypeIsDisabled(w) ? "x" : p.workSettings.GetPriority(w).ToString());
                rows.Add(j.ToString());
            }
            return new Json().Bool("manual", Find.PlaySettings.useWorkPriorities).Strs("order", types.Select(w => w.defName))
                .Raw("grid", Json.Array(rows)).Strs("skippedDisabled", skipped);
        }

        public static Json Research(Args a)
        {
            if (a.Has("project"))
            {
                ResearchProjectDef p = Colony.Need<ResearchProjectDef>(a.Str("project"), "research project");
                if (p.IsFinished) throw new LabError(p.defName + " is already finished");
                if (!p.CanStartNow) throw new LabError(p.defName + " can't start yet (prerequisites / bench)");
                Find.ResearchManager.SetCurrentProject(p);
            }
            ResearchProjectDef cur = Find.ResearchManager.GetProject();
            List<string> rows = new List<string>();
            foreach (ResearchProjectDef r in DefDatabase<ResearchProjectDef>.AllDefsListForReading.Where(r => !r.IsFinished && r.CanStartNow).OrderBy(r => r.CostApparent))
                rows.Add(new Json().Str("def", r.defName).Str("label", r.label).Num("cost", r.CostApparent, 0).Num("pct", r.ProgressPercent * 100f, 0)
                    .Str("bench", r.requiredResearchBuilding?.defName).ToString());
            return new Json().Str("current", cur?.defName).Num("pct", cur != null ? cur.ProgressPercent * 100f : 0f, 0)
                .Strs("finished", DefDatabase<ResearchProjectDef>.AllDefsListForReading.Where(r => r.IsFinished).Select(r => r.defName))
                .Raw("available", Json.Array(rows));
        }

        /// <summary>
        /// bench (ThingID or def → first of ours) + add recipe [mode count|until|forever, count,
        /// radius] | remove index | recipes:true. No action = the bench's bill list.
        /// </summary>
        public static Json Bill(Args a)
        {
            Map map = Colony.Home();
            string key = a.Str("bench");
            Building bench = map.listerBuildings.allBuildingsColonist.FirstOrDefault(b => b.ThingID == key)
                ?? map.listerBuildings.allBuildingsColonist.FirstOrDefault(b => b.def.defName == key);
            if (bench == null) throw new LabError("no bench of ours '" + key + "'");
            if (!(bench is IBillGiver giver)) throw new LabError(bench.def.defName + " takes no bills");
            if (a.Bool("recipes"))
            {
                List<string> rr = new List<string>();
                foreach (RecipeDef r in bench.def.AllRecipes.Where(r => r.AvailableNow))
                    rr.Add(new Json().Str("def", r.defName).Str("label", r.label)
                        .Strs("needs", r.ingredients.Select(i => i.Summary)).ToString());
                return new Json().Str("bench", bench.ThingID).Raw("recipes", Json.Array(rr));
            }
            if (a.Has("add"))
            {
                RecipeDef r = Colony.Need<RecipeDef>(a.Str("add"), "recipe");
                if (!bench.def.AllRecipes.Contains(r)) throw new LabError(r.defName + " is not made at " + bench.def.defName);
                if (!r.AvailableNow) throw new LabError(r.defName + " needs research first");
                Bill b = r.MakeNewBill();
                if (b is Bill_Production bp)
                {
                    switch (a.Str("mode", "count"))
                    {
                        case "forever": bp.repeatMode = BillRepeatModeDefOf.Forever; break;
                        case "until": bp.repeatMode = BillRepeatModeDefOf.TargetCount; bp.targetCount = a.Int("count", 10); break;
                        default: bp.repeatMode = BillRepeatModeDefOf.RepeatCount; bp.repeatCount = a.Int("count", 1); break;
                    }
                }
                if (a.Has("radius")) b.ingredientSearchRadius = a.Num("radius");
                giver.BillStack.AddBill(b);
            }
            if (a.Has("remove"))
            {
                int i = a.Int("remove");
                if (i < 0 || i >= giver.BillStack.Count) throw new LabError("no bill #" + i);
                giver.BillStack.Delete(giver.BillStack.Bills[i]);
            }
            List<string> rows = new List<string>();
            for (int i = 0; i < giver.BillStack.Count; i++)
            {
                Bill b = giver.BillStack.Bills[i];
                Json j = new Json().Num("i", i).Str("recipe", b.recipe.defName).Bool("suspended", b.suspended);
                if (b is Bill_Production bp) j.Str("mode", bp.repeatMode.defName).Num("count", bp.repeatCount).Num("target", bp.targetCount);
                rows.Add(j.ToString());
            }
            return new Json().Str("bench", bench.ThingID).Str("def", bench.def.defName).Raw("bills", Json.Array(rows));
        }

        // ------------------------------------------------------------------ pawns: draft, move, attack

        /// <summary>pawns [names|ids] or all:true, drafted bool.</summary>
        public static Json Draft(Args a)
        {
            Map map = Colony.Home();
            bool drafted = a.Bool("drafted", true);
            List<Pawn> who = a.Bool("all") ? map.mapPawns.FreeColonistsSpawned.ToList() : a.List("pawns").Select(o => Commands.FindPawn(Convert.ToString(o))).ToList();
            List<string> done = new List<string>();
            foreach (Pawn p in who)
            {
                if (p.drafter == null || p.Downed) continue;
                p.drafter.Drafted = drafted;
                done.Add(p.LabelShort);
            }
            return new Json().Bool("drafted", drafted).Strs("pawns", done);
        }

        /// <summary>
        /// Ordered jobs on drafted pawns, like a right-click: goto {to:[x,z]}, attack {target id}
        /// (ranged weapon → AttackStatic, else AttackMelee). Drafts the pawn if needed.
        /// </summary>
        public static Json Order(Args a)
        {
            Map map = Colony.Home();
            Pawn p = Commands.FindPawn(a.Str("pawn"));
            if (p.drafter != null && !p.Drafted) p.drafter.Drafted = true;
            string job = a.Str("job", "goto");
            Job j;
            switch (job)
            {
                case "goto":
                {
                    IntVec3 c = PointOf(a.List("to"));
                    if (!c.InBounds(map) || !c.Standable(map)) throw new LabError("can't stand at " + c);
                    j = JobMaker.MakeJob(JobDefOf.Goto, c);
                    break;
                }
                case "attack":
                {
                    string key = a.Str("target");
                    Thing t = map.mapPawns.AllPawnsSpawned.FirstOrDefault(x => x.ThingID == key || x.LabelShort == key)
                        ?? map.listerThings.AllThings.FirstOrDefault(x => x.ThingID == key);
                    if (t == null) throw new LabError("no target '" + key + "'");
                    // melee:true forces fists/butt-stroke even with a gun: a ranged pawn being bitten
                    // just stands there re-aiming (Andres, 10/4: "ranged pawns just FREEZE if they are
                    // getting melee'd like idiots" — Cipier vs the mad hare, shooting 1).
                    bool ranged = !a.Bool("melee") && p.equipment?.Primary != null && p.equipment.Primary.def.IsRangedWeapon;
                    j = JobMaker.MakeJob(ranged ? JobDefOf.AttackStatic : JobDefOf.AttackMelee, t);
                    break;
                }
                case "equip":
                case "wear":
                {
                    // Walk over and pick it up / put it on, like the right-click "Equip"/"Wear".
                    string key = a.Str("target");
                    Thing t = map.listerThings.AllThings.FirstOrDefault(x => x.ThingID == key)
                        ?? map.listerThings.AllThings.FirstOrDefault(x => x.def.defName == key && x.Spawned && !x.IsForbidden(p));
                    if (t == null) throw new LabError("no item '" + key + "'");
                    if (job == "equip" && !t.def.IsWeapon) throw new LabError(t.def.defName + " is not a weapon");
                    if (job == "wear" && !t.def.IsApparel) throw new LabError(t.def.defName + " is not apparel");
                    j = JobMaker.MakeJob(job == "equip" ? JobDefOf.Equip : JobDefOf.Wear, t);
                    // Ordered pickups are not combat moves: release the draft so it isn't a soldier stroll.
                    if (p.Drafted && !a.Bool("keepDrafted")) p.drafter.Drafted = false;
                    break;
                }
                case "capture":
                case "rescue":
                {
                    // FloatMenuOptionProvider_CapturePawn / rescue: find the bed the game would pick,
                    // carry the downed pawn to it (capture needs a bed set for prisoners: `bed`).
                    Pawn victim = map.mapPawns.AllPawnsSpawned.FirstOrDefault(x => x.ThingID == a.Str("target") || x.LabelShort == a.Str("target"));
                    if (victim == null || !victim.Downed) throw new LabError("no downed pawn '" + a.Str("target") + "'");
                    GuestStatus? status = job == "capture" ? GuestStatus.Prisoner : (GuestStatus?)null;
                    Building_Bed bed = RestUtility.FindBedFor(victim, p, checkSocialProperness: false, ignoreOtherReservations: false, status)
                        ?? RestUtility.FindBedFor(victim, p, checkSocialProperness: false, ignoreOtherReservations: true, status);
                    if (bed == null) throw new LabError("no free " + (job == "capture" ? "prisoner " : "") + "bed for " + victim.LabelShort);
                    j = JobMaker.MakeJob(job == "capture" ? JobDefOf.Capture : JobDefOf.Rescue, victim, bed);
                    j.count = 1;
                    if (p.Drafted) p.drafter.Drafted = false;
                    break;
                }
                default: throw new LabError("job goto|attack|equip|wear|capture|rescue");
            }
            bool ok = p.jobs.TryTakeOrderedJob(j, JobTag.Misc);
            return new Json().Str("pawn", p.LabelShort).Str("job", job).Bool("taken", ok);
        }

        /// <summary>bed (ThingID or at [x,z]) + mode prisoner|medical|colonist.</summary>
        public static Json Bed(Args a)
        {
            Map map = Colony.Home();
            Building_Bed bed = null;
            if (a.Has("bed")) bed = map.listerBuildings.allBuildingsColonist.OfType<Building_Bed>().FirstOrDefault(b => b.ThingID == a.Str("bed"));
            else if (a.List("at").Count == 2) bed = PointOf(a.List("at")).GetFirstThing<Building_Bed>(map);
            if (bed == null) throw new LabError("no bed of ours there");
            switch (a.Str("mode", "colonist"))
            {
                case "prisoner": bed.Medical = false; bed.ForOwnerType = BedOwnerType.Prisoner; break;
                case "medical": bed.ForPrisoners = false; bed.Medical = true; break;
                // ForPrisoners=false logs "should it be for colonists or slaves?": set the owner type.
                default: bed.ForOwnerType = BedOwnerType.Colonist; bed.Medical = false; break;
            }
            // SetBedOwnerTypeByInterface does this next: Room.isPrisonCell is ONLY recomputed in
            // Notify_RoomShapeChanged, so without it the role reads PrisonCell while capture still
            // finds "no prisoner bed" (live 10/4, Meza bleeding on the doorstep).
            bed.GetDistrict()?.Notify_RoomShapeOrContainedBedsChanged();
            Room room = bed.GetRoom();
            room?.Notify_RoomShapeChanged();
            return new Json().Str("bed", bed.ThingID).Bool("prisoners", bed.ForPrisoners).Bool("medical", bed.Medical)
                .Bool("inPrisonCell", bed.Position.IsInPrisonCell(map)).Str("room", room?.Role?.defName);
        }

        /// <summary>Prisoners: no mode = list (resistance, mode); pawn + mode recruit|reduce|maintain|release|execute.</summary>
        public static Json Prisoner(Args a)
        {
            Map map = Colony.Home();
            if (a.Has("pawn"))
            {
                Pawn p = map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => x.ThingID == a.Str("pawn") || x.LabelShort == a.Str("pawn"));
                if (p == null) throw new LabError("no prisoner '" + a.Str("pawn") + "'");
                PrisonerInteractionModeDef mode;
                switch (a.Str("mode", "recruit"))
                {
                    case "recruit": mode = PrisonerInteractionModeDefOf.AttemptRecruit; break;
                    case "reduce": mode = PrisonerInteractionModeDefOf.ReduceResistance; break;
                    case "release": mode = PrisonerInteractionModeDefOf.Release; break;
                    case "execute": mode = PrisonerInteractionModeDefOf.Execution; break;
                    default: mode = PrisonerInteractionModeDefOf.MaintainOnly; break;
                }
                p.guest.SetExclusiveInteraction(mode);
            }
            List<string> rows = new List<string>();
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                rows.Add(new Json().Str("id", p.ThingID).Str("name", p.LabelShort).Str("mode", p.guest.ExclusiveInteractionMode?.defName)
                    .Num("resistance", p.guest.Resistance, 1).Num("hp", p.health.summaryHealth.SummaryHealthPercent * 100f, 0)
                    .Num("bleedPerDay", p.health.hediffSet.BleedRateTotal, 2).Num("mood", p.needs?.mood != null ? p.needs.mood.CurLevelPercentage * 100f : -1f, 0).ToString());
            return new Json().Raw("prisoners", Json.Array(rows));
        }

        /// <summary>The game's own verdict on the room at [x,z]: role, prison cell, proper, size.</summary>
        public static Json RoomInfo(Args a)
        {
            Map map = Colony.Home();
            IntVec3 c = PointOf(a.List("at"));
            Room r = c.GetRoom(map);
            if (r == null) throw new LabError("no room at " + c);
            return new Json().Str("role", r.Role?.defName).Bool("prisonCell", r.IsPrisonCell).Bool("proper", r.ProperRoom).Bool("huge", r.IsHuge)
                .Bool("outdoors", r.PsychologicallyOutdoors).Bool("fogged", r.Fogged).Num("cells", r.CellCount)
                .Num("beds", r.ContainedBeds.Count()).Num("impressiveness", r.GetStat(RoomStatDefOf.Impressiveness), 1)
                .Num("tempC", r.Temperature, 1);
        }

        /// <summary>Unforbid (or forbid) every item in the cells (default whole map).</summary>
        public static Json Forbid(Args a)
        {
            Map map = Colony.Home();
            bool value = a.Bool("forbidden", false);
            CellRect r = Colony.RectArg(a, map, map.BoundsRect());
            int n = 0;
            foreach (Thing t in map.listerThings.AllThings.ToList())
            {
                if (!t.Spawned || !r.Contains(t.Position) || t.Position.Fogged(map)) continue;
                if (t.def.category != ThingCategory.Item && !(t is Building_Door)) continue;
                if (t.TryGetComp<CompForbiddable>() == null || t.IsForbidden(Faction.OfPlayer) == value) continue;
                t.SetForbidden(value, warnOnFail: false);
                n++;
            }
            return new Json().Bool("forbidden", value).Num("changed", n);
        }

        // ------------------------------------------------------------------ helpers

        internal static List<IntVec3> Cells(Args a, Map map)
        {
            List<IntVec3> cells = new List<IntVec3>();
            List<object> at = a.List("at");
            if (at.Count == 2) cells.Add(PointOf(at));
            foreach (object o in a.List("cells")) cells.Add(PointOf(o));
            List<object> rect = a.List("rect");
            if (rect.Count == 4) cells.AddRange(RectOf(rect).Cells);
            List<object> outline = a.List("outline");
            if (outline.Count == 4) cells.AddRange(RectOf(outline).EdgeCells);
            List<object> line = a.List("line");
            if (line.Count == 4)
            {
                IntVec3 p0 = new IntVec3(Convert.ToInt32(line[0]), 0, Convert.ToInt32(line[1]));
                IntVec3 p1 = new IntVec3(Convert.ToInt32(line[2]), 0, Convert.ToInt32(line[3]));
                cells.AddRange(CellRect.FromLimits(p0, p1).Cells);
            }
            return cells.Where(c => c.InBounds(map)).Distinct().ToList();
        }

        private static CellRect RectOf(List<object> r) =>
            new CellRect(Convert.ToInt32(r[0]), Convert.ToInt32(r[1]), Convert.ToInt32(r[2]), Convert.ToInt32(r[3]));

        internal static IntVec3 PointOf(object o)
        {
            if (!(o is List<object> l) || l.Count != 2) throw new LabError("a cell is [x,z]");
            return new IntVec3(Convert.ToInt32(l[0]), 0, Convert.ToInt32(l[1]));
        }

        private static Rot4 ParseRot(string s)
        {
            switch ((s ?? "north").ToLowerInvariant())
            {
                case "north": case "n": case "0": return Rot4.North;
                case "east": case "e": case "1": return Rot4.East;
                case "south": case "s": case "2": return Rot4.South;
                case "west": case "w": case "3": return Rot4.West;
                default: throw new LabError("rot north|east|south|west");
            }
        }
    }
}
