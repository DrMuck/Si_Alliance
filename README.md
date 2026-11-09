# Si_Alliance

Server-only MelonLoader mod for Silica: in a Humans vs Humans vs Aliens round, two commanders can form an alliance
against the third team. When the third ("lone") team is eliminated the alliance dissolves and the two allies finish
the round as a normal 1v1, so vanilla picks a real winner and shows the End screen. Since 0.3.0 other mods can force a
**locked** alliance for a whole round through `AllianceApi` (Si_VersusVote's 2v1 layout: Sol + Centauri vs Aliens);
when the Aliens are eliminated the allied humans win together (see Locked alliances).

How it works: vanilla already has `Team.FriendlyIndex`. Teams that share an index > -1 are not enemies for every
`GameMode.GetTeamsAreEnemy` / `GetTeamsAreFriendly` check (AI targeting, turrets, detection sharing, pings). The mod
gives both allies the same index while the alliance lasts and restores the vanilla value afterwards. Design, decisions
and cross-mod contracts: [docs/PLAN.md](docs/PLAN.md).

Requires the [Admin Mod](https://github.com/data-bomb/Silica) (commands, chat, damage event). Optional: Si_ModUI
(panel), Si_EloMatchmaking 0.7.0+ (bloc player balance).

## Commands

| Command | Who | Effect |
|---|---|---|
| `!ally <team>` | commander | Propose an alliance to that team (name prefix: `sol`, `cent`, `alien`); works from the round start (no minimum game time since 0.3.2). With `fps_vote` `both`/`lone` this opens a player vote (see Vote); with `off` the other commander has 60 s to answer. A team without a human commander (RTS AI) is handled by `ai_policy`. |
| `!ally yes` / `!ally no` | players of a voting team | Vote on the open alliance (`accept` = yes; `decline`, `deny`, `reject` = no; `!ally respond accept\|decline` likewise). Voting again overwrites your vote. |
| `!ally accept` / `!ally decline` | commander of the asked team (`fps_vote` `off`) | Answer the open request (`!ally respond accept\|decline` does the same, used by the ModUI picker). A decline blocks the requesting team for `decline_cooldown_s`. While a vote runs these are votes. |
| `!ally break` | commander of an ally | Announced to everyone; the alliance ends after `break_grace_s`. Once per team per round. A locked alliance answers "This alliance is locked (2v1)". |
| `!ally status` | anyone | Current alliance, open request or running vote with its tallies. |
| `!allyadmin force <teamA> <teamB> [lock]` (or `<teamA>+<teamB>`) | admin (Teams) | Ally two teams now, no vote; ignores `one_alliance_per_round` (still HvHvA only). Works from the layout choice on (countdown included). `lock` makes it a locked alliance. Refused while a locked alliance stands. |
| `!allyadmin clear [--force]` | admin | End the alliance, or remove the open request / cancel the running vote (announced). A locked alliance needs `--force` (also `force`, `-f`); the round then goes on as a normal 3-way. |
| `!allyadmin mult <x>` | admin | Set `lone_income_mult` (1..5, saved). |
| `!allyadmin status` | admin | Teams with FriendlyIndex, player count, commander, lost flag, dropped hit counters. |
| `!allyadmin reload` | admin | Re-read the config. |

## Vote

With `fps_vote` = `both` (default) a commander's `!ally <team>` opens a vote of `vote_s` seconds (45): everyone is
told "Sol proposes an alliance with Centauri against Alien. Players of Sol and Centauri: !ally yes | !ally no (45 s)".
The proposing commander's yes is counted automatically; the asked commander votes like any player. With `lone` only
the asked team votes and the proposing commander's word stands for his team. A voting team passes when it has more
yes than no votes among the votes cast (only the players who voted count, not the team's size; a tie or no vote at all
does not pass; votes of players who left the team are dropped). A team whose players (`Team.GetNumPlayers()`) have all
voted is final; when every voting team is final, or the time is up, the vote resolves: all voting teams pass -> the alliance
forms; otherwise "Alliance Sol+Centauri rejected: Sol 2 yes / 3 no (no majority), Centauri ..." and the proposing
team waits `decline_cooldown_s`. A team with no human players passes (team A: the proposer's word; team B: only under
`ai_policy` `always`). An asked team without a human commander: `ai_policy` `never` refuses at once, `always` lets its
players vote. `fps_vote` = `off` keeps the 0.1.x commander accept/decline.

The alliance ends automatically when the lone team is eliminated (final showdown), when either ally is eliminated,
at round end, on scene load, after `!ally break` + grace, or by `!allyadmin clear`.

Announcements: every state change is broadcast; while an alliance is active everyone gets a reminder every `remind_s`
and late joiners a private notice (`join_notice`). Since 0.3.1 everyone also gets a two-line intro `intro_s` (60) into
each 3-team round ("Alliances are on: two teams can ally against the third. A commander proposes one with !ally <team>."
plus the vote rule of the current `fps_vote`), built from the live config; not when alliances are off, the round already has an alliance (a locked 2v1 has its own line), the mod was
loaded mid-round, or no player is connected.

## Mod API (`Si_Alliance.AllianceApi`, ApiVersion 1)

For other server mods, by reflection (type `Si_Alliance.AllianceApi` in assembly `Si_Alliance`). Main thread, server
only; nothing throws, failures return false and are logged (`[ALLY] API ... refused: <why>`).

```csharp
public static class AllianceApi {
  public const int ApiVersion = 1;
  public static bool ForceAlliance(string teamA, string teamB, bool locked, string reason);
  public static bool ClearForced(string reason);
  public static bool IsActive { get; }
  public static bool IsLocked { get; }
  public static string Describe();          // "Sol+Centauri vs Alien (locked, 2v1 IndustrialQuarter)"
  public static string LastRoundWinners { get; }   // "Sol+Centauri" after a humans_win end until the next restart, else ""
}
```

- `ForceAlliance` = `!allyadmin force A B` without the chat: forms the alliance at once, bypassing `enabled`,
  `one_alliance_per_round` and the vote / accept flow; an open request or vote is cancelled. Team names:
  `TeamShortName` or `TeamName`, case-insensitive, an unambiguous prefix also works (`Sol`, `Centauri`, `Alien`). Works
  from `MP_Strategy.SetTeamVersusMode(HUMANS_VS_HUMANS_VS_ALIENS)` on, i.e. during MissionState INIT with no players,
  units or commanders (it only needs the active team setups of the layout and `Team.FriendlyIndex`). An alliance formed
  before the start survives `OnGameStarted` (announced again at the start; players who join later get the join notice).
  Returns false when: not initialised (Admin Mod missing) / not the server, no Strategy mode, the round is over, the
  layout is not HvHvA (3 active teams), a team is not found / both names are the same team / a team is already
  eliminated, or a different pair is allied. The same pair already allied returns true (and `locked` locks it).
- `ClearForced` dissolves an alliance formed by the API or `!allyadmin force` (locked or not; CSV reason `api_clear`),
  false when there is none or the active one was formed by players.
- `IsActive`: any alliance (also while breaking). `IsLocked`: the active alliance is locked.
- Reset: a locked alliance (like every alliance) ends at round end, scene load and `MP_Strategy.Restart` (prefix: the
  next-map vote picking the same map restarts without a scene load).

## Locked alliances (2v1)

`ForceAlliance(.., locked: true, ..)` or `!allyadmin force A B lock`:

- players cannot break it (`!ally break` -> "This alliance is locked (2v1)", no Break entry in the ModUI panel);
  admins need `!allyadmin clear --force`; `!allyadmin force` of another pair is refused while it stands;
- allied damage stays zeroed, allied voice as for any alliance, reminders and join notices say "This round is 2v1: ...";
- **lone team eliminated**, `locked_end` = `humans_win` (default): the round ends at once and the allies win together.
  Vanilla only ends a round when at most one team setup is not lost (`MP_Strategy.GetShouldEndGame`) and only knows one
  winner (`GetWinningTeam`), so with two allied survivors it would never end. The mod broadcasts "Sol and Centauri win
  together (2v1)! Alien is eliminated.", sets the reported winner to the ally with more standing structures (tie: more
  players, tie: the first team), and calls `MP_Strategy.EndRound()` (as `!endround` does) -> `OnMissionStateChanged(ENDED)`
  -> `GetWinningTeam()` (postfix: replaces vanilla's null with that ally, server only) -> `GameEvents.OnGameEnded(winner)`
  (Elo, loggers) -> the alliance is dissolved with CSV reason `humans_win`. `AllianceApi.LastRoundWinners` = `Sol+Centauri`
  until the next restart. `locked_end` = `dissolve`: the 0.2 behaviour, final 1v1 between the former allies;
- **one ally eliminated**: the alliance dissolves (`ally_eliminated`) and the round goes on as the survivor vs the lone
  team, ended by vanilla as usual.

**Client view of a humans_win end:** clients compute `GetWinningTeam` themselves from their own team state and see two
survivors, so they get no End screen (the same as after an admin `!endround`): the death camera runs for the restart
delay (30 s), then the next-map vote. The chat line names the winners. A real End screen would need one ally to
actually lose (all critical structures destroyed) or a client mod.

**Si_EloMatchmaking:** bloc balance works from the moment the alliance is formed, also at INIT: Elo reads
`AllianceHook.GroupOf` live for every join, redirect, `!reserve` and ask (no caching), so players joining during the
countdown or later are balanced as Sol+Centauri bloc vs Alien (the bloc counts are compared, not Sol vs Centauri inside
the bloc; with 2 blocs Elo uses its 2-team MaxDifference formula). Rating: Elo rates the reported winner only; it
pairs the winner against every other commanded team, so after a humans_win end the second ally's commander and
players are rated as losers against the first ally. Elo needs a change to read `AllianceApi.LastRoundWinners` (or
`GroupOf`, which may already be null: Si_Alliance dissolves in its own OnGameEnded handler) and skip the ally pair.

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

An unlocked alliance always dissolves before the round can end with two allied survivors, so the End screen and
winner work. A locked one with `locked_end` `humans_win` ends the round itself (see Locked alliances: no client End
screen).
A later Si_ModUI client hook ("set FriendlyIndex of team X to Y") would fix the cosmetic rows.

## Config

`UserData/cfg/Alliance/Si_Alliance_Config.json` (written with defaults on first start, unknown values sanitized):

| Key | Default | Meaning |
|---|---|---|
| `enabled` | true | Master switch for player requests (admin commands still work). |
| `ai_policy` | `always` | Target team without a human commander: `always` accepts (`fps_vote` off) or lets its players vote (no players = passes), `never` refuses, `vote` is reserved (treated as `never`). |
| `fps_vote` | `both` | `both`: players of both teams vote; `lone`: only the asked team votes; `off`: the asked commander answers alone (60 s). A 0.1.x config (it has `quorum`) with `off` is switched to `both` once on load. |
| `vote_s` | 45 | Vote duration in seconds (10..180). |
| `quorum` | - | Removed in 0.2.0 (ignored in old files, dropped on the next save). |
| `break_grace_s` | 30 | Seconds between `!ally break` and the end of the alliance. |
| `decline_cooldown_s` | 120 | Requesting team's wait after a decline or an unanswered request. |
| `lone_income_mult` | 1.0 | Harvest multiplier for the lone team (1 = off, max 5). |
| `bloc_balance` | true | Set the Si_EloMatchmaking bloc hook. |
| `one_alliance_per_round` | true | At most one alliance per round. |
| `allied_voice` | true | Relay allied team voice (see limitations). |
| `remind_s` | 180 | Seconds between "X + Y are allied against Z" reminders to everyone while an alliance is active (0 = off, min 30). |
| `join_notice` | true | A player who joins (or was absent at formation) gets a private notice about the alliance ~10 s after appearing. |
| `intro_s` | 60 | Seconds of game time after the round start at which everyone gets a two-line `!ally` / vote explanation (0 = off, max 3600). |
| `locked_end` | `humans_win` | Locked alliance, lone team eliminated: `humans_win` = the round ends, the allies win together (stronger ally reported as winner); `dissolve` = final 1v1 like an unlocked alliance. |
| `debug` | false | Extra log lines. |

## Logging

Every state change is logged with the tag `[ALLY]`, game time and player counts per team. Each finished alliance
appends a line to `UserData/cfg/Alliance/log/alliances.csv`: date, map, formation time, teams, lone team, players per
team at formation, who formed it (for a vote: `vote passed: Sol 3 yes / 1 no, Centauri 2 yes / 0 no, proposed by X`; API:
`api locked: 2v1 IndustrialQuarter`; admin: `admin Name (locked)`), end time, reason (`lone_eliminated`, `ally_eliminated`,
`round_end`, `humans_win`, `break`, `admin_clear`, `api_clear`, `scene_load`, `restart`), outcome (humans_win:
`Sol+Centauri win together (2v1), reported winner Sol (structures Sol 14 / Centauri 9, players 6 / 5)`), harvest per
team during the alliance, lone bonus paid, dropped hit counters. A pre-start alliance's formation time and player counts
are taken again at the round start.

## Si_ModUI

`ModUIData()` exports: Info lines (status such as `Vote: Sol proposes an alliance with Centauri. Sol 3 yes / 1 no,
Centauri 2 yes / 2 no, ~20 s left`, `Sol + Centauri allied vs Alien`, `... breaking in 12 s`, and the rules), a team
picker for `!ally`, while a vote runs the actions `!ally yes` / `!ally no` (with `fps_vote` off: an accept/decline
choice picker `!ally respond`), Break, Status, and admin entries Force (team, team), Clear, Lone income multiplier, Admin status.

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
