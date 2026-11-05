# Dedicated server hosting

A Modstoke dedicated server can be driven by a hosting agent over its standard
input and output, which is what a Pelican/Wings console provides. The agent
writes command lines to stdin and reads one-line JSON replies and events from
stdout. Human log output is unchanged; machine lines start with `@modstoke `.

The desktop console pipe (`Modstoke/UserData/console-endpoint.json`) is a
Windows named pipe and is not reachable from outside Wine. Use stdin/stdout or
the optional TCP port described below.

All of this is active only on the dedicated server (`SonsOfTheForestDS.exe`).
On a game client nothing is written to stdout and no port is opened. Set
`MODSTOKE_HOST_STATUS=1` on a client to get the status and event files for
testing.

## Requests

```text
[#<id> ][confirm ]<command>[ <arguments>]
```

- `<id>` is 1 to 64 characters from `A-Z a-z 0-9 . _ : -`. A line that starts
  with `#<id>` always gets a machine reply carrying that id.
- Host commands, including those mods add through `ServerHosting.RegisterCommand`
  (named like `admin.kick`), also accept `--json` (reply with `"id":null`) and
  `--id <id>` anywhere in their arguments. Older mod console commands only reply
  in JSON when the line starts with `#<id>`.
- `--actor <label>` and `--actor-steam <steamid64>` name the panel user behind a
  request. The host asserts them; they are not authenticated. stdin and TCP
  carry the server owner's rights, and a mod may use the SteamID only to
  restrict a staff account, never to grant more.
- Lines longer than 2048 characters are rejected. Input is read as UTF-8.

```text
#r12 status
players --json --id r13
#r14 stop
```

## Replies and events

Every machine line is `@modstoke ` followed by one compact JSON object and
`\n`. Lines never exceed 1 MiB. Anything else on stdout is log output and
should be passed through to the operator as is.

```text
@modstoke {"id":"r12","ok":true,"command":"status","state":"ready",...}
@modstoke {"id":"r15","ok":false,"command":"save","error":"busy","detail":"A save is already running."}
@modstoke {"event":"join","ts":"2026-11-05T18:02:11.204Z","sessionId":"3f2b9c4e-8d1a-4c57-9e0b-2a6f1d7c5e90","name":"Player","steamId":"76561190000000001"}
```

- Property names are camelCase, SteamIDs are strings and timestamps are UTC in
  the form `yyyy-MM-ddTHH:mm:ss.fffZ`. A `sessionId` is a GUID string that
  stays the same for one connection.
- Mods and log text can put arbitrary characters on stdout, but never a line
  that starts with `@modstoke `, and events never carry `id`, `ok` or
  `command`. Only TCP replies come from an authenticated channel.
- A failed reply has `error` (a code) and `detail` (text for the operator).
- Error codes: `unknown-command`, `invalid-arguments`, `invalid-id`,
  `not-ready`, `unavailable`, `busy`, `failed`, `refused`, `timeout`,
  `too-large`, `unauthorized`.
- A request refused before it reaches the game thread (Loader not ready,
  queue full, bad id) gets a reply only if it carried `#<id>`. `save` and
  `stop` from the server console still get through when the queue is full.
- Replies are never dropped. If stdout cannot keep up, the oldest queued
  events are dropped first.

## Commands

| Command | Reply body |
| --- | --- |
| `status` | The host status object, the same as `host-status.json`. |
| `version` | `protocol`, `loader`, `game`, `runtime`. |
| `players` | `count`, `max` and `players` with name, steamId, ping, position, life state and movement flags. Mods can add fields. |
| `mods` | One row per mod: id, name, version, loader (`modstoke`, `redloader`, `bepinex`), status, load time, error, SHA-256 and client requirement. |
| `requirements generate [--name <text>]` | Writes `Modstoke/Runtime/server-requirements.json` and returns it. |
| `save` | `saved`, `at`, `directory`, `durationMs`. |
| `stop [--no-save]` | `saved`, `exitCode`, `saveDetail`. Sent before the game quits. |
| `config check` | Each known config file with its path, format, reload mode and whether it parses. |
| `reload` | `applied` and `restartRequired` config ids. |

`status` and `mods` without `#<id>` or `--json` print the usual human
summary. `players`, `save` and `stop` exist only on a dedicated server.
`save`, `stop` and `reload` are accepted from the server console, the TCP port
and the desktop console, not from the in-game console.

`help` lists every command, including those added by mods.

## Stopping the server

Set the Pelican egg's stop command to `stop`. The server saves the world,
replies, emits `stop`, and quits with:

| Exit code | Meaning |
| --- | --- |
| 0 | Saved, or nothing needed saving (`--no-save`, or no world loaded). |
| 2 | Stopped, but the save failed or timed out. |
| 3 | The stop watchdog had to end the process. It emits a last `stop` event with `"watchdog":true` first. |
| 70 | An unhandled exception while the process was already going down. |

The save times out after 30 s. A watchdog ends the process 50 s after `stop`
or 15 s after the game starts quitting, whichever is first, so a stop always
finishes inside Pelican's 60 s grace period.

## Files

All paths are under the game folder.

| Path | Contents |
| --- | --- |
| `Modstoke/Runtime/host-status.json` | Current state, versions, uptime, performance, players, memory, mod load results, last save, pending config changes, file integrity and TCP state. Rewritten atomically every 2 s and soon after any change, and at least every 5 s while the game thread is busy loading or saving. |
| `Modstoke/Runtime/events.ndjson` | The event stream, one JSON object per line without the prefix. Rotates once to `events.previous.ndjson` at 4 MiB. |
| `Modstoke/Runtime/crash-yyyyMMddTHHmmssZ.json` | Written when an unhandled exception takes the server down: the exception, the mod it came from if known, recent Unity errors and the last 200 log lines. The newest 10 are kept. |
| `Modstoke/Runtime/server-requirements.json` | The required-mods list built from the installed mods, rewritten each time a dedicated server becomes ready and by `requirements generate`. Admin enforces its own copy (`RequiredModsFile`); this one is for listings and the join flow. |

`state` in the status file is one of `starting`, `loading`, `ready`, `saving`,
`stopping` or `crashed`. The first time it reaches `ready` the server also
prints:

```text
@modstoke {"event":"ready","ts":"...","protocol":1,"loader":"...","game":"...","pid":1234,"players":0,"maxPlayers":8}
```

The status file stops being rewritten once the server quits, so treat the
server as running while `updatedAt` is less than 15 s old, and as stopped
after that. The host agent's own view of the process wins when it has one.
`pid` is the Windows process id inside Wine, not a Linux pid.

`pendingMods` lists the mod installs and removals the native installer
queued in `Modstoke/Runtime/pending/` while the server ran: `id`, `action`
(`install` or `remove`), `modId`, `version`, `archiveSha256`, `queuedUtc` and
`requestedBy`. The installer applies them before the next start; the Loader
only reports them.

## Events

| Event | Fields |
| --- | --- |
| `ready` | `protocol`, `loader`, `game`, `pid`, `players`, `maxPlayers` |
| `stopping` | `reason` |
| `stop` | `exitCode`, `saved` |
| `join`, `leave` | `sessionId`, `name`, `steamId` |
| `chat` | `channel` (`game` or `command`), `sessionId`, `name`, `steamId`, `text` |
| `save` | `ok`, `source` (`command`, `stop` or `auto`), `directory`, `durationMs`, `detail` |
| `mod-error` | `id`, `name`, `loader`, `error` |
| `integrity` | `status`, `mismatches` |
| `crash` | `file`, `exceptionType`, `message`, `mod` |

Mods publish their own events with `Modstoke.Sons.ServerHosting.Emit`. Their
type always contains a dot, for example `admin.kick`. Each type may send a burst
of 100 and then 20 per second; the rest are dropped and counted in the status
file's `events.rateLimited`, next to `events.dropped` for lines a slow reader
missed. An outcome that arrives after a command's reply is an event carrying
the request id as `correlationId`.

## Mod commands

A mod adds a host command with
`ServerHosting.RegisterCommand(name, description, request => ServerHostReply.Ok(data))`.
The handler runs on the game thread, must not block, and returns exactly one
reply: `ServerHostReply.Ok(data, message)` or `ServerHostReply.Fail(code, detail)`
with a protocol error code. The request carries `Id`, `Command`, `Arguments`
(host flags removed), `Origin` (`stdin`, `tcp`, `pipe`, `game` or `other`),
`Confirmed`, `ActorLabel` and `ActorSteamId`. Extra player fields for
`players` come from the `ServerHosting.PlayerFields` event.

## TCP

Set both variables in the container to open a local port as well:

| Variable | Value |
| --- | --- |
| `MODSTOKE_HOST_PORT` | Port 1-65535. The server listens on 127.0.0.1 only. |
| `MODSTOKE_HOST_TOKEN` | Shared secret, at least 16 characters. |

The first line a client sends must be the token, within 10 s. The server then
answers `@modstoke {"id":null,"ok":true,"command":"auth","protocol":1}`, or
`unauthorized` and closes. After that the protocol is the same as stdin, every
request gets a JSON reply on its own connection, and events go to all
connected clients. At most two clients can be connected; a new connection
replaces one that has not authenticated yet, and is closed when both are
authenticated. A client that stops reading for 10 s is disconnected. The token
is never logged, and the server removes it from its environment so child
processes do not inherit it.

## Wine notes

- Check once that ordinary log lines reach the container console. BepInEx
  writes the log to its console handle (`CONOUT$`), while machine lines are
  written to the original stdout handle.
- Keep `Logging.Console.StandardOutType` in `BepInEx/config/BepInEx.cfg` at
  `Auto` or `StandardOut`. `ConsoleOut` can close the original stdout and the
  agent then sees no machine lines.
- On a dedicated server, RedLoader and SonsSdk message boxes are written to
  the log instead of opening a dialog, so nothing waits on a display.
