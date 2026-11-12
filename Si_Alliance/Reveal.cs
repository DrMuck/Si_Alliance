using MelonLoader;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Si_Alliance
{
    /// <summary>
    /// Ally reveal sweep (0.3.3). Bug: an ally object spawned while the alliance stands stays invisible to the other ally's
    /// clients (no map icon, hidden in the RTS view, placement blocked by an "invisible" object). Game.SpawnPrefab sets
    /// BaseGameObject.Team before SetNetSource/SendNetSpawn; the Team setter calls Target.RevealToFriendlyTeams ->
    /// ally.DetectTarget -> RPC_TargetDetected, which reaches the clients before the ObjectCreate packet and is dropped there
    /// (unknown NetID). Team.TickTeams then pins friendly detections at 8 s, so the target is never lost and the RPC is never
    /// sent again; clients do not know FriendlyIndex, so they never reveal it themselves.
    ///
    /// Fix: while an alliance is Active (also during MissionState INIT: the HQs spawn on the first team join) every
    /// reveal_interval_s, for X = A, Y = B and X = B, Y = A, every target of Y in X.DetectedTargets with a valid NetID is
    /// re-sent to the clients as Team X TARGET_DETECTED (the vanilla RPC, Team.CreateRPCPacket(2) + packed NetID, broadcast
    /// by Team.SendRPCPacket) once, at the earliest in the sweep after the one that first saw it. All spawn paths send the
    /// ObjectCreate packet synchronously in the same call that set the Team, so one sweep later it is out. No
    /// Team.DetectTarget / LostTarget calls (they fire game events with AI side effects); duplicate RPCs are harmless on the
    /// client (DetectTarget only refreshes the timer of a known target).
    /// Tracked per (team X, Target instance) with the NetID: a NetID change re-sends. Entries not seen in a sweep are pruned.
    /// Players whose level finished loading (GameEvents.OnPlayerJoined: new joiners and re-loads after a map change) get all
    /// ally objects already sent, to that player only, a few seconds later (covers Si_PacketChunker's TeamInit trimming on
    /// 0.9.47 and the join snapshot race).
    /// </summary>
    internal static class Reveal
    {
        const byte RpcTargetDetected = 2;    // Team.ERPCs.TARGET_DETECTED (private enum; index 2 in 0.9.47 and 1.0.x)
        const int MaxSendsPerSweep = 100;    // formation of a mid-round alliance: hundreds of existing objects, spread over a few sweeps
        const int PruneEverySweeps = 10;
        const float JoinDelayS = 5f;
        const float SummaryEveryS = 60f;

        struct Entry
        {
            public uint NetId;
            public int FirstSweep, SeenSweep;
            public bool Sent;
        }

        sealed class RefEq : IEqualityComparer<Target>
        {
            public bool Equals(Target? x, Target? y) => ReferenceEquals(x, y);
            public int GetHashCode(Target obj) => RuntimeHelpers.GetHashCode(obj);
        }

        static readonly RefEq _eq = new RefEq();
        // targets of B as seen by A (re-sent as Team A TARGET_DETECTED), and the reverse
        static readonly Dictionary<Target, Entry> _forA = new Dictionary<Target, Entry>(256, _eq);
        static readonly Dictionary<Target, Entry> _forB = new Dictionary<Target, Entry>(256, _eq);
        static readonly List<Target> _scratch = new List<Target>(64);
        static readonly List<Player> _joinWho = new List<Player>(8);
        static readonly List<float> _joinAt = new List<float>(8);

        static Team? _a, _b;
        static int _sweep;
        static float _next, _nextSummary;
        static bool _firstLogged, _failed;
        static int _sentSinceSummary, _sentTotal, _joinResends;

        internal static int SentTotal => _sentTotal;
        internal static int Tracked => _forA.Count + _forB.Count;

        static AllianceConfig Cfg => AllianceMod.Cfg;

        /// <summary>Called every frame by OnUpdate while an alliance is Active; sweeps every reveal_interval_s.</summary>
        internal static void Tick(float now)
        {
            if (_failed || !Cfg.RevealAllies || !Alliance.Active) return;
            if (now < _next) return;
            _next = now + Cfg.RevealIntervalS;
            Team? a = Alliance.A, b = Alliance.B;
            if (a == null || b == null) return;
            if (!ReferenceEquals(a, _a) || !ReferenceEquals(b, _b))
            {
                if (_a != null || _b != null) { Reset(null); _next = now + Cfg.RevealIntervalS; }   // another pair without a reset in between
                _a = a; _b = b;
            }

            _sweep++;
            int budget = MaxSendsPerSweep;
            int sent = Sweep(a, b, _forA, ref budget);
            sent += Sweep(b, a, _forB, ref budget);
            if (_sweep % PruneEverySweeps == 0) { Prune(_forA); Prune(_forB); }

            if (sent > 0)
            {
                _sentTotal += sent;
                if (!_firstLogged)
                {
                    _firstLogged = true;
                    _nextSummary = now + SummaryEveryS;
                    MelonLogger.Msg("[ALLY] reveal: re-sending ally objects to clients for " + Alliance.NameOf(a) + " + " + Alliance.NameOf(b) + " (" + sent +
                                    " in the first sweep" + (budget <= 0 ? ", capped at " + MaxSendsPerSweep + " per sweep" : "") + ", every " +
                                    Cfg.RevealIntervalS.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " s)");
                }
                else _sentSinceSummary += sent;
            }
            if (_sentSinceSummary > 0 && now >= _nextSummary)
            {
                _nextSummary = now + SummaryEveryS;
                MelonLogger.Msg("[ALLY] reveal: re-sent " + _sentSinceSummary + " ally objects to clients (total " + _sentTotal + ", tracked " + Tracked + ")");
                _sentSinceSummary = 0;
            }
            if (_joinWho.Count > 0) JoinResends(now, a, b);
        }

        /// <summary>
        /// Targets of y in x.DetectedTargets: new ones are recorded (never sent in the sweep that first saw them), recorded
        /// ones that are still unsent, alive and active go out as Team x TARGET_DETECTED to every client.
        /// </summary>
        static int Sweep(Team x, Team y, Dictionary<Target, Entry> map, ref int budget)
        {
            List<Target> list = x.DetectedTargets;
            if (list == null) return 0;
            int sent = 0;
            for (int i = 0; i < list.Count; i++)
            {
                Target t = list[i];
                if ((object)t == null) continue;
                BaseGameObject o = t.Owner;
                if ((object)o == null || !ReferenceEquals(o.Team, y)) continue;
                NetworkComponent nc = o.NetworkComponent;
                if ((object)nc == null || !nc.IsValid || nc.NetID == 0) continue;
                uint id = nc.NetID;
                if (!map.TryGetValue(t, out Entry e) || e.NetId != id)
                {
                    map[t] = new Entry { NetId = id, FirstSweep = _sweep, SeenSweep = _sweep, Sent = false };
                    continue;
                }
                e.SeenSweep = _sweep;
                if (!e.Sent && budget > 0 && e.FirstSweep < _sweep && !t.IsDestroyed && t.IsActive && Send(x, id, null))
                {
                    e.Sent = true;
                    sent++;
                    budget--;
                }
                map[t] = e;
            }
            return sent;
        }

        /// <summary>Drops entries not seen in the current sweep (destroyed, lost, changed team).</summary>
        static void Prune(Dictionary<Target, Entry> map)
        {
            _scratch.Clear();
            foreach (var kv in map)
                if (kv.Value.SeenSweep != _sweep) _scratch.Add(kv.Key);
            for (int i = 0; i < _scratch.Count; i++) map.Remove(_scratch[i]);
            _scratch.Clear();
        }

        /// <summary>Team x TARGET_DETECTED for netId: to = null broadcasts (Team.SendRPCPacket), else to that player only.</summary>
        static bool Send(Team x, uint netId, Player? to)
        {
            GameByteStreamWriter? w = x.CreateRPCPacket(RpcTargetDetected);
            if (w == null) return false;
            w.WritePackedUInt32(netId);
            return to == null ? x.SendRPCPacket(w) : NetworkLayer.SendPacketToPlayer(w, ENetworkPacketSend.Reliable, to);
        }

        // ------------------------------------------------------------------ joins

        /// <summary>GameEvents.OnPlayerJoined (the player's level is loaded and the server sent it every object): queue a re-send.</summary>
        internal static void OnPlayerJoined(Player p)
        {
            try
            {
                if (!AllianceMod._registered || (object)p == null || !Cfg.RevealAllies || !Alliance.Active) return;
                if (ReferenceEquals(p, NetworkGameServer.GetServerPlayer())) return;
                float due = Time.time + JoinDelayS;
                for (int i = 0; i < _joinWho.Count; i++)
                    if (ReferenceEquals(_joinWho[i], p)) { _joinAt[i] = due; return; }
                _joinWho.Add(p);
                _joinAt.Add(due);
            }
            catch (Exception ex) { MelonLogger.Error("[ALLY] reveal OnPlayerJoined: " + ex); }
        }

        static void JoinResends(float now, Team a, Team b)
        {
            for (int i = _joinWho.Count - 1; i >= 0; i--)
            {
                if (now < _joinAt[i]) continue;
                Player p = _joinWho[i];
                _joinWho.RemoveAt(i);
                _joinAt.RemoveAt(i);
                if (p == null || !Player.Players.Contains(p) || !p.NetIsReady) continue;   // left, or loading again (joins again then)
                int n = ResendTo(a, b, _forA, p) + ResendTo(b, a, _forB, p);
                _joinResends++;
                MelonLogger.Msg("[ALLY] reveal: join re-send to " + (p.PlayerName ?? "?") + ": " + n + " ally objects");
            }
        }

        /// <summary>Every entry already broadcast whose target still belongs to y with the same NetID, to player p only.</summary>
        static int ResendTo(Team x, Team y, Dictionary<Target, Entry> map, Player p)
        {
            int n = 0;
            foreach (var kv in map)
            {
                Entry e = kv.Value;
                if (!e.Sent) continue;
                Target t = kv.Key;
                if ((object)t == null) continue;
                BaseGameObject o = t.Owner;
                if ((object)o == null || !ReferenceEquals(o.Team, y)) continue;
                NetworkComponent nc = o.NetworkComponent;
                if ((object)nc == null || !nc.IsValid || nc.NetID != e.NetId) continue;
                if (t.IsDestroyed) continue;
                if (Send(x, e.NetId, p)) n++;
            }
            return n;
        }

        // ------------------------------------------------------------------ state

        /// <summary>Stops the sweep after an exception until the next reset (no error spam every interval).</summary>
        internal static void Fail(Exception ex)
        {
            _failed = true;
            MelonLogger.Error("[ALLY] reveal sweep failed, off until the alliance ends: " + ex);
        }

        /// <summary>Clears all tracking (formation, dissolve, restart, scene load). why != null logs a final line when anything was sent.</summary>
        internal static void Reset(string? why)
        {
            if (why != null && (_sentTotal > 0 || _joinResends > 0))
                MelonLogger.Msg("[ALLY] reveal: stopped (" + why + "), re-sent " + _sentTotal + " ally objects in total, " + _joinResends + " join re-sends");
            _forA.Clear(); _forB.Clear(); _scratch.Clear();
            _joinWho.Clear(); _joinAt.Clear();
            _a = null; _b = null;
            _sweep = 0; _next = 0f; _nextSummary = 0f;
            _firstLogged = false; _failed = false;
            _sentSinceSummary = 0; _sentTotal = 0; _joinResends = 0;
        }

        internal static string StatusText()
        {
            return "reveal_allies " + Cfg.RevealAllies + " (every " + Cfg.RevealIntervalS.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " s" +
                   (Alliance.Active && Cfg.RevealAllies ? ", re-sent " + _sentTotal + ", tracked " + Tracked + ", join re-sends " + _joinResends + (_failed ? ", FAILED" : "") : "") + ")";
        }
    }
}
