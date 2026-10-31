// Python's colours: the grammar (which word is a control-flow keyword, a constant, an operator) and the meaning of each name from the
// language server (pymagic/semanticTokens: a module, a class, a parameter, self, a constant, a method, a builtin...), coloured by
// theme.js. The grammar is Monaco's own Python, with control flow, True / False / None and operators told apart, and the soft keywords
// (match, case, type, _) left to the server, which knows when they are keywords.
import * as monaco from 'monaco-editor';
import { language as pythonLanguage, conf as pythonConf } from 'monaco-editor/esm/vs/basic-languages/python/python.js';

/** The legend, in the order of LanguageService.SemanticTokenTypes / SemanticTokenModifiers. */
export const LEGEND = {
    tokenTypes: ['namespace', 'class', 'enum', 'typeParameter', 'parameter', 'selfParameter', 'variable', 'property', 'enumMember', 'function', 'method', 'decorator', 'softKeyword'],
    tokenModifiers: ['readonly', 'defaultLibrary', 'async', 'static', 'declaration'],
};

const CONTROL = ['if', 'elif', 'else', 'for', 'while', 'break', 'continue', 'return', 'yield', 'try', 'except', 'finally', 'raise', 'with', 'import', 'from', 'as', 'pass', 'await', 'async', 'assert', 'del'];
const CONSTANTS = ['True', 'False', 'None'];
const SOFT = ['_', 'match', 'case', 'type', 'print', 'exec'];

/** Monaco's Python grammar with control flow, constants and operators as tokens of their own. */
export function pythonGrammar() {
    const keywords = pythonLanguage.keywords.filter((k) => !CONTROL.includes(k) && !CONSTANTS.includes(k) && !SOFT.includes(k));
    const root = pythonLanguage.tokenizer.root.map((rule) => {
        if (Array.isArray(rule) && String(rule[0]) === String(/[a-zA-Z_]\w*/))
            return [/[a-zA-Z_]\w*/, { cases: { '@controlKeywords': 'keyword.control', '@constants': 'constant', '@keywords': 'keyword', '@default': 'identifier' } }];
        return rule;
    });
    // operators between the decorator rule and the names: = + - * / % < > ! & | ^ ~ and the arrow
    const at = root.findIndex((r) => Array.isArray(r) && String(r[0]) === String(/@[a-zA-Z_]\w*/));
    root.splice(at + 1, 0, [/->|[=+\-*/%<>!&|^~]+/, 'operator']);
    return { ...pythonLanguage, keywords, controlKeywords: CONTROL, constants: CONSTANTS, tokenizer: { ...pythonLanguage.tokenizer, root } };
}

export function setupSemantic(deps) {
    // Monaco sets its own Python grammar the first time Python is met (after loading it): ours goes in now and again after that
    const install = () => { monaco.languages.setMonarchTokensProvider('python', pythonGrammar()); monaco.languages.setLanguageConfiguration('python', pythonConf); };
    install();
    const encountered = monaco.languages.onLanguageEncountered ?? monaco.languages.onLanguage;
    encountered('python', () => { setTimeout(install, 0); setTimeout(install, 300); });
    monaco.languages.registerDocumentSemanticTokensProvider('python', {
        getLegend: () => LEGEND,
        async provideDocumentSemanticTokens(model) {
            const uri = deps.uriOf(model);
            if (!uri) return null;
            try {
                const res = await deps.lsp.request('pymagic/semanticTokens', { textDocument: { uri } });
                return res?.data ? { data: new Uint32Array(res.data) } : null;
            } catch {
                return null;
            }
        },
        releaseDocumentSemanticTokens() { },
    });
}
