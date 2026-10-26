using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MelonLoader;
using UnityEngine;

namespace Si_Alliance
{
    // Si_ModUI export (contract: Si_ModUI README "Mod data"): mirrors the chat commands only, chat stays fully usable.
    // The panel shows the state in words (what is going on, who can act now, when it opens), the three teams with
    // players and commander, and only the player entries that make sense in the current state (answer while a request
    // is open, break while allied, propose otherwise). The admin "ally" entry is a choice of the three team pairs.
    // Countdowns are rounded to 10 s and the round time to the minute so the server pushes few frames.
    public partial class AllianceMod
    {
        static string? _cache; static string _cacheKey = "";

        public static string? ModUIData()
        {
            try
            {
                float now = Time.time;
                bool running = Alliance.GameRunning;
                List<Team> teams = running ? Alliance.ActiveTeams() : new List<Team>();
                string status = StatusLine(now, running);
                string teamsLine = TeamsLine(teams);
                string rules = "Rules: commanders propose and answer (" + Cfg.MinMinutes.ToString("0.#", CultureInfo.InvariantCulture) + " min after the start" +
                               (Cfg.OneAlliancePerRound ? ", one alliance per round" : "") + "). Allies cannot damage each other; AI and turrets hold fire. " +
                               "The alliance ends when the third team is eliminated (final 1v1)" +
                               (Cfg.LoneIncomeMult > 1f ? "; the lone team harvests x" + Cfg.LoneIncomeMult.ToString("0.##", CultureInfo.InvariantCulture) : "") + ".";
                var pairs = Pairs(teams);
                string key = status + "|" + teamsLine + "|" + rules + "|" + Alliance.State + "|" + Cfg.Enabled + "|" + Cfg.AiPolicy + "|" + string.Join(",", pairs.ConvertAll(p => p.value));
                if (_cache != null && key == _cacheKey) return _cache;

                var x = ModUI.ModUIExport.New().Enabled(Cfg.Enabled).Info(status);
                if (teamsLine.Length > 0) x.Info(teamsLine);
                x.Info(rules);

                // ---- players (commanders) ----
                if (Alliance.State == AllyState.Pending)
                    x.Picker("!ally respond", "Answer " + Alliance.NameOf(Alliance.ReqFrom) + "'s request",
                             "Commander of " + Alliance.NameOf(Alliance.ReqTo) + ": accept or decline the alliance proposed by " + Alliance.NameOf(Alliance.ReqFrom) + ". A decline blocks a new request for " + Cfg.DeclineCooldownS.ToString("0", CultureInfo.InvariantCulture) + " s.",
                             false, "choice", ("accept", "Accept"), ("decline", "Decline"));
                else if (!Alliance.Active)
                    x.Picker("!ally", "Propose alliance", "Commander: pick the team you want to ally with against the third team. Its commander has 60 s to answer; a team without a human commander answers by server policy (" + Cfg.AiPolicy + ").", false, "team");
                if (Alliance.State == AllyState.Active)
                    x.Action("!ally break", "Break alliance", "Commander of an allied team: announced to everyone, the alliance ends " + Cfg.BreakGraceS.ToString("0", CultureInfo.InvariantCulture) + " s later. Once per team per round.");
                x.Report("!ally status", "Status", "Current alliance, open request and rules in chat.");

                // ---- admins ----
                if (pairs.Count > 0)
                    x.Picker("!allyadmin force", "Ally two teams (admin)", "Forms the alliance at once, no commander vote; ignores the opening time and the one-per-round rule.", true, "choice", pairs.ToArray());
                else
                    x.Picker("!allyadmin force", "Ally two teams (admin)", "Forms the alliance at once (pick both teams); needs a running 3-team round.", true, "team,team");
                string clearLabel = Alliance.State == AllyState.Pending ? "Cancel request (admin)" : Alliance.Active ? "Dissolve alliance (admin)" : "Dissolve / cancel (admin)";
                x.Action("!allyadmin clear", clearLabel, "Ends the alliance at once or removes the open request; teams are hostile again.", true);
                x.Value("!allyadmin mult", "Lone team income multiplier", "Harvest multiplier of the team facing the alliance while it is active (1 = off, max 5). Saved to the config.", true, Cfg.LoneIncomeMult);
                x.Report("!allyadmin status", "Admin status", "Teams with FriendlyIndex, players, commander, lost flag and dropped-hit counters in chat.", true);
                _cache = x.Build(); _cacheKey = key;
                return _cache;
            }
            catch (Exception exception) { MelonLogger.Warning("[ALLY] ModUIData: " + exception.Message); return null; }
        }

        static string StatusLine(float now, bool running)
        {
            if (!Cfg.Enabled) return "Alliances are switched off on this server.";
            if (!running) return "No round running.";
            switch (Alliance.State)
            {
                case AllyState.Pending:
                    return "Request open: " + Alliance.NameOf(Alliance.ReqFrom) + " asks " + Alliance.NameOf(Alliance.ReqTo) + " for an alliance (" + Alliance.NameOf(Alliance.ReqTo) + "'s commander has ~" + Tens(Alliance.ReqExpires - now) + " s to answer).";
                case AllyState.Active:
                    return "ALLIED: " + Alliance.NameOf(Alliance.A) + " + " + Alliance.NameOf(Alliance.B) + " against " + Alliance.NameOf(Alliance.Lone) +
                           (Cfg.LoneIncomeMult > 1f ? " (" + Alliance.NameOf(Alliance.Lone) + " income x" + Cfg.LoneIncomeMult.ToString("0.##", CultureInfo.InvariantCulture) + ")" : "") + ".";
                case AllyState.Breaking:
                    return "BREAKING: " + Alliance.NameOf(Alliance.A) + " + " + Alliance.NameOf(Alliance.B) + " end their alliance in ~" + Tens(Alliance.BreakAt - now) + " s.";
            }
            if (!Alliance.IsHvHvA(out string why)) return "No alliance: " + why + ".";
            if (Cfg.OneAlliancePerRound && Alliance.FormedThisRound) return "No alliance. This round's alliance has ended (one per round).";
            float open = Cfg.MinMinutes * 60f, t = Alliance.GameTime;
            if (t < open) return "No alliance yet. Commanders can propose one from minute " + Cfg.MinMinutes.ToString("0.#", CultureInfo.InvariantCulture) + " (round time " + ((int)(t / 60f)) + " min).";
            return "No alliance. Commanders can propose one now.";
        }

        static string TeamsLine(List<Team> teams)
        {
            if (teams.Count == 0) return "";
            var sb = new StringBuilder("Teams: ");
            for (int i = 0; i < teams.Count; i++)
            {
                Team t = teams[i];
                int n = 0; try { n = t.GetNumPlayers(); } catch { }
                Player? c = Alliance.CommanderOf(t);
                if (i > 0) sb.Append("  |  ");
                sb.Append(Alliance.NameOf(t)).Append(' ').Append(n).Append(n == 1 ? " player" : " players").Append(", ")
                  .Append(c != null ? "commander " + c.PlayerName : "no commander");
                if (Alliance.HasLost(t)) sb.Append(", ELIMINATED");
            }
            return sb.ToString();
        }

        // The three team pairs of a 3-team round as picker choices: value "A+B" (one token), label "Ally A + B (vs C)".
        static List<(string value, string label)> Pairs(List<Team> teams)
        {
            var list = new List<(string value, string label)>();
            if (teams.Count != 3) return list;
            for (int i = 0; i < 3; i++)
                for (int j = i + 1; j < 3; j++)
                {
                    string a = Alliance.NameOf(teams[i]), b = Alliance.NameOf(teams[j]), c = Alliance.NameOf(teams[3 - i - j]);
                    list.Add((a + "+" + b, "Ally " + a + " + " + b + "  (vs " + c + ")"));
                }
            return list;
        }

        static string Tens(float seconds) => (Math.Max(0, (int)Math.Ceiling(seconds / 10f)) * 10).ToString(CultureInfo.InvariantCulture);
    }
}
