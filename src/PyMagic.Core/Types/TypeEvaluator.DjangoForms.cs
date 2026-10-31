using PyMagic.Core.Syntax;

namespace PyMagic.Core.Types;

/// <summary>A form's fields: the ones it declares and, for a model form, those its Meta takes from the model; `cleaned_data` holds their values.</summary>
public sealed partial class TypeEvaluator
{
    readonly Dictionary<ClassInfo, IReadOnlyList<DjangoFormField>?> _djangoFormFields = new();
    readonly Dictionary<ClassInfo, ClassInfo> _djangoCleanedData = new();

    static readonly Dictionary<string, string> FormFieldValues = new(StringComparer.Ordinal)
    {
        ["CharField"] = "str", ["EmailField"] = "str", ["SlugField"] = "str", ["URLField"] = "str", ["RegexField"] = "str", ["ChoiceField"] = "str",
        ["TypedChoiceField"] = "any", ["GenericIPAddressField"] = "str", ["FilePathField"] = "str", ["UUIDField"] = "uuid",
        ["IntegerField"] = "int", ["FloatField"] = "float", ["DecimalField"] = "decimal", ["BooleanField"] = "bool", ["NullBooleanField"] = "bool?",
        ["DateField"] = "date", ["DateTimeField"] = "datetime", ["TimeField"] = "time", ["DurationField"] = "timedelta",
        ["MultipleChoiceField"] = "list", ["TypedMultipleChoiceField"] = "list", ["JSONField"] = "any", ["FileField"] = "any", ["ImageField"] = "any",
        ["ModelChoiceField"] = "any", ["ModelMultipleChoiceField"] = "any",
    };

    /// <summary>A class of the user's code built on django.forms' Form or ModelForm.</summary>
    public bool IsDjangoForm(ClassInfo c) =>
        !c.Module.IsStub && !IsDjangoInternal(c)
        && Mro(c).Any(e => e is ClassType ec && ec.Info.Name == "BaseForm" && ec.Info.Module.Name.StartsWith("django.forms", StringComparison.Ordinal));

    /// <summary>
    /// The fields of a form: those its classes declare (`title = forms.CharField()`), and for a model form the model's fields its Meta names
    /// (`fields = [...]`, `"__all__"`, or all but `exclude`); null when that is not known (a model form whose model cannot be found).
    /// </summary>
    public IReadOnlyList<DjangoFormField>? DjangoFormFields(ClassInfo form)
    {
        if (_djangoFormFields.TryGetValue(form, out var known)) return known;
        _djangoFormFields[form] = null;
        if (!IsDjangoForm(form)) return null;
        var list = new List<DjangoFormField>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in Mro(form))
        {
            if (entry is not ClassType e || e.Info.Module.IsStub || IsDjangoInternal(e.Info)) continue;
            foreach (var stmt in e.Info.Node.Body.OfType<AssignStmt>())
            {
                if (stmt.Targets is not [NameExpr target] || stmt.Value is not CallExpr || seen.Contains(target.Name)) continue;
                if (TypeOf(e.Info.Module, stmt.Value) is not ClassType { IsInstance: true } fieldType || FormFieldKind(fieldType.Info) is not { } kind) continue;
                seen.Add(target.Name);
                list.Add(new DjangoFormField(target.Name, FormValueType(kind), target, e.Info.Module));
            }
        }

        if (DjangoRoleOf(form) == DjangoClassRole.Form)
        {
            if (DjangoMetaModel(form) is not { } model || DjangoMetaOf(form) is not { } meta) return _djangoFormFields[form] = list.Count > 0 ? list : null;
            Expr? fields = null, exclude = null;
            foreach (var stmt in meta.Meta.Body.OfType<AssignStmt>())
                if (stmt.Targets is [NameExpr { Name: var n }]) { if (n == "fields") fields = stmt.Value; else if (n == "exclude") exclude = stmt.Value; }
            var excluded = Strings(exclude).ToHashSet(StringComparer.Ordinal);
            var modelFields = DjangoFieldsOf(model).Where(f => !f.PrimaryKey).ToList();
            IEnumerable<DjangoField> taken = StringValue(fields) == "__all__" || fields == null ? modelFields.Where(f => !excluded.Contains(f.Name))
                : Strings(fields).Select(name => modelFields.FirstOrDefault(f => f.Name == name)).OfType<DjangoField>();
            foreach (var f in taken)
                if (seen.Add(f.Name))
                {
                    var decl = f.Owner.Scope.Symbols.GetValueOrDefault(f.Name)?.Declarations.FirstOrDefault()?.Node;
                    list.Add(new DjangoFormField(f.Name, DjangoValueType(f) is var v && f.Kind == "many-to-many" ? PyType.Unknown : v, decl, f.Owner.Module));
                }
        }
        return _djangoFormFields[form] = list;

        IEnumerable<string> Strings(Expr? e) => e is ListExpr l ? l.Items.Select(StringValue).OfType<string>()
            : e is TupleExpr t ? t.Items.Select(StringValue).OfType<string>() : Enumerable.Empty<string>();
    }

    /// <summary>The kind of value a field class of django.forms cleans to, found along its bases.</summary>
    string? FormFieldKind(ClassInfo fieldClass)
    {
        foreach (var entry in Mro(fieldClass))
            if (entry is ClassType e && e.Info.Module.Name.StartsWith("django.forms", StringComparison.Ordinal))
            {
                if (FormFieldValues.TryGetValue(e.Info.Name, out var kind)) return kind;
                if (e.Info.Name == "Field") return "any";
            }
        return null;
    }

    PyType FormValueType(string kind) => kind switch
    {
        "str" => Inst("str"),
        "int" => Inst("int"),
        "float" => Inst("float"),
        "bool" => Inst("bool"),
        "bool?" => PyType.Union(new[] { Inst("bool"), NoneInstance }),
        "list" => Inst("list", Inst("str")),
        "decimal" => DjangoInstanceOf("decimal", "Decimal") ?? PyType.Unknown,
        "date" => DjangoInstanceOf("datetime", "date") ?? PyType.Unknown,
        "datetime" => DjangoInstanceOf("datetime", "datetime") ?? PyType.Unknown,
        "time" => DjangoInstanceOf("datetime", "time") ?? PyType.Unknown,
        "timedelta" => DjangoInstanceOf("datetime", "timedelta") ?? PyType.Unknown,
        "uuid" => DjangoInstanceOf("uuid", "UUID") ?? PyType.Unknown,
        _ => PyType.Unknown,
    };

    /// <summary>`form.cleaned_data`: a dict with a key for each field of the form, its value the field's cleaned value.</summary>
    PyType? DjangoCleanedData(ClassType c, bool instance)
    {
        if (!instance || c.Info.Module.IsStub || DjangoFormFields(c.Info) is not { } fields) return null;
        if (!_djangoCleanedData.TryGetValue(c.Info, out var data))
        {
            data = MakeSyntheticClass(c.Info.Module, $"cleaned_data of {c.Info.Name}", c.Info.Node, BuiltinClass("dict"), Array.Empty<(string, PyType)>());
            ClassDataOf(data).IsTypedDict = true;
            _syntheticTypedDictFields[data] = fields.Select(f => (new FieldInfo(f.Name, f.Type, false), true)).ToList();
            _djangoCleanedData[c.Info] = data;
        }
        return InstanceOfClass(data);
    }
}

/// <summary>A field of a form: its name, the value it cleans to, and where it is declared (the form's assignment or the model's field).</summary>
public sealed record DjangoFormField(string Name, PyType Type, Node? Declaration, ModuleInfo Module);
