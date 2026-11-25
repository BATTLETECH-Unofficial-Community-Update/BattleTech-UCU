# Unlock server: design

Status: built 2026-11-13 and tested against a local Worker (Testing plan, below); deployed to Cloudflare the same day
(codes only, until the Steam route is deployed). Players who own BATTLETECH on Steam unlock through Steam with no code
(*Steam route*); GOG installs unlock on their files with no code (*GOG route*, 2026-11-15); the maintainer's own access codes
are the manual route. A package needs an access code when the Release Manager's two unlock fields are set;
with them empty it packs an unlocked package. Since 2026-11-25 the packer writes vault v4 (*Vault v4*); v2 and v3 packages
still install. Setup and use: `../keyserver/README.md`.

## Goal

A release installs only after a server we run agrees. On top of today's retail check, the installer needs a
per-release secret that only the server hands out, and only to a valid, unrevoked access code. This gives:

- **Per-person access:** each player has their own code; revoking one affects no one else.
- **Off switch:** turning the server off stops every new install and update of every v3 release.
- **A record:** which code unlocked which release, and when.
- **Leaked packages are useless alone:** the package plus BATTLETECH isn't enough without a code.

It does not protect an installed game (files sit decrypted on disk), and it can't prove an installer is genuine:
the server checks the person (their code), never the program.

## Keys

Vault v2: `package key` = the reused content key. The vault holds one slot per accepted retail fingerprint, each sealing
the package key under that fingerprint. The manifest is sealed under the package key.

v3 adds a **release share**, and v4 **key separation**:

```
release id   = 16 random bytes, made at pack time, stored in the vault in clear
share        = HMAC-SHA256(master secret, "btucu-share|" + release id)
slot secret  = HKDF(fingerprint || share, salt = slot salt, info = "slot4")       v3: "slot3"
manifest key = HKDF(package key, salt = share, info = "manifest4")                v3: "manifest3"
file key     = HKDF(package key, salt = SHA-256 of the blob's plain bytes, info = "file4")        v4
blob IV      = first 16 bytes of the same with info = "blob4-iv"                                  v4
blob name    = "data/v4-" + 32 hex digits of the same with info = "blob4-name" + ".pak"           v4
```

A blob's plain bytes are the whole file, or the residual of a file rebuilt from retail ranges.

- **One master secret, no per-release upload.** The Worker and the Release Manager both hold the master secret;
  each release's share is computed from it, so packing a release needs no server step.
- **Updates still download only what changed.** The content key and the blob derivation are the same from one v4 release
  to the next, so an unchanged file keeps its blob; and the installer skips a file its record shows as current, whatever
  its blob is called. An install from a v3 release updates to v4 by downloading the changed files only.
- **A blob's key has two inputs:** the content key and the hash of the blob's plain bytes. The hashes are in the sealed
  manifest, which a locked release seals under its own share. Bytes that someone has, can guess or can look up are no
  secret to them.
- **A release's own share, key and manifest open that release for good.** A retired release is one the server no longer
  hands the share out for; retiring recalls nothing already handed out.
- **The hashes are key material.** The SHA-256 of a packaged file or residual never goes in anything public: manifests use
  SHA-512 (`../src/PublicManifest.cs`). The install record in the player's own folder holds them and is never collected.
- **A new content key** (`ReleasePack --new-key` with `--previous`) is possible at any release. It shares no blob with the
  releases before it, so packing seals everything anew; players still download the changed files only.
- **The master secret** lives at `<workspace>\ReleaseManager\keys\master-secret.txt` (beside the content key,
  backed up the same way, never in the repository or a package) and as a Worker secret. If it leaks, make a new one:
  later releases use it, and earlier releases stay on the old one, or are retired.

## Vault v3

```
"BTU6GAT3"
release id (16 bytes)
unlock URL (length-prefixed UTF-8)     where to ask; the installer also has a compiled-in default
slots     (as v2, sealed with the v3 slot secret)
manifest  (as v2, sealed with the v3 manifest key)
```

The new magic makes older installers hand off to the release's own installer (the existing
NeedsNewerInstaller path), so an old installer never misreads a v3 vault.

## Vault v4

```
"BTU6GAR4"
locked (1 byte)                        1 with the unlock server; 0 without, and then no id or URL
release id, unlock URL                 as v3
slots     (as v3, sealed under v4's own labels; unlocked: the package key under the fingerprint)
manifest  (as v3, with an 8th column: the SHA-256 of a ranged entry's residual, empty for other entries)
store sections (as v3)
```

One magic covers packages with and without retail ranges. Every label is v4's own, so a vault with its magic changed to
another version's opens nothing. Online, an older installer hands off to the release's own, as for v3. Offline, a v4
package needs its own installer.

The server is unchanged: it answers for a release id, and never sees a vault.

## Server (Cloudflare Worker + D1)

`tools/release-gate/keyserver/`: `worker.js`, `wrangler.toml`, `schema.sql`, admin scripts, tests. No secrets in it.

**Endpoint:** `POST /v1/unlock` with JSON `{ "release": "<hex id>", "code": "<access code>", "installer": "<version>" }`.

| Result | Status | Body |
|---|---|---|
| unlocked | 200 | `{ "share": "<hex>" }` |
| unknown or revoked code | 403 | `{ "error": "code" }` |
| GOG proof matches no accepted fingerprint | 403 | `{ "error": "gog" }` |
| GOG route off, or no `GOG_FINGERPRINTS` | 503 | `{ "error": "gog-off", "message": "..." }` |
| release retired | 410 | `{ "error": "release" }` |
| server switched off | 503 | `{ "error": "closed", "message": "..." }` |
| too many tries | 429 | `{ "error": "slow-down" }` |

**D1 tables:**

- `codes(hash, name, created, revoked)`. Holds a SHA-256 of each code, never the code itself, plus the name you gave it.
- `retired(release)`: releases the server refuses.
- `unlocks(code_hash, release, at, ok)`: the record. **No IP addresses are stored.**

**Settings:**

- Worker secret `MASTER_SECRET`.
- Variable `OPEN` (`true`/`false`), the off switch, with an optional `CLOSED_MESSAGE` shown to players.

**Limits:**

- Per code: 30 unlocks a day.
- Per IP: Cloudflare's rate-limiting binding, held in memory, not stored: 10 requests a minute (the binding counts
  over 10 or 60 seconds only).

Free tier (100k requests a day, D1 100k writes a day) is far above this use.

**Admin:** `keyserver/admin.ps1` runs `wrangler d1 execute` for:

- `new-code <name>`: prints the code once, and appends name plus code to `keys\access-codes.tsv` (offline; the D1 table has hashes only);
- `revoke <name>`
- `retire <release>`
- `log [<name>]`
- `close [<message>]` / `open`

**Codes:** 20 random base32 characters, shown as `XXXXX-XXXXX-XXXXX-XXXXX`. Dashes, spaces and letter case are ignored.

## Installer

- **When the vault is v3**, the window asks for the access code under the retail folder. It's remembered in
  `%APPDATA%\BattleTechUCU\access.txt` (that user only).
- **The unlock happens at Check**, so a bad code is reported before anything is written.
- **Headless:** `--code <code>`, or `--code-file <file>`.
- **Testing:** `--share-file <file>` unlocks with a share directly, with no server. The Release Manager uses it for its own test installs.
- **Messages:**
  - "This access code isn't valid or was revoked."
  - "This release has been withdrawn."
  - The server's closed message.
  - "Couldn't reach the unlock server (…). Check your internet connection and try again."
- **The share is never written to disk.** Every install or update asks again: one request each.
- **A package zip, a loose folder and `--remote`** all read the same vault, so all three get this.

## Release Manager

- **New setting:** the master secret file, with a "Create" button if it's missing, in the same way as the content key.
- **Packing:**
  - **Locked by default.** With both unlock fields empty it packs an unlocked package, which anyone with BATTLETECH installs.
  - **The log** shows the release id.
  - **The test install** unlocks with `--share-file`, written to a temporary file and deleted afterwards.
- **Push:** before pushing a v3 release, check that the server answers `/v1/unlock` for that release id with a
  test code (`keys\test-code.txt`, a code named `release-manager-test`). Refuse to push if it doesn't.

## Testing plan

1. **Worker:**
   - Run it with `wrangler dev` (local D1).
   - Test every status code, the limits, and revoke, retire and close.
2. **Installer against the local Worker:**
   - Install a synthetic v3 package.
   - Check that a bad code, a revoked code and a retired release are refused before anything is written.
   - Update one v3 release to the next, and check that only the changed blobs download.
3. **Older installers:** a v2 installer starting a v3 release hands off to the release's installer.
4. **Full package:** repack full-10 as v3 to a test folder and do a scripted install against the local Worker.
   The result must be file-equal to the v2 install.
5. **Live:** once the maintainer has deployed the Worker, one install from the test code against the live address.

## The maintainer's part

- Create the Cloudflare account.
- Install Node.js and `wrangler`, then `wrangler login`.
- Create the D1 database (`wrangler d1 create btucu-unlock`). Paste its id into `wrangler.toml`.
- Set the master secret: `wrangler secret put MASTER_SECRET`, pasting `keys\master-secret.txt`.
- Deploy: `wrangler deploy`. Note the `*.workers.dev` address; it becomes the installer's default unlock URL.
- Issue codes with `admin.ps1 new-code <name>` and send each one to its player.

Agents write and test the Worker locally. They don't create the account, log in to Cloudflare or handle the master secret.

## Decisions

1. **Codes:** our own (the maintainer, 2026-11-13).
2. **When a package needs a code:** when the master secret file and the unlock URL are both set in the Release Manager;
   both empty packs v2.
3. **Server down:** an existing install still starts the game. The server gates installing and updating only, never play.
4. **Steam ownership is the normal route; codes are the exception** (the maintainer, 2026-11-13): GitHub releases are public, and
   players can't be asked to make their Steam profiles public. See *Steam route*.
5. **Family Sharing borrowers:** allowed by default; `admin.ps1 family deny` refuses them.
6. **GOG installs unlock on their files** (the maintainer, 2026-11-15: "Let's do 4 (GOG files alone)"; SCOPE.md extension 4,
   b783935a5). GOG offers no ownership check a server outside GOG can use without HBS/Paradox's GOG secrets, so a copy
   of GOG's files is enough. See *GOG route*.

## Steam route (2026-11-13)

**How the installer gets a ticket:**
- With no access code entered, it loads `BattleTech_Data\Plugins\steam_api64.dll` from the player's own BATTLETECH
  folder and starts Steam's API as app 637090.
- It asks the running Steam client for a Web API ticket bound to the identity `btucu-unlock`. Retail's DLL (about 2018)
  lacks that call, so it goes through the client's `SteamUser023` interface, vtable slot 14. A client without that
  interface gets the older session ticket instead.
- Ticket bytes arrive as callback 168. They are read with `Steam_BGetCallback` from steamclient64.dll.
- The ticket is valid only while Steam's API is up, so the installer keeps it up until the server has answered, then
  cancels the ticket and shuts the API down.

**How the server confirms it:**
- The Worker calls `api.steampowered.com/ISteamUserAuth/AuthenticateUserTicket/v1` with the regular Web API key
  `STEAM_WEBAPI_KEY`, `appid=637090` and the identity. It accepts only `result: "OK"`.
- The account's hash goes in the `steam` table. A difference between `steamid` and `ownersteamid` marks a borrowed copy.

**Why it shows ownership:**
- `SteamAPI_Init` fails for an account without a licence: tested with an unowned app, 2050650.
- Valve binds each ticket to its app and its identity: a ticket checked against another app gives error 102, and the
  wrong identity gives error 105.
- Valve's documentation says this call needs a publisher key and calls Web API tickets identity-only. Tested on
  2026-11-13, a regular key works on api.steampowered.com, and no profile is read. Evidence: gbrain
  `lessons/steam-ticket-ownership-without-publisher-key`.

**Limits:**
- Someone who controls their own Steam client could still subvert the client-side licence check. A ticket Valve
  confirms is still tied to a real account, which can be rate-limited and blocked.
- The licence check that is fully server-side is `CheckAppOwnership`. It needs Paradox's publisher key.

**Release Manager:** before a GitHub upload, it sends the live server a dummy ticket. The dummy must reach Valve and be
refused there, which shows the route is on and the key works.

## GOG route (2026-11-15)

**What the installer sends:** for a GOG install (`goggame-1482783682.info` in the folder) with no code entered, route
`gog`: `{ "release": "<id>", "gog": "<64 hex>", "installer": "<version>" }`, where `gog` is HMAC-SHA256 keyed with the
install's fingerprint (`Gate.Fingerprint`, the same one the vault's slots use) over `btucu-gog|` + the release id. The
fingerprint never leaves the PC, and a proof opens one release only.

**How the server checks it:** the Worker secret `GOG_FINGERPRINTS` lists the accepted fingerprints (hex, comma or space
separated; the value is `ReleasePack --fingerprint <GOG install>`). It recomputes the HMAC for every fingerprint and
compares each in constant time. Then: `open`, the `gog` off switch (`admin.ps1 gog-off [<message>]`, `gog-on`; also off
while the secret is unset), retired releases. Each attempt is recorded under the key `gog`; `admin.ps1 gog` counts them.

**Why the secret is private:** a fingerprint opens its slot in every package that accepts that GOG build, so it is kept
like the master secret: only the Worker and the packer's own reads of the GOG install know it.

**Limits:**
- Anyone with a copy of GOG's files gets in. That is the bound the maintainer accepted.
- There's no per-user identity, so no per-user daily cap; Cloudflare's per-IP limit still applies, and `gog-off` stops
  the route without touching Steam or codes.
- A different GOG build has a different fingerprint: the installer refuses it before asking (the package has no slot
  for it), and adding it means a repack plus its fingerprint in the secret.

**Release Manager and ReleasePublish:** for a package with a GOG section (`ucu-manifest-gog.tsv`), `CheckServer` also
checks the GOG route. With a GOG install on that PC, it sends a real proof and expects the release's share, which shows
the secret holds that fingerprint. Without one, it sends a dummy proof and expects 403 `gog`, which shows only that the
route is on. A GitHub upload is refused on a problem.

## Test record (2026-11-13)

All of these ran against `wrangler dev --local` (wrangler 4.161.0), from a copy of `keyserver/` outside `tools/`. Inside
`tools/`, wrangler's runtime can't create its folders on this workstation.

**Worker:**
- A good code got a share equal to the one computed locally from the master secret.
- An unknown code and a revoked code got 403; a malformed request 400; GET 405.

**Synthetic v3 package** (five files), packed with ReleasePack, installed headless:

| Case | Result |
|---|---|
| Short code | refused before the server was asked |
| Revoked code | refused |
| Good code | installed, file-equal to the player |
| Server unreachable | refused with the "couldn't reach" message |
| Wrong retail install | refused with no request to the server; its log unchanged |
| Retired release | "withdrawn" |
| Closed | the close message |
| `--share-file` | installed with no server |
| Wrong share | "doesn't fit this release" |

Nothing was written for any refused case.

**v2 compatibility:**
- The same player packed v2 installed as before.
- Its blob names equal the v3 package's, so updates still download only what changed.

**Release Manager `CheckServer`:**
- refuses with no test code, with a different master secret, and with no master secret file for a v3 package;
- passes for a good server;
- skips a v2 package.

**Installer window**, driven by UI Automation:
- The access code row shows for a v3 package.
- A revoked code shows the refusal.
- A good code shows "Access code accepted", enables Install, and is remembered.

**Steam route, against `wrangler dev` with `STEAM_APP_ID=480` (Spacewar), a real Steam ticket and Valve's live API:**
- An install with no code succeeded (1 account recorded, not borrowed).
- A blocked account was refused.
- `steam-off` showed its message.
- A forged ticket got 403 "Invalid parameter", and a malformed one got 400.
- The code route still installed.
- A BATTLETECH folder without steam_api64.dll gave a clear message.
- The window showed "Steam confirmed that you own BATTLETECH."
- `SteamRouteProblem` reported on, off and unreachable correctly.
- One leftover: Steam prints `pipes.cpp (733) … m_QueueCallbackMsg` at API shutdown. It appears only on the headless
  console and has no effect.

**Not run yet:**
- The deployed Worker on Cloudflare, including its rate limiter.
- The Steam route as app 637090. The lease was held; the first real unlock covers it.
- A Family Sharing borrower.
- A full-size repack. Agents don't cut packages, so the maintainer's first v3 pack and its test install are the full-size test.

## Test record: GOG route (2026-11-15)

`test/gog-unlock.ps1` against `wrangler dev --local` (wrangler 4.161.0) from a copy of `keyserver/` in a scratch folder,
with `GOG_FINGERPRINTS` holding a random fingerprint plus the GOG 1.9.1 install's, and a v3 package packed with
`--gog`. All 13 checks passed:
- A GOG install with no code installed, with the Galaxy natives; a Steam install with a code installed without them.
- `gog-off` refused with its message, and a code still installed. A retired release got "withdrawn", and `close` its
  message.
- `GogRouteProblem`: a real proof passed and its share matched the master secret's; a dummy proof passed; an unknown
  fingerprint named `GOG_FINGERPRINTS`; the route switched off said so.
- `admin.ps1 log` and `admin.ps1 gog` listed the GOG attempts.
- A dummy proof by hand got 403 `gog`, and a malformed one 400.

Not run: the deployed Worker, which needs the maintainer's `GOG_FINGERPRINTS` secret and deploy (`../keyserver/README.md`).

## Test record: vault v4 (2026-11-25)

Synthetic packages with throwaway keys; `wrangler dev --local` on the unchanged `worker.js`. `test/vault4.ps1` (packs,
installs, updates v3 to v4 and v4 to v4, damage, stopped and cancelled installs, older installers), `test/gog-unlock.ps1`,
`test/previous.ps1` and `test/ranges.ps1` pass. The code and GOG routes unlocked v3 and v4 packages from the same server;
the Steam route was run with Valve's answer stubbed. Not run: the deployed Worker, and a real Steam ticket.

## Developer tokens (2026-11-20)

The maintainer, 2026-11-20: a handful of developers run release players with no store, and "our word in the token are sufficient".
`POST /v1/dev-token {"code"}` answers a code with a `developers` row (`admin.ps1 dev-grant`) with a token signed by the
`DEV_SIGNING_KEY` Worker secret (RSA 2048 JWK, RSASSA-PKCS1-v1_5 SHA-256). The share's HMAC can't serve: the player would
need the master secret to check it. The player carries only the public key and checks the token offline, so a run needs
no network. Expiry (1-90 days per grant) is the only revocation. Use and commands: `../keyserver/README.md`.

Tested on `wrangler dev --local` (2026-11-20, scratch copy): a granted code got a token that checked `VALID`; a token
with an edited payload and a token checked against another key read `BAD-SIGNATURE`; a token past `exp` read `EXPIRED`;
an ungranted, an unknown and a revoked code got 403 `code`; `/v1/unlock` still answered a code; `log` and `devs` listed
the fetches. Not run: the server with no `DEV_SIGNING_KEY` (503 `dev-off`), and the deployed Worker.
