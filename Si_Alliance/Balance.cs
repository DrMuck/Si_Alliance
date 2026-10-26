using MelonLoader;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace Si_Alliance
{
    /// <summary>
    /// Balancing the lone team (PLAN section 5):
    ///  - bloc player cap: Si_EloMatchmaking.AllianceHook.GroupOf (static Func&lt;Team,int&gt;) set by reflection, so the
    ///    allied pair counts as one bloc in Elo's join rule; null again when the alliance ends;
    ///  - income: lone_income_mult on every harvest deposit of the lone team (GameEvents.OnResourcesHarvested).
    /// </summary>
    internal static class Balance
    {
        static FieldInfo? _groupOf;
        static bool _searched, _missingLogged;

        // per-round harvest totals while an alliance is active (for the log / CSV)
        static readonly Dictionary<Team, long> _harvest = new Dictionary<Team, long>();
        internal static long BonusPaid;

        internal static void ResetRound()
        {
            _harvest.Clear();
            BonusPaid = 0;
        }

        // ------------------------------------------------------------------ Elo bloc hook

        static FieldInfo? GroupOfField()
        {
            if (_groupOf != null) return _groupOf;
            if (_searched) return null;
            _searched = true;
            try
            {
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string name;
                    try { name = asm.GetName().Name ?? ""; } catch { continue; }
                    if (name.IndexOf("EloMatchmaking", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    Type? t = null;
                    try { t = asm.GetType("Si_EloMatchmaking.AllianceHook", false); } catch { }
                    if (t == null) continue;
                    FieldInfo? f = t.GetField("GroupOf", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (f == null) continue;
                    if (f.FieldType != typeof(Func<Team, int>))
                    {
                        MelonLogger.Warning("[ALLY] Si_EloMatchmaking.AllianceHook.GroupOf has type " + f.FieldType + ", expected Func<Team,int>: bloc balance off");
                        return null;
                    }
                    _groupOf = f;
                    MelonLogger.Msg("[ALLY] Si_EloMatchmaking AllianceHook found in " + name);
                    return f;
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[ALLY] AllianceHook search failed: " + ex.Message); }
            return null;
        }

        /// <summary>Sets the Elo bloc hook; returns true when it was set.</summary>
        internal static bool OnActivate(Team a, Team b)
        {
            if (!AllianceMod.Cfg.BlocBalance) return false;
            FieldInfo? f = GroupOfField();
            if (f == null)
            {
                if (!_missingLogged)
                {
                    _missingLogged = true;
                    MelonLogger.Msg("[ALLY] Si_EloMatchmaking.AllianceHook not present (Elo < 0.7.0 or not installed): the join rule keeps counting teams separately");
                }
                return false;
            }
            try
            {
                int group = a.Index;
                Func<Team, int> groupOf = t => t == null ? -1 : (ReferenceEquals(t, a) || ReferenceEquals(t, b)) ? group : t.Index;
                f.SetValue(null, groupOf);
                return true;
            }
            catch (Exception ex) { MelonLogger.Warning("[ALLY] setting AllianceHook.GroupOf failed: " + ex.Message); return false; }
        }

        internal static void OnDissolve()
        {
            RestoreMaxDiff();
            if (_groupOf == null) return;
            try { _groupOf.SetValue(null, null); }
            catch (Exception ex) { MelonLogger.Warning("[ALLY] clearing AllianceHook.GroupOf failed: " + ex.Message); }
        }

        // ------------------------------------------------------------------ vanilla join gate (1.0.x)
        // MP_Strategy.GetCanJoinTeam allows a join while teamCount - minCount < MaximumPlayerDifference; clients grey the
        // join button with the synced value. While an alliance is active the server value is raised so the lone team
        // may fill up to the allied sum: max(orig, alliedSum - lone + orig). The property also exists in 0.9.47 (without
        // GetCanJoinTeam); both are reached by reflection and skipped silently when missing. 0 = gate off: untouched.

        static PropertyInfo? _maxDiffProp;
        static bool _maxDiffSearched;
        static UnityEngine.Object? _maxDiffMode;
        static int _origMaxDiff = -1;

        static PropertyInfo? MaxDiffProp()
        {
            if (_maxDiffSearched) return _maxDiffProp;
            _maxDiffSearched = true;
            try
            {
                var p = typeof(MP_Strategy).GetProperty("MaximumPlayerDifference", BindingFlags.Public | BindingFlags.Instance);
                if (p != null && p.PropertyType == typeof(int) && p.CanRead && p.CanWrite) _maxDiffProp = p;
            }
            catch { }
            return _maxDiffProp;
        }

        /// <summary>The server's MaximumPlayerDifference before the alliance raised it (or the current value; -1 = unknown).</summary>
        internal static int OriginalMaxDiff
        {
            get
            {
                if (_origMaxDiff >= 0) return _origMaxDiff;
                var p = MaxDiffProp(); var mode = GameMode.CurrentGameMode as MP_Strategy;
                if (p == null || mode == null) return -1;
                try { return (int)p.GetValue(mode); } catch { return -1; }
            }
        }

        /// <summary>Called on activation and once per second while the alliance is active.</summary>
        internal static void UpdateMaxDiff()
        {
            var p = MaxDiffProp();
            var mode = GameMode.CurrentGameMode as MP_Strategy;
            if (p == null || mode == null || !Alliance.Active) return;
            try
            {
                if (_origMaxDiff < 0)
                {
                    _origMaxDiff = (int)p.GetValue(mode);
                    _maxDiffMode = mode;
                    AllianceMod.Debug("MaximumPlayerDifference at formation: " + _origMaxDiff);
                }
                if (_origMaxDiff <= 0) return;   // vanilla gate off
                int allied = Alliance.A!.GetNumPlayers() + Alliance.B!.GetNumPlayers();
                int lone = Alliance.Lone != null ? Alliance.Lone.GetNumPlayers() : 0;
                int want = Math.Max(_origMaxDiff, allied - lone + _origMaxDiff);
                int cur = (int)p.GetValue(mode);
                if (cur != want)
                {
                    p.SetValue(mode, want);
                    MelonLogger.Msg("[ALLY] MaximumPlayerDifference " + cur + " -> " + want + " (allied " + allied + ", lone " + lone + ", original " + _origMaxDiff + ")");
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[ALLY] MaximumPlayerDifference update failed: " + ex.Message); }
        }

        static void RestoreMaxDiff()
        {
            int orig = _origMaxDiff;
            var mode = _maxDiffMode;
            _origMaxDiff = -1; _maxDiffMode = null;
            var p = MaxDiffProp();
            if (orig <= 0 || p == null || mode == null) return;   // Unity null: that game mode is gone
            try
            {
                if ((int)p.GetValue(mode) != orig)
                {
                    p.SetValue(mode, orig);
                    MelonLogger.Msg("[ALLY] MaximumPlayerDifference restored to " + orig);
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[ALLY] MaximumPlayerDifference restore failed: " + ex.Message); }
        }

        // Postfix MP_Strategy.GetCanJoinTeam(Team team, Player player) (1.0.x; servers without Si_EloMatchmaking): a join
        // to the lone team is allowed while it stays within allied sum + the original difference.
        internal static void Postfix_GetCanJoinTeam(Team __0, Player __1, ref bool __result)
        {
            if (__result || !Alliance.Active || __0 == null || !ReferenceEquals(__0, Alliance.Lone)) return;
            int orig = OriginalMaxDiff;
            if (orig <= 0) return;
            int allied = Alliance.A!.GetNumPlayers() + Alliance.B!.GetNumPlayers();
            if (__1 != null && Alliance.IsMember(__1.Team)) allied--;   // the joiner leaves an allied team
            if (__0.GetNumPlayers() + 1 <= allied + orig) __result = true;
        }

        // ------------------------------------------------------------------ income

        // GameEvents.OnResourcesHarvested (Team.OnExtractedResourcesStored, server, after every harvester deposit).
        // StoreResource does not raise the event again, so there is no recursion.
        internal static void OnResourcesHarvested(Team team, Resource resource, int delta, int total)
        {
            if (!Alliance.Active || team == null || delta <= 0) return;
            try
            {
                _harvest.TryGetValue(team, out long sum);
                _harvest[team] = sum + delta;
                if (!ReferenceEquals(team, Alliance.Lone)) return;
                float m = AllianceMod.Cfg.LoneIncomeMult;
                if (m <= 1f) return;
                int bonus = (int)Math.Round(delta * (m - 1f));
                if (bonus <= 0) return;
                int left = team.StoreResource(bonus);
                if (left > 0) team.StartingResources = team.StartingResources + left;
                BonusPaid += bonus;
                AllianceMod.Debug("lone bonus " + bonus + " on " + delta + " (" + left + " to StartingResources), total " + BonusPaid);
            }
            catch (Exception ex) { MelonLogger.Warning("[ALLY] income bonus failed: " + ex.Message); }
        }

        internal static long HarvestOf(Team? t) => t != null && _harvest.TryGetValue(t, out long v) ? v : 0;

        internal static string TotalsLine(Team? a, Team? b, Team? lone)
        {
            return "harvest " + Alliance.NameOf(a) + " " + HarvestOf(a) + " " + Alliance.NameOf(b) + " " + HarvestOf(b) + " " + Alliance.NameOf(lone) + " " + HarvestOf(lone) +
                   ", lone bonus " + BonusPaid.ToString(CultureInfo.InvariantCulture) + " (mult " + AllianceMod.Cfg.LoneIncomeMult.ToString("0.##", CultureInfo.InvariantCulture) + ")";
        }
    }
}
