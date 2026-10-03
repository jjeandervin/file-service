# Spec: Extract the file service into its own repo and make it multi-client

## Goal

Move the file service out of `hoovie-pack` into a new repo, `jjeandervin/file-service`, published as versioned container images. Then make it safe for more than one app to share. HooviePack stays working throughout and becomes the first client; ACBF becomes the second.

The spec has three parts, run as separate sessions or by hand:

- **Part A: the new `file-service` repo.** A Claude Code session in that repo.
- **Part B: the `hoovie-pack` changes.** A Claude Code session in hoovie-pack, done after A publishes 1.0.0.
- **Part C: the production cutover.** A manual checklist for the owner.

ACBF's own wiring (the Aspire container, its client key, and photo features) belongs to the ACBF inventory spec, not this one.

## What exists today (hoovie-pack, commit `f010d3d`)

- **`apps/api/src/HooviePack.Files.Api`:** a controller API. Endpoints:
  - `POST /files/uploads` returns a presigned PUT, an upload token, and a file ID.
  - `POST /files/{id}/complete` takes the token and verifies S3's size and content type.
  - `GET /files/{id}/download` returns a presigned GET.
  - `DELETE /files/{id}`.
  - The `X-Internal-Api-Key` header is checked on `/files/*`.

  Metadata lives in `files."Files"`, using EF with its own migrations history table in schema `files`. There are health endpoints at `/health/live` and `/health/ready`.
- **`HooviePack.Files.Domain`:** the request and response contracts, also referenced by `HooviePack.Api`.
- **Tests:** `tests/HooviePack.Files.Api.Tests`.
- **Dockerfiles:** `Dockerfile.files` and `Dockerfile.files.migrations`, built by compose and CI.
- **`HooviePack.FileMigration`:** a legacy-media importer. It references both `HooviePack.Api` and `HooviePack.Files.Api`, and writes `FileRecord.LegacySourcePath`.

Everything in this design is kept. The changes are the extraction, client isolation, publishing, and three small features.

---

# Part A — `jjeandervin/file-service`

## A1. Extract with history (owner runs once)

```sh
git clone https://github.com/jjeandervin/hoovie-pack file-service
cd file-service
git filter-repo \
  --path apps/api/src/HooviePack.Files.Api/ \
  --path apps/api/src/HooviePack.Files.Domain/ \
  --path apps/api/tests/HooviePack.Files.Api.Tests/ \
  --path apps/api/Dockerfile.files \
  --path apps/api/Dockerfile.files.migrations \
  --path apps/api/dotnet-tools.json \
  --path-rename apps/api/:
git remote add origin git@github.com:jjeandervin/file-service.git
git push -u origin main
```

This keeps the history of those files only. `git filter-repo` is a separate install (`pip install git-filter-repo`).

## A2. Restructure and rename

Do this as its own commit, with no behavior change.

| Before | After |
|---|---|
| `src/HooviePack.Files.Api` | `src/FileService.Api` (namespace `FileService.Api`) |
| `src/HooviePack.Files.Domain` | `src/FileService.Contracts` (namespace `FileService.Contracts`) |
| `tests/HooviePack.Files.Api.Tests` | `tests/FileService.Api.Tests` |
| `Dockerfile.files` | `Dockerfile` |
| `Dockerfile.files.migrations` | `Dockerfile.migrations` |

- Add a `FileService.slnx`, `global.json` (the same .NET 10 SDK as hoovie-pack), `.editorconfig`, `.gitignore`, and a `README.md` (§A9).
- Keep the class names `FilesDbContext` and `FileRecord`, the schema `files`, the table `"Files"`, and the history table `files."__EFMigrationsHistory"`. **Don't regenerate existing migrations.** EF history rows are keyed by migration ID, so renaming namespaces is safe. Update the namespaces in the migration and designer files.
- `LegacySourcePath` stays as-is. HooviePack's legacy import used it.
- All existing tests must pass before moving on.

## A3. Clients (the main change)

### Configuration

```json
"Clients": [
  { "Id": "hooviepack", "ApiKey": "<secret>", "KeyPrefix": "files" },
  { "Id": "acbf", "ApiKey": "<secret>", "KeyPrefix": "acbf",
    "MaxFileBytes": 10485760, "AllowedContentTypes": ["image/jpeg", "image/png", "image/webp"], "BucketName": null }
]
```

Supply these through environment variables, e.g. `Clients__0__Id`, `Clients__0__ApiKey`, `Clients__0__KeyPrefix`. Secrets never go in committed files.

**Per-client fields:**
- `Id`: matches `^[a-z][a-z0-9-]{1,31}$`.
- `ApiKey`: 32 characters or more.
- `KeyPrefix`: the existing prefix rules.
- Optional `MaxFileBytes`, `AllowedContentTypes`, and `BucketName`, which default to the global `FileStorage` values.

**Validated at startup (fail fast):**
- IDs are unique, and API keys are unique.
- Within one bucket, no client's prefix equals another's or is a segment-prefix of it (e.g. `acbf` and `acbf/x` conflict).

**Legacy single-key mode.** This lets the new image run on HooviePack's current configuration, unchanged. When `Clients` is empty and `InternalApi:ApiKey` is set, synthesize one client, `hooviepack`, with `KeyPrefix = FileStorage:KeyPrefix`, and log a warning at startup. Both set at once is a startup error. Remove this mode in a future 2.0.

### Authentication

Rename `InternalApiKeyMiddleware` to `ClientAuthenticationMiddleware`.

- It hashes the supplied `X-Internal-Api-Key` and compares it with a constant-time comparison against **every** client's key hash. Don't short-circuit, so timing can't leak which client matched.
- On a match, it sets an `ICallerClient` (scoped): `Id`, `KeyPrefix`, `BucketName`, and the effective limits.
- No match → the existing 401 body.
- `/health/*` stays open.

### Data

Migration `AddClientId`:
- Add `ClientId varchar(32) NOT NULL DEFAULT 'hooviepack'`.
- Add an index on `(ClientId, Status, CreatedAt)`.
- **Keep the database default permanently.** The pre-extraction HooviePack image inserts rows without `ClientId`, and the default is what lets a rollback to that image still work (Part C).
- Add a `BucketName varchar(63) NULL` column to `FileRecord`, filled at creation from the client's effective bucket, so later client config changes never orphan objects. Null means the global bucket, which covers existing rows.

### Behavior

- **Create:** the record gets `ClientId = caller.Id`, and the storage key is `{caller.KeyPrefix}/{fileId:N}/original`. HooviePack's prefix stays `files`, so every existing key keeps its location. Limits and content types come from the caller's effective settings.
- **Complete, download, and delete** load the record with `Id == fileId && ClientId == caller.Id`. Another client's file is **404**, the same as a file that doesn't exist, so callers can't learn whether an ID exists.
- **S3 operations** use the record's bucket (`BucketName ?? FileStorage:BucketName`).
- **Tests:**
  - a client can't complete, download, or delete another client's file
  - keys use each client's prefix
  - legacy mode behaves exactly like today, including existing keys
  - startup validation failures
  - existing rows read as `hooviepack` after the migration

## A4. Batch download URLs

`POST /files/downloads` with `{ "fileIds": ["…", …] }` (1–100 distinct IDs) returns:

```json
{ "items": [ { "fileId": "…", "downloadUrl": "…", "expiresAt": "…" } ], "missing": ["…"] }
```

- Only `Ready` files owned by the caller get URLs. Everything else goes in `missing`, with no distinction between "not yours" and "doesn't exist".
- The batch does **not** do the per-object S3 HEAD that the single download does, because 100 round trips would defeat the point. It trusts `Ready`, and that trade-off is documented in the README.
- Contracts go in `FileService.Contracts`.
- Tests cover mixed ownership, the 100-item limit, duplicates, and an empty list (400).

## A5. Abandoned-upload cleanup

A `PendingUploadCleanupService` background service runs every `FileStorage:PendingCleanupIntervalMinutes` (default 60).

- It deletes `Pending` records older than `FileStorage:PendingUploadTtlHours` (default 24; minimum 1) in batches of 100. For each, it deletes the S3 object first (idempotent), then the row, the same order `DeleteAsync` already uses.
- Failures are logged and retried next cycle, and they never crash the host.
- A `FileStorage:PendingCleanupEnabled` switch (default true) turns it off.
- Tests use a fake `TimeProvider` and fake storage.

## A6. Content verification on complete

After the size and content-type checks pass, read the first 16 bytes with a ranged GET (add `ReadPrefixAsync(key, count)` to `IObjectStorage`). Check them against the declared type:

| Type | Signature |
|---|---|
| `image/jpeg` | `FF D8 FF` |
| `image/png` | `89 50 4E 47 0D 0A 1A 0A` |
| `image/webp` | `RIFF`, then any 4 bytes, then `WEBP` |

- A type with no known signature passes this check.
- A mismatch deletes the object and record, and returns the same 400 as a size mismatch.
- The check is on by default: `FileStorage:VerifyContentSignature`.
- Tests cover a real small JPEG, PNG, and WebP, a text file declared as `image/jpeg`, and a truncated file.

## A7. Health and startup

- `/health/ready` stays as Postgres readiness. Add an optional S3 check (a HEAD on the bucket), off by default, via `FileStorage:CheckBucketOnReady`.
- Log the configured client IDs and prefixes at startup, never their keys.

## A8. Images and CI

GitHub Actions, `ci.yml`:

- **Every push and PR:** restore, build with warnings as errors, and test against a Postgres 17 service container (the current tests' pattern). Also run gitleaks, as hoovie-pack does.
- **Tags `v*.*.*`:** build and push two images to GHCR:
  - `ghcr.io/jjeandervin/file-service`: the API
  - `ghcr.io/jjeandervin/file-service-migrations`: the EF migrations bundle, the same pattern as the current `Dockerfile.files.migrations`

  Each gets tags `X.Y.Z`, `X.Y`, and the commit SHA. Don't push `latest`; deployments pin versions.
- Images carry OCI labels for source, version, and revision.
- **Package visibility is public**: the images contain no secrets, and the homelab and ACBF pull them without logging in. Document how to switch to private (a PAT with `read:packages` and `docker login ghcr.io`).

Release **1.0.0** once A2–A7 are merged.

## A9. README

- **What it is:** a shared internal file service. It must never be exposed to the internet, and only its callers' APIs may reach it.
- **API reference:** every endpoint, header, request and response body, and status code, including 404-for-others'-files and the batch endpoint's no-HEAD trade-off.
- **The client flow:** create an upload → browser PUT to the presigned URL with `requiredHeaders` → the caller API completes it with the upload token → store the `fileId`.
- **Configuration reference:** the `FileStorage` options, `Clients`, legacy mode and its removal plan, and AWS credentials through the SDK chain (env vars in compose).
- **The bucket CORS rule browsers need:** `PUT` and `GET` from each client's web origin, with the `Content-Type` header allowed. Include a sample JSON rule.
- **Running locally:** `docker run` with a local Postgres, and optionally MinIO via `ServiceUrl` and `ForcePathStyle`.
- **Versioning:** SemVer, what counts as breaking, and a CHANGELOG.

## A10. Acceptance

- [ ] The history-preserving repo exists, and its CI is green.
- [ ] All the old tests pass after the rename, plus the new tests for A3–A7.
- [ ] `v1.0.0` publishes both images, and `docker run` of the API against a local Postgres reaches `/health/ready`.
- [ ] With only `InternalApi__ApiKey` set (legacy mode), a HooviePack-style create → PUT → complete → download → delete cycle works end to end against the dev bucket, and objects land at `files/{id}/original`.

---

# Part B — `hoovie-pack` becomes a client

Do this after 1.0.0 is published.

**B0. The legacy importer.** `HooviePack.FileMigration` depends on file-service internals and can't stay once those are gone.
- **If the owner confirms** the legacy media migration has completed in production, delete `src/HooviePack.FileMigration`, `tests/HooviePack.FileMigration.Tests`, `Dockerfile.file-migration`, and their CI and compose entries. Git history keeps them.
- **If it hasn't completed, stop.** Run it first.

**B1.** Remove `src/HooviePack.Files.Api`, `src/HooviePack.Files.Domain`, `tests/HooviePack.Files.Api.Tests`, `Dockerfile.files`, and `Dockerfile.files.migrations`, and drop them from `HooviePack.slnx`.

**B2.** Copy the five contracts into `HooviePack.Api` under `HooviePack.Api.Infrastructure.Storage.Contracts`, with **the same JSON shape**: `CreateUploadRequest`, `CompleteUploadRequest`, `UploadResponse`, `DownloadResponse`, and `FileMetadataResponse`.
- Update `Dockerfile.migrations` and `Dockerfile`, which currently copy `HooviePack.Files.Domain`.
- `FileServiceClientContractTests` must still pass, and they're now the guard against drift from the service.

**B3.** In `compose.yaml`:
- `files-api` uses `image: ${FILES_SERVICE_IMAGE:-ghcr.io/jjeandervin/file-service:1.0.0}`. Delete its `build:` block.
- The files-migrations service uses `ghcr.io/jjeandervin/file-service-migrations:1.0.0` the same way.
- Keep today's environment variables (`InternalApi__ApiKey`, …), so the first deployment runs in legacy mode with **no** configuration change.
- Add commented `Clients__0__…` lines, and a note in `.env.example` explaining the switch in Part C.

**B4. CI:** remove the files image builds. Wherever CI starts the files service for integration tests, use the pinned GHCR images.

**B5. Test:** run the full test suite, then run the local compose stack and check avatar, dog photo, post photo, and calendar photo upload and display.

---

# Part C — Production cutover (owner checklist)

1. **Back up:** `pg_dump` the whole `hooviepack` database, or at least schema `files`. Note the currently deployed image digests, for rollback.
2. **Deploy file-service 1.0.0 in legacy mode.** Pull the new images, run the files-migrations image (which adds `ClientId` with default `hooviepack`), and restart `files-api`. No environment changes.
3. **Verify HooviePack:** existing photos load (avatar, dog, post, calendar), a new upload works end to end, deleting a post photo removes it, and `docker compose logs files-api` shows the legacy-mode warning and no errors.
4. **Switch to explicit clients:** set `Clients__0__Id=hooviepack`, `Clients__0__ApiKey=${FILES_INTERNAL_API_KEY}`, `Clients__0__KeyPrefix=files`, remove `InternalApi__ApiKey`, restart, and repeat step 3.
5. **Add ACBF:** generate a new key (`openssl rand -base64 48`) and add `Clients__1__Id=acbf`, `Clients__1__ApiKey=…`, `Clients__1__KeyPrefix=acbf`. Restart and repeat step 3. With the ACBF key, `GET /files/{a HooviePack file id}/download` must return **404**.
6. **S3 CORS:** add ACBF's production web origin to the prod bucket's CORS rule. Confirm the IAM policy allows the `acbf/` prefix (a bucket-wide policy already does).
7. **Rollback, at any step:** redeploy the previous `files-api` image digest. The `ClientId` column's default keeps old-image inserts working. Leave the column; it's harmless.
