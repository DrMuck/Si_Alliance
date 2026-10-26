using System;
using System.IO;
using MelonLoader;
using MelonLoader.Utils;
using Newtonsoft.Json;

namespace Si_Alliance
{
    /// <summary>
    /// UserData/cfg/Alliance/Si_Alliance_Config.json (PLAN sections 6 and 9).
    /// </summary>
    public class AllianceConfig
    {
        [JsonProperty("enabled")] public bool Enabled = true;
        // No player request before this many minutes of game time (admin force ignores it).
        [JsonProperty("min_minutes")] public float MinMinutes = 5f;
        // Target team without a human commander (RTS AI): "always" = accepts (fps_vote off) or lets its players vote
        // (fps_vote on; no players = passes), "never" = refuses, "vote" = reserved (treated as "never").
        [JsonProperty("ai_policy")] public string AiPolicy = "always";
        // FPS vote on a request: "both" = players of both teams vote, "lone" = only the asked team votes (the proposing
        // commander's word stands for his team), "off" = the asked commander answers alone (0.1.x behaviour).
        [JsonProperty("fps_vote")] public string FpsVote = "both";
        // Vote duration in seconds (10..180).
        [JsonProperty("vote_s")] public float VoteS = 45f;
        // Share of a team's human players that must have voted for that team's vote to count (0..1).
        [JsonProperty("min_turnout")] public float MinTurnout = 0.33f;
        // 0.1.x key "quorum" is gone (ignored when an old file is loaded, dropped on the next save).
        // Seconds between "!ally break" and the end of the alliance.
        [JsonProperty("break_grace_s")] public float BreakGraceS = 30f;
        // A team whose request was declined cannot request again for this long.
        [JsonProperty("decline_cooldown_s")] public float DeclineCooldownS = 120f;
        // Harvest multiplier for the lone team while an alliance is active (1.0 = off).
        [JsonProperty("lone_income_mult")] public float LoneIncomeMult = 1.0f;
        // Hand the allied pair to Si_EloMatchmaking as one bloc for the join rule (AllianceHook.GroupOf).
        [JsonProperty("bloc_balance")] public bool BlocBalance = true;
        [JsonProperty("one_alliance_per_round")] public bool OneAlliancePerRound = true;
        // Allied team voice (server relay; unmodded clients still only play other-team voice in proximity).
        [JsonProperty("allied_voice")] public bool AlliedVoice = true;
        [JsonProperty("debug")] public bool Debug = false;

        public static string Dir => System.IO.Path.Combine(MelonEnvironment.UserDataDirectory, "cfg", "Alliance");
        public static string Path => System.IO.Path.Combine(Dir, "Si_Alliance_Config.json");

        public static AllianceConfig Load()
        {
            AllianceConfig cfg = new AllianceConfig();
            try
            {
                if (File.Exists(Path))
                {
                    string json = File.ReadAllText(Path);
                    cfg = JsonConvert.DeserializeObject<AllianceConfig>(json) ?? new AllianceConfig();
                    MelonLogger.Msg("[ALLY] Config loaded from " + Path);
                    cfg.Sanitize();
                    // 0.1.x files (they have "quorum") carry fps_vote "off" only because the vote was reserved then
                    bool legacy = false;
                    try { legacy = Newtonsoft.Json.Linq.JObject.Parse(json)["quorum"] != null; } catch { }
                    if (legacy && cfg.FpsVote == "off")
                    {
                        cfg.FpsVote = "both";
                        MelonLogger.Msg("[ALLY] Config from 0.1.x: fps_vote \"off\" (reserved then) -> \"both\"");
                    }
                    cfg.Save();   // writes keys added by a newer version
                }
                else
                {
                    cfg.Save();
                    MelonLogger.Msg("[ALLY] Default config written to " + Path);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[ALLY] Config load failed (" + ex.Message + "), using defaults");
            }
            cfg.Sanitize();
            return cfg;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(Path, JsonConvert.SerializeObject(this, Formatting.Indented));
            }
            catch (Exception ex) { MelonLogger.Warning("[ALLY] Config save failed: " + ex.Message); }
        }

        public void Sanitize()
        {
            if (float.IsNaN(MinMinutes) || MinMinutes < 0f) MinMinutes = 0f;
            if (MinMinutes > 120f) MinMinutes = 120f;
            AiPolicy = (AiPolicy ?? "always").Trim().ToLowerInvariant();
            if (AiPolicy != "always" && AiPolicy != "never" && AiPolicy != "vote") AiPolicy = "always";
            FpsVote = (FpsVote ?? "both").Trim().ToLowerInvariant();
            if (FpsVote != "off" && FpsVote != "both" && FpsVote != "lone") FpsVote = "both";
            if (float.IsNaN(VoteS) || VoteS < 10f) VoteS = 10f;
            if (VoteS > 180f) VoteS = 180f;
            if (float.IsNaN(MinTurnout) || MinTurnout < 0f) MinTurnout = 0f;
            if (MinTurnout > 1f) MinTurnout = 1f;
            if (float.IsNaN(BreakGraceS) || BreakGraceS < 0f) BreakGraceS = 0f;
            if (BreakGraceS > 600f) BreakGraceS = 600f;
            if (float.IsNaN(DeclineCooldownS) || DeclineCooldownS < 0f) DeclineCooldownS = 0f;
            if (DeclineCooldownS > 3600f) DeclineCooldownS = 3600f;
            LoneIncomeMult = ClampMult(LoneIncomeMult);
        }

        public static float ClampMult(float m)
        {
            if (float.IsNaN(m) || m < 1f) return 1f;
            if (m > 5f) return 5f;
            return m;
        }
    }
}
