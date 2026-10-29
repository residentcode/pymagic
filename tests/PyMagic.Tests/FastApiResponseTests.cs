namespace PyMagic.Tests;

/// <summary>
/// What a FastAPI endpoint returns against its response model (item 147), with the real FastAPI and Pydantic (skipped where they are not installed).
/// Each case was answered by FastAPI 0.141 and Pydantic 2.13 first: the reported ones fail with ResponseValidationError (500), the others answer 200.
/// </summary>
public class FastApiResponseTests
{
    const string Models = @"
from fastapi import FastAPI
from fastapi.responses import JSONResponse
from pydantic import BaseModel, Field

app = FastAPI()


class Item(BaseModel):
    name: str
    price: float = Field(gt=0)
    note: str = ''


class User(BaseModel):
    name: str


class Full(BaseModel):
    name: str
    price: float
    extra: int = 0


class Plain:
    def __init__(self):
        self.name = 'p'
        self.price = 1.0

";

    static List<string> Reported(string endpoints)
    {
        if (RealFastApiTests.Site == null) return new();
        var p = RealFastApiTests.Open(Models + endpoints);
        return p.Problems().Where(d => d.Code == "fastapi-response-model").OrderBy(d => d.Range.Start.Line)
            .Select(d => d.Message["FastAPI cannot answer with ".Length..d.Message.IndexOf(": the request fails", StringComparison.Ordinal)]).ToList();
    }

    [Fact]
    public void AnswersTheModelRefusesAreErrors()
    {
        if (RealFastApiTests.Site == null) return;
        var found = Reported(@"
@app.get('/a', response_model=Item)
def a():
    return {'name': 'a'}


@app.get('/b')
def b() -> Item:
    return {'name': 'a', 'price': 'cheap'}


@app.get('/c', response_model=Item)
def c():
    return User(name='u')


@app.get('/d', response_model=Item)
def d():
    return 'text'


@app.get('/e', response_model=Item)
def e():
    return None


@app.get('/f', response_model=list[Item])
def f():
    return {'name': 'a', 'price': 1}


@app.get('/g', response_model=list[Item])
def g():
    return [{'name': 'a', 'price': 1}, {'name': 'b'}]


@app.get('/h', response_model=str)
def h():
    return 5
");
        Assert.Equal(string.Join("\n", new[]
        {
            "a dict without 'price', which Item requires as Item",
            "'cheap' for 'price', which is a float as Item",
            "a User, which has no 'price' as Item",
            "a str as Item",
            "None as Item",
            "a dict where a list is wanted as list[Item]",
            "a dict without 'price', which Item requires as item 2 of the list as list[Item]",
            "an int where a str is wanted as str",
        }), string.Join("\n", found));
    }

    [Fact]
    public void AnswersTheModelTakesAreNotReported()
    {
        if (RealFastApiTests.Site == null) return;
        var found = Reported(@"
@app.get('/a', response_model=Item)
def a():
    return {'name': 'a', 'price': 1, 'zzz': 2}


@app.get('/b', response_model=Item)
def b():
    return {'name': 'a', 'price': '2.5'}


@app.get('/c', response_model=Item)
def c():
    return Full(name='f', price=2)


@app.get('/d', response_model=Item)
def d():
    return Plain()


@app.get('/e', response_model=Item)
def e():
    return JSONResponse({'x': 1})


@app.get('/f', response_model=list[Item])
def f():
    return [{'name': 'a', 'price': 1}]


@app.get('/g', response_model=None)
def g():
    return 5


@app.get('/h')
def h() -> Item:
    data = {'name': 'a', 'price': 1}
    return Item(**data)


@app.get('/i', response_model=Item)
def i(payload: dict):
    return {**payload, 'name': 'x'}
");
        Assert.Equal("", string.Join("\n", found));
    }
}
