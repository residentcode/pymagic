// The Tests panel's logic that needs no page: statistics of a run, its export as JUnit XML or HTML, the history of runs,
// and which package a run that could not start needs.

/** One run: {when, framework, results: [{id, outcome, duration, message}]} with its counts and time. */
export function summarizeRun(results) {
    const count = (...outcomes) => results.filter((r) => outcomes.includes(r.outcome)).length;
    return {
        total: results.length, passed: count('passed', 'xfailed'), failed: count('failed', 'xpassed'), errors: count('error'), skipped: count('skipped'),
        duration: results.reduce((t, r) => t + (r.duration ?? 0), 0),
    };
}

/** The tests of a run, the slowest first, and the files with their totals, the slowest first. */
export function statistics(results) {
    const tests = [...results].sort((a, b) => (b.duration ?? 0) - (a.duration ?? 0) || a.id.localeCompare(b.id));
    const byFile = new Map();
    for (const r of results) {
        const file = r.id.split('::')[0];
        const f = byFile.get(file) ?? { file, tests: 0, failed: 0, duration: 0 };
        f.tests++;
        f.failed += ['failed', 'error', 'xpassed'].includes(r.outcome) ? 1 : 0;
        f.duration += r.duration ?? 0;
        byFile.set(file, f);
    }
    return { tests, files: [...byFile.values()].sort((a, b) => b.duration - a.duration || a.file.localeCompare(b.file)) };
}

const xml = (text) => String(text ?? '').replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])).replace(/[\u0000-\u0008\u000b\u000c\u000e-\u001f]/g, '');

/** The run as JUnit XML, which CI servers and PyCharm read: one testsuite per file, a testcase per test. */
export function junitXml(run) {
    const s = summarizeRun(run.results);
    const files = new Map();
    for (const r of run.results) {
        const file = r.id.split('::')[0];
        if (!files.has(file))
            files.set(file, []);
        files.get(file).push(r);
    }
    const out = [`<?xml version="1.0" encoding="UTF-8"?>`, `<testsuites name="PyMagic" tests="${s.total}" failures="${s.failed}" errors="${s.errors}" skipped="${s.skipped}" time="${s.duration.toFixed(3)}" timestamp="${xml(run.when)}">`];
    for (const [file, list] of files) {
        const fs = summarizeRun(list);
        out.push(`  <testsuite name="${xml(file)}" tests="${fs.total}" failures="${fs.failed}" errors="${fs.errors}" skipped="${fs.skipped}" time="${fs.duration.toFixed(3)}">`);
        for (const r of list) {
            const parts = r.id.split('::');
            const name = parts[parts.length - 1], classname = [file.replace(/\.py$/, '').replace(/\//g, '.'), ...parts.slice(1, -1)].join('.');
            const head = `    <testcase classname="${xml(classname)}" name="${xml(name)}" time="${(r.duration ?? 0).toFixed(3)}"`;
            if (r.outcome === 'failed' || r.outcome === 'xpassed')
                out.push(`${head}>`, `      <failure message="${xml(r.message)}">${xml(r.traceback ?? r.message)}</failure>`, '    </testcase>');
            else if (r.outcome === 'error')
                out.push(`${head}>`, `      <error message="${xml(r.message)}">${xml(r.traceback ?? r.message)}</error>`, '    </testcase>');
            else if (r.outcome === 'skipped')
                out.push(`${head}>`, `      <skipped message="${xml(r.message)}"/>`, '    </testcase>');
            else
                out.push(`${head}/>`);
        }
        out.push('  </testsuite>');
    }
    out.push('</testsuites>', '');
    return out.join('\n');
}

/** The run as a page of its own: the counts, then each test with its result and, for a failure, its message. */
export function htmlReport(run) {
    const s = summarizeRun(run.results);
    const color = { passed: '#2e7d32', xfailed: '#2e7d32', failed: '#c62828', error: '#c62828', xpassed: '#c62828', skipped: '#777' };
    const rows = run.results.map((r) => `<tr><td style="color:${color[r.outcome] ?? '#333'}">${xml(r.outcome)}</td><td>${xml(r.id)}</td><td>${(r.duration ?? 0).toFixed(3)} s</td><td><pre>${xml(r.message)}</pre></td></tr>`);
    return `<!DOCTYPE html>
<html lang="en"><head><meta charset="utf-8"><title>Test results ${xml(run.when)}</title>
<style>body{font:14px system-ui,sans-serif;margin:24px}table{border-collapse:collapse}td,th{border-bottom:1px solid #ddd;padding:4px 10px;text-align:left;vertical-align:top}pre{margin:0;white-space:pre-wrap}</style></head>
<body><h1>Test results</h1><p>${xml(run.when)} · ${xml(run.framework)} · ${s.total} tests: ${s.passed} passed, ${s.failed} failed, ${s.errors} errors, ${s.skipped} skipped · ${s.duration.toFixed(2)} s</p>
<table><tr><th>Result</th><th>Test</th><th>Time</th><th>Message</th></tr>
${rows.join('\n')}
</table></body></html>
`;
}

/** The history with a new run first, at most `max` runs. */
export function addRun(history, run, max = 20) {
    return [run, ...(Array.isArray(history) ? history : [])].slice(0, max);
}

/** The package a run that could not start needs, from what it printed: pytest, or Twisted for Trial (doctest and nose need none). */
export function missingRunner(text) {
    if (/cannot run tests with pytest|pytest is not installed/.test(text))
        return 'pytest';
    if (/cannot run tests with trial/.test(text))
        return 'twisted';
    return null;
}
