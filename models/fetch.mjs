// Fetches the model of full line completion into this folder, once: Qwen2.5-Coder-0.5B (Apache-2.0), quantized to Q8_0 by ggml-org, the one
// llama.vscode uses for fill-in-the-middle. It is 531 MB, too big for git, so it is not in the repo; desktop/package.mjs runs this before it packs
// the app, and the app carries the file (nothing is downloaded when the feature is used). The SHA-256 is pinned: a file that differs is refused.
// Run: node models/fetch.mjs
import { createHash } from 'node:crypto';
import { createReadStream, createWriteStream, existsSync, renameSync, rmSync } from 'node:fs';
import { resolve } from 'node:path';
import { Readable } from 'node:stream';
import { pipeline } from 'node:stream/promises';

export const MODEL = {
    file: 'qwen2.5-coder-0.5b-q8_0.gguf',
    url: 'https://huggingface.co/ggml-org/Qwen2.5-Coder-0.5B-Q8_0-GGUF/resolve/ecdd60b1c25ab158bec6e1ab084aeebf9eae0af4/qwen2.5-coder-0.5b-q8_0.gguf',
    sha256: 'd0f8cd6c49bab52a0abdbe47948518b1f4d9b1a8a2a6825099cea31cea10ac56',
};

const sha256Of = async (path) => {
    const hash = createHash('sha256');
    await pipeline(createReadStream(path), hash);
    return hash.digest('hex');
};

/** The model's path, fetched first when it is not here yet. */
export async function fetchModel(dir = import.meta.dirname) {
    const path = resolve(dir, MODEL.file);
    if (existsSync(path)) return path;
    const part = path + '.part';
    console.log(`Downloading ${MODEL.file} (531 MB) from Hugging Face...`);
    const response = await fetch(MODEL.url);
    if (!response.ok) throw new Error(`${MODEL.url}: ${response.status} ${response.statusText}`);
    await pipeline(Readable.fromWeb(response.body), createWriteStream(part));
    const got = await sha256Of(part);
    if (got !== MODEL.sha256) {
        rmSync(part, { force: true });
        throw new Error(`${MODEL.file}: SHA-256 ${got}, expected ${MODEL.sha256}`);
    }
    renameSync(part, path);
    return path;
}

if (process.argv[1] && resolve(process.argv[1]) === resolve(import.meta.filename))
    console.log(await fetchModel());
