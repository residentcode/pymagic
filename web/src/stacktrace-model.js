// Reading a stack trace pasted from elsewhere (a log, a bug report, another machine): which lines name a file and a line, and
// which file of the project each one is, found by the longest ending of its path that a project file has.

const PATTERNS = [
    /File "([^"]+)", line (\d+)/,                         // Python's traceback
    /^\s*([^\s:"]+\.pyi?):(\d+)(?::\d+)?(?::|\s|$)/,      // pytest, flake8, mypy: path.py:12: or path.py:12:5:
    /\bat ([^\s()]+\.pyi?):(\d+)/,                         // "at path.py:12"
];

/** The project file a path written in a trace stands for: the one with the longest common ending of whole path parts, or null. */
export function projectFileFor(path, files) {
    const parts = path.replace(/\\/g, '/').split('/').filter(Boolean);
    let best = null, bestLength = 0, tie = false;
    for (const f of files) {
        const fp = f.split('/');
        let n = 0;
        while (n < fp.length && n < parts.length && fp[fp.length - 1 - n] === parts[parts.length - 1 - n]) n++;
        // the file's own name must agree; a longer agreement wins, and two files that agree as far stay unresolved
        if (n > bestLength) {
            best = f;
            bestLength = n;
            tie = false;
        }
        else if (n === bestLength && n > 0)
            tie = true;
    }
    return bestLength > 0 && !tie ? best : null;
}

/** Each line of the trace: {text, path, line, rel} where path and line are what the trace says (or null) and rel the project's file (or null). */
export function readTrace(text, files) {
    return text.replace(/\r\n/g, '\n').split('\n').map((lineText) => {
        for (const re of PATTERNS) {
            const m = re.exec(lineText);
            if (m)
                return { text: lineText, path: m[1], line: Number(m[2]), rel: projectFileFor(m[1], files) };
        }
        return { text: lineText, path: null, line: null, rel: null };
    });
}
