// The Django tab's logic that needs no page: the manage.py commands, reading a command line, and how a migration is named.
// [label, arguments, debug, hint]
export const COMMANDS = [
    ['Run server', ['runserver'], false, 'manage.py runserver'],
    ['Debug server', ['runserver', '--noreload'], true, 'manage.py runserver --noreload, under the debugger: breakpoints in your views stop the request (the reloader would start a second process the debugger does not see)'],
    ['migrate', ['migrate'], false, 'Apply the migrations'],
    ['makemigrations', ['makemigrations'], false, 'Create migrations for the changes of the models'],
    ['Missing migrations?', ['makemigrations', '--check', '--dry-run'], false, 'Changes nothing: fails when the models have changes without a migration'],
    ['showmigrations', ['showmigrations'], false, 'List the migrations and which are applied'],
    ['check', ['check'], false, 'Check the project for problems'],
    ['test', ['test'], false, 'Run the tests with Django\'s runner'],
    ['shell', ['shell'], false, 'A Python shell with the project loaded (type in the input box under the output)'],
    ['createsuperuser', ['createsuperuser'], false, 'Create an administrator (answer the questions in the input box)'],
    ['collectstatic', ['collectstatic', '--noinput'], false, 'Copy the static files to STATIC_ROOT'],
];
/** The words of a command line, with "double" and 'single' quotes grouping words. */
export function splitArguments(text) {
    const words = [];
    const re = /"([^"]*)"|'([^']*)'|(\S+)/g;
    let m;
    while ((m = re.exec(text)) !== null)
        words.push(m[1] ?? m[2] ?? m[3]);
    return words;
}
/** A migration's text for the list: `0001_initial` shown as its number and name. */
export function migrationLabel(m) {
    const at = m.name.indexOf('_');
    return at > 0 ? `${m.name.slice(0, at)}  ${m.name.slice(at + 1).replace(/_/g, ' ')}` : m.name;
}
