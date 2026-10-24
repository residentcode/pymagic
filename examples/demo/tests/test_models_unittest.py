import unittest

from shop.models import Cart, Product


class CartTests(unittest.TestCase):
    def test_items_are_stored(self):
        cart = Cart()
        cart.add(Product("Dune", 9.5))
        self.assertEqual(len(cart.items), 1)

    def test_coupon_defaults_to_none(self):
        self.assertIsNone(Cart().coupon)

    @unittest.skip("not ready")
    def test_skipped(self):
        self.fail()

    def test_raises(self):
        with self.assertRaises(AttributeError):
            Cart().missing_attribute


if __name__ == "__main__":
    unittest.main()
