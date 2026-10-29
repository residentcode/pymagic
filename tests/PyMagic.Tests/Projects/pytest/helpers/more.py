import pytest


@pytest.fixture
def nested():
    return "from a plugin's pytest_plugins"
