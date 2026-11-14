// The live templates PyMagic comes with, beside Python's own and the Django template ones (those are the language server's, which knows where a
// statement starts and adds a template's import). A group is a name, where its templates apply (live-templates-model.js's CONTEXTS), whether
// they are only offered at the start of a line, and its templates as text: "abbreviation | description", then the template, a line of --- between
// two. In the text $name$ is a place Tab goes to (the same name twice is typed once), $END$ where the cursor ends and $SELECTION$ the text that
// was selected; four spaces are one level of indentation.

const sets = [];
const group = (name, contexts, lineStart, text) => sets.push({ name, contexts, lineStart, text });

group('Django', ['python'], true, `
model | Django model
class $Name$(models.Model):
    $field$ = models.CharField(max_length=$length$)

    def __str__(self):
        return self.$field$
---
mchar | CharField
$name$ = models.CharField(max_length=$length$)
---
mtext | TextField
$name$ = models.TextField(blank=True)
---
mint | IntegerField
$name$ = models.IntegerField(default=$value$)
---
mbool | BooleanField
$name$ = models.BooleanField(default=$value$)
---
mdate | DateTimeField
$name$ = models.DateTimeField(auto_now_add=True)
---
mfk | ForeignKey
$name$ = models.ForeignKey($Model$, on_delete=models.CASCADE, related_name='$related$')
---
mm2m | ManyToManyField
$name$ = models.ManyToManyField($Model$, related_name='$related$')
---
meta | class Meta
class Meta:
    ordering = ['$field$']
---
view | view function
def $name$(request):
    return render(request, '$template$', {$END$})
---
cbv | TemplateView
class $Name$View(TemplateView):
    template_name = '$template$'
---
listview | ListView
class $Name$ListView(ListView):
    model = $Model$
    paginate_by = $count$
---
detailview | DetailView
class $Name$DetailView(DetailView):
    model = $Model$
---
form | Form
class $Name$Form(forms.Form):
    $field$ = forms.CharField(max_length=$length$)
---
modelform | ModelForm
class $Name$Form(forms.ModelForm):
    class Meta:
        model = $Model$
        fields = [$END$]
---
admin | ModelAdmin
@admin.register($Model$)
class $Model$Admin(admin.ModelAdmin):
    list_display = [$END$]
---
path | path() of urlpatterns
path('$route$/', views.$view$, name='$name$'),
---
urls | urls.py
from django.urls import path

from . import views

app_name = '$app$'
urlpatterns = [
    path('', views.$view$, name='$name$'),
]
---
signal | signal receiver
@receiver($signal$, sender=$Model$)
def $name$(sender, instance, **kwargs):
    $END$
---
command | management command
class Command(BaseCommand):
    help = '$help$'

    def handle(self, *args, **options):
        $END$
---
testcase | TestCase
class $Name$Tests(TestCase):
    def test_$case$(self):
        $END$
---
serializer | REST framework ModelSerializer
class $Name$Serializer(serializers.ModelSerializer):
    class Meta:
        model = $Model$
        fields = [$END$]
---
viewset | REST framework ModelViewSet
class $Name$ViewSet(viewsets.ModelViewSet):
    queryset = $Model$.objects.all()
    serializer_class = $Serializer$
`);

group('Flask', ['python'], true, `
flapp | Flask application
app = Flask(__name__)
---
route | route
@app.route('/$path$')
def $name$():
    return $END$
---
routep | route with methods
@app.route('/$path$', methods=['GET', 'POST'])
def $name$():
    if request.method == 'POST':
        $END$
    return render_template('$template$')
---
flget | GET route
@app.get('/$path$')
def $name$():
    return $END$
---
flpost | POST route
@app.post('/$path$')
def $name$():
    return $END$
---
blueprint | Blueprint
$name$ = Blueprint('$name$', __name__, url_prefix='/$prefix$')
---
bproute | Blueprint route
@$bp$.route('/$path$')
def $name$():
    return $END$
---
errorhandler | error handler
@app.errorhandler($code$)
def $name$(error):
    return $END$, $code$
---
beforereq | before_request
@app.before_request
def $name$():
    $END$
---
teardown | teardown_appcontext
@app.teardown_appcontext
def $name$(exception):
    $END$
---
flcli | command of the flask program
@app.cli.command('$name$')
def $function$():
    $END$
---
factory | application factory
def create_app(config=None):
    app = Flask(__name__)
    app.config.from_mapping(config or {})
    $END$
    return app
---
flrun | app.run under __main__
if __name__ == '__main__':
    app.run(debug=True)
`);

group('FastAPI', ['python'], true, `
faapp | FastAPI application
app = FastAPI()
---
faget | GET operation
@app.get('/$path$')
async def $name$():
    return $END$
---
fapost | POST operation
@app.post('/$path$')
async def $name$($item$: $Model$):
    return $END$
---
faput | PUT operation
@app.put('/$path$/{$id$}')
async def $name$($id$: int, $item$: $Model$):
    return $END$
---
fadel | DELETE operation
@app.delete('/$path$/{$id$}')
async def $name$($id$: int):
    return $END$
---
farouter | APIRouter
router = APIRouter(prefix='/$prefix$', tags=['$tag$'])
---
famodel | pydantic model
class $Name$(BaseModel):
    $field$: $type$
---
fadep | dependency
def $name$():
    $resource$ = $create$
    try:
        yield $resource$
    finally:
        $END$
---
faexc | raise HTTPException
raise HTTPException(status_code=$code$, detail='$detail$')
---
famw | HTTP middleware
@app.middleware('http')
async def $name$(request: Request, call_next):
    response = await call_next(request)
    $END$
    return response
`);

group('JavaScript', ['javascript'], false, `
fori | for loop with an index
for (let $i$ = 0; $i$ < $array$.length; $i$++) {
    $END$
}
---
forof | for...of loop
for (const $item$ of $items$) {
    $END$
}
---
forin | for...in loop
for (const $key$ in $object$) {
    $END$
}
---
foreach | forEach call
$items$.forEach(($item$) => {
    $END$
});
---
if | if statement
if ($condition$) {
    $END$
}
---
ife | if / else
if ($condition$) {
    $first$
} else {
    $END$
}
---
sw | switch statement
switch ($value$) {
    case $one$:
        $END$
        break;
    default:
        break;
}
---
while | while loop
while ($condition$) {
    $END$
}
---
try | try / catch
try {
    $END$
} catch ($error$) {
    console.error($error$);
}
---
tryf | try / finally
try {
    $body$
} finally {
    $END$
}
---
fn | function
function $name$($params$) {
    $END$
}
---
afn | async function
async function $name$($params$) {
    $END$
}
---
arf | arrow function
const $name$ = ($params$) => {
    $END$
};
---
iife | function called at once
(() => {
    $END$
})();
---
cl | class
class $Name$ {
    constructor($params$) {
        $END$
    }
}
---
log | console.log
console.log($END$);
---
logv | console.log of a name and its value
console.log('$value$:', $value$);
---
err | console.error
console.error($END$);
---
imp | import
import $name$ from '$module$';
---
impn | import of names
import { $names$ } from '$module$';
---
req | require
const $name$ = require('$module$');
---
exp | export
export const $name$ = $END$;
---
expd | export default
export default $END$;
---
prom | new Promise
new Promise((resolve, reject) => {
    $END$
});
---
then | then / catch
$promise$
    .then(($result$) => {
        $END$
    })
    .catch(($error$) => {
        console.error($error$);
    });
---
timeout | setTimeout
setTimeout(() => {
    $END$
}, $ms$);
---
interval | setInterval
setInterval(() => {
    $END$
}, $ms$);
---
fetch | fetch of JSON
const $response$ = await fetch($url$);
const $data$ = await $response$.json();
---
ael | addEventListener
$target$.addEventListener('$event$', ($e$) => {
    $END$
});
---
qs | document.querySelector
document.querySelector('$selector$')
---
us | 'use strict'
'use strict';
---
todo | TODO comment
// TODO: $END$
`);

group('JavaScript Testing', ['javascript'], false, `
describe | describe block
describe('$subject$', () => {
    $END$
});
---
it | it test
it('$does$', () => {
    $END$
});
---
ita | async it test
it('$does$', async () => {
    $END$
});
---
test | test
test('$does$', () => {
    $END$
});
---
testa | async test
test('$does$', async () => {
    $END$
});
---
bfa | beforeAll
beforeAll(() => {
    $END$
});
---
bfe | beforeEach
beforeEach(() => {
    $END$
});
---
afa | afterAll
afterAll(() => {
    $END$
});
---
afe | afterEach
afterEach(() => {
    $END$
});
---
expb | expect(...).toBe(...)
expect($actual$).toBe($expected$);
---
expe | expect(...).toEqual(...)
expect($actual$).toEqual($expected$);
---
expt | expect(...).toThrow()
expect(() => $call$).toThrow($error$);
---
mock | jest.fn mock
const $name$ = jest.fn($END$);
---
spy | jest.spyOn
const $name$ = jest.spyOn($object$, '$method$');
`);

group('Express', ['javascript'], false, `
exapp | Express application
const express = require('express');

const app = express();
app.use(express.json());

$END$

app.listen($port$, () => {
    console.log('Listening on port $port$');
});
---
exget | GET route
app.get('/$path$', (req, res) => {
    $END$
});
---
expost | POST route
app.post('/$path$', (req, res) => {
    $END$
});
---
exput | PUT route
app.put('/$path$/:id', (req, res) => {
    $END$
});
---
exdel | DELETE route
app.delete('/$path$/:id', (req, res) => {
    $END$
});
---
exrouter | Router of its own file
const express = require('express');

const router = express.Router();

router.get('/', (req, res) => {
    $END$
});

module.exports = router;
---
exmw | middleware
function $name$(req, res, next) {
    $END$
    next();
}
---
exerr | error handling middleware
app.use((err, req, res, next) => {
    console.error(err);
    res.status(err.status || 500).json({ error: err.message });
});
---
exuse | app.use of a router
app.use('/$path$', $router$);
---
exstatic | static files
app.use(express.static('$folder$'));
---
exrender | res.render
res.render('$view$', { $END$ });
---
exjson | res.json
res.json($END$);
---
exstatus | res.status(...).json
res.status($code$).json({ error: '$message$' });
`);

group('React', ['javascript'], false, `
rfc | function component
export default function $Name$($props$) {
    return (
        $END$
    );
}
---
rus | useState
const [$value$, set$Value$] = useState($initial$);
---
rue | useEffect
useEffect(() => {
    $END$
}, [$deps$]);
---
rur | useRef
const $name$ = useRef($initial$);
---
rum | useMemo
const $name$ = useMemo(() => $value$, [$deps$]);
---
rucb | useCallback
const $name$ = useCallback(($params$) => {
    $END$
}, [$deps$]);
---
ruc | useContext
const $name$ = useContext($Context$);
`);

group('TypeScript', ['typescript'], false, `
int | interface
interface $Name$ {
    $END$
}
---
type | type alias
type $Name$ = $END$;
---
enum | enum
enum $Name$ {
    $END$
}
---
tfn | function with types
function $name$($param$: $type$): $result$ {
    $END$
}
---
gen | generic function
function $name$<$T$>($param$: $T$): $T$ {
    $END$
}
`);

group('HTML/XML', ['html'], false, `
html5 | HTML page
<!DOCTYPE html>
<html lang="$lang$">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>$title$</title>
</head>
<body>
    $END$
</body>
</html>
---
viewport | viewport meta tag
<meta name="viewport" content="width=device-width, initial-scale=1.0">
---
css | link to a stylesheet
<link rel="stylesheet" href="$href$">
---
js | script from a file
<script src="$src$"></script>
---
jsm | module script
<script type="module" src="$src$"></script>
---
ahref | link
<a href="$href$">$text$</a>
---
imgsrc | image
<img src="$src$" alt="$alt$">
---
formp | form posted
<form action="$action$" method="post">
    $END$
</form>
---
inp | labelled input
<label for="$id$">$label$</label>
<input type="$type$" id="$id$" name="$id$">
---
sel | select with options
<select name="$name$" id="$name$">
    <option value="$value$">$text$</option>
</select>
---
tbl | table
<table>
    <thead>
        <tr>
            <th>$heading$</th>
        </tr>
    </thead>
    <tbody>
        <tr>
            <td>$END$</td>
        </tr>
    </tbody>
</table>
---
uli | list
<ul>
    <li>$END$</li>
</ul>
---
btn | button
<button type="$type$">$text$</button>
---
cmt | comment
<!-- $END$ -->
`);

group('CSS', ['css'], false, `
flexc | flex container, centred
display: flex;
align-items: center;
justify-content: center;
---
gridc | grid of columns
display: grid;
grid-template-columns: repeat($count$, 1fr);
gap: $gap$;
---
media | media query by width
@media (max-width: $width$) {
    $END$
}
---
dark | media query for a dark scheme
@media (prefers-color-scheme: dark) {
    $END$
}
---
kf | keyframes
@keyframes $name$ {
    from {
        $first$
    }
    to {
        $END$
    }
}
---
ff | font face
@font-face {
    font-family: '$family$';
    src: url('$url$') format('woff2');
    font-display: swap;
}
---
imp | import
@import url('$url$');
---
var | custom property used
var(--$name$)
---
root | custom properties of the page
:root {
    --$name$: $value$;
}
---
trans | transition
transition: $property$ $time$ ease;
---
ell | text cut with an ellipsis
overflow: hidden;
text-overflow: ellipsis;
white-space: nowrap;
---
abs | positioned over its parent
position: absolute;
inset: 0;
`);

group('SQL', ['sql'], false, `
sel | select
SELECT $columns$
FROM $table$;
---
selw | select with a condition
SELECT $columns$
FROM $table$
WHERE $condition$;
---
selc | count of rows
SELECT COUNT(*)
FROM $table$;
---
ins | insert
INSERT INTO $table$ ($columns$)
VALUES ($values$);
---
upd | update
UPDATE $table$
SET $column$ = $value$
WHERE $condition$;
---
del | delete
DELETE FROM $table$
WHERE $condition$;
---
join | inner join
INNER JOIN $table$ ON $left$ = $right$
---
ljoin | left join
LEFT JOIN $table$ ON $left$ = $right$
---
grp | group by
SELECT $column$, COUNT(*) AS $count$
FROM $table$
GROUP BY $column$
ORDER BY $count$ DESC;
---
tab | create table
CREATE TABLE $table$ (
    id INTEGER PRIMARY KEY,
    $column$ $type$ NOT NULL
);
---
col | add a column
ALTER TABLE $table$ ADD COLUMN $column$ $type$;
---
idx | create index
CREATE INDEX $name$ ON $table$ ($columns$);
---
view | create view
CREATE VIEW $name$ AS
SELECT $columns$
FROM $table$;
---
cte | with (common table expression)
WITH $name$ AS (
    SELECT $columns$
    FROM $table$
)
SELECT *
FROM $name$;
---
case | case expression
CASE
    WHEN $condition$ THEN $value$
    ELSE $other$
END
---
tx | transaction
BEGIN;
$END$
COMMIT;
`);

group('Shell Script', ['shell'], false, `
sb | bash first line and strict mode
#!/usr/bin/env bash
set -euo pipefail
---
if | if statement
if [[ $condition$ ]]; then
    $END$
fi
---
ife | if / else
if [[ $condition$ ]]; then
    $first$
else
    $END$
fi
---
for | for loop over items
for $item$ in $items$; do
    $END$
done
---
fori | for loop with a counter
for (( $i$ = 0; $i$ < $count$; $i$++ )); do
    $END$
done
---
while | while loop
while [[ $condition$ ]]; do
    $END$
done
---
readl | read a file line by line
while IFS= read -r $line$; do
    $END$
done < "$$file$"
---
case | case statement
case "$$value$" in
    $pattern$)
        $END$
        ;;
    *)
        ;;
esac
---
fn | function
$name$() {
    $END$
}
---
cmd | check that a program is there
if ! command -v $program$ > /dev/null; then
    echo "$program$ is not installed" >&2
    exit 1
fi
---
here | here document
cat <<EOF
$END$
EOF
---
dir | folder of the script
$script_dir$="$(cd "$(dirname "$0")" && pwd)"
`);

group('HTTP Request', ['http'], false, `
gtr | GET request
GET http://$host$/$path$
Accept: application/json

###
---
gtrp | GET request with parameters
GET http://$host$/$path$?$param$=$value$
Accept: application/json

###
---
ptr | POST request with a JSON body
POST http://$host$/$path$
Content-Type: application/json

{
    $END$
}

###
---
ptrp | POST request of a form
POST http://$host$/$path$
Content-Type: application/x-www-form-urlencoded

$param$=$value$

###
---
mptr | POST request of a file (multipart)
POST http://$host$/$path$
Content-Type: multipart/form-data; boundary=boundary

--boundary
Content-Disposition: form-data; name="$field$"; filename="$name$"

< $file$
--boundary--

###
---
putr | PUT request with a JSON body
PUT http://$host$/$path$
Content-Type: application/json

{
    $END$
}

###
---
delr | DELETE request
DELETE http://$host$/$path$

###
---
auth | Authorization header with a token
Authorization: Bearer $token$
---
basic | Authorization header with a name and password
Authorization: Basic $user$ $password$
`);

group('PHP', ['php'], false, `
cls | class
class $Name$
{
    $END$
}
---
con | constructor
public function __construct($params$)
{
    $END$
}
---
pubf | public method
public function $name$($params$)
{
    $END$
}
---
prif | private method
private function $name$($params$)
{
    $END$
}
---
pubsf | public static method
public static function $name$($params$)
{
    $END$
}
---
fn | function
function $name$($params$)
{
    $END$
}
---
fore | foreach
foreach ($$items$ as $$item$) {
    $END$
}
---
forek | foreach with keys
foreach ($$items$ as $$key$ => $$value$) {
    $END$
}
---
if | if statement
if ($condition$) {
    $END$
}
---
ife | if / else
if ($condition$) {
    $first$
} else {
    $END$
}
---
try | try / catch
try {
    $END$
} catch (\\Throwable $e) {
    throw $e;
}
---
ns | namespace
namespace $Name$;
---
eco | echo
echo $END$;
`);

group('Laravel', ['php'], false, `
rget | GET route
Route::get('/$path$', [$Controller$::class, '$method$'])->name('$name$');
---
rpost | POST route
Route::post('/$path$', [$Controller$::class, '$method$'])->name('$name$');
---
rres | resource routes
Route::resource('$name$', $Controller$::class);
---
rgroup | route group
Route::middleware('$middleware$')->prefix('$prefix$')->group(function () {
    $END$
});
---
action | controller action
public function $name$(Request $request)
{
    return view('$view$', [$END$]);
}
---
valid | validated input of the request
$validated = $request->validate([
    '$field$' => 'required|$rule$',
]);
---
dd | dd()
dd($END$);
---
fill | fillable attributes of a model
protected $fillable = [
    '$field$',
];
---
hasmany | hasMany relation
public function $name$()
{
    return $this->hasMany($Model$::class);
}
---
belongs | belongsTo relation
public function $name$()
{
    return $this->belongsTo($Model$::class);
}
---
migcol | column of a migration
$table->$type$('$name$');
---
migtab | table of a migration
Schema::create('$table$', function (Blueprint $table) {
    $table->id();
    $END$
    $table->timestamps();
});
`);

group('Blade', ['blade'], false, `
if | @if ... @endif
@if ($condition$)
    $END$
@endif
---
ife | @if ... @else ... @endif
@if ($condition$)
    $first$
@else
    $END$
@endif
---
foreach | @foreach ... @endforeach
@foreach ($$items$ as $$item$)
    $END$
@endforeach
---
forelse | @forelse ... @empty ... @endforelse
@forelse ($$items$ as $$item$)
    $first$
@empty
    $END$
@endforelse
---
extends | @extends
@extends('$layout$')
---
section | @section ... @endsection
@section('$name$')
    $END$
@endsection
---
yield | @yield
@yield('$name$')
---
include | @include
@include('$view$')
---
csrf | @csrf
@csrf
---
auth | @auth ... @endauth
@auth
    $END$
@endauth
---
php | @php ... @endphp
@php
    $END$
@endphp
---
var | {{ ... }}
{{ $END$ }}
`);

group('OpenAPI Specifications (.yaml)', ['yaml'], false, `
oapi | OpenAPI document
openapi: 3.1.0
info:
    title: $title$
    version: $version$
paths:
    $END$
---
opath | path with a GET operation
/$path$:
    get:
        summary: $summary$
        responses:
            '200':
                description: $description$
---
oparam | parameter
- name: $name$
  in: $where$
  required: true
  schema:
      type: $type$
---
oresp | response with a JSON body
'$code$':
    description: $description$
    content:
        application/json:
            schema:
                $END$
---
oschema | object schema
$Name$:
    type: object
    required:
        - $field$
    properties:
        $field$:
            type: $type$
---
oref | reference to a schema
$ref: '#/components/schemas/$Name$'
`);

group('OpenAPI Specifications (.json)', ['json'], false, `
oapi | OpenAPI document
{
    "openapi": "3.1.0",
    "info": {
        "title": "$title$",
        "version": "$version$"
    },
    "paths": {
        $END$
    }
}
---
opath | path with a GET operation
"/$path$": {
    "get": {
        "summary": "$summary$",
        "responses": {
            "200": {
                "description": "$description$"
            }
        }
    }
}
---
oschema | object schema
"$Name$": {
    "type": "object",
    "required": ["$field$"],
    "properties": {
        "$field$": {
            "type": "$type$"
        }
    }
}
---
oref | reference to a schema
"$ref": "#/components/schemas/$Name$"
`);

/** The groups as data: [{ name, contexts, lineStart, templates: [{ abbr, description, body }] }], the body as it is written here. */
export const GROUPS = sets.map(({ name, contexts, lineStart, text }) => ({
    name, contexts, lineStart,
    templates: text.split(/^---$/m).map((block) => {
        const lines = block.replace(/^\n+|\n+$/g, '').split('\n');
        const [abbr, description] = lines[0].split(' | ');
        return { abbr: abbr.trim(), description: (description ?? '').trim(), body: lines.slice(1).join('\n') };
    }).filter((t) => t.abbr),
}));
