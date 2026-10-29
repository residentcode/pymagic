# The Django app of DjangoMigrationTests

`shop`'s migrations and two versions of its `models.py`, used by `DjangoMigrationTests` (item 150):

- `0001_initial.py`, `0002_book_rating.py`: the migrations (the second adds `rating` and renames `pages` to `page_count`).
- `models.py`: the models changed since: `price` added, `subtitle` removed, `rating` a `DecimalField`, `Review` new.
  Django 6.1's `makemigrations --dry-run` on this app found exactly those four changes; `expected.txt` is what PyMagic says about them.
- `models_matching.py`: the models as the migrations leave them, with an abstract base, a proxy and an unmanaged model: nothing to say.
- `0001_squashed_0002_book_rating.py`: a squashed migration that stands in for the two.

They are files rather than strings in the test so that the test program holds no Python source or command lines
(Kaspersky's heuristics took a test program with them for malware and deleted it).
