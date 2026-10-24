import yaml                                  # not installed here: offers "Install package 'PyYAML'"
from shop.models import Cart, Prodcut        # typo: offers "Change to 'Product'"


def load(path: str) -> Cart:
    with open(path) as handle:
        data = yaml.safe_load(handle)
    cart = Cart()
    for entry in data["items"]:
        cart.add(Prodcut(entry["name"], entry["price"]))
    return cart
