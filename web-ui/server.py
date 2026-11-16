import argparse
from contextlib import closing
from hashlib import sha256
from html import escape
import json
import logging
from pathlib import Path
import re
from secrets import compare_digest
import sqlite3
import sys
from threading import Thread
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlsplit
from zipfile import ZipFile


HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))  # The desktop viewer launches Python in isolated mode.
from auth import Authorization, RequestError, discord_id, read_settings, verify_discord_user
from archive_admin import admins_only, delete_channel, hidden_channels, restrict_to_admins, set_channel_hidden, visible_asset

PAGE_SIZE = 60
STATIC = {
    "/": ("index.html", "text/html; charset=utf-8"),
    "/app.js": ("app.js", "text/javascript; charset=utf-8"),
    "/style.css": ("style.css", "text/css; charset=utf-8"),
}
MEDIA_TYPES = {
    ".png": "image/png", ".jpg": "image/jpeg", ".jpeg": "image/jpeg",
    ".gif": "image/gif", ".webp": "image/webp", ".avif": "image/avif",
    ".mp4": "video/mp4", ".webm": "video/webm", ".mp3": "audio/mpeg",
    ".ogg": "audio/ogg", ".wav": "audio/wav", ".m4a": "audio/mp4",
}
ASSET_PATH = re.compile(r"media/[a-f0-9]{64}(\.[a-z0-9]{1,8})?")
CUSTOM_EMOJI_URL = re.compile(r"https://cdn\.discordapp\.com/emojis/([0-9]{1,20})\.(gif|webp)")
TWEMOJI_PATH = re.compile(r"/emoji/([a-f0-9]{1,6}(?:-[a-f0-9]{1,6}){0,31}\.png)")

MESSAGES = {
    "no_snapshot": ("The snapshot has not been created yet.", "Снимок ещё не создан."),
    "select_channel": ("Select a channel.", "Укажите канал."),
    "page_unavailable": ("This page of messages is unavailable.", "Страница сообщений недоступна."),
    "attachment": ("Attachment", "Вложение"),
    "message_media": ("Media from message", "Медиа из сообщения"),
    "unknown_author": ("Unknown author", "Неизвестный автор"),
    "host_forbidden": ("This address is not allowed to serve the archive.", "Этот адрес не разрешён для просмотра архива."),
    "page_not_found": ("Page not found.", "Страница не найдена."),
    "snapshot_unavailable": ("The snapshot is temporarily unavailable. If copying is in progress, pause it and refresh the page.", "Снимок временно недоступен. Если идёт копирование, поставьте его на паузу и обновите страницу."),
    "archive_read_failed": ("Could not read the archive.", "Не удалось прочитать архив."),
    "start_snapshot": ("Create a snapshot in APEX Discord Snapshot Master first.", "Сначала создайте снимок в APEX Discord Snapshot Master."),
    "snapshot_format": ("The snapshot is still being created or uses an unsupported format.", "Снимок ещё создаётся или имеет неподдерживаемый формат."),
    "file_not_found": ("The file is missing from the archive.", "Файл отсутствует в архиве."),
    "auth_config": ("Archive authorization needs valid Discord OAuth2 settings.", "Для архива нужны действительные настройки Discord OAuth2."),
    "auth_required": ("Sign in with Discord to open the archive.", "Войдите через Discord, чтобы открыть архив."),
    "auth_origin": ("Open the archive at the address configured in the Discord Redirect URI.", "Откройте архив по адресу, указанному в Redirect URI Discord."),
    "auth_state": ("This Discord sign-in expired or is invalid. Open the archive to try again.", "Срок входа через Discord истёк или запрос недействителен. Откройте архив и повторите вход."),
    "auth_discord": ("Discord could not verify your account. Try signing in again.", "Discord не смог проверить ваш аккаунт. Попробуйте войти ещё раз."),
    "auth_busy": ("Too many active sign-ins. Try again later.", "Слишком много активных входов. Повторите попытку позже."),
    "admin_only": ("The archive is open to its administrators only.", "Архив открыт только для администраторов."),
    "admin_required": ("This action requires an archive administrator.", "Это действие доступно администратору архива."),
    "invalid_action": ("The request is invalid. Reload the archive and try again.", "Запрос недействителен. Обновите страницу и повторите действие."),
    "channel_missing": ("That channel or thread has already been removed.", "Этот канал или ветка уже удалены."),
    "archive_read_only": ("The viewer needs write access to snapshot.sqlite and its directory.", "Просмотрщику нужен доступ на запись в snapshot.sqlite и его папку."),
    "last_admin": ("Keep at least one archive administrator.", "Оставьте хотя бы одного администратора архива."),
}


def translate(language, key):
    return MESSAGES[key][language == "ru"]


def project_name(archive):
    try:
        manifest = json.loads((archive / "project.json").read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None
    name = manifest.get("Name") if isinstance(manifest, dict) else None
    return (name.strip() or None) if isinstance(name, str) else None


def read_snapshot(db, direct_hidden=(), hidden=(), admin=False):
    row = db.execute("""
        SELECT s.guild_id, s.started_at, s.data,
               (SELECT id FROM assets WHERE id = s.icon_id AND sha256 IS NOT NULL) AS icon,
               EXISTS(SELECT 1 FROM channels WHERE pending_copy = 1 OR history_done = 0 OR discovery_done = 0)
               OR EXISTS(SELECT 1 FROM assets WHERE sha256 IS NULL) AS incomplete
        FROM snapshot s WHERE s.id = 1
    """).fetchone()
    if row is None:
        raise RequestError(503, "no_snapshot")
    return {
        "name": json.loads(row["data"]).get("name") or row["guild_id"],
        "startedAt": row["started_at"], "icon": row["icon"],
        "incomplete": bool(row["incomplete"]),
        "categories": [dict(item) for item in db.execute("SELECT id, name FROM categories ORDER BY json_extract(data, '$.position'), id")],
        "channels": [dict(item, **({"hidden": item["id"] in hidden, "hiddenDirectly": item["id"] in direct_hidden} if admin else {})) for item in db.execute("""
            SELECT id, parent_id, name, type FROM channels
            ORDER BY json_extract(data, '$.position'), length(id), id
        """) if admin or item["id"] not in hidden],
    }


def read_messages(db, query, language="en"):
    def first(key):
        return query.get(key, [""])[0]

    channel, search, before = first("channel"), first("q").strip()[:200], first("before")
    if not re.fullmatch(r"[0-9]{1,20}", channel):
        raise RequestError(400, "select_channel")
    cursor = db.execute("SELECT timestamp, id FROM messages WHERE channel_id = ? AND id = ?", (channel, before)).fetchone()
    if before and cursor is None:
        raise RequestError(400, "page_unavailable")

    conditions = ["m.channel_id = ?"]
    params = [channel]
    if search:
        # SQLite lower() only handles ASCII. Case folding also supports Cyrillic.
        db.create_function("fold", 1, lambda value: (value or "").casefold(), deterministic=True)
        conditions.append("instr(fold(m.content), ?) > 0")
        params.append(search.casefold())
    if cursor:
        conditions.append("(m.timestamp, m.id) < (?, ?)")
        params.extend(cursor)
    params.append(PAGE_SIZE + 1)
    rows = db.execute(f"""
        SELECT m.id, m.timestamp, m.content, m.data,
           json_extract(p.member_data, '$.nick') AS nickname,
           (SELECT a.id FROM assets a WHERE a.id = p.guild_avatar_id AND a.sha256 IS NOT NULL) AS local_avatar
        FROM messages m LEFT JOIN profiles p ON p.id = m.author_id
        WHERE {" AND ".join(conditions)}
        ORDER BY m.timestamp DESC, m.id DESC LIMIT ?
    """, params).fetchall()

    message_ids = [row["id"] for row in rows[:PAGE_SIZE]]
    media = {key: [] for key in message_ids}
    if message_ids:
        slots = ",".join("?" for _ in message_ids)
        for asset in db.execute(f"""
            SELECT ma.message_id, a.id, a.kind, a.path, a.url
            FROM message_assets ma JOIN assets a ON a.id = ma.asset_id
            WHERE ma.message_id IN ({slots}) AND a.sha256 IS NOT NULL ORDER BY a.id
        """, message_ids):
            media[asset["message_id"]].append(asset)

    messages = []
    for row in reversed(rows[:PAGE_SIZE]):
        raw = json.loads(row["data"])
        author = raw.get("author") or {}
        member = raw.get("member") or {}
        files = media[row["id"]]
        avatar = next((file["id"] for file in files if file["kind"] == "avatar"), None)
        attachments = []
        for file in raw.get("attachments", []):
            # Attachment IDs use the same key as ArchiveMedia.Asset in the importer.
            from_id = attachment_asset_id(file["id"])
            saved = next((asset for asset in files if asset["id"] == from_id), None)
            attachments.append({
                "id": from_id if saved else None, "name": file.get("filename") or translate(language, "attachment"),
                "type": MEDIA_TYPES.get(Path(saved["path"]).suffix, "") if saved else "",
            })
        for asset in files:
            if asset["kind"] in ("embed", "sticker") and (kind := MEDIA_TYPES.get(Path(asset["path"]).suffix)):
                attachments.append({"id": asset["id"], "name": translate(language, "message_media"), "type": kind, "inline": True})
        emoji_assets = {}
        for asset in files:
            if asset["kind"] == "emoji" and (emoji := CUSTOM_EMOJI_URL.fullmatch(asset["url"])):
                emoji_assets[emoji[1] + (":a" if emoji[2] == "gif" else "")] = asset["id"]
        reactions = []
        for reaction in raw.get("reactions") or []:
            emoji = reaction.get("emoji") or {}
            name = emoji.get("name")
            emoji_id = emoji.get("id")
            reactions.append({
                "name": (f":{name}:" if name else f":{emoji_id}:") if emoji_id else name or "",
                "asset": emoji_assets.get(emoji_id + (":a" if emoji.get("animated") else "")) if emoji_id else None,
                "count": reaction["count"], "burst": (reaction.get("count_details") or {}).get("burst", 0),
            })
        messages.append({
            "id": row["id"], "timestamp": row["timestamp"], "content": row["content"] or "",
            "author": member.get("nick") or row["nickname"] or author.get("global_name") or author.get("username") or translate(language, "unknown_author"),
            "authorId": author.get("id") if not raw.get("webhook_id") and discord_id(author.get("id")) else None,
            "avatar": row["local_avatar"] or avatar, "attachments": attachments,
            "emojiAssets": emoji_assets, "reactions": reactions,
        })
    return {"messages": messages, "before": message_ids[-1] if len(rows) > PAGE_SIZE else None}


def attachment_asset_id(discord_id):
    return sha256(f"attachment:{discord_id}".encode()).hexdigest()


class ArchiveHandler(BaseHTTPRequestHandler):
    timeout = 20

    def do_GET(self):
        self.dispatch(self.read_request)

    def do_POST(self):
        self.dispatch(self.write_request)

    def dispatch(self, operation):
        language = self.headers.get("Accept-Language", "").split(",", 1)[0].split(";", 1)[0].strip().lower().split("-", 1)[0]
        try:
            hosts = self.headers.get_all("Host", [])
            if len(hosts) != 1 or not re.fullmatch(r"[A-Za-z0-9.-]+(?::[0-9]+)?", hosts[0]):
                raise RequestError(403, "host_forbidden")
            host = hosts[0]
            allowed = host.split(":", 1)[0].lower() in self.server.allowed_hosts
            url = urlsplit(self.path)
            public = self.command == "GET" and (url.path in STATIC or TWEMOJI_PATH.fullmatch(url.path))
            if public and allowed:
                self.public_file(url.path)
                return
            settings = read_settings(self.server.archive)
            canonical = settings is not None and settings.accepts_host(host)
            if not allowed and not canonical:
                raise RequestError(403, "host_forbidden")
            if public:
                self.public_file(url.path)
                return
            if settings and not canonical:
                raise RequestError(403, "auth_origin")
            operation(url, settings, language)
        except RequestError as error:
            self.send_json({"error": translate(language, str(error))}, error.status)
        except (BrokenPipeError, ConnectionResetError):
            pass
        except sqlite3.OperationalError as error:
            logging.warning("SQLite: %s", error)
            code = getattr(error, "sqlite_errorcode", 0) & 0xff
            key = "archive_read_only" if self.command == "POST" and code in (8, 14) else "snapshot_unavailable"
            self.send_json({"error": translate(language, key)}, 503)
        except (OSError, ValueError, sqlite3.Error) as error:
            logging.warning("Archive: %s", error)
            self.send_json({"error": translate(language, "archive_read_failed")}, 500)

    def log_request(self, code="-", size="-"):
        # A Discord authorization code is part of the callback URL.
        self.log_message("%s %s %s", self.command, str(code), str(size))

    def public_file(self, path):
        if path in STATIC:
            name, kind = STATIC[path]
            data = (HERE / name).read_bytes()
            if path == "/" and (title := project_name(self.server.archive)):
                title_tag = f"<title>{escape(title)}</title>".encode("utf-8")
                data = data.replace(b"<title>APEX DSM</title>", title_tag, 1)
            self.send_data(data, kind)
        else:
            try:
                self.send_data(self.server.emoji.read(TWEMOJI_PATH.fullmatch(path)[1]), "image/png")
            except KeyError:
                raise RequestError(404, "file_not_found") from None

    def read_request(self, url, settings, language):
        auth = self.server.auth
        cookie = self.headers.get("Cookie", "")
        key = settings.cookie_value("session", cookie) if settings else ""
        session = auth.session(settings, key) if settings else None
        if url.path == "/api/session":
            restricted = False
            if session and self.database.is_file():
                with closing(self.open_snapshot()) as db:
                    restricted = admins_only(db)
            self.send_json({
                "enabled": settings is not None, "authenticated": session is not None,
                "admin": bool(session and session.admin), "adminsOnly": restricted,
                "csrf": session.csrf if session else None,
            })
            return
        if settings and url.path == "/auth/login":
            self.redirect(*auth.begin(settings))
            return
        if settings and url.path == "/auth/callback":
            query = parse_qs(url.query)
            auth.consume_flow(settings, query, settings.cookie_value("oauth", cookie))
            clear_flow = settings.cookie("oauth", "", 0)
            codes = query.get("code", [])
            if len(codes) != 1:
                self.redirect("/?auth=denied", clear_flow)
                return
            try:
                user, guilds = verify_discord_user(settings, codes[0])
            except RequestError:
                self.redirect("/?auth=failed", clear_flow)
                return
            session_cookie = auth.create_session(settings, user, guilds, key)
            if session_cookie:
                self.redirect("/", clear_flow, session_cookie)
            else:
                auth.logout(key)
                self.redirect("/?auth=denied", clear_flow, settings.cookie("session", "", 0))
            return
        if url.path == "/api/admin/members":
            if not session or not session.admin:
                raise RequestError(403, "admin_required")
            self.send_json({
                "users": [{"id": user, "admin": admin} for user, admin in sorted(settings.users.items())],
                "guilds": sorted(settings.guilds),
            })
            return

        media = re.fullmatch(r"/media/([a-f0-9]{64})", url.path)
        if url.path not in ("/api/snapshot", "/api/messages") and not media:
            raise RequestError(404, "page_not_found")
        if settings and not session:
            raise RequestError(401, "auth_required")
        with closing(self.open_snapshot()) as db:
            db.execute("BEGIN")
            if session and not session.admin and admins_only(db):
                raise RequestError(403, "admin_only")
            admin = bool(session and session.admin)
            direct_hidden, hidden = hidden_channels(db)
            if url.path == "/api/snapshot":
                snapshot = read_snapshot(db, direct_hidden, hidden, admin)
                snapshot["projectName"] = project_name(self.server.archive)
                self.send_json(snapshot)
            elif url.path == "/api/messages":
                query = parse_qs(url.query)
                if not admin and query.get("channel", [""])[0] in hidden:
                    raise RequestError(404, "channel_missing")
                self.send_json(read_messages(db, query, language))
            else:
                if not admin and hidden and not visible_asset(db, media[1]):
                    raise RequestError(404, "file_not_found")
                row = db.execute("SELECT path FROM assets WHERE id = ? AND sha256 IS NOT NULL", media.groups()).fetchone()
        if media:
            self.send_media(row["path"] if row else "")

    def write_request(self, url, settings, language):
        if url.path not in ("/auth/logout", "/api/admin/access", "/api/admin/channel", "/api/admin/visibility", "/api/admin/members"):
            raise RequestError(404, "page_not_found")
        key = settings.cookie_value("session", self.headers.get("Cookie", "")) if settings else ""
        session = self.server.auth.session(settings, key) if settings else None
        if not session:
            raise RequestError(401, "auth_required")
        if not compare_digest(session.csrf.encode(), self.headers.get("X-CSRF-Token", "").encode()):
            raise RequestError(403, "invalid_action")
        if url.path == "/auth/logout":
            self.server.auth.logout(key)
            self.send_json({"ok": True}, cookies=(settings.cookie("session", "", 0),))
            return
        if not session.admin:
            raise RequestError(403, "admin_required")
        lengths = self.headers.get_all("Content-Length", [])
        if (len(lengths) != 1 or not re.fullmatch(r"[0-9]{1,4}", lengths[0])
                or self.headers.get_content_type() != "application/json"):
            raise RequestError(400, "invalid_action")
        try:
            payload = json.loads(self.rfile.read(int(lengths[0])))
        except (ValueError, UnicodeError):
            raise RequestError(400, "invalid_action") from None
        if not isinstance(payload, dict):
            raise RequestError(400, "invalid_action")
        if url.path == "/api/admin/members":
            self.server.auth.change_access(self.server.archive, key, payload)
        else:
            current = read_settings(self.server.archive)
            if not current or not current.users.get(session.user) or current.revision != settings.revision:
                raise RequestError(403, "admin_required")
            with closing(self.open_snapshot(write=True)) as db, db:
                db.execute("BEGIN IMMEDIATE")
                channel = payload.get("channel")
                if url.path == "/api/admin/access" and type(payload.get("adminsOnly")) is bool:
                    restrict_to_admins(db, payload["adminsOnly"])
                elif url.path == "/api/admin/channel" and discord_id(channel):
                    delete_channel(db, channel)
                elif url.path == "/api/admin/visibility" and discord_id(channel) and type(payload.get("hidden")) is bool:
                    set_channel_hidden(db, channel, payload["hidden"])
                else:
                    raise RequestError(400, "invalid_action")
        self.send_json({"ok": True})

    @property
    def database(self):
        return self.server.archive / "snapshot.sqlite"

    def open_snapshot(self, write=False):
        database = self.database
        if not database.is_file():
            raise RequestError(503, "start_snapshot")
        db = sqlite3.connect(database.as_uri() + ("?mode=rw" if write else "?mode=ro"), uri=True, timeout=1)
        try:
            if db.execute("PRAGMA user_version").fetchone()[0] != 2:
                raise RequestError(503, "snapshot_format")
            if write:
                db.execute("PRAGMA foreign_keys = ON")
                db.execute("PRAGMA secure_delete = ON")
            db.row_factory = sqlite3.Row
            return db
        except Exception:
            db.close()
            raise

    def send_json(self, payload, status=200, cookies=()):
        self.send_data(json.dumps(payload, ensure_ascii=False).encode(), "application/json; charset=utf-8", status, cookies)

    def redirect(self, location, *cookies):
        self.start_response(303, "text/plain; charset=utf-8", 0)
        self.send_header("Location", location)
        for cookie in cookies:
            self.send_header("Set-Cookie", cookie)
        self.end_headers()

    def start_response(self, status, kind, length):
        self.send_response(status)
        self.send_header("Content-Type", kind)
        self.send_header("Content-Length", str(length))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("Referrer-Policy", "no-referrer")
        self.send_header("Content-Security-Policy", "default-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'")

    def send_data(self, data, kind, status=200, cookies=()):
        self.start_response(status, kind, len(data))
        for cookie in cookies:
            self.send_header("Set-Cookie", cookie)
        self.end_headers()
        self.wfile.write(data)

    def send_media(self, relative):
        if not ASSET_PATH.fullmatch(relative):
            raise RequestError(404, "file_not_found")
        path = (self.server.archive / relative).resolve()
        if path.parent != self.server.archive / "media" or not path.is_file():
            raise RequestError(404, "file_not_found")
        kind = MEDIA_TYPES.get(path.suffix, "application/octet-stream")
        with path.open("rb") as source:
            size = source.seek(0, 2)
            start, end = 0, size - 1
            value = self.headers.get("Range")
            if value:
                match = re.fullmatch(r"bytes=([0-9]{1,20})-([0-9]{0,20})", value)
                if match:
                    start = int(match[1])
                    end = min(int(match[2]), end) if match[2] else end
                if not match or start > end:
                    self.start_response(416, kind, 0)
                    self.send_header("Content-Range", f"bytes */{size}")
                    self.end_headers()
                    return
            remaining = end - start + 1
            self.start_response(206 if value else 200, kind, remaining)
            self.send_header("Accept-Ranges", "bytes")
            if value:
                self.send_header("Content-Range", f"bytes {start}-{end}/{size}")
            if kind == "application/octet-stream":
                self.send_header("Content-Disposition", "attachment")
            self.end_headers()
            source.seek(start)
            while remaining and (chunk := source.read(min(remaining, 65536))):
                self.wfile.write(chunk)
                remaining -= len(chunk)


def main():
    parser = argparse.ArgumentParser(description="APEX Discord Snapshot Master archive viewer")
    parser.add_argument("--archive", type=Path, default=HERE.parent)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8080)
    parser.add_argument("--allow-host", action="append", default=[], metavar="DOMAIN")
    parser.add_argument("--desktop", action="store_true", help=argparse.SUPPRESS)
    args = parser.parse_args()
    with ZipFile(HERE / "Twemoji.zip") as emoji, ThreadingHTTPServer((args.host, args.port), ArchiveHandler) as server:
        server.archive = args.archive.resolve()
        server.emoji = emoji
        server.auth = Authorization()
        server.allowed_hosts = {"127.0.0.1", "localhost", *(host.lower() for host in args.allow_host)}
        if args.desktop:
            def stop_when_app_closes():
                sys.stdin.buffer.read()
                server.shutdown()

            Thread(target=stop_when_app_closes, daemon=True).start()
            print(server.server_port, flush=True)
        else:
            print(f"APEX Discord Snapshot Master · http://{args.host}:{server.server_port}", flush=True)
        try:
            server.serve_forever(poll_interval=0.1 if args.desktop else 0.5)
        except KeyboardInterrupt:
            pass


if __name__ == "__main__":
    main()
