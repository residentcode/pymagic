# The Django app of DjangoQueryTests

`models.py` (Author, and Book with a foreign key to it) and `views.py`, which has, line by line, what item 154 checks:
a query through a related manager (`author.book_set.filter(titel=...)`), `Prefetch("bok_set")` next to a right `Prefetch("book_set", ...)`,
`OuterRef("pkk")` of an inner query kept in a variable, and a right inline `OuterRef("name")`.
Each was run with Django 6.1 first: the wrong ones raise FieldError or AttributeError, the right ones run.
