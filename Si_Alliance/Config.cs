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
        // 0.1.x-0.3.1 key "min_minutes" is gone since 0.3.2: requests from the round start (ignored in old files, dropped on the next save).
        // Target team without a human commander (RTS AI): "always" = accepts (fps_vote off) or lets its players vote
        // (fps_vote on; no players = passes), "never" = refuses, "vote" = reserved (treated as "never").
        [JsonProperty("ai_policy")] public string AiPolicy = "always";
        // FPS vote on a request: "both" = players of both teams vote, "lone" = only the asked team votes (the proposing
        // commander's word stands for his team), "off" = the asked commander answers alone (0.1.x behaviour).
        [JsonProperty("fps_vote")] public string FpsVote = "both";
        // Vote duration in seconds (10..180).
        [JsonProperty("vote_s")] public float VoteS = 45f;
        // 0.2.x/0.3.0 key "min_turnout" is gone since 0.3.1: majority of the votes cast (ignored in old files, dropped on the next save).
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
        /// <summary>Seconds between "X + Y are allied" reminders to everyone while an alliance is active (0 = off).</summary>
        [JsonProperty("remind_s")] public float RemindS = 180f;
        /// <summary>Players who join (or were absent at formation) get a private notice ~10 s after they appear.</summary>
        [JsonProperty("join_notice")] public bool JoinNotice = true;
        /// <summary>Seconds of game time after the round start at which everyone gets a short !ally / vote explanation (HvHvA only, 0 = off).</summary>
        [JsonProperty("intro_s")] public float IntroS = 60f;
        /// <summary>
        /// Locked alliance (AllianceApi.ForceAlliance(.., locked: true), e.g. Si_VersusVote's 2v1) when the lone team is
        /// eliminated: "humans_win" = the round ends at once, the allies win together (the ally with more structures is
        /// the reported winner), "dissolve" = like an unlocked alliance, final 1v1 between the former allies.
        /// </summary>
        [JsonProperty("locked_end")] public string LockedEnd = "humans_win";
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
            AiPolicy = (AiPolicy ?? "always").Trim().ToLowerInvariant();
            if (AiPolicy != "always" && AiPolicy != "never" && AiPolicy != "vote") AiPolicy = "always";
            FpsVote = (FpsVote ?? "both").Trim().ToLowerInvariant();
            if (FpsVote != "off" && FpsVote != "both" && FpsVote != "lone") FpsVote = "both";
            if (float.IsNaN(VoteS) || VoteS < 10f) VoteS = 10f;
            if (VoteS > 180f) VoteS = 180f;
            if (float.IsNaN(BreakGraceS) || BreakGraceS < 0f) BreakGraceS = 0f;
            if (BreakGraceS > 600f) BreakGraceS = 600f;
            if (float.IsNaN(DeclineCooldownS) || DeclineCooldownS < 0f) DeclineCooldownS = 0f;
            if (DeclineCooldownS > 3600f) DeclineCooldownS = 3600f;
            if (float.IsNaN(RemindS) || RemindS < 0f) RemindS = 0f;
            if (RemindS > 0f && RemindS < 30f) RemindS = 30f;
            if (RemindS > 3600f) RemindS = 3600f;
            if (float.IsNaN(IntroS) || IntroS < 0f) IntroS = 0f;
            if (IntroS > 3600f) IntroS = 3600f;
            LoneIncomeMult = ClampMult(LoneIncomeMult);
            LockedEnd = (LockedEnd ?? "humans_win").Trim().ToLowerInvariant();
            if (LockedEnd != "humans_win" && LockedEnd != "dissolve") LockedEnd = "humans_win";
        }

        public static float ClampMult(float m)
        {
            if (float.IsNaN(m) || m < 1f) return 1f;
            if (m > 5f) return 5f;
            return m;
        }
    }
}
