from dataclasses import dataclass, replace
from hashlib import sha256
from http.cookies import CookieError, SimpleCookie
import json
import os
import re
from secrets import compare_digest, token_urlsafe
from threading import Lock
from tempfile import NamedTemporaryFile
from time import monotonic
from urllib.error import URLError
from urllib.parse import urlencode, urlsplit
from urllib.request import HTTPRedirectHandler, Request, build_opener


CONFIG_FILE = ".discord-auth.json"
DISCORD = "https://discord.com"
FLOW_SECONDS = 600
SESSION_SECONDS = 3600
MAX_ENTRIES = 4096


class RequestError(Exception):
    def __init__(self, status, message):
        self.status = status
        super().__init__(message)


def discord_id(value):
    return isinstance(value, str) and re.fullmatch(r"[1-9][0-9]{0,19}", value) and int(value) < 2**64


@dataclass(frozen=True)
class Settings:
    client_id: str
    secret: str
    redirect: str
    authority: str
    secure: bool
    users: dict
    guilds: frozenset
    session_version: str | None
    revision: bytes

    def accepts_host(self, host):
        # Browsers omit the default port in Host, even if the redirect URI includes it.
        suffix = ":443" if self.secure else ":80"
        return host.lower().removesuffix(suffix) == self.authority.removesuffix(suffix)

    def cookie(self, name, value, age):
        prefix = "__Host-" if self.secure else ""
        secure = "; Secure" if self.secure else ""
        return f"{prefix}snapshot_{name}={value}; Path=/; HttpOnly; SameSite=Lax; Max-Age={age}{secure}"

    def cookie_value(self, name, header):
        prefix = "__Host-" if self.secure else ""
        try:
            item = SimpleCookie(header).get(f"{prefix}snapshot_{name}")
            return item.value if item else ""
        except CookieError:
            return ""


def read_settings(archive):
    path = archive / CONFIG_FILE
    try:
        with path.open("rb") as source:
            raw = source.read(1024 * 1024 + 1)
    except FileNotFoundError:
        if not path.is_symlink():
            return None
        raise RequestError(503, "auth_config")
    except OSError:
        raise RequestError(503, "auth_config") from None
    try:
        if len(raw) > 1024 * 1024:
            raise ValueError
        data = json.loads(raw)
        client_id, secret, redirect = data["clientId"], data["clientSecret"], data["redirectUri"]
        uri = urlsplit(redirect)
        loopback = uri.hostname in ("localhost", "127.0.0.1")
        if (not discord_id(client_id) or not isinstance(secret, str) or not secret.strip()
                or (uri.scheme != "https" and not (uri.scheme == "http" and loopback))
                or not re.fullmatch(r"[a-zA-Z0-9.-]+(?::[0-9]{1,5})?", uri.netloc)
                or uri.port == 0 or uri.path != "/auth/callback" or uri.query or uri.fragment
                or redirect != f"{uri.scheme}://{uri.netloc}{uri.path}"):
            raise ValueError
        users = {user["id"]: user["admin"] for user in data["users"]}
        guilds = frozenset(data["guilds"])
        if (not isinstance(data["users"], list) or not isinstance(data["guilds"], list)
                or len(users) != len(data["users"]) or not (users or guilds)
                or any(not discord_id(user) or type(admin) is not bool for user, admin in users.items())
                or any(not discord_id(guild) for guild in guilds)):
            raise ValueError
        session_version = data.get("sessionVersion")
        if session_version is not None and not isinstance(session_version, str):
            raise ValueError
        revision = sha256(json.dumps([client_id, secret, redirect, session_version]).encode()).digest()
        return Settings(client_id, secret, redirect, uri.netloc.lower(), uri.scheme == "https", users, guilds, session_version, revision)
    except (TypeError, ValueError, KeyError, AttributeError):
        raise RequestError(503, "auth_config") from None


@dataclass(frozen=True)
class Session:
    user: str
    guilds: frozenset
    admin: bool
    csrf: str
    revision: bytes
    expires: float


class Authorization:
    def __init__(self):
        self.flows = {}
        self.sessions = {}
        self.lock = Lock()

    @staticmethod
    def prune(entries, now):
        for key in [key for key, record in entries.items() if record.expires <= now]:
            del entries[key]
        if len(entries) >= MAX_ENTRIES:
            raise RequestError(429, "auth_busy")

    def begin(self, settings):
        state, browser = token_urlsafe(32), token_urlsafe(32)
        now = monotonic()
        with self.lock:
            self.prune(self.flows, now)
            self.flows[state] = Flow(browser.encode(), settings.revision, now + FLOW_SECONDS)
        params = urlencode({
            "client_id": settings.client_id, "response_type": "code", "redirect_uri": settings.redirect,
            "scope": "identify guilds" if settings.guilds else "identify", "state": state,
        })
        return f"{DISCORD}/oauth2/authorize?{params}", settings.cookie("oauth", browser, FLOW_SECONDS)

    def consume_flow(self, settings, query, browser):
        states = query.get("state", [])
        if len(states) != 1:
            raise RequestError(400, "auth_state")
        state = states[0]
        with self.lock:
            flow = self.flows.get(state)
            if (not flow or flow.expires <= monotonic() or flow.revision != settings.revision
                    or not compare_digest(flow.browser, browser.encode())):
                raise RequestError(400, "auth_state")
            del self.flows[state]
        if query.get("iss", [DISCORD]) != [DISCORD]:
            raise RequestError(400, "auth_state")

    def create_session(self, settings, user, guilds, old_session):
        if user not in settings.users and not settings.guilds.intersection(guilds):
            return None
        key = token_urlsafe(32)
        now = monotonic()
        with self.lock:
            self.sessions.pop(old_session, None)
            self.prune(self.sessions, now)
            self.sessions[key] = Session(user, frozenset(guilds), settings.users.get(user, False), token_urlsafe(32), settings.revision, now + SESSION_SECONDS)
        return settings.cookie("session", key, SESSION_SECONDS)

    def session(self, settings, key):
        with self.lock:
            session = self.sessions.get(key)
            if session and session.expires > monotonic() and session.revision == settings.revision:
                if session.user in settings.users or settings.guilds.intersection(session.guilds):
                    return replace(session, admin=settings.users.get(session.user, False))
        return None

    def logout(self, key):
        with self.lock:
            self.sessions.pop(key, None)

    def change_access(self, archive, key, payload):
        # Re-read the policy while serializing edits made by this viewer.
        with self.lock:
            settings = read_settings(archive)
            session = self.sessions.get(key)
            if (settings is None or session is None or session.revision != settings.revision
                    or session.expires <= monotonic() or not settings.users.get(session.user)):
                raise RequestError(403, "admin_required")
            users, guilds = dict(settings.users), set(settings.guilds)
            kind, entry = payload.get("kind"), payload.get("id")
            if kind not in ("user", "guild") or not discord_id(entry) or type(payload.get("remove")) is not bool:
                raise RequestError(400, "invalid_action")
            if kind == "guild":
                (guilds.discard if payload["remove"] else guilds.add)(entry)
            elif payload["remove"]:
                users.pop(entry, None)
            elif type(payload.get("admin")) is bool:
                users[entry] = payload["admin"]
            else:
                raise RequestError(400, "invalid_action")
            if not any(users.values()):
                raise RequestError(409, "last_admin")
            path = archive / CONFIG_FILE
            backup = {
                "clientId": settings.client_id, "clientSecret": settings.secret, "redirectUri": settings.redirect,
                "users": [{"id": user, "admin": admin} for user, admin in sorted(users.items())],
                "guilds": sorted(guilds),
                "sessionVersion": settings.session_version,
            }
            temporary = None
            try:
                with NamedTemporaryFile(mode="w", encoding="utf-8", dir=archive, prefix=CONFIG_FILE + ".", delete=False) as file:
                    temporary = file.name
                    json.dump(backup, file, indent=2)
                    file.flush()
                    os.fsync(file.fileno())
                os.replace(temporary, path)
            except OSError:
                raise RequestError(503, "archive_read_only") from None
            finally:
                if temporary and os.path.exists(temporary):
                    os.unlink(temporary)


@dataclass(frozen=True)
class Flow:
    browser: bytes
    revision: bytes
    expires: float


class RejectRedirects(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def discord_request(path, *, form=None, token=None):
    headers = {"Accept": "application/json", "User-Agent": "Discord Snapshot Archive Viewer"}
    if form is not None:
        headers["Content-Type"] = "application/x-www-form-urlencoded"
    if token is not None:
        headers["Authorization"] = f"Bearer {token}"
    request = Request(DISCORD + "/api/v10" + path, urlencode(form).encode() if form is not None else None, headers)
    with build_opener(RejectRedirects()).open(request, timeout=10) as response:
        return json.loads(response.read(1024 * 1024))


def verify_discord_user(settings, code):
    try:
        grant = discord_request("/oauth2/token", form={
            "grant_type": "authorization_code", "code": code, "redirect_uri": settings.redirect,
            "client_id": settings.client_id, "client_secret": settings.secret,
        })
        token = grant["access_token"]
        user = discord_request("/users/@me", token=token)["id"]
        if not discord_id(user):
            raise ValueError
        guilds = set()
        if settings.guilds:
            guilds = {guild["id"] for guild in discord_request("/users/@me/guilds?limit=200", token=token)}
        return user, guilds
    except (URLError, OSError, ValueError, TypeError, KeyError):
        raise RequestError(502, "auth_discord") from None
