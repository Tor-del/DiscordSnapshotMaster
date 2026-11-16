from contextlib import closing
import http.client
from http.cookies import SimpleCookie
from http.server import ThreadingHTTPServer
import json
from os import environ
from pathlib import Path
import re
import sqlite3
import sys
from tempfile import TemporaryDirectory
from threading import Thread
import unittest
from unittest.mock import patch
from urllib.parse import parse_qs, urlencode, urlsplit


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, environ.get("DISCORD_VIEWER_SOURCE", str(ROOT / "web-ui")))
import auth
import server

SCHEMA_SOURCE = Path(environ.get("DISCORD_ARCHIVE_SCHEMA_SOURCE", str(ROOT / "core/ArchiveStore.cs")))
ASSET = "1" * 64
SHARED = "2" * 64


class ViewerAuthorizationTests(unittest.TestCase):
    def setUp(self):
        folder = TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        self.archive = Path(folder.name)
        self.app = ThreadingHTTPServer(("127.0.0.1", 0), server.ArchiveHandler)
        self.app.archive = self.archive
        self.app.auth = auth.Authorization()
        self.app.allowed_hosts = {"127.0.0.1"}
        Thread(target=self.app.serve_forever, kwargs={"poll_interval": 0.01}, daemon=True).start()
        self.addCleanup(self.app.server_close)
        self.addCleanup(self.app.shutdown)
        self.config = self.archive / auth.CONFIG_FILE
        self.settings = {
            "clientId": "1234", "clientSecret": "test-secret",
            "redirectUri": f"http://127.0.0.1:{self.app.server_port}/auth/callback",
            "users": [{"id": "100", "admin": True}, {"id": "101", "admin": False}],
            "guilds": ["200"],
        }
        self.config.write_text(json.dumps(self.settings))
        self.database = self.archive / "snapshot.sqlite"
        with closing(sqlite3.connect(self.database)) as db, db:
            schema = re.search(r'private const string Schema = """(.*?)"""', SCHEMA_SOURCE.read_text(), re.S)[1]
            db.executescript(schema)
            db.executescript("""
                PRAGMA user_version = 2;
                PRAGMA foreign_keys = ON;
                INSERT INTO snapshot (id, guild_id, cutoff, started_at, data, copy_id)
                VALUES (1, '200', '90', '2026-11-16T12:00:00Z', '{"name":"Archive"}', 'copy');
                INSERT INTO channels (id, parent_id, name, type, data, selected, cursor, history_done, pending_copy)
                VALUES ('3', NULL, 'Channel', 0, '{}', 1, '0', 1, 0),
                       ('4', '3', 'Thread', 11, '{}', 0, '0', 1, 0),
                       ('8', NULL, 'Survivor', 0, '{}', 1, '0', 1, 0);
                INSERT INTO messages (id, channel_id, timestamp, content, data)
                VALUES ('10', '3', '2026-11-16T12:00:00Z', 'Delete this', '{}'),
                       ('11', '4', '2026-11-16T12:00:00Z', 'Delete this thread', '{}'),
                       ('12', '8', '2026-11-16T12:00:00Z', 'Keep this', '{"author":{"id":"602","username":"Reader"}}');
            """)
            db.executemany("INSERT INTO assets (id, kind, url, path, sha256, channel_id, message_id) VALUES (?, 'attachment', '', ?, 'saved', '4', '11')",
                           [(key, f"media/{key}.png") for key in (ASSET, SHARED)])
            db.executemany("INSERT INTO message_assets VALUES (?, ?)", [("11", ASSET), ("11", SHARED), ("12", SHARED)])
        (self.archive / "media").mkdir()
        for key in (ASSET, SHARED):
            (self.archive / f"media/{key}.png").write_bytes(b"test image")

    def request(self, path, cookie=None, *, data=None, csrf=None, host=None):
        headers = {}
        if cookie:
            headers["Cookie"] = cookie
        if host:
            headers["Host"] = host
        if data is not None:
            headers["Content-Type"] = "application/json"
        if csrf:
            headers["X-CSRF-Token"] = csrf
        connection = http.client.HTTPConnection("127.0.0.1", self.app.server_port, timeout=3)
        try:
            connection.request("GET" if data is None else "POST", path, None if data is None else json.dumps(data), headers)
            response = connection.getresponse()
            body = response.read()
            if response.headers.get_content_type() == "application/json":
                body = json.loads(body)
            return response.status, response.headers, body
        finally:
            connection.close()

    def login(self, user, guilds=(), allowed=True):
        code, headers, _ = self.request("/auth/login")
        self.assertEqual(code, 303)
        target = urlsplit(headers["Location"])
        params = parse_qs(target.query)
        self.assertEqual(target.netloc, "discord.com")
        self.assertEqual(params["scope"], ["identify guilds"])
        callback = "/auth/callback?" + urlencode({"state": params["state"][0], "code": "example", "iss": auth.DISCORD})
        self.assertEqual(self.request(callback, "snapshot_oauth=wrong")[0], 400)
        cookie = headers["Set-Cookie"].split(";", 1)[0]
        with patch("server.verify_discord_user", return_value=(user, guilds)):
            status, headers, _ = self.request(callback, cookie)
        self.assertEqual(status, 303)
        self.assertEqual(self.request(callback, cookie)[0], 400)
        self.assertEqual(headers["Location"], "/" if allowed else "/?auth=denied")
        if allowed:
            session = SimpleCookie()
            for header in headers.get_all("Set-Cookie"):
                session.load(header)
            return f"snapshot_session={session['snapshot_session'].value}"
        return None

    def test_public_compatibility_and_fail_closed(self):
        self.config.unlink()
        self.assertEqual(self.request("/api/snapshot")[0], 200)
        self.assertEqual(self.request("/media/" + ASSET)[0], 200)
        self.config.write_text("{}")
        for route in ("/api/snapshot", "/api/messages?channel=3", "/media/" + ASSET):
            self.assertEqual(self.request(route)[0], 503)

    def test_project_name_follows_manifest(self):
        manifest = self.archive / "project.json"
        viewer = self.login("101")
        self.assertIsNone(self.request("/api/snapshot", viewer)[2]["projectName"])
        manifest.write_text(json.dumps({"Name": "Архив & копия", "Settings": {"Token": "test-token"}}), encoding="utf-8")
        status, _, snapshot = self.request("/api/snapshot", viewer)
        self.assertEqual(status, 200)
        self.assertEqual(snapshot["projectName"], "Архив & копия")
        self.assertNotIn("test-token", json.dumps(snapshot))
        self.assertEqual(self.request("/project.json", viewer)[0], 404)
        manifest.write_text('{"Name":"Renamed"}', encoding="utf-8")
        self.assertEqual(self.request("/api/snapshot", viewer)[2]["projectName"], "Renamed")

    def test_permissions_and_deletion(self):
        for route in ("/api/snapshot", "/api/messages?channel=3", "/media/" + ASSET):
            self.assertEqual(self.request(route)[0], 401)
        self.assertEqual(self.request("/" + auth.CONFIG_FILE)[0], 404)
        self.assertEqual(self.request("/api/session", host="127.0.0.1:1")[0], 403)

        admin = self.login("100")
        viewer = self.login("101")
        guild_viewer = self.login("102", {"200"})
        self.login("103", {"201"}, allowed=False)
        session = self.request("/api/session", admin)[2]
        csrf = session["csrf"]
        self.assertTrue(session["admin"])
        self.assertEqual(self.request("/api/snapshot", guild_viewer)[0], 200)
        viewer_csrf = self.request("/api/session", viewer)[2]["csrf"]
        self.assertEqual(self.request("/api/admin/channel", viewer, data={"channel": "3"}, csrf=viewer_csrf)[0], 403)
        self.assertEqual(self.request("/api/admin/access", admin, data={"adminsOnly": True})[0], 403)
        self.assertEqual(self.request("/api/admin/access", admin, csrf=csrf, data={"adminsOnly": True})[0], 200)
        self.assertEqual(self.request("/api/messages?channel=3", viewer)[0], 403)
        self.assertEqual(self.request("/media/" + ASSET, guild_viewer)[0], 403)

        self.assertEqual(self.request("/api/admin/channel", admin, csrf=csrf, data={"channel": "3"})[0], 200)
        self.assertEqual(self.request("/api/admin/channel", admin, csrf=csrf, data={"channel": "3"})[0], 404)
        with closing(sqlite3.connect(self.database)) as db:
            self.assertEqual(db.execute("SELECT id FROM channels").fetchall(), [("8",)])
            self.assertEqual(db.execute("SELECT id FROM messages").fetchall(), [("12",)])
            self.assertEqual(db.execute("SELECT id, channel_id, message_id FROM assets").fetchall(), [(SHARED, "8", "12")])
            self.assertEqual(db.execute("PRAGMA foreign_key_check").fetchall(), [])
        self.assertEqual(self.request("/media/" + ASSET, admin)[0], 404)
        self.assertEqual(self.request("/media/" + SHARED, admin)[0], 200)

        self.assertEqual(self.request("/api/admin/access", admin, csrf=csrf, data={"adminsOnly": False})[0], 200)
        self.assertEqual(self.request("/api/snapshot", viewer)[0], 200)
        self.assertEqual(self.request("/auth/logout", admin, csrf=csrf, data={})[0], 200)
        self.assertEqual(self.request("/api/snapshot", admin)[0], 401)

    def test_discord_exchange(self):
        settings = auth.read_settings(self.archive)
        with patch("auth.discord_request", side_effect=[{"access_token": "example"}, {"id": "102"}, [{"id": "200"}]]) as discord:
            self.assertEqual(auth.verify_discord_user(settings, "code"), ("102", {"200"}))
        self.assertEqual(discord.call_args_list[0].args, ("/oauth2/token",))
        self.assertEqual(discord.call_args_list[0].kwargs["form"]["redirect_uri"], settings.redirect)
        self.assertEqual(discord.call_args_list[1].args, ("/users/@me",))
        self.assertEqual(discord.call_args_list[2].args, ("/users/@me/guilds?limit=200",))

    def test_channel_visibility_and_access_management(self):
        admin = self.login("100")
        csrf = self.request("/api/session", admin)[2]["csrf"]
        viewer = self.login("101")
        via_guild = self.login("102", {"200"})
        self.assertEqual(self.request("/api/admin/members", viewer)[0], 403)
        access = self.request("/api/admin/members", admin)[2]
        self.assertNotIn("clientSecret", access)
        self.assertEqual(access["guilds"], ["200"])
        self.assertEqual(self.request("/api/admin/members", admin, csrf=csrf,
                                      data={"kind": "user", "id": "100", "remove": True})[0], 409)
        for change in (
            {"kind": "user", "id": "105", "remove": False, "admin": False},
            {"kind": "guild", "id": "204", "remove": False},
            {"kind": "guild", "id": "200", "remove": True},
        ):
            self.assertEqual(self.request("/api/admin/members", admin, csrf=csrf, data=change)[0], 200)
        self.assertEqual(self.request("/api/snapshot", via_guild)[0], 401)
        self.assertEqual(self.request("/api/snapshot", self.login("105"))[0], 200)
        self.assertEqual(self.request("/api/snapshot", self.login("104", {"204"}))[0], 200)

        self.assertEqual(self.request("/api/admin/visibility", admin, csrf=csrf, data={"channel": "3", "hidden": True})[0], 200)
        self.assertEqual([ch["id"] for ch in self.request("/api/snapshot", viewer)[2]["channels"]], ["8"])
        self.assertEqual([ch["id"] for ch in self.request("/api/snapshot", admin)[2]["channels"] if ch["hidden"]], ["3", "4"])
        self.assertEqual(self.request("/api/messages?channel=4", viewer)[0], 404)
        self.assertEqual(self.request("/api/messages?channel=4", admin)[0], 200)
        self.assertEqual(self.request("/media/" + ASSET, viewer)[0], 404)
        self.assertEqual(self.request("/media/" + SHARED, viewer)[0], 200)
        self.assertEqual(self.request("/api/messages?channel=8", viewer)[2]["messages"][0]["authorId"], "602")
        self.assertEqual(self.request("/api/admin/visibility", admin, csrf=csrf, data={"channel": "3", "hidden": False})[0], 200)
        self.assertEqual(self.request("/api/messages?channel=4", viewer)[0], 200)
        self.assertEqual(self.request("/api/admin/members", admin, csrf=csrf,
                                      data={"kind": "user", "id": "101", "remove": True})[0], 200)
        self.assertEqual(self.request("/api/messages?channel=4", viewer)[0], 401)


if __name__ == "__main__":
    unittest.main()
