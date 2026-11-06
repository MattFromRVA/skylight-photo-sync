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
