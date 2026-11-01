"""Debug Cell on a Jupyter server: PyMagic sends this text to the kernel (JupyterKernel.cs), together with pm_debug.py, and calls run_debugged.

The cell runs the way pm_kernel.py runs one for the built-in kernel: under the name of a file that stands for it (so the breakpoints of the cell in the
IDE stop it), with the debugger of the IDE connected back to the IDE's debug server. IPython's own transformation turns magics and shell lines into
Python first. The value of a last expression is shown through the display hook, and a cell that raises is shown the usual way.
"""
import ast
import linecache
import os
import socket
import sys


def run_debugged(code, file, host, port, token, user_ns, ipython=None):
    import pm_debug
    root = os.getcwd()
    filename = os.path.normpath(os.path.join(root, file))
    source = ipython.transform_cell(code) if ipython is not None else code
    linecache.cache[filename] = (len(source), None, source.splitlines(True), filename)
    # bdb looks a breakpoint's line up under the file name in its canonical form (lower case on Windows)
    canonical = os.path.normcase(os.path.abspath(filename))
    linecache.cache[canonical] = (len(source), None, source.splitlines(True), canonical)
    dbg = None
    try:
        tree = ast.parse(source, filename)
        last = None
        if tree.body and isinstance(tree.body[-1], ast.Expr):
            last = ast.Expression(tree.body.pop().value)
        body = compile(tree, filename, "exec")
        final = compile(last, filename, "eval") if last is not None else None
        dbg = pm_debug.connect(host, int(port), token, root)
        exec(body, user_ns)
        if final is not None:
            value = eval(final, user_ns)
            if value is not None:
                sys.displayhook(value)
    except BaseException:  # noqa: BLE001 - what a cell raises is shown like any other cell's error, not lost
        if ipython is not None:
            ipython.showtraceback()
        else:
            raise
    finally:
        if dbg is not None:
            try:
                dbg.detach()                              # the kernel goes on without the debugger
                dbg.sock.shutdown(socket.SHUT_RDWR)       # (its reader thread still holds the socket: this is what lets the IDE see the connection end)
            except BaseException:  # noqa: BLE001
                pass
