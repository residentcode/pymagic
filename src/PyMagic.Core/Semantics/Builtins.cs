namespace PyMagic.Core.Semantics;

/// <summary>Names available in every module without an import (Python 3.13), plus implicit module/class/function names.</summary>
public static class Builtins
{
    public static readonly HashSet<string> Names = new(StringComparer.Ordinal)
    {
        // functions
        "abs", "aiter", "all", "anext", "any", "ascii", "bin", "breakpoint", "callable", "chr", "compile",
        "delattr", "dir", "divmod", "eval", "exec", "format", "getattr", "globals", "hasattr", "hash", "help",
        "hex", "id", "input", "isinstance", "issubclass", "iter", "len", "locals", "max", "min", "next", "oct",
        "open", "ord", "pow", "print", "repr", "round", "setattr", "sorted", "sum", "vars", "__import__",
        "__build_class__",
        // types
        "bool", "bytearray", "bytes", "classmethod", "complex", "dict", "enumerate", "filter", "float",
        "frozenset", "int", "list", "map", "memoryview", "object", "property", "range", "reversed", "set",
        "slice", "staticmethod", "str", "super", "tuple", "type", "zip",
        // constants
        "None", "True", "False", "Ellipsis", "NotImplemented", "__debug__", "copyright", "credits", "license",
        "exit", "quit",
        // exceptions
        "BaseException", "BaseExceptionGroup", "Exception", "ExceptionGroup", "GeneratorExit", "KeyboardInterrupt",
        "SystemExit", "ArithmeticError", "AssertionError", "AttributeError", "BufferError", "EOFError",
        "ImportError", "LookupError", "MemoryError", "NameError", "OSError", "ReferenceError", "RuntimeError",
        "StopAsyncIteration", "StopIteration", "SyntaxError", "SystemError", "TypeError", "ValueError",
        "FloatingPointError", "OverflowError", "ZeroDivisionError", "ModuleNotFoundError", "IndexError",
        "KeyError", "UnboundLocalError", "BlockingIOError", "ChildProcessError", "ConnectionError",
        "BrokenPipeError", "ConnectionAbortedError", "ConnectionRefusedError", "ConnectionResetError",
        "FileExistsError", "FileNotFoundError", "InterruptedError", "IsADirectoryError", "NotADirectoryError",
        "PermissionError", "ProcessLookupError", "TimeoutError", "NotImplementedError", "RecursionError",
        "IndentationError", "TabError", "UnicodeError", "UnicodeDecodeError", "UnicodeEncodeError",
        "UnicodeTranslateError", "Warning", "UserWarning", "DeprecationWarning", "PendingDeprecationWarning",
        "SyntaxWarning", "RuntimeWarning", "FutureWarning", "ImportWarning", "UnicodeWarning", "BytesWarning",
        "ResourceWarning", "EncodingWarning", "PythonFinalizationError", "EnvironmentError", "IOError",
        "WindowsError",
        // implicit module / class / function names
        "__name__", "__file__", "__doc__", "__package__", "__spec__", "__loader__", "__builtins__", "__path__",
        "__cached__", "__annotations__", "__qualname__", "__module__", "__class__", "__dict__",
    };
}
