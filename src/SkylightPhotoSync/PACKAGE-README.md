# Skylight Photo Sync

`skylight-sync` is a .NET global tool that sends new Amazon Photos to a Skylight Calendar or
Skylight Frame by email, on a schedule, from your Mac.

Every hour (configurable) it finds new photos in one Amazon Photos album or in all photos,
converts HEIC to JPEG, resizes them for the frame, strips location data, and emails them to
your Skylight household address. It remembers what it sent so nothing is sent twice.

It runs on macOS and needs the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

## Install

```bash
dotnet tool install --global SkylightPhotoSync
skylight-sync --help
```

## Set up

Follow the [setup guide](https://github.com/MattFromRVA/skylight-photo-sync/blob/main/docs/setup.md).
Source, issues, and troubleshooting are at
[github.com/MattFromRVA/skylight-photo-sync](https://github.com/MattFromRVA/skylight-photo-sync).
