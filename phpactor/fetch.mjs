// Fetches Phpactor (PHP's language server, MIT License) into this folder, once: phpactor.phar of its release 2026.10.07.0. It is not in the repo;
// desktop/package.mjs runs this before it packs the app, and the app carries the file (nothing is downloaded when PHP's code insight is used).
// PyMagic starts it with the PHP of Settings > PHP through pm_phpactor.php, which lets it run on Windows' PHP (it has no posix extension).
// The SHA-256 is pinned: a file that differs is refused. Run: node phpactor/fetch.mjs
import { createHash } from 'node:crypto';
import { createReadStream, createWriteStream, existsSync, renameSync, rmSync } from 'node:fs';
import { resolve } from 'node:path';
import { Readable } from 'node:stream';
import { pipeline } from 'node:stream/promises';

export const PHPACTOR = {
    file: 'phpactor.phar',
    version: '2026.10.07.0',
    url: 'https://github.com/phpactor/phpactor/releases/download/2026.10.07.0/phpactor.phar',
    sha256: 'b28bf6166e016d539fc2f2aec18ef918b12590e436a6f1ccbdf30b5bbdbdb16b',
};

const sha256Of = async (path) => {
    const hash = createHash('sha256');
    await pipeline(createReadStream(path), hash);
    return hash.digest('hex');
};

/** The phar's path, fetched first when it is not here yet (or is not the pinned one). */
export async function fetchPhpactor(dir = import.meta.dirname) {
    const path = resolve(dir, PHPACTOR.file);
    if (existsSync(path) && await sha256Of(path) === PHPACTOR.sha256) return path;
    const part = path + '.part';
    console.log(`Downloading Phpactor ${PHPACTOR.version} (5 MB) from GitHub...`);
    const response = await fetch(PHPACTOR.url);
    if (!response.ok) throw new Error(`${PHPACTOR.url}: ${response.status} ${response.statusText}`);
    await pipeline(Readable.fromWeb(response.body), createWriteStream(part));
    const got = await sha256Of(part);
    if (got !== PHPACTOR.sha256) {
        rmSync(part, { force: true });
        throw new Error(`${PHPACTOR.file}: SHA-256 ${got}, expected ${PHPACTOR.sha256}`);
    }
    renameSync(part, path);
    return path;
}

if (process.argv[1] && resolve(process.argv[1]) === resolve(import.meta.filename))
    console.log(await fetchPhpactor());
