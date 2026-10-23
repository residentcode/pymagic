from shop.models import Cart, Category, Product


def describe(cart: Cart) -> str:
    if cart.coupon is not None:
        return cart.coupon.upper()
    return f"{len(cart.items)} items"


cart = Cart()
cart.add(Product("Dune", 9.5))
cart.add(Product("Robot", 20.0, Category.TOY))

print(describe(cart), cart.total())
print(cart.itemz)          # unknown attribute
cart.add()                 # missing argument
print(undefined_name)      # unresolved
