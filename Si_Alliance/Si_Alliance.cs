/*
 Si_Alliance - in-game alliances between two of the three teams in a Humans vs Humans vs Aliens round.
 Server-only. Vanilla primitive: Team.FriendlyIndex (teams sharing an index > -1 are not enemies for every
 GameMode.GetTeamsAreEnemy / GetTeamsAreFriendly check: AI targeting, turrets, detection sharing, pings).

   !ally <team>            commander: propose an alliance to that team
   !ally accept|decline    commander of the asked team (also !ally respond accept|decline)
   !ally break             commander of an allied team: alliance ends after break_grace_s
   !ally status            anyone
   !allyadmin force <A> <B> | clear | mult <x> | status | reload      (Power.Cheat)

 Design and decisions: docs/PLAN.md. Requires the Admin Mod (commands, chat, damage event).
*/

using MelonLoader;
using SilicaAdminMod;
using System;
using System.Linq;
using UnityEngine;

[assembly: MelonInfo(typeof(Si_Alliance.AllianceMod), "Si_Alliance", Si_Alliance.AllianceMod.Version, "DrMuck")]
[assembly: MelonGame("Bohemia Interactive", "Silica")]

namespace Si_Alliance
{
    public partial class AllianceMod : MelonMod
    {
        public const string Version = "0.1.0";
        internal const string Tag = "<color=#F5C542>[ALLY]</color> ";
        internal static AllianceConfig Cfg = new AllianceConfig();
        internal static bool _registered;

        static float _nextTick;
        internal static bool CheckSoon;   // set by the destroyed postfixes, evaluated next frame

        public override void OnLateInitializeMelon()
        {
            Cfg = AllianceConfig.Load();
            if (!RegisteredMelons.Any(m => m.Info.Name == "Admin Mod"))
            {
                MelonLogger.Warning("[ALLY] Admin Mod not found: Si_Alliance needs its commands, chat and damage event, doing nothing");
                return;
            }
            try
            {
                HelperMethods.RegisterPlayerCommand("ally", Commands.Command_Ally, true);
                HelperMethods.RegisterAdminCommand("allyadmin", Commands.Command_AllyAdmin, Power.Cheat,
                    "Alliance admin. Usage: !allyadmin force <teamA> <teamB> | clear | mult <x> | status | reload");
                Event_Damage.OnPrePlayerDamageReceived += DamagePatches.OnPrePlayerDamage;
                DamagePatches.Apply(HarmonyInstance);
                _registered = true;
                MelonLogger.Msg("[ALLY] Si_Alliance v" + Version + " loaded (enabled " + Cfg.Enabled + ", ai_policy " + Cfg.AiPolicy +
                                ", lone_income_mult " + Cfg.LoneIncomeMult.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "), !ally / !allyadmin registered");
            }
            catch (Exception ex) { MelonLogger.Error("[ALLY] init failed: " + ex); }
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            if (!_registered) return;
            Alliance.Dissolve("scene_load", "-", announce: false);
            Alliance.ResetRound();
            GameEvents.OnGameStarted -= OnGameStarted; GameEvents.OnGameStarted += OnGameStarted;
            GameEvents.OnGameEnded -= OnGameEnded; GameEvents.OnGameEnded += OnGameEnded;
            GameEvents.OnResourcesHarvested -= Balance.OnResourcesHarvested; GameEvents.OnResourcesHarvested += Balance.OnResourcesHarvested;
        }

        static void OnGameStarted(GameMode mode)
        {
            try
            {
                Alliance.Dissolve("round_start", "-", announce: false);
                Alliance.ResetRound();
            }
            catch (Exception ex) { MelonLogger.Error("[ALLY] OnGameStarted: " + ex); }
        }

        static void OnGameEnded(GameMode mode, Team winner)
        {
            try
            {
                Alliance.Dissolve("round_end", "winner=" + (winner != null ? Alliance.NameOf(winner) : "none"), announce: false);
                Alliance.ClearPending();
            }
            catch (Exception ex) { MelonLogger.Error("[ALLY] OnGameEnded: " + ex); }
        }

        public override void OnUpdate()
        {
            if (!_registered) return;
            if (Alliance.State == AllyState.None) return;
            float now = Time.time;
            if (!CheckSoon && now < _nextTick) return;
            _nextTick = now + 1f;
            CheckSoon = false;
            try { Alliance.Tick(now); }
            catch (Exception ex) { MelonLogger.Error("[ALLY] tick: " + ex); }
        }

        // ------------------------------------------------------------------ chat helpers

        internal static void Reply(Player? p, string msg)
        {
            if (p == null) { MelonLogger.Msg("[ALLY] " + msg); return; }
            HelperMethods.SendChatMessageToPlayer(p, Tag + msg);
        }

        internal static void ToTeam(Team? t, string msg)
        {
            if (t == null) return;
            HelperMethods.SendChatMessageToTeam(t, Tag + msg);
        }

        internal static void ToAll(string msg)
        {
            HelperMethods.SendChatMessageToAll(Tag + msg);
        }

        internal static void Debug(string msg)
        {
            if (Cfg.Debug) MelonLogger.Msg("[ALLY] " + msg);
        }
    }
}
