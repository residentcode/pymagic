using System.Globalization;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;
using PyMagic.Server.Pytest;

namespace PyMagic.Server;

/// <summary>What a FastAPI endpoint returns, against its response model (item 147).</summary>
public sealed partial class LanguageService
{
    /// <summary>
    /// A `return` that FastAPI's validation of the response model refuses: the request then fails with ResponseValidationError and a 500 (each case
    /// answered by FastAPI 0.141 and Pydantic 2.13 first). The model is `response_model=` or else the return annotation. Refused: a dict written in the return
    /// that lacks a required field of the model, or gives a field a word where it needs a number; another Pydantic model that lacks one; a str, int, float,
    /// bool, list or None where a model is wanted; a dict or a model where a list of them is wanted, and the items of a list written in the return. Extra keys,
    /// objects with the fields as attributes, Response objects and types PyMagic does not know pass.
    /// </summary>
    IEnumerable<Finding> FastApiResponseFindings(ModuleInfo m, TypeEvaluator ev, RouteDef r)
    {
        if (r.Flask || r.Methods.Contains("WS")) yield break;
        var def = r.Def;
        var decorator = RouteDecorator(r);
        Expr? modelExpr = decorator?.Args.FirstOrDefault(a => a.Name == "response_model")?.Value;
        if (modelExpr is ConstExpr { Kind: ConstKind.None }) yield break;   // response_model=None: no validation
        modelExpr ??= def.Returns;
        if (modelExpr == null || HasYield(def.Body)) yield break;
        if (ev.AnnotationType(m, modelExpr) is not ClassType { IsInstance: true } model || IsResponseClass(ev, model)) yield break;
        string modelText = SourceText(m, modelExpr);
        foreach (var ret in AstChildren.Descendants(def).OfType<ReturnStmt>().Where(x => OwnerFunction(m, x) == def))
        {
            var value = ret.Value;
            string? problem = value == null ? (PydanticFields(ev, model) != null ? "None" : null) : ProblemWith(ev, m, model, value, ev.TypeOf(m, value));
            if (problem == null) continue;
            int start = value?.Start ?? ret.Start, end = value?.End ?? ret.End;
            yield return new Finding(start, end - start, $"FastAPI cannot answer with {problem} as {modelText}: the request fails with ResponseValidationError (500)", "fastapi-response-model", FindingLevel.Error);
        }
    }

    /// <summary>What is wrong with a value as the model (a short phrase), or null when it fits or cannot be known.</summary>
    string? ProblemWith(TypeEvaluator ev, ModuleInfo m, ClassType model, Expr? expr, PyType type)
    {
        if (type.Members.Any(t => t is not ClassType { IsInstance: true }) || type.Members.Count == 0) return null;   // unknown, or None mixed in: not judged
        var members = type.Members.Cast<ClassType>().ToList();
        if (members.Any(t => IsResponseClass(ev, t))) return null;
        string qn = model.Info.QualifiedName;

        // a list of models
        if (qn is "builtins.list" or "builtins.tuple" or "builtins.set" or "typing.List" or "typing.Sequence")
        {
            var item = model.Args.FirstOrDefault() as ClassType;
            if (expr is ListExpr list && item != null)
            {
                for (int i = 0; i < list.Items.Count; i++)
                    if (ProblemWith(ev, m, item, list.Items[i], ev.TypeOf(m, list.Items[i])) is { } p) return $"{p} as item {i + 1} of the list";
                return null;
            }
            if (members.All(t => t.Info.QualifiedName == "builtins.dict" || PydanticFields(ev, t) != null)) return Kind(members[0]) + " where a list is wanted";
            return null;
        }

        // a Pydantic model
        if (PydanticFields(ev, model) is { } fields)
        {
            if (members.All(t => t.Info.QualifiedName is "builtins.str" or "builtins.int" or "builtins.float" or "builtins.bool" or "builtins.list" or "builtins.tuple" or "builtins.set" || t.Info.Name == "NoneType")) return Kind(members[0]);
            if (members.Count != 1) return null;
            var t = members[0];
            if (expr is DictExpr dict && t.Info.QualifiedName == "builtins.dict")
            {
                if (dict.Keys.Any(k => k == null)) return null;   // {**other}: the keys are not known
                var keys = dict.Keys.Select(k => k is ConstExpr c ? FixtureModel.StringValue(c) : null).ToList();
                if (keys.Any(k => k == null)) return null;
                var missing = fields.Where(f => f.Required && !keys.Contains(f.Name)).Select(f => f.Name).ToList();
                if (missing.Count > 0) return $"a dict without {Quoted(missing)}, which {model.Info.Name} requires";
                for (int i = 0; i < keys.Count; i++)
                {
                    var field = fields.FirstOrDefault(f => f.Name == keys[i]);
                    if (field.Name == null || dict.Values[i] is not ConstExpr { Kind: ConstKind.Str } sv || FixtureModel.StringValue(sv) is not { } word) continue;
                    if (field.Number is { } number && !double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return $"'{word}' for '{field.Name}', which is {Article(number)}";
                }
                return null;
            }
            // another model: the fields it has are what Pydantic can read
            if (!ReferenceEquals(t.Info, model.Info) && PydanticFields(ev, t) is { } other && !ev.MroOf(t.Info).OfType<ClassType>().Any(c => ReferenceEquals(c.Info, model.Info)))
            {
                var missing = fields.Where(f => f.Required && !other.Any(o => o.Name == f.Name)).Select(f => f.Name).ToList();
                if (missing.Count > 0) return $"{Article(t.Info.Name)}, which has no {Quoted(missing)}";
            }
            return null;
        }

        // a plain type
        if (qn == "builtins.str" && members.All(x => x.Info.QualifiedName is "builtins.int" or "builtins.float")) return Kind(members[0]) + " where a str is wanted";
        if (qn is "builtins.int" or "builtins.float" && expr is ConstExpr { Kind: ConstKind.Str } s && FixtureModel.StringValue(s) is { } text
            && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return $"'{text}' where {Article(model.Info.Name)} is wanted";
        return null;
    }

    static string Kind(ClassType t) => t.Info.Name == "NoneType" ? "None" : Article(t.Info.Name);
    static string Quoted(List<string> names) => string.Join(", ", names.Select(n => $"'{n}'"));

    /// <summary>A Starlette or FastAPI Response (or a subclass): FastAPI sends it as it is.</summary>
    static bool IsResponseClass(TypeEvaluator ev, ClassType t) =>
        ev.MroOf(t.Info).OfType<ClassType>().Any(c => c.Info.Name == "Response" && c.Info.Module.Name.StartsWith("starlette", StringComparison.Ordinal));

    /// <summary>The fields of a Pydantic model (its class and its project bases): name, whether it is required, and "int"/"float" when it holds a number; null when it is not one.</summary>
    static List<(string Name, bool Required, string? Number)>? PydanticFields(TypeEvaluator ev, ClassType t)
    {
        var mro = ev.MroOf(t.Info).OfType<ClassType>().ToList();
        if (!mro.Any(c => c.Info.Name == "BaseModel" && c.Info.Module.Name.StartsWith("pydantic", StringComparison.Ordinal))) return null;
        var fields = new List<(string, bool, string?)>();
        foreach (var c in mro.AsEnumerable().Reverse())
        {
            if (c.Info.Module.Name.StartsWith("pydantic", StringComparison.Ordinal) || c.Info.QualifiedName == "builtins.object") continue;
            foreach (var a in c.Info.Node.Body.OfType<AnnAssignStmt>())
            {
                if (a.Target is not NameExpr { Name: var name } || name.StartsWith('_') || SourceText(c.Info.Module, a.Annotation).StartsWith("ClassVar", StringComparison.Ordinal)) continue;
                bool required = a.Value == null || a.Value is CallExpr { Func: NameExpr { Name: "Field" } or AttributeExpr { Attr: "Field" } } f
                    && !f.Args.Any(x => x.Name is "default" or "default_factory") && f.Args.FirstOrDefault(x => x.Name == null)?.Value is null or ConstExpr { Kind: ConstKind.Ellipsis };
                string? number = ev.AnnotationType(c.Info.Module, a.Annotation) is ClassType { Info.QualifiedName: "builtins.int" or "builtins.float" } n ? n.Info.Name : null;
                fields.RemoveAll(x => x.Item1 == name);
                fields.Add((name, required, number));
            }
        }
        return fields;
    }
}
