"""Debug Cell on a Jupyter server: PyMagic sends this text to the kernel (JupyterKernel.cs), together with pm_debug.py, and calls run_debugged.

The cell runs the way pm_kernel.py runs one for the built-in kernel: under the name of a file that stands for it (so the breakpoints of the cell in the
IDE stop it), with the debugger of the IDE connected back to the IDE's debug server. IPython's own transformation turns magics and shell lines into
Python first. The value of a last expression is shown through the display hook, and a cell that raises is shown the usual way.

A kernel on another computer cannot reach the IDE's debug server (no way in is opened for it), so there the debugger talks over the
kernel's own channels instead (`channel=True`, KernelChannel): what it sends goes out on IOPub as display data of a type of its own,
which PyMagic takes out of the cell's output, and what the IDE sends comes back as the answers to input requests on the stdin channel,
asked one after the other by the debugger's reader thread (the cell's own thread is busy running the cell). No port is opened.
"""
import ast
import linecache
import os
import socket
import sys
import threading

MIME = "application/vnd.pymagic.debug+json"   # what the debugger sends (JupyterKernel.cs knows it)
PROMPT = "\x01pymagic-debug"                   # the prompt of the input requests that ask for what the IDE sends


class KernelChannel:
    """The debugger's connection to the IDE through the kernel's channels: a socket for pm_debug (sendall, makefile, close, shutdown).

    The ident and the parent of the cell's execute request are taken here, on the cell's thread, because the kernel keeps them for
    the message it is working on; the reader thread asks with them."""

    def __init__(self, ipython):
        kernel = ipython.kernel
        self.kernel = kernel
        get_parent = getattr(kernel, "get_parent", None)
        self.parent = get_parent("shell") if get_parent is not None else kernel._parent_header
        ident = kernel._parent_ident
        self.ident = ident["shell"] if isinstance(ident, dict) else ident
        self.closed = False
        self.reader = None

    def free_threads(self):
        """The kernel's threads that carry messages (IOPub, and the control and shell channel threads of newer kernels): the debugger
        neither traces nor holds them, or what it sends while a thread is stopped would wait for that thread to go on."""
        kernel = self.kernel
        found = []
        for holder in (getattr(kernel, "iopub_thread", None), getattr(kernel, "control_thread", None), getattr(kernel, "shell_channel_thread", None)):
            thread = getattr(holder, "thread", holder)
            ident = getattr(thread, "ident", None)
            if isinstance(ident, int) and thread is not threading.main_thread():
                found.append(ident)
        return found

    def publish(self, text):
        kernel = self.kernel
        topic = kernel._topic("display_data") if hasattr(kernel, "_topic") else None
        kernel.session.send(kernel.iopub_socket, "display_data", {"data": {MIME: text}, "metadata": {}, "transient": {}}, parent=self.parent, ident=topic)

    def sendall(self, data):
        if self.closed:
            raise OSError("the channel to the IDE is closed")
        self.publish(data.decode("utf-8"))

    def makefile(self, *args, **kwargs):
        return self.lines()

    def lines(self):
        # each answer holds one or more lines; an empty one means the IDE is gone
        self.reader = threading.current_thread()
        while not self.closed:
            try:
                value = self.kernel._input_request(PROMPT, self.ident, self.parent, password=False)
            except BaseException:  # noqa: BLE001 - an interrupt or a kernel that cannot ask: the IDE is gone for the debugger
                return
            if not value:
                return
            for line in value.splitlines():
                yield line + "\n"

    def close(self):
        if self.closed:
            return
        self.closed = True
        try:
            self.publish("")   # an empty message: PyMagic ends its side, and answers the question the reader thread is waiting on
        except BaseException:  # noqa: BLE001
            pass

    def shutdown(self, how=None):
        self.close()

    def wait(self, seconds=5):
        """Lets the reader thread see the answer to its last question, so no question of the debugger is left for the next cell."""
        reader = self.reader
        if reader is not None and reader is not threading.current_thread():
            reader.join(seconds)


def run_debugged(code, file, host, port, token, user_ns, ipython=None, channel=False):
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
        sock = KernelChannel(ipython) if channel and ipython is not None else None
        dbg = pm_debug.connect(host, int(port), token, root, sock=sock)
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
                if isinstance(dbg.sock, KernelChannel):
                    dbg.sock.wait()
            except BaseException:  # noqa: BLE001
                pass
