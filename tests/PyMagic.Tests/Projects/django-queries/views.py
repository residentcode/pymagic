from django.db.models import OuterRef, Prefetch, Subquery

from shop.models import Author, Book


def gaps(author: Author):
    # a related manager: the fields are Book's
    author.book_set.filter(titel="x")
    author.book_set.filter(title="x")
    # Prefetch: a relation of Author, and a queryset of its model
    Author.objects.prefetch_related(Prefetch("bok_set"), Prefetch("book_set", queryset=Book.objects.filter(titel="y")))
    # OuterRef names a field of the outer query's model (Author)
    newest = Book.objects.filter(author=OuterRef("pkk")).order_by("-id").values("title")[:1]
    Author.objects.annotate(newest=Subquery(newest))
    # inline, and right: name is a field of Author
    Author.objects.annotate(n=Subquery(Book.objects.filter(title=OuterRef("name")).values("title")[:1]))
    return author.book_set.first()
