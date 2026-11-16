from auth import RequestError


HIDDEN_TREE = """
    WITH RECURSIVE hidden(id) AS (
      SELECT channel_id FROM viewer_hidden
      UNION
      SELECT c.id FROM channels c JOIN hidden h ON c.parent_id = h.id
    )
"""


def hidden_channels(db):
    if not db.execute("SELECT 1 FROM sqlite_master WHERE name = 'viewer_hidden' AND type = 'table'").fetchone():
        return set(), set()
    direct = {row[0] for row in db.execute("SELECT channel_id FROM viewer_hidden")}
    return direct, {row[0] for row in db.execute(HIDDEN_TREE + "SELECT id FROM hidden")}


def set_channel_hidden(db, channel, hidden):
    if not db.execute("SELECT 1 FROM channels WHERE id = ?", (channel,)).fetchone():
        raise RequestError(404, "channel_missing")
    db.execute("""
        CREATE TABLE IF NOT EXISTS viewer_hidden (
          channel_id TEXT PRIMARY KEY REFERENCES channels(id) ON DELETE CASCADE
        )
    """)
    if hidden:
        db.execute("INSERT OR IGNORE INTO viewer_hidden VALUES (?)", (channel,))
    else:
        db.execute("DELETE FROM viewer_hidden WHERE channel_id = ?", (channel,))


def visible_asset(db, asset):
    return db.execute(HIDDEN_TREE + """
        SELECT 1 FROM snapshot WHERE icon_id = ?
        UNION ALL
        SELECT 1 FROM message_assets a JOIN messages m ON m.id = a.message_id
        WHERE a.asset_id = ? AND m.channel_id NOT IN (SELECT id FROM hidden)
        UNION ALL
        SELECT 1 FROM profiles p JOIN messages m ON m.author_id = p.id
        WHERE p.guild_avatar_id = ? AND m.channel_id NOT IN (SELECT id FROM hidden)
        LIMIT 1
    """, (asset, asset, asset)).fetchone() is not None


def admins_only(db):
    exists = db.execute("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'viewer_access'").fetchone()
    if not exists:
        return False
    row = db.execute("SELECT admins_only FROM viewer_access WHERE id = 1").fetchone()
    return bool(row and row[0])


def restrict_to_admins(db, restricted):
    db.execute("""
        CREATE TABLE IF NOT EXISTS viewer_access (
          id INTEGER PRIMARY KEY CHECK (id = 1),
          admins_only INTEGER NOT NULL CHECK (admins_only IN (0, 1))
        )
    """)
    db.execute("""
        INSERT INTO viewer_access VALUES (1, ?)
        ON CONFLICT(id) DO UPDATE SET admins_only = excluded.admins_only
    """, (restricted,))


def delete_channel(db, channel):
    db.execute("CREATE TEMP TABLE removed_channels (id TEXT PRIMARY KEY)")
    db.execute("""
        INSERT INTO removed_channels
        WITH RECURSIVE subtree(id) AS (
          SELECT id FROM channels WHERE id = ?
          UNION
          SELECT c.id FROM channels c JOIN subtree s ON c.parent_id = s.id
        )
        SELECT id FROM subtree
    """, (channel,))
    if not db.execute("SELECT 1 FROM removed_channels LIMIT 1").fetchone():
        raise RequestError(404, "channel_missing")

    db.execute("CREATE TEMP TABLE removed_messages (id TEXT PRIMARY KEY)")
    db.execute("""
        INSERT INTO removed_messages
        SELECT id FROM messages WHERE channel_id IN (SELECT id FROM removed_channels)
    """)
    db.execute("CREATE TEMP TABLE affected_assets (id TEXT PRIMARY KEY)")
    db.execute("""
        INSERT INTO affected_assets
        SELECT asset_id FROM message_assets WHERE message_id IN (SELECT id FROM removed_messages)
        UNION
        SELECT id FROM assets WHERE channel_id IN (SELECT id FROM removed_channels)
    """)
    db.execute("DELETE FROM message_assets WHERE message_id IN (SELECT id FROM removed_messages)")
    db.execute("DELETE FROM messages WHERE id IN (SELECT id FROM removed_messages)")
    db.execute("DELETE FROM channels WHERE id IN (SELECT id FROM removed_channels)")

    # Shared files retain a live message as their download source, when one exists.
    db.execute("CREATE TEMP TABLE surviving_sources (asset_id TEXT PRIMARY KEY, message_id TEXT)")
    db.execute("""
        INSERT INTO surviving_sources
        SELECT ma.asset_id, min(ma.message_id)
        FROM message_assets ma JOIN affected_assets a ON a.id = ma.asset_id GROUP BY ma.asset_id
    """)
    db.execute("""
        DELETE FROM assets WHERE id IN (SELECT id FROM affected_assets)
          AND id NOT IN (SELECT asset_id FROM surviving_sources)
          AND NOT EXISTS (SELECT 1 FROM snapshot s WHERE s.icon_id = assets.id)
          AND NOT EXISTS (SELECT 1 FROM profiles p WHERE p.avatar_id = assets.id OR p.guild_avatar_id = assets.id)
    """)
    db.execute("""
        UPDATE assets SET (channel_id, message_id) = (
          SELECT m.channel_id, m.id FROM surviving_sources s JOIN messages m ON m.id = s.message_id
          WHERE s.asset_id = assets.id
        ) WHERE channel_id IN (SELECT id FROM removed_channels)
    """)
