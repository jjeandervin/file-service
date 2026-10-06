# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/). The README explains [what counts as breaking](README.md#versioning).

## [Unreleased]

## [1.0.0] - 2026-10-06

The first release as a standalone, multi-client service, extracted from `hoovie-pack` with its history. It runs unchanged on HooviePack's existing configuration (legacy single-key mode) and database.

### Added

- **Clients.** A `Clients` list, where each client has its own API key and key prefix, and can override `MaxFileBytes`, `AllowedContentTypes` and `BucketName`. The configuration is validated at startup, and client IDs and prefixes (never keys) are logged.
- **Client isolation.** Every file records its `ClientId` and `BucketName`. A client gets `404` for another client's file on every endpoint.
- `POST /files/downloads`, which returns download URLs for up to 100 of the caller's `Ready` files in one call, without a per-object S3 HEAD.
- Abandoned-upload cleanup: `Pending` uploads older than `FileStorage:PendingUploadTtlHours` (default 24) are deleted every `FileStorage:PendingCleanupIntervalMinutes` (default 60). Records with a `LegacySourcePath` are never deleted by the cleanup.
- Content verification on complete: JPEG, PNG and WebP uploads must start with their format's signature (`FileStorage:VerifyContentSignature`, on by default).
- An optional S3 bucket check in `/health/ready` (`FileStorage:CheckBucketOnReady`).
- Migration `AddClientId`: `ClientId varchar(32) NOT NULL DEFAULT 'hooviepack'`, `BucketName varchar(63) NULL`, and an index on `(ClientId, Status, CreatedAt)`. The default is permanent, so the pre-extraction image keeps working after the migration.
- Container images on GHCR, `ghcr.io/jjeandervin/file-service` and `ghcr.io/jjeandervin/file-service-migrations`, tagged `X.Y.Z`, `X.Y` and the commit SHA.

### Changed

- Renamed `HooviePack.Files.Api` to `FileService.Api`, and `HooviePack.Files.Domain` to `FileService.Contracts` (namespace `FileService.Contracts`). The JSON shapes are unchanged.
- `InternalApiKeyMiddleware` is now `ClientAuthenticationMiddleware`. The `X-Internal-Api-Key` header and the 401 body are unchanged.
- The image no longer ships a default `FileStorage:BucketName` for the `Production` environment. Set it explicitly; the service refuses to start without it.

### Deprecated

- Legacy single-key mode (`InternalApi:ApiKey` with no `Clients`). It will be removed in 2.0.
