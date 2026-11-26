# Spike: Family Vault and reading more than one Amazon account (issue #14)

Date: 2026-11-25. Status: **research only, nothing built.** No live request was made for this
spike: every statement about the private Family Vault endpoints comes from reading other people's
code and is marked **(unverified)**. The decision and the live checks are Matt's (see the end).

## Question

The tool reads exactly one Amazon account, the one signed in with `skylight-sync login` (spec §4).
Photos that live only in another family member's account never reach the frame. Can the tool see
them, either through the Family Vault with the one login it already has, or through a second
device login for the other person's account? Or is neither worth doing?

## Short answer

- **Family Vault exists and members can add to it, but there is no documented way to read it.**
  Amazon's help pages say a vault has up to six members including the Prime owner, each member
  adds photos to it on purpose (or turns on an auto-add setting), and a person can be in only one
  vault. Nobody in the community libraries reads vault contents: the `amazon-photos` library can
  only *add to* and *remove from* a vault, and lists `searchContext=family` as an accepted value
  without using or describing it.
- **The likely read path is `search` with `searchContext=family`, but that is a guess.** Whether it
  returns photos *other* members added, whether `nodes/{id}/content` then downloads them with this
  account's cookies, and what the response looks like are all unknown until tried on Matt's account.
- **A second device login is known to work mechanically.** It is the same flow that was verified
  live on 2026-11-10, run once more while the other person signs in. The costs are their
  consent, their sign-in and two-factor prompt, a second refresh token to store, and config and
  state changes so two sources feed one frame.
- **Recommendation:** do a 15-minute live check of the vault first (questions below). If
  `searchContext=family` returns other members' photos and they download, build "vault mode" as a
  third source mode with the existing login. If it doesn't, ask whether a **shared album** is
  enough before building a second login, because the album path may already work today.

## Findings

### How Family Vault works (Amazon's own pages)

Sources: Amazon help pages "What are the Different Amazon Photos Features?" ([GLSS4222BAQWWB3S](https://digprjsurvey.amazon.com/csad/help/node/GLSS4222BAQWWB3S)),
"Add Photos in Family Vault Using a Web Browser" ([G5BW892PWB2AE8JF](https://digprjsurvey.amazon.com/csad/help/node/G5BW892PWB2AE8JF)),
"… Using the iOS App" ([GMXPTY3W7B4E6UMQ](https://digprjsurvey.amazon.com/csad/help/node/GMXPTY3W7B4E6UMQ)),
"… Using the Android App" ([GSHMHFEY7SX3UU76](https://digprjsurvey.amazon.com/csad/help/node/GSHMHFEY7SX3UU76)).
These are served from an Amazon help host (`digprjsurvey.amazon.com`); the usual
`amazon.com/gp/help/customer/display.html?nodeId=…` copies answered 503 from this session.

- **Who is in it.** "A Family Vault consists of up to six members including the Prime account
  owner." Each member gets their own Amazon Photos account at no extra cost. The 2016 launch said
  the owner could invite five people ([TechCrunch, 2016-10-18](https://techcrunch.com/2016/10/18/amazon-launches-family-vault-a-way-for-families-to-share-prime-photos-free-storage/)).
- **Who can add.** "To add photos, you must be an owner or a member of a Family Vault." The vault
  has to be activated in the app's or website's settings first.
- **How photos get in.** On purpose, per photo: select, then Share, then Add to Family Vault. Or in
  bulk from Settings, then Family Vault Settings, then "Add Your Photos to Family Vault". Launch coverage
  describes an automatic "add all my photos" setting. Whether that is still offered today, and
  whether it adds new uploads or only the existing library, is **(unverified)**.
- **Limits.** "You can only be a member of one Family Vault at a time." Two vaults can't be
  combined.
- **Who sees vault photos.** The launch article says the point is that "everyone in the group can
  see them on their own devices". No Amazon page found says in so many words that every member sees
  every item regardless of who added it. **(unverified, but it is the whole point of the feature.)**
- **Who can remove another member's photos:** not documented anywhere found.

Family Vault is not the same as **Groups** (shared spaces with up to 50 members, where people add
photos, albums, comments and reactions). The current shared-album path goes through Groups, not the
vault (see "What the tool already sees").

### Private endpoints (community code, not tried)

| What | Endpoint | Source | Status |
|---|---|---|---|
| Add to / remove from vault | `POST https://www.amazon.{tld}/drive/v1/familyArchive/` with JSON `{nodesToAdd: [...], nodesToRemove: [...], familyId, resourceVersion: "V2", ContentType: "JSON"}` | [`amazon_photos/_api.py`](https://github.com/trevorhobenshield/amazon_photos/blob/685c965b5a4ba1ac85d418820ad200e12c18a46d/amazon_photos/_api.py#L877-L917) `add_to_family_vault` / `remove_from_family_vault` (identical in the [kpfitzgerald fork](https://github.com/kpfitzgerald/amazon_photos)) | Write only. The tool would never call it. The library doesn't say how to find `familyId`. |
| Search the vault | `GET {metadataUrl}search?…&searchContext=family` | `amazon_photos` [README "Search" table](https://github.com/trevorhobenshield/amazon_photos#search) lists `customer`, `all`, `unknown`, `family`, `groups` as values; the library's own calls use only `customer` and `all` | **(unverified)**: what it returns, whether it includes other members' photos, and whether it needs extra parameters |
| Search everything visible | `searchContext=all` | same README; used by the library only for `search` aggregations | **(unverified)**: might combine own and vault and group items |
| Search one group | `GET {metadataUrl}search/groups/{groupId}` | Method name and route in a decompiled copy of Amazon's own Android cloud-drive SDK, found on GitHub (`com/amazon/clouddrive/cdasdk/cds/search/CDSSearchRetrofitBinding.java` in `Super-Crab/com.amazon.dee.app`). The same SDK's `SearchContext` enum has exactly `customer`, `family`, `groups` and `all`, and its node model has a `familyModifiedDate` field. | **(unverified)**: third-party decompile, used here only as a hint that the values are real |
| Download | `GET {contentUrl}nodes/{id}/content` | used today (`AmazonEndpoints.NodeContent`) | Works for this account's own nodes. **(unverified)** for a node another member owns; the content host may need that owner's `ownerId` (the `contentRedirection` URL in `research-notes.md` takes an `ownerId` parameter). |

Also checked, with nothing on Family Vault: [kpfitzgerald/amazon-photos-mcp](https://github.com/kpfitzgerald/amazon-photos-mcp)
and [airbone42/immich-amazon-sync](https://github.com/airbone42/immich-amazon-sync). Amazon's
retired public Drive API docs ([developer.amazon.com](https://developer.amazon.com/docs/amazon-drive/ad-restful-api.html))
never covered the vault. A GitHub code search for `familyArchive` and `nodesToAdd familyId` found
only the `amazon-photos` library.

### What the tool already sees (from the recorded fixtures)

The fixtures recorded from Matt's account on 2026-11-06 (`tests/SkylightPhotoSync.Tests/Fixtures/Amazon/`)
carry fields that hint at cross-account content:

- Every node has `xAccntParents` and `xAccntParentMap` ("cross-account parents", going by the
  name). They are empty on every recorded node. Whether a vault or group item from another account
  would fill them is **(unverified)**.
- The configured album and most others carry `groupPermissions[{groupId, albumIds, …}]`, so they
  are shared through Groups. All three recorded pages of the album have `ownerId` equal to the
  account's own root `ownerId`.
- `createdBy` differs between items (three distinct redacted values). **It is not the person who
  added the photo.** Amazon's Drive API docs define it as the friendly name of the *application*
  that created the node (for example the phone app or the web uploader), and the root folder's own
  `createdBy` differs from its `ownerId` too. The fixtures therefore don't show whether anyone else
  has added to the album.

Spec §4 says photos other people add to a shared album owned by this account are included. Going
by the fixtures, that is plausible, but it has not been checked with a photo known to come from
someone else. **(unverified)**

### A second device login

From the device-login notes in `docs/research-notes.md` and the code in
`src/SkylightPhotoSync/Sources/Amazon/`:

- **Mechanics: known.** `login` builds a PKCE sign-in URL for a fresh 40-character serial, the
  person signs in to *their* Amazon account in a browser (with their two-factor prompt), the pasted
  landing URL yields a single-use code, `POST /auth/register` returns a refresh token, and each run
  mints cookies with `POST /ap/exchangetoken/cookies`. Nothing in that flow is tied to Matt's
  account. A second person's sign-in gives a second, independent refresh token. The second
  registration itself has not been tried. **(unverified, but the same code path)**
- **What is single-account today.** `Secrets` has one `AmazonRefreshToken` and one
  `AmazonDeviceSerial` (`[amazon] refresh_token`, `device_serial`). `AmazonPhotosSource` takes one
  `Secrets`. `login` overwrites the saved device, and `logout` deregisters it. Config has one
  `[source]`. The state store keys sent items by Amazon node id. Node ids are probably unique
  across accounts, but that is **(unverified)**.
- **Visible to the other person.** The device shows as "<their first name>'s Skylight Photo Sync"
  under their Manage Your Content and Devices. They can remove it at any time, which turns into
  `needs_login` for their source only if the code keeps sources separate.
- **It reads their whole library** unless limited to one of their albums. Their screenshots and
  receipts are exactly what spec §5 says must not reach the frame, so album mode should be the only
  mode for a second account.

## Options

| | A. Vault through Matt's login | B. Second device login | C. Shared album, no new code | D. Do nothing |
|---|---|---|---|---|
| What family members do | Add photos to the vault (or turn on auto-add) | Sign in once on Matt's Mac and approve their 2FA; keep the device registered | Add photos to Matt's shared "Skylight" album from their phone | Send photos to Matt, or upload through the Skylight app |
| New secrets | None | A second refresh token and serial | None | None |
| Code size | Small to medium: a `vault` source mode, a new search URL, maybe an `ownerId` on download, new fixtures | Medium: a list of Amazon accounts in secrets and config, `login --account <name>`, one source per account merged into one candidate list, per-account `needs_login` and alerts, login/logout/status changes | None if spec §4 is right; docs only | None |
| Known to work | **No.** Every vault read is unverified | Login flow yes; two accounts at once no | **Partly.** The album reads fine; others' photos in it unverified | Yes |
| Fragility | A second private endpoint family that nobody documents; could change silently | Same endpoints as today; one more token to expire or be revoked | Same as today | None |
| Curation | Vault is often "everything" if auto-add is on, so all mode would need the same lookback and screenshot risk as today's all mode | Their album only, by rule | Already curated: only what people choose to add | n/a |
| Privacy and consent | Members already agreed to share with the vault | Reads another adult's account with a token on Matt's Mac; needs their explicit OK | People choose each photo | n/a |

## Recommendation

1. **First, check C (needs Matt, 5 minutes).** Ask a family member to add one photo to the Skylight
   album from their own phone, then run `skylight-sync run --dry-run --limit 3` and see whether it
   is listed. If it is, the issue's real need ("photos that only exist in another member's account")
   may already be met by asking people to add to the shared album, and #14 becomes a docs change.
2. **Then, if Matt wants the vault, run the vault live check below** before any design work. Build
   option A only if `searchContext=family` returns other members' items *and* they download with
   Matt's cookies. Add it as a third `[source] mode = "vault"` beside `album` and `all`, with the
   same lookback cap.
3. **Hold option B** unless both of those fail and Matt still wants a specific person's photos. It
   is the most code and the most consent, and its only advantage over C is that the other person
   does nothing after signing in, which is also why it can pull in photos they never meant for the
   frame.
4. Whatever is chosen, fix spec §4's "Family Vault support is a follow-up issue" to point here.

## Questions for Matt

Each one is **needs Matt**: either a decision or a check only his account can answer.

1. **needs Matt: is the family in a Family Vault today, and whose?** Who owns it, roughly how many
   members, and is auto-add on for anyone? If there is no vault, option A is moot.
2. **needs Matt: which photos are missing?** Whose account are they in, and do those people already
   add to the vault, a shared album, or neither? This decides between A, B and C.
3. **needs Matt (live check, about 5 minutes): does a family member's photo added to the shared
   Skylight album show up in `run --dry-run`?** (Recommendation step 1.)
4. **needs Matt (live check, about 15 minutes, read-only): does the vault read work?** With a
   minted session, run:
   - `GET {metadataUrl}search?filters=type:(PHOTOS OR VIDEOS)&searchContext=family&limit=5&resourceVersion=V2&ContentType=JSON`,
     then the same with `searchContext=all`. Note the `count`, and whether any returned node's
     `ownerId` differs from the root node's `ownerId`, which would mean another member's photo.
   - For one such node, `GET {contentUrl}nodes/{id}/content`, and if that fails, the same with
     `?ownerId=<that node's ownerId>`.
   - Record the bodies with the fixture recorder's redaction rules, labelled as recorded.
   - Nothing in this check writes to the account. Do **not** call `familyArchive`.
5. **needs Matt (decision): if option B, which person, and have they agreed** to a device on Matt's
   Mac that can read their library? Album mode only for their account, or would they accept all mode?
6. **needs Matt (decision): if the vault has auto-add on, is vault mode "everything",** and should
   it then be limited by the lookback window like all mode, or by a vault album if one exists
   (unknown whether the vault has albums)?

## Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| `searchContext=family` doesn't exist as hoped or returns only Matt's own vault items | Unknown | Live check before any code |
| Other members' vault nodes can't be downloaded with Matt's cookies | Unknown | Live check step 2. If it fails, option A is out. |
| A second account's screenshots or receipts reach the frame | Medium with B in all mode | Album mode only for extra accounts |
| Second refresh token revoked by the other person | Low to medium | Per-account `needs_login` and alert; other sources keep working |
| Same photo in two accounts (vault copy plus original) sent twice | Unknown | Dedupe on `contentProperties.md5` as well as node id, if A or B is built |
| ToS | Unknown | Same position as today: personal use, low volume, read-only |
