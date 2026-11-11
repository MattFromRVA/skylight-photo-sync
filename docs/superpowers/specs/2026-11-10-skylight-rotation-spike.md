# Spike: hourly photo rotation on the Skylight frame

Date: 2026-11-10. Status: **implemented on 2026-11-11** as the optional `[rotation]` feature
(setup guide step 13), with Matt's answers below. The live check at the end has still not been
run: every Skylight response in the tests is a hand-written placeholder. Items marked
**(verify)** are still unverified.

Decisions (Matt, 2026-11-11): recycle once the album is out of new photos (question 1, yes);
30 in and 30 out each hour (`max_items_per_run = 30`, `keep = 30`); storing the Skylight password in
secrets.toml is fine.

As built, the design below differs in a few places:

- `frame_name` blank means every frame on the account. Only this tool's photos are deleted anywhere,
  so that is safe, and it covers more than one Skylight address in `[email] to`.
- Each run waits up to `arrival_wait_minutes` (default 10) for the photos it just emailed to show up
  before pruning. Otherwise the prune would see last hour's photos as the newest and keep them.
- Deletions go to a `pruned_messages` table. A resend updates `sent_items.sent_at`, and recycling
  picks the longest-ago send first.
- `skylight-sync prune [--dry-run]` runs the clean-up on its own and works with rotation off, so it
  doubles as the live check below.

## Question

Can the tool keep the frame at a fixed number of photos (say the newest N) by pruning older
photos automatically every hour, using email or an API?

## Short answer

- **Email: no.** Skylight's email address only takes photos in. There is no subject line,
  reply, or command email that deletes a photo. The only automatic removal Skylight does is
  replacing the oldest photos once the frame's storage is full (8 GB, roughly 8,000+ photos).
  That isn't useful as a rotation.
- **API: yes, probably, but it's unofficial.** The Skylight apps talk to a reverse-engineered
  REST API at `https://app.ourskylight.com/api` that can list a frame's photos and delete them
  one at a time. Two open-source clients document it. Skylight doesn't publish or support this
  API, so it can change or break without notice.
- **Recommendation:** build rotation as an **optional, off-by-default** "prune" step that uses
  the unofficial API. Keep email as the way photos go in. If the API breaks, the prune step
  fails and raises an alert, and the sync keeps working as it does today.

## What the unofficial API offers

Sources: [dknowles2 gist](https://gist.github.com/dknowles2/b8eab833eb23eb388c3d78999a3565f8),
[dknowles2/pyskylight](https://github.com/dknowles2/pyskylight) (Python client; its
`docs/api-notes.md` says paging was checked against a live account),
[aarons22/skylight-tools](https://github.com/aarons22/skylight-tools) (OpenAPI spec; covers
auth and frames, not photos).

In the API, photos are called **messages**.

| Need | Endpoint | Notes |
|---|---|---|
| Sign in | OAuth 2 auth code + PKCE: `GET /oauth/authorize` → `GET /auth/session/new` (CSRF from `<meta name="csrf-token">`) → `POST /auth/session` form `authenticity_token,email,password` → code from redirect `Location` → `POST /oauth/token` | Needs the Skylight account email and password. No MFA mentioned. `User-Agent: SkylightMobile (web)` reportedly required. **(verify)** |
| Stay signed in | `POST /oauth/token grant_type=refresh_token` | Access token lasts about 2 h (`expires_in: 7200`). pyskylight refreshes before expiry and retries once on 401/403. |
| Find the frame | `GET /api/frames` | Numeric frame id. The frame record includes a `plus` flag. |
| List photos | `GET /api/frames/{id}/messages?page=N` | Newest first, fixed 30 per page; `meta.num_pages`. Fields: `id`, `created_at`, `from_email`, `sender_id`, `asset_type`, `asset_url` (signed URL, expires), `caption`, `destroyed_at`. |
| Delete one photo | `DELETE /api/frames/{id}/messages/{messageId}` | No body. Implemented in pyskylight. |
| Bulk delete | `DELETE /api/frames/{id}/messages/destroy_multiple` | Listed in the gist. Body not documented (probably `{"ids":[...]}`). **(verify)** Not needed; per-photo delete is enough at hourly scale. |
| Upload | `POST /api/messages/uploads`, presigned-URL endpoints | Listed by name only, no shapes. Not needed; email already works. |

Not established: whether deleting photos through the API needs Skylight Plus (bulk delete in the
app may be Plus-only), any rate limits, and whether the API's terms permit this use. The
Skylight support site was unreachable from this session, so the Plus question is still open.

## How rotation would work

Run as a new step at the end of each hourly `run`, after the email is sent:

1. Sign in to Skylight (refresh the cached token, or do a full password sign-in if needed).
2. List the frame's messages, paging until we have them all (30 per page, newest first).
3. Keep only **our** photos: `from_email` equals `email.from`. Photos family members add from the
   app or their own email are never touched.
4. Sort ours by `created_at` descending, keep the newest `rotation.keep` (for example 30), and
   delete the rest one at a time, oldest first, capped at `rotation.max_deletes_per_run` as a
   safety brake.
5. Record each deletion in a new `pruned_items` table (message id, created_at, pruned_at).

This doesn't require mapping our Amazon item ids to Skylight message ids. `from_email` plus
`created_at` is enough.

Note that the newest photos *sent* aren't necessarily the newest *taken*: the sync sends oldest
first, so the frame holds whatever went out most recently.

### Config as built (off by default)

```toml
[rotation]
enabled = false
keep = 30                  # photos from this tool to keep on each frame
max_deletes_per_run = 100  # safety brake, per frame per run
frame_name = ""            # blank = every frame on the account
arrival_wait_minutes = 10  # wait for just-sent photos before pruning
```

There is no `dry_run` key; `skylight-sync run --dry-run` and `skylight-sync prune --dry-run`
cover it.

`secrets.toml` gains `skylight_email` and `skylight_password`. They would be stored like the
SMTP password (0600, never committed).

## Design questions for Matt

1. **What happens once the album runs out of new photos?** Dedupe means a pruned photo is
   never sent again, so the frame would just hold the last N forever. If the goal is
   "a fresh 30 every hour, forever", we also need **recycling**: when nothing is unsent, resend
   the photos that were pruned longest ago. That changes the dedupe rule from "never resend"
   to "don't resend while it's on the frame". This is the biggest decision.
2. **How many photos per hour?** With a keep of 30 and the per-run cap of 25 (`max_items_per_run`),
   the frame turns over almost completely every hour. If you want slower turnover, lower
   `max_items_per_run`, for example to 5.
3. **Is storing your Skylight password OK?** The API has no app passwords or API keys, so the
   tool needs the real account password (or a token refreshed from it).
4. **Clean-up of today's 200+:** the first run with rotation turned on would delete everything
   from this tool except the newest 30, up to the safety brake. Run it with `dry_run = true` first.

## Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| Skylight changes or blocks the unofficial API | Medium | Prune step is optional and isolated; failure raises an alert and sending keeps working |
| Deleting the wrong photos | Low | Only delete where `from_email == email.from`; `dry_run`; per-run delete cap; log every delete |
| Account lockout or CAPTCHA on password sign-in | Unknown | Cache and refresh the token so a full sign-in is rare; alert on failure, like the Amazon `needs_login` flow |
| Deleting needs Skylight Plus | Unknown | Check in the live test below |
| ToS | Unknown | Same position as the Amazon side; personal use, low volume |

## Next step: a 30-minute live check (needs Matt's Skylight sign-in)

Before writing code, confirm on the real account:

1. The password sign-in flow gives a token (and whether there's a CAPTCHA or MFA).
2. `GET /api/frames/{id}/messages` returns our emailed photos with `from_email` set to our sender.
3. `DELETE .../messages/{id}` on one test photo removes it from the frame and the app, and works
   without Plus (or with it, if that's what the account has).
4. Save redacted response bodies as test fixtures, labeled as recorded from the live account,
   following the same rule as Task 12.

If all four pass, plan the work as tasks: a Skylight API client behind an `ISkylightClient`
interface (faked in tests, no network), a `Rotator` with the keep/sort/delete logic, config and
secrets keys, a `pruned_items` table, and the optional recycling change from question 1.
