using System;
using System.Collections.Generic;
using System.IO;
using RimWorld;
using UnityEngine;
using Verse;

// Two kinds of picture:
//  * render: the pawn alone through vanilla PortraitsCache (the colonist-bar renderer), one PNG per
//    rotation on a transparent background. This is the apparel-QA view: no terrain, no UI, exact
//    layer stacking, ready for pixel checks.
//  * shot:   the real game camera, UI hidden, framed on a pawn or cell. This is the in-world view:
//    map scale, lighting, neighbours, what a player actually sees.
// Both run a couple of frames after the command so a dress/hediff change in the previous command
// has rebuilt the render tree before anything is captured.

namespace ClioLabRW
{
    internal static class Render
    {
        public static void Portraits(string id, string cmd, Pawn pawn, Args a)
        {
            List<object> rotsArg = a.List("rots");
            List<string> rots = new List<string>();
            foreach (object r in rotsArg) rots.Add(Convert.ToString(r));
            if (rots.Count == 0) rots.AddRange(new[] { "south", "east", "north" });

            List<object> sizeArg = a.List("size");
            Vector2 size = sizeArg.Count == 2
                ? new Vector2(Convert.ToSingle(sizeArg[0]), Convert.ToSingle(sizeArg[1]))
                : new Vector2(a.Num("size", 256f), a.Num("size", 256f));
            float zoom = a.Num("zoom", 1f);
            bool headgear = a.Bool("headgear", true);
            bool clothes = a.Bool("clothes", true);
            string name = Safe(a.Str("name", pawn.ThingID));

            LabDriver.After(2, () =>
            {
                try
                {
                    Commands.Dirty(pawn);
                    List<string> files = new List<string>();
                    foreach (string rs in rots)
                    {
                        Rot4 rot = ParseRot(rs);
                        RenderTexture rt = PortraitsCache.Get(pawn, size, rot, default(Vector3), zoom,
                            supersample: true, compensateForUIScale: false,
                            renderHeadgear: headgear, renderClothes: clothes);
                        string file = name + "_" + rs + ".png";
                        File.WriteAllBytes(Path.Combine(LabIO.Renders, file), ToPng(rt));
                        files.Add(file);
                    }
                    Commands.Finish(id, cmd, Commands.PawnInfo(pawn)
                        .Str("dir", LabIO.Renders)
                        .Strs("files", files)
                        .Num("w", (long)size.x).Num("h", (long)size.y));
                }
                catch (Exception e)
                {
                    Commands.Fail(id, cmd, e.GetType().Name + ": " + e.Message, e.StackTrace);
                }
            });
        }

        public static void Shot(string id, string cmd, Args a)
        {
            if (Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null)
                throw new LabError("no map loaded");
            Vector3 at = Commands.TargetPos(a);
            float camSize = a.Num("size", 8f);
            bool hideUi = a.Bool("hideUi", true);
            string name = Safe(a.Str("name", "shot_" + DateTime.Now.ToString("HHmmss_fff")));

            Commands.Aim(at, camSize);
            if (hideUi)
            {
                Commands.CloseLog();   // screenshotMode hides the HUD, not open windows
                Find.UIRoot.screenshotMode.Active = true;
            }

            LabDriver.After(a.Int("settleFrames", 6), () =>
            {
                string shotPath = Path.Combine(GenFilePaths.ScreenshotFolderPath, name + ".png");
                try
                {
                    if (File.Exists(shotPath)) File.Delete(shotPath);
                    ScreenshotTaker.TakeNonSteamShot(name);
                }
                catch (Exception e)
                {
                    Find.UIRoot.screenshotMode.Active = false;
                    Commands.Fail(id, cmd, "screenshot failed: " + e.Message);
                    return;
                }
                WaitForFile(id, cmd, shotPath, name, hideUi, 120);
            });
        }

        /// <summary>CaptureScreenshot writes at end of frame (sometimes later); poll, then move it
        /// beside the renders so the CLI has one folder to read.</summary>
        private static void WaitForFile(string id, string cmd, string shotPath, string name, bool hideUi, int framesLeft)
        {
            LabDriver.After(1, () =>
            {
                bool ready = false;
                try { ready = File.Exists(shotPath) && new FileInfo(shotPath).Length > 0; }
                catch (Exception) { }
                if (!ready && framesLeft > 0)
                {
                    WaitForFile(id, cmd, shotPath, name, hideUi, framesLeft - 1);
                    return;
                }
                if (hideUi) Find.UIRoot.screenshotMode.Active = false;
                if (!ready)
                {
                    Commands.Fail(id, cmd, "screenshot never appeared at " + shotPath);
                    return;
                }
                LabDriver.After(2, () =>   // let the encoder finish flushing before the move
                {
                    try
                    {
                        string dest = Path.Combine(LabIO.Renders, name + ".png");
                        if (File.Exists(dest)) File.Delete(dest);
                        File.Move(shotPath, dest);
                        Commands.Finish(id, cmd, new Json().Str("dir", LabIO.Renders).Strs("files", new[] { name + ".png" })
                            .Num("w", Screen.width).Num("h", Screen.height));
                    }
                    catch (Exception e)
                    {
                        Commands.Fail(id, cmd, "moving screenshot failed: " + e.Message);
                    }
                });
            });
        }

        private static byte[] ToPng(RenderTexture rt)
        {
            RenderTexture previous = RenderTexture.active;
            Texture2D tex = new Texture2D(rt.width, rt.height, TextureFormat.ARGB32, false);
            try
            {
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0f, 0f, rt.width, rt.height), 0, 0);
                tex.Apply();
                return tex.EncodeToPNG();
            }
            finally
            {
                RenderTexture.active = previous;
                UnityEngine.Object.Destroy(tex);
            }
        }

        private static Rot4 ParseRot(string s)
        {
            switch (s)
            {
                case "north": return Rot4.North;
                case "east": return Rot4.East;
                case "west": return Rot4.West;
                case "south": return Rot4.South;
                default: throw new LabError("rotation must be north/east/south/west, got '" + s + "'");
            }
        }

        private static string Safe(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }
    }
}
