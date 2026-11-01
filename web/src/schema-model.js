// A small JSON Schema validator for what SchemaStore's schemas use (draft-07 and later): type, enum, const, required, properties,
// patternProperties, additionalProperties, items, minimum/maximum, lengths, pattern, $ref inside the schema, allOf, anyOf, oneOf.
// A $ref to another document, `format`, and the keywords not listed are accepted. No DOM here: web/test/schema.test.mjs checks it.

const typeOf = (v) => (v === null ? 'null' : Array.isArray(v) ? 'array' : Number.isInteger(v) ? 'integer' : typeof v);
const isType = (v, t) => (t === 'number' ? typeof v === 'number' : t === 'integer' ? Number.isInteger(v) : typeOf(v) === t);

function resolve(root, ref) {
    if (!ref.startsWith('#')) return null;
    let node = root;
    for (const part of ref.slice(1).split('/').filter(Boolean)) {
        node = node?.[decodeURIComponent(part).replace(/~1/g, '/').replace(/~0/g, '~')];
        if (node === undefined) return null;
    }
    return node;
}

/** Problems of `data` against `schema`: [{path: [keys and indexes], message}]. */
export function validate(data, schema, limit = 200) {
    const out = [];
    const walk = (value, node, path, depth) => {
        if (out.length >= limit) return;
        if (node === true || node == null || typeof node !== 'object' || depth > 40) return;
        if (node === false) { out.push({ path, message: 'Not allowed here' }); return; }
        if (node.$ref) {
            const target = resolve(schema, node.$ref);
            if (target) walk(value, target, path, depth + 1);
        }
        if (node.allOf) for (const sub of node.allOf) walk(value, sub, path, depth + 1);
        if (node.anyOf || node.oneOf) {
            const branches = node.anyOf ?? node.oneOf;
            const results = branches.map((b) => problemsOf(value, b, path, depth + 1));
            const ok = results.filter((r) => r.length === 0).length;
            if (ok === 0) {
                // the branches that fit the value's type say what is wrong; the others only say it is the wrong type
                const near = results.filter((r) => !r.some((p) => p.path.length === path.length && /^Should be /.test(p.message)));
                if (near.length === 1) out.push(...near[0]);
                else out.push({ path, message: describeMismatch(value, branches, schema), generic: true });
            } else if (node.oneOf && ok > 1 && !node.anyOf) {
                out.push({ path, message: 'Matches more than one of the allowed forms' });
            }
        }
        if (node.type !== undefined) {
            const types = Array.isArray(node.type) ? node.type : [node.type];
            if (!types.some((t) => isType(value, t))) {
                out.push({ path, message: `Should be ${types.join(' or ')}, not ${typeOf(value)}` });
                return;
            }
        }
        if (node.const !== undefined && JSON.stringify(value) !== JSON.stringify(node.const)) out.push({ path, message: `Should be ${JSON.stringify(node.const)}` });
        if (node.enum && !node.enum.some((e) => JSON.stringify(e) === JSON.stringify(value)))
            out.push({ path, message: `Should be one of ${node.enum.map((e) => JSON.stringify(e)).join(', ')}` });
        if (typeof value === 'string') {
            if (node.minLength !== undefined && value.length < node.minLength) out.push({ path, message: `Should have at least ${node.minLength} characters` });
            if (node.maxLength !== undefined && value.length > node.maxLength) out.push({ path, message: `Should have at most ${node.maxLength} characters` });
            if (node.pattern) {
                try { if (!new RegExp(node.pattern, 'u').test(value)) out.push({ path, message: `Should match ${node.pattern}` }); } catch { /* a pattern JavaScript cannot read */ }
            }
        }
        if (typeof value === 'number') {
            if (node.minimum !== undefined && value < node.minimum) out.push({ path, message: `Should be at least ${node.minimum}` });
            if (node.maximum !== undefined && value > node.maximum) out.push({ path, message: `Should be at most ${node.maximum}` });
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
            const patterns = Object.entries(node.patternProperties ?? {}).flatMap(([p, s]) => { try { return [[new RegExp(p, 'u'), s]]; } catch { return []; } });
            for (const [key, v] of Object.entries(value)) {
                let matched = false;
                if (node.properties && key in node.properties) { matched = true; walk(v, node.properties[key], [...path, key], depth + 1); }
                for (const [re, s] of patterns) if (re.test(key)) { matched = true; walk(v, s, [...path, key], depth + 1); }
                if (!matched && node.additionalProperties !== undefined) {
                    if (node.additionalProperties === false) out.push({ path: [...path, key], message: `The property ${key} is not allowed here`, key: true });
                    else walk(v, node.additionalProperties, [...path, key], depth + 1);
                }
            }
        }
    };
    const problemsOf = (value, node, path, depth) => {
        const keep = out.length;
        walk(value, node, path, depth);
        return out.splice(keep);
    };
    walk(data, schema, [], 0);
    // "matches no allowed form" adds nothing when a problem inside the value already says what is wrong
    return out.filter((p) => !p.generic || !out.some((q) => q !== p && q.path.length > p.path.length && p.path.every((k, i) => q.path[i] === k)));
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
