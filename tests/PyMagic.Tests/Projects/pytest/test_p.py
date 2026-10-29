def test_p(shared, nested):
    assert shared == "from -p" and nested.startswith("from a plugin")


def test_anyio_off(anyio_backend):
    pass
