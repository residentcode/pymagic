'use strict';

// The garbage collector settings of the engine process (the .NET host the shell starts).
//
// Reading a project allocates a lot and keeps almost all of it (the parsed files stay in the cache), so the default collector spends a third
// of the time of a big analysis paused, one thread copying what survives. A server collector with four heaps shares that work, and a small
// budget for the youngest generation keeps its memory where the default has it. Measured on the Problems of Django (907 files) and of
// transformers (2,169 files), against the default collector:
//   Django        5.2 s ->  3.9 s, peak 525 MB -> 520 MB
//   transformers 15.5 s ->  9.2 s, peak 2.1 GB -> 2.2 GB
// A short editing session of a small project takes 15 MB more. Without the budget the same collector needs 60 to 130% more memory, and the
// budget can only be given through the environment (the runtime configuration file does not read it), which is why it is set here and not
// in the project file.

// What each setting is, as the runtime names it (the values are hexadecimal: 800000 is 8 MB).
const SETTINGS = {
  DOTNET_gcServer: '1',
  DOTNET_GCHeapCount: '4',
  DOTNET_gcConcurrent: '0',
  DOTNET_GCgen0MaxBudget: '800000',
};

/**
 * The environment for the engine process: the given one with the settings added. A setting the user already made (any DOTNET_gc* variable)
 * stays as it is, and a computer with fewer than four cores keeps the default collector, which is what suits it.
 */
function hostEnvironment(cpuCount, env = process.env) {
  const out = { ...env };
  if (cpuCount < 4) return out;
  const names = Object.keys(out).map(k => k.toLowerCase());
  if (names.some(n => n.startsWith('dotnet_gc') || n.startsWith('complus_gc'))) return out;
  return Object.assign(out, SETTINGS);
}

module.exports = { hostEnvironment, SETTINGS };
