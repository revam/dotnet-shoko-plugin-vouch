# Shoko Vouch Plugin

A [Shoko](https://shokoanime.com/) plugin that signs in a second device by
showing it a code.

A television, a console, or anything else awkward to type a password into
displays a short code. A phone that is already signed in scans it, is shown
what is asking, and confirms — and the first device receives its own API key.
If the phone refuses instead, the first device falls back to the ordinary
sign-in screen it would have shown anyway.

An already-signed-in device *vouches* for a new one. Hence the name.

## What it is not

**The code is not a credential.** A code on a screen is readable by anyone
who can see the screen, including a camera behind the viewer and a photograph
taken later. So the code carries a *request*: it names something waiting to be
approved, and it cannot be exchanged for anything.

Four properties follow from that, and they are the reason this exists as a
handshake rather than as a "show a key on screen" shortcut:

- **Authority comes from the approver, never from possession of the code.**
  Approving requires a signed-in Shoko session, and the key that is issued
  belongs to whoever approved. Holding the code gets you as far as the
  confirmation screen and no further.
- **The key reaches only the device that asked.** It is delivered over that
  device's own polling call, authenticated by a 256-bit device code that is
  never displayed and never part of the QR. It is never rendered on the
  approving phone, where a screenshot would outlive the flow.
- **A request is short-lived and single-use.** Five minutes to be answered,
  two more to be collected, and the key is handed over exactly once. A
  photographed screen stops being worth anything in minutes.
- **The confirmation names what is asking** — the device's own name, the kind
  it claims to be, the address it asked from, and the code itself, so it can
  be checked against the screen across the room.

**It is not OAuth.** The *interaction* is RFC 8628's, the OAuth 2.0 Device
Authorization Grant, and so is the state machine — device code, short user
code, verification URI, and polling until approval, denial or expiry. None of
the machinery is: there is no authorization server, no scopes, no grant types,
and what is issued at the end is an ordinary Shoko API key that appears in the
user's token list like any other. The borrowed part is the discipline around
polling and, above all, keeping *pending*, *denied* and *expired* apart — a
device with no keyboard can only tell the person in front of it which of those
happened if the server never conflates them.

**The QR code is not a protocol.** It encodes the verification URL with the
code already in it, and nothing else. Rendering it is the client's job; the
plugin hands back the URL. Always show the code as text beside it, for the
device with no camera pointed at it.

## The flow

```
  Device with no keyboard                Phone, already signed in
  ───────────────────────                ────────────────────────
  POST Request  ──────────────▶  a request is opened
       ◀── deviceCode (secret), userCode (public), verificationUri

  shows the code and a QR
  of the verification URL
                                 scans it, opens the approval page
                                 GET  Pending/{userCode}   [signed in]
                                      ◀── what is asking

  POST Poll  ── pending ──▶            confirms
  POST Poll  ── pending ──▶       POST Approve             [signed in]
                                       a key is minted for that user
  POST Poll  ── approved + apikey
       stores the key, done            "you can close this page now"
```

Refusal is the same shape with `Deny` in place of `Approve`, and the polling
device is told `denied` rather than being left to time out.

## Installation

### GUI (Recommended)

1. Open the Shoko Web UI and navigate to **Settings → Plugins → Repositories**.
2. Add the manifest URL:
   ```
   https://raw.githubusercontent.com/revam/dotnet-shoko-plugin-vouch/metadata/manifest.json
   ```
3. Go to **Settings → Plugins → Browse** and find **Vouch**.
4. Click **Install** on the desired version.
5. Restart Shoko.

### Manual

1. Download the latest `Shoko.Plugin.Vouch-<version>-any.zip` from the
   releases page.
2. Extract the ZIP and place `Shoko.Plugin.Vouch.dll` into your Shoko
   **Plugins** folder.
3. Restart Shoko.

## For client authors

The plugin id is `d88779b6-3551-4b07-a05e-595b6e167b13`. Declare an
**optional** dependency on it and probe `GET /api/plugin/Vouch/v1/Available`:
offer the "show me a code instead" path when it answers, and the ordinary
sign-in form when it does not — which is what you would have shown anyway.

### The device being paired

```jsonc
// POST /api/plugin/Vouch/v1/Request
{ "deviceName": "Living Room TV", "deviceType": "TV" }

// 200
{
  "deviceCode": "…64 hex chars…",   // keep in memory; never render this
  "userCode": "BCDF-GHJK",          // show this, and put it in the QR URL
  "verificationUri": "http://shoko.local:8111/plugin/Vouch/Approve",
  "verificationUriComplete": "http://shoko.local:8111/plugin/Vouch/Approve?code=BCDF-GHJK",
  "expiresAt": "…", "expiresIn": 300, "interval": 5
}
```

Then poll, no faster than `interval` seconds apart:

```jsonc
// POST /api/plugin/Vouch/v1/Poll
{ "deviceCode": "…" }

// 200 — one of
{ "status": "pending",   "interval": 5 }
{ "status": "approved",  "apikey": "…", "username": "alice", "deviceName": "Living Room TV" }
{ "status": "denied"    }
{ "status": "expired"   }
{ "status": "completed" }
// 404 { "status": "unknown" }
// 429 { "status": "pending", "slowDown": true, "interval": 10 }   + Retry-After
```

**Handle the four outcomes separately.** `approved` carries the key exactly
once — store it and stop polling. `denied` means someone said no: stop
polling and show your ordinary sign-in screen, because a refused pairing must
leave the user no worse off than never having tried. `expired` and `completed`
mean ask for a new code. `unknown` means the server has forgotten this
request entirely.

**Respect `slowDown`.** A 429 with `slowDown` means you polled early; the
`interval` in the body has grown, and it never shrinks. A terminal answer is
never withheld for polling too fast, so backing off costs you nothing.

### The device doing the vouching

All three require a signed-in Shoko session (`apikey` header).

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET`  | `Pending/{userCode}` | What is asking. Never returns a key or a device code. |
| `POST` | `Approve` | Mint a key for the signed-in user and hand it to the requesting device. Refused with `403` when the caller's own key was itself vouched. |
| `POST` | `Deny` | Refuse. |

`Approve` and `Deny` take `{ "userCode": "BCDF-GHJK" }`, and neither response
ever contains a key.

Note `deviceNameInUse` on the `Pending` response. It no longer means what it
once did: Shoko hands back an *existing* key only when that key does not
expire, and every key this plugin issues does, so approving can no longer
hand two devices the same key. What the flag means now is that this user
already has a vouched key under this device name, and a second one would be
indistinguishable from the first in the device list. Worth saying on your
confirmation screen; no longer a warning about shared credentials.

### The approval page

The plugin serves its own at `/plugin/Vouch/Approve` — one self-contained HTML
file, no external resources, so it works on a phone with no route to the
internet. It reads the session Shoko's own web UI stores on this origin, shows
what is asking, and offers a sign-in form when there is no session (that token
is held in memory and never stored). With no `?code=` it asks for the code to
be typed, for the device with no camera.

It is a fallback, not the intended surface: a front-end that wants this flow
inside its own design calls the API directly and never loads it.

## API Endpoints

Served under `/api/plugin/Vouch/v1/`:

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| `GET`  | `Available` | anonymous | Probe: version, verification path, code shape, timings. |
| `POST` | `Request` | anonymous | Open a pairing request. |
| `POST` | `Poll` | device code | Ask after your own request; collect the key. |
| `GET`  | `Pending/{userCode}` | signed in | What is asking. |
| `POST` | `Approve` | signed in | Approve, minting a key for yourself. |
| `POST` | `Deny` | signed in | Refuse. |

The full Swagger documentation is available at
`http[s]://<shoko host>/swagger/index.html?urls.primaryName=Vouch V1`.

## Timings

| What | Value |
|------|-------|
| A request stays answerable | 5 minutes |
| After approval, to collect the key | 2 minutes |
| Expired and refused requests are remembered | 10 minutes |
| Starting poll interval | 5 seconds |
| Added to the interval per early poll | 5 seconds, up to 60 |

None of these are configurable. They are security properties rather than
preferences, and the retention window in particular is what lets a device that
stopped polling learn *expired* instead of *unknown*.

## Rate Limits

| What | Limit | Scope |
|------|-------|-------|
| Opening a request | 20 per 15 minutes | Per IP address |
| Codes that matched nothing | 10 per 15 minutes | Per IP address |
| Polling early | `slowDown`, interval grows | Per request |

Exceeding a limit returns **429 Too Many Requests** with a `Retry-After`
header and a `retryAfter` field in the body.

## Configuration

| Option | Default | Description |
|--------|---------|-------------|
| `TrustProxy` | `false` | Read `X-Forwarded-*` when determining the client address and the host in the verification URL. Enable only behind a trusted reverse proxy — otherwise a caller chooses its own address and the host that ends up inside a QR code. Only the rightmost `X-Forwarded-For` entry is believed, and only if it parses as an address, so a caller cannot vary the chain to escape rate limiting. |
| `IssuedKeyLifetimeHours` | `4320` (six months) | How long an issued key lasts, in hours. Minimum 1. There is no never-expires setting: an issued key outliving the session that approved it is the point of the plugin, and a bound is what that costs. Administrators only — Shoko gates its whole configuration API on the `admin` role. |

### A vouched key says so, and cannot vouch

Every issued key carries its approver in its device name — `Living Room TV
(Vouched by revam)` — so a glance at the device list says which keys came from
pairing and who approved each one.

That marker is a control, not a label. `Approve` refuses a caller whose own
device name carries it, so a vouched key cannot mint another. The reason is
the lifetime: chaining would let a key approaching expiry vouch a fresh one
with a full term, and repeat, which makes the bound above unenforceable. The
device pairing is *for* — a shared screen, signed in unattended for months —
is also the worst candidate for minting credentials.

The marker is never the part that gets truncated when a name is too long.

## What it does not store

Nothing. Pairing requests live in memory and die with the process; a restart
drops them, and a device polling one is told the request is unknown and starts
again — the same thing it would have done had the request expired. Approved
keys that nobody collected are revoked by the sweep rather than left in the
user's token list.

## Building from Source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
dotnet restore
dotnet build --configuration Release
dotnet test --configuration Release
```

The compiled assembly will be located at
`source/bin/Release/net10.0/Shoko.Plugin.Vouch.dll`.

## License

This project is licensed under the MIT License.
