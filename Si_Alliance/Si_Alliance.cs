/*
 Si_Alliance - in-game alliances between two of the three teams in a Humans vs Humans vs Aliens round.
 Server-only. Vanilla primitive: Team.FriendlyIndex (teams sharing an index > -1 are not enemies for every
 GameMode.GetTeamsAreEnemy / GetTeamsAreFriendly check: AI targeting, turrets, detection sharing, pings).

   !ally <team>            commander: propose an alliance to that team
   !ally accept|decline    commander of the asked team, fps_vote off (also !ally respond accept|decline)
   !ally yes|no            players of a voting team (fps_vote both|lone; accept/decline count as votes)
   !ally break             commander of an allied team: alliance ends after break_grace_s (not a locked one)
   !ally status            anyone
   !allyadmin force <A> <B> [lock] | clear [--force] | mult <x> | status | reload      (Power.Teams)

 Mods: AllianceApi (ForceAlliance / ClearForced / IsActive / IsLocked / Describe), e.g. Si_VersusVote's locked 2v1 round.
 Ally reveal sweep (0.3.3, Reveal.cs): re-sends TARGET_DETECTED for ally objects spawned during the alliance (vanilla's own
 reveal reaches clients before the object's spawn packet and is dropped).

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
        public const string Version = "0.3.3";
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
                HelperMethods.RegisterAdminCommand("allyadmin", Commands.Command_AllyAdmin, Power.Teams,
                    "Alliance admin. Usage: !allyadmin force <teamA> <teamB> [lock] | clear [--force] | mult <x> | status | reload");
                Event_Damage.OnPrePlayerDamageReceived += DamagePatches.OnPrePlayerDamage;
                DamagePatches.Apply(HarmonyInstance);
                RoundPatches.Apply(HarmonyInstance);
                _registered = true;
                MelonLogger.Msg("[ALLY] Si_Alliance v" + Version + " loaded (enabled " + Cfg.Enabled + ", ai_policy " + Cfg.AiPolicy + ", fps_vote " + Cfg.FpsVote +
                                ", lone_income_mult " + Cfg.LoneIncomeMult.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + ", locked_end " + Cfg.LockedEnd + ", reveal_allies " + Cfg.RevealAllies + "), !ally / !allyadmin registered, AllianceApi v" + AllianceApi.ApiVersion);
            }
            catch (Exception ex) { MelonLogger.Error("[ALLY] init failed: " + ex); }
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            if (!_registered) return;
            Alliance.Dissolve("scene_load", "-", announce: false);
            Alliance.OnRestart();
            GameEvents.OnGameStarted -= OnGameStarted; GameEvents.OnGameStarted += OnGameStarted;
            GameEvents.OnGameEnded -= OnGameEnded; GameEvents.OnGameEnded += OnGameEnded;
            GameEvents.OnResourcesHarvested -= Balance.OnResourcesHarvested; GameEvents.OnResourcesHarvested += Balance.OnResourcesHarvested;
            GameEvents.OnPlayerJoined -= Reveal.OnPlayerJoined; GameEvents.OnPlayerJoined += Reveal.OnPlayerJoined;
        }

        static void OnGameStarted(GameMode mode)
        {
            try
            {
                Alliance.OnRoundStarted();   // keeps an alliance formed before the start (AllianceApi at INIT)
            }
            catch (Exception ex) { MelonLogger.Error("[ALLY] OnGameStarted: " + ex); }
        }

        static void OnGameEnded(GameMode mode, Team winner)
        {
            try
            {
                Alliance.OnRoundEnded(winner);
            }
            catch (Exception ex) { MelonLogger.Error("[ALLY] OnGameEnded: " + ex); }
        }

        public override void OnUpdate()
        {
            if (!_registered) return;
            float now = Time.time;
            if (Alliance.IntroArmed)
            {
                try { Alliance.IntroTick(now); }
                catch (Exception ex) { Alliance.IntroArmed = false; MelonLogger.Error("[ALLY] intro: " + ex); }
            }
            // ally reveal sweep (0.3.3): its own interval, also during MissionState INIT (pre-start 2v1: HQs spawn on the first team join)
            if (Alliance.Active && Cfg.RevealAllies)
            {
                try { Reveal.Tick(now); }
                catch (Exception ex) { Reveal.Fail(ex); }
            }
            if (Alliance.State == AllyState.None) return;
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

        // Broadcast through the game's own server chat (Player.SendServerChatMessage, the path of vanilla's
        // "'X' is now the Commander" lines): one call, every client, and it also raises GameEvents.OnChatMessage so
        // chat loggers record it. Not the Admin Mod per-player helper, which the ModUI proxy captures for the caller.
        internal static void ToAll(string msg)
        {
            int n = 0; try { n = Player.Players.Count; } catch { }
            MelonLogger.Msg("[ALLY] announce to " + n + " players: " + msg);
            try { Player.SendServerChatMessage(Tag + msg); }
            catch (Exception ex)
            {
                MelonLogger.Warning("[ALLY] SendServerChatMessage failed (" + ex.GetBaseException().Message + "), falling back to per-player send");
                HelperMethods.SendChatMessageToAll(Tag + msg);
            }
        }

        internal static void Debug(string msg)
        {
            if (Cfg.Debug) MelonLogger.Msg("[ALLY] " + msg);
        }
    }
}
