using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

// Overseer, part 1: the eyes. Andres, 10/4: "can you use your Clio lab for Rimworld to actually...
// play a colony with full control?" — so the bench grew a player. These commands start a real game
// and describe it in words and ASCII, because a grid is something I can plan on and a screenshot
// is not. Orders live in Orders.cs, time in Clock.cs.
//
// Everything reads the HOME map (Find.AnyPlayerHomeMap), never whatever the camera happens to show.

namespace ClioLabRW
{
    internal static class Colony
    {
        // ------------------------------------------------------------------ new game

        /// <summary>The newgame request, kept so `state` can report where we landed and why.</summary>
        internal static string StartNote;

        /// <summary>
        /// From the main menu: Root_Play.SetupForQuickTestPlay with every hard-coded choice made a
        /// parameter (quicktest is Cassandra/Rough on a random tile), then the vanilla scene change
        /// (PageUtility.InitGameStart). World gen runs as an async long event exactly like the
        /// Create World page does it; the scene change is queued behind it.
        /// </summary>
        public static Json NewGame(Args a)
        {
            if (Current.ProgramState != ProgramState.Entry)
                throw new LabError("newgame needs the main menu (lab.mjs launch --menu); state is " + Current.ProgramState);
            StorytellerDef teller = Need<StorytellerDef>(a.Str("storyteller", "Cassandra"), "storyteller");
            DifficultyDef diff = Need<DifficultyDef>(a.Str("difficulty", "Easy"), "difficulty");
            BiomeDef biome = a.Has("biome") ? Need<BiomeDef>(a.Str("biome"), "biome") : null;
            string seed = a.Str("seed", GenText.RandomSeedString());
            float coverage = a.Num("coverage", 0.3f);
            int mapSize = a.Int("mapSize", 250);
            float tMin = a.Num("tempMin", 8f), tMax = a.Num("tempMax", 22f);
            HashSet<Hilliness> hills = new HashSet<Hilliness>();
            foreach (object h in a.List("hills")) hills.Add((Hilliness)Enum.Parse(typeof(Hilliness), Convert.ToString(h)));
            if (hills.Count == 0) { hills.Add(Hilliness.SmallHills); hills.Add(Hilliness.LargeHills); }

            LongEventHandler.QueueLongEvent(() =>
            {
                Current.ProgramState = ProgramState.Entry;
                Game.ClearCaches();
                Current.Game = new Game();
                Current.Game.InitData = new GameInitData();
                Current.Game.Scenario = ScenarioDefOf.Crashlanded.scenario;
                Find.Scenario.PreConfigure();
                Current.Game.storyteller = new Storyteller(teller, diff);
                Current.Game.World = WorldGenerator.GenerateWorld(coverage, seed, OverallRainfall.Normal, OverallTemperature.Normal,
                    OverallPopulation.Normal, LandmarkDensity.Normal);

                // Strictest first, then relax: biome+hills+temperature → biome+temperature → biome → anything.
                string how = null;
                PlanetTile tile = PlanetTile.Invalid;
                Predicate<PlanetTile>[] tiers =
                {
                    t => BiomeOk(t, biome) && hills.Contains(Find.WorldGrid[t].hilliness) && TempOk(t, tMin, tMax),
                    t => BiomeOk(t, biome) && TempOk(t, tMin, tMax),
                    t => BiomeOk(t, biome),
                    t => true,
                };
                string[] names = { "biome+hills+temp", "biome+temp", "biome", "any" };
                for (int i = 0; i < tiers.Length && !tile.Valid; i++)
                {
                    tile = TileFinder.RandomSettlementTileFor(Faction.OfPlayer, true, tiers[i]);
                    how = names[i];
                }
                Find.GameInitData.startingTile = tile;
                Find.GameInitData.mapSize = mapSize;
                Find.Scenario.PostIdeoChosen();

                Tile tt = Find.WorldGrid[tile];
                StartNote = teller.defName + "/" + diff.defName + " seed " + seed + ", tile " + (int)tile + " " + tt.PrimaryBiome.defName
                    + " " + tt.hilliness + ", avg " + tt.temperature.ToString("0.0") + "C, rain " + tt.rainfall.ToString("0") + "mm (matched " + how + ")";
                Log.Message("[ClioLab] newgame: " + StartNote);
            }, "GeneratingWorld", doAsynchronously: true, exceptionHandler: null);
            PageUtility.InitGameStart();

            return new Json().Str("queued", "world gen + map gen").Str("storyteller", teller.defName).Str("difficulty", diff.defName)
                .Str("seed", seed).Str("biome", biome?.defName);
        }

        private static bool BiomeOk(PlanetTile t, BiomeDef biome) => biome == null || Find.WorldGrid[t].PrimaryBiome == biome;

        private static bool TempOk(PlanetTile t, float lo, float hi)
        {
            float temp = Find.WorldGrid[t].temperature;
            return temp >= lo && temp <= hi;
        }

        // ------------------------------------------------------------------ state

        public static Json State(Args a)
        {
            Map map = Home();
            TickManager tm = Find.TickManager;
            Vector2 ll = Find.WorldGrid.LongLatOf(map.Tile);
            long abs = tm.TicksAbs;

            List<string> colonists = new List<string>();
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned) colonists.Add(ColonistBrief(p));
            // Colonists off the home map (caravans, downed elsewhere) still count.
            foreach (Pawn p in PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists)
                if (!p.Spawned || p.Map != map) colonists.Add(new Json().Str("id", p.ThingID).Str("name", p.LabelShort).Str("away", "not on the home map").ToString());

            Json res = new Json();
            foreach (KeyValuePair<ThingDef, int> kv in map.resourceCounter.AllCountedAmounts.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value))
                res.Num(kv.Key.defName, kv.Value);

            ResearchProjectDef proj = Find.ResearchManager.GetProject();
            Json research = new Json()
                .Str("current", proj?.defName)
                .Num("pct", proj != null ? proj.ProgressPercent * 100f : 0f, 0)
                .Num("finished", DefDatabase<ResearchProjectDef>.AllDefsListForReading.Count(r => r.IsFinished));

            Dictionary<string, int> hostiles = new Dictionary<string, int>();
            foreach (Pawn p in Hostiles(map))
            {
                string k = (p.Faction?.Name ?? (p.InAggroMentalState ? "manhunter " + p.def.label : p.def.label));
                hostiles.TryGetValue(k, out int n);
                hostiles[k] = n + 1;
            }
            Json hj = new Json();
            foreach (KeyValuePair<string, int> kv in hostiles) hj.Num(kv.Key, kv.Value);

            Dictionary<string, int> herd = new Dictionary<string, int>();
            foreach (Pawn an in map.mapPawns.SpawnedColonyAnimals)
            {
                herd.TryGetValue(an.def.label, out int n);
                herd[an.def.label] = n + 1;
            }
            Json herdJ = new Json();
            foreach (KeyValuePair<string, int> kv in herd) herdJ.Num(kv.Key, kv.Value);

            List<string> zones = new List<string>();
            foreach (Zone z in map.zoneManager.AllZones)
            {
                CellRect b = CellRect.FromCellList(z.Cells);
                Json zj = new Json().Str("label", z.label).Str("type", z is Zone_Growing ? "grow" : z is Zone_Stockpile ? "stockpile" : z.GetType().Name)
                    .Num("cells", z.CellCount).Str("bounds", b.minX + "," + b.minZ + " " + b.Width + "x" + b.Height);
                if (z is Zone_Growing g) zj.Str("plant", g.GetPlantDefToGrow()?.defName);
                if (z is Zone_Stockpile s) zj.Str("priority", s.settings.Priority.ToString());
                zones.Add(zj.ToString());
            }

            Json buildings = new Json();
            foreach (IGrouping<ThingDef, Building> g in map.listerBuildings.allBuildingsColonist.GroupBy(b => b.def).OrderByDescending(g => g.Count()))
                buildings.Num(g.Key.defName, g.Count());

            return new Json()
                .Str("colony", Faction.OfPlayer?.Name)
                .Str("start", StartNote)
                .Str("storyteller", Find.Storyteller.def.defName + "/" + Find.Storyteller.difficultyDef?.defName)
                .Str("date", GenDate.DateFullStringWithHourAt(abs, ll))
                .Str("season", GenDate.Season(abs, ll).LabelCap())
                .Num("day", GenDate.DaysPassed)
                .Num("tick", tm.TicksGame)
                .Str("speed", tm.CurTimeSpeed.ToString())
                .Str("weather", map.weatherManager.curWeather?.label)
                .Num("tempC", map.mapTemperature.OutdoorTemp, 1)
                .Str("growing", Zone_Growing.GrowingQuadrumsDescription(map))
                .Num("wealth", map.wealthWatcher.WealthTotal, 0)
                .Raw("colonists", Json.Array(colonists))
                .Raw("resources", res.ToString())
                .Raw("research", research.ToString())
                .Raw("hostiles", hj.ToString())
                .Raw("animals", herdJ.ToString())
                .Raw("zones", Json.Array(zones))
                .Raw("buildings", buildings.ToString())
                .Num("blueprints", map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Count)
                .Num("frames", map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame).Count)
                .Raw("alerts", Json.Array(Alerts()))
                .Raw("letters", Json.Array(Clock.LetterRows(false)))
                .Raw("windows", Json.Array(Clock.WindowRows()));
        }

        private static string ColonistBrief(Pawn p)
        {
            string job = null;
            try { job = p.jobs?.curDriver?.GetReport()?.StripTags(); } catch (Exception) { }
            Json j = new Json()
                .Str("id", p.ThingID)
                .Str("name", p.LabelShort)
                .Str("pos", p.Position.x + "," + p.Position.z)
                .Str("job", job)
                .Num("hp", p.health.summaryHealth.SummaryHealthPercent * 100f, 0)
                // Summary health ignores blood loss: Meza read a "steady 38%" right up to bleeding out (10/4).
                .Num("bleed", p.health.hediffSet.BleedRateTotal, 2)
                .Num("mood", p.needs?.mood != null ? p.needs.mood.CurLevelPercentage * 100f : -1f, 0)
                .Num("food", p.needs?.food != null ? p.needs.food.CurLevelPercentage * 100f : -1f, 0)
                .Num("rest", p.needs?.rest != null ? p.needs.rest.CurLevelPercentage * 100f : -1f, 0)
                .Num("joy", p.needs?.joy != null ? p.needs.joy.CurLevelPercentage * 100f : -1f, 0)
                .Str("mental", p.MentalStateDef?.label)
                .Bool("downed", p.Downed)
                .Bool("drafted", p.Drafted)
                .Num("breakAt", p.mindState?.mentalBreaker != null ? p.mindState.mentalBreaker.BreakThresholdMinor * 100f : -1f, 0)
                .Raw("hurts", Json.Array(Hurts(p)));
            return j.ToString();
        }

        private static List<string> Hurts(Pawn p)
        {
            List<string> rows = new List<string>();
            foreach (Hediff h in p.health.hediffSet.hediffs)
            {
                if (h.IsPermanent()) continue;   // old scars: history, not a problem to act on
                bool injury = h is Hediff_Injury;
                if (!injury && !h.def.makesSickThought && !h.def.tendable && !(h is Hediff_MissingPart)) continue;
                if (rows.Count >= 6) break;
                rows.Add(Json.Quote(h.LabelCap + (h.Part != null ? " (" + h.Part.Label + ")" : "") + (h.TendableNow() ? " [needs tending]" : "")));
            }
            return rows;
        }

        /// <summary>Everything about one colonist: skills, traits, thoughts, health, gear, work.</summary>
        public static Json ColonistFull(Args a)
        {
            Pawn p = Commands.FindPawn(a.Str("pawn"));
            Json j = new Json().Raw("brief", ColonistBrief(p));
            if (p.story != null)
            {
                j.Str("backstory", (p.story.Childhood?.TitleCapFor(p.gender) ?? "") + " / " + (p.story.Adulthood?.TitleCapFor(p.gender) ?? ""))
                 .Strs("traits", p.story.traits.allTraits.Select(t => t.LabelCap.ToString()));
            }
            if (p.skills != null)
            {
                Json sk = new Json();
                foreach (SkillRecord s in p.skills.skills)
                    sk.Str(s.def.defName, s.TotallyDisabled ? "x" : s.Level + (s.passion == Passion.Major ? "++" : s.passion == Passion.Minor ? "+" : ""));
                j.Raw("skills", sk.ToString());
            }
            j.Strs("incapable", DefDatabase<WorkTypeDef>.AllDefsListForReading.Where(w => p.WorkTypeIsDisabled(w)).Select(w => w.defName));
            if (p.needs?.mood != null)
            {
                List<Thought> groups = new List<Thought>();
                p.needs.mood.thoughts.GetDistinctMoodThoughtGroups(groups);
                Json th = new Json();
                foreach (Thought t in groups.OrderBy(t => p.needs.mood.thoughts.MoodOffsetOfGroup(t)))
                {
                    float off = p.needs.mood.thoughts.MoodOffsetOfGroup(t);
                    if (Math.Abs(off) >= 0.5f) th.Num(t.LabelCap.ToString().StripTags(), off, 0);
                }
                j.Raw("thoughts", th.ToString());
            }
            j.Strs("health", p.health.hediffSet.hediffs.Select(h => h.LabelCap + (h.Part != null ? " (" + h.Part.Label + ")" : "")));
            j.Str("weapon", p.equipment?.Primary?.LabelCap);
            j.Strs("apparel", p.apparel?.WornApparel.Select(x => x.LabelCap.ToString()));
            j.Strs("inventory", p.inventory?.innerContainer.Select(x => x.LabelCap.ToString()));
            return j;
        }

        internal static IEnumerable<Pawn> Hostiles(Map map)
        {
            return map.mapPawns.AllPawnsSpawned.Where(p => !p.Downed && !p.Dead && p.HostileTo(Faction.OfPlayer)
                && !p.IsPrisonerOfColony && (p.RaceProps.Humanlike || p.RaceProps.IsMechanoid || p.InAggroMentalState || p.Faction != null));
        }

        private static readonly FieldInfo ActiveAlertsField = typeof(AlertsReadout).GetField("activeAlerts", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>The alerts column on the right of the screen, as the player reads it.</summary>
        private static List<string> Alerts()
        {
            List<string> rows = new List<string>();
            try
            {
                if (!(Find.UIRoot is UIRoot_Play ui) || ActiveAlertsField == null) return rows;
                if (!(ActiveAlertsField.GetValue(ui.alerts) is List<Alert> active)) return rows;
                foreach (Alert al in active)
                    rows.Add(new Json().Str("label", al.GetLabel().StripTags()).Str("pri", al.Priority.ToString()).ToString());
            }
            catch (Exception)
            {
            }
            return rows;
        }

        // ------------------------------------------------------------------ the map as text

        /// <summary>
        /// ASCII map, north at the top. rect [x,z,w,h] (default whole map), step n = one char per
        /// n×n block (overview). In a block, anything "important" (pawn, building, blueprint, ore)
        /// wins; otherwise the most common character.
        /// </summary>
        public static Json MapAscii(Args a)
        {
            Map map = Home();
            CellRect r = RectArg(a, map, map.BoundsRect());
            int step = Math.Max(1, a.Int("step", 1));
            var rows = new List<string>();

            rows.Add("x: " + r.minX + ".." + r.maxX + " (step " + step + ", north up)");

            Dictionary<char, int> tally = new Dictionary<char, int>();
            for (int z = r.maxZ; z >= r.minZ; z -= step)
            {
                StringBuilder sb = new StringBuilder();
                sb.Append(z.ToString().PadLeft(4)).Append("  ");
                for (int x = r.minX; x <= r.maxX; x += step)
                {
                    if (step == 1)
                    {
                        sb.Append(Glyph(new IntVec3(x, 0, z), map));
                        continue;
                    }
                    tally.Clear();
                    char best = ' ';
                    int bestRank = -1;
                    for (int dz = 0; dz < step; dz++)
                        for (int dx = 0; dx < step; dx++)
                        {
                            IntVec3 c = new IntVec3(x + dx, 0, z - dz);
                            if (!c.InBounds(map)) continue;
                            char g = Glyph(c, map);
                            int rank = Rank(g);
                            if (rank > bestRank) { bestRank = rank; best = g; }
                            tally.TryGetValue(g, out int n);
                            tally[g] = n + 1;
                        }
                    sb.Append(bestRank >= 50 ? best : tally.Count > 0 ? tally.OrderByDescending(kv => kv.Value).First().Key : ' ');
                }
                rows.Add(sb.ToString());
            }
            // Column ruler: the tens digit every 10 cells.
            StringBuilder ruler = new StringBuilder("      ");
            StringBuilder ones = new StringBuilder("      ");
            for (int x = r.minX; x <= r.maxX; x += step)
            {
                // A tick wherever a multiple of 10 falls inside this column's block.
                ruler.Append((x + step - 1) / 10 > (x - 1) / 10 || x == 0 ? (char)('0' + ((x + step - 1) / 10) % 10) : ' ');
                ones.Append((char)('0' + x % 10));
            }
            rows.Add(ruler.ToString());
            if (step == 1) rows.Add(ones.ToString());
            return new Json().Raw("rows", Json.Array(rows.Select(Json.Quote).ToList())).Str("legend", Legend);
        }

        internal const string Legend =
            "@ colonist  ! hostile  A colony animal  a wild animal  h other person | " +
            "# wall(ours)  + door  b bed  B other building(ours)  X wall/building(not ours)  p blueprint  f frame | " +
            "R rock  $ ore/mineable resource  ^ cave floor (thick roof)  * item  c chunk | " +
            "T tree  v wild harvestable plant  , crop | g growing zone  s stockpile | " +
            "\" rich soil  . soil  : poor soil(gravel/sand)  ' no fertility  _ built floor  ~ shallow water  = deep water  % marsh/mud  (space) fog";

        private static int Rank(char g)
        {
            switch (g)
            {
                case '!': return 100;
                case '@': return 95;
                case '$': return 80;
                case 'A': case 'a': case 'h': return 70;
                case '#': case '+': case 'b': case 'B': case 'X': return 65;
                case 'p': case 'f': return 60;
                case '*': return 55;
                default: return 0;
            }
        }

        internal static char Glyph(IntVec3 c, Map map)
        {
            if (c.Fogged(map)) return ' ';
            List<Thing> things = map.thingGrid.ThingsListAtFast(c);
            char item = '\0', plant = '\0', bp = '\0';
            for (int i = 0; i < things.Count; i++)
            {
                Thing t = things[i];
                if (t is Pawn p)
                {
                    if (p.HostileTo(Faction.OfPlayer) && !p.Downed) return '!';
                    if (p.IsColonist) return '@';
                    if (p.RaceProps.Animal) return p.Faction == Faction.OfPlayer ? 'A' : 'a';
                    return 'h';
                }
            }
            Building ed = c.GetEdifice(map);
            if (ed != null)
            {
                BuildingProperties bprops = ed.def.building;
                if (bprops != null && bprops.isNaturalRock)
                    return bprops.isResourceRock || (bprops.mineableThing != null && !bprops.mineableThing.IsWithinCategory(ThingCategoryDefOf.StoneChunks)) ? '$' : 'R';
                bool ours = ed.Faction == Faction.OfPlayer;
                if (ed.def.IsDoor) return ours ? '+' : 'X';
                if (ed.def.IsBed) return ours ? 'b' : 'X';
                if (!ours) return 'X';
                return bprops != null && ed.def.passability == Traversability.Impassable && ed.def.fillPercent >= 0.99f ? '#' : 'B';
            }
            for (int i = 0; i < things.Count; i++)
            {
                Thing t = things[i];
                if (t is Building b && !(b is Frame))
                {
                    if (b.def.IsBed) return b.Faction == Faction.OfPlayer ? 'b' : 'X';
                    if (b.Faction == Faction.OfPlayer) return 'B';
                    if (b.def.category == ThingCategory.Building && !(b is Building_Trap)) return 'X';
                }
                else if (t is Blueprint) bp = 'p';
                else if (t is Frame) bp = 'f';
                else if (t is Plant pl)
                {
                    if (pl.def.plant.IsTree) plant = 'T';
                    else if (pl.sown) plant = ',';
                    else if (pl.def.plant.harvestedThingDef != null && pl.def.plant.harvestYield > 0 && !pl.def.plant.isStump && pl.def.ingestible != null) plant = plant == '\0' ? 'v' : plant;
                }
                else if (t.def.category == ThingCategory.Item)
                    item = t.def.thingCategories != null && t.def.thingCategories.Any(tc => tc.defName.Contains("Chunk")) ? 'c' : '*';
            }
            if (bp != '\0') return bp;
            if (item != '\0') return item;
            if (plant != '\0') return plant;
            Zone zone = map.zoneManager.ZoneAt(c);
            if (zone is Zone_Growing) return 'g';
            if (zone is Zone_Stockpile) return 's';
            RoofDef roof = map.roofGrid.RoofAt(c);
            if (roof != null && roof.isThickRoof) return '^';
            TerrainDef terr = map.terrainGrid.TerrainAt(c);
            if (terr.IsWater) return terr.passability == Traversability.Impassable || terr.defName.Contains("Deep") ? '=' : '~';
            if (terr.defName.Contains("Marsh") || terr.defName.Contains("Mud")) return '%';
            if (terr.BuildableByPlayer || terr.layerable) return '_';
            float fert = map.fertilityGrid.FertilityAt(c);
            if (fert >= 1.3f) return '"';
            if (fert >= 0.95f) return '.';
            if (fert > 0f) return ':';
            return '\'';
        }

        /// <summary>Things in a rect (default whole map), grouped by def: items, buildings, plants, pawns.</summary>
        public static Json Things(Args a)
        {
            Map map = Home();
            CellRect r = RectArg(a, map, map.BoundsRect());
            string group = a.Str("group", "items");
            string filter = a.Str("filter", "");
            int maxPos = a.Int("positions", 6);
            var byDef = new Dictionary<string, List<Thing>>();
            foreach (Thing t in map.listerThings.AllThings)
            {
                if (!t.Spawned || !r.Contains(t.Position) || t.Position.Fogged(map)) continue;
                bool keep;
                switch (group)
                {
                    case "items": keep = t.def.category == ThingCategory.Item; break;
                    case "buildings": keep = t.def.category == ThingCategory.Building; break;
                    case "plants": keep = t.def.category == ThingCategory.Plant; break;
                    case "pawns": keep = t is Pawn; break;
                    case "all": keep = t.def.category != ThingCategory.Mote && t.def.category != ThingCategory.Filth && t.def.category != ThingCategory.Projectile; break;
                    default: throw new LabError("group must be items/buildings/plants/pawns/all");
                }
                if (!keep) continue;
                if (filter.Length > 0 && t.def.defName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!byDef.TryGetValue(t.def.defName, out List<Thing> list)) byDef[t.def.defName] = list = new List<Thing>();
                list.Add(t);
            }
            List<string> rows = new List<string>();
            foreach (KeyValuePair<string, List<Thing>> kv in byDef.OrderByDescending(kv => kv.Value.Sum(t => t.stackCount)))
            {
                List<Thing> l = kv.Value;
                Json j = new Json().Str("def", kv.Key).Str("label", l[0].def.label).Num("n", l.Count).Num("total", l.Sum(t => t.stackCount));
                if (l.Any(t => t.IsForbidden(Faction.OfPlayer))) j.Num("forbidden", l.Count(t => t.IsForbidden(Faction.OfPlayer)));
                if (l[0] is Plant) j.Num("growthPct", l.Average(t => ((Plant)t).Growth) * 100f, 0).Num("harvestable", l.Count(t => ((Plant)t).HarvestableNow));
                j.Strs("at", l.Take(maxPos).Select(t => t.Position.x + "," + t.Position.z + (t is Pawn p ? " " + p.LabelShort + " " + t.ThingID : t.def.category == ThingCategory.Building && t.Faction == Faction.OfPlayer ? " " + t.ThingID : "")));
                rows.Add(j.ToString());
            }
            return new Json().Str("group", group).Num("defs", rows.Count).Raw("things", Json.Array(rows));
        }

        /// <summary>What can be built right now (research done), with size, stuff and cost.</summary>
        public static Json Buildables(Args a)
        {
            string filter = a.Str("filter", "");
            string cat = a.Str("category", "");
            bool all = a.Bool("all");
            List<string> rows = new List<string>();
            IEnumerable<BuildableDef> defs = DefDatabase<ThingDef>.AllDefs.Cast<BuildableDef>().Concat(DefDatabase<TerrainDef>.AllDefs.Cast<BuildableDef>());
            foreach (BuildableDef d in defs)
            {
                if (!d.BuildableByPlayer) continue;
                if (!all && !d.IsResearchFinished) continue;
                if (filter.Length > 0 && d.defName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 && (d.label ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (cat.Length > 0 && !string.Equals(d.designationCategory?.defName, cat, StringComparison.OrdinalIgnoreCase)) continue;
                Json j = new Json().Str("def", d.defName).Str("label", d.label).Str("cat", d.designationCategory?.defName);
                if (d is ThingDef td)
                {
                    j.Str("size", td.size.x + "x" + td.size.z);
                    if (td.MadeFromStuff) j.Num("stuffCount", td.costStuffCount).Strs("stuff", td.stuffCategories?.Select(s => s.defName));
                }
                if (d.costList != null) j.Strs("cost", d.costList.Select(c => c.thingDef.defName + "x" + c.count));
                if (!d.IsResearchFinished) j.Bool("locked", true);
                rows.Add(j.ToString());
            }
            return new Json().Num("count", rows.Count).Raw("defs", Json.Array(rows));
        }

        // ------------------------------------------------------------------ helpers

        internal static Map Home()
        {
            if (Current.ProgramState != ProgramState.Playing) throw new LabError("no game running (state " + Current.ProgramState + ")");
            Map m = Find.AnyPlayerHomeMap ?? Find.CurrentMap;
            if (m == null) throw new LabError("no home map");
            return m;
        }

        /// <summary>rect [x,z,w,h] | at [x,z] + radius | default.</summary>
        internal static CellRect RectArg(Args a, Map map, CellRect fallback)
        {
            List<object> rect = a.List("rect");
            if (rect.Count == 4)
                return new CellRect(Convert.ToInt32(rect[0]), Convert.ToInt32(rect[1]), Convert.ToInt32(rect[2]), Convert.ToInt32(rect[3])).ClipInsideMap(map);
            List<object> at = a.List("at");
            if (at.Count == 2 && a.Has("radius"))
            {
                int rad = a.Int("radius");
                return CellRect.CenteredOn(new IntVec3(Convert.ToInt32(at[0]), 0, Convert.ToInt32(at[1])), rad).ClipInsideMap(map);
            }
            return fallback;
        }

        internal static T Need<T>(string defName, string what) where T : Def
        {
            if (string.IsNullOrEmpty(defName)) throw new LabError("missing " + what);
            T def = DefDatabase<T>.GetNamedSilentFail(defName);
            if (def == null) throw new LabError("unknown " + what + " '" + defName + "'");
            return def;
        }
    }
}
