# Si_Alliance - in-game team alliances for HvHvA (design + feasibility)

Date: 2026-10-26. Status: DESIGN ONLY, nothing built. Sources: decompiles C:\tmp\silica_decompile_106 (test 1.0.6/1.0.8)
and C:\tmp\silica_decompile_0947 (public server), Si_4way\Si_4way_Server\Alliance.cs (March 2026 alliance layer),
Si_EloMatchmaking, Si_KingOfTheHill, Si_RTS_AI, Si_AdminMod sources.

## 1. Verdict

**Yes, a SERVER-ONLY alliance mod is feasible.** Vanilla already has the primitive:

- `Team.FriendlyIndex` (public int, Team.cs:50, both branches). Tooltip: "teams with this same index are considered
  friendly and expose fog of war etc to each other". Shipped assets: Sol=1, Centauri=2, Alien=3, Wildlife/GM=-1
  (the `*_Allied` twin teams "Sol II" etc. share their parent's index). Nothing in code writes it at runtime.
- `GameMode.GetTeamsAreEnemy` / `GetTeamsAreFriendly` (GameMode.cs:263/276, virtual, MP_Strategy does NOT override)
  return not-enemy / friendly when both teams share a FriendlyIndex > -1. Every hostility check in the game goes
  through these (Target.GetIsEnemy, TargetFinder, AI tasks, turret/creature/soldier fire gating, detection, pings).
- `Team.TickTeams` (Team.cs:885) notices a FriendlyIndex change and calls `Target.RevealToFriendlyTeams()`;
  friendly teams' targets stay detected (Team.cs:906). Detections reach clients via `RPC_TargetDetected`.
- FriendlyIndex is NOT networked (not in Team.OnSendNetInit, no RPC). Clients keep 1/2/3.

So: `cent.FriendlyIndex = sol.FriendlyIndex` on the server = AI targeting, turrets, detection sharing, ping relay,
RTS_AI Intel all treat the two as allies. No Harmony patch on GetTeamsAreEnemy needed (and a postfix alone would miss
the GetTeamsAreFriendly paths). KoH's existing postfix only flips the GM team, no conflict.

`ETeamsVersus.HUMANS2_VS_ALIENS` (GameModeExt.cs:13) is a stub: only the enum + an achievements mapping use it, the
vote fallback excludes it, nothing sets FriendlyIndex from it. Not a usable vanilla 2v1 mode yet. Together with the
`*_Allied` twin teams it looks like a vanilla 2v1 is being prepared; if it ships, it will have to set FriendlyIndex
on both sides, which removes every client-side limitation in section 2. This mod builds on the same field, so it
stays compatible (it just restores whatever vanilla set at round start).

Si_4way (March 2026) proved the gameplay side in play: AI stopped targeting allies with a server-only GetTeamsAreEnemy
postfix + ProjectileBasic FF prefixes. Player-fired friendly fire was never verified there.

## 2. What server-only cannot do (accept these or ship a tiny client hook later)

Damage is client-authoritative but NOT client-applied: the client only sends `ObjectClientDamageHit` and shows hit
effects; health is reduced only on the server and replicated (`SynchHealth` 0.25 s). The server can therefore drop
allied damage completely. What stays wrong on unmodded clients:

| Client symptom | Cause | Mitigation |
|---|---|---|
| Allied units/structures drawn red (crosshair, health bars, map icons, outlines), "enemy spotted" voice, combat music | all UI uses GetIsEnemy with client FriendlyIndex | chat announcement + ModUI Info line "Sol + Centauri allied"; team colours stay distinct |
| Players can still shoot allies, hit sparks/blood show | client always sends the hit | server zeroes the damage (section 4) |
| Commander right-click on ally = Attack order (forced target bypasses enemy checks) | OrderTargetingController client-side | damage still dropped server-side; clear forced targets when alliance forms |
| Ally's explored fog terrain not shared (FogOfWar.cs:568 is local) | client-local | spotted enemy units ARE shared (detection RPCs) |
| Ally pings relayed by server but hidden by PingManager | client-side filter | none |
| StealthIndicator says "spotted" while ally sees you | server pins ally targets detected | none, cosmetic |
| Win screen: if the round ends with two allied survivors, `GetWinningTeam()` is null on every client -> no End screen, both "lost" | LocalWon/LocalLost computed per client | **avoid by design: alliance dissolves when the third team dies (section 3)** |

Optional later: the Si_ModUI CLIENT mod is already installed by regulars. A 10-line hook there ("set FriendlyIndex of
team X to Y" from the server document) would fix every cosmetic row above. Not required for v1.

## 3. Game design (proposal)

**Scope:** HvHvA only (3 active teams). Alliance = exactly one pair; the third team is "the lone team".

**Lifecycle**
1. Request: commander of team A types `!ally <team>` (or ModUI picker). Only commanders can request. Cooldown
   per team after a decline (config, 120 s).
2. Accept: commander of team B `!ally accept` / `!ally decline`. If B has no human commander (RTS_AI team):
   config policy `ai_policy = never | always | vote`. Optional FPS ratification (config `fps_vote = off|both|lone`):
   team-only vote via chat `!ally yes/no`, quorum like CommManagement Mutineer (ceil(0.54 * team players), 30 s),
   announced with SendChatMessageToTeam. Default v1: commanders only, no FPS vote (fewer rules first).
3. Active: announce to ALL ("Sol and Centauri are now allied against Alien"), ModUI Info line, FriendlyIndex set,
   damage drop on, balancing on (section 5).
4. End: automatically when the lone team loses its last critical (GetHasLost) -> "Alliance over, final showdown!",
   FriendlyIndex restored, 1v1 to the normal vanilla end (real winner, End screen works). Also ends when either
   ally is eliminated, at round end, or by `!ally break` (announced, takes effect after a config grace of 30 s so
   nobody gets backstabbed mid-fight; one break per team per round).
5. Restrictions: no new alliance within the first N minutes (config `min_minutes = 5`), at most one alliance per
   round (config), admin `!ally force <A> <B>` / `!ally clear` for testing.

## 4. Mechanics (server mod)

- **Hostility:** store original FriendlyIndex per team at round start, set `b.FriendlyIndex = a.FriendlyIndex`,
  restore on end. Clear `Unit.ForcedAttackTarget` / assigned forced targets of both teams on formation (otherwise an
  existing attack order keeps firing at the ally; damage is dropped anyway).
- **Damage drop:** (a) player hits: AdminMod `Event_Damage.OnPrePlayerDamageReceived` -> `a.Damage = 0` when
  instigator team and victim team are friendly (same hook as Si_AntiCheat/Si_FriendlyFireLimits; never a prefix that
  returns false, it breaks the packet stream). Keep negative damage (repair). (b) server-side damage (AI weapons,
  splash, melee, DamageZone, collisions): Harmony prefix on `DamageManager.ApplyDamage` on the server, same test.
  (c) optional belt-and-braces: Si_4way's `ProjectileBasic.GetAllowFriendlyFireOnImpact/Damage` prefixes (cache
  the Team accessor, hot path). Si_FriendlyFireLimits 1.3.5 compares Team.Index only, no interference.
- **Voice:** `Player.GetCanReceiveVoice` prefix from Si_4way (allied team voice). Optional.
- **Round state:** re-subscribe `GameEvents.OnGameStarted/OnGameEnded` in OnSceneWasLoaded (cleared each round);
  reset everything in OnGameStarted; hook `MP_Strategy.OnStructureDestroyed` / `StrategyMode.OnUnitDestroyed`
  postfixes (as Si_4way WinCondition.cs) or poll `GetHasLost()` of the lone team once per second to trigger step 3.4.
- **RTS_AI:** `Mil/Intel.cs:181-219` filters with Team.GetTeamsAreEnemy -> respects FriendlyIndex automatically.
  Needs a small public `Intel.ForgetTeam(Team)` so already-known ally bases drop out of Objectives (otherwise the
  stale target can linger until ForgetGone). ControlMap/CombatLog count all other teams (eco only, acceptable).
  Si_Alliance calls it by reflection if present.

## 5. Balancing the lone team

- **Player cap by bloc (preferred):** treat the allied pair as ONE bloc in the join rule: bloc counts
  {A+B, lone}; a join is allowed if bloc difference after join <= MaxDifference. The lone team may then fill up to
  the allied sum. Where: Si_EloMatchmaking owns the `MP_Strategy.ProcessNetRPC` join prefix (it unpatches
  BasicTeamBalance; a third prefix would fight over the consumed packet). Add to Elo (0.7.0): a public static
  `Func<Team, int>? TeamGroup` (or `ExtraSlots`) consulted in `BalanceRule.TeamStates/DiffAfterJoin/CountOk`, so
  join, redirect, `!reserve` and `!elo balance` all follow. Si_Alliance sets it by reflection. On 0.9.47 there is
  no vanilla server-side join check at all, so this is the only gate there too (public server runs Elo 0.3.0,
  needs the update). 1.0.x additionally has `MP_Strategy.GetCanJoinTeam` (MaximumPlayerDifference); a postfix there
  keeps vanilla from vetoing first.
  Announce when the alliance forms: "Alien is open for N more players" + the ModUI Info line.
- **Eco multiplier (config `lone_income_mult`, default 1.0 = off, try 1.25-1.5):** subscribe to vanilla
  `GameEvents.OnResourcesHarvested (Team, Resource, int delta, int total)` (fires from Team.OnExtractedResourcesStored
  after every harvester/shrimp deposit, server-only, both branches); pay `team.StoreResource(round(delta*(m-1)))`,
  leftover to `team.StartingResources` as KoH Rewards.cs does. No recursion (StoreResource does not fire the event).
  Si_UnitBalance has no income param, so this stays in Si_Alliance. Optional: scale by player imbalance
  (mult = 1 + k * (alliedPlayers - lonePlayers)/lonePlayers) instead of a flat number.
- Measure first (user's rule): log alliance formation time, player counts, income per team, outcome to
  UserData/cfg/Alliance/log/ for later review with MapReplay data.

## 6. Commands / UI

- Player: `!ally <team>`, `!ally accept|decline|break|status|yes|no`. Register via
  `HelperMethods.RegisterPlayerCommand("ally", cb, hideFromChat: true)`; replies `SendChatMessageToPlayer` /
  `SendChatMessageToTeam` / `ReplyToCommand`. Commander lookup `MP_Strategy.GetCommanderForTeam(team)`.
- Admin: `!ally force <A> <B>`, `!ally clear`, `!ally mult <x>` (Power.Cheat).
- ModUI (`ModUI_Export.cs` copy): Info line with status, Picker `team` for the request (commander-gated inside the
  handler), choice picker accept/decline, admin force/clear. Pending request shown as shared Info line (player data
  refreshes only once a minute).
- Config `UserData/cfg/Alliance/Si_Alliance_Config.json`: enabled, min_minutes, ai_policy, fps_vote, quorum,
  break_grace_s, decline_cooldown_s, lone_income_mult, bloc_balance, debug.

## 7. Project setup

Template Si_Spawn: netstandard2.1, `SilicaBranch` switch (test default; `-p:SilicaBranch=default` ->
bin\Release\default\), include\default + include\test from Projects\_silica_refs, MelonInfo author DrMuck,
`[SPAWN]`-style tag `<color=#...>[ALLY]</color>`, refcheck both builds, dist zip in Projects\_dist. Mono server:
typed `Team` params in Harmony patches are fine (object/reflection is only an Il2Cpp client rule).
Local server is Release 1.0.8 (test), public server 0.9.47. 0.9.47 differences that matter: `GetWinningTeam`
non-virtual (not needed with the dissolve design), no GetCanJoinTeam, AI target filter in Unit.cs instead of
TargetFinder (same GetTeamsAreEnemy call).

## 8. Implementation steps (one feature at a time, test each in game, Si_4way lesson)

1. Skeleton + config + `!ally force/clear` admin command setting FriendlyIndex; verify in game: AI turrets/units
   stop shooting the ally, RTS_AI stops attacking, minimap shows ally's detections.
2. Damage drop (Event_Damage + ApplyDamage prefix); verify: player shots, splash, melee, AI shots do no damage;
   repair still works.
3. Dissolve on lone-team elimination / ally elimination / round end; verify End screen and winner.
4. Commander request/accept flow + announcements + ModUI.
5. Bloc player balance (Elo 0.7.0 hook) + income multiplier + logging.
6. RTS_AI Intel.ForgetTeam + ai_policy; forced-target clearing; voice.
7. Dual-branch build, refcheck, dist, memory note.

## 9. Decisions (user, 2026-10-26)

- Alliance DISSOLVES when the lone team is eliminated -> final 1v1 showdown (vanilla End screen). Also dissolves when
  an ally is eliminated, at round end, or via `!ally break` (30 s grace, one break per team per round).
- v1: commanders only request/accept. No FPS vote (config keys reserved: fps_vote="off", quorum=0.54).
- Player cap: bloc balance (allied pair counts as one bloc) via a hook in Si_EloMatchmaking.
- Income multiplier: config `lone_income_mult`, default 1.0 (off); flat, not scaled by imbalance in v1.
- RTS_AI (no human commander) team: config `ai_policy`, default "always" (AI accepts any request; AI never requests).
- Server-only mod. No client component.

## 10. Cross-mod API contracts

- **Si_EloMatchmaking 0.7.0:** `public static class Si_EloMatchmaking.AllianceHook { public static Func<Team,int>? GroupOf; }`
  Returns a group id per team (default team.Index). BalanceRule treats teams with the same group id as one bloc:
  bloc player count = sum of member teams; join allowed if (targetBloc + 1) - min(other bloc counts) <= MaxDifference;
  redirect/`!reserve`/`!elo balance` use the same grouping. Si_Alliance sets it by reflection (assembly scan for the
  type name), clears to null on dissolve/round end.
- **Si_RTS_AI:** Intel purges known structures/bases of teams that are no longer enemies (re-check
  `Team.GetTeamsAreEnemy` for known entries each tick); no cross-mod call needed.
