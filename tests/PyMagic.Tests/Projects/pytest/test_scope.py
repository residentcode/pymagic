import pytest


@pytest.fixture(scope="session")
def db(tmp_path):
    return tmp_path


@pytest.fixture(scope="module")
def conn(db):
    return db


@pytest.fixture(scope="session")
def fine(tmp_path_factory, request):
    return tmp_path_factory


def test_db(db):
    pass


def test_conn(conn):
    pass


def test_fine(fine):
    pass
