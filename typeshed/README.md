# typeshed

The type stubs of Python's standard library (`stdlib/`) and of third-party packages (`stubs/`), from
[typeshed](https://github.com/python/typeshed) at commit `8d185c6c42d70d79251478cbd37bccafc56a409f` (2026-10-29). PyMagic ships them so that its type
checks are the same on every PC, with or without PyCharm. Only what PyMagic reads is kept: the `.pyi` files,
`stdlib/VERSIONS` and each distribution's `METADATA.toml`; typeshed's own tests are left out. typeshed's licence is in
`LICENSE`.

To update: download another commit of typeshed and copy the same files over this folder.
