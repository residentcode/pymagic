// Whether web/node_modules lacks a package web/package.json asks for (or has another version of it),
// so a pull that adds a package (xterm.js, say) installs it before the page is built.
const fs = require('node:fs');
const path = require('node:path');

function missingWebPackages(web) {
  const wanted = JSON.parse(fs.readFileSync(path.join(web, 'package.json'), 'utf8'));
  const all = { ...wanted.dependencies, ...wanted.devDependencies };
  return Object.entries(all).filter(([name, version]) => {
    try {
      const got = JSON.parse(fs.readFileSync(path.join(web, 'node_modules', name, 'package.json'), 'utf8')).version;
      return /^\d/.test(version) && got !== version;
    } catch { return true; }
  }).map(([name]) => name);
}

module.exports = { missingWebPackages };
