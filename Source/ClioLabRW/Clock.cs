using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;

// Overseer, part 3: time and the things that interrupt it. The game lives paused; `advance` runs it
// for N hours and stops EARLY the moment something wants a player: a new letter, a new window
// (event dialog, research done, naming), a new hostile, a colonist downed / broken / dead. That is
// what turns real-time RimWorld into turns I can think between. Letters and dialogs are then
// answered through the game's own option code (DiaOption.Activate), the same path as a click.

namespace ClioLabRW
{
    internal static class Clock
    {
        private sealed class Run
        {
            public string Id, Cmd;
            public int StartTick, TargetTick, Letters0, Colonists0;
            public float WallDeadline;
            public TimeSpeed Speed;
            public HashSet<Window> Windows0;
            public HashSet<int> Hostiles0, Downed0, Mental0;
            public Dictionary<int, float> Health0;
        }

        private static Run _run;

        public static void Start(string id, string cmd, Args a)
        {
            Map map = Colony.Home();
            if (_run != null) throw new LabError("an advance is already running");
            TickManager tm = Find.TickManager;
            float hours = Mathf.Max(0.1f, a.Num("hours", 6f));
            _run = new Run
            {
                Id = id,
                Cmd = cmd,
                StartTick = tm.TicksGame,
                TargetTick = tm.TicksGame + Mathf.RoundToInt(hours * GenDate.TicksPerHour),
                Letters0 = Find.LetterStack.LettersListForReading.Count,
                Colonists0 = PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists.Count(),
                WallDeadline = Time.realtimeSinceStartup + a.Num("wallSec", 240f),
                Speed = (TimeSpeed)Mathf.Clamp(a.Int("speed", 3), 1, 4),
                Windows0 = new HashSet<Window>(Find.WindowStack.Windows),
                Hostiles0 = new HashSet<int>(Colony.Hostiles(map).Select(p => p.thingIDNumber)),
                Downed0 = new HashSet<int>(map.mapPawns.FreeColonistsSpawned.Where(p => p.Downed).Select(p => p.thingIDNumber)),
                Mental0 = new HashSet<int>(map.mapPawns.FreeColonistsSpawned.Where(p => p.InMentalState).Select(p => p.thingIDNumber)),
                Health0 = map.mapPawns.FreeColonistsSpawned.ToDictionary(p => p.thingIDNumber, p => p.health.summaryHealth.SummaryHealthPercent),
            };
            Commands.CloseLog();
            tm.CurTimeSpeed = _run.Speed;
        }

        /// <summary>Called every frame by LabDriver.</summary>
        public static void Update()
        {
            Run r = _run;
            if (r == null) return;
            if (Current.ProgramState != ProgramState.Playing || Find.AnyPlayerHomeMap == null)
            {
                _run = null;
                Commands.Fail(r.Id, r.Cmd, "game left Playing during advance");
                return;
            }
            string why = null;
            try { why = Check(r); }
            catch (Exception e) { why = "check failed: " + e.Message; }
            if (why == null) return;

            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            _run = null;
            Map map = Find.AnyPlayerHomeMap;
            List<Letter> all = Find.LetterStack.LettersListForReading;
            List<string> fresh = new List<string>();
            for (int i = r.Letters0; i < all.Count; i++) fresh.Add(LetterJson(all[i], i, true));
            Vector2 ll = Find.WorldGrid.LongLatOf(map.Tile);
            Commands.Finish(r.Id, r.Cmd, new Json()
                .Str("stop", why)
                .Num("hours", (Find.TickManager.TicksGame - r.StartTick) / (float)GenDate.TicksPerHour, 1)
                .Str("date", GenDate.DateFullStringWithHourAt(Find.TickManager.TicksAbs, ll))
                .Raw("newLetters", Json.Array(fresh))
                .Raw("windows", Json.Array(WindowRows()))
                .Raw("hostiles", Json.Array(Colony.Hostiles(map).Take(30).Select(p =>
                    new Json().Str("id", p.ThingID).Str("what", p.LabelShort).Str("faction", p.Faction?.Name).Str("pos", p.Position.x + "," + p.Position.z)
                        .Str("weapon", p.equipment?.Primary?.def.label).ToString()).ToList())));
        }

        private static string Check(Run r)
        {
            TickManager tm = Find.TickManager;
            Map map = Find.AnyPlayerHomeMap;
            if (tm.TicksGame >= r.TargetTick) return "time";
            if (Find.LetterStack.LettersListForReading.Count > r.Letters0)
                return "letter: " + Find.LetterStack.LettersListForReading.Last().Label.Resolve().StripTags();
            foreach (Window w in Find.WindowStack.Windows)
                // Tabs and info cards are someone (Andres) clicking around in the window, not the game asking.
                if (!r.Windows0.Contains(w) && !(w is LudeonTK.EditWindow_Log) && !(w is ImmediateWindow) && !(w is MainTabWindow) && !(w is Dialog_InfoCard))
                    return "window: " + w.GetType().Name;
            foreach (Pawn p in Colony.Hostiles(map))
                if (!r.Hostiles0.Contains(p.thingIDNumber)) return "hostile: " + p.LabelShort + (p.Faction != null ? " (" + p.Faction.Name + ")" : "");
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                if (p.Downed && !r.Downed0.Contains(p.thingIDNumber)) return "downed: " + p.LabelShort;
                if (p.InMentalState && !r.Mental0.Contains(p.thingIDNumber)) return "mental: " + p.LabelShort + " " + p.MentalStateDef?.label;
                // Any fresh harm, even a hare bite far from home (10/4: Cipier was mauled for hours
                // by a hostile that was already on the map when the advance began, so nothing fired).
                if (r.Health0.TryGetValue(p.thingIDNumber, out float hp0) && p.health.summaryHealth.SummaryHealthPercent < hp0 - 0.015f)
                    return "hurt: " + p.LabelShort + " " + (p.health.summaryHealth.SummaryHealthPercent * 100f).ToString("0") + "%";
            }
            if (PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists.Count() < r.Colonists0) return "colonist lost";
            if (Time.realtimeSinceStartup > r.WallDeadline) return "wall clock cap (call advance again)";
            // Someone paused by hand (Andres watching the window, maybe): respect it.
            if (tm.CurTimeSpeed == TimeSpeed.Paused) return "paused externally";
            if (tm.CurTimeSpeed != r.Speed && !tm.Paused) tm.CurTimeSpeed = r.Speed;
            return null;
        }

        public static void Abort()
        {
            if (_run == null) return;
            Run r = _run;
            _run = null;
            Commands.Fail(r.Id, r.Cmd, "aborted");
        }

        // ------------------------------------------------------------------ letters

        internal static List<string> LetterRows(bool withText)
        {
            List<string> rows = new List<string>();
            if (Current.ProgramState != ProgramState.Playing) return rows;
            List<Letter> all = Find.LetterStack.LettersListForReading;
            for (int i = 0; i < all.Count; i++) rows.Add(LetterJson(all[i], i, withText));
            return rows;
        }

        private static string LetterJson(Letter l, int index, bool withText)
        {
            Json j = new Json().Num("i", index).Str("label", l.Label.Resolve().StripTags()).Str("def", l.def.defName);
            if (l is ChoiceLetter cl)
            {
                if (withText)
                {
                    string text = cl.Text.Resolve().StripTags();
                    if (text.Length > 900) text = text.Substring(0, 900) + "…";
                    j.Str("text", text);
                }
                try { j.Strs("choices", cl.Choices.Select(o => OptionText(o) + (o.disabled ? " [disabled]" : ""))); }
                catch (Exception) { }
            }
            return j.ToString();
        }

        /// <summary>letters | open i (shows its dialog, answer with `dialog`) | dismiss i | text i.</summary>
        public static Json Letters(Args a)
        {
            Colony.Home();
            List<Letter> all = Find.LetterStack.LettersListForReading;
            if (a.Has("open"))
            {
                Letter l = LetterAt(all, a.Int("open"));
                l.OpenLetter();
                return new Json().Str("opened", l.Label.Resolve().StripTags()).Raw("windows", Json.Array(WindowRows()));
            }
            if (a.Has("dismiss"))
            {
                Letter l = LetterAt(all, a.Int("dismiss"));
                Find.LetterStack.RemoveLetter(l);
                return new Json().Str("dismissed", l.Label.Resolve().StripTags());
            }
            if (a.Bool("dismissAll"))
            {
                // Only plain informational letters; anything with a choice stays for a real answer.
                int n = 0;
                foreach (Letter l in all.ToList())
                {
                    if (l is ChoiceLetter cl && cl.Choices.Count() > 1) continue;
                    Find.LetterStack.RemoveLetter(l);
                    n++;
                }
                return new Json().Num("dismissed", n).Raw("left", Json.Array(LetterRows(false)));
            }
            return new Json().Raw("letters", Json.Array(LetterRows(a.Bool("text", true))));
        }

        private static Letter LetterAt(List<Letter> all, int i)
        {
            if (i < 0 || i >= all.Count) throw new LabError("no letter #" + i + " (have " + all.Count + ")");
            return all[i];
        }

        // ------------------------------------------------------------------ windows / dialogs

        private static readonly FieldInfo CurNodeField = typeof(Dialog_NodeTree).GetField("curNode", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo OptionTextField = typeof(DiaOption).GetField("text", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo ActivateMethod = typeof(DiaOption).GetMethod("Activate", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo CurNameField = typeof(Dialog_GiveName).GetField("curName", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo CurSecondNameField = typeof(Dialog_GiveName).GetField("curSecondName", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo UseSecondNameField = typeof(Dialog_GiveName).GetField("useSecondName", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo NamedMethod = typeof(Dialog_GiveName).GetMethod("Named", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo NamedSecondMethod = typeof(Dialog_GiveName).GetMethod("NamedSecond", BindingFlags.Instance | BindingFlags.NonPublic);

        private static string OptionText(DiaOption o) => (OptionTextField?.GetValue(o) as string ?? "?").StripTags();

        internal static List<string> WindowRows()
        {
            List<string> rows = new List<string>();
            if (Find.WindowStack == null) return rows;
            int i = 0;
            foreach (Window w in Find.WindowStack.Windows)
            {
                if (w is LudeonTK.EditWindow_Log || w is ImmediateWindow) { i++; continue; }
                Json j = new Json().Num("i", i++).Str("type", w.GetType().Name);
                if (w is Dialog_NodeTree nt && CurNodeField?.GetValue(nt) is DiaNode node)
                {
                    string text = node.text.Resolve().StripTags();
                    if (text.Length > 1200) text = text.Substring(0, 1200) + "…";
                    j.Str("text", text).Strs("options", node.options.Select((o, k) => k + ": " + OptionText(o) + (o.disabled ? " [disabled: " + o.disabledReason + "]" : "")));
                }
                else if (w is Dialog_MessageBox mb)
                {
                    j.Str("text", mb.text.Resolve().StripTags()).Strs("options", new[] { mb.buttonAText, mb.buttonBText, mb.buttonCText }
                        .Select((t, k) => t == null ? null : k + ": " + t.StripTags()).Where(t => t != null));
                }
                else if (w is Dialog_GiveName gn)
                {
                    j.Str("suggested", CurNameField?.GetValue(gn) as string).Str("suggestedSecond", CurSecondNameField?.GetValue(gn) as string)
                     .Str("hint", "dialog {name, second} to accept");
                }
                rows.Add(j.ToString());
            }
            return rows;
        }

        /// <summary>
        /// Answer the top-most answerable window: choose k (node-tree option / message-box button),
        /// name + second (naming dialogs; omitted = the game's own suggestion), close true.
        /// </summary>
        public static Json Dialog(Args a)
        {
            Window w = Find.WindowStack.Windows.LastOrDefault(x => x is Dialog_NodeTree || x is Dialog_MessageBox || x is Dialog_GiveName)
                ?? Find.WindowStack.Windows.LastOrDefault(x => !(x is LudeonTK.EditWindow_Log) && !(x is ImmediateWindow));
            if (w == null) throw new LabError("no window open");
            string type = w.GetType().Name;
            if (a.Bool("close"))
            {
                Find.WindowStack.TryRemove(w);
                return new Json().Str("closed", type).Raw("windows", Json.Array(WindowRows()));
            }
            if (w is Dialog_GiveName gn)
            {
                string name = a.Str("name") ?? CurNameField?.GetValue(gn) as string;
                bool second = UseSecondNameField != null && (bool)UseSecondNameField.GetValue(gn);
                NamedMethod.Invoke(gn, new object[] { name?.Trim() });
                if (second) NamedSecondMethod.Invoke(gn, new object[] { (a.Str("second") ?? CurSecondNameField?.GetValue(gn) as string)?.Trim() });
                Find.WindowStack.TryRemove(gn);
                return new Json().Str("named", name).Str("second", second ? a.Str("second") ?? "(suggested)" : null).Raw("windows", Json.Array(WindowRows()));
            }
            if (!a.Has("choose")) throw new LabError("dialog needs choose k, name, or close (top window: " + type + ")");
            int k = a.Int("choose");
            if (w is Dialog_NodeTree nt)
            {
                DiaNode node = CurNodeField.GetValue(nt) as DiaNode;
                if (node == null || k < 0 || k >= node.options.Count) throw new LabError("no option " + k);
                DiaOption o = node.options[k];
                if (o.disabled) throw new LabError("option " + k + " is disabled: " + o.disabledReason);
                string picked = OptionText(o);
                ActivateMethod.Invoke(o, null);
                return new Json().Str("chose", picked).Raw("windows", Json.Array(WindowRows()));
            }
            if (w is Dialog_MessageBox mb)
            {
                Action act = k == 0 ? mb.buttonAAction : k == 1 ? mb.buttonBAction : mb.buttonCAction;
                string label = k == 0 ? mb.buttonAText : k == 1 ? mb.buttonBText : mb.buttonCText;
                if (label == null) throw new LabError("no button " + k);
                act?.Invoke();
                Find.WindowStack.TryRemove(mb);
                return new Json().Str("chose", label).Raw("windows", Json.Array(WindowRows()));
            }
            throw new LabError("can't answer a " + type + " (try close)");
        }

        // ------------------------------------------------------------------ save / load

        public static Json Save(Args a)
        {
            Colony.Home();
            string name = a.Str("name", "clio-colony");
            Abort();
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            LongEventHandler.QueueLongEvent(() => GameDataSaveLoader.SaveGame(name), "SavingLongEvent", false, null);
            return new Json().Str("saving", name);
        }

        public static Json Load(Args a)
        {
            string name = a.Str("name");
            if (string.IsNullOrEmpty(name)) throw new LabError("load needs name");
            if (!GenFilePaths.AllSavedGameFiles.Any(f => System.IO.Path.GetFileNameWithoutExtension(f.Name) == name))
                throw new LabError("no save '" + name + "'");
            Abort();
            // LoadGame queues its own scene change; wrapping it in another long event would nest them.
            GameDataSaveLoader.LoadGame(name);
            return new Json().Str("loading", name);
        }

        public static Json Saves()
        {
            return new Json().Strs("saves", GenFilePaths.AllSavedGameFiles.OrderByDescending(f => f.LastWriteTime)
                .Select(f => System.IO.Path.GetFileNameWithoutExtension(f.Name) + " (" + f.LastWriteTime.ToString("MM-dd HH:mm") + ")"));
        }
    }
}
