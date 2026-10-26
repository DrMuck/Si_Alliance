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
        // Target team without a human commander (RTS AI): "always" = accepts at once, "never" = refuses,
        // "vote" = reserved for the FPS vote (v1 treats it as "never").
        [JsonProperty("ai_policy")] public string AiPolicy = "always";
        // Reserved for the FPS ratification vote (off | both | lone). v1: only "off".
        [JsonProperty("fps_vote")] public string FpsVote = "off";
        [JsonProperty("quorum")] public float Quorum = 0.54f;
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
                    cfg = JsonConvert.DeserializeObject<AllianceConfig>(File.ReadAllText(Path)) ?? new AllianceConfig();
                    MelonLogger.Msg("[ALLY] Config loaded from " + Path);
                    cfg.Sanitize();
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
            FpsVote = (FpsVote ?? "off").Trim().ToLowerInvariant();
            if (FpsVote != "off" && FpsVote != "both" && FpsVote != "lone") FpsVote = "off";
            if (float.IsNaN(Quorum) || Quorum < 0.1f) Quorum = 0.1f;
            if (Quorum > 1f) Quorum = 1f;
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
