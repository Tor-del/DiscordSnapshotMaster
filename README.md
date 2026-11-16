# APEX DSM

APEX DSM archives Discord channel history visible to a bot into a local SQLite database. It includes a Windows desktop app, a command-line importer, and a browser viewer that can run locally or in Docker.

## Install on Windows

Download and run `APEX-Discord-Snapshot-Master-Setup-x64.exe` from [GitHub Releases](https://github.com/Tor-del/DiscordSnapshotMaster/releases). The installer includes .NET and Python, adds an **APEX DSM** shortcut to the Start menu, and offers a desktop shortcut. You can uninstall the app through Windows Settings.

To build the installer, install the .NET 10 SDK and Inno Setup 7, then run the following from the repository root.

```powershell
.\installer\Build.ps1
```

The installer appears in `dist/`. Use `-Compiler` to specify the path to `ISCC.exe`, `-Version` to set the version, and `-PythonArchive` to supply a local copy of the embeddable Python ZIP.

## Archive Discord channels

1. Add a bot to the server and grant it `View Channel` and `Read Message History` for the channels you want to archive. Voice channel chat also requires `Connect`. To include all private threads in a text channel, grant `Manage Threads`.
2. Enable **Message Content Intent** on the application's **Bot** tab. Verified apps may need approval from Discord. [Discord Developer Portal](https://discord.com/developers/applications)
3. Open **APEX DSM**, choose **Create or Open**, and select a project folder. You can reopen projects from **Recent Projects**, remove a single entry with its × button, or clear the list.
4. Enter the bot token, use the plug icon to connect, then select your server and channels. Choose **Start** and review the channel list before confirming. Filtering the list keeps the selections already made.
5. Use **Pause** and **Resume** to stop and continue an import, including after reopening the project. Reopen a completed project to add channels or fetch new messages and threads since the previous snapshot.

Run the desktop app from source with `dotnet run --project gui`. The app defaults to English and remembers the language selected in the project picker.

Projects store settings in `project.json`, message history in `snapshot.sqlite`, and attachments in `media/`. During an import, Windows DPAPI protects the saved bot token for the current account. You can supply a new token when resuming from a different Windows account or computer. The app clears the import settings after completion.

The shield icon checks the database and saved files whenever the snapshot is available and the importer is idle. Results appear in the project log. The app and viewer display Discord's Twemoji assets. [Discord Twemoji](https://github.com/discord/twemoji) · [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/)

## View an archive

Enable **Add web viewer to archive**, then use the browser icon after the import completes. The desktop app starts a server on `127.0.0.1` and opens it in your browser. The source build requires Python 3.10 or newer. The viewer shows saved reactions and displays custom emoji whose images were downloaded to the archive.

Every new archive includes `compose.yml`. Run the following from the archive folder and open [http://127.0.0.1:8080](http://127.0.0.1:8080).

```sh
docker compose up -d
```

Set `DISCORD_ARCHIVE_READ_ONLY=true` to mount the archive read-only. For a deployment that permits web administration, grant the container's configured UID and GID access to the archive files. [Viewer configuration and HTTPS](web-ui/README.md#docker)

### Discord sign-in

Enable **Add authorization** and open **Authorization settings**. In **Discord OAuth2**, enter the application's Client ID, Client Secret, and a Redirect URI registered in the Discord Developer Portal. Use `http://127.0.0.1:8080/auth/callback` for local viewing or the site's HTTPS address followed by `/auth/callback`. [Discord Developer Portal](https://discord.com/developers/applications)

Use **Users** and **Guilds** to grant access by Discord user ID or server membership. Select **Administrator** next to a user ID to allow that user to manage access, enable admin-only mode, and hide or delete channels. The settings file, `.discord-auth.json`, contains the Client Secret. Limit file access to the archive owner and the viewer process.

### Projects over SSH

Choose **Over SSH…** in the project picker. Enter the host, user, port, and absolute path, such as `/srv/discord/archive`. Windows OpenSSH uses the keys from your SSH configuration or agent. Verify the server's host key and add it to `known_hosts` before connecting.

The app works on a local copy of the database and uploads new attachments individually. Saving authorization settings synchronizes those settings immediately. **Synchronize**, import completion, and closing the project synchronize other changed files. The app checks for remote changes before replacing files through SFTP and keeps the working copy if it detects a conflict. Coordinate SSH updates with changes made in the web viewer when both can write to the same archive.

## Use the CLI

Run the CLI on Windows with the .NET 10 SDK. It prompts for the bot token, server, and channels. Use `--language ru` for Russian output.

```powershell
dotnet run --project cli -- --output "D:\Discord Archives"
dotnet run --project cli -- --resume "archives/<archive-directory>"
dotnet run --project cli -- --status "archives/<archive-directory>"
```

The default output directory is `archives/`. Press **Ctrl+C** to pause. A resumed import reads from the last saved page and retries missing downloads. Requests are sequential, with default minimum intervals of 2 seconds for the API and 1 second for CDN downloads. Use `--api-delay-ms` and `--cdn-delay-ms` to set those intervals.

The importer stores messages with their original Discord JSON, including reaction counts, along with channel and thread metadata. It downloads attachments, avatars, server icons, and custom emoji and stickers where available. The import uses a fixed upper message timestamp. Changes on Discord during the import can affect the result.

`--status` checks SQLite and the SHA-256 hashes of downloaded files and reports gaps and permissions that limited coverage. It exits with `0` for a complete archive that passes verification and `2` for an incomplete archive, gaps, or damaged files.

## Development checks

```powershell
dotnet run --project tests
dotnet build gui/DiscordSnapshot.Gui.csproj -p:TargetPlatformDisplayName=Windows
dotnet build cli/DiscordSnapshot.Cli.csproj
node --check web-ui/app.js
python -B tests/viewer_auth.py
docker compose config
```

Run the viewer against a test archive with `python web-ui/server.py --archive <archive-path>` and open [http://127.0.0.1:8080](http://127.0.0.1:8080).

## Releases and container images

Pushing a version tag in the form `vMAJOR.MINOR.PATCH` starts the [release workflow](.github/workflows/release.yml). It builds and uploads the Windows installer to GitHub Releases and runs a browser viewer smoke test before publishing the container.

The workflow publishes the version tag and `latest` to `ghcr.io/tor-del/discordsnapshotmaster` using the repository's Actions token. To allow anonymous pulls, make the GHCR package public in its package settings. To use that image, set the `image` field in the archive's `compose.yml` to `ghcr.io/tor-del/discordsnapshotmaster:latest`.

The shipped Compose file uses Docker Hub at `docker.io/tordel/discord-snapshot-web:latest`. To publish there as well, set the repository's `DOCKERHUB_USERNAME` and `DOCKERHUB_TOKEN` Actions secrets. The account must have permission to push to that repository.
