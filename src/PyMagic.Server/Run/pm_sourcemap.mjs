// Source maps for PyMagic's Node and browser debugger (pm_nodedebug.mjs): where a line of the code as written (a .ts file, a Next.js page) is
// in the code that runs (what tsc, webpack, Turbopack or Vite made of it), and back. Version 3 maps, their `mappings` read here (base64 VLQ),
// with `sections` (index maps) too. The names a map gives its sources (webpack://app/./src/a.ts, turbopack:///[project]/app/page.tsx,
// /src/main.ts, ../src/a.ts) are made into files of the project.
import { readFileSync } from 'node:fs';
import { dirname, isAbsolute, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const B64 = new Map([...'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/'].map((c, i) => [c, i]));

/** The segments of a map's `mappings`: [{ line, column, source, sourceLine, sourceColumn }], 0-based, generated positions in order. */
export function decodeMappings(mappings) {
    const out = [];
    let line = 0, source = 0, sourceLine = 0, sourceColumn = 0, name = 0;
    let i = 0;
    const n = mappings.length;
    while (i < n) {
        let column = 0;
        // one line of generated code: its segments, separated by commas, up to ;
        while (i < n && mappings[i] !== ';') {
            if (mappings[i] === ',') { i++; continue; }
            const fields = [];
            while (i < n && mappings[i] !== ',' && mappings[i] !== ';') {
                let value = 0, shift = 0, digit;
                do {
                    digit = B64.get(mappings[i++]);
                    if (digit === undefined) throw new Error('not a source map');
                    value += (digit & 31) << shift;
                    shift += 5;
                } while (digit & 32);
                fields.push(value & 1 ? -(value >>> 1) : value >>> 1);
            }
            column += fields[0];
            if (fields.length >= 4) {
                source += fields[1];
                sourceLine += fields[2];
                sourceColumn += fields[3];
                if (fields.length >= 5) name += fields[4];
                out.push({ line, column, source, sourceLine, sourceColumn });
            }
        }
        line++;
        i++;
    }
    return out;
}

/**
 * A file of the project for a map's source name. `mapUrl` is where the map is (a file:// or http:// address), `root` the project folder,
 * `serverPath` for a source served over http (a browser's), how its address path is a file: /src/a.ts -> root/src/a.ts.
 */
export function sourceFile(name, sourceRoot, mapUrl, root) {
    let s = (sourceRoot ? sourceRoot.replace(/\/?$/, '/') : '') + name;
    // the bundlers' own names: webpack://<name>/./src/a.ts, webpack:///./src/a.ts, turbopack:///[project]/app/page.tsx
    let m;
    if ((m = /^webpack:\/\/[^/]*\/(?:\.\/)?(.*)$/.exec(s))) return join(root, m[1].replace(/^\.\//, '').split('?')[0]);
    if ((m = /^turbopack:\/\/\/\[project\]\/(.*)$/.exec(s))) return join(root, m[1].split('?')[0]);
    if ((m = /^turbopack:\/\/\/(.*)$/.exec(s))) return isAbsolute(m[1]) ? m[1] : join(root, m[1]);
    if (s.startsWith('file://')) { try { return fileURLToPath(s); } catch { return null; } }
    if (/^[A-Za-z]:[\\/]/.test(s)) return s;
    if (/^https?:\/\//.test(s)) {
        // a browser's: the path of the address is a file of the project (Vite serves /src/main.ts from root/src/main.ts)
        try { return join(root, decodeURIComponent(new URL(s).pathname).replace(/^\/(@fs\/)?/, '')); } catch { return null; }
    }
    // a name relative to the map: beside it on disk, or under the project when the map came over http
    if (mapUrl?.startsWith('file://')) {
        try { return resolve(dirname(fileURLToPath(mapUrl)), s); } catch { return null; }
    }
    if (mapUrl && /^https?:\/\//.test(mapUrl)) {
        try { return join(root, decodeURIComponent(new URL(s, mapUrl).pathname).replace(/^\/(@fs\/)?/, '')); } catch { return null; }
    }
    if (s.startsWith('/') && process.platform === 'win32') return join(root, s);
    return isAbsolute(s) ? s : join(root, s);
}

/** A map read from what a script names: a data: address, a file next to the script, or an http address (`fetchText` reads it). */
export async function loadMap(sourceMapURL, scriptUrl, fetchText) {
    if (!sourceMapURL) return null;
    try {
        if (sourceMapURL.startsWith('data:')) {
            const comma = sourceMapURL.indexOf(',');
            const head = sourceMapURL.slice(0, comma);
            const body = sourceMapURL.slice(comma + 1);
            const text = /;base64/i.test(head) ? Buffer.from(body, 'base64').toString('utf8') : decodeURIComponent(body);
            return { map: JSON.parse(text), url: scriptUrl };
        }
        const url = new URL(sourceMapURL, scriptUrl || undefined).href;
        if (url.startsWith('file://')) return { map: JSON.parse(readFileSync(fileURLToPath(url), 'utf8')), url };
        if (/^https?:\/\//.test(url) && fetchText) return { map: JSON.parse(await fetchText(url)), url };
    }
    catch { /* no map: the script is shown as it runs */ }
    return null;
}

/**
 * A script's source map made ready to look up: its sections flattened, each source a file of the project.
 * { files: [file|null], segments sorted by generated position, bySource: Map(fileKey -> segments sorted by original position) }.
 */
export function prepare(loaded, root, key = (f) => f) {
    if (!loaded) return null;
    const files = [];
    const segments = [];
    const add = (map, offsetLine, offsetColumn) => {
        if (map.sections) {
            for (const s of map.sections) if (s.map) add(s.map, offsetLine + (s.offset?.line ?? 0), (s.offset?.line ?? 0) === 0 ? offsetColumn + (s.offset?.column ?? 0) : s.offset?.column ?? 0);
            return;
        }
        const base = files.length;
        for (const name of map.sources ?? []) files.push(name == null ? null : sourceFile(name, map.sourceRoot, loaded.url, root));
        for (const seg of decodeMappings(map.mappings ?? '')) {
            segments.push({ line: seg.line + offsetLine, column: seg.line === 0 ? seg.column + offsetColumn : seg.column, source: base + seg.source, sourceLine: seg.sourceLine, sourceColumn: seg.sourceColumn });
        }
    };
    add(loaded.map, 0, 0);
    segments.sort((a, b) => a.line - b.line || a.column - b.column);
    const bySource = new Map();
    for (const seg of segments) {
        const f = files[seg.source];
        if (!f) continue;
        const k = key(f);
        if (!bySource.has(k)) bySource.set(k, []);
        bySource.get(k).push(seg);
    }
    for (const list of bySource.values()) list.sort((a, b) => a.sourceLine - b.sourceLine || a.sourceColumn - b.sourceColumn || a.line - b.line || a.column - b.column);
    return { files, segments, bySource };
}

/** Where a line of a source (1-based) runs: the first generated position of that line, or of the nearest line after it within 3 lines. */
export function generatedFor(prepared, fileKey, line) {
    const list = prepared?.bySource.get(fileKey);
    if (!list) return null;
    for (let want = line - 1; want < line + 2; want++) {
        const hits = list.filter((s) => s.sourceLine === want);
        if (hits.length) {
            // the earliest place it runs from (some bundlers also map a line's end to it)
            hits.sort((a, b) => a.line - b.line || a.column - b.column);
            return { line: hits[0].line, column: hits[0].column, sourceLine: want + 1 };
        }
    }
    return null;
}

/** The source and line (1-based) a generated position (0-based) comes from, or null when the map says nothing of it. */
export function originalFor(prepared, line, column) {
    const segs = prepared?.segments;
    if (!segs?.length) return null;
    // the last segment at or before the position, on its line
    let lo = 0, hi = segs.length - 1, found = -1;
    while (lo <= hi) {
        const mid = (lo + hi) >> 1;
        const s = segs[mid];
        if (s.line < line || (s.line === line && s.column <= column)) { found = mid; lo = mid + 1; }
        else hi = mid - 1;
    }
    if (found < 0 || segs[found].line !== line) {
        // nothing before it on its line: the first segment of the line
        const first = segs.find((s) => s.line === line);
        if (!first) return null;
        found = segs.indexOf(first);
    }
    const s = segs[found];
    const file = prepared.files[s.source];
    return file ? { file, line: s.sourceLine + 1, column: s.sourceColumn } : null;
}
