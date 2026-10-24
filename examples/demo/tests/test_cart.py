import pytest

from shop.models import Cart, Category, Product


def make_cart() -> Cart:
    cart = Cart()
    cart.add(Product("Dune", 9.5))
    cart.add(Product("Robot", 20.0, Category.TOY))
    return cart


def test_total():
    assert make_cart().total() == 29.5


def test_empty_cart_is_free():
    assert Cart().total() == 0


def test_wrong_expectation():
    assert make_cart().total() == 30   # fails on purpose


@pytest.mark.skip(reason="coupons are not implemented yet")
def test_coupon():
    assert False


@pytest.mark.parametrize("price, count, expected", [(2.0, 3, 6.0), (1.5, 2, 3.0), (5.0, 1, 4.0)])
def test_many_items(price, count, expected):
    cart = Cart()
    for _ in range(count):
        cart.add(Product("x", price))
    assert cart.total() == expected


class TestLabels:
    def test_label_mentions_category(self):
        assert "toy" in Product("Robot", 1.0, Category.TOY).label()

    def test_label_has_name(self):
        assert Product("Dune", 1.0).label().startswith("Dune")
