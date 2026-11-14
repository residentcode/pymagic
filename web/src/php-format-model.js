// PHP's layout without PHP. Reformat Code: Prettier's PHP plugin (PER coding style, PHP 8.4) when the project's Pint or PHP CS Fixer cannot run
// (no PHP on this computer, no formatter in the project). While typing: the lines whose indentation does not follow the nesting of { } blocks,
// as PSR-12 / PER (and Pint) lay it out: a statement 4 columns in from the line that opened its block, a closing } at that line's indentation.
// Only what is certain is told: a line inside an open ( or [ (a call's arguments, an array), a statement's continuation (a chained ->call, a
// condition on two lines), a comment, a switch's cases and a template with ?> HTML or endif; are left alone.
import * as prettier from 'prettier/standalone';
import phpPlugin from '@prettier/plugin-php/standalone';
import phpParser from 'php-parser';

/** The indentation a file uses: tabs when most of its indented lines start with one. */
export function usesTabs(text) {
    let tabs = 0, spaces = 0;
    for (const line of text.split('\n')) {
        if (line.startsWith('\t')) tabs++;
        else if (line.startsWith('    ')) spaces++;
    }
    return tabs > spaces;
}

/** The text laid out as Prettier's PHP plugin has it (PER coding style: 4 spaces, or the file's tabs). Throws on a syntax error. */
export async function formatPhpText(text) {
    return prettier.format(text, {
        parser: 'php', plugins: [phpPlugin], phpVersion: '8.4', tabWidth: 4, useTabs: usesTabs(text), printWidth: 120,
        singleQuote: true, trailingCommaPHP: true, braceStyle: 'per-cs', endOfLine: text.includes('\r\n') ? 'crlf' : 'lf',
    });
}

let lexer = null;
const COMMENTS = new Set(['T_COMMENT', 'T_DOC_COMMENT']);
const TEMPLATE_ENDS = new Set(['T_ENDIF', 'T_ENDFOREACH', 'T_ENDWHILE', 'T_ENDFOR', 'T_ENDSWITCH', 'T_ENDDECLARE']);
const MEMBER = new Set(['T_OBJECT_OPERATOR', 'T_NULLSAFE_OBJECT_OPERATOR', 'T_DOUBLE_COLON', '$']);
const columns = (indent) => { let c = 0; for (const ch of indent) c += ch === '\t' ? 4 - (c % 4) : 1; return c; };
const describe = (n, tabs) => (n === 0 ? 'none' : tabs ? `${n / 4} tab${n / 4 === 1 ? '' : 's'}` : `${n} space${n === 1 ? '' : 's'}`);

/**
 * The lines whose indentation does not follow the { } blocks they are in: [{ line (1-based), actual, expected (columns), message }]. Empty for
 * a text that does not lex or is a template.
 */
export function indentProblems(text, limit = 200) {
    return scan(text, limit).found;
}

/**
 * The text with each line's indentation as its { } blocks have it (the check's findings fixed, the lines that go with a moved line moved
 * along: a statement's continuation, a block's inside), nothing else changed: no line wrapped or joined, strings and heredocs as they were.
 */
export function fixIndentation(text) {
    const tabs = usesTabs(text);
    const eol = text.includes('\r\n') ? '\r\n' : '\n';
    const lines = text.split(/\r?\n/);
    for (let pass = 0; pass < 2000; pass++) {
        const { found, checked, movable } = scan(lines.join('\n'), 1);
        if (!found.length) break;
        const p = found[0];
        // the lines that move with it: up to the next line checked at its depth or outside it
        const i = checked.findIndex((c) => c.line === p.line - 1);
        let end = lines.length - 1;
        for (let k = i + 1; k < checked.length; k++) if (checked[k].depth <= checked[i].depth) { end = checked[k].line - 1; break; }
        const delta = p.expected - p.actual;
        for (let l = p.line - 1; l <= end; l++) {
            if (!movable.has(l) || !lines[l].trim()) continue;
            const old = /^[ \t]*/.exec(lines[l])[0];
            const cols = Math.max(0, columns(old) + delta);
            lines[l] = (tabs ? '\t'.repeat(Math.floor(cols / 4)) + ' '.repeat(cols % 4) : ' '.repeat(cols)) + lines[l].slice(old.length);
        }
    }
    return lines.join(eol);
}

function scan(text, limit) {
    const none = { found: [], checked: [], movable: new Set() };
    if (text.length > 1_000_000) return none;
    lexer ??= new phpParser.Engine({ parser: { php8: true }, lexer: { all_tokens: true } });
    let tokens;
    try { tokens = lexer.tokenGetAll(text); } catch { return none; }
    const lineStarts = [0];
    for (let i = 0; i < text.length; i++) if (text[i] === '\n') lineStarts.push(i + 1);
    const lineOf = (pos) => { let lo = 0, hi = lineStarts.length - 1; while (lo < hi) { const mid = (lo + hi + 1) >> 1; if (lineStarts[mid] <= pos) lo = mid; else hi = mid - 1; } return lo; };
    const indentAt = (line) => columns(/^[ \t]*/.exec(text.slice(lineStarts[line], lineStarts[line + 1] ?? text.length))[0]);
    const tabs = usesTabs(text);

    const stack = [];            // { kind: '{' | '(' | 's' (an expression's or a string's brace), line, base, switch, attr }
    const found = [], checked = [], movable = new Set();
    let inHeredoc = false;
    let pos = 0, ended = true, sawSwitch = false, lastSig = null, startLine = -1, atStart = [];
    for (const tok of tokens) {
        const [type, value] = typeof tok === 'string' ? [tok, tok] : tok;
        // the token's place in the text (the lexer leaves out the whitespace inside an attribute's #[ ]): after blanks it skipped
        let at = pos;
        while (at < text.length && !text.startsWith(value, at) && /\s/.test(text[at])) at++;
        if (!text.startsWith(value, at)) return none;
        pos = at + value.length;
        if (type === 'T_INLINE_HTML' && value.trim() || TEMPLATE_ENDS.has(type)) return none;
        if (type === 'T_WHITESPACE') continue;
        const line = lineOf(at);
        // the first token of a line: its indentation checked when its place is certain
        if (line !== startLine && !text.slice(lineStarts[line], at).trim()) {
            startLine = line;
            // a line may move when it starts with its own token, not inside a heredoc (whose text would change)
            if (!inHeredoc && type !== 'T_END_HEREDOC') movable.add(line);
            let block = null;
            for (let k = stack.length - 1; k >= 0; k--) if (stack[k].kind === '{') { block = stack[k]; break; }
            const inside = stack.slice(stack.indexOf(block) + 1);
            atStart = inside.filter((e) => e.kind === '(');
            const closing = type === '}';
            if (!COMMENTS.has(type) && !inside.length && !(block?.switch && !closing) && (closing || ended)) {
                const blocks = stack.filter((e) => e.kind === '{').length;
                checked.push({ line, depth: closing ? blocks - 1 : blocks });
                const base = block ? block.base : 0;
                const expected = closing ? base : block ? base + 4 : 0;
                const actual = indentAt(line);
                if (actual !== expected && type !== 'T_OPEN_TAG' && found.length < limit)
                    found.push({ line: line + 1, actual, expected, message: `Indentation is ${describe(actual, tabs)}, expected ${describe(expected, tabs)} (Fix indentation: Alt+Enter)` });
            }
        }
        if (type === 'T_START_HEREDOC') inHeredoc = true;
        else if (type === 'T_END_HEREDOC') inHeredoc = false;
        if (COMMENTS.has(type)) continue;
        if (type === '(' || type === '[' || type === 'T_ATTRIBUTE') stack.push({ kind: '(', line, attr: type === 'T_ATTRIBUTE' });
        else if (type === ')' || type === ']') {
            const top = stack[stack.length - 1];
            if (top?.kind === '(') stack.pop();
            if (top?.attr) { ended = true; lastSig = ']'; continue; }
        }
        else if (type === 'T_CURLY_OPEN' || type === 'T_DOLLAR_OPEN_CURLY_BRACES') stack.push({ kind: 's' });
        else if (type === '{') {
            if (MEMBER.has(lastSig)) stack.push({ kind: 's' });
            else {
                // the block's lines go in from the line that opened it; when this line closes a ( opened on an earlier line (a condition on two
                // lines: `$b) {`), from the line of that (
                const closed = atStart.find((e) => !stack.includes(e));
                stack.push({ kind: '{', line, base: indentAt(closed ? closed.line : line), switch: sawSwitch });
            }
        }
        let blockEnd = false;
        if (type === '}') {
            while (stack.length && stack[stack.length - 1].kind === '(') stack.pop();
            // a block's end ends a statement; not the } of `->{$name}` or "{$x}", nor of a block on one line (`fn () { $a[] = 1; }` goes on)
            const open = stack.pop();
            blockEnd = open?.kind === '{' && open.line !== line;
        }
        if (type === 'T_SWITCH') sawSwitch = true;
        lastSig = type;
        ended = type === ';' || (type === '{' && stack[stack.length - 1]?.kind === '{') || blockEnd || type === 'T_OPEN_TAG';
        if (ended) sawSwitch = false;
    }
    return { found, checked, movable };
}
