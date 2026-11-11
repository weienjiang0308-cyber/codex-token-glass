"""Read-only resolver verification against a synthetic, isolated database."""
import json
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile

exe = Path(sys.argv[1] if len(sys.argv) > 1 else "dist/CodexStatusbar.exe").resolve()
with tempfile.TemporaryDirectory(prefix="codex-visible-route-") as folder:
    database = Path(folder) / "state.sqlite"
    with sqlite3.connect(database) as connection:
        connection.execute("CREATE TABLE threads(id TEXT,name TEXT,title TEXT,rollout_path TEXT,tokens_used INTEGER,recency_at_ms INTEGER)")
        connection.executemany("INSERT INTO threads VALUES(?,?,?,?,?,?)", [
            ("a", "Visible A", "Original A", "", 10, 1),
            ("b", "Visible B", "Original B", "", 20, 999999),
            ("d1", "Duplicate", "First", "", 1, 1),
            ("d2", "Duplicate", "Second", "", 2, 999),
        ])
    connection.close()
    for title, expected in [("Visible A", "a"), ("Visible B", "b"), ("Duplicate", None), ("New chat", None), ("Original A", None)]:
        result = subprocess.run([str(exe), "--resolve-title-json", str(database), title], capture_output=True, check=True, timeout=15)
        row = json.loads(result.stdout)
        actual = row["Id"] if row else None
        assert actual == expected, (title, actual, expected)
    # Equal display names: only the IPC-followed chat whose own name is the visible title may be chosen.
    for title, followed, expected in [("Duplicate", "d2", "d2"), ("Duplicate", "d1", "d1"), ("Duplicate", "a", None),
                                      ("Duplicate", "missing", None), ("Visible A", "b", "a"), ("New chat", "a", None)]:
        result = subprocess.run([str(exe), "--resolve-title-json", str(database), title, followed], capture_output=True, check=True, timeout=15)
        actual = json.loads(result.stdout)["Id"]
        assert actual == expected, (title, followed, actual, expected)
    print("PASS 6 duplicate-name disambiguation cases")
print("PASS 5 isolated visible-conversation routing cases")
