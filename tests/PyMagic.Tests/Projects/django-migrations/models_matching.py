from django.db import models


class Stamped(models.Model):
    created = models.DateTimeField(auto_now_add=True)

    class Meta:
        abstract = True


class Author(models.Model):
    name = models.CharField(max_length=100)


class Book(models.Model):
    title = models.CharField(max_length=200)
    subtitle = models.CharField(max_length=200)
    page_count = models.IntegerField()
    rating = models.FloatField(null=True)
    author = models.ForeignKey(Author, on_delete=models.CASCADE)


class Cheap(Book):
    class Meta:
        proxy = True


class Legacy(models.Model):
    code = models.CharField(max_length=10)

    class Meta:
        managed = False
