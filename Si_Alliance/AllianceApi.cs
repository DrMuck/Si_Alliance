using MelonLoader;
using System;

namespace Si_Alliance
{
    /// <summary>
    /// Public API for other server mods (Si_VersusVote calls it by reflection). Main thread only, server only.
    /// Every call is logged with its reason; failures return false (and are logged), they never throw.
    /// </summary>
    public static class AllianceApi
    {
        public const int ApiVersion = 1;

        /// <summary>
        /// Forms the alliance teamA + teamB against the third team at once (same effect as "!allyadmin force A B"), bypassing
        /// one_alliance_per_round, enabled and the vote / accept flow. Usable from MissionState INIT right after
        /// MP_Strategy.SetTeamVersusMode(HUMANS_VS_HUMANS_VS_ALIENS) on: teams are looked up among the active team setups of
        /// that layout, no players, units or commanders are needed. An alliance formed before the start survives the round
        /// start (OnGameStarted) and is announced again then.
        /// teamA / teamB: TeamShortName or TeamName, case-insensitive; an unambiguous prefix works too ("sol", "cent", "alien").
        /// locked: players cannot "!ally break" it, admins need "!allyadmin clear --force", and when the lone team is
        /// eliminated the round ends with the allies winning together (config locked_end "humans_win", default) instead of
        /// dissolving into a final 1v1 ("dissolve").
        /// Returns false when: not the server / Admin Mod missing, no Strategy round, the round is over, the layout is not
        /// Humans vs Humans vs Aliens (3 active teams), a team is not found or both names give the same team, or another
        /// alliance (a different pair) is active. The same pair already allied returns true (locked = true then locks it).
        /// A pending request or running vote is cancelled.
        /// </summary>
        public static bool ForceAlliance(string teamA, string teamB, bool locked, string reason)
        {
            reason = (reason ?? "").Trim();
            string tag = "ForceAlliance(" + teamA + ", " + teamB + ", locked " + locked + ", '" + reason + "')";
            try
            {
                if (!AllianceMod._registered) return Fail(tag, "Si_Alliance not initialised (Admin Mod missing?)");
                if (!Game.GetIsServer()) return Fail(tag, "not the server");
                if (!(GameMode.CurrentGameMode is MP_Strategy)) return Fail(tag, "no Strategy game mode");
                if (Alliance.GameOver) return Fail(tag, "the round is over");
                if (!Alliance.IsHvHvA(out string why)) return Fail(tag, why);
                Team? a = Alliance.FindTeam(teamA ?? "", out string ea);
                if (a == null) return Fail(tag, ea);
                Team? b = Alliance.FindTeam(teamB ?? "", out string eb);
                if (b == null) return Fail(tag, eb);
                if (ReferenceEquals(a, b)) return Fail(tag, "two different teams needed");
                if (Alliance.HasLost(a) || Alliance.HasLost(b)) return Fail(tag, "a team is already eliminated");

                if (Alliance.Active)
                {
                    bool same = (ReferenceEquals(a, Alliance.A) && ReferenceEquals(b, Alliance.B)) || (ReferenceEquals(a, Alliance.B) && ReferenceEquals(b, Alliance.A));
                    if (!same) return Fail(tag, "another alliance is active: " + Alliance.Describe());
                    if (locked && !Alliance.Locked)
                    {
                        Alliance.Locked = true; Alliance.Forced = true;
                        if (reason.Length > 0) Alliance.ForceReason = reason;
                        if (Alliance.State == AllyState.Breaking) { Alliance.State = AllyState.Active; Alliance.BreakAt = 0f; Alliance.BreakBy = null; }
                        MelonLogger.Msg("[ALLY] API " + tag + ": same pair already allied, now locked");
                    }
                    else MelonLogger.Msg("[ALLY] API " + tag + ": same pair already allied, nothing to do");
                    return true;
                }

                if (Alliance.State == AllyState.Pending)
                {
                    MelonLogger.Msg("[ALLY] API " + tag + ": cancels the open " + (Alliance.Voting ? "vote" : "request") + " " + Alliance.StatusText(UnityEngine.Time.time));
                    Alliance.ClearPending();
                }
                string formedBy = "api" + (locked ? " locked" : "") + (reason.Length > 0 ? ": " + reason : "");
                if (!Alliance.Activate(a, b, formedBy, true, locked, reason, out string e)) return Fail(tag, e);
                MelonLogger.Msg("[ALLY] API " + tag + " -> " + Alliance.Describe());
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Error("[ALLY] API " + tag + " threw: " + ex);
                return false;
            }
        }

        /// <summary>
        /// Removes an alliance formed by ForceAlliance or "!allyadmin force" (locked or not), FriendlyIndex restored, CSV line
        /// with reason "api_clear". False when there is none (or the active one was formed by a player vote / request).
        /// </summary>
        public static bool ClearForced(string reason)
        {
            reason = (reason ?? "").Trim();
            try
            {
                if (!Alliance.Active) { MelonLogger.Msg("[ALLY] API ClearForced('" + reason + "'): no alliance"); return false; }
                if (!Alliance.Forced) { MelonLogger.Msg("[ALLY] API ClearForced('" + reason + "'): the active alliance was not forced (" + Alliance.Describe() + "), kept"); return false; }
                MelonLogger.Msg("[ALLY] API ClearForced('" + reason + "'): " + Alliance.Describe());
                Alliance.Dissolve("api_clear", reason.Length > 0 ? reason : "cleared by a mod", announce: Alliance.GameRunning);
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Error("[ALLY] API ClearForced threw: " + ex);
                return false;
            }
        }

        /// <summary>Any alliance active (forced or player-formed, also while breaking).</summary>
        public static bool IsActive => Alliance.Active;

        /// <summary>The active alliance is locked (2v1).</summary>
        public static bool IsLocked => Alliance.Active && Alliance.Locked;

        /// <summary>"Sol+Centauri vs Alien (locked, 2v1 IndustrialQuarter)"; without an alliance the status text ("No alliance", open vote ...).</summary>
        public static string Describe()
        {
            try { return Alliance.Describe(); }
            catch (Exception ex) { return "error: " + ex.GetBaseException().Message; }
        }

        /// <summary>
        /// "Sol+Centauri" from the moment a locked humans_win end fires until the next round restart, "" otherwise. Vanilla
        /// can only report one winning team (GameEvents.OnGameEnded / GetWinningTeam get the stronger ally); a mod that rates
        /// rounds (Si_EloMatchmaking) can read this during its OnGameEnded handling to treat both allies as winners.
        /// </summary>
        public static string LastRoundWinners => Alliance.LastRoundWinners;

        static bool Fail(string tag, string why)
        {
            MelonLogger.Msg("[ALLY] API " + tag + " refused: " + why);
            return false;
        }
    }
}
