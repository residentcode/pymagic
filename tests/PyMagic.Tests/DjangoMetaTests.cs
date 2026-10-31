using PyMagic.Server;

namespace PyMagic.Tests;

public class DjangoMetaTests
{
    const string Shop = "file:///c:/proj/shop/";

    static readonly (string Path, string Text)[] Libraries =
    {
        ("django/forms/__init__.py", "class BaseForm:\n    def __init__(self, *args, **kwargs): ...\n\n\nclass BaseModelForm(BaseForm):\n    def save(self, commit=True): ...\n\n\nclass ModelForm(BaseModelForm): ...\n\n\nclass Form(BaseForm): ...\n\n\nclass Field: ...\n\n\nclass CharField(Field): ...\n\n\nclass IntegerField(Field): ...\n"),
        ("django/contrib/__init__.py", ""),
        ("django/contrib/admin/__init__.py",
            "class BaseModelAdmin: ...\n\n\nclass ModelAdmin(BaseModelAdmin):\n    def save_model(self, request, obj, form, change): ...\n\n    def get_queryset(self, request): ...\n\n    def formfield_for_foreignkey(self, db_field, request, **kwargs): ...\n\n\nclass InlineModelAdmin(BaseModelAdmin): ...\n\n\nclass TabularInline(InlineModelAdmin): ...\n\n\n" +
            "def register(*models, site=None):\n    def decorator(cls):\n        return cls\n    return decorator\n\n\nclass AdminSite:\n    def register(self, model_or_iterable, admin_class=None, **options): ...\n\n\nsite = AdminSite()\n"),
        ("rest_framework/__init__.py", ""),
        ("rest_framework/serializers.py", "class Serializer:\n    def save(self, **kwargs): ...\n\n\nclass ModelSerializer(Serializer): ...\n\n\nclass CharField: ...\n"),
    };

    const string Models =
        "from django.db import models\n\n\nclass Author(models.Model):\n    name = models.CharField(max_length=100)\n\n\n" +
        "class Book(models.Model):\n    title = models.CharField(max_length=200)\n    pages = models.IntegerField(default=0)\n    author = models.ForeignKey(Author, on_delete=models.CASCADE)\n\n" +
        "    def blurb(self):\n        return self.title\n\n    @property\n    def short(self):\n        return self.title[:10]\n\n    class Meta:\n        ordering = [\"title\"]\n";

    static (LanguageService Svc, string Uri, Position Pos) Open(string file, string marked, string? models = null)
    {
        int i = marked.IndexOf('§');
        string text = i < 0 ? marked : marked.Remove(i, 1);
        var b = DjangoFixture.Service().With("shop/__init__.py", "").With("shop/models.py", models ?? Models);
        foreach (var (p, t) in Libraries) b.With(p, t);
        b.With("shop/" + file, text);
        return (b.Service, Shop + file, i < 0 ? new Position(0, 0) : new LineMap(text).PositionOf(i));
    }

    static List<LspDiagnostic> Problems(string file, string code, string? models = null)
    {
        var (svc, uri, _) = Open(file, code, models);
        return svc.Diagnostics(uri).Where(d => d.Code == "django-meta-field").ToList();
    }

    // ------------------------------------------------------------ model Meta

    [Fact]
    public void ModelMetaOrderingAndUniqueTogether()
    {
        string models = Models.Replace("ordering = [\"title\"]", "ordering = [\"-title\", \"author__name\", \"nope\", \"author__nope\"]\n        unique_together = [(\"title\", \"pages\"), (\"title\", \"gone\")]");
        var found = Problems("models.py", models, models);
        var names = found.Select(d => d.Message).ToList();
        Assert.True(names.Any(n => n.Contains("'nope'")), "ordering by a missing field");
        Assert.True(names.Any(n => n.Contains("'nope'") && n.Contains("author") || n.Contains("author__nope") || n.Contains("Cannot resolve 'nope'")), "a missing field behind a relation");
        Assert.True(names.Any(n => n.Contains("'gone'")), "unique_together");
        Assert.True(!names.Any(n => n.Contains("'title'") || n.Contains("'pages'")), "the valid ones are quiet");
    }

    // ------------------------------------------------------------ forms and serializers

    [Fact]
    public void AFormTakesFieldsOfTheModelAndItsOwnDeclaredOnes()
    {
        string code = "from django import forms\nfrom shop.models import Book\n\n\nclass BookForm(forms.ModelForm):\n    note = forms.CharField()\n\n    class Meta:\n        model = Book\n        fields = [\"title\", \"note\", \"titel\", \"__all__\"]\n        exclude = (\"pagez\",)\n        labels = {\"title\": \"Name\", \"pages\": \"How long\"}\n";
        var found = Problems("forms.py", code);
        Assert.Equal(2, found.Count);
        Assert.True(found.Any(d => d.Message.Contains("'titel' is not a field of Book")));
        Assert.True(found.Any(d => d.Message.Contains("'pagez'")));
    }

    [Fact]
    public void ASerializerMayNameAMethodOrPropertyOfTheModel()
    {
        string code = "from rest_framework import serializers\nfrom shop.models import Book\n\n\nclass BookSerializer(serializers.ModelSerializer):\n    class Meta:\n        model = Book\n        fields = [\"title\", \"short\", \"blurb\", \"oops\"]\n        read_only_fields = [\"pages\"]\n        extra_kwargs = {\"title\": {\"required\": False}, \"nothing\": {}}\n";
        var found = Problems("serializers.py", code);
        Assert.Equal(2, found.Count);
        Assert.True(found.Any(d => d.Message.Contains("'oops'")));
        Assert.True(found.Any(d => d.Message.Contains("'nothing'")));
    }

    [Fact]
    public void AFormWithoutAKnownModelIsNotChecked()
    {
        string code = "from django import forms\n\n\nclass F(forms.ModelForm):\n    class Meta:\n        model = Unknown\n        fields = [\"whatever\"]\n";
        Assert.Equal(0, Problems("forms.py", code).Count);
    }

    // ------------------------------------------------------------ the admin

    const string AdminCode =
        "from django.contrib import admin\nfrom shop.models import Book\n\n\n@admin.register(Book)\nclass BookAdmin(admin.ModelAdmin):\n" +
        "    list_display = (\"title\", \"author\", \"blurb\", \"short\", \"nice\", \"__str__\", \"missing\")\n    list_filter = (\"author\", \"pages\", \"nope\")\n" +
        "    search_fields = [\"^title\", \"author__name\", \"author__name__icontains\", \"bogus\"]\n    ordering = (\"-title\",)\n    readonly_fields = (\"nice\",)\n" +
        "    prepopulated_fields = {\"title\": (\"pages\",)}\n    fieldsets = ((None, {\"fields\": (\"title\", \"nice\", \"zzz\")}),)\n\n    def nice(self, obj):\n        return obj.title\n";

    [Fact]
    public void AnAdminChecksWhatItListsAgainstTheModelAndItself()
    {
        var found = Problems("admin.py", AdminCode).Select(d => d.Message).ToList();
        Assert.Equal(4, found.Count);
        Assert.True(found.Any(m => m.Contains("'missing'")));
        Assert.True(found.Any(m => m.Contains("'nope'")));
        Assert.True(found.Any(m => m.Contains("bogus")));
        Assert.True(found.Any(m => m.Contains("'zzz'")));
    }

    [Fact]
    public void RegistrationWithTheSiteWorksToo()
    {
        string code = "from django.contrib import admin\nfrom shop.models import Book\n\n\nclass BookAdmin(admin.ModelAdmin):\n    list_display = (\"title\", \"oops\")\n\n\nadmin.site.register(Book, BookAdmin)\n";
        Assert.Equal(1, Problems("admin.py", code).Count);
    }

    [Fact]
    public void AnAdminNobodyRegisteredIsNotChecked()
    {
        string code = "from django.contrib import admin\n\n\nclass BookAdmin(admin.ModelAdmin):\n    list_display = (\"title\", \"oops\")\n";
        Assert.Equal(0, Problems("admin.py", code).Count);
    }

    [Fact]
    public void AnInlineTakesItsModelFromItself()
    {
        string code = "from django.contrib import admin\nfrom shop.models import Book\n\n\nclass BookInline(admin.TabularInline):\n    model = Book\n    fields = (\"title\", \"oops\")\n";
        Assert.Equal(1, Problems("admin.py", code).Count);
    }

    [Fact]
    public void AdminMethodObjIsTheModel()
    {
        var (svc, uri, pos) = Open("admin.py", "from django.contrib import admin\nfrom shop.models import Book\n\n\n@admin.register(Book)\nclass BookAdmin(admin.ModelAdmin):\n    def save_model(self, request, obj, form, change):\n        o§bj\n");
        Assert.Contains("Book", svc.Hover(uri, pos)!.Contents.Value);
    }

    // ------------------------------------------------------------ forms know their instance

    [Fact]
    public void FormInstanceAndSaveAreTheModel()
    {
        string code = "from django import forms\nfrom shop.models import Book\n\n\nclass BookForm(forms.ModelForm):\n    class Meta:\n        model = Book\n        fields = \"__all__\"\n\n\ndef view(form: BookForm):\n    book = form.save()\n    b§ook\n";
        var (svc, uri, pos) = Open("forms.py", code);
        Assert.Contains("Book", svc.Hover(uri, pos)!.Contents.Value);
        var (svc2, uri2, pos2) = Open("forms.py", code.Replace("b§ook\n", "form.inst§ance\n").Replace("book = form.save()\n    ", ""));
        Assert.Contains("Book", svc2.Hover(uri2, pos2)!.Contents.Value);
    }

    // ------------------------------------------------------------ completion, go to, hover

    [Fact]
    public void FieldNamesCompleteInsideTheList()
    {
        string code = "from django import forms\nfrom shop.models import Book\n\n\nclass BookForm(forms.ModelForm):\n    class Meta:\n        model = Book\n        fields = [\"ti§\"]\n";
        var (svc, uri, pos) = Open("forms.py", code);
        var labels = svc.Completion(uri, pos).Select(i => i.Label).ToList();
        Assert.True(labels.Contains("title"));
        Assert.True(!labels.Contains("pages"));
    }

    [Fact]
    public void AdminListDisplayOffersMethodsToo()
    {
        string code = "from django.contrib import admin\nfrom shop.models import Book\n\n\n@admin.register(Book)\nclass BookAdmin(admin.ModelAdmin):\n    list_display = (\"§\",)\n\n    def nice(self, obj):\n        return 1\n";
        var (svc, uri, pos) = Open("admin.py", code);
        var labels = svc.Completion(uri, pos).Select(i => i.Label).ToList();
        Assert.True(labels.Contains("title") && labels.Contains("blurb") && labels.Contains("short") && labels.Contains("nice"));
    }

    [Fact]
    public void SearchFieldsCompleteAcrossRelations()
    {
        string code = "from django.contrib import admin\nfrom shop.models import Book\n\n\n@admin.register(Book)\nclass BookAdmin(admin.ModelAdmin):\n    search_fields = [\"author__na§\"]\n";
        var (svc, uri, pos) = Open("admin.py", code);
        Assert.True(svc.Completion(uri, pos).Any(i => i.Label == "author__name"));
    }

    [Fact]
    public void AFieldNameLeadsToTheField()
    {
        string code = "from django.contrib import admin\nfrom shop.models import Book\n\n\n@admin.register(Book)\nclass BookAdmin(admin.ModelAdmin):\n    list_display = (\"title\", \"author__name§\")\n    ordering = [\"-ti§\"]\n";
        var (svc, uri, pos) = Open("admin.py", code.Replace("ordering = [\"-ti§\"]", "ordering = [\"-title\"]"));
        var found = svc.Definition(uri, pos);
        Assert.Equal(1, found.Count);
        Assert.Contains("shop/models.py", found[0].Uri);
        Assert.Equal(true, found[0].Range.Start.Line == 4 || found[0].Range.Start.Line >= 3);
        var (svc2, uri2, pos2) = Open("admin.py", code.Replace("\"author__name§\"", "\"author__name\"").Replace("[\"-ti§\"]", "[\"-ti§tle\"]"));
        Assert.Contains("(field) Book.title", svc2.Hover(uri2, pos2)!.Contents.Value);
    }

    [Fact]
    public void ProfilesCanSwitchTheCheckOff()
    {
        string code = "from django import forms\nfrom shop.models import Book\n\n\nclass F(forms.ModelForm):\n    class Meta:\n        model = Book\n        fields = [\"oops\"]\n";
        var (svc, uri, _) = Open("forms.py", code);
        string dir = Path.Combine(Path.GetTempPath(), "pymagic_meta_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        svc.SourceRoots.Insert(0, dir);
        svc.SetInspection("django-meta-field", "off");
        Assert.Equal(0, svc.Diagnostics(uri).Count(d => d.Code == "django-meta-field"));
    }

    // ------------------------------------------------------------ get_queryset, formfield_for_*, callables of list_display (item 201)

    const string Registered = "from django.contrib import admin\nfrom shop.models import Book\n\n\n@admin.register(Book)\nclass BookAdmin(admin.ModelAdmin):\n";

    [Fact]
    public void GetQuerysetOfAnAdminIsAQuerysetOfItsModel()
    {
        var (svc, uri, pos) = Open("admin.py", Registered + "    def get_queryset(self, request):\n        qs = super().get_queryset(request)\n        book = qs.first()\n        bo§ok\n");
        Assert.Contains("Book", svc.Hover(uri, pos)!.Contents.Value);
        var (svc2, uri2, pos2) = Open("admin.py", Registered + "    def changelist(self, request):\n        q§s = self.get_queryset(request)\n");
        Assert.Contains("QuerySet", svc2.Hover(uri2, pos2)!.Contents.Value);
    }

    [Fact]
    public void FormfieldForForeignkeyNamesAForeignKeyOfTheModel()
    {
        string code = Registered + "    def formfield_for_foreignkey(self, db_field, request, **kwargs):\n        if db_field.name == \"autor\":\n            pass\n        if db_field.name in (\"author\", \"title\"):\n            pass\n        return super().formfield_for_foreignkey(db_field, request, **kwargs)\n";
        var found = Problems("admin.py", code).Select(d => d.Message).ToList();
        Assert.Equal(2, found.Count);
        Assert.True(found.Any(m => m.Contains("'autor' is not a field of Book") && m.Contains("author") && !m.Contains("pages")), string.Join(" | ", found));
        Assert.True(found.Any(m => m.Contains("only called for ForeignKey") && m.Contains("'title'")), string.Join(" | ", found));
        var (svc, uri, pos) = Open("admin.py", code.Replace("\"autor\"", "\"a§\""));
        var labels = svc.Completion(uri, pos).Select(i => i.Label).ToList();
        Assert.True(labels.Contains("author") && !labels.Contains("title"), string.Join(", ", labels));
    }

    [Fact]
    public void AFunctionListDisplayCallsGetsTheModelFromAnotherModule()
    {
        var b = DjangoFixture.Service().With("shop/__init__.py", "").With("shop/models.py", Models);
        foreach (var (p, t) in Libraries) b.With(p, t);
        string display = "def short_title(book):\n    return book\n\n\ndef both(book, other):\n    return book\n";
        b.With("shop/display.py", display);
        b.With("shop/admin.py", Registered.Replace("from shop.models import Book\n", "from shop.models import Book\nfrom shop import display\n") + "    list_display = (\"title\", display.short_title, display.both)\n");
        var svc = b.Service;
        var hover = svc.Hover(Shop + "display.py", new LineMap(display).PositionOf(display.IndexOf("book\n") + 1));
        Assert.Contains("Book", hover!.Contents.Value);
        var found = svc.Diagnostics(Shop + "admin.py").Where(d => d.Code == "django-meta-field").Select(d => d.Message).ToList();
        Assert.Equal(1, found.Count);
        Assert.Contains("calls both with each Book", found[0]);
    }

    // ------------------------------------------------------------ clean_<field> and cleaned_data (item 202)

    const string Contact = "from django import forms\n\n\nclass ContactForm(forms.Form):\n    name = forms.CharField()\n    age = forms.IntegerField()\n\n";

    [Fact]
    public void ACleanMethodNamesAFieldOfTheForm()
    {
        var found = Problems("forms.py", Contact + "    def clean_nmae(self):\n        return 1\n\n    def clean_age(self):\n        return 2\n\n    def clean(self):\n        return 3\n").Select(d => d.Message).ToList();
        Assert.Equal(1, found.Count);
        Assert.Contains("clean_nmae is never called: ContactForm has no field 'nmae'. Its fields are: name, age", found[0]);
    }

    [Fact]
    public void CleanedDataHoldsTheValueOfEachField()
    {
        var (svc, uri, pos) = Open("forms.py", Contact + "    def clean_age(self):\n        years = self.cleaned_data[\"age\"]\n        yea§rs\n");
        Assert.Contains("int", svc.Hover(uri, pos)!.Contents.Value);
    }

    [Fact]
    public void AModelFormsCleanedDataHasTheFieldsItsMetaTakes()
    {
        string code = "from django import forms\nfrom shop.models import Book\n\n\nclass BookForm(forms.ModelForm):\n    note = forms.CharField()\n\n    class Meta:\n        model = Book\n        fields = [\"title\", \"pages\"]\n\n" +
            "    def clean_author(self):\n        return 1\n\n    def clean_title(self):\n        a = self.cleaned_data[\"titel\"]\n        b = self.cleaned_data.get(\"note\")\n        c = self.cleaned_data.get(\"nope\")\n        return self.cleaned_data[\"title\"]\n";
        var (svc, uri, _) = Open("forms.py", code);
        var found = svc.Diagnostics(uri).Where(d => d.Code is "django-meta-field" or "typeddict-keys").Select(d => d.Message).ToList();
        Assert.Equal(3, found.Count);
        Assert.True(found.Any(m => m.Contains("clean_author is never called") && m.Contains("note, title, pages")), string.Join(" | ", found));
        Assert.True(found.Any(m => m.Contains("BookForm has no field 'titel'")), string.Join(" | ", found));
        Assert.True(found.Any(m => m.Contains("BookForm has no field 'nope'")), string.Join(" | ", found));
        var (svc2, uri2, pos2) = Open("forms.py", code.Replace("cleaned_data[\"titel\"]", "cleaned_data[\"§\"]"));
        var labels = svc2.Completion(uri2, pos2).Select(i => i.Label).ToList();
        Assert.True(labels.Contains("title") && labels.Contains("pages") && labels.Contains("note") && !labels.Contains("author"), string.Join(", ", labels));
    }
}
