using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PyMagic.Server;

/// <summary>
/// PHP's tests in the Tests panel: PHPUnit's test classes (methods named test…, or marked #[Test] / @test) and Pest's tests (test('…') and
/// it('…'), in describe('…') blocks too), found by reading the files under the folders phpunit.xml names (tests/ when it names none). Ids
/// are as Python's: <c>tests/Unit/MathTest.php::MathTest::test_adds</c>, <c>tests/Unit/ExampleTest.php::it adds</c>; a data set's case
/// is <c>…::test_pairs[same]</c>. A run is PHPUnit's or Pest's own command with --teamcity, whose messages (TeamCityReader) become the panel's.
/// </summary>
public static class PhpTests
{
    /// <summary>Whether the project has PHP tests to run: Composer's PHPUnit or Pest, or a phpunit.xml.</summary>
    public static bool Applies(string root) =>
        File.Exists(Path.Combine(root, "composer.json")) &&
        (File.Exists(Path.Combine(root, "vendor", "bin", "phpunit")) || File.Exists(Path.Combine(root, "vendor", "bin", "pest")) ||
         File.Exists(Path.Combine(root, "phpunit.xml")) || File.Exists(Path.Combine(root, "phpunit.xml.dist")));

    /// <summary>The project's test runner: "pest" when Pest is installed (it runs PHPUnit's tests too), else "phpunit".</summary>
    public static string Runner(string root) => File.Exists(Path.Combine(root, "vendor", "bin", "pest")) ? "pest" : "phpunit";

    /// <summary>The folders of the test suites phpunit.xml names (relative to the project), or tests/.</summary>
    static List<string> SuiteFolders(string root)
    {
        var folders = new List<string>();
        foreach (var name in new[] { "phpunit.xml", "phpunit.xml.dist" })
        {
            string file = Path.Combine(root, name);
            if (!File.Exists(file)) continue;
            try
            {
                string xml = File.ReadAllText(file);
                int suites = xml.IndexOf("<testsuites", StringComparison.Ordinal);
                if (suites >= 0)
                    foreach (Match m in Regex.Matches(xml[suites..], @"<directory[^>]*>\s*([^<]+?)\s*</directory>"))
                        folders.Add(m.Groups[1].Value.Replace("./", "").TrimEnd('/', '\\'));
            }
            catch (IOException) { }
            break;
        }
        if (folders.Count == 0) folders.Add("tests");
        return folders.Distinct().ToList();
    }

    /// <summary>The major version of the project's PHPUnit (vendor/phpunit/phpunit), or null when it has none.</summary>
    static int? PhpUnitMajor(string root)
    {
        try
        {
            string file = Path.Combine(root, "vendor", "phpunit", "phpunit", "src", "Runner", "Version.php");
            return File.Exists(file) && Regex.Match(File.ReadAllText(file), @"VersionId\(\s*'(\d+)\.") is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;
        }
        catch (IOException) { return null; }
    }

    /// <summary>The project's test files (*Test.php under the suites' folders), each with its tests; files without tests are left out.</summary>
    public static List<TestNode> Discover(string root)
    {
        var result = new List<TestNode>();
        if (!Applies(root)) return result;
        bool docblocks = PhpUnitMajor(root) is not >= 12;
        var files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in SuiteFolders(root))
        {
            string dir = Path.GetFullPath(Path.Combine(root, folder));
            if (!dir.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) || !Directory.Exists(dir)) continue;
            try { foreach (var f in Directory.EnumerateFiles(dir, "*Test.php", SearchOption.AllDirectories)) files.Add(f); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        foreach (var file in files)
        {
            string text;
            try { text = File.ReadAllText(file); } catch (IOException) { continue; }
            if (DiscoverFile(Path.GetRelativePath(root, file).Replace('\\', '/'), text, docblocks) is { } node) result.Add(node);
        }
        return result;
    }

    /// <summary>The tests of one PHP file: a PHPUnit class with its test methods, or Pest's tests; null when it has none.</summary>
    /// <param name="docblocks">Whether a docblock's @test marks a test: PHPUnit 11 and older (PHPUnit 12 reads attributes only).</param>
    public static TestNode? DiscoverFile(string rel, string text, bool docblocks = true)
    {
        string code = LaravelLinks.CodeOnly(text);
        int LineAt(int at) { int n = 1; for (int i = 0; i < at && i < text.Length; i++) if (text[i] == '\n') n++; return n; }
        var children = new List<TestNode>();

        // PHPUnit: class XTest extends …TestCase (an abstract class is a base, not a test)
        foreach (Match c in Regex.Matches(code, @"(?<![\w$])(abstract\s+|final\s+|readonly\s+)*class\s+(\w+)\s+extends\s+[\w\\]+[^{]*\{"))
        {
            if (c.Groups[1].Captures.Any(x => x.Value.TrimEnd() == "abstract")) continue;
            string cls = c.Groups[2].Value;
            int open = c.Index + c.Length - 1, close = LaravelLinks.ClosingBrace(code, open);
            var methods = new List<TestNode>();
            foreach (Match m in Regex.Matches(code[open..close], @"(?<![\w$])public\s+(?:static\s+)?function\s+(\w+)\s*\("))
            {
                string name = m.Groups[1].Value;
                int at = open + m.Index;
                // marked as a test before it: #[Test] or a docblock's @test (the text between the last statement and the method)
                int before = Math.Max(code.LastIndexOf(';', at), Math.Max(code.LastIndexOf('}', at), code.LastIndexOf('{', at)));
                string head = text[(before + 1)..at];
                bool marked = Regex.IsMatch(head, @"#\[\s*(?:\\?PHPUnit\\Framework\\Attributes\\)?Test\s*[\](]") || (docblocks && Regex.IsMatch(head, @"@test\b"));
                if (!name.StartsWith("test", StringComparison.Ordinal) && !marked) continue;
                int body = code.IndexOf('{', at);
                int end = body >= 0 && body < close ? LaravelLinks.ClosingBrace(code, body) : at;
                methods.Add(new TestNode($"{rel}::{cls}::{name}", name, "method", rel, LineAt(at), LineAt(end), new()));
            }
            if (methods.Count > 0) children.Add(new TestNode($"{rel}::{cls}", cls, "class", rel, LineAt(c.Index), LineAt(close), methods));
        }

        // Pest: test('…', …) and it('…', …) (whose name is "it …"), at the top or in describe('…', …), whose name goes before theirs
        if (children.Count == 0)
        {
            var blocks = new List<(int Start, int End, string Name)>();
            foreach (Match d in Regex.Matches(text, @"(?<![\w$>:])describe\s*\(\s*(['""])((?:(?!\1).)*)\1"))
            {
                if (code[d.Index] != 'd') continue;
                int paren = d.Index + d.Value.IndexOf('(');
                blocks.Add((d.Index, CloseParen(code, paren), d.Groups[2].Value));
            }
            foreach (Match t in Regex.Matches(text, @"(?<![\w$>:])(test|it)\s*\(\s*(['""])((?:(?!\2).)*)\2"))
            {
                if (code[t.Index] != t.Groups[1].Value[0]) continue;
                string name = (t.Groups[1].Value == "it" ? "it " : "") + t.Groups[3].Value;
                string path = string.Concat(blocks.Where(b => b.Start < t.Index && t.Index < b.End).OrderBy(b => b.Start).Select(b => "`" + b.Name + "` → "));
                int end = CloseParen(code, t.Index + t.Value.IndexOf('('));
                children.Add(new TestNode($"{rel}::{path}{name}", path + name, "function", rel, LineAt(t.Index), LineAt(end), new()));
            }
        }
        return children.Count == 0 ? null : new TestNode(rel, Path.GetFileName(rel), "file", rel, 1, LineAt(text.Length), children);
    }

    static int CloseParen(string code, int open)
    {
        int depth = 0;
        for (int i = open; i < code.Length; i++)
        {
            if (code[i] == '(') depth++;
            else if (code[i] == ')' && --depth == 0) return i;
        }
        return code.Length;
    }

    /// <summary>
    /// The command line of a run of these tests (after vendor/bin/phpunit or pest): --teamcity, the test files, and a --filter of the tests
    /// chosen within them (none for whole files). Names are matched by the short class name (a backslash would not reach PHP from Windows'
    /// command line as it is).
    /// </summary>
    public static List<string> Arguments(string runner, IReadOnlyList<string> ids)
    {
        var args = new List<string> { "--teamcity", "--colors=never" };
        if (ids.Count == 0) return args;
        var files = ids.Select(i => i.Split("::")[0]).Distinct().ToList();
        args.AddRange(files);
        // a test of a file (not the file, not its class alone) narrows the run to the tests chosen
        var named = ids.Select(i => i.Split("::")).Where(p => runner == "pest" ? p.Length >= 2 : p.Length >= 3).ToList();
        if (named.Count == 0 || named.Count < ids.Count) return args;
        var parts = new List<string>();
        foreach (var p in named)
        {
            string last = string.Join("::", p.Skip(runner == "pest" ? 1 : 2));
            string? dataSet = null;
            if (last.EndsWith(']') && last.IndexOf('[') is var b and > 0) { dataSet = last[(b + 1)..^1]; last = last[..b]; }
            string name = runner == "pest" ? Regex.Escape(last) : Regex.Escape(p[1] + "::" + last);
            // one case of a dataset: PHPUnit's by its name; Pest's filter does not reach a case, so its whole test runs
            parts.Add(dataSet == null || runner == "pest" ? name + "(?: with data set .*)?" : name + " with data set " + Regex.Escape("\"" + dataSet + "\""));
        }
        args.Add("--filter");
        args.Add("/(?:" + string.Join("|", parts) + ")$/");
        return args;
    }
}

/// <summary>
/// The TeamCity messages PHPUnit and Pest print with --teamcity, as the Tests panel's: testStarted with the test's id, testResult with its
/// outcome, duration (seconds), message, the failure's details and the line of the test file it failed at. Other lines are the run's output.
/// </summary>
public sealed class TeamCityReader
{
    readonly Func<string, string?> _relOf;
    readonly Dictionary<string, string> _ids = new();            // a running test's name (and flow) → its id
    readonly Dictionary<string, JsonObject> _failures = new();
    string _pending = "";

    /// <param name="relOf">A path as the tests name it (this computer's, or the other machine's) → the project's relative path, or null.</param>
    public TeamCityReader(Func<string, string?> relOf) => _relOf = relOf;

    /// <summary>Some text of the run's output: the messages of its complete lines (and the other lines, as output).</summary>
    public List<JsonObject> Feed(string text)
    {
        var events = new List<JsonObject>();
        _pending += text;
        int nl;
        var plain = new System.Text.StringBuilder();
        while ((nl = _pending.IndexOf('\n')) >= 0)
        {
            string line = _pending[..(nl + 1)];
            _pending = _pending[(nl + 1)..];
            string trimmed = line.TrimEnd('\r', '\n');
            if (trimmed.StartsWith("##teamcity[", StringComparison.Ordinal) && Message(trimmed) is { } m)
            {
                if (plain.Length > 0) { events.Add(Output(plain.ToString())); plain.Clear(); }
                if (Handle(m.Name, m.Attrs) is { } e) events.Add(e);
            }
            else plain.Append(line);
        }
        if (plain.Length > 0) events.Add(Output(plain.ToString()));
        return events;
    }

    /// <summary>What is left when the run ends (a last line without its line break).</summary>
    public List<JsonObject> Flush() { var rest = _pending; _pending = ""; return rest.Length > 0 ? Feed(rest + "\n") : new(); }

    static JsonObject Output(string text) => new() { ["type"] = "output", ["stream"] = "stdout", ["text"] = text };

    static (string Name, Dictionary<string, string> Attrs)? Message(string line)
    {
        var m = Regex.Match(line, @"^##teamcity\[(\w+)(.*)\]\s*$");
        if (!m.Success) return null;
        var attrs = new Dictionary<string, string>();
        foreach (Match a in Regex.Matches(m.Groups[2].Value, @"(\w+)='((?:\|.|[^|'])*)'"))
            attrs[a.Groups[1].Value] = Unescape(a.Groups[2].Value);
        return (m.Groups[1].Value, attrs);
    }

    static string Unescape(string s) => Regex.Replace(s, @"\|(.)", m => m.Groups[1].Value switch { "n" => "\n", "r" => "\r", "[" => "[", "]" => "]", "'" => "'", "|" => "|", var x => x });

    JsonObject? Handle(string name, Dictionary<string, string> a)
    {
        string test = a.GetValueOrDefault("name", "");
        string key = test + "\0" + a.GetValueOrDefault("flowId", "");
        switch (name)
        {
            case "testStarted":
            {
                if (IdOf(a.GetValueOrDefault("locationHint", ""), test) is not { } id) return null;
                _ids[key] = id;
                return new JsonObject { ["type"] = "testStarted", ["id"] = id };
            }
            case "testFailed":
            case "testIgnored":
            {
                if (!_ids.ContainsKey(key)) return null;
                var f = new JsonObject { ["outcome"] = name == "testIgnored" ? "skipped" : "failed", ["message"] = a.GetValueOrDefault("message", "") };
                string details = a.GetValueOrDefault("details", "");
                if (details.Length > 0) f["traceback"] = details.TrimEnd();
                // where it failed: the first place of the details in the project (its test file, usually)
                foreach (Match place in Regex.Matches(details, @"^\s*(?:at\s+)?(.+?):(\d+)\s*$", RegexOptions.Multiline))
                    if (_relOf(place.Groups[1].Value.Trim()) is { } rel) { f["file"] = rel; f["line"] = int.Parse(place.Groups[2].Value); break; }
                // PHP's errors (an exception that was not an assertion's) are errors, as pytest's
                if (name == "testFailed" && !Regex.IsMatch(f["message"]!.GetValue<string>(), @"Failed asserting|assert", RegexOptions.IgnoreCase) && Regex.IsMatch(f["message"]!.GetValue<string>(), @"^[\w\\]+(Exception|Error)\b")) f["outcome"] = "error";
                _failures[key] = f;
                return null;
            }
            case "testFinished":
            {
                if (!_ids.Remove(key, out var id)) return null;
                var result = new JsonObject { ["type"] = "testResult", ["id"] = id, ["outcome"] = "passed", ["duration"] = double.TryParse(a.GetValueOrDefault("duration", "0"), System.Globalization.CultureInfo.InvariantCulture, out var ms) ? ms / 1000.0 : 0 };
                if (_failures.Remove(key, out var f)) foreach (var (k, v) in f) result[k] = v?.DeepClone();
                return result;
            }
        }
        return null;
    }

    /// <summary>
    /// A test's id from its location: php_qn://FILE::\Namespace\Class::method (with data set "x") is FILE::Class::method[x];
    /// pest_qn://FILE::description is FILE::description.
    /// </summary>
    string? IdOf(string hint, string name)
    {
        var m = Regex.Match(hint, @"^(php_qn|pest_qn|file)://(.+?)::(.+)$");
        if (!m.Success || _relOf(m.Groups[2].Value) is not { } rel) return null;
        string rest = m.Groups[3].Value;
        if (m.Groups[1].Value == "php_qn")
        {
            var parts = rest.Split("::");
            if (parts.Length < 2) return null;
            string cls = parts[0].TrimStart('\\');
            cls = cls[(cls.LastIndexOf('\\') + 1)..];
            string method = string.Join("::", parts.Skip(1));
            var data = Regex.Match(method, @"^(.*?) with data set (?:""(.*)""|#?(.*))$");
            if (data.Success) method = data.Groups[1].Value + "[" + (data.Groups[2].Success ? data.Groups[2].Value : data.Groups[3].Value) + "]";
            return $"{rel}::{cls}::{method}";
        }
        // Pest's case of a dataset: … with data set "dataset "same"" (named) or … with data set "(1, 2)"
        var set = Regex.Match(rest, @"^(.*?) with data set ""(.*)""$");
        if (set.Success)
        {
            string label = set.Groups[2].Value;
            if (Regex.Match(label, @"^dataset ""(.*)""$") is { Success: true } named) label = named.Groups[1].Value;
            rest = set.Groups[1].Value + "[" + label + "]";
        }
        return $"{rel}::{rest}";
    }
}
