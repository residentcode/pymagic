"""PyMagic's SQLite helper: one request as JSON on stdin, its answer as JSON on stdout (the standard sqlite3 module only).

  {"op": "schema", "path"}                                   tables and views: columns, primary key, indexes, foreign keys
  {"op": "rows", "path", "table", "where", "order", "desc", "limit", "offset"}
                                                             a page of a table's rows, with the key of each row and the count
  {"op": "apply", "path", "table", "changes": [...]}          edits of the data editor, in one transaction:
                                                             {"kind": "update", "key": {...}, "values": {...}}, {"kind": "insert", "values"},
                                                             {"kind": "delete", "key"}; a key is {"rowid": n} or the primary key's columns
  {"op": "run", "path", "sql", "limit"}                      one statement of the console: a result set, or the rows it changed
  {"op": "create", "path"}                                   a new, empty database file

A value is sent as JSON: numbers, text and null as they are, a blob as {"blob": first bytes in hex, "size"}, a REAL with
nothing after its point as {"real": "2.0"}.
"""
import json
import pathlib
import sqlite3
import sys
import time

MAX_ROWS = 10000


def value_of(v):
    if isinstance(v, (bytes, bytearray, memoryview)):
        b = bytes(v)
        return {"blob": b[:32].hex(), "size": len(b)}
    if isinstance(v, bool):
        return v
    if isinstance(v, int) and abs(v) > 2 ** 53:
        return str(v)   # JavaScript's numbers would round it
    if isinstance(v, float) and v.is_integer():
        return {"real": repr(v)}   # 2.0 stays a REAL: JavaScript would show it as 2
    return v


def quote(name):
    return '"' + str(name).replace('"', '""') + '"'


def connect(path, write=False):
    if write:
        return sqlite3.connect(path, timeout=5)
    # reading never makes a missing file, and never writes
    return sqlite3.connect(pathlib.Path(path).resolve().as_uri() + "?mode=ro", uri=True, timeout=5)


def schema(path):
    db = connect(path)
    try:
        objects = []
        for name, kind, sql in db.execute("SELECT name, type, sql FROM sqlite_master WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite_%' ORDER BY type, name"):
            columns = [{"name": c[1], "type": c[2], "notNull": bool(c[3]), "default": c[4], "pk": c[5]} for c in db.execute("PRAGMA table_info(%s)" % quote(name))]
            indexes = []
            for ix in db.execute("PRAGMA index_list(%s)" % quote(name)).fetchall():
                cols = [r[2] for r in db.execute("PRAGMA index_info(%s)" % quote(ix[1]))]
                indexes.append({"name": ix[1], "unique": bool(ix[2]), "columns": cols})
            keys = [{"column": f[3], "table": f[2], "to": f[4]} for f in db.execute("PRAGMA foreign_key_list(%s)" % quote(name))]
            rowid = kind == "table" and "WITHOUT ROWID" not in (sql or "").upper()
            objects.append({"name": name, "kind": kind, "columns": columns, "indexes": indexes, "foreignKeys": keys, "rowid": rowid, "sql": sql})
        return {"objects": objects}
    finally:
        db.close()


def rows(path, table, where="", order="", desc=False, limit=200, offset=0):
    db = connect(path)
    try:
        info = schema(path)
        obj = next((o for o in info["objects"] if o["name"] == table), None)
        if obj is None:
            return {"error": "No table or view named %s." % table}
        names = [c["name"] for c in obj["columns"]]
        pk = [c["name"] for c in sorted(obj["columns"], key=lambda c: c["pk"]) if c["pk"]]
        key = "rowid" if obj["rowid"] else ("pk" if pk else None)
        select = ("rowid AS __pm_rowid, " if key == "rowid" else "") + ", ".join(quote(n) for n in names)
        sql = "SELECT %s FROM %s" % (select, quote(table))
        if where.strip():
            sql += " WHERE " + where
        if order and order in names:
            sql += " ORDER BY %s%s" % (quote(order), " DESC" if desc else "")
        total = db.execute("SELECT count(*) FROM %s%s" % (quote(table), " WHERE " + where if where.strip() else "")).fetchone()[0]
        cursor = db.execute(sql + " LIMIT ? OFFSET ?", (int(limit), int(offset)))
        out = []
        for r in cursor:
            values = list(r[1:] if key == "rowid" else r)
            row_key = {"rowid": r[0]} if key == "rowid" else ({n: value_of(values[names.index(n)]) for n in pk} if key == "pk" else None)
            out.append({"key": row_key, "values": [value_of(v) for v in values]})
        return {"columns": names, "rows": out, "total": total, "editable": obj["kind"] == "table" and key is not None, "offset": int(offset)}
    except sqlite3.Error as e:
        return {"error": "%s: %s" % (type(e).__name__, e)}
    finally:
        db.close()


def where_of(key):
    names = list(key)
    return " AND ".join(("rowid" if n == "rowid" else quote(n)) + " IS ?" for n in names), [key[n] for n in names]


def apply(path, table, changes):
    db = connect(path, write=True)
    try:
        done = 0
        with db:
            for c in changes:
                if c["kind"] == "update":
                    cols = list(c["values"])
                    clause, args = where_of(c["key"])
                    cur = db.execute("UPDATE %s SET %s WHERE %s" % (quote(table), ", ".join(quote(n) + " = ?" for n in cols), clause), [c["values"][n] for n in cols] + args)
                elif c["kind"] == "insert":
                    cols = list(c["values"])
                    cur = db.execute("INSERT INTO %s (%s) VALUES (%s)" % (quote(table), ", ".join(quote(n) for n in cols), ", ".join("?" * len(cols)))
                                     if cols else "INSERT INTO %s DEFAULT VALUES" % quote(table), [c["values"][n] for n in cols])
                elif c["kind"] == "delete":
                    clause, args = where_of(c["key"])
                    cur = db.execute("DELETE FROM %s WHERE %s" % (quote(table), clause), args)
                else:
                    continue
                done += cur.rowcount
        return {"changed": done}
    except sqlite3.Error as e:
        return {"error": "%s: %s (nothing was changed)" % (type(e).__name__, e)}
    finally:
        db.close()


def run(path, sql, limit=500):
    db = connect(path, write=True)
    try:
        started = time.perf_counter()
        cur = db.execute(sql)
        if cur.description is not None:
            columns = [d[0] for d in cur.description]
            fetched = cur.fetchmany(int(limit) + 1)
            db.commit()
            return {"columns": columns, "rows": [[value_of(v) for v in r] for r in fetched[:int(limit)]], "more": len(fetched) > int(limit),
                    "time": time.perf_counter() - started}
        db.commit()
        return {"changed": cur.rowcount, "time": time.perf_counter() - started}
    except sqlite3.Error as e:
        return {"error": "%s: %s" % (type(e).__name__, e)}
    except ValueError as e:   # "You can only execute one statement at a time."
        return {"error": str(e)}
    finally:
        db.close()


def main():
    request = json.loads(sys.stdin.read())
    op, path = request.get("op"), request.get("path", "")
    try:
        if op == "schema":
            answer = schema(path)
        elif op == "rows":
            answer = rows(path, request.get("table", ""), request.get("where") or "", request.get("order") or "", bool(request.get("desc")),
                          min(int(request.get("limit") or 200), MAX_ROWS), int(request.get("offset") or 0))
        elif op == "apply":
            answer = apply(path, request.get("table", ""), request.get("changes") or [])
        elif op == "create":
            # a new database: SQLite writes its header with the first table, so one is made and dropped
            db = sqlite3.connect(path)
            db.execute("CREATE TABLE __pymagic_new (x)")
            db.execute("DROP TABLE __pymagic_new")
            db.close()
            answer = {"created": path}
        elif op == "run":
            answer = run(path, request.get("sql", ""), min(int(request.get("limit") or 500), MAX_ROWS))
        else:
            answer = {"error": "unknown request %r" % op}
    except sqlite3.Error as e:
        answer = {"error": "%s: %s" % (type(e).__name__, e)}
    sys.stdout.write(json.dumps(answer))


if __name__ == "__main__":
    main()
