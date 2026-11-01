"""PyMagic's reStructuredText helper: {"text": ...} as JSON on stdin, {"html": ...} on stdout (docutils, when the Python has it).

The page shows the HTML after taking out anything but a list of safe tags, and the settings here already keep a document from reading files
or inserting raw HTML.
"""
import json
import sys


def main():
    request = json.loads(sys.stdin.read())
    try:
        import docutils.core
    except ImportError:
        sys.stdout.write(json.dumps({"error": "docutils is not installed in this Python: pip install docutils", "missing": True}))
        return
    try:
        parts = docutils.core.publish_parts(
            source=request.get("text", ""), writer_name="html5",
            settings_overrides={"raw_enabled": False, "file_insertion_enabled": False, "report_level": 3, "halt_level": 5,
                                "warning_stream": False, "embed_stylesheet": False, "stylesheet_path": None, "syntax_highlight": "none"})
        sys.stdout.write(json.dumps({"html": parts["html_body"], "title": parts.get("title", "")}))
    except Exception as e:
        sys.stdout.write(json.dumps({"error": "%s: %s" % (type(e).__name__, e)}))


if __name__ == "__main__":
    main()
