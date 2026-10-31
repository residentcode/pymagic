// What the Coverage panel works out from the runs it shows: suites merged, totals per file and folder, the state and popup text of a
// line, the next uncovered line, and the HTML report. No DOM here: web/test/coverage.test.mjs checks it.

/**
 * Several runs (suites) as one: a line ran when it ran in any of them, a branch went a way when it did in any. The lines that can run
 * and the ways of each branch are the newest run's (the first given), since the files may have changed between runs.
 */
export function mergeSuites(suites) {
    const list = suites.filter(Boolean);
    if (list.length === 0) return null;
    if (list.length === 1) return list[0];
    const files = {};
    for (const [rel, newest] of Object.entries(list[0].files ?? {})) {
        const executable = new Set(newest.executable);
        const executed = new Set(newest.executed);
        const branches = {};
        for (const [line, [ways, taken]] of Object.entries(newest.branches ?? {})) branches[line] = [ways, new Set(taken)];
        for (const older of list.slice(1)) {
            const f = older.files?.[rel];
            if (!f) continue;
            for (const n of f.executed) if (executable.has(n)) executed.add(n);
            for (const [line, [, taken]] of Object.entries(f.branches ?? {}))
                if (branches[line]) for (const w of taken) if (branches[line][0].includes(w)) branches[line][1].add(w);
        }
        const entry = { executable: newest.executable, executed: [...executed].sort((a, b) => a - b) };
        if (Object.keys(branches).length)
            entry.branches = Object.fromEntries(Object.entries(branches).map(([l, [ways, taken]]) => [l, [ways, [...taken].sort((a, b) => a - b)]]));
        files[rel] = entry;
    }
    return { ...list[0], files };
}

export function percentOf(f) {
    return f.executable.length === 0 ? 100 : (100 * f.executed.length) / f.executable.length;
}

/** {lines, hit, branches, taken} of a file. */
export function countsOf(f) {
    let branches = 0, taken = 0;
    for (const [ways, went] of Object.values(f.branches ?? {})) { branches += ways.length; taken += went.length; }
    return { lines: f.executable.length, hit: f.executed.length, branches, taken };
}

/** Totals for every file and every folder above one ('' is the project): {path: {lines, hit, branches, taken, percent}}. */
export function treeTotals(data) {
    const out = {};
    const add = (path, c) => {
        const t = (out[path] ??= { lines: 0, hit: 0, branches: 0, taken: 0 });
        t.lines += c.lines; t.hit += c.hit; t.branches += c.branches; t.taken += c.taken;
    };
    for (const [rel, f] of Object.entries(data?.files ?? {})) {
        if (f.executable.length === 0) continue;
        const c = countsOf(f);
        add(rel, c);
        const parts = rel.split('/');
        for (let i = parts.length - 1; i >= 0; i--) add(parts.slice(0, i).join('/'), c);
    }
    for (const t of Object.values(out)) t.percent = t.lines === 0 ? 100 : (100 * t.hit) / t.lines;
    return out;
}

/** 'hit', 'part' (it ran, a way of its branch did not), 'miss', or null for a line that cannot run. */
export function lineState(f, line) {
    if (!f || !f.executable.includes(line)) return null;
    if (!f.executed.includes(line)) return 'miss';
    const b = f.branches?.[String(line)];
    return b && b[1].length < b[0].length ? 'part' : 'hit';
}

const wayName = (n) => (n === 0 ? 'leaving the function' : `line ${n}`);

/** The text of the line popup: whether it ran, and for a branch the ways it went and did not. */
export function lineInfo(f, line) {
    const state = lineState(f, line);
    if (!state) return null;
    const parts = [state === 'miss' ? 'Not run' : 'Ran'];
    const b = f.branches?.[String(line)];
    if (b) {
        const [ways, taken] = b;
        parts.push(`branches: ${taken.length} of ${ways.length} taken`);
        const missed = ways.filter((w) => !taken.includes(w));
        if (missed.length && state !== 'miss') parts.push(`never went to ${missed.map(wayName).join(', ')}`);
    }
    return parts.join('; ');
}

/** The next (dir 1) or previous (dir -1) line from this one that did not run or did not take every way of its branch; null when there is none. */
export function nextUncovered(f, line, dir) {
    if (!f) return null;
    const lines = f.executable.filter((n) => lineState(f, n) !== 'hit');
    if (dir > 0) return lines.find((n) => n > line) ?? null;
    for (let i = lines.length - 1; i >= 0; i--) if (lines[i] < line) return lines[i];
    return null;
}

const escape = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

/**
 * The report as one HTML page: the totals, a table of the files (the least covered first) and each file's source with its lines
 * marked. sources: {rel: text} for the files to show in full (the others get their row only).
 */
export function reportHtml(data, sources, title = 'Coverage') {
    const totals = treeTotals(data)[''] ?? { lines: 0, hit: 0, branches: 0, taken: 0, percent: 100 };
    const files = Object.entries(data.files ?? {}).filter(([, f]) => f.executable.length > 0)
        .sort((a, b) => percentOf(a[1]) - percentOf(b[1]) || a[0].localeCompare(b[0]));
    const rows = files.map(([rel, f]) => {
        const c = countsOf(f);
        const id = 'f-' + rel.replace(/[^\w]/g, '-');
        return `<tr><td><a href="#${id}">${escape(rel)}</a></td><td>${c.hit}/${c.lines}</td><td>${c.branches ? `${c.taken}/${c.branches}` : ''}</td><td>${percentOf(f).toFixed(1)}%</td></tr>`;
    }).join('\n');
    const bodies = files.filter(([rel]) => sources[rel] != null).map(([rel, f]) => {
        const id = 'f-' + rel.replace(/[^\w]/g, '-');
        const lines = sources[rel].replace(/\r\n/g, '\n').split('\n').map((text, i) => {
            const n = i + 1;
            const state = lineState(f, n);
            const info = lineInfo(f, n);
            return `<tr class="${state ?? ''}"${info ? ` title="${escape(info)}"` : ''}><td class="n">${n}</td><td><pre>${escape(text)}</pre></td></tr>`;
        }).join('\n');
        return `<h2 id="${id}">${escape(rel)} <small>${percentOf(f).toFixed(1)}%</small></h2>\n<table class="src">\n${lines}\n</table>`;
    }).join('\n');
    const when = data.when ? new Date(data.when).toLocaleString() : '';
    const branchText = totals.branches ? `, ${totals.taken} of ${totals.branches} branch ways taken` : '';
    return `<!doctype html>
<html><head><meta charset="utf-8"><title>${escape(title)}</title>
<style>
body { font: 14px system-ui, sans-serif; margin: 24px; color: #1d2030; }
table { border-collapse: collapse; }
.files td, .files th { padding: 4px 12px; border-bottom: 1px solid #e3e5ee; text-align: left; }
.src { width: 100%; font: 13px ui-monospace, Consolas, monospace; }
.src pre { margin: 0; white-space: pre-wrap; }
.src td.n { color: #8a8fa6; text-align: right; padding-right: 10px; user-select: none; }
tr.hit td.n { box-shadow: inset -3px 0 #3fb27f; }
tr.miss { background: #fde8e8; }
tr.miss td.n { box-shadow: inset -3px 0 #e05555; }
tr.part { background: #fff6d6; }
tr.part td.n { box-shadow: inset -3px 0 #e0a800; }
h2 small { color: #8a8fa6; font-weight: normal; }
</style></head><body>
<h1>${escape(title)}</h1>
<p>${totals.percent.toFixed(1)}% of ${totals.lines} lines ran (${totals.hit})${branchText}, in ${files.length} files${when ? `; ${escape(when)}` : ''}.</p>
<table class="files"><tr><th>File</th><th>Lines</th><th>Branches</th><th>Covered</th></tr>
${rows}
</table>
${bodies}
</body></html>
`;
}
