-- One row per published DMG. The newest published_at is what /download serves.
CREATE TABLE releases (
  version      TEXT PRIMARY KEY,   -- CFBundleShortVersionString, e.g. 0.1.0
  path         TEXT NOT NULL,      -- static asset path, e.g. /releases/Morphy-0.1.0.dmg
  sha256       TEXT NOT NULL,
  bytes        INTEGER NOT NULL,
  published_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now'))
);
