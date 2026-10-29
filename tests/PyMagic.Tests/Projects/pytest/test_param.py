import pytest


@pytest.fixture(params=[1, 2, 3])
def number(request):
    return request.param


@pytest.fixture(params=["a", ("b", "c")])
def mixed(request):
    return type(request.param).__name__


def test_number(number):
    assert isinstance(number, int)


def test_mixed(mixed):
    assert mixed in ("str", "tuple")
