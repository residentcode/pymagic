"""PyMagic's PostgreSQL and MySQL helper: one request as JSON on stdin, its answer as JSON on stdout. It answers the requests
pm_db.py answers for an SQLite file, with the same shapes, for a server:

  {"op": "test",   "server"}                                  connects and says the server's version
  {"op": "schema", "server"}                                  tables and views: columns, primary key, indexes, foreign keys
  {"op": "rows",   "server", "table", "where", "order", "desc", "limit", "offset"}
  {"op": "apply",  "server", "table", "changes": [...]}        in one transaction; a key is the primary key's columns
  {"op": "run",    "server", "sql", "limit"}                  one statement of the console
  {"op": "import", "server", "table", "csv"}                  the rows of a CSV file added to a table, all or none

"server" is {"kind": "postgres" | "mysql", "host", "port", "user", "password", "database"}. PostgreSQL needs the psycopg package
(psycopg[binary]) and MySQL the PyMySQL package in the project's Python; without it the answer says so. A table of a schema other
than public (PostgreSQL) is named schema.table.
"""
import datetime
import decimal
import json
import sys
import time
import uuid

MAX_ROWS = 10000
SYSTEM_SCHEMAS = ("pg_catalog", "information_schema", "pg_toast")


def value_of(v):
    if isinstance(v, (bytes, bytearray, memoryview)):
        b = bytes(v)
        return {"blob": b[:32].hex(), "size": len(b)}
    if isinstance(v, bool) or v is None or isinstance(v, str):
        return v
    if isinstance(v, int):
        return str(v) if abs(v) > 2 ** 53 else v
    if isinstance(v, float):
        return {"real": repr(v)} if v.is_integer() else v
    if isinstance(v, decimal.Decimal):
        return str(v)
    if isinstance(v, (datetime.datetime, datetime.date, datetime.time)):
        return v.isoformat(sep=" ") if isinstance(v, datetime.datetime) else v.isoformat()
    if isinstance(v, (datetime.timedelta, uuid.UUID)):
        return str(v)
    if isinstance(v, (dict, list)):
        return json.dumps(v)
    return str(v)


class Missing(Exception):
    """The driver is not installed."""


class Dialect:
    def __init__(self, server):
        self.server = server
        self.kind = server.get("kind")

    def quote(self, name):
        return '"' + str(name).replace('"', '""') + '"' if self.kind == "postgres" else "`" + str(name).replace("`", "``") + "`"

    def table(self, name):
        """A table's name as SQL: schema.table is two names in PostgreSQL."""
        if self.kind == "postgres" and "." in name:
            schema, _, table = name.partition(".")
            return self.quote(schema) + "." + self.quote(table)
        return self.quote(name)

    def null_safe(self, column):
        return "%s IS NOT DISTINCT FROM %%s" % self.quote(column) if self.kind == "postgres" else "%s <=> %%s" % self.quote(column)

    def connect(self):
        s = self.server
        timeout = 8
        if self.kind == "postgres":
            try:
                import psycopg
            except ImportError:
                raise Missing('PostgreSQL needs the psycopg package in this Python: pip install "psycopg[binary]"')
            # client-side binding: the server coerces a text value to the column's type, as it does a typed-in literal
            return psycopg.connect(host=s.get("host") or "localhost", port=int(s.get("port") or 5432), user=s.get("user") or "postgres",
                                   password=s.get("password") or None, dbname=s.get("database") or "postgres", connect_timeout=timeout,
                                   cursor_factory=psycopg.ClientCursor)
        if self.kind == "mysql":
            try:
                import pymysql
            except ImportError:
                raise Missing("MySQL needs the PyMySQL package in this Python: pip install PyMySQL")
            return pymysql.connect(host=s.get("host") or "localhost", port=int(s.get("port") or 3306), user=s.get("user") or "root",
                                   password=s.get("password") or "", database=s.get("database") or None, connect_timeout=timeout,
                                   charset="utf8mb4", autocommit=False)
        raise Missing("unknown kind of server %r" % self.kind)


def version(d, db):
    cur = db.cursor()
    cur.execute("SELECT version()")
    return cur.fetchone()[0]


def fetch(db, sql, args=()):
    cur = db.cursor()
    cur.execute(sql, args)
    return cur.fetchall()


def schema(d, db):
    if d.kind == "postgres":
        return {"objects": schema_postgres(db)}
    return {"objects": schema_mysql(db)}


def schema_postgres(db):
    tables = fetch(db, """SELECT table_schema, table_name, table_type FROM information_schema.tables
                          WHERE table_schema NOT IN ('pg_catalog', 'information_schema', 'pg_toast') AND table_schema NOT LIKE 'pg_temp%%' AND table_schema NOT LIKE 'pg_toast_temp%%'
                          ORDER BY table_type, table_schema, table_name""")
    cols = {}
    for s, t, name, typ, udt, nullable, default in fetch(db, """SELECT table_schema, table_name, column_name, data_type, udt_name, is_nullable, column_default
                                                                FROM information_schema.columns WHERE table_schema NOT IN ('pg_catalog', 'information_schema', 'pg_toast')
                                                                ORDER BY table_schema, table_name, ordinal_position"""):
        cols.setdefault((s, t), []).append({"name": name, "type": udt if typ in ("USER-DEFINED", "ARRAY") else typ, "notNull": nullable == "NO", "default": default, "pk": 0})
    pks = {}
    for s, t, col, pos in fetch(db, """SELECT kcu.table_schema, kcu.table_name, kcu.column_name, kcu.ordinal_position
                                       FROM information_schema.table_constraints tc JOIN information_schema.key_column_usage kcu
                                         ON tc.constraint_name = kcu.constraint_name AND tc.table_schema = kcu.table_schema AND tc.table_name = kcu.table_name
                                       WHERE tc.constraint_type = 'PRIMARY KEY'"""):
        pks.setdefault((s, t), {})[col] = pos
    indexes = {}
    for s, t, name, unique, columns in fetch(db, """SELECT n.nspname, c.relname, i.relname, x.indisunique,
                                                           (SELECT string_agg(a.attname, ',' ORDER BY k.ord) FROM unnest(x.indkey) WITH ORDINALITY k(attnum, ord)
                                                            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = k.attnum)
                                                    FROM pg_index x JOIN pg_class c ON c.oid = x.indrelid JOIN pg_class i ON i.oid = x.indexrelid
                                                    JOIN pg_namespace n ON n.oid = c.relnamespace
                                                    WHERE n.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast')"""):
        indexes.setdefault((s, t), []).append({"name": name, "unique": bool(unique), "columns": (columns or "").split(",")})
    fks = {}
    for s, t, col, ft, fcol in fetch(db, """SELECT kcu.table_schema, kcu.table_name, kcu.column_name, ccu.table_name, ccu.column_name
                                            FROM information_schema.table_constraints tc
                                            JOIN information_schema.key_column_usage kcu ON tc.constraint_name = kcu.constraint_name AND tc.table_schema = kcu.table_schema
                                            JOIN information_schema.constraint_column_usage ccu ON ccu.constraint_name = tc.constraint_name AND ccu.table_schema = tc.table_schema
                                            WHERE tc.constraint_type = 'FOREIGN KEY'"""):
        fks.setdefault((s, t), []).append({"column": col, "table": ft, "to": fcol})
    objects = []
    for s, t, kind in tables:
        columns = cols.get((s, t), [])
        for c in columns:
            c["pk"] = pks.get((s, t), {}).get(c["name"], 0)
        objects.append({"name": t if s == "public" else "%s.%s" % (s, t), "kind": "view" if kind == "VIEW" else "table", "columns": columns,
                        "indexes": indexes.get((s, t), []), "foreignKeys": fks.get((s, t), []), "rowid": False, "sql": None})
    return objects


def schema_mysql(db):
    tables = fetch(db, "SELECT table_name, table_type FROM information_schema.tables WHERE table_schema = DATABASE() ORDER BY table_type, table_name")
    cols = {}
    for t, name, typ, nullable, default, key in fetch(db, """SELECT table_name, column_name, column_type, is_nullable, column_default, column_key
                                                             FROM information_schema.columns WHERE table_schema = DATABASE() ORDER BY table_name, ordinal_position"""):
        cols.setdefault(t, []).append({"name": name, "type": typ, "notNull": nullable == "NO", "default": default, "pk": 1 if key == "PRI" else 0})
    pks = {}
    for t, col, pos in fetch(db, "SELECT table_name, column_name, ordinal_position FROM information_schema.key_column_usage WHERE table_schema = DATABASE() AND constraint_name = 'PRIMARY'"):
        pks.setdefault(t, {})[col] = pos
    indexes = {}
    for t, name, nonunique, col in fetch(db, "SELECT table_name, index_name, non_unique, column_name FROM information_schema.statistics WHERE table_schema = DATABASE() ORDER BY table_name, index_name, seq_in_index"):
        group = indexes.setdefault(t, {})
        group.setdefault(name, {"name": name, "unique": not nonunique, "columns": []})["columns"].append(col)
    fks = {}
    for t, col, ft, fcol in fetch(db, """SELECT table_name, column_name, referenced_table_name, referenced_column_name FROM information_schema.key_column_usage
                                         WHERE table_schema = DATABASE() AND referenced_table_name IS NOT NULL"""):
        fks.setdefault(t, []).append({"column": col, "table": ft, "to": fcol})
    objects = []
    for t, kind in tables:
        columns = cols.get(t, [])
        for c in columns:
            c["pk"] = pks.get(t, {}).get(c["name"], 0)
        objects.append({"name": t, "kind": "view" if kind == "VIEW" else "table", "columns": columns, "indexes": list(indexes.get(t, {}).values()),
                        "foreignKeys": fks.get(t, []), "rowid": False, "sql": None})
    return objects


def rows(d, db, table, where="", order="", desc=False, limit=200, offset=0):
    objs = schema(d, db)["objects"]
    obj = next((o for o in objs if o["name"] == table), None)
    if obj is None:
        return {"error": "No table or view named %s." % table}
    names = [c["name"] for c in obj["columns"]]
    pk = [c["name"] for c in sorted(obj["columns"], key=lambda c: c["pk"]) if c["pk"]]
    sql = "SELECT %s FROM %s" % (", ".join(d.quote(n) for n in names), d.table(table))
    clause = " WHERE " + where.replace("%", "%%") if where.strip() else ""   # the driver formats the query: a % of the user's is doubled
    total = fetch(db, "SELECT count(*) FROM %s%s" % (d.table(table), clause))[0][0]
    if order and order in names:
        clause += " ORDER BY %s%s" % (d.quote(order), " DESC" if desc else "")
    elif pk:
        clause += " ORDER BY " + ", ".join(d.quote(n) for n in pk)
    out = []
    for r in fetch(db, sql + clause + " LIMIT %s OFFSET %s", (int(limit), int(offset))):
        key = {n: value_of(r[names.index(n)]) for n in pk} if pk else None
        out.append({"key": key, "values": [value_of(v) for v in r]})
    return {"columns": names, "rows": out, "total": total, "editable": obj["kind"] == "table" and bool(pk), "offset": int(offset)}


def apply(d, db, table, changes):
    done = 0
    cur = db.cursor()
    t = d.table(table)
    try:
        for c in changes:
            if c["kind"] == "update":
                cols = list(c["values"])
                keys = list(c["key"])
                cur.execute("UPDATE %s SET %s WHERE %s" % (t, ", ".join(d.quote(n) + " = %s" for n in cols), " AND ".join(d.null_safe(k) for k in keys)),
                            [c["values"][n] for n in cols] + [c["key"][k] for k in keys])
            elif c["kind"] == "insert":
                cols = list(c["values"])
                if cols:
                    cur.execute("INSERT INTO %s (%s) VALUES (%s)" % (t, ", ".join(d.quote(n) for n in cols), ", ".join(["%s"] * len(cols))), [c["values"][n] for n in cols])
                else:
                    cur.execute("INSERT INTO %s DEFAULT VALUES" % t if d.kind == "postgres" else "INSERT INTO %s () VALUES ()" % t)
            elif c["kind"] == "delete":
                keys = list(c["key"])
                cur.execute("DELETE FROM %s WHERE %s" % (t, " AND ".join(d.null_safe(k) for k in keys)), [c["key"][k] for k in keys])
            else:
                continue
            done += max(cur.rowcount, 0)
        db.commit()
        return {"changed": done}
    except Exception as e:
        db.rollback()
        return {"error": "%s: %s (nothing was changed)" % (type(e).__name__, str(e).strip())}


def run(d, db, sql, limit=500):
    started = time.perf_counter()
    cur = db.cursor()
    cur.execute(sql)
    if cur.description is not None:
        columns = [c[0] for c in cur.description]
        fetched = cur.fetchmany(int(limit) + 1)
        db.commit()
        return {"columns": columns, "rows": [[value_of(v) for v in r] for r in fetched[:int(limit)]], "more": len(fetched) > int(limit), "time": time.perf_counter() - started}
    db.commit()
    return {"changed": max(cur.rowcount, 0), "time": time.perf_counter() - started}


def import_csv(d, db, table, csv_path):
    import csv
    obj = next((o for o in schema(d, db)["objects"] if o["name"] == table), None)
    if obj is None:
        return {"error": "No table named %s." % table}
    names = [c["name"] for c in obj["columns"]]
    try:
        with open(csv_path, newline="", encoding="utf-8-sig") as f:
            sample = f.read(4096)
            f.seek(0)
            try:
                dialect = csv.Sniffer().sniff(sample, delimiters=",;\t|")
            except csv.Error:
                dialect = csv.excel
            reader = csv.reader(f, dialect)
            header = next(reader, None)
            if not header:
                return {"error": "The file is empty."}
            by_lower = {n.lower(): n for n in names}
            unknown = [h for h in header if h.strip().lower() not in by_lower]
            if unknown:
                return {"error": "The table %s has no column %s (its columns: %s)." % (table, ", ".join(unknown), ", ".join(names))}
            columns = [by_lower[h.strip().lower()] for h in header]
            sql = "INSERT INTO %s (%s) VALUES (%s)" % (d.table(table), ", ".join(d.quote(c) for c in columns), ", ".join(["%s"] * len(columns)))
            cur = db.cursor()
            count = 0
            for line, row in enumerate(reader, 2):
                if not any(field.strip() for field in row):
                    continue
                if len(row) != len(columns):
                    raise ValueError("line %d has %d fields, the header %d (nothing was imported)" % (line, len(row), len(columns)))
                cur.execute(sql, [None if field == "" else field for field in row])
                count += 1
            db.commit()
        return {"imported": count}
    except (OSError, UnicodeDecodeError) as e:
        db.rollback()
        return {"error": "The file could not be read: %s" % e}
    except Exception as e:
        db.rollback()
        return {"error": str(e) if isinstance(e, ValueError) else "%s: %s (nothing was imported)" % (type(e).__name__, str(e).strip())}


def main():
    request = json.loads(sys.stdin.read())
    op = request.get("op")
    d = Dialect(request.get("server") or {})
    db = None
    try:
        db = d.connect()
        if op == "test":
            answer = {"version": str(version(d, db))}
        elif op == "schema":
            answer = schema(d, db)
        elif op == "rows":
            answer = rows(d, db, request.get("table", ""), request.get("where") or "", request.get("order") or "", bool(request.get("desc")),
                          min(int(request.get("limit") or 200), MAX_ROWS), int(request.get("offset") or 0))
        elif op == "apply":
            answer = apply(d, db, request.get("table", ""), request.get("changes") or [])
        elif op == "import":
            answer = import_csv(d, db, request.get("table", ""), request.get("csv", ""))
        elif op == "run":
            answer = run(d, db, request.get("sql", ""), min(int(request.get("limit") or 500), MAX_ROWS))
        else:
            answer = {"error": "unknown request %r" % op}
    except Missing as e:
        answer = {"error": str(e), "missingDriver": True}
    except Exception as e:
        if db is not None:
            try:
                db.rollback()
            except Exception:
                pass
        answer = {"error": "%s: %s" % (type(e).__name__, str(e).strip())}
    finally:
        if db is not None:
            try:
                db.close()
            except Exception:
                pass
    sys.stdout.write(json.dumps(answer))


if __name__ == "__main__":
    main()
