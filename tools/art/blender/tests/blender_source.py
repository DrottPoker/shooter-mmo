"""Reads the definitions of a Blender module from its source, so the pure logic of the art pipeline is
tested without Blender.

Most modules of tools/art/blender start with `import bpy`, although most of their functions and tables
never touch Blender, so they cannot be imported on a machine without it, such as CI. load() parses the
file, takes the named definitions and everything of the same file they use (functions, classes,
constants, and imports of the standard library), and runs only those. What it runs is the source text
of the module, not a copy or a stand-in: no fake `bpy` exists anywhere. A definition that needs
Blender fails loudly where a test calls it, with a NameError that names `bpy`, `bmesh`, or
`mathutils`, and that is the sign that its logic belongs in a module of its own, as cloth_core.py is.

A collaborating module of the pipeline, such as `cage` in garment.py, is not run: the test passes it
as a keyword, from another load() or as a stand-in for what the test is about. numpy is passed with
numpy=True and skips the test when it is not installed; CI installs it from requirements.txt."""

from __future__ import annotations

import ast
import builtins
import pathlib
import re
import sys
import types
import unittest

BLENDER_ROOT = pathlib.Path(__file__).resolve().parents[1]
REPOSITORY_ROOT = BLENDER_ROOT.parents[2]
_STANDARD = set(sys.stdlib_module_names)
_parsed: dict[pathlib.Path, ast.Module] = {}


def _tree(path: pathlib.Path) -> ast.Module:
    if path not in _parsed:
        _parsed[path] = ast.parse(path.read_text(encoding="utf-8"), filename=str(path))
    return _parsed[path]


def _defined(node: ast.stmt) -> list[str]:
    if isinstance(node, (ast.FunctionDef, ast.ClassDef)):
        return [node.name]
    if isinstance(node, ast.Assign):
        return [name for target in node.targets for name in _target_names(target)]
    if isinstance(node, ast.AnnAssign):
        return _target_names(node.target)
    if isinstance(node, ast.Import):
        return [(alias.asname or alias.name).split(".")[0] for alias in node.names if alias.name.split(".")[0] in _STANDARD]
    if isinstance(node, ast.ImportFrom) and node.level == 0 and node.module and node.module.split(".")[0] in _STANDARD:
        return [alias.asname or alias.name for alias in node.names]
    return []


def _target_names(target: ast.expr) -> list[str]:
    if isinstance(target, ast.Name):
        return [target.id]
    if isinstance(target, (ast.Tuple, ast.List)):
        return [name for element in target.elts for name in _target_names(element)]
    return []


def _used(node: ast.stmt) -> set[str]:
    return {n.id for n in ast.walk(node) if isinstance(n, ast.Name) and isinstance(n.ctx, ast.Load)}


def load(relative_path: str, *names: str, numpy: bool = False, **provided: object) -> types.SimpleNamespace:
    """The named definitions of tools/art/blender/<relative_path>, with their dependencies in that file.

    provided gives the names the file takes from other modules. A dependency that cannot run without
    Blender is left out, and fails with a NameError only if a test reaches it."""
    path = BLENDER_ROOT / relative_path
    spaces = {"__name__": f"blender_source.{path.stem}", "__builtins__": builtins, **provided}
    if numpy:
        try:
            import numpy as np
        except ImportError:
            raise unittest.SkipTest("numpy is not installed; see requirements.txt") from None
        spaces.setdefault("np", np)
    spaces["__doc__"] = ast.get_docstring(_tree(path), clean=False)
    module = types.ModuleType(spaces["__name__"])
    module.__dict__.update(spaces)
    sys.modules[module.__name__] = module  # dataclasses look their module up here
    nodes = [n for n in _tree(path).body if _defined(n)]
    defined = {name for node in nodes for name in _defined(node)}
    missing = [name for name in names if name not in defined]
    if missing:
        raise LookupError(f"{relative_path} defines no {missing}")
    # What the requested names need, and what that needs in turn.
    wanted = set(names)
    pending = list(names)
    while pending:
        name = pending.pop()
        for node in nodes:
            if name in _defined(node):
                for used in _used(node) & defined:
                    if used not in wanted and used not in provided:
                        wanted.add(used)
                        pending.append(used)
    for node in nodes:
        if not set(_defined(node)) & wanted:
            continue
        code = compile(ast.Module(body=[node], type_ignores=[]), str(path), "exec", flags=_ANNOTATIONS, dont_inherit=True)
        try:
            exec(code, module.__dict__)
        except NameError:
            # A dependency that needs Blender at definition time; a requested name must not.
            if set(_defined(node)) & set(names):
                raise
    return types.SimpleNamespace(**{name: module.__dict__[name] for name in names})


# Annotations stay text, as `from __future__ import annotations` makes them, so a signature that
# names bpy.types.Object defines without Blender.
_ANNOTATIONS = __import__("__future__").annotations.compiler_flag


def source_text(relative_path: str) -> str:
    """A file of the pipeline as text, for tests of what a script names."""
    return (BLENDER_ROOT / relative_path).read_text(encoding="utf-8")


def repository_text(relative_path: str) -> str:
    return (REPOSITORY_ROOT / relative_path).read_text(encoding="utf-8")


def powershell_list(script: str, name: str) -> list[str]:
    """The items of `$script:<name> = @("a", "b")` in a PowerShell file below tools/art, quotes removed,
    where the PowerShell scripts keep the tables that the Blender code keeps too."""
    text = (REPOSITORY_ROOT / "tools" / "art" / script).read_text(encoding="utf-8")
    match = re.search(r"\$script:" + re.escape(name) + r"\s*=\s*@\(([^)]*)\)", text)
    if match is None:
        raise LookupError(f"{script} has no list {name}")
    return [item.strip().strip('"') for item in match.group(1).split(",") if item.strip()]


def powershell_value(script: str, name: str) -> str:
    """The text of `$script:<name> = <value>` on one line, quotes removed."""
    text = (REPOSITORY_ROOT / "tools" / "art" / script).read_text(encoding="utf-8")
    match = re.search(r"\$script:" + re.escape(name) + r"\s*=\s*(\S[^\r\n]*)", text)
    if match is None:
        raise LookupError(f"{script} has no value {name}")
    return match.group(1).strip().strip("'\"")
