namespace PyMagic.Tests;

/// <summary>
/// A small stand-in for Django with the real package's layout and class names (the library is not installed on every machine):
/// django.db.models.base.Model, django.db.models.fields.*, fields.related.ForeignKey, manager.Manager, query.QuerySet,
/// django.conf.settings, django.urls and django.shortcuts. The code in it is as untyped as the real one.
/// </summary>
internal static class DjangoFixture
{
    public const string Root = "file:///c:/proj/";

    static readonly (string Path, string Text)[] Library =
    {
        ("django/__init__.py", "VERSION = (5, 0, 0)\n"),
        ("django/db/__init__.py", ""),
        ("django/db/models/__init__.py",
            "from django.db.models.base import Model\n" +
            "from django.db.models.fields import (Field, AutoField, BigAutoField, CharField, TextField, SlugField, EmailField, URLField,\n" +
            "    IntegerField, PositiveIntegerField, FloatField, DecimalField, BooleanField, DateField, DateTimeField, TimeField, DurationField,\n" +
            "    UUIDField, BinaryField)\n" +
            "from django.db.models.fields.related import ForeignKey, OneToOneField, ManyToManyField\n" +
            "from django.db.models.fields.json import JSONField\n" +
            "from django.db.models.manager import Manager\n" +
            "from django.db.models.query import QuerySet\n" +
            "from django.db.models.deletion import CASCADE, SET_NULL, PROTECT\n"),
        ("django/db/models/base.py",
            "class ModelBase(type):\n    def __new__(cls, name, bases, attrs, **kwargs):\n        return super().__new__(cls, name, bases, attrs)\n\n\n" +
            "class Model(metaclass=ModelBase):\n    def __init__(self, *args, **kwargs): ...\n    def save(self, *args, **kwargs): ...\n    def delete(self, *args, **kwargs): ...\n    def refresh_from_db(self, *args, **kwargs): ...\n"),
        ("django/db/models/fields/__init__.py",
            "class Field:\n    def __init__(self, verbose_name=None, name=None, primary_key=False, max_length=None, unique=False, blank=False, null=False, db_index=False, default=None, editable=True, choices=None, help_text='', **kwargs): ...\n\n\n" +
            string.Join("\n\n\n", new[]
            {
                "class AutoField(Field): ...", "class BigAutoField(AutoField): ...", "class CharField(Field): ...", "class TextField(Field): ...",
                "class SlugField(CharField): ...", "class EmailField(CharField): ...", "class URLField(CharField): ...", "class IntegerField(Field): ...",
                "class PositiveIntegerField(IntegerField): ...", "class FloatField(Field): ...", "class DecimalField(Field): ...", "class BooleanField(Field): ...",
                "class DateField(Field): ...", "class DateTimeField(DateField): ...", "class TimeField(Field): ...", "class DurationField(Field): ...",
                "class UUIDField(Field): ...", "class BinaryField(Field): ...",
            }) + "\n"),
        ("django/db/models/fields/related.py",
            "from django.db.models.fields import Field\n\n\nclass ForeignKey(Field):\n    def __init__(self, to, on_delete=None, related_name=None, related_query_name=None, **kwargs): ...\n\n\n" +
            "class OneToOneField(ForeignKey): ...\n\n\nclass ManyToManyField(Field):\n    def __init__(self, to, related_name=None, **kwargs): ...\n"),
        ("django/db/models/fields/json.py", "from django.db.models.fields import Field\n\n\nclass JSONField(Field): ...\n"),
        ("django/db/models/manager.py", "class BaseManager:\n    def get_queryset(self): ...\n\n    @classmethod\n    def from_queryset(cls, queryset_class, class_name=None): ...\n\n\nclass Manager(BaseManager): ...\n"),
        ("django/db/models/query.py", "class QuerySet:\n    def __init__(self, model=None): ...\n\n    @classmethod\n    def as_manager(cls): ...\n"),
        ("django/db/models/deletion.py", "CASCADE = object()\nSET_NULL = object()\nPROTECT = object()\n"),
        ("django/core/__init__.py", ""),
        ("django/core/exceptions.py", "class ObjectDoesNotExist(Exception): ...\n\n\nclass MultipleObjectsReturned(Exception): ...\n"),
        ("django/conf/__init__.py", "class LazySettings:\n    def __getattr__(self, name): ...\n\n\nsettings = LazySettings()\n"),
        ("django/conf/global_settings.py", "DEBUG = False\nALLOWED_HOSTS = []\nSECRET_KEY = ''\nLANGUAGE_CODE = 'en-us'\nTIME_ZONE = 'America/Chicago'\nUSE_TZ = True\n"),
        ("django/urls/__init__.py",
            "def path(route, view, kwargs=None, name=None): ...\n\ndef re_path(route, view, kwargs=None, name=None): ...\n\ndef include(arg, namespace=None): ...\n\n" +
            "def reverse(viewname, urlconf=None, args=None, kwargs=None, current_app=None): ...\n\ndef reverse_lazy(viewname, urlconf=None, args=None, kwargs=None, current_app=None): ...\n"),
        ("django/shortcuts.py",
            "def render(request, template_name, context=None, content_type=None, status=None, using=None): ...\n\ndef redirect(to, *args, permanent=False, **kwargs): ...\n\ndef get_object_or_404(klass, *args, **kwargs): ...\n"),
    };

    public const string Models =
        "from django.db import models\n\n\n" +
        "class Author(models.Model):\n    name = models.CharField(max_length=100)\n    born = models.DateField(null=True)\n\n\n" +
        "class Profile(models.Model):\n    owner = models.OneToOneField(Author, on_delete=models.CASCADE)\n    bio = models.TextField()\n\n\n" +
        "class Tag(models.Model):\n    label = models.SlugField()\n\n\n" +
        "class Book(models.Model):\n    title = models.CharField(max_length=200)\n    pages = models.IntegerField(default=0)\n    rating = models.FloatField(null=True)\n" +
        "    in_print = models.BooleanField(default=True)\n    price = models.DecimalField(max_digits=6, decimal_places=2)\n    published = models.DateTimeField(auto_now_add=True)\n" +
        "    author = models.ForeignKey(Author, on_delete=models.CASCADE)\n    editor = models.ForeignKey(\"Author\", null=True, on_delete=models.SET_NULL, related_name=\"edited\")\n" +
        "    tags = models.ManyToManyField(\"Tag\", related_name=\"books\")\n    status = models.CharField(max_length=10, choices=[(\"d\", \"Draft\")])\n";

    /// <summary>The library files as (uri, text) pairs, for a language service whose source root is c:/proj.</summary>
    public static IEnumerable<(string Uri, string Text)> LibraryFiles() => Library.Select(f => (Root + f.Path, f.Text));

    public static LanguageServiceBuilder Service() => new();

    public sealed class LanguageServiceBuilder
    {
        readonly PyMagic.Server.LanguageService _svc = new();

        public LanguageServiceBuilder()
        {
            _svc.SourceRoots.Add("c:/proj");
            foreach (var (u, t) in LibraryFiles()) _svc.Open(u, t, 1);
        }

        public PyMagic.Server.LanguageService Service => _svc;

        public LanguageServiceBuilder With(string path, string text) { _svc.Open(Root + path, text, 1); return this; }
    }
}
