// The Database panel's logic that needs no page: how a value is shown, the statement at the console's cursor, the
// completions of SQL from a database's schema, and the changes the data editor sends.

/** A value of SQLite as the grid shows it: NULL, a blob by its size, numbers and text as they are. */
export function cellText(v) {
    if (v === null || v === undefined)
        return 'NULL';
    if (typeof v === 'object' && 'blob' in v)
        return `<blob ${v.size} byte${v.size === 1 ? '' : 's'}>`;
    if (typeof v === 'object' && 'real' in v)
        return v.real;   // a REAL such as 2.0, which a JavaScript number would show as 2
    return String(v);
}

/** What a cell typed in becomes: `NULL` written alone is NULL, anything else is its text (SQLite gives it the column's type). */
export function typedValue(text) {
    return text === 'NULL' ? null : text;
}

/**
 * The statement of an SQL text around an offset: the text between the semicolons before and after it, semicolons
 * inside quotes and comments not counting. The statement before the cursor when it stands right after a semicolon.
 */
export function statementAt(text, offset) {
    const ends = semicolons(text);
    let start = 0;
    for (const end of ends) {
        if (offset <= end || (offset === end + 1 && text.slice(start, end).trim() && !text.slice(end + 1).split('\n')[0].trim())) {
            return { start, end, text: text.slice(start, end).trim() };
        }
        start = end + 1;
    }
    // past the last semicolon: the text after it, or the last statement when nothing follows it
    if (!text.slice(start).trim() && ends.length) {
        const prev = ends.length > 1 ? ends[ends.length - 2] + 1 : 0;
        return { start: prev, end: ends[ends.length - 1], text: text.slice(prev, ends[ends.length - 1]).trim() };
    }
    return { start, end: text.length, text: text.slice(start).trim() };
}

/** The offsets of the semicolons that end statements: not those in quotes, brackets or comments. */
function semicolons(text) {
    const ends = [];
    for (let i = 0; i < text.length; i++) {
        const c = text[i];
        if (c === "'" || c === '"' || c === '`' || c === '[') {
            const close = c === '[' ? ']' : c;
            i++;
            while (i < text.length && text[i] !== close)
                i++;
        }
        else if (c === '-' && text[i + 1] === '-') {
            while (i < text.length && text[i] !== '\n')
                i++;
        }
        else if (c === '/' && text[i + 1] === '*') {
            const end = text.indexOf('*/', i + 2);
            i = end < 0 ? text.length : end + 1;
        }
        else if (c === ';')
            ends.push(i);
    }
    return ends;
}

const KEYWORDS = ['SELECT', 'FROM', 'WHERE', 'JOIN', 'LEFT JOIN', 'INNER JOIN', 'ON', 'GROUP BY', 'ORDER BY', 'HAVING', 'LIMIT', 'OFFSET',
    'INSERT INTO', 'VALUES', 'UPDATE', 'SET', 'DELETE FROM', 'CREATE TABLE', 'ALTER TABLE', 'DROP TABLE', 'CREATE INDEX', 'AND', 'OR',
    'NOT', 'NULL', 'IS', 'IN', 'LIKE', 'BETWEEN', 'AS', 'DISTINCT', 'COUNT(*)', 'DESC', 'ASC', 'PRAGMA'];

/** The tables a statement names with their aliases: `FROM item i JOIN tag AS t` gives {item: 'item', i: 'item', tag: 'tag', t: 'tag'}. */
export function aliasesOf(sql, tables) {
    const known = new Map(tables.map((t) => [t.toLowerCase(), t]));
    const found = {};
    const re = /\b(?:FROM|JOIN|UPDATE|INTO)\s+["`[]?(\w+)["`\]]?(?:\s+(?:AS\s+)?(\w+))?/gi;
    for (const m of sql.matchAll(re)) {
        const table = known.get(m[1].toLowerCase());
        if (!table)
            continue;
        found[table] = table;
        if (m[2] && !/^(WHERE|JOIN|LEFT|INNER|ON|SET|VALUES|ORDER|GROUP|LIMIT|AS)$/i.test(m[2]))
            found[m[2]] = table;
    }
    return found;
}

/**
 * The completions at the end of `before` (the statement up to the cursor) from a schema ([{name, kind, columns}]):
 * after `name.` the columns of that table or alias; else the tables and views, the columns of the tables the
 * statement names (all columns when it names none yet), and the keywords.
 */
export function sqlCompletions(before, statement, objects) {
    const byName = new Map(objects.map((o) => [o.name, o]));
    const aliases = aliasesOf(statement, objects.map((o) => o.name));
    const dotted = /(\w+)\.\w*$/.exec(before);
    if (dotted) {
        const table = byName.get(aliases[dotted[1]] ?? dotted[1]) ?? objects.find((o) => o.name.toLowerCase() === dotted[1].toLowerCase());
        return table ? table.columns.map((c) => ({ label: c.name, kind: 'column', detail: `${c.type || 'any'} · ${table.name}` })) : [];
    }
    const named = [...new Set(Object.values(aliases))].map((n) => byName.get(n)).filter(Boolean);
    const columns = (named.length ? named : objects).flatMap((o) => o.columns.map((c) => ({ label: c.name, kind: 'column', detail: `${c.type || 'any'} · ${o.name}` })));
    const seen = new Set();
    return [
        ...objects.map((o) => ({ label: o.name, kind: o.kind === 'view' ? 'view' : 'table', detail: `${o.kind}, ${o.columns.length} columns` })),
        ...columns.filter((c) => !seen.has(c.label) && seen.add(c.label)),
        ...KEYWORDS.map((k) => ({ label: k, kind: 'keyword', detail: '' })),
    ];
}

/**
 * The changes of the data editor as pm_db.py takes them: the edited cells of each row (by the row's key), the rows
 * added (their filled cells) and the rows deleted; a deleted row's edits are dropped.
 */
export function changesOf(columns, rows, edits, added, deleted) {
    const changes = [];
    for (const [index, cells] of edits) {
        if (deleted.has(index) || !rows[index]?.key)
            continue;
        const values = {};
        for (const [col, v] of Object.entries(cells))
            values[columns[Number(col)]] = v;
        if (Object.keys(values).length)
            changes.push({ kind: 'update', key: rows[index].key, values });
    }
    for (const cells of added) {
        const values = {};
        for (const [col, v] of Object.entries(cells))
            values[columns[Number(col)]] = v;
        changes.push({ kind: 'insert', values });
    }
    for (const index of [...deleted].sort((a, b) => a - b))
        if (rows[index]?.key)
            changes.push({ kind: 'delete', key: rows[index].key });
    return changes;
}

/** Every statement of an SQL text, in order, as statementAt finds them (empty ones left out): what Run file runs. */
export function statementsOf(text) {
    const found = [];
    let start = 0;
    for (const end of [...semicolons(text), text.length]) {
        const piece = text.slice(start, end).trim();
        if (piece)
            found.push({ start, end, text: piece });
        start = end + 1;
    }
    return found;
}

/** A value as JSON writes it: a REAL as its number, a blob as {blob, size} (its first bytes only, as the engine sent it). */
const plain = (v) => (v !== null && typeof v === 'object' && 'real' in v ? Number(v.real) : v);

/** The results as JSON: an array of objects, one per row, keyed by the column names. */
export function toJson(columns, rows) {
    return JSON.stringify(rows.map((r) => Object.fromEntries(columns.map((c, i) => [c, plain(r[i]) ?? null]))), null, 2) + '\n';
}

const sqlName = (name) => (/^[A-Za-z_][A-Za-z0-9_]*$/.test(name) ? name : '"' + String(name).replace(/"/g, '""') + '"');
function sqlValue(v) {
    if (v === null || v === undefined)
        return 'NULL';
    if (typeof v === 'object' && 'real' in v)
        return v.real;
    if (typeof v === 'object' && 'blob' in v)
        return v.size * 2 === v.blob.length ? `X'${v.blob}'` : `NULL /* a blob of ${v.size} bytes, not exported */`;
    if (typeof v === 'number' || typeof v === 'boolean')
        return String(Number(v));
    return "'" + String(v).replace(/'/g, "''") + "'";
}

/** The results as SQL: one INSERT per row into `table`, which a console or a .sql file can run again. */
export function toInserts(table, columns, rows) {
    const head = `INSERT INTO ${sqlName(table)} (${columns.map(sqlName).join(', ')}) VALUES `;
    return rows.map((r) => `${head}(${r.map(sqlValue).join(', ')});`).join('\n') + '\n';
}

/** The results as CSV, the way spreadsheets read it: fields with commas, quotes or line breaks quoted, NULL empty. */
export function toCsv(columns, rows) {
    const field = (v) => {
        const t = v === null || v === undefined ? '' : typeof v === 'object' ? cellText(v) : String(v);
        return /[",\r\n]/.test(t) ? '"' + t.replace(/"/g, '""') + '"' : t;
    };
    return [columns.map(field).join(','), ...rows.map((r) => r.map(field).join(','))].join('\r\n') + '\r\n';
}
