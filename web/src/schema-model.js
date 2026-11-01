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
    // walk gives the names of the properties the node (and what it applies) dealt with, which `unevaluatedProperties` needs
    const walk = (value, node, path, depth) => {
        const evaluated = new Set();
        if (out.length >= limit) return evaluated;
        if (node === true || node == null || typeof node !== 'object' || depth > 40) return evaluated;
        if (node === false) { out.push({ path, message: 'Not allowed here' }); return evaluated; }
        const merge = (set) => { for (const k of set) evaluated.add(k); };
        if (node.$ref) {
            const target = resolve(schema, node.$ref);
            if (target) merge(walk(value, target, path, depth + 1));
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

/** Which schema checks a project file, by its path: 'package' | 'pyproject' | 'workflow' | 'compose' | 'gitlab' | 'precommit' | 'mkdocs' | 'dependabot' | 'codecov' | 'tox' | 'renovate' | 'openapi' | null.
 * 'openapi' is only the file's name: openapiSchemaOf says from the content which schema (3.0 or Swagger 2.0) applies, if any. */
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
    return name === 'package.json' ? 'package' : name === 'pyproject.toml' ? 'pyproject' : null;
}

/** The schema of an OpenAPI / Swagger document, by its version: 'openapi' (3.0.x), 'swagger' (2.0), or null (3.1 uses $dynamicRef, which the validator cannot follow, so it is not checked). */
export function openapiSchemaOf(data) {
    if (!data || typeof data !== 'object') return null;
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
