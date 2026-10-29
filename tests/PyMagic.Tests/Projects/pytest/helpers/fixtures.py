import pytest

pytest_plugins = ["helpers.more"]


@pytest.fixture
def shared():
    return "from -p"
