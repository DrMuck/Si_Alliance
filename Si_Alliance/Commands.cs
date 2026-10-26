using MelonLoader;
using SilicaAdminMod;
using System;
using System.Globalization;
using UnityEngine;

namespace Si_Alliance
{
    /// <summary>
    /// !ally (players, commander-gated inside) and !allyadmin (Power.Teams: the game's own admin list grants it, Cheat it does not). One command name each, no aliases.
    /// </summary>
    internal static class Commands
    {
        static AllianceConfig Cfg => AllianceMod.Cfg;

        static void Reply(Player? p, string msg) => AllianceMod.Reply(p, msg);

        // ------------------------------------------------------------------ !ally

        public static void Command_Ally(Player? caller, string args)
        {
            try { HandleAlly(caller, args ?? ""); }
            catch (Exception ex)
            {
                MelonLogger.Error("[ALLY] !ally: " + ex);
                Reply(caller, "Error: " + ex.GetBaseException().Message);
            }
        }

        static void HandleAlly(Player? caller, string args)
        {
            string[] tokens = args.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string sub = tokens.Length > 1 ? tokens[1].ToLowerInvariant() : "";
            string rest = tokens.Length > 2 ? string.Join(" ", tokens, 2, tokens.Length - 2) : "";

            switch (sub)
            {
                case "": case "help": case "?":
                    Reply(caller, Cfg.FpsVote == "off"
                        ? "!ally <team> (commander: propose)  -  !ally accept | decline  -  !ally break  -  !ally status"
                        : "!ally <team> (commander: propose, then " + (Cfg.FpsVote == "both" ? "both teams vote" : "the asked team votes") + ")  -  !ally yes | no (vote)  -  !ally break  -  !ally status");
                    Reply(caller, "Status: " + Alliance.StatusText(Time.time));
                    return;
                case "status": Status(caller); return;
            }

            if (caller == null) { Reply(caller, "console: only 'status' works without a player (admins: !allyadmin)"); return; }
            if (!Cfg.Enabled) { Reply(caller, "alliances are switched off on this server"); return; }

            switch (sub)
            {
                case "accept": Accept(caller); return;
                case "decline": case "deny": case "reject": DeclineCmd(caller); return;
                case "respond": case "answer":
                {
                    string v = rest.Trim().ToLowerInvariant();
                    if (v == "accept" || v == "yes") { Accept(caller); return; }
                    if (v == "decline" || v == "deny" || v == "reject" || v == "no") { DeclineCmd(caller); return; }
                    Reply(caller, Alliance.Voting ? "usage: !ally yes | !ally no" : "usage: !ally accept | !ally decline");
                    return;
                }
                case "break": Break(caller); return;
                case "yes": case "no":
                    if (Alliance.State == AllyState.Pending && !Alliance.Voting)
                    { Reply(caller, "this request is answered by the commander of " + Alliance.NameOf(Alliance.ReqTo) + " (!ally accept | decline), there is no vote"); return; }
                    Vote(caller, sub == "yes");
                    return;
            }
            Request(caller, string.Join(" ", tokens, 1, tokens.Length - 1));
        }

        static void Status(Player? caller)
        {
            string s = Alliance.StatusText(Time.time);
            if (!Cfg.Enabled) s += " | alliances OFF";
            if (Alliance.FormedThisRound && Cfg.OneAlliancePerRound && !Alliance.Active) s += " | no further alliance this round";
            if (Cfg.LoneIncomeMult > 1f) s += " | lone income x" + Cfg.LoneIncomeMult.ToString("0.##", CultureInfo.InvariantCulture);
            Reply(caller, s);
        }

        static void Request(Player caller, string targetWord)
        {
            if (!Alliance.GameRunning) { Reply(caller, "no round in progress"); return; }
            if (!Alliance.IsHvHvA(out string why)) { Reply(caller, why); return; }
            Team? own = caller.Team;
            if (own == null || !Alliance.ActiveTeams().Contains(own)) { Reply(caller, "join a team first"); return; }
            if (!Alliance.IsCommander(caller, own)) { Reply(caller, "only your team's commander can propose an alliance"); return; }
            if (Alliance.Active) { Reply(caller, "there is already an alliance: " + Alliance.StatusText(Time.time)); return; }
            if (Alliance.State == AllyState.Pending) { Reply(caller, "a request is already open: " + Alliance.StatusText(Time.time)); return; }
            if (Cfg.OneAlliancePerRound && Alliance.FormedThisRound) { Reply(caller, "only one alliance per round"); return; }
            float minS = Cfg.MinMinutes * 60f;
            if (Alliance.GameTime < minS)
            {
                Reply(caller, "alliances open after " + Cfg.MinMinutes.ToString("0.#", CultureInfo.InvariantCulture) + " minutes (in " + Alliance.F(minS - Alliance.GameTime) + " s)");
                return;
            }
            float cd = Alliance.CooldownLeft(own, Time.time);
            if (cd > 0f) { Reply(caller, "your last request was declined, wait " + Alliance.F(cd) + " s"); return; }

            Team? target = Alliance.FindTeam(targetWord, out string err);
            if (target == null) { Reply(caller, err + " (usage: !ally <team>)"); return; }
            if (ReferenceEquals(target, own)) { Reply(caller, "that is your own team"); return; }
            if (Alliance.HasLost(own) || Alliance.HasLost(target)) { Reply(caller, "that team is already eliminated"); return; }

            Player? other = Alliance.CommanderOf(target);
            bool vote = Cfg.FpsVote != "off";
            if (other == null)
            {
                // no human commander (RTS AI or empty seat): config policy
                string policy = Cfg.AiPolicy;
                if (policy != "always")
                {
                    Reply(caller, Alliance.NameOf(target) + " has no human commander and does not accept alliances" +
                                  (policy == "vote" ? " (ai_policy \"vote\" is reserved, treated as \"never\")" : ""));
                    return;
                }
                if (!vote)
                {
                    MelonLogger.Msg("[ALLY] " + Alliance.NameOf(target) + " has no human commander, ai_policy=always: accepted");
                    if (!Alliance.Activate(own, target, "request " + caller.PlayerName + ", AI accepted", out string e)) Reply(caller, "alliance failed: " + e);
                    return;
                }
                MelonLogger.Msg("[ALLY] " + Alliance.NameOf(target) + " has no human commander, ai_policy=always: its players vote");
            }
            if (vote)
            {
                // FPS vote: the vote decides, also for the asked commander (his accept/decline is a vote)
                Alliance.CreateVote(own, target, caller, Cfg.FpsVote == "both");
                return;
            }
            Alliance.CreatePending(own, target, caller);
            Reply(caller, "request sent to " + Alliance.NameOf(target) + " (" + (other != null ? other.PlayerName : "?") + ")");
        }

        static void Vote(Player caller, bool yes)
        {
            bool ok = Alliance.RecordVote(caller, yes, out string reply);
            Reply(caller, reply);
            if (ok) Alliance.CheckVote(Time.time);
        }

        static void Accept(Player caller)
        {
            if (Alliance.Voting) { Vote(caller, true); return; }
            if (Alliance.State != AllyState.Pending || Alliance.ReqFrom == null || Alliance.ReqTo == null) { Reply(caller, "there is no open alliance request"); return; }
            if (!ReferenceEquals(caller.Team, Alliance.ReqTo) || !Alliance.IsCommander(caller, Alliance.ReqTo))
            { Reply(caller, "only the commander of " + Alliance.NameOf(Alliance.ReqTo) + " can answer"); return; }
            Team from = Alliance.ReqFrom, to = Alliance.ReqTo;
            if (!Alliance.Activate(from, to, "request accepted by " + caller.PlayerName, out string e))
            {
                Alliance.ClearPending();
                Reply(caller, "alliance failed: " + e);
            }
        }

        static void DeclineCmd(Player caller)
        {
            if (Alliance.Voting) { Vote(caller, false); return; }
            if (Alliance.State != AllyState.Pending || Alliance.ReqTo == null) { Reply(caller, "there is no open alliance request"); return; }
            if (!ReferenceEquals(caller.Team, Alliance.ReqTo) || !Alliance.IsCommander(caller, Alliance.ReqTo))
            { Reply(caller, "only the commander of " + Alliance.NameOf(Alliance.ReqTo) + " can answer"); return; }
            Alliance.Decline(caller, expired: false);
        }

        static void Break(Player caller)
        {
            if (!Alliance.Active) { Reply(caller, "there is no alliance"); return; }
            Team? own = caller.Team;
            if (!Alliance.IsMember(own) || own == null) { Reply(caller, "your team is not in the alliance"); return; }
            if (!Alliance.IsCommander(caller, own)) { Reply(caller, "only your team's commander can break the alliance"); return; }
            if (Alliance.State == AllyState.Breaking) { Reply(caller, "the alliance is already ending: " + Alliance.StatusText(Time.time)); return; }
            if (Alliance.HasBroken(own)) { Reply(caller, "your team already broke an alliance this round"); return; }
            Alliance.StartBreak(own, caller);
        }

        // ------------------------------------------------------------------ !allyadmin

        public static void Command_AllyAdmin(Player? caller, string args)
        {
            try { HandleAdmin(caller, args ?? ""); }
            catch (Exception ex)
            {
                MelonLogger.Error("[ALLY] !allyadmin: " + ex);
                Reply(caller, "Error: " + ex.GetBaseException().Message);
            }
        }

        static void HandleAdmin(Player? caller, string args)
        {
            string[] tokens = args.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string sub = tokens.Length > 1 ? tokens[1].ToLowerInvariant() : "";
            string who = caller != null ? caller.PlayerName : "CONSOLE";
            switch (sub)
            {
                case "force":
                {
                    // "!allyadmin force sol cent" or "!allyadmin force Sol+Centauri" (one token, sent by the ModUI pair picker)
                    string wordA = tokens.Length > 2 ? tokens[2] : "", wordB = tokens.Length > 3 ? tokens[3] : "";
                    int plus = wordA.IndexOf('+');
                    if (plus > 0 && wordB.Length == 0) { wordB = wordA.Substring(plus + 1); wordA = wordA.Substring(0, plus); }
                    if (wordA.Length == 0 || wordB.Length == 0) { Reply(caller, "usage: !allyadmin force <teamA> <teamB>"); return; }
                    if (!Alliance.GameRunning) { Reply(caller, "no round in progress"); return; }
                    if (!Alliance.IsHvHvA(out string why)) { Reply(caller, why); return; }
                    Team? a = Alliance.FindTeam(wordA, out string ea);
                    if (a == null) { Reply(caller, ea); return; }
                    Team? b = Alliance.FindTeam(wordB, out string eb);
                    if (b == null) { Reply(caller, eb); return; }
                    if (ReferenceEquals(a, b)) { Reply(caller, "two different teams needed"); return; }
                    if (Alliance.Active) Alliance.Dissolve("admin_clear", "replaced by admin " + who, announce: false);
                    Alliance.ClearPending();
                    if (!Alliance.Activate(a, b, "admin " + who, out string e)) { Reply(caller, "alliance failed: " + e); return; }
                    if (caller != null) HelperMethods.AlertAdminAction(caller, "forced an alliance of " + Alliance.NameOf(a) + " and " + Alliance.NameOf(b));
                    return;
                }
                case "clear":
                {
                    if (!Alliance.Active && Alliance.State != AllyState.Pending) { Reply(caller, "there is no alliance or request"); return; }
                    if (Alliance.Active) Alliance.Dissolve("admin_clear", "cleared by admin " + who, announce: true);
                    else if (Alliance.Voting)
                    {
                        string pair = Alliance.NameOf(Alliance.ReqFrom) + "+" + Alliance.NameOf(Alliance.ReqTo);
                        MelonLogger.Msg("[ALLY] vote " + pair + " cancelled by admin " + who + " (" + Alliance.VoteTallies() + ")");
                        Alliance.ClearPending();
                        AllianceMod.ToAll("The alliance vote " + pair + " was cancelled by an admin.");
                    }
                    else { Alliance.ClearPending(); Reply(caller, "open request removed"); }
                    if (caller != null) HelperMethods.AlertAdminAction(caller, "cleared the alliance");
                    return;
                }
                case "mult":
                {
                    if (tokens.Length < 3 || !float.TryParse(tokens[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float m))
                    { Reply(caller, "lone_income_mult = " + Cfg.LoneIncomeMult.ToString("0.##", CultureInfo.InvariantCulture) + " (usage: !allyadmin mult <1..5>)"); return; }
                    Cfg.LoneIncomeMult = AllianceConfig.ClampMult(m);
                    Cfg.Save();
                    Reply(caller, "lone_income_mult = " + Cfg.LoneIncomeMult.ToString("0.##", CultureInfo.InvariantCulture) + " (saved)");
                    MelonLogger.Msg("[ALLY] " + who + " set lone_income_mult " + Cfg.LoneIncomeMult.ToString("0.##", CultureInfo.InvariantCulture));
                    return;
                }
                case "reload":
                    AllianceMod.Cfg = AllianceConfig.Load();
                    Reply(caller, "config reloaded");
                    return;
                case "status":
                {
                    Status(caller);
                    var teams = Alliance.ActiveTeams();
                    var sb = new System.Text.StringBuilder("teams:");
                    foreach (Team t in teams)
                    {
                        Player? c = Alliance.CommanderOf(t);
                        sb.Append(' ').Append(Alliance.NameOf(t)).Append("[idx ").Append(t.FriendlyIndex).Append(", ")
                          .Append(t.GetNumPlayers()).Append(" pl, cmdr ").Append(c != null ? c.PlayerName : "none")
                          .Append(Alliance.HasLost(t) ? ", LOST" : "").Append(']');
                    }
                    sb.Append(" | game ").Append(Alliance.F(Alliance.GameTime)).Append(" s, dropped hits ").Append(Alliance.DroppedHits).Append('/').Append(Alliance.DroppedServerHits);
                    Reply(caller, sb.ToString());
                    return;
                }
            }
            Reply(caller, "!allyadmin force <teamA> <teamB> | clear | mult <x> | status | reload");
        }
    }
}
