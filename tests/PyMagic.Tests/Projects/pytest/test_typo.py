import pytest


@pytest.fixture(scope="sesion")
def typo():
    return 1


def test_typo(typo):
    pass
