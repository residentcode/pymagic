// The problems of a requirements file or pyproject.toml: a requirement the interpreter does not have, a Poetry dependency whose range
// leaves out the newest release, and a stub package (types-X) made for another version of X than the one installed.
// No DOM here: web/test/requirements.test.mjs checks it.

/** pip's normal form of a name: lower case, runs of -_. as one dash. */
export const normalize = (name) => name.toLowerCase().replace(/[-_.]+/g, '-');

/** 'requirements.txt', 'requirements/dev.txt' and 'pyproject.toml' are checked; anything else is not. */
export function kindOf(rel) {
    const lower = rel.toLowerCase();
    if (/(^|\/)pyproject\.toml$/.test(lower)) return 'pyproject';
    if (/(^|\/)requirements[^/]*\.txt$/.test(lower) || /(^|\/)requirements\/[^/]+\.txt$/.test(lower)) return 'requirements';
    return null;
}

/**
 * The requirements a file declares, with where each name is: {name, line (1-based), start, end (1-based columns of the name), poetry?}.
 * poetry is the constraint of a [tool.poetry...dependencies] entry ("^1.2", "~2.0", "1.4.*", ">=1,<2").
 */
export function requirementLines(text, kind) {
    const out = [];
    const lines = text.replace(/\r/g, '').split('\n');
    if (kind === 'requirements') {
        lines.forEach((raw, i) => {
            const line = raw.replace(/\s+#.*$/, '');
            const m = /^\s*([A-Za-z0-9][A-Za-z0-9._-]*)/.exec(line);
            if (!m || /^\s*(-|#)/.test(line) || line.includes('://')) return;
            const start = line.indexOf(m[1]) + 1;
            out.push({ name: m[1], line: i + 1, start, end: start + m[1].length });
        });
        return out;
    }
    let section = null;
    let inArray = false;
    lines.forEach((raw, i) => {
        const line = raw.trim();
        const header = /^\[([^\]]+)\]\s*$/.exec(line);
        if (header) { section = header[1].trim(); inArray = false; return; }
        // PEP 621: dependencies = [...] under [project], every list under [project.optional-dependencies]
        if (section === 'project' && /^dependencies\s*=/.test(line) || section === 'project.optional-dependencies' && /^[\w-]+\s*=\s*\[/.test(line)) inArray = true;
        if (inArray) {
            for (const s of raw.matchAll(/["']([A-Za-z0-9][A-Za-z0-9._-]*)[^"']*["']/g)) {
                const start = s.index + 2;
                out.push({ name: s[1], line: i + 1, start, end: start + s[1].length });
            }
            if (line.includes(']')) inArray = false;
            return;
        }
        // Poetry: name = "^1.2" or name = { version = "^1.2", ... } under [tool.poetry.dependencies] and the groups' dependencies
        if (section && /^tool\.poetry\.(dependencies|dev-dependencies|group\.[\w-]+\.dependencies)$/.test(section)) {
            const m = /^([A-Za-z0-9][A-Za-z0-9._-]*)\s*=\s*(?:"([^"]*)"|'([^']*)'|\{.*?version\s*=\s*["']([^"']*)["'])/.exec(line);
            if (!m || m[1] === 'python') return;
            const start = raw.indexOf(m[1]) + 1;
            out.push({ name: m[1], line: i + 1, start, end: start + m[1].length, poetry: m[2] ?? m[3] ?? m[4] });
        }
    });
    return out;
}

const parts = (v) => v.split(/[.+-]/).map((x) => (/^\d+$/.test(x) ? Number(x) : NaN)).filter((x) => !Number.isNaN(x));

function compare(a, b) {
    const x = parts(a), y = parts(b);
    for (let i = 0; i < Math.max(x.length, y.length); i++) {
        const d = (x[i] ?? 0) - (y[i] ?? 0);
        if (d !== 0) return d;
    }
    return 0;
}

/** The exclusive upper end Poetry's constraint allows (^1.2 is below 2, ~1.2 below 1.3, 1.4.* below 1.5, <2 below 2), or null when it has none. */
export function upperBound(constraint) {
    const c = constraint.trim();
    if (c === '*' || c === '') return null;
    let m;
    if ((m = /^\^\s*(\d+)(?:\.(\d+))?(?:\.(\d+))?/.exec(c))) {
        const [maj, min, pat] = [Number(m[1]), Number(m[2] ?? 0), Number(m[3] ?? 0)];
        return maj > 0 ? `${maj + 1}` : min > 0 || m[2] === undefined ? `0.${min + 1}` : `0.0.${pat + 1}`;
    }
    if ((m = /^~\s*(\d+)(?:\.(\d+))?/.exec(c))) return m[2] === undefined ? `${Number(m[1]) + 1}` : `${m[1]}.${Number(m[2]) + 1}`;
    if ((m = /^(\d+)\.(\d+)\.\*$/.exec(c))) return `${m[1]}.${Number(m[2]) + 1}`;
    if ((m = /^(\d+)\.\*$/.exec(c))) return `${Number(m[1]) + 1}`;
    if ((m = /<\s*(\d[\w.]*)/.exec(c))) return m[1];
    if ((m = /^==?\s*(\d[\w.]*)$/.exec(c)) || (m = /^(\d[\w.]*)$/.exec(c))) return `${m[1]}.0.1`;   // an exact version: anything newer is out
    return null;
}

/**
 * The markers of the file: installed is Map(normalized name -> version) or null when the interpreter is not known; latest is
 * Map(normalized name -> newest release on PyPI) for the Poetry entries that could be asked about.
 */
export function problems(entries, installed, latest = new Map()) {
    const out = [];
    for (const e of entries) {
        const key = normalize(e.name);
        if (installed && !installed.has(key))
            out.push({ ...e, severity: 'warning', message: `Package '${e.name}' is not installed in the interpreter`, code: 'requirement-not-installed' });
        if (e.poetry && latest.has(key)) {
            const newest = latest.get(key), top = upperBound(e.poetry);
            if (top && compare(newest, top) >= 0)
                out.push({ ...e, severity: 'info', message: `${e.name} ${newest} is out, and "${e.poetry}" leaves it out`, code: 'poetry-outdated' });
        }
        // types-requests 2.31.x is made for requests 2.31
        const stubbed = /^types-(.+)$/.exec(key);
        if (stubbed && installed?.has(key) && installed.has(stubbed[1])) {
            const stub = parts(installed.get(key)), real = parts(installed.get(stubbed[1]));
            if (stub.length >= 2 && real.length >= 2 && (stub[0] !== real[0] || stub[1] !== real[1]))
                out.push({ ...e, severity: 'warning', message: `${e.name} ${installed.get(key)} is made for ${stubbed[1]} ${stub[0]}.${stub[1]}, and ${stubbed[1]} ${installed.get(stubbed[1])} is installed`, code: 'stubs-mismatch' });
        }
    }
    return out;
}
