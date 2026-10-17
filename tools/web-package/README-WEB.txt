YEEYEEYEE Web (Windows x64)
==========================

Requirements
------------
- Windows x64
- ASP.NET Core 10 Runtime (this package is framework-dependent)
- A writable installation directory

Layout
------
After the first install the directory looks like this:

  <install root>\
    current.txt                 version that start-web.ps1 launches
    start-web.ps1               stable launcher (pins the data directory)
    install-web.ps1             first install and legacy-data migration
    upgrade-web.ps1             verified upgrade with backup and health check
    rollback-web.ps1            program rollback / data restore
    versions\<version>\         program files (replaced on upgrade)
    data\                       persistent data (never replaced)
      yeeeyee.users.db          accounts, password hashes and sessions
      yeeeyee.jobs.db           generation jobs
      yeeeyee.chat.db           chat history and chat attachments
      assets\                   generated media and attachments on disk
      config\web-ai-config.json model and media settings
      config\ai-key.bin         AES-GCM key used to encrypt stored secrets
      config\sites\             ComfyUI sites and imported workflows
      projects\                 Web projects
      logs\, run\               logs and the current pid file
    backups\                    pre-upgrade data backups
    logs\                       launcher and upgrade logs

First install / migration from an old layout
--------------------------------------------
Old packages kept databases, assets, config and Projects beside the program.
Such a directory cannot upgrade itself safely. Migrate once:

  1. Stop the old service (close its PowerShell window).
  2. Optional but recommended: copy the entire old directory to a safe place.
  3. Extract this zip to a temporary directory.
  4. Run (paths are examples):

       powershell -NoProfile -ExecutionPolicy Bypass -File install-web.ps1 `
         -InstallRoot C:\YEEYEEYEE-Web `
         -LegacyDataDir C:\old-yeeeyee-web-0.1.5.4 `
         -Start

  install-web.ps1 copies accounts, jobs, chat history, attachments, assets,
  configuration, the encryption key and projects into <install root>\data.
  It verifies every copied file and leaves the old directory untouched.
  5. Verify login, projects, chat history, attachments and encrypted settings.
  6. Only then remove the old directory.

Upgrade
-------
  powershell -NoProfile -ExecutionPolicy Bypass -File upgrade-web.ps1 `
    -ZipPath C:\downloads\yeeeyee-web-<version>-win-x64.zip `
    -ExpectedSha256 <sha256> `
    -Force

The script verifies the zip and version, extracts to a staging directory,
checks the backend and canvas-dist, stops only this installation's service,
backs up data, switches current.txt, starts the new version and polls /health.
If the new version fails and both releases declare the same data format, the
previous program is started again automatically. Otherwise automatic rollback
is refused and the script prints the manual steps.

A data backup counts as restorable only when it contains backup-complete.json.
That record is written after every file was copied and hash-verified, and holds
the source version, dataFormat, completion time and a size/SHA-256 list of the
copied files. It never contains configuration contents, tokens or keys. While a
copy is running the directory has backup-state.json instead. A failed or
interrupted upgrade leaves a directory with that in-progress marker and no
completion record; it is kept for inspection but is not a restorable backup.

During the upgrade:
- running chat replies and generation jobs are interrupted; retry them later
- uploads in progress fail; upload them again
- unsaved drafts must be saved before upgrading
- open pages reconnect and fetch a fresh snapshot; review drafts before saving

Rollback
--------
Program only (data unchanged):

  powershell -NoProfile -ExecutionPolicy Bypass -File rollback-web.ps1 `
    -Version <previous version>

Restore the pre-upgrade data as well:

  powershell -NoProfile -ExecutionPolicy Bypass -File rollback-web.ps1 `
    -Version <previous version> `
    -RestoreData <install root>\backups\data-<timestamp> `
    -ConfirmDataLoss

Restoring data returns to the backup moment. Every chat message, job, upload
and project write made after that backup is no longer served. The current data
directory is moved to backups\data-before-rollback-<timestamp> first, so those
files can still be inspected. A program-only rollback across a declared data
format change is refused unless -ForceIncompatibleData is passed explicitly.

-RestoreData verifies the backup before the service is stopped or any data is
moved. It refuses an empty directory, a directory without backup-complete.json,
a directory that still carries the in-progress marker, a backup with missing
files, and a backup whose file sizes or SHA-256 hashes do not match the record.
Older backups made before completion records existed have no marker and are
therefore refused, not assumed complete; inspect them manually if needed.
Data-before-rollback-<timestamp> directories are raw move-aside copies for
inspection, not marked backups, so -RestoreData does not accept them.

Never copy data\, config\ai-key.bin or backups\ into a new program directory
by hand. If the encryption key is lost, existing model and media keys cannot be
decrypted and must be entered again.
