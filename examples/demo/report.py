from shop.models import Cart


def summary(cart: Cart, discount: float) -> str:
    total = 0.0
    names = []
    for item in cart.items:
        total += item.price
        names.append(item.name.upper())
    final = total - total * discount / 100
    return f"{', '.join(names)}: {final:.2f}"
