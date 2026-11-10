# Research notes: Amazon Photos → Skylight Calendar auto-sync (2026-11-02)

## Skylight side (receiving photos)

- Every Skylight household/device has a fixed email address `<name>@ourskylight.com`.
  Emailing an attachment to it adds the photo. Works for Skylight Calendar as well as Frame.
  The address cannot be changed. (Skylight help: Settings article 45795554249371)
- Photo screensaver on the **Calendar** requires the **Plus plan**.
- Privacy setting "Who can send photos?" = "Senders who I approve" or "Anyone".
  First email from a new sender triggers an approval email to the account owner. Approve
  the automation's sender address once. (Help article 42966379395995)
- Email attachment limit: **25 MB per email**. Formats: JPEG, HEIC, PNG; MP4, MOV, GIF.
  No stated cap on attachments per email; prior art uses ~20 per email, ~24 MB total.
- No public API. Community-documented private API exists:
  base `https://app.ourskylight.com/api`, OAuth2 PKCE login as `client_id=skylight-mobile`,
  `GET /api/frames`, `POST /api/message_upload_urls` (presigned), `POST /api/messages/uploads`.
  Gist: dknowles2/b8eab833eb23eb388c3d78999a3565f8. Go client: sebrandon1/go-skylight
  (active Oct 2026). Ruby gem: jakewilkins/skylight-frame. MCP: chrischall/skylight-mcp.
  Private API could change without notice; **email is the officially supported path.**

## Amazon Photos side (getting new photos out)

- **No official API.** Amazon Drive API was retired; desktop app two-way "Sync" was
  discontinued Feb 29 2024 (existing sync setups grandfathered; cannot be re-enabled).
  Desktop app "Backup" is upload-only. Cloud→computer is manual download only.
- Unofficial Python library `amazon-photos` (trevorhobenshield/amazon_photos, 102★,
  last release 0.0.97 Jan 2024, last push Jul 2024). Auth = copy `session-id`, `ubid-main`,
  `at-main` cookies from a logged-in browser. Supports `query()` with `createdDate`/
  `modifiedDate` range filters and `download(node_ids)`.
  Known 2025–2026 issues: cookie key naming (`-` vs `_`), 429 rate limiting needs ~60s
  backoff, cookies expire and must be refreshed manually. Community fork with fixes:
  kpfitzgerald (PRs #27–#31), plus an MCP server kpfitzgerald/amazon-photos-mcp.
- Practical implication: the Amazon connector is the fragile part. Design for
  cookie refresh + loud failure alerts, and isolate it behind an interface so a
  different source (iCloud/Immich/local folder) could be swapped in.

## Amazon Drive v1, verified 2026-11-06

Probed live with curl against a us-east-1 account on amazon.com, then pinned by the recorded
fixtures under `tests/SkylightPhotoSync.Tests/Fixtures/Amazon/` and `AmazonEndpointsTests`.

- **Discovery.** `GET https://www.amazon.com/drive/v1/account/endpoint` returns `metadataUrl`
  (`https://cdws.us-east-1.amazonaws.com/drive/v1/`), `contentUrl`
  (`https://content-na.drive.amazonaws.com/`), `downloadServiceUrl`, `thumbnailServiceUrl`,
  `uploadServiceUrl`, `region` and `customerExists`.
- **Required query params.** Every metadata request needs `resourceVersion=V2&ContentType=JSON`.
  Without them `nodes/{id}/children` answers 404 and `search` answers 400. The Python library also
  sends `asset=ALL&tempLink=false`; not required. `search` additionally needs
  `searchContext=customer` (else 400 `Value null at 'searchContext' failed to satisfy constraint`).
- **Date filters.** `contentDate:[A TO B]` and `contentProperties.contentDate:[A TO B]` are rejected
  (`There is no fieldNumber for contentDate` / `Invalid fieldName`).
  `createdDate:[2026-09-01T00:00:00.000Z TO 2026-12-31T23:59:59.999Z]` works (ISO with `.000Z`),
  as do `timeYear:(2026) AND timeMonth:(10)`. Upload date is never earlier than capture date, so
  all mode filters server-side on `createdDate` and keeps the client-side capture-date window on
  `contentProperties.contentDate`.
- **Sorting.** `sort=['contentDate ASC']` is rejected; `sort=['contentProperties.contentDate ASC']`
  works on both children and search and gives true capture-date order. `['createdDate ASC']` also
  works. The value is a single-quoted JSON-ish list, URL-encoded.
- **Pagination.** Responses carry `count` (total) and `nextToken`; pass it back as `startToken`.
  `limit=200` is accepted. Verified across a 595-item album and on search; pages differ.
- **Albums.** `nodes?filters=kind:VISUAL_COLLECTION` returns `data[]` of `id`, `name`, `kind`,
  `createdDate`, `ownerId`, `collectionProperties.covers`, `isShared`, … Names can carry trailing
  spaces, so the client accepts a trimmed match and quotes names in its not-found error.
- **FILE node.** Top level: `id`, `name`, `kind: "FILE"`, `createdDate`, `modifiedDate`,
  `ownerId`, `parents[]`, `parentMap`, `isShared`, `status`. `contentProperties`: `contentDate`,
  `contentType` (`image/jpeg`), `extension` (`jpg`), `md5` (32 hex), `size`, `version`,
  `contentSignatures`, `image{width,height,orientation,make,model,dateTimeOriginal,…}`. Videos
  presumably carry `video{}`; not yet observed.
- **Download.** `GET {contentUrl}nodes/{id}/content` returns 200 with the original bytes and the
  right `Content-Type`. `{contentUrl}cdproxy/nodes/{id}/content` also works, and
  `{metadataUrl}nodes/{id}/contentRedirection?querySuffix=%3Fdownload%3Dtrue&ownerId=…` 302s to the
  content host with a `Content-Disposition` filename.
- **Auth failures.** Wrong `at-main`: 401 `{"message":"Get error during session authentication."}`
  on the account endpoint and metadata calls. No cookies: 401 `{"Message":"No Auth Method Provided"}`.
  Valid cookies without the `x-amzn-sessionid` header: 401, so the client always sends it.
  401/403 map to `AuthExpiredException`. Cookies that had just passed `login` were rejected again
  within about fifteen minutes on 2026-11-06, so `at-main` can rotate quickly; refresh with
  `skylight-sync login` and record right away.

## Device login (issue #30): verified live 2026-11-10

Implemented 2026-11-06 from the sources below and run against a live amazon.com account on
2026-11-10, where it worked unchanged. What the live pass confirmed:

- **Sign-in.** The URL from `BuildSignInUrl` was accepted as built, with the retail
  `marketPlaceId=ATVPDKIKX0DER`. After the two-factor prompt the browser landed on the
  `/ap/maplanding` "page not found" with `openid.oa2.authorization_code=` in the address, the
  full address was shown and the pasted code registered. No device-consent page appeared.
- **Register.** `POST https://api.amazon.com/auth/register` answered with
  `response.success.tokens.bearer.{refresh_token,access_token}` and
  `extensions.customer_info.name`; `login` printed `Amazon Photos: connected as <name>, 52067
  items` (the name is `customer_info.name`, since the Drive root node has none) and `Device
  registered as "Skylight Photo Sync"`. This step runs once per login with a single-use code and
  cannot be replayed to record, so `tests/SkylightPhotoSync.Tests/Fixtures/AmazonAuth/placeholder_auth_register.json`
  stays hand-written and labelled `_placeholder`, with its cookie entries aligned to the
  serialisation seen in the exchange.
- **Cookie exchange**, recorded by `skylight-sync record-fixtures --auth` as
  `tests/SkylightPhotoSync.Tests/Fixtures/AmazonAuth/exchange_cookies.json` (every value replaced by
  a placeholder of the same shape): the cookies are keyed `response.tokens.cookies[".amazon.com"]`,
  in the order `session-id`, `ubid-main`, `session-token`, `x-main`, `at-main`, `sess-at-main`;
  `Secure`/`HttpOnly` are JSON booleans, not strings; `x-main`, `at-main` **and** `sess-at-main`
  arrive wrapped in quotes, which the parser strips; `Expires` looks like
  `5 Nov 2046 13:09:03 GMT` (unpadded day; twenty years for the ids and `x-main`, one year for
  the tokens). The body also carries `response.tokens.ttl` (2592000, thirty days),
  `response.age_classification`, a top-level `secureSessionToken` and `request_id`, all ignored.
  The parser needed no change.
- **Drive v1 with minted cookies.** `run --dry-run --limit 3` printed
  `found 595, sent 3, skipped 24, batches 1 (dry run)`, exit 0, the same as the pasted-cookie pass
  of 2026-11-06; `status` shows `needs_login: no` and `auth: device login`. Repeated the same day
  from a worktree build with a second minted set.
- **Not exercised live:** `logout` (`/auth/token`, `/auth/deregister`) and a second `login` over
  an existing device login (the deregister-previous path). Their fixtures remain hand-written
  placeholders.

The documented shapes, as implemented:

- **Flow** (mkb79/Audible "external login", also Apollon77/alexa-cookie since v2 and
  airbone42/immich-amazon-sync PR #2): PKCE sign-in in the user's browser → authorization code on
  the `/ap/maplanding` landing page → `POST /auth/register` once → refresh token stored → per
  run `POST /ap/exchangetoken/cookies` for fresh website cookies. Never re-register: alexa-cookie
  issue #190 (June 2026) was a needless `/auth/register` with an expired access token.
- **Sign-in URL.** `https://www.amazon.{tld}/ap/signin` with `openid.oa2.response_type=code`,
  `openid.oa2.code_challenge_method=S256`, `openid.oa2.code_challenge=base64url(sha256(verifier))`
  (verifier = base64url of 32 random bytes), `openid.oa2.client_id=device:<hex(serial#A2CZJZGLK2JJVM)>`,
  `openid.oa2.scope=device_auth_access`, `openid.return_to=https://www.amazon.{tld}/ap/maplanding`,
  `openid.assoc_handle=amzn_audible_ios_{us|uk|de|fr|it|es|ca}`, `pageId=amzn_audible_ios`,
  `openid.identity`/`openid.claimed_id=http://specs.openid.net/auth/2.0/identifier_select`,
  `openid.mode=checkid_setup`, `openid.ns`, `openid.ns.oa2`, `openid.ns.pape`,
  `openid.pape.max_auth_age=0`, `accountStatusPolicy=P1`, `forceMobileLayout=true`,
  `marketPlaceId`: the retail marketplace (`ATVPDKIKX0DER` for `.com`, per issue #30),
  accepted live; the Audible app sends Audible's own (`AF2M0KC94RCEA` for the US), the value to
  try if another storefront refuses or loops.
  Serial: 40 random uppercase alphanumerics (alexa-cookie style; Audible uses 32 hex).
- **Landing page.** After 2FA the browser lands on an error page at
  `https://www.amazon.{tld}/ap/maplanding?...&openid.oa2.authorization_code=...`. The code is
  single-use and short-lived. Confirmed live: the browser showed the full address and the
  pasted code registered.
- **Register.** `POST https://api.amazon.{tld}/auth/register` (JSON): `requested_token_type:
  ["bearer","mac_dms","website_cookies"]`, `cookies: {website_cookies: [], domain: ".amazon.{tld}"}`,
  `registration_data: {domain: "Device", app_version: "3.56.2", device_serial, device_type:
  "A2CZJZGLK2JJVM", device_name: "%FIRST_NAME%%FIRST_NAME_POSSESSIVE_STRING%%DUPE_STRATEGY_1ST%Skylight Photo Sync",
  os_version: "15.0.0", software_version: "35602678", device_model: "iPhone", app_name: "Audible"}`,
  `auth_data: {client_id (hex, no "device:" prefix), authorization_code, code_verifier,
  code_algorithm: "SHA-256", client_domain: "DeviceLegacy"}`, `requested_extensions:
  ["device_info","customer_info"]`. Response: `response.success.tokens.bearer.{refresh_token
  (Atnr|…), access_token (Atna|…), expires_in ("3600")}`, `tokens.website_cookies[]`
  (`Name`/`Value`/`Expires`/`Path`/`Secure`/`HttpOnly`; `x-main`, `at-main` and `sess-at-main`
  arrive quoted, going by the exchange), `tokens.mac_dms` (unused),
  `extensions.customer_info.name` (used for "connected as", confirmed live). Errors: non-200
  with `response.error.{code,message}`. Host: `api.amazon.{tld}` (Audible; alexa-cookie always
  uses `api.amazon.com`, identical for `.com`), worked live.
- **Cookie exchange.** `POST https://www.amazon.{tld}/ap/exchangetoken/cookies` (form):
  `app_name=Audible`, `app_version=3.56.2`, `source_token=<refresh>`,
  `source_token_type=refresh_token`, `requested_token_type=auth_cookies`, `domain=.amazon.{tld}`;
  header `x-amzn-identity-auth-domain: api.amazon.{tld}`. Response:
  `response.tokens.cookies[".amazon.{tld}"][]` with `Name`, `Value`, `Expires`
  (`5 Nov 2046 13:09:03 GMT`), `Path`, `Secure`, `HttpOnly` (booleans), beside `tokens.ttl`
  (2592000): `session-id`, `ubid-main`, `session-token`, `x-main`, `at-main`, `sess-at-main`, in
  that order, the last three quoted. alexa-cookie reports them good for ~14 days and the `ttl`
  says thirty; the tool re-mints at the start of every run and once more on the first 401 of a
  run. 401/403, or a body without cookies, → `AuthExpiredException` → `needs_login`, one alert.
  Confirmed: the Drive v1 endpoints accept these cookies together with the `x-amzn-sessionid`
  header taken from the minted `session-id` (the pasted-cookie path needed it too).
- **Access token and deregister.** `POST https://api.amazon.{tld}/auth/token` (form:
  `app_name`, `app_version`, `source_token=<refresh>`, `source_token_type=refresh_token`,
  `requested_token_type=access_token`) → `{access_token, token_type: "bearer", expires_in}`.
  `POST https://api.amazon.{tld}/auth/deregister` with `Authorization: Bearer <access>` and body
  `{"deregister_all_existing_accounts": false}` removes only this device. Used by `logout` and when
  `login` fails after registration.
- **Lifetime and revocation.** The refresh token stays valid until the device is removed from
  the account (immich-amazon-sync README; alexa-cookie users report years). The device appears
  under Account > Manage Your Content and Devices > Devices and can be removed there.
- **2FA.** Email/SMS codes are reported flaky on this sign-in page; Amazon-app approval or an
  authenticator app works.
- Storefronts supported: `com`, `co.uk`, `de`, `fr`, `it`, `es`, `ca` (the ones immich-amazon-sync
  lists); the tool is pinned to `com`.

Sources: https://github.com/mkb79/Audible (`src/audible/login.py`, `register.py`),
https://audible.readthedocs.io/en/latest/auth/authorization.html,
https://github.com/Apollon77/alexa-cookie (README; issue #190),
https://github.com/adn77/alexa-cookie-cli, https://github.com/airbone42/immich-amazon-sync/pull/2.

## Prior art (same shape of problem)

- MChartier/immich-skylight-mailer (TypeScript, MIT): Immich album → JPEG via sharp →
  batched SMTP emails → Skylight. Tracks sent assets in `state/sent.json`. Cron-scheduled.
  Env: MAX_ATTACHMENTS_PER_EMAIL=20, MAX_EMAIL_TOTAL_BYTES=24000000, TARGET_WIDTH/HEIGHT,
  JPEG_QUALITY, STRIP_METADATA, DRY_RUN.
- jfroy/immich-skylight, abealsaleh/immich-skylight-sync: use the private API instead.

## Candidate architecture

Scheduled job (launchd on a Mac, or Docker on a NAS/mini server) that:
1. Loads Amazon cookies from a local secrets file; queries Amazon Photos for items with
   createdDate > last watermark (optionally restricted to one album).
2. Downloads new items; converts HEIC→JPEG, resizes to frame resolution, strips GPS EXIF.
3. Batches into emails ≤ ~20 attachments / ≤ 24 MB; sends via SMTP (Gmail app password)
   to `<household>@ourskylight.com`.
4. Records sent node IDs in a state file; advances watermark only after successful send.
5. Alerts (email/push) when Amazon auth fails so cookies can be refreshed.

Open decisions for Matt: which photos qualify (all new vs. a curated album), where it
runs, Plus plan status, videos yes/no, sender address to use.

## Sources
- https://skylight.zendesk.com/hc/en-us/articles/45795554249371-Settings
- https://skylight.zendesk.com/hc/en-us/articles/42966379395995-How-to-Approve-a-Sender
- https://skylight.zendesk.com/hc/en-us/articles/360024406912-What-file-types-does-Skylight-support
- https://skylight.zendesk.com/hc/en-us/articles/360022886612-How-many-photos-can-I-send-at-a-time
- https://myskylight.com/products/skylight-calendar
- https://gist.github.com/dknowles2/b8eab833eb23eb388c3d78999a3565f8
- https://github.com/sebrandon1/go-skylight
- https://github.com/MChartier/immich-skylight-mailer
- https://github.com/trevorhobenshield/amazon_photos (issues #24, #32)
- https://pypi.org/project/amazon-photos/
- https://www.amazonforum.com/s/question/0D5at00000Mn8zGCAR/syncing-from-cloud-to-pc
- https://uk.amazonforum.com/s/question/0D56Q0000Cwt8QzSQI/what-to-do-once-amazon-photos-sync-on-windows-is-gone
