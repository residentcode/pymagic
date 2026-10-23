from shop.models import Cart, Category, Product


def apply_discount(cart: Cart, percent: float) -> float:
    total = cart.total()
    discount = total * percent / 100
    return total - discount


cart = Cart()
cart.add(Product("Dune", 9.5))
cart.add(Product("Robot", 20.0, Category.TOY))

for item in cart.items:
    print(item.label())

final = apply_discount(cart, 10)
print(f"Total after discount: {final:.2f}")
