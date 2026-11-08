using HarmonyLib;
using MelonLoader;
using System;
using System.Reflection;

namespace Si_Alliance
{
    /// <summary>
    /// Round lifecycle patches for locked alliances (0.3.0):
    ///  - prefix MP_Strategy.Restart(): a same-map replay (next-map vote picks the current level -> Restart_Synced) loads no
    ///    scene, so the old round's alliance, lock and reported winner are dropped here (also runs at every scene start);
    ///  - postfix MP_Strategy.GetWinningTeam(): after a locked humans_win end vanilla sees two surviving teams and returns
    ///    null; the server then reports Alliance.ForcedWinner (OnGameEnded, Elo, loggers). Only replaces a null result.
    /// </summary>
    internal static class RoundPatches
    {
        internal static void Apply(HarmonyLib.Harmony harmony)
        {
            Patch(harmony, AccessTools.Method(typeof(MP_Strategy), "Restart", Type.EmptyTypes), nameof(Prefix_Restart), null, "MP_Strategy.Restart");
            Patch(harmony, AccessTools.Method(typeof(MP_Strategy), "GetWinningTeam", Type.EmptyTypes), null, nameof(Postfix_GetWinningTeam), "MP_Strategy.GetWinningTeam");
        }

        static void Patch(HarmonyLib.Harmony harmony, MethodInfo? target, string? prefix, string? postfix, string label)
        {
            if (target == null) { MelonLogger.Warning("[ALLY] " + label + " not found: not patched"); return; }
            try
            {
                harmony.Patch(target,
                    prefix: prefix != null ? new HarmonyMethod(typeof(RoundPatches), prefix) : null,
                    postfix: postfix != null ? new HarmonyMethod(typeof(RoundPatches), postfix) : null);
                AllianceMod.Debug("patched " + label);
            }
            catch (Exception ex) { MelonLogger.Warning("[ALLY] patching " + label + " failed: " + ex.Message); }
        }

        static void Prefix_Restart()
        {
            if (!AllianceMod._registered || !Game.GetIsServer()) return;
            try { Alliance.OnRestart(); }
            catch (Exception ex) { MelonLogger.Error("[ALLY] Restart prefix: " + ex); }
        }

        static void Postfix_GetWinningTeam(ref Team __result)
        {
            if (__result != null) return;
            Team? w = Alliance.ForcedWinner;
            if (w != null) __result = w;
        }
    }
}
