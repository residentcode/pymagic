from django.db import models


class Author(models.Model):
    name = models.CharField(max_length=100)


class Book(models.Model):
    title = models.CharField(max_length=200)
    page_count = models.IntegerField()
    rating = models.DecimalField(max_digits=3, decimal_places=1, null=True)
    price = models.DecimalField(max_digits=6, decimal_places=2, default=0)
    author = models.ForeignKey(Author, on_delete=models.CASCADE)


class Review(models.Model):
    book = models.ForeignKey(Book, on_delete=models.CASCADE)
    text = models.TextField()
