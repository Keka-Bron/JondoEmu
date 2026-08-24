"""Read-only SQLite bridge for the Jondo Admin Electron application."""
from __future__ import annotations

import json
import sqlite3
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
BASES = ROOT / "bases"


def output(value: object) -> None:
    print(json.dumps(value, ensure_ascii=False, default=str))


def database_path(name: str) -> Path:
    path = (BASES / name).resolve()
    if path.parent != BASES.resolve() or path.suffix.lower() not in {".db", ".sqlite", ".sqlite3"} or not path.is_file():
        raise ValueError("Unknown database.")
    return path


def quote(identifier: str) -> str:
    if not identifier or "\x00" in identifier:
        raise ValueError("Invalid identifier.")
    return '"' + identifier.replace('"', '""') + '"'


def list_databases() -> dict:
    files = []
    for path in sorted(BASES.glob("*")):
        if path.is_file() and path.suffix.lower() in {".db", ".sqlite", ".sqlite3"}:
            files.append({"name": path.name, "size": path.stat().st_size})
    return {"databases": files}


def list_tables(name: str) -> dict:
    with sqlite3.connect(database_path(name)) as conn:
        rows = conn.execute("SELECT name FROM sqlite_master WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite_%' ORDER BY name").fetchall()
        tables = []
        for (table,) in rows:
            columns = conn.execute(f"PRAGMA table_info({quote(table)})").fetchall()
            count = conn.execute(f"SELECT COUNT(*) FROM {quote(table)}").fetchone()[0]
            tables.append({"name": table, "rowCount": count, "columns": [{"name": column[1], "type": column[2], "primaryKey": bool(column[5])} for column in columns]})
    return {"tables": tables}


def rows(name: str, table: str, page: int, page_size: int) -> dict:
    page = max(0, min(int(page), 1000000))
    page_size = max(1, min(int(page_size), 200))
    with sqlite3.connect(database_path(name)) as conn:
        known = {row[0] for row in conn.execute("SELECT name FROM sqlite_master WHERE type IN ('table', 'view')")}
        if table not in known:
            raise ValueError("Unknown table.")
        columns = [row[1] for row in conn.execute(f"PRAGMA table_info({quote(table)})")]
        total = conn.execute(f"SELECT COUNT(*) FROM {quote(table)}").fetchone()[0]
        result = conn.execute(f"SELECT * FROM {quote(table)} LIMIT ? OFFSET ?", (page_size, page * page_size))
        sensitive = {"password", "token", "gametoken", "launchertoken", "secret", "hash"}
        data = []
        for row in result.fetchall():
            record = dict(zip(columns, row))
            for column in columns:
                if column.lower() in sensitive and record[column] is not None:
                    record[column] = "••••••••"
            data.append(record)
    return {"columns": columns, "rows": data, "total": total, "page": page, "pageSize": page_size}


def main() -> None:
    request = json.loads(sys.argv[1])
    action = request.get("action")
    if action == "databases": output(list_databases())
    elif action == "tables": output(list_tables(str(request.get("database", ""))))
    elif action == "rows": output(rows(str(request.get("database", "")), str(request.get("table", "")), request.get("page", 0), request.get("pageSize", 50)))
    else: raise ValueError("Unknown admin database action.")


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:  # bridge errors are intentionally user-facing
        output({"error": str(exc)})
        raise SystemExit(1)
