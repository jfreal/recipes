# MediaReconciler

A Windows command-line tool that answers one question: **is every photo and video actually
backed up, and where am I paying to store the same thing twice?**

You list as many sources as you like (iCloud, several Dropbox accounts, several Google
Photos libraries, the iPhone itself, any folder or drive). It finds every copy of every
picture across all of them and writes a report with:

- what is **in no backup at all** (only on the phone),
- what has **fewer backup copies** than you want,
- what is **safe to delete from the phone** to free space,
- **what you'd lose if you cancelled each subscription**,
- **byte-identical duplicates** wasting space inside one source,
- a CSV for each backup listing what's **missing from it** (and where to copy it from).

It is **read-only**: it never deletes, moves or uploads anything.

## Build and run

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
cd tools\MediaReconciler

# One self-contained MediaReconciler.exe (no .NET needed on the machine that runs it):
dotnet publish src\MediaReconciler -c Release -f net10.0-windows -r win-x64 --self-contained -p:PublishSingleFile=true -o publish

publish\MediaReconciler.exe init            # writes reconcile.json, edit it
publish\MediaReconciler.exe run             # scans and writes report\
```

`dotnet test` runs the tests. The `net10.0` build also runs on macOS/Linux for every source
except the direct iPhone one.

The first run reads every byte of every local file to hash it, so a 200 GB library takes a
while. Results are cached in `%LOCALAPPDATA%\MediaReconciler\scan-cache.json`, and later runs
only read new or changed files. Ctrl+C is safe: what was read so far is kept.

## Configuring sources

`reconcile.json` (comments allowed). Every source needs a unique `name` and a `type`:

```jsonc
{
  "sources": [
    { "name": "Wife iCloud",          "type": "folder",         "path": "D:\\Backups\\iCloud\\wife" },
    { "name": "My iCloud",            "type": "folder",         "path": "D:\\Backups\\iCloud\\john" },
    { "name": "Dropbox (John)",       "type": "dropbox",        "path": "/Camera Uploads",
      "appKey": "abc123", "refreshToken": "env:DROPBOX_JOHN" },
    { "name": "Dropbox (Wife)",       "type": "dropbox",        "path": "",
      "appKey": "abc123", "refreshToken": "env:DROPBOX_WIFE" },
    { "name": "Google Photos (wife)", "type": "google-takeout", "path": "D:\\Takeout\\wife" },
    { "name": "Wife iPhone",          "type": "iphone",         "hash": false }
  ],
  "minBackupCopies": 2
}
```

| Option | Meaning |
|---|---|
| `minBackupCopies` | How many backups a picture needs to count as safe. Default 2. |
| `timeOnlyMatching` | Match Dropbox's renamed copies by capture time (see [matching](#how-matching-works)). Default true. |
| `outputDirectory` | Where the report goes. Default `report` next to the config. |
| `cachePath` | Where the hash cache lives. |
| per source: `isBackup` | Whether a copy here counts as a backup. Default true, except for `iphone`. |
| per source: `hash` | Hash file contents. Default true. With `false`, matching uses names, sizes and dates only. |
| per source: `exclude` | Folder names to skip, e.g. `["Screenshots"]`. |

Secrets can be written as `"env:VARIABLE"` so they stay out of the file
(`setx DROPBOX_JOHN <token>` sets one permanently on Windows).

### iCloud Photos → `folder`

Apple has no API for iCloud Photos, so give the tool a local copy:

- **icloudpd (recommended).** [icloud_photos_downloader](https://github.com/icloud-photos-downloader/icloud_photos_downloader)
  downloads the full-size originals and sets each file's date to the photo's date. Run it on a
  schedule into one folder per Apple ID:
  ```powershell
  icloudpd --directory D:\Backups\iCloud\wife --username wife@example.com --folder-structure "{:%Y}"
  ```
  It needs "Advanced Data Protection" off and "Access iCloud Data on the Web" on, and asks
  for two-factor authentication again every couple of months.
- **iCloud for Windows.** Its Photos folder works, but files that are "online only" are
  placeholders. The tool won't download them (that could be hundreds of GB) and matches them
  by name, size and date instead. Set `"readOnlineOnlyFiles": true` to hash them anyway.

### Dropbox → `dropbox`

Read through the Dropbox API. Only the file list is fetched, never the files: Dropbox reports
a content hash for every file, and this tool computes the same hash locally, so Dropbox copies
are compared byte-for-byte without downloading anything.

One-time setup:

1. At <https://www.dropbox.com/developers/apps> click **Create app** → *Scoped access* →
   *Full Dropbox* → any name.
2. On the **Permissions** tab tick `files.metadata.read` and click **Submit**.
3. Copy the **App key** from the Settings tab.
4. For **each Dropbox account** you want to check, run this and sign in as that account:
   ```powershell
   MediaReconciler.exe dropbox-auth --app-key <app key>
   ```
   It prints the `refreshToken` for that account's source. One app key works for every account.

`path` is the folder to check (`"/Camera Uploads"`), or `""` for the whole account.

You can also point a `folder` source at the Dropbox desktop folder, but online-only files there
are placeholders too, so the API source is usually better.

### Google Photos → `google-takeout`

Since March 2025 Google no longer lets apps list a Google Photos library, so the only complete
view is an export:

1. Go to <https://takeout.google.com>, click **Deselect all**, tick only **Google Photos**.
2. Choose **.zip** (not .tgz) and the largest file size, and export.
3. Download all the zip parts into one folder (one folder per Google account) and set `path` to it.

You don't need to unzip them. The tool reads the photos inside the zips and the JSON files
next to them (which hold the real capture time and the original file name), and counts a photo
once even though Takeout puts a copy in every album folder. Google's `-edited` copies are skipped
unless you set `"includeEdited": true`.

### The iPhone itself → `iphone` (Windows build)

Plug the phone in by USB, unlock it and tap **Trust**. `MediaReconciler.exe devices` lists
what Windows sees. `device` picks one phone when several are plugged in (part of its name).

- Only photos whose full-size original is on the phone are listed. With *Optimize iPhone
  Storage* on, older photos live only in iCloud, and that's expected.
- Set **Settings → Photos → Transfer to Mac or PC → Keep Originals**, or Windows hands over
  converted JPEGs.
- `"hash": false` (the sample default) is much faster: phone files are matched by name and
  size, which is reliable for iPhone `IMG_1234.HEIC` names. `"hash": true` copies every file
  over USB to hash it.

I couldn't test this source against a real iPhone, so treat its first run with some suspicion.

### Anything else → `manifest`

If another program can list a source (your existing iPhone app, a NAS, another cloud), have it
write a CSV and add it as a `manifest` source. A header row is required, columns can be in any
order, and only `name` is mandatory. A `manifest` counts as a backup unless you set
`"isBackup": false`, so set it on any manifest that lists your phone, or the risk numbers and
exit code will understate what is unprotected:

```csv
location,name,size,hash,taken_utc,taken_local
DCIM/100APPLE/IMG_0001.HEIC,IMG_0001.HEIC,2345678,,2024-05-01T19:34:56Z,2024-05-01T12:34:56
```

`hash`, if given, must be a Dropbox content hash: SHA-256 each 4 MiB block, then SHA-256 the
concatenated block hashes ([reference](https://www.dropbox.com/developers/reference/content-hash);
`Hashing/DropboxContentHasher.cs` is a C# version).

## Reading the report

`report\summary.md` (also printed to the console) shows the headline numbers. The CSVs open in Excel:

| File | What it lists |
|---|---|
| `at-risk.csv` | Pictures with fewer than `minBackupCopies` backups, the ones in **no** backup first, with a path to open each from. |
| `missing-from-<backup>.csv` | Everything that isn't in that backup, and where else it is. Use it to fill the gaps. |
| `safe-to-remove-from-<phone>.csv` | Items on a non-backup source (the phone) that already have enough backups. Only exact (identical-bytes) matches count here: a name-only or time-only match could be a recompressed copy, so it never does. Keep `hash` on for the phone. |
| `duplicates-in-<source>.csv` | Extra byte-identical copies inside one source. |
| `weak-matches.csv` | Pairs matched only by capture time. Spot-check a few. |
| `all-pictures.csv` | Every picture, with where each copy lives in each source. |

The exit code is 3 when something is in no backup at all, so a scheduled task can flag it.

### Freeing space

1. Run with the phone connected (or an up-to-date icloudpd mirror and the phone's manifest).
2. Fix `at-risk.csv` first. Nothing should be in no backup.
3. Delete from the phone what's in `safe-to-remove-from-<phone>.csv`. With iCloud Photos
   on, deleting from the phone deletes from iCloud too, so for iCloud users the usual fix is
   *Optimize iPhone Storage* rather than deleting.
4. Use **What if you dropped one backup?** to pick a subscription you can cancel. Copy
   that source's "lost" items into the backups you keep first (`missing-from-<kept>.csv`),
   run again, and cancel only once the lost count is zero.

## How matching works

The same picture often has different bytes and names in different places. Dropbox renames
camera uploads to `2024-05-01 12.34.56.jpg` and may convert HEIC to JPG, Google may
recompress, and time zones differ. Rules, from most to least certain:

1. **Exact.** Identical content hash. Also finds duplicates inside one source.
2. **Strong.** Same original name, extension and size (e.g. an unhashed iPhone file vs. the iCloud original).
3. **Strong.** Same original name and capture time, allowing for whole time-zone shifts
   (camera local time vs. UTC), for recompressed copies and Live Photo videos.
4. **Weak.** Same capture second and type when one copy was renamed from its date. Turn off
   with `"timeOnlyMatching": false`.

Rules 2–4 never put two items from the same source into one group. Five burst shots taken in
the same second stay five pictures, so a missing one can't hide behind its neighbours. Two
different camera names (`IMG_0001` vs `IMG_9876`) are never matched by time alone, so your
photo and your wife's photo from the same moment don't count as backups of each other.

Capture times come from EXIF / QuickTime metadata, then from date-style file names, then
(trusted least) from file timestamps.

## Limitations

- Google Takeout `.tgz` exports aren't read. Choose `.zip`.
- iCloud Photos is only reachable through a local copy (icloudpd or iCloud for Windows).
- Photos edited in place on one service have different bytes than the original. They still
  match by name and time, but as *Strong*, not *Exact*.
- Screenshots and other files without EXIF fall back to file timestamps, which copying can change.
