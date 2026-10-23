-- Mac and Windows share the table; the same version number can exist once per platform.
CREATE TABLE releases_new (
  platform     TEXT NOT NULL CHECK (platform IN ('mac', 'windows')),
  version      TEXT NOT NULL,
  path         TEXT NOT NULL,      -- R2 key with a leading slash, e.g. /releases/Morphy-0.1.0.dmg
  sha256       TEXT NOT NULL,
  bytes        INTEGER NOT NULL,
  published_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
  PRIMARY KEY (platform, version)
);
INSERT INTO releases_new (platform, version, path, sha256, bytes, published_at)
  SELECT 'mac', version, path, sha256, bytes, published_at FROM releases;
DROP TABLE releases;
ALTER TABLE releases_new RENAME TO releases;
