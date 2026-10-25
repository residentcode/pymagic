using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>The inspections that need types: protected members, tuple items, functions returning nothing, exceptions, properties.</summary>
public class TypedInspectionTests
{
    static List<string> Problems(string src, bool withTypeshed = false)
    {
        var ws = new Workspace();
        ws.AddModule("other", "class Box:\n    _hidden = 1\n\n    def __init__(self):\n        self._inner = 2\n        self.shown = 3\n\n\nVALUE = 1\n_PRIVATE = 2\n");
        var m = ws.AddModule("main", src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        return ws.Evaluator.Inspect(m).Select(d => d.Message).ToList();
    }

    [Fact]
    public void AProtectedMemberFromOutside()
    {
        var found = Problems("from other import Box\n\nb = Box()\nprint(b._inner)\nprint(b.shown)\nprint(Box._hidden)\n");
        Assert.Equal(2, found.Count(m => m.StartsWith("Access to a protected member")));
        Assert.True(found.Any(m => m.Contains("_inner") && m.Contains("class")), string.Join(" / ", found));
    }

    [Fact]
    public void AProtectedMemberOfAModule()
    {
        var found = Problems("import other\n\nprint(other._PRIVATE)\nprint(other.VALUE)\n");
        Assert.Equal(1, found.Count(m => m.StartsWith("Access to a protected member") && m.Contains("module")));
    }

    [Fact]
    public void ProtectedMembersAreFineInsideTheClassAndItsSubclasses()
    {
        var found = Problems("from other import Box\n\n\nclass Mine(Box):\n    def f(self):\n        print(self._inner)\n        other = Box()\n        print(other._inner)\n\n\nclass Own:\n    def __init__(self):\n        self._x = 1\n\n    def g(self, o):\n        print(self._x, o._x)\n");
        Assert.Equal(0, found.Count(m => m.StartsWith("Access to a protected member")));
    }

    [Fact]
    public void ItemAssignmentOnATuple()
    {
        var found = Problems("t = (1, 2)\nt[0] = 5\nl = [1, 2]\nl[0] = 5\nd = {}\nd['a'] = 1\n");
        Assert.Equal(1, found.Count(m => m.Contains("does not support item assignment")));
    }

    [Fact]
    public void AFunctionThatReturnsNothing()
    {
        var found = Problems("def log(x):\n    print(x)\n\n\ndef value():\n    return 1\n\n\ndef maybe(x):\n    if x:\n        return x\n\n\ndef stub():\n    pass\n\n\na = log(1)\nb = value()\nc = maybe(1)\nd = stub()\n");
        Assert.Equal(1, found.Count(m => m.EndsWith("doesn't return anything")));
        Assert.Contains("'log'", found.First(m => m.EndsWith("doesn't return anything")));
    }

    [Fact]
    public void AMethodOfAClassWithBasesMayBeOverridden()
    {
        var found = Problems("class Base:\n    def run(self):\n        print(1)\n\n\nclass Child(Base):\n    def run(self):\n        print(2)\n\n\nx = Child().run()\n");
        Assert.Equal(0, found.Count(m => m.EndsWith("doesn't return anything")));
    }

    [Fact]
    public void ExceptClausesThatCannotRun()
    {
        var found = Problems("try:\n    pass\nexcept Exception:\n    pass\nexcept ValueError:\n    pass\nexcept (KeyError, OSError):\n    pass\n");
        Assert.Equal(3, found.Count(m => m.Contains("already handled by an earlier clause")));
        Assert.Equal(0, Problems("try:\n    pass\nexcept ValueError:\n    pass\nexcept Exception:\n    pass\n").Count(m => m.Contains("already handled")));
    }

    [Fact]
    public void RaisingSomethingThatIsNoException()
    {
        var found = Problems("class NotOne:\n    pass\n\n\nclass Fine(Exception):\n    pass\n\n\ndef f():\n    raise NotOne()\n\n\ndef g():\n    raise Fine('x')\n\n\ndef h():\n    raise ValueError\n");
        Assert.Equal(1, found.Count(m => m.StartsWith("Exceptions must derive")));
    }

    [Fact]
    public void CatchingSomethingThatIsNoException()
    {
        var found = Problems("class NotOne:\n    pass\n\n\ntry:\n    pass\nexcept NotOne:\n    pass\nexcept ValueError:\n    pass\n");
        Assert.Equal(1, found.Count(m => m.StartsWith("Exceptions must derive")));
    }

    [Fact]
    public void ARaiseOfAnUnknownClassIsNotJudged()
    {
        Assert.Equal(0, Problems("from nowhere import Mystery\n\n\ndef f():\n    raise Mystery()\n").Count(m => m.StartsWith("Exceptions must derive")));
    }

    [Fact]
    public void AssignmentToAReadOnlyProperty()
    {
        string cls = "class P:\n    @property\n    def ro(self):\n        return 1\n\n    @property\n    def rw(self):\n        return 2\n\n    @rw.setter\n    def rw(self, v):\n        pass\n\n\n";
        var found = Problems(cls + "p = P()\np.ro = 3\np.rw = 4\n");
        Assert.Equal(1, found.Count(m => m.EndsWith("cannot be set")));
        Assert.Contains("'ro'", found.First(m => m.EndsWith("cannot be set")));
    }
}
