using System;
using System.Globalization;
using MelonLoader;
using UnityEngine;

namespace Si_Alliance
{
    // Si_ModUI export (contract: Si_ModUI README "Mod data"): mirrors the chat commands only, chat stays fully usable.
    // The pending request is shown as a shared Info line (per-player data refreshes only once a minute).
    public partial class AllianceMod
    {
        static string? _cache; static string _cacheKey = "";

        public static string? ModUIData()
        {
            try
            {
                string status = Alliance.StatusText(Time.time);
                string key = status + "|" + Cfg.Enabled + "|" + Cfg.LoneIncomeMult.ToString(CultureInfo.InvariantCulture) + "|" + Cfg.MinMinutes.ToString(CultureInfo.InvariantCulture) + "|" + Cfg.AiPolicy;
                if (_cache != null && key == _cacheKey) return _cache;

                string rules = "Commanders only. Opens after " + Cfg.MinMinutes.ToString("0.#", CultureInfo.InvariantCulture) + " min" +
                               (Cfg.OneAlliancePerRound ? ", one per round" : "") + ", ends when the third team is eliminated (final 1v1)" +
                               (Cfg.LoneIncomeMult > 1f ? ", lone team income x" + Cfg.LoneIncomeMult.ToString("0.##", CultureInfo.InvariantCulture) : "") + ".";
                var x = ModUI.ModUIExport.New()
                    .Enabled(Cfg.Enabled)
                    .Info("Alliance: " + status)
                    .Info(rules)
                    .Picker("!ally", "Propose alliance", "Commander: propose an alliance against the third team to the chosen team. Its commander has 60 s to answer; a team without a human commander answers by server policy.", false, "team")
                    .Picker("!ally respond", "Answer request", "Commander of the asked team: accept or decline the open alliance request.", false, "choice",
                            ("accept", "Accept"), ("decline", "Decline"))
                    .Action("!ally break", "Break alliance", "Commander of an allied team: the alliance ends after " + Cfg.BreakGraceS.ToString("0", CultureInfo.InvariantCulture) + " s (announced to everyone). Once per team per round.")
                    .Report("!ally status", "Status", "Current alliance, open request and rules.")
                    .Picker("!allyadmin force", "Force alliance", "Admin: ally two teams now (pick both teams); ignores the time limit and the one-per-round rule.", true, "team,team")
                    .Action("!allyadmin clear", "Clear alliance", "Admin: end the alliance or remove the open request.", true)
                    .Value("!allyadmin mult", "Lone income multiplier", "Admin: harvest multiplier of the lone team while an alliance is active (1 = off, max 5). Saved to the config.", true, Cfg.LoneIncomeMult)
                    .Report("!allyadmin status", "Admin status", "Teams with FriendlyIndex, players, commander, dropped hits.", true);
                _cache = x.Build(); _cacheKey = key;
                return _cache;
            }
            catch (Exception exception) { MelonLogger.Warning("[ALLY] ModUIData: " + exception.Message); return null; }
        }
    }
}
