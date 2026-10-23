from dataclasses import dataclass, field
from enum import Enum
from typing import Optional


class Category(Enum):
    BOOK = "book"
    TOY = "toy"


@dataclass
class Product:
    name: str
    price: float
    category: Category = Category.BOOK
    tags: list[str] = field(default_factory=list)

    def label(self) -> str:
        return f"{self.name} ({self.category.value})"


class Cart:
    def __init__(self) -> None:
        self.items: list[Product] = []
        self.coupon: Optional[str] = None

    def add(self, product: Product) -> None:
        self.items.append(product)

    def total(self) -> float:
        return sum(p.price for p in self.items)
