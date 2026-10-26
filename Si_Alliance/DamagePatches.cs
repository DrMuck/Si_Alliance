using HarmonyLib;
using MelonLoader;
using SilicaAdminMod;
using System;
using System.Reflection;
using UnityEngine;

namespace Si_Alliance
{
    /// <summary>
    /// Damage between allies is dropped on the server (PLAN section 4):
    ///  (a) player hits arrive as ObjectClientDamageHit packets: Admin Mod Event_Damage.OnPrePlayerDamageReceived
    ///      (its transpiler in OnReceiveClientDamageHitPacket) - never a prefix returning false in the packet path;
    ///  (b) server-side damage (AI weapons, splash, melee, collisions): prefix on DamageManager.ApplyDamage that
    ///      zeroes the damage argument (ApplyDamage returns 0 at once for damage == 0);
    ///  (c) ProjectileBasic friendly-fire checks: allied projectiles pass through and do no damage (Si_4way).
    /// Plus allied team voice (Player.GetCanReceiveVoice) and the destroyed postfixes that trigger the dissolve check.
    /// Hot paths: the static Alliance.Active check comes first; no try/catch, LINQ or allocation per call.
    /// Negative damage (repair) is never touched.
    /// </summary>
    internal static class DamagePatches
    {
        internal static void Apply(HarmonyLib.Harmony harmony)
        {
            Patch(harmony, AccessTools.Method(typeof(DamageManager), "ApplyDamage",
                      new[] { typeof(Collider), typeof(float), typeof(EDamageType), typeof(GameObject), typeof(Vector3) }),
                  nameof(Prefix_ApplyDamage), null, "DamageManager.ApplyDamage");
            Patch(harmony, AccessTools.Method(typeof(ProjectileBasic), "GetAllowFriendlyFireOnImpact", new[] { typeof(GameObject) }),
                  nameof(Prefix_FFOnImpact), null, "ProjectileBasic.GetAllowFriendlyFireOnImpact");
            Patch(harmony, AccessTools.Method(typeof(ProjectileBasic), "GetAllowFriendlyFireDamage", new[] { typeof(DamageManager) }),
                  nameof(Prefix_FFDamage), null, "ProjectileBasic.GetAllowFriendlyFireDamage");
            Patch(harmony, AccessTools.Method(typeof(Player), "GetCanReceiveVoice", new[] { typeof(Player), typeof(bool) }),
                  nameof(Prefix_GetCanReceiveVoice), null, "Player.GetCanReceiveVoice");
            Patch(harmony, AccessTools.Method(typeof(MP_Strategy), "OnStructureDestroyed", new[] { typeof(Structure), typeof(GameObject) }),
                  null, nameof(Postfix_OnStructureDestroyed), "MP_Strategy.OnStructureDestroyed");
            Patch(harmony, AccessTools.Method(typeof(MP_Strategy), "OnUnitDestroyed", new[] { typeof(Unit), typeof(GameObject) }),
                  null, nameof(Postfix_OnUnitDestroyed), "MP_Strategy.OnUnitDestroyed");
            // 1.0.x only (0.9.47 has no server-side join gate): skipped silently when missing
            MethodInfo? join = AccessTools.Method(typeof(MP_Strategy), "GetCanJoinTeam", new[] { typeof(Team), typeof(Player) });
            if (join != null)
            {
                try { harmony.Patch(join, postfix: new HarmonyMethod(typeof(Balance), nameof(Balance.Postfix_GetCanJoinTeam))); AllianceMod.Debug("patched MP_Strategy.GetCanJoinTeam"); }
                catch (Exception ex) { MelonLogger.Warning("[ALLY] patching MP_Strategy.GetCanJoinTeam failed: " + ex.Message); }
            }
            else AllianceMod.Debug("MP_Strategy.GetCanJoinTeam not present (0.9.47): no vanilla join gate to relax");
        }

        static void Patch(HarmonyLib.Harmony harmony, MethodInfo? target, string? prefix, string? postfix, string label)
        {
            if (target == null) { MelonLogger.Warning("[ALLY] " + label + " not found: not patched"); return; }
            try
            {
                harmony.Patch(target,
                    prefix: prefix != null ? new HarmonyMethod(typeof(DamagePatches), prefix) : null,
                    postfix: postfix != null ? new HarmonyMethod(typeof(DamagePatches), postfix) : null);
                AllianceMod.Debug("patched " + label);
            }
            catch (Exception ex) { MelonLogger.Warning("[ALLY] patching " + label + " failed: " + ex.Message); }
        }

        // ------------------------------------------------------------------ (a) player hits (Admin Mod event)

        internal static void OnPrePlayerDamage(object? sender, OnPreDamageReceivedArgs args)
        {
            if (!Alliance.Active || args == null) return;
            if (args.Damage <= 0f) return;                       // keep repair
            DamageManager dm = args.DamageManager;
            if (dm == null) return;
            Team victim = dm.Team;
            if (!Alliance.IsMember(victim)) return;
            GameObject inst = args.Instigator;                    // the getter returns null when the packet named no object
            if (inst == null) return;
            BaseGameObject shooter = inst.GetBaseGameObject();
            if (shooter == null) return;
            if (!Alliance.IsCrossAllied(shooter.Team, victim)) return;
            args.Damage = 0f;
            Alliance.DroppedHits++;
        }

        // ------------------------------------------------------------------ (b) server-side damage

        // ApplyDamage(Collider collider, float damage, EDamageType damageType, GameObject instigator, Vector3 hitPoint)
        static void Prefix_ApplyDamage(DamageManager __instance, ref float __1, GameObject __3)
        {
            if (!Alliance.Active || __1 <= 0f) return;
            Team victim = __instance.Team;
            if (!Alliance.IsMember(victim)) return;
            if (__3 == null) return;
            BaseGameObject shooter = __3.GetBaseGameObject();
            if (shooter == null) return;
            if (!Alliance.IsCrossAllied(shooter.Team, victim)) return;
            __1 = 0f;
            Alliance.DroppedServerHits++;
        }

        // ------------------------------------------------------------------ (c) projectile friendly fire

        static bool Prefix_FFOnImpact(ProjectileBasic __instance, GameObject __0, ref bool __result)
        {
            if (!Alliance.Active || __0 == null) return true;
            Team projTeam = __instance.Team;
            if (!Alliance.IsMember(projTeam)) return true;
            BaseGameObject hit = __0.GetBaseGameObject();
            if (hit == null) return true;
            if (!Alliance.IsCrossAllied(projTeam, hit.Team)) return true;
            ProjectileData data = __instance.Data;
            if (data != null && data.IsRepair) return true;      // repair beams keep working on allies
            __result = false;                                      // pass through the ally
            return false;
        }

        static bool Prefix_FFDamage(ProjectileBasic __instance, DamageManager __0, ref bool __result)
        {
            if (!Alliance.Active || __0 == null) return true;
            Team projTeam = __instance.Team;
            if (!Alliance.IsMember(projTeam)) return true;
            if (!Alliance.IsCrossAllied(projTeam, __0.Team)) return true;
            ProjectileData data = __instance.Data;
            if (data != null && data.IsRepair) return true;
            __result = false;
            return false;
        }

        // ------------------------------------------------------------------ allied voice

        // GetCanReceiveVoice(Player fromPlayer, bool proximityOnly): the server relays team voice of an ally like own-team
        // voice. Proximity voice keeps the vanilla distance rule.
        static bool Prefix_GetCanReceiveVoice(Player __instance, Player __0, bool __1, ref bool __result)
        {
            if (!Alliance.Active || __1 || !AllianceMod.Cfg.AlliedVoice) return true;
            if (__0 == null || __instance == null) return true;
            if (__0.IsMuted) return true;
            if (!Alliance.IsCrossAllied(__0.Team, __instance.Team)) return true;
            __result = true;
            return false;
        }

        // ------------------------------------------------------------------ dissolve triggers

        static void Postfix_OnStructureDestroyed(Structure __0)
        {
            if (!Alliance.Active || __0 == null) return;
            ObjectInfo info = __0.ObjectInfo;
            if (info != null && info.Critical) AllianceMod.CheckSoon = true;
        }

        static void Postfix_OnUnitDestroyed(Unit __0)
        {
            if (!Alliance.Active || __0 == null) return;
            ObjectInfo info = __0.ObjectInfo;
            if (info != null && info.Critical) AllianceMod.CheckSoon = true;
        }
    }
}
