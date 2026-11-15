'use strict';

// The tools PyMagic's MCP server offers an AI agent (Claude Code, Cursor, VS Code, …), with PyCharm's names and parameters so that what an agent
// knows of PyCharm's server works here too. Each takes an optional projectPath: which open project (a PyMagic window) is meant; without it, the
// window last used. Paths are relative to the project's folder, with / or \. Carried out by the project's page (web/src/mcp-tools.js), except
// execute_terminal_command, which main.js runs.

const project = { projectPath: { type: 'string', description: 'The folder of the open project the call is for (as PyMagic shows it). Optional: without it, the project of the PyMagic window used last.' } };
const str = (description) => ({ type: 'string', description });
const int = (description) => ({ type: 'integer', description });
const bool = (description) => ({ type: 'boolean', description });
const tool = (name, description, properties, required = [], annotations = {}) => ({
  name, description,
  inputSchema: { type: 'object', properties: { ...properties, ...project }, required, additionalProperties: false },
  annotations: { readOnlyHint: false, destructiveHint: false, openWorldHint: false, ...annotations },
});
const reads = { readOnlyHint: true };

const TOOLS = [
  tool('get_file_text_by_path', 'The text of a project file, as the editor has it (with changes not saved yet).', {
    pathInProject: str('The file, from the project folder.'),
    maxLinesCount: int('At most this many lines (default 5000).'),
    truncateMode: { type: 'string', enum: ['START', 'MIDDLE', 'END', 'NONE'], description: 'Which lines are left out past maxLinesCount: START keeps the last ones, END the first ones (the default), MIDDLE both ends; NONE gives the whole text.' },
  }, ['pathInProject'], reads),
  tool('replace_text_in_file', 'Replaces text in a project file; an open file is changed in the editor (Undo takes it back) and saved.', {
    pathInProject: str('The file, from the project folder.'),
    oldText: str('The text to find.'),
    newText: str('What it is replaced with.'),
    replaceAll: bool('Every occurrence (the default) or only the first.'),
    caseSensitive: bool('Whether case must match (default true).'),
  }, ['pathInProject', 'oldText', 'newText']),
  tool('create_new_file', 'Makes a file in the project, with the folders it needs.', {
    pathInProject: str('The new file, from the project folder.'),
    text: str('Its text (empty by default).'),
    overwrite: bool('Write over a file that is there already (default false).'),
  }, ['pathInProject']),
  tool('find_files_by_name_keyword', 'Project files whose name has the keyword (case not minding).', {
    nameKeyword: str('A part of the file name.'),
    fileCountLimit: int('At most this many files (default 1000).'),
  }, ['nameKeyword'], reads),
  tool('find_files_by_glob', 'Project files whose path matches a glob: * within a name, ** any folders, ? one character.', {
    globPattern: str('For example **/*.py or src/**/test_*.py.'),
    subDirectoryRelativePath: str('Only under this folder of the project.'),
    fileCountLimit: int('At most this many files (default 1000).'),
  }, ['globPattern'], reads),
  tool('search_in_files_by_text', 'Searches the project\'s files for a text; each match with its file, line and the line\'s text (the match between ||).', {
    searchText: str('The text to find.'),
    directoryToSearch: str('Only under this folder of the project.'),
    fileMask: str('Only files matching these globs, comma-separated (*.py, *.html).'),
    caseSensitive: bool('Whether case must match (default true).'),
    maxUsageCount: int('At most this many matches (default 1000).'),
  }, ['searchText'], reads),
  tool('search_in_files_by_regex', 'Searches the project\'s files for a regular expression (.NET syntax); each match with its file, line and the line\'s text.', {
    regexPattern: str('The regular expression.'),
    directoryToSearch: str('Only under this folder of the project.'),
    fileMask: str('Only files matching these globs, comma-separated.'),
    caseSensitive: bool('Whether case must match (default true).'),
    maxUsageCount: int('At most this many matches (default 1000).'),
  }, ['regexPattern'], reads),
  tool('list_directory_tree', 'The folders and files under a folder of the project, as a tree.', {
    directoryPath: str('The folder, from the project folder (the project itself when empty).'),
    maxDepth: int('How many levels deep (default 5).'),
  }, [], reads),
  tool('get_file_problems', 'The errors and warnings PyMagic finds in a file: its inspections for Python, the editor\'s checks for every other kind (a file that is not open is read for it).', {
    filePath: str('The file, from the project folder.'),
    errorsOnly: bool('Only the errors (default false).'),
  }, ['filePath'], reads),
  tool('get_project_problems', 'The problems of every Python file of the project (the Problems panel\'s project check).', {
    errorsOnly: bool('Only the errors (default false).'),
    maxCount: int('At most this many (default 500).'),
  }, [], reads),
  tool('get_symbol_info', 'What PyMagic knows of the name at a place in a Python, JavaScript, TypeScript or PHP file: its kind, type, signature and documentation, as the editor\'s hover shows it, and where it is defined.', {
    filePath: str('The file, from the project folder.'),
    line: int('The line, from 1.'),
    column: int('The column, from 1.'),
  }, ['filePath', 'line', 'column'], reads),
  tool('rename_refactoring', 'Renames a symbol of a Python, JavaScript, TypeScript or PHP file (a class, function, method, variable or parameter) and every use of it in the project, as Refactor > Rename does.', {
    pathInProject: str('The file the symbol is defined in.'),
    symbolName: str('Its name; Class.method for a member.'),
    newName: str('The new name.'),
  }, ['pathInProject', 'symbolName', 'newName']),
  tool('reformat_file', 'Formats a file with the project\'s formatter (Code > Reformat Code) and saves it.', {
    path: str('The file, from the project folder.'),
  }, ['path']),
  tool('open_file_in_editor', 'Opens a file in PyMagic\'s editor, at a line when given.', {
    filePath: str('The file, from the project folder.'),
    line: int('A line to go to, from 1.'),
    column: int('A column on it, from 1.'),
  }, ['filePath']),
  tool('get_all_open_file_paths', 'The files open in the editor\'s tabs, and the one shown.', {}, [], reads),
  tool('get_run_configurations', 'The project\'s run configurations, each with its kind and what it starts.', {}, [], reads),
  tool('execute_run_configuration', 'Runs a run configuration and waits for it to end; gives its exit code and output. PyMagic asks the person first unless they chose not to be asked (Settings > MCP Server).', {
    configurationName: str('The name of the configuration.'),
    timeout: int('How long to wait in milliseconds (default 120000); a program still running then goes on.'),
    maxLinesCount: int('At most this many lines of output, the last ones (default 1000).'),
  }, ['configurationName'], { destructiveHint: true, openWorldHint: true }),
  tool('execute_terminal_command', 'Runs a shell command (cmd.exe) in the project folder, with the project\'s interpreter first on PATH, and gives its output and exit code. PyMagic asks the person first unless they chose not to be asked (Settings > MCP Server).', {
    command: str('The command line.'),
    timeout: int('How long to wait in milliseconds (default 120000); the command is stopped then.'),
    maxLinesCount: int('At most this many lines of output, the last ones (default 1000).'),
  }, ['command'], { destructiveHint: true, openWorldHint: true }),
  tool('get_quick_fixes', 'The quick fixes PyMagic offers in a file (Alt+Enter on a problem), Python or another language: each with its line and title. Without a line, those of every line that has a problem.', {
    filePath: str('The file, from the project folder.'),
    line: int('Only this line, from 1.'),
  }, ['filePath'], reads),
  tool('apply_quick_fix', 'Carries out a quick fix of a file\'s line (as get_quick_fixes names it) and saves what it changed.', {
    filePath: str('The file, from the project folder.'),
    line: int('The line, from 1.'),
    title: str('The fix\'s title, or a part of it.'),
  }, ['filePath', 'line', 'title']),
  tool('get_project_vcs_status', 'The project\'s Git state: its branch, what it tracks, and every changed file with what happened to it.', {}, [], reads),
  tool('find_commit_by_message', 'The commits whose message has a text, the newest first: hash, date, author and subject.', {
    text: str('The text to find in commit messages.'),
    maxCount: int('At most this many (default 50).'),
  }, ['text'], reads),
  tool('toggle_debugger_breakpoint', 'Sets a breakpoint on a line of a file, or takes away the one that is there.', {
    filePathInProject: str('The file, from the project folder.'),
    line: int('The line, from 1.'),
  }, ['filePathInProject', 'line']),
  tool('get_debugger_breakpoints', 'The breakpoints of the files PyMagic has read: file, line and, when set, condition and log message.', {}, [], reads),
  tool('get_git_branches', 'The project\'s Git branches: the current one, the local ones and the remotes\'.', {}, [], reads),
  tool('create_git_branch', 'Makes a Git branch at the current commit and switches to it (checkout: false leaves the current branch as it is).', {
    name: str('The new branch\'s name.'),
    checkout: bool('Switch to it (default true).'),
  }, ['name']),
  tool('checkout_git_branch', 'Switches the project to another Git branch (git switch); Git refuses when changes not committed would be lost. PyMagic asks the person first unless they chose not to be asked.', {
    name: str('The branch.'),
  }, ['name'], { destructiveHint: true }),
  tool('commit_changes', 'Commits changed files to Git with a message, as PyMagic\'s Commit window does (files not tracked yet are added). PyMagic asks the person first unless they chose not to be asked.', {
    message: str('The commit message.'),
    paths: str('The files or folders to commit, from the project folder, separated by commas or line breaks; every changed file without it.'),
  }, ['message'], { destructiveHint: true }),
  tool('start_debugger_session', 'Starts a run configuration, or a file, under PyMagic\'s debugger and waits for its first stop (a breakpoint, an exception) or its end; says where it is paused, with the stack and the output. PyMagic asks the person first unless they chose not to be asked.', {
    configurationName: str('The run configuration to debug (get_run_configurations lists them).'),
    filePath: str('Or a file to debug, from the project folder.'),
    arguments: str('The file\'s command-line arguments.'),
    timeout: int('How long to wait for the first stop in milliseconds (default 30000); the program goes on after it.'),
  }, [], { destructiveHint: true, openWorldHint: true }),
  tool('get_debugger_state', 'What the debug session is at: paused where and why with its stack, running, or ended with its exit code; and the program\'s output.', {}, [], reads),
  tool('debugger_step', 'Goes on with the paused program and waits for its next stop or its end: CONTINUE to the next breakpoint, STEP_OVER the line, STEP_INTO the call, STEP_OUT of the function; PAUSE stops a running one.', {
    action: { type: 'string', enum: ['CONTINUE', 'STEP_OVER', 'STEP_INTO', 'STEP_OUT', 'PAUSE'], description: 'What to do.' },
    timeout: int('How long to wait for the next stop in milliseconds (default 30000).'),
  }, ['action']),
  tool('get_debugger_variables', 'The values of a frame of the paused program: its local names, each with type and value; with an expression, that value\'s members (attributes, items).', {
    frame: int('The frame, as the stack numbers it (default 0, the innermost).'),
    expression: str('A name or expression whose members are wanted (self, config["db"], rows[0]).'),
    maxCount: int('At most this many of each (default 200).'),
  }, [], reads),
  tool('evaluate_debugger_expression', 'Evaluates an expression in a frame of the paused program and gives its value.', {
    expression: str('The expression, in the program\'s language.'),
    frame: int('The frame, as the stack numbers it (default 0).'),
  }, ['expression']),
  tool('stop_debugger_session', 'Stops the program that is being run or debugged.', {}, []),
  tool('get_project_dependencies', 'The packages installed in the project\'s Python interpreter, with their versions.', {}, [], reads),
  tool('get_project_info', 'The project: its folder, its Python interpreter, and the other projects open in PyMagic.', {}, [], reads),
];

/** The tools main.js carries out itself (not the page). */
const IN_MAIN = new Set(['execute_terminal_command', 'get_project_info']);

/** A call's arguments checked against the tool's schema: the required ones there, each of its type; an unknown argument is an error. Gives a message, or null. */
function checkArguments(def, args) {
  if (args == null) args = {};
  if (typeof args !== 'object' || Array.isArray(args)) return 'the arguments must be an object';
  const props = def.inputSchema.properties;
  for (const key of def.inputSchema.required) if (args[key] === undefined || args[key] === null) return `${key} is required`;
  for (const [key, value] of Object.entries(args)) {
    const p = props[key];
    if (!p) return `${def.name} has no argument ${key}`;
    if (value === null || value === undefined) continue;
    if (p.type === 'string' && typeof value !== 'string') return `${key} must be a string`;
    if (p.type === 'integer' && !Number.isInteger(value)) return `${key} must be a whole number`;
    if (p.type === 'boolean' && typeof value !== 'boolean') return `${key} must be true or false`;
    if (p.enum && !p.enum.includes(value)) return `${key} must be one of ${p.enum.join(', ')}`;
  }
  return null;
}

module.exports = { TOOLS, IN_MAIN, checkArguments };
