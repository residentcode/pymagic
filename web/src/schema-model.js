// A small JSON Schema validator for what SchemaStore's schemas use (draft-07 and later): type, enum, const, required, properties,
// patternProperties, additionalProperties, unevaluatedProperties, propertyNames, min/maxProperties, items, minimum/maximum, lengths,
// pattern, $ref inside the schema (to a path or an $anchor), $dynamicRef to a $dynamicAnchor (OpenAPI 3.1's), allOf, anyOf, oneOf,
// not, if/then/else and dependentSchemas. A $ref to another document, `format`, and the keywords not listed are accepted.
// No DOM here: web/test/schema.test.mjs checks it.

const typeOf = (v) => (v === null ? 'null' : Array.isArray(v) ? 'array' : Number.isInteger(v) ? 'integer' : typeof v);
const isType = (v, t) => (t === 'number' ? typeof v === 'number' : t === 'integer' ? Number.isInteger(v) : typeOf(v) === t);

/** The nodes of a schema with an $anchor or a $dynamicAnchor, by name (found once per schema). */
const anchorsOf = new WeakMap();
function anchors(root) {
    if (anchorsOf.has(root)) return anchorsOf.get(root);
    const found = new Map();
    const visit = (node, depth) => {
        if (!node || typeof node !== 'object' || depth > 60) return;
        if (!Array.isArray(node)) {
            if (typeof node.$anchor === 'string' && !found.has(node.$anchor)) found.set(node.$anchor, node);
            if (typeof node.$id === 'string' && /^#[^/]/.test(node.$id) && !found.has(node.$id.slice(1))) found.set(node.$id.slice(1), node);
            if (typeof node.$dynamicAnchor === 'string' && !found.has(node.$dynamicAnchor)) found.set(node.$dynamicAnchor, node);
        }
        for (const v of Object.values(node)) visit(v, depth + 1);
    };
    visit(root, 0);
    anchorsOf.set(root, found);
    return found;
}

/** A schema address as ids are kept: no fragment, no trailing slash, http read as https (the server's Schemas.NormalId does the same). */
export function normalId(id) {
    let s = String(id);
    const hash = s.indexOf('#');
    if (hash >= 0) s = s.slice(0, hash);
    if (/^http:\/\//i.test(s)) s = 'https://' + s.slice(7);
    return s.replace(/\/+$/, '');
}

const SKIPPED = new Set(['enum', 'const', 'default', 'examples', 'example']);   // values, not schemas: an "$id" there is data

/**
 * Each node of a schema document with the document it belongs to and the address its relative $refs are read against, and the nodes that
 * carry an $id (AsyncAPI keeps its definitions under their own: "http://asyncapi.com/definitions/3.0.0/tag.json"). Draft-04's `id` counts in a
 * draft-04 document only. Built once per document.
 */
const indexes = new WeakMap();
function indexOf(root) {
    if (!root || typeof root !== 'object') return null;
    if (indexes.has(root)) return indexes.get(root);
    const draft4 = typeof root.$schema === 'string' && /draft-04/.test(root.$schema);
    const ctx = new WeakMap();
    const ids = new Map();
    const visit = (node, base, doc, depth) => {
        if (!node || typeof node !== 'object' || depth > 80) return;
        if (!Array.isArray(node)) {
            const own = typeof node.$id === 'string' ? node.$id : draft4 && typeof node.id === 'string' ? node.id : null;
            // "$id": "#name" is draft-06/07's way to name an anchor (anchors() finds it), not a document of its own
            if (own && !own.startsWith('#')) {
                try { base = new URL(own, base || undefined).href; } catch { base = own; }
                doc = node;
                ids.set(normalId(base), node);
            }
            ctx.set(node, { doc, base });
            for (const [k, v] of Object.entries(node)) if (!SKIPPED.has(k)) visit(v, base, doc, depth + 1);
        }
        else for (const v of node) visit(v, base, doc, depth + 1);
    };
    visit(root, '', root, 0);
    const index = { ctx, ids };
    indexes.set(root, index);
    return index;
}

/**
 * The addresses a schema $refs outside itself (another document: JSON Schema 7 from AsyncAPI, the vocabularies from the 2020-12 meta-schema),
 * normalized: the page loads those it has (built in, by their $id) before checking, and hands them to validate as its registry.
 */
export function externalRefs(root) {
    const index = indexOf(root);
    const out = new Set();
    if (!index) return out;
    const visit = (node, depth) => {
        if (!node || typeof node !== 'object' || depth > 80) return;
        if (!Array.isArray(node)) {
            const ref = node.$ref;
            if (typeof ref === 'string' && !ref.startsWith('#')) {
                const base = index.ctx.get(node)?.base;
                let abs;
                try { abs = new URL(ref, base || undefined).href; } catch { abs = ref; }
                const id = normalId(abs);
                if (!index.ids.has(id)) out.add(id);
            }
            for (const [k, v] of Object.entries(node)) if (!SKIPPED.has(k)) visit(v, depth + 1);
        }
        else for (const v of node) visit(v, depth + 1);
    };
    visit(root, 0);
    return out;
}

/** What a $ref of `from` names: in the same document, under an $id of the root's, or in a document of the registry (by its normalized id). */
function resolveRef(root, from, ref, registry) {
    const at = indexOf(root)?.ctx.get(from) ?? findContext(from, registry) ?? { doc: root, base: '' };
    if (ref.startsWith('#')) return resolve(at.doc, ref);
    let abs;
    try { abs = new URL(ref, at.base || undefined).href; } catch { abs = ref; }
    const hash = abs.indexOf('#');
    const id = normalId(abs);
    let target = indexOf(root)?.ids.get(id);
    if (!target && registry) {
        const doc = registry(id);
        if (doc) target = indexOf(doc)?.ids.get(id) ?? doc;
    }
    if (!target) return null;
    return hash >= 0 && abs.length > hash + 1 ? resolve(target, abs.slice(hash)) : target;
}

/** The context of a node of a registry document (a $ref reached into another document, whose own $refs are read against it). */
const registryDocs = new Set();
function findContext(node, registry) {
    if (!registry) return null;
    for (const doc of registryDocs) { const c = indexOf(doc)?.ctx.get(node); if (c) return c; }
    return null;
}

function resolve(root, ref) {
    if (!ref.startsWith('#')) return null;
    // #name: an anchor ($anchor, or the $dynamicAnchor a $dynamicRef names: within one document it is that node)
    if (ref.length > 1 && ref[1] !== '/') return typeof root === 'object' && root ? anchors(root).get(ref.slice(1)) ?? null : null;
    let node = root;
    for (const part of ref.slice(1).split('/').filter(Boolean)) {
        node = node?.[decodeURIComponent(part).replace(/~1/g, '/').replace(/~0/g, '~')];
        if (node === undefined) return null;
    }
    return node;
}

/**
 * Problems of `data` against `schema`: [{path: [keys and indexes], message}]. `registry(id)` gives another schema document by its normalized
 * id, for the $refs that leave this one (externalRefs says which); without it such a $ref is accepted.
 */
export function validate(data, schema, limit = 200, registry = null) {
    const out = [];
    if (registry) {
        // the registry's documents are indexed as they are reached, so their own relative $refs resolve
        const own = registry;
        registry = (id) => { const doc = own(id); if (doc) registryDocs.add(doc); return doc; };
    }
    // walk gives the names of the properties the node (and what it applies) dealt with, which `unevaluatedProperties` needs
    const walk = (value, node, path, depth) => {
        const evaluated = new Set();
        if (out.length >= limit) return evaluated;
        if (node === true || node == null || typeof node !== 'object' || depth > 40) return evaluated;
        if (node === false) { out.push({ path, message: 'Not allowed here' }); return evaluated; }
        const merge = (set) => { for (const k of set) evaluated.add(k); };
        if (typeof node.$ref === 'string') {
            const target = resolveRef(schema, node, node.$ref, registry);
            if (target) merge(walk(value, target, path, depth + 1));
        }
        // $dynamicRef (2020-12) and $recursiveRef (2019-09) name the outermost schema's anchor: the schema being checked against
        if (typeof node.$dynamicRef === 'string') {
            const target = resolve(schema, node.$dynamicRef) ?? resolveRef(schema, node, node.$dynamicRef, registry);
            if (target) merge(walk(value, target, path, depth + 1));
        }
        if (node.$recursiveRef === '#') merge(walk(value, schema, path, depth + 1));
        // not: the value must not fit; if/then/else: what fits `if` is held to `then`, the rest to `else`
        if (node.not !== undefined && node.not !== false && problemsOf(value, node.not, path, depth + 1).length === 0) {
            // `not` of properties that may not be given (Renovate's options of the global config): each one given is named
            const forms = node.not && typeof node.not === 'object' ? [node.not, ...(node.not.anyOf ?? [])] : [];
            const banned = forms.flatMap((f) => (Array.isArray(f.required) && Object.keys(f).every((k) => k === 'required') ? f.required : []));
            const given = value && typeof value === 'object' && !Array.isArray(value) ? banned.filter((k) => k in value) : [];
            if (given.length) for (const k of given) out.push({ path: [...path, k], message: `The property ${k} is not allowed here`, key: true });
            else out.push({ path, message: 'Should not be of this form' });
        }
        if (node.if !== undefined) {
            const passed = problemsOf(value, node.if, path, depth + 1);
            if (passed.length === 0) { merge(passed.evaluated); if (node.then !== undefined) merge(walk(value, node.then, path, depth + 1)); }
            else if (node.else !== undefined) merge(walk(value, node.else, path, depth + 1));
        }
        if (node.allOf) for (const sub of node.allOf) merge(walk(value, sub, path, depth + 1));
        if (node.anyOf || node.oneOf) {
            const branches = node.anyOf ?? node.oneOf;
            const results = branches.map((b) => problemsOf(value, b, path, depth + 1));
            const ok = results.filter((r) => r.length === 0).length;
            for (const r of results) if (r.length === 0) merge(r.evaluated);
            if (ok === 0) {
                // the branches that fit the value's type say what is wrong; the others only say it is the wrong type
                const near = results.filter((r) => !r.some((p) => p.path.length === path.length && /^Should be /.test(p.message)));
                // with several, the branch that fits best is the one meant (a job with a wrong `runs-on` is a normal job, not a reusable one)
                // (a property the branch does not know, or one it requires, counts for more than a wrong value: that branch is the wrong one)
                const score = (r) => r.filter((p) => p.key || p.missing).length * 1000 + r.length;
                const best = [...near].sort((x, y) => score(x) - score(y));
                if (near.length === 1 || (near.length > 1 && score(best[0]) < score(best[1]))) out.push(...best[0]);
                else out.push({ path, message: describeMismatch(value, branches, schema), generic: true });
            } else if (node.oneOf && ok > 1 && !node.anyOf) {
                out.push({ path, message: 'Matches more than one of the allowed forms' });
            }
        }
        if (node.type !== undefined) {
            const types = Array.isArray(node.type) ? node.type : [node.type];
            if (!types.some((t) => isType(value, t))) {
                out.push({ path, message: `Should be ${types.join(' or ')}, not ${typeOf(value)}` });
                return evaluated;
            }
        }
        if (node.const !== undefined && JSON.stringify(value) !== JSON.stringify(node.const)) out.push({ path, message: `Should be ${JSON.stringify(node.const)}` });
        if (node.enum && !node.enum.some((e) => JSON.stringify(e) === JSON.stringify(value)))
            out.push({ path, message: `Should be one of ${node.enum.map((e) => JSON.stringify(e)).join(', ')}` });
        if (typeof value === 'string') {
            if (node.minLength !== undefined && value.length < node.minLength) out.push({ path, message: `Should have at least ${node.minLength} characters` });
            if (node.maxLength !== undefined && value.length > node.maxLength) out.push({ path, message: `Should have at most ${node.maxLength} characters` });
            if (node.pattern) {
                try { if (!new RegExp(node.pattern, 'u').test(value)) out.push({ path, message: node.pattern.includes('\\$\\{\\{') ? 'Should be a ${{ expression }}' : `Should match ${node.pattern}` }); } catch { /* a pattern JavaScript cannot read */ }
            }
        }
        if (typeof value === 'number') {
            // draft-04 makes the bound itself exclusive with `exclusiveMinimum: true`; draft-06 and later give the bound as exclusiveMinimum
            if (node.minimum !== undefined && (node.exclusiveMinimum === true ? value <= node.minimum : value < node.minimum))
                out.push({ path, message: node.exclusiveMinimum === true ? `Should be more than ${node.minimum}` : `Should be at least ${node.minimum}` });
            if (node.maximum !== undefined && (node.exclusiveMaximum === true ? value >= node.maximum : value > node.maximum))
                out.push({ path, message: node.exclusiveMaximum === true ? `Should be less than ${node.maximum}` : `Should be at most ${node.maximum}` });
            if (typeof node.exclusiveMinimum === 'number' && value <= node.exclusiveMinimum) out.push({ path, message: `Should be more than ${node.exclusiveMinimum}` });
            if (typeof node.exclusiveMaximum === 'number' && value >= node.exclusiveMaximum) out.push({ path, message: `Should be less than ${node.exclusiveMaximum}` });
        }
        if (Array.isArray(value)) {
            if (node.minItems !== undefined && value.length < node.minItems) out.push({ path, message: `Should have at least ${node.minItems} items` });
            if (node.maxItems !== undefined && value.length > node.maxItems) out.push({ path, message: `Should have at most ${node.maxItems} items` });
            if (node.uniqueItems && new Set(value.map((v) => JSON.stringify(v))).size !== value.length) out.push({ path, message: 'The items should be different' });
            if (Array.isArray(node.items)) value.forEach((v, i) => walk(v, node.items[i] ?? node.additionalItems, [...path, i], depth + 1));
            else if (node.items) value.forEach((v, i) => walk(v, node.items, [...path, i], depth + 1));
        }
        if (value && typeof value === 'object' && !Array.isArray(value)) {
            for (const key of node.required ?? []) if (!(key in value)) out.push({ path, message: `The property ${key} is required`, missing: key });
            const count = Object.keys(value).length;
            if (node.minProperties !== undefined && count < node.minProperties) out.push({ path, message: `Should have at least ${node.minProperties} ${node.minProperties === 1 ? 'property' : 'properties'}` });
            if (node.maxProperties !== undefined && count > node.maxProperties) out.push({ path, message: `Should have at most ${node.maxProperties} ${node.maxProperties === 1 ? 'property' : 'properties'}` });
            if (node.propertyNames !== undefined)
                for (const key of Object.keys(value))
                    for (const p of problemsOf(key, node.propertyNames, [...path, key], depth + 1)) out.push({ ...p, message: `The name ${key}: ${p.message}`, key: true });
            for (const [key, sub] of Object.entries(node.dependentSchemas ?? {})) if (key in value) merge(walk(value, sub, path, depth + 1));
            const patterns = Object.entries(node.patternProperties ?? {}).flatMap(([p, s]) => { try { return [[new RegExp(p, 'u'), s]]; } catch { return []; } });
            for (const [key, v] of Object.entries(value)) {
                let matched = false;
                if (node.properties && key in node.properties) { matched = true; walk(v, node.properties[key], [...path, key], depth + 1); }
                for (const [re, s] of patterns) if (re.test(key)) { matched = true; walk(v, s, [...path, key], depth + 1); }
                if (matched) evaluated.add(key);
                if (!matched && node.additionalProperties !== undefined) {
                    evaluated.add(key);
                    if (node.additionalProperties === false) out.push({ path: [...path, key], message: `The property ${key} is not allowed here`, key: true });
                    else walk(v, node.additionalProperties, [...path, key], depth + 1);
                }
            }
            // what neither this node nor anything it applied (allOf, $ref, the branch that fitted) dealt with
            if (node.unevaluatedProperties !== undefined) {
                for (const [key, v] of Object.entries(value)) {
                    if (evaluated.has(key)) continue;
                    if (node.unevaluatedProperties === false) out.push({ path: [...path, key], message: `The property ${key} is not allowed here`, key: true });
                    else walk(v, node.unevaluatedProperties, [...path, key], depth + 1);
                    evaluated.add(key);
                }
            }
        }
        return evaluated;
    };
    const problemsOf = (value, node, path, depth) => {
        const keep = out.length;
        const evaluated = walk(value, node, path, depth);
        const found = out.splice(keep);
        found.evaluated = evaluated;
        return found;
    };
    walk(data, schema, [], 0);
    // "matches no allowed form" adds nothing when a problem inside the value already says what is wrong
    const seen = new Set();
    return out.filter((p) => { const k = JSON.stringify([p.path, p.message]); return !seen.has(k) && seen.add(k); }).filter((p) => !p.generic || !out.some((q) => q !== p && q.path.length > p.path.length && p.path.every((k, i) => q.path[i] === k)));
}

function describeMismatch(value, branches, schema) {
    const types = new Set();
    for (const b of branches) {
        const node = b?.$ref ? resolve(schema, b.$ref) ?? b : b;
        for (const t of [].concat(node?.type ?? [])) types.add(t);
        for (const e of node?.enum ?? []) types.add(JSON.stringify(e));
    }
    return types.size ? `Does not match any allowed form (${[...types].slice(0, 6).join(', ')})` : 'Does not match any allowed form';
}

/**
 * Where a problem's path is in a TOML or JSON text, found by the last key: {line, start, end} (1-based). The text is searched for the
 * keys of the path in order, so `[tool.poetry]`'s `name` is not confused with another table's.
 */
export function locate(text, problem) {
    const lines = text.split('\n');
    const keys = problem.path.filter((p) => typeof p === 'string');
    let from = 0;
    let found = null;
    for (let k = 0; k < keys.length; k++) {
        const key = keys[k];
        const esc = key.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
        const header = new RegExp(`^\\s*\\[\\[?[^\\]]*\\b${esc}\\b`);
        const assign = new RegExp(`^(\\s*)(?:"${esc}"|'${esc}'|${esc})\\s*[=:]`);
        for (let i = from; i < lines.length; i++) {
            let m = assign.exec(lines[i]);
            if (m) { found = { line: i + 1, start: m[1].length + 1, end: m[1].length + 1 + key.length }; from = i; break; }
            if (k < keys.length - 1 && header.test(lines[i])) { found = { line: i + 1, start: lines[i].indexOf(key) + 1, end: lines[i].indexOf(key) + 1 + key.length }; from = i; break; }
        }
    }
    if (problem.missing && keys.length) {
        // a missing property: the table or object it belongs to is the place
        return found ?? { line: 1, start: 1, end: 2 };
    }
    return found ?? { line: 1, start: 1, end: 2 };
}

/** Which schema checks a project file, by its path: 'package' | 'pyproject' | 'workflow' | 'compose' | 'gitlab' | 'precommit' | 'mkdocs' | 'dependabot' | 'codecov' | 'tox' | 'renovate' | 'openapi' | 'kustomization' | null.
 * 'openapi' is only the file's name: openapiSchemaOf says from the content which schema (3.1, 3.0 or Swagger 2.0) applies, if any. */
export function schemaOf(rel) {
    const lower = rel.toLowerCase();
    const name = lower.split('/').pop();
    if (/(^|\/)\.github\/workflows\/[^/]+\.ya?ml$/.test(lower)) return 'workflow';
    if (/^(docker-)?compose(\.[^/]+)?\.ya?ml$/.test(name)) return 'compose';
    if (/(^|\.)gitlab-ci\.ya?ml$/.test(name)) return 'gitlab';
    if (/^\.pre-commit-config\.ya?ml$/.test(name)) return 'precommit';
    if (/^mkdocs\.ya?ml$/.test(name)) return 'mkdocs';
    if (/(^|\/)\.github\/dependabot\.ya?ml$/.test(lower)) return 'dependabot';
    if (/^\.?codecov\.ya?ml$/.test(name)) return 'codecov';
    if (name === 'tox.toml') return 'tox';
    if (name === 'renovate.json' || name === '.renovaterc' || name === '.renovaterc.json') return 'renovate';
    if (/^(openapi|swagger)(\.[^/.]+)?\.(ya?ml|json)$/.test(name)) return 'openapi';
    if (/^kustomization\.ya?ml$/.test(name) || rel.split('/').pop() === 'Kustomization') return 'kustomization';
    // the files SchemaStore's catalog maps to the schemas PyCharm lists (asyncapi says by its content which version)
    if (/asyncapi[^/]*\.(json|ya?ml)$/.test(name)) return 'asyncapi';
    if (/(^|\/)\.devcontainer(\/[^/]+)?\/devcontainer\.json$/.test(lower) || name === '.devcontainer.json') return 'devcontainer';
    if (/^action\.ya?ml$/.test(name)) return 'github-action';
    if (/^(\.babelrc(\.json)?|babel\.config\.json)$/.test(name)) return 'babelrc';
    if (/^\.eslintrc(\.json|\.ya?ml)?$/.test(name)) return 'eslintrc';
    if (name === 'jest.config.json') return 'jest';
    if (/^jsconfig(\.[^/]+)?\.json$/.test(name)) return 'jsconfig';
    if (/^tsconfig(\.[^/]+)?\.json$/.test(name)) return 'tsconfig';
    if (/^\.prettierrc(\.json|\.ya?ml)?$/.test(name)) return 'prettierrc';
    if (/^\.stylelintrc(\.json|\.ya?ml)?$/.test(name)) return 'stylelintrc';
    if (/^tslint\.(json|ya?ml)$/.test(name)) return 'tslint';
    if (name === 'tsd.json') return 'tsd';
    if (name === 'typings.json') return 'typings';
    if (/^(mermaid\.config|\.?mermaidrc)\.json$/.test(name)) return 'mermaid';
    if (name === 'http-client.env.json') return 'http-client-env';
    if (name === 'http-client.private.env.json') return 'http-client-private-env';
    if (/\.(md|markdown)$/.test(name)) return 'markdown-frontmatter';
    return name === 'package.json' ? 'package' : name === 'pyproject.toml' ? 'pyproject' : null;
}

/** The AsyncAPI schema of a document, by its `asyncapi` version: 'asyncapi2' (2.x), 'asyncapi3' (3.x), or null. */
export function asyncapiSchemaOf(data) {
    const v = data && typeof data === 'object' ? String(data.asyncapi ?? '') : '';
    return /^2\./.test(v) ? 'asyncapi2' : /^3\./.test(v) ? 'asyncapi3' : null;
}

/** Files read as JSON with comments (JSONC): tsconfig, jsconfig, devcontainer.json, .eslintrc and the like allow // and /* *\/ and trailing commas. */
export function parseJsonc(text) {
    let out = '';
    let i = 0;
    while (i < text.length) {
        const c = text[i];
        if (c === '"') {
            let j = i + 1;
            while (j < text.length && text[j] !== '"') j += text[j] === '\\' ? 2 : 1;
            out += text.slice(i, j + 1);
            i = j + 1;
        }
        else if (c === '/' && text[i + 1] === '/') { while (i < text.length && text[i] !== '\n') i++; }
        else if (c === '/' && text[i + 1] === '*') { const end = text.indexOf('*/', i + 2); const gone = text.slice(i, end < 0 ? text.length : end + 2); out += gone.replace(/[^\n]/g, ' '); i = end < 0 ? text.length : end + 2; }
        else { out += c; i++; }
    }
    // a comma before a closing bracket
    return JSON.parse(out.replace(/,(\s*[}\]])/g, '$1'));
}

/** The YAML front matter of a Markdown text (between --- lines at its very top): {text, line} (line: where it starts, 0-based), or null. */
export function frontMatterOf(text) {
    const m = /^---\r?\n([\s\S]*?)\r?\n---\s*(\r?\n|$)/.exec(text);
    return m ? { text: m[1], line: 1 } : null;
}

/** A project path matched against a mapping's pattern: a path from the root, * within a name, ** for any folders, ? one character; no slash: any folder. */
export function globMatch(pattern, rel) {
    const p = pattern.replace(/\\/g, '/').replace(/^\.\//, '');
    let re = '';
    for (let i = 0; i < p.length; i++) {
        if (p.startsWith('**/', i)) { re += '(?:.*/)?'; i += 2; }
        else if (p.startsWith('/**', i) && i + 3 === p.length) { re += '(?:/.*)?'; i += 2; }
        else if (p.startsWith('**', i)) { re += '.*'; i += 1; }
        else if (p[i] === '*') re += '[^/]*';
        else if (p[i] === '?') re += '[^/]';
        else re += p[i].replace(/[.+^${}()|[\]]/g, '\\$&');
    }
    return new RegExp((p.includes('/') ? '^' : '^(?:.*/)?') + re + '$', 'i').test(rel.replace(/\\/g, '/'));
}

/**
 * Which schema checks a file, as PyCharm decides it: none when the project ignores the file; else the project's own mapping whose pattern
 * matches; else what the file says itself ("$schema", or YAML's `# yaml-language-server: $schema=`); else its name (schemaOf).
 * Gives {name} (a built-in schema), {file} (a .json in the project), {ignored: true}, or null; `why` says which rule chose it.
 */
export function chooseSchema(rel, project, declared) {
    const norm = rel.replace(/\\/g, '/');
    if ((project?.ignored ?? []).some((p) => p.replace(/\\/g, '/') === norm)) return { ignored: true, why: 'ignored' };
    for (const m of project?.mappings ?? []) {
        if (!(m.patterns ?? []).some((p) => globMatch(p, norm))) continue;
        return m.schema.startsWith('file:') ? { file: m.schema.slice(5), why: 'mapping', mapping: m.name } : { name: m.schema, why: 'mapping', mapping: m.name };
    }
    if (declared) return { ...declared, why: 'declared' };
    const name = schemaOf(norm);
    return name ? { name, why: 'name' } : null;
}

/**
 * What a file's own "$schema" names: a built-in schema by its $id ({name}), a .json path relative to the file ({file}, a path from the
 * project's root), or null (an address PyMagic has not got: nothing is downloaded). `ids` maps normalized ids to names.
 */
export function declaredSchema(value, ids, rel) {
    if (typeof value !== 'string' || !value) return null;
    const name = ids?.[normalId(value)];
    if (name) return { name };
    if (/^[a-z][a-z0-9+.-]*:/i.test(value)) return null;
    if (!/\.json$/i.test(value)) return null;
    const dir = rel.replace(/\\/g, '/').split('/').slice(0, -1);
    for (const part of value.replace(/\\/g, '/').split('/')) {
        if (part === '' || part === '.') continue;
        if (part === '..') { if (!dir.length) return null; dir.pop(); }
        else dir.push(part);
    }
    return { file: dir.join('/') };
}

/** YAML's way to name its schema: a `# yaml-language-server: $schema=<address>` comment in the file's first lines. */
export function yamlModeline(text) {
    const m = /^\s*#\s*yaml-language-server:\s*\$schema=(\S+)/m.exec(text.split('\n').slice(0, 10).join('\n'));
    return m ? m[1] : null;
}

/** The schema of an OpenAPI / Swagger document, by its version: 'openapi31' (3.1.x), 'openapi' (3.0.x), 'swagger' (2.0), or null. */
export function openapiSchemaOf(data) {
    if (!data || typeof data !== 'object') return null;
    if (typeof data.openapi === 'string' && /^3\.1(\.|$)/.test(data.openapi)) return 'openapi31';
    if (typeof data.openapi === 'string' && /^3\.0(\.|$)/.test(data.openapi)) return 'openapi';
    if (String(data.swagger) === '2.0' || String(data.swagger) === '2') return 'swagger';
    return null;
}

/** Whether a YAML text may hold Kubernetes manifests: a top-level apiVersion and kind, and no template markers. */
export function looksKubernetes(text) {
    return /^apiVersion:/m.test(text) && /^kind:/m.test(text) && !text.includes('{{');
}

/**
 * The name of the schema of one Kubernetes manifest (yannh/kubernetes-json-schema keeps one file per kind: deployment-apps-v1,
 * service-v1, ingress-networking-v1), or null when the document has no apiVersion and kind of that shape.
 */
export function kubernetesSchemaOf(doc) {
    if (!doc || typeof doc !== 'object' || typeof doc.apiVersion !== 'string' || typeof doc.kind !== 'string') return null;
    const [first, second] = doc.apiVersion.split('/');
    const group = second === undefined ? '' : first.split('.')[0];
    const version = second === undefined ? first : second;
    if (!/^[A-Za-z0-9]+$/.test(doc.kind) || !/^[a-z0-9]*$/.test(group) || !/^v\d+((alpha|beta)\d+)?$/.test(version)) return null;
    return ['k8s', doc.kind.toLowerCase(), group, version].filter(Boolean).join('-');
}

/**
 * The schemas of the custom resources the project's CustomResourceDefinitions declare, by `group/version:Kind`: each version's
 * openAPIV3Schema (apiextensions v1), or the definition's own (v1beta1's spec.validation). `docs` are the parsed YAML documents.
 */
export function crdSchemas(docs) {
    const found = new Map();
    for (const d of docs) {
        if (!d || typeof d !== 'object' || d.kind !== 'CustomResourceDefinition' || typeof d.apiVersion !== 'string' || !d.apiVersion.startsWith('apiextensions.k8s.io/')) continue;
        const group = d.spec?.group, kind = d.spec?.names?.kind;
        if (typeof group !== 'string' || typeof kind !== 'string') continue;
        const versions = Array.isArray(d.spec.versions) ? d.spec.versions : d.spec.version ? [{ name: d.spec.version }] : [];
        for (const v of versions) {
            const schema = v?.schema?.openAPIV3Schema ?? d.spec.validation?.openAPIV3Schema;
            if (typeof v?.name === 'string' && schema && typeof schema === 'object') found.set(`${group}/${v.name}:${kind}`, schema);
        }
    }
    return found;
}

/** The key of a manifest for crdSchemas: `group/version:Kind`, or null for a kind of the core group (no group, no CRD). */
export function crdKeyOf(doc) {
    if (!doc || typeof doc !== 'object' || typeof doc.apiVersion !== 'string' || typeof doc.kind !== 'string' || !doc.apiVersion.includes('/')) return null;
    return `${doc.apiVersion}:${doc.kind}`;
}
