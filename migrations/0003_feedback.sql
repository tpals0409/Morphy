-- Reply addresses from the website contact form. The issue itself is public on GitHub; this stays private.
CREATE TABLE feedback (
  issue_number INTEGER NOT NULL,
  email        TEXT NOT NULL,
  created_at   TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now'))
);
