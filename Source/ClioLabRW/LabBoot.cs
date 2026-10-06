using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using Verse;

// Entry point and plumbing. Runs once after defs load: prepare <SaveDataFolder>/ClioLab/, tap the
// Unity log, and park a DontDestroyOnLoad GameObject whose Update() drains the inbox every frame in
// every scene (main menu included). No Harmony, no Game/Map components: nothing reaches a save.
//
// Layout under <SaveDataFolder>/ClioLab/:
//   inbox/<id>.json     one command per file; the CLI writes <id>.tmp then renames, so a half-written
//                       file is never picked up
//   results/<id>.json   one result per command ({"id","ok","error"?, ...payload})
//   renders/            PNGs from render/shot
//   feed.ndjson         boot line, every command outcome, every warning/error the game logs

namespace ClioLabRW
{
    [StaticConstructorOnStartup]
    public static class LabBoot
    {
        public const string PackageId = "lumenastrum.cliolabrw";
        public const string Version = "0.1.0";

        static LabBoot()
        {
            LabIO.Init();
            LogTap.Install();

            // Unity objects must be made on the main thread; static constructors may run inside a
            // long event, so defer to the point where loading has finished.
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                GameObject go = new GameObject("ClioLabDriver");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<LabDriver>();
            });

            string rimworld;
            try { rimworld = RimWorld.VersionControl.CurrentVersionStringWithRev; }
            catch (Exception) { rimworld = "unknown"; }

            Log.Message("[ClioLab] v" + Version + " loaded, dll written " + DllStamp() + ", lab: " + LabIO.Root);
            LabIO.Feed(new Json()
                .Str("ev", "mod_boot")
                .Str("modver", Version)
                .Str("dll", DllStamp())
                .Str("rimworld", rimworld)
                .Str("wall", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                .ToString());
        }

        internal static string DllStamp()
        {
            try
            {
                foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
                {
                    if (!string.Equals(mod.PackageId, PackageId, StringComparison.OrdinalIgnoreCase)) continue;
                    string dll = Path.Combine(mod.RootDir, "Assemblies", "ClioLabRW.dll");
                    if (File.Exists(dll)) return File.GetLastWriteTime(dll).ToString("yyyy-MM-dd HH:mm:ss");
                }
            }
            catch (Exception)
            {
            }
            return "unknown";
        }
    }

    internal static class LabIO
    {
        private static readonly Encoding NoBom = new UTF8Encoding(false);
        private static readonly object FeedLock = new object();

        public static string Root, Inbox, Results, Renders, FeedPath;
        public static bool Disabled;

        public static void Init()
        {
            try
            {
                Root = Path.Combine(GenFilePaths.SaveDataFolderPath, "ClioLab");
                Inbox = Path.Combine(Root, "inbox");
                Results = Path.Combine(Root, "results");
                Renders = Path.Combine(Root, "renders");
                Directory.CreateDirectory(Inbox);
                Directory.CreateDirectory(Results);
                Directory.CreateDirectory(Renders);
                FeedPath = Path.Combine(Root, "feed.ndjson");
                // Commands left over from a previous run are stale by definition.
                foreach (string f in Directory.GetFiles(Inbox)) File.Delete(f);
                if (File.Exists(FeedPath) && new FileInfo(FeedPath).Length > 5L * 1024 * 1024) File.WriteAllText(FeedPath, "");
            }
            catch (Exception e)
            {
                Disabled = true;
                Log.Warning("[ClioLab] disabled, could not prepare the lab folder: " + e.Message);
            }
        }

        /// <summary>Thread-safe: the log tap calls this from Unity's logging threads.</summary>
        public static void Feed(string jsonLine)
        {
            if (Disabled || FeedPath == null) return;
            try
            {
                lock (FeedLock) File.AppendAllText(FeedPath, jsonLine + "\n", NoBom);
            }
            catch (Exception)
            {
                // Never log from here: the tap would feed the failure straight back in.
            }
        }

        public static void WriteResult(string id, string json)
        {
            string tmp = Path.Combine(Results, id + ".tmp");
            File.WriteAllText(tmp, json, NoBom);
            string final = Path.Combine(Results, id + ".json");
            if (File.Exists(final)) File.Delete(final);
            File.Move(tmp, final);
        }
    }

    /// <summary>
    /// Every warning, error and exception Unity sees, Verse.Log or not, lands in the feed with its
    /// stack. Errors are what the lab is for; plain messages only when they carry a mod tag worth
    /// keeping, so a 400-mod boot doesn't drown the signal.
    /// </summary>
    internal static class LogTap
    {
        private static long _seq;
        public static long Errors, Warnings;

        public static void Install()
        {
            Application.logMessageReceivedThreaded += OnLog;
        }

        private static void OnLog(string condition, string stackTrace, LogType type)
        {
            string level;
            switch (type)
            {
                case LogType.Error:
                case LogType.Assert:
                case LogType.Exception:
                    level = "error"; System.Threading.Interlocked.Increment(ref Errors); break;
                case LogType.Warning:
                    level = "warn"; System.Threading.Interlocked.Increment(ref Warnings); break;
                default:
                    if (condition == null || !(condition.StartsWith("[Heyra") || condition.StartsWith("[ClioLab") || condition.StartsWith("[HAR"))) return;
                    level = "info"; break;
            }
            long n = System.Threading.Interlocked.Increment(ref _seq);
            string msg = condition ?? "";
            if (msg.Length > 4000) msg = msg.Substring(0, 4000) + "…";
            string stack = stackTrace ?? "";
            if (stack.Length > 4000) stack = stack.Substring(0, 4000) + "…";
            LabIO.Feed(new Json()
                .Str("ev", "log")
                .Num("seq", n)
                .Str("level", level)
                .Str("msg", msg)
                .Str("stack", stack.Length > 0 ? stack : null)
                .Num("frame", Time.frameCount)
                .Str("wall", DateTime.Now.ToString("HH:mm:ss.fff"))
                .ToString());
        }
    }

    /// <summary>Per-frame inbox drain plus a tiny frame-delayed job queue (camera moves need a frame
    /// before a screenshot sees them).</summary>
    internal sealed class LabDriver : MonoBehaviour
    {
        private float _nextPoll;
        private static readonly List<Delayed> Jobs = new List<Delayed>();

        private sealed class Delayed
        {
            public int RunAtFrame;
            public Action Run;
        }

        public static void After(int frames, Action run)
        {
            Jobs.Add(new Delayed { RunAtFrame = Time.frameCount + Math.Max(1, frames), Run = run });
        }

        private void Update()
        {
            for (int i = Jobs.Count - 1; i >= 0; i--)
            {
                if (Time.frameCount < Jobs[i].RunAtFrame) continue;
                Delayed job = Jobs[i];
                Jobs.RemoveAt(i);
                try { job.Run(); }
                catch (Exception e) { Log.Warning("[ClioLab] delayed job failed: " + e); }
            }

            try { Clock.Update(); }
            catch (Exception e) { Log.Warning("[ClioLab] clock failed: " + e); }

            if (LabIO.Disabled || Time.realtimeSinceStartup < _nextPoll) return;
            _nextPoll = Time.realtimeSinceStartup + 0.2f;
            if (LongEventHandler.AnyEventNowOrWaiting) return;

            string[] files;
            try { files = Directory.GetFiles(LabIO.Inbox, "*.json"); }
            catch (Exception) { return; }
            if (files.Length == 0) return;
            System.Array.Sort(files, StringComparer.Ordinal);

            // One command per frame keeps a burst of renders from hitching the game.
            Commands.Handle(files[0]);
        }
    }
}
