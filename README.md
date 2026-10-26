# Si_Alliance

Server-only MelonLoader mod for Silica: in a Humans vs Humans vs Aliens round, two commanders can form an alliance
against the third team. When the third ("lone") team is eliminated the alliance dissolves and the two allies finish
the round as a normal 1v1, so vanilla picks a real winner and shows the End screen.

How it works: vanilla already has `Team.FriendlyIndex`. Teams that share an index > -1 are not enemies for every
`GameMode.GetTeamsAreEnemy` / `GetTeamsAreFriendly` check (AI targeting, turrets, detection sharing, pings). The mod
gives both allies the same index while the alliance lasts and restores the vanilla value afterwards. Design, decisions
and cross-mod contracts: [docs/PLAN.md](docs/PLAN.md).

Requires the [Admin Mod](https://github.com/data-bomb/Silica) (commands, chat, damage event). Optional: Si_ModUI
(panel), Si_EloMatchmaking 0.7.0+ (bloc player balance).

## Commands

| Command | Who | Effect |
|---|---|---|
| `!ally <team>` | commander | Propose an alliance to that team (name prefix: `sol`, `cent`, `alien`). The other commander has 60 s to answer. A team without a human commander (RTS AI) answers by `ai_policy`. |
| `!ally accept` / `!ally decline` | commander of the asked team | Answer the open request (`!ally respond accept\|decline` does the same, used by the ModUI picker). A decline blocks the requesting team for `decline_cooldown_s`. |
| `!ally break` | commander of an ally | Announced to everyone; the alliance ends after `break_grace_s`. Once per team per round. |
| `!ally status` | anyone | Current alliance or open request. |
| `!allyadmin force <teamA> <teamB>` | admin (Cheat) | Ally two teams now; ignores `min_minutes` and `one_alliance_per_round` (still HvHvA only). |
| `!allyadmin clear` | admin | End the alliance or remove the open request. |
| `!allyadmin mult <x>` | admin | Set `lone_income_mult` (1..5, saved). |
| `!allyadmin status` | admin | Teams with FriendlyIndex, player count, commander, lost flag, dropped hit counters. |
| `!allyadmin reload` | admin | Re-read the config. |

The alliance ends automatically when the lone team is eliminated (final showdown), when either ally is eliminated,
at round end, on scene load, after `!ally break` + grace, or by `!allyadmin clear`.

## What it does while an alliance is active

- Same `FriendlyIndex` for both allies: AI units, turrets, RTS AI and detection treat them as friends.
- Attack orders of either team that point at the new ally are cleared once at formation.
- Allied damage is dropped on the server: player hits via the Admin Mod `Event_Damage.OnPrePlayerDamageReceived`,
  server-side damage via a prefix on `DamageManager.ApplyDamage` (damage set to 0), projectiles via
  `ProjectileBasic.GetAllowFriendlyFireOnImpact/Damage` (allied shots pass through). Repair (negative damage, repair
  projectiles) keeps working on allies.
- Allied team voice is relayed by the server (`allied_voice`).
- Player balance: with **Si_EloMatchmaking 0.7.0+** `AllianceHook.GroupOf` is set so the allied pair counts as one
  bloc in Elo's join rule (join, redirect, `!reserve`, `!elo balance`). **Without Elo on 1.0.x** only the vanilla
  gate exists: the server's `MaximumPlayerDifference` is raised to `max(orig, alliedSum - lone + orig)` (re-computed
  every second, restored on dissolve; the value is synced, so clients do not grey the join button), and a postfix on
  `MP_Strategy.GetCanJoinTeam` lets a join to the lone team through while it stays within allied sum + original
  difference. A difference of 0 (gate off) is not touched. **On 0.9.47 without Elo there is no server-side join gate
  at all.**
- Lone team income: every harvest deposit of the lone team pays `delta * (lone_income_mult - 1)` extra
  (`Team.StoreResource`, overflow to `StartingResources`). Off at the default 1.0.

## Known limitations (server-only, unmodded clients)

Clients keep their own FriendlyIndex (it is not networked), so on every client:

- allied units and structures are still drawn as enemies (red crosshair, health bars, map icons, outlines), "enemy
  spotted" voice lines and combat music play;
- players can still shoot allies and see hit effects; the server drops the damage, health never goes down;
- a commander's right-click on an ally still issues an attack order (forced target); damage is dropped anyway;
- the ally's explored fog terrain is not shared (spotted units are, via detection RPCs);
- ally pings are relayed but hidden by the client's ping filter; the stealth indicator may say "spotted";
- allied voice: the server relays team voice to the ally, but an unmodded client only plays other-team voice within
  proximity range (200 m), so in practice allied voice reaches only nearby players.

The alliance always dissolves before the round can end with two allied survivors, so the End screen and winner work.
A later Si_ModUI client hook ("set FriendlyIndex of team X to Y") would fix the cosmetic rows.

## Config

`UserData/cfg/Alliance/Si_Alliance_Config.json` (written with defaults on first start, unknown values sanitized):

| Key | Default | Meaning |
|---|---|---|
| `enabled` | true | Master switch for player requests (admin commands still work). |
| `min_minutes` | 5 | No request before this much game time. |
| `ai_policy` | `always` | Target team without a human commander: `always` accepts, `never` refuses, `vote` is reserved (treated as `never`). |
| `fps_vote` | `off` | Reserved (FPS ratification vote, not in v1). |
| `quorum` | 0.54 | Reserved for `fps_vote`. |
| `break_grace_s` | 30 | Seconds between `!ally break` and the end of the alliance. |
| `decline_cooldown_s` | 120 | Requesting team's wait after a decline or an unanswered request. |
| `lone_income_mult` | 1.0 | Harvest multiplier for the lone team (1 = off, max 5). |
| `bloc_balance` | true | Set the Si_EloMatchmaking bloc hook. |
| `one_alliance_per_round` | true | At most one alliance per round. |
| `allied_voice` | true | Relay allied team voice (see limitations). |
| `debug` | false | Extra log lines. |

## Logging

Every state change is logged with the tag `[ALLY]`, game time and player counts per team. Each finished alliance
appends a line to `UserData/cfg/Alliance/log/alliances.csv`: date, map, formation time, teams, lone team, players per
team at formation, who formed it, end time, reason (`lone_eliminated`, `ally_eliminated`, `round_end`, `break`,
`admin_clear`, `scene_load`), outcome, harvest per team during the alliance, lone bonus paid, dropped hit counters.

## Si_ModUI

`ModUIData()` exports: Info lines (status such as `Pending: Sol -> Centauri (42 s)`, `Sol + Centauri allied vs Alien`,
`... breaking in 12 s`, and the rules), a team picker for `!ally`, an accept/decline choice picker (`!ally respond`),
Break, Status, and admin entries Force (team, team), Clear, Lone income multiplier, Admin status.

## Build / deploy

`Si_Alliance/Si_Alliance.csproj` (netstandard2.1) references `include/<branch>/*.dll` (copied from
`Projects\_silica_refs`, git-ignored):

```
dotnet build -c Release                           # test branch  -> bin/Release/netstandard2.1/Si_Alliance.dll
dotnet build -c Release -p:SilicaBranch=default   # default      -> bin/Release/default/Si_Alliance.dll
dotnet C:\tmp\refcheck\bin\Release\net8.0\refcheck.dll "C:\Users\schwe\Projects\_silica_refs\default" "" Si_Alliance\bin\Release\default\Si_Alliance.dll
dotnet C:\tmp\refcheck\bin\Release\net8.0\refcheck.dll "C:\Users\schwe\Projects\_silica_refs\test" "" Si_Alliance\bin\Release\netstandard2.1\Si_Alliance.dll
```

Copy the DLL of the server's branch to `<server>\Mods\Si_Alliance.dll`. Every game member used exists in 0.9.47 and
1.0.x; `MP_Strategy.GetCanJoinTeam` (1.0.x only) and `MaximumPlayerDifference` are reached by reflection.
