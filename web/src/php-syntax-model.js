// PHP's syntax check without PHP: the php-parser package (glayzzle's, PHP 8.4's grammar) on the editor's text, used when php -l cannot run
// (no PHP on this computer, Sail's container stopped). Like PHP it stops at the first error, so there is one finding at most. A few constructs
// PHP takes and the package does not are not told as errors: `instanceof (expression)`, `X::$name::new()`, and a named argument after `...$list`.
import phpParser from 'php-parser';

let engine = null;

// lines the package reads wrongly: an error on one of them is not PHP's
const MISREAD = [/\binstanceof\s*\(/i, /::\s*new\s*\(/i];

/** The first syntax error of a PHP file's text: { line, column (1-based), message }, or null when it has none. */
export function phpSyntaxError(text) {
    if (text.length > 2_000_000) return null;
    engine ??= new phpParser.Engine({ parser: { php8: true, suppressErrors: false, extractDoc: false, version: '8.4' }, ast: { withPositions: false } });
    try {
        engine.parseCode(text, 'file.php');
        return null;
    } catch (e) {
        if (e?.name !== 'SyntaxError' || !e.lineNumber) return null;
        if (/non-variadic argument after a variadic/i.test(e.message)) return null;
        const lineText = text.split('\n')[e.lineNumber - 1] ?? '';
        if (MISREAD.some((r) => r.test(lineText))) return null;
        const message = String(e.message).replace(/^Parse Error\s*:\s*/i, '').replace(/\s+on line \d+\s*$/i, '').replace(/^syntax error, /i, 'Syntax error: ');
        return { line: e.lineNumber, column: (e.columnNumber ?? 0) + 1, message: /^Syntax error/i.test(message) ? message : `Syntax error: ${message}` };
    }
}
