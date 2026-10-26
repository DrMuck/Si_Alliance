using MelonLoader;
using Silica.AI;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Si_Alliance
{
    public enum AllyState { None, Pending, Active, Breaking }

    /// <summary>
    /// Alliance state machine and hostility: None -> Pending -> Active -> Breaking -> None.
    /// Hostility is vanilla's own: both allies get the same Team.FriendlyIndex while the alliance lasts.
    /// Team is a ScriptableObject (survives rounds), so the vanilla index is always restored.
    /// </summary>
    internal static class Alliance
    {
        // ---- hot-path fields (read by the damage / projectile / voice patches): Active implies A, B != null ----
        internal static bool Active;
        internal static Team? A, B, Lone;

        internal static AllyState State = AllyState.None;

        // pending request
        internal static Team? ReqFrom, ReqTo;
        internal static float ReqExpires;
        internal const float RequestTimeoutS = 60f;

        // breaking
        internal static float BreakAt;
        internal static Team? BreakBy;

        // round bookkeeping
        internal static bool FormedThisRound;
        static readonly HashSet<Team> _brokeThisRound = new HashSet<Team>();
        static readonly Dictionary<Team, float> _cooldownUntil = new Dictionary<Team, float>();
        // FriendlyIndex as vanilla set it, captured before the first change (never overwritten)
        static readonly Dictionary<Team, int> _vanillaIndex = new Dictionary<Team, int>();

        // record of the current alliance (CSV)
        static float _formGameTime;
        static string _formedBy = "";
        static int _playersA, _playersB, _playersLone;
        static string _nameA = "", _nameB = "", _nameLone = "";
        internal static int DroppedHits, DroppedServerHits;

        // ------------------------------------------------------------------ hot-path tests

        /// <summary>true when x and y are the two different allied teams.</summary>
        internal static bool IsCrossAllied(Team? x, Team? y)
        {
            if (!Active) return false;
            if (ReferenceEquals(x, A)) return ReferenceEquals(y, B);
            if (ReferenceEquals(x, B)) return ReferenceEquals(y, A);
            return false;
        }

        internal static bool IsMember(Team? t) => Active && (ReferenceEquals(t, A) || ReferenceEquals(t, B));

        // ------------------------------------------------------------------ game helpers

        internal static GameModeExt? Mode => GameMode.CurrentGameMode as GameModeExt;

        internal static float GameTime
        {
            get { var gm = GameMode.CurrentGameMode; return gm != null ? gm.GameTime : 0f; }
        }

        internal static bool GameRunning
        {
            get { var gm = GameMode.CurrentGameMode; return gm != null && gm.GameOngoing; }
        }

        /// <summary>Teams of the active strategy team setups (Sol, Centauri, Alien in HvHvA).</summary>
        internal static List<Team> ActiveTeams()
        {
            var list = new List<Team>(3);
            var mode = Mode;
            if (mode == null || mode.BaseTeamSetups == null) return list;
            foreach (BaseTeamSetup s in mode.BaseTeamSetups)
            {
                if (s == null || s.Team == null) continue;
                if (!mode.GetTeamSetupActive(s)) continue;
                if (!list.Contains(s.Team)) list.Add(s.Team);
            }
            return list;
        }

        internal static bool IsHvHvA(out string why)
        {
            why = "";
            var mode = Mode;
            if (mode == null) { why = "no game mode running"; return false; }
            if (!(mode is MP_Strategy)) { why = "alliances only exist in the Strategy mode"; return false; }
            if (mode.TeamsVersus == GameModeExt.ETeamsVersus.HUMANS_VS_HUMANS_VS_ALIENS) return true;
            if (ActiveTeams().Count == 3) return true;
            why = "alliances only exist in Humans vs Humans vs Aliens (3 teams)";
            return false;
        }

        internal static bool HasLost(Team? team)
        {
            if (team == null) return true;
            var mode = Mode;
            if (mode == null || mode.BaseTeamSetups == null) return false;
            foreach (BaseTeamSetup s in mode.BaseTeamSetups)
            {
                if (s == null || !ReferenceEquals(s.Team, team) || !mode.GetTeamSetupActive(s)) continue;
                return s.GetHasLost();
            }
            return false;
        }

        internal static Player? CommanderOf(Team? team)
        {
            if (team == null) return null;
            var mode = Mode;
            if (mode == null) return null;
            Player p = mode.GetCommanderForTeam(team);
            return p != null ? p : null;
        }

        internal static bool IsCommander(Player caller, Team? team)
        {
            Player? c = CommanderOf(team);
            return c != null && ReferenceEquals(c, caller);
        }

        internal static string NameOf(Team? t)
        {
            if (t == null) return "none";
            try { if (!string.IsNullOrEmpty(t.TeamShortName)) return t.TeamShortName; } catch { }
            try { if (!string.IsNullOrEmpty(t.TeamName)) return t.TeamName; } catch { }
            try { return t.name; } catch { return "team"; }
        }

        static int Players(Team? t)
        {
            if (t == null) return 0;
            try { return t.GetNumPlayers(); } catch { return 0; }
        }

        internal static string F(float v) => v.ToString("0", CultureInfo.InvariantCulture);

        static string Clock(float seconds)
        {
            if (seconds < 0f) seconds = 0f;
            int s = (int)seconds;
            return (s / 60).ToString(CultureInfo.InvariantCulture) + ":" + (s % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        /// <summary>Active team by name prefix (case-insensitive): "sol", "cent", "centauri", "alien", "ali" ...</summary>
        internal static Team? FindTeam(string word, out string why)
        {
            why = "";
            string w = (word ?? "").Trim().ToLowerInvariant();
            if (w.Length == 0) { why = "no team given"; return null; }
            Team? best = null; int bestScore = 0, ties = 0;
            foreach (Team t in ActiveTeams())
            {
                string sn = "", ln = "";
                try { sn = (t.TeamShortName ?? "").ToLowerInvariant(); } catch { }
                try { ln = (t.TeamName ?? "").ToLowerInvariant(); } catch { }
                int score = 0;
                if (sn == w || ln == w) score = 4;
                else if (sn.StartsWith(w) || ln.StartsWith(w)) score = 3;
                else if (w.Length >= 3 && (sn.Contains(w) || ln.Contains(w))) score = 1;
                if (score > bestScore) { best = t; bestScore = score; ties = 0; }
                else if (score > 0 && score == bestScore) ties++;
            }
            if (best == null) { why = "no team in this match matches '" + word + "'"; return null; }
            if (ties > 0) { why = "'" + word + "' matches several teams, be more specific"; return null; }
            return best;
        }

        // ------------------------------------------------------------------ round state

        internal static void ResetRound()
        {
            ClearPending();
            FormedThisRound = false;
            _brokeThisRound.Clear();
            _cooldownUntil.Clear();
            Balance.ResetRound();
        }

        internal static void ClearPending()
        {
            ReqFrom = null; ReqTo = null; ReqExpires = 0f;
            if (State == AllyState.Pending) State = AllyState.None;
        }

        internal static float CooldownLeft(Team t, float now)
        {
            return _cooldownUntil.TryGetValue(t, out float until) ? Math.Max(0f, until - now) : 0f;
        }

        internal static bool HasBroken(Team t) => _brokeThisRound.Contains(t);

        // ------------------------------------------------------------------ transitions

        internal static void CreatePending(Team from, Team to, Player by)
        {
            ReqFrom = from; ReqTo = to; ReqExpires = Time.time + RequestTimeoutS;
            State = AllyState.Pending;
            MelonLogger.Msg("[ALLY] t=" + Clock(GameTime) + " PENDING " + NameOf(from) + " -> " + NameOf(to) + " by " + by.PlayerName + PlayersLine(from, to, null));
            AllianceMod.ToTeam(from, "Your commander proposed an alliance to " + NameOf(to) + " (" + F(RequestTimeoutS) + " s to answer).");
            AllianceMod.ToTeam(to, NameOf(from) + " proposes an alliance against the third team. Commander: !ally accept or !ally decline (" + F(RequestTimeoutS) + " s).");
        }

        internal static void Decline(Player? by, bool expired)
        {
            Team? from = ReqFrom, to = ReqTo;
            ClearPending();
            if (from == null || to == null) return;
            if (Cfg.DeclineCooldownS > 0f) _cooldownUntil[from] = Time.time + Cfg.DeclineCooldownS;
            string what = expired ? "expired without an answer" : "was declined";
            MelonLogger.Msg("[ALLY] t=" + Clock(GameTime) + " " + (expired ? "EXPIRED " : "DECLINED ") + NameOf(from) + " -> " + NameOf(to) + (by != null ? " by " + by.PlayerName : ""));
            AllianceMod.ToTeam(from, "The alliance request to " + NameOf(to) + " " + what + ".");
            AllianceMod.ToTeam(to, "The alliance request of " + NameOf(from) + " " + what + ".");
        }

        static AllianceConfig Cfg => AllianceMod.Cfg;

        /// <summary>Forms the alliance. Callers have checked HvHvA, distinct active teams, no alliance yet.</summary>
        internal static bool Activate(Team a, Team b, string formedBy, out string error)
        {
            error = "";
            if (!Game.GetIsServer()) { error = "server only"; return false; }
            if (Active) { error = "an alliance already exists"; return false; }
            if (a == null || b == null || ReferenceEquals(a, b)) { error = "two different teams needed"; return false; }

            Team? lone = null;
            foreach (Team t in ActiveTeams())
                if (!ReferenceEquals(t, a) && !ReferenceEquals(t, b)) { lone = t; break; }
            if (lone == null) { error = "no third team in this match"; return false; }

            if (!_vanillaIndex.ContainsKey(a)) _vanillaIndex[a] = a.FriendlyIndex;
            if (!_vanillaIndex.ContainsKey(b)) _vanillaIndex[b] = b.FriendlyIndex;
            if (!_vanillaIndex.ContainsKey(lone)) _vanillaIndex[lone] = lone.FriendlyIndex;

            // shared index: A's own, unless it is -1 (never friendly) or the lone team already uses it
            int idx = a.FriendlyIndex;
            if (idx < 0 || idx == lone.FriendlyIndex)
            {
                idx = 90 + Math.Max(0, a.Index);
                while (idx == lone.FriendlyIndex) idx++;
            }
            a.FriendlyIndex = idx;
            b.FriendlyIndex = idx;

            A = a; B = b; Lone = lone;
            Active = true;
            State = AllyState.Active;
            FormedThisRound = true;
            ReqFrom = null; ReqTo = null; ReqExpires = 0f;
            BreakAt = 0f; BreakBy = null;
            DroppedHits = 0; DroppedServerHits = 0;

            _formGameTime = GameTime;
            _formedBy = formedBy;
            _nameA = NameOf(a); _nameB = NameOf(b); _nameLone = NameOf(lone);
            _playersA = Players(a); _playersB = Players(b); _playersLone = Players(lone);

            int cleared = ClearForcedTargets(a, b) + ClearForcedTargets(b, a);
            Balance.ResetRound();   // harvest totals are per alliance
            bool hook = Balance.OnActivate(a, b);
            Balance.UpdateMaxDiff();

            MelonLogger.Msg("[ALLY] t=" + Clock(_formGameTime) + " ACTIVE " + _nameA + " + " + _nameB + " vs " + _nameLone + " (" + formedBy + ")" +
                            PlayersLine(a, b, lone) + ", FriendlyIndex " + _vanillaIndex[a] + "/" + _vanillaIndex[b] + " -> " + idx +
                            ", forced targets cleared " + cleared + ", Elo bloc hook " + (hook ? "set" : "not set"));
            string extra = (hook || Balance.OriginalMaxDiff > 0) && _playersLone < _playersA + _playersB
                ? " " + _nameLone + " may now take up to " + (_playersA + _playersB - _playersLone) + " more players."
                : "";
            AllianceMod.ToAll(_nameA + " and " + _nameB + " are now allied against " + _nameLone + "! Allied fire does no damage." + extra);
            if (Cfg.LoneIncomeMult > 1f)
                AllianceMod.ToTeam(lone, "Your harvest income is multiplied by " + Cfg.LoneIncomeMult.ToString("0.##", CultureInfo.InvariantCulture) + " while the alliance lasts.");
            return true;
        }

        internal static void StartBreak(Team by, Player caller)
        {
            _brokeThisRound.Add(by);
            if (Cfg.BreakGraceS <= 0f)
            {
                Dissolve("break", "broken by " + NameOf(by), announce: true);
                return;
            }
            State = AllyState.Breaking;
            BreakBy = by;
            BreakAt = Time.time + Cfg.BreakGraceS;
            MelonLogger.Msg("[ALLY] t=" + Clock(GameTime) + " BREAKING by " + NameOf(by) + " (" + caller.PlayerName + "), ends in " + F(Cfg.BreakGraceS) + " s");
            AllianceMod.ToAll(NameOf(by) + " breaks the alliance with " + NameOf(ReferenceEquals(by, A) ? B : A) + ". It ends in " + F(Cfg.BreakGraceS) + " seconds.");
        }

        /// <summary>Ends the alliance (if any): FriendlyIndex restored, Elo hook cleared, CSV line written.</summary>
        internal static void Dissolve(string reason, string outcome, bool announce)
        {
            if (!Active)
            {
                if (State != AllyState.Pending) State = AllyState.None;
                return;
            }
            Team? a = A, b = B, lone = Lone;
            Active = false;
            State = AllyState.None;
            BreakAt = 0f; BreakBy = null;

            Restore(a); Restore(b); Restore(lone);
            Balance.OnDissolve();

            float tEnd = GameTime;
            MelonLogger.Msg("[ALLY] t=" + Clock(tEnd) + " DISSOLVED " + _nameA + " + " + _nameB + " (" + reason + ", " + outcome + "), lasted " +
                            Clock(tEnd - _formGameTime) + PlayersLine(a, b, lone) + ", dropped player hits " + DroppedHits + ", server hits " + DroppedServerHits +
                            ", " + Balance.TotalsLine(a, b, lone));
            WriteCsv(tEnd, reason, outcome, a, b, lone);
            A = null; B = null; Lone = null;

            if (!announce) return;
            switch (reason)
            {
                case "lone_eliminated":
                    AllianceMod.ToAll(_nameLone + " is eliminated. The alliance is over: " + _nameA + " vs " + _nameB + ", final showdown!");
                    break;
                case "ally_eliminated":
                    AllianceMod.ToAll("The alliance of " + _nameA + " and " + _nameB + " is over (" + outcome + ").");
                    break;
                case "break":
                    AllianceMod.ToAll("The alliance of " + _nameA + " and " + _nameB + " is over. They are enemies again!");
                    break;
                default:
                    AllianceMod.ToAll("The alliance of " + _nameA + " and " + _nameB + " has ended (" + outcome + ").");
                    break;
            }
        }

        static void Restore(Team? t)
        {
            if (t == null) return;
            if (_vanillaIndex.TryGetValue(t, out int idx))
            {
                try { t.FriendlyIndex = idx; } catch { }
            }
        }

        /// <summary>Runs once per second (and right after a critical object died) while State != None.</summary>
        internal static void Tick(float now)
        {
            if (State == AllyState.Pending)
            {
                if (ReqFrom == null || ReqTo == null || !GameRunning) { ClearPending(); return; }
                if (HasLost(ReqFrom) || HasLost(ReqTo)) { ClearPending(); return; }
                if (now >= ReqExpires) Decline(null, expired: true);
                return;
            }
            if (!Active) return;
            if (!GameRunning) return;   // OnGameEnded dissolves
            if (HasLost(Lone)) { Dissolve("lone_eliminated", "showdown " + NameOf(A) + " vs " + NameOf(B), announce: true); return; }
            if (HasLost(A)) { Dissolve("ally_eliminated", NameOf(A) + " eliminated", announce: true); return; }
            if (HasLost(B)) { Dissolve("ally_eliminated", NameOf(B) + " eliminated", announce: true); return; }
            if (State == AllyState.Breaking && now >= BreakAt) { Dissolve("break", "broken by " + NameOf(BreakBy), announce: true); return; }
            Balance.UpdateMaxDiff();
        }

        /// <summary>Drops attack orders of owner's units that point at the new ally (forced targets bypass enemy checks).</summary>
        static int ClearForcedTargets(Team owner, Team ally)
        {
            int n = 0;
            List<Unit> units;
            try { units = owner.Units; } catch { return 0; }
            if (units == null) return 0;
            for (int i = 0; i < units.Count; i++)
            {
                try
                {
                    Unit u = units[i];
                    if (u == null) continue;
                    AIBaseAgent agent = u.AIAgent;
                    if (agent == null) continue;
                    AIAiming aim = agent.AgentAiming;
                    if (aim == null) continue;
                    Target t = aim.AssignedAttackTarget;
                    if (t == null || t.Owner == null) continue;
                    if (!ReferenceEquals(t.Owner.Team, ally)) continue;
                    aim.SetAssignedAttackTarget(null);
                    n++;
                }
                catch { }
            }
            return n;
        }

        // ------------------------------------------------------------------ status / logging

        internal static string StatusText(float now)
        {
            switch (State)
            {
                case AllyState.Pending:
                    return "Pending: " + NameOf(ReqFrom) + " -> " + NameOf(ReqTo) + " (" + F(Math.Max(0f, ReqExpires - now)) + " s)";
                case AllyState.Active:
                    return NameOf(A) + " + " + NameOf(B) + " allied vs " + NameOf(Lone);
                case AllyState.Breaking:
                    return NameOf(A) + " + " + NameOf(B) + " allied vs " + NameOf(Lone) + ", breaking in " + F(Math.Max(0f, BreakAt - now)) + " s";
                default:
                    return "No alliance";
            }
        }

        static string PlayersLine(Team? a, Team? b, Team? c)
        {
            var sb = new StringBuilder(", players ");
            sb.Append(NameOf(a)).Append(' ').Append(Players(a));
            sb.Append(' ').Append(NameOf(b)).Append(' ').Append(Players(b));
            if (c != null) sb.Append(' ').Append(NameOf(c)).Append(' ').Append(Players(c));
            return sb.ToString();
        }

        static void WriteCsv(float tEnd, string reason, string outcome, Team? a, Team? b, Team? lone)
        {
            try
            {
                string dir = Path.Combine(AllianceConfig.Dir, "log");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "alliances.csv");
                bool header = !File.Exists(file);
                string map = "";
                try { map = NetworkGameServer.GetServerMap() ?? ""; } catch { }
                var sb = new StringBuilder();
                if (header) sb.AppendLine("date,map,t_form_s,teams,lone,players_a,players_b,players_lone,formed_by,t_end_s,reason,outcome,harvest_a,harvest_b,harvest_lone,lone_bonus,dropped_player_hits,dropped_server_hits");
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',')
                  .Append(Csv(map)).Append(',')
                  .Append(F(_formGameTime)).Append(',')
                  .Append(Csv(_nameA + "+" + _nameB)).Append(',')
                  .Append(Csv(_nameLone)).Append(',')
                  .Append(_playersA).Append(',').Append(_playersB).Append(',').Append(_playersLone).Append(',')
                  .Append(Csv(_formedBy)).Append(',')
                  .Append(F(tEnd)).Append(',')
                  .Append(Csv(reason)).Append(',')
                  .Append(Csv(outcome)).Append(',')
                  .Append(Balance.HarvestOf(a)).Append(',').Append(Balance.HarvestOf(b)).Append(',').Append(Balance.HarvestOf(lone)).Append(',')
                  .Append(Balance.BonusPaid).Append(',')
                  .Append(DroppedHits).Append(',').Append(DroppedServerHits)
                  .AppendLine();
                File.AppendAllText(file, sb.ToString());
            }
            catch (Exception ex) { MelonLogger.Warning("[ALLY] alliances.csv write failed: " + ex.Message); }
        }

        static string Csv(string s)
        {
            s = s ?? "";
            if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
    }
}
