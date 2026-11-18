"""The two command line files of the art pipeline, art_pipeline_cli.py and human_base_cli.py, as far as
they are arguments and dispatch: what the parser accepts, how the list arguments are split, which step
takes how many arguments, and what the files print, checked against how the PowerShell scripts under
tools/art call them. Runs without Blender: see blender_source.py."""

import argparse
import contextlib
import io
import os
import pathlib
import re
import sys
import types
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

from blender_source import BLENDER_ROOT, REPOSITORY_ROOT, load, source_text  # noqa: E402

ART_SCRIPTS = REPOSITORY_ROOT / "tools" / "art"
# What Blender itself takes before the "--" that hands the rest to the script.
BLENDER_OPTIONS = {"--background", "--factory-startup", "--python-use-system-env", "--python-exit-code", "--python",
                   "--disable-autoexec", "--version"}

scene = load("art_pipeline/scene.py", "ArtPipelineError", "parse_args")
texture = load("art_pipeline/texture.py", "DYE_LEVEL")
cli = load("art_pipeline_cli.py", "_parser", "_bodies", "_pairs", "_clip_libraries", "_held_attachments", texture=texture)


def subcommands() -> dict[str, argparse.ArgumentParser]:
    action = next(a for a in cli._parser()._actions if isinstance(a, argparse._SubParsersAction))
    return dict(action.choices)


def parse(*arguments: str) -> argparse.Namespace:
    with contextlib.redirect_stderr(io.StringIO()):
        return cli._parser().parse_args(list(arguments))


def powershell(*names: str) -> str:
    return "\n".join((ART_SCRIPTS / name).read_text(encoding="utf-8") for name in names)


def all_powershell() -> str:
    return "\n".join(path.read_text(encoding="utf-8") for path in sorted(ART_SCRIPTS.glob("*.ps1")))


class ScriptArgumentTests(unittest.TestCase):
    def _parse(self, argv):
        original = sys.argv
        sys.argv = argv
        try:
            parser = argparse.ArgumentParser()
            parser.add_argument("--name")
            parser.add_argument("rest", nargs="*")
            return scene.parse_args(parser)
        finally:
            sys.argv = original

    def test_only_what_follows_the_double_dash_is_the_scripts(self):
        args = self._parse(["blender", "--background", "--python", "x.py", "--", "--name", "Robe", "a", "b"])
        self.assertEqual((args.name, args.rest), ("Robe", ["a", "b"]))

    def test_without_a_double_dash_the_script_has_no_arguments(self):
        args = self._parse(["blender", "--name", "Robe"])
        self.assertEqual((args.name, args.rest), (None, []))

    def test_the_first_double_dash_splits_and_a_later_one_belongs_to_the_script(self):
        args = self._parse(["blender", "--", "--name", "a", "--", "b"])
        self.assertEqual(args.name, "a")


class ParserTests(unittest.TestCase):
    def test_a_command_is_required(self):
        with self.assertRaises(SystemExit):
            parse()

    def test_every_command_the_scripts_start_exists_and_every_command_is_started_by_a_script(self):
        text = all_powershell()
        started = set(re.findall(r'"--",\s*"([a-z][a-z-]+)"', text))
        started |= set(re.findall(r'@\("(validate|export|preview|reference|clips)"', text))
        self.assertEqual(started, set(subcommands()))

    def test_every_option_the_scripts_pass_is_an_option_of_some_command(self):
        known = {option for parser in subcommands().values() for action in parser._actions for option in action.option_strings}
        used = set(re.findall(r'"(--[a-z][a-z-]*)"', all_powershell())) - BLENDER_OPTIONS
        self.assertEqual(sorted(used - known), [])

    def test_validate_and_export_need_a_kind_and_the_root_objects(self):
        for command in ("validate", "export"):
            with self.subTest(command=command):
                with self.assertRaises(SystemExit):
                    parse(command, "--roots", "Rock")
                with self.assertRaises(SystemExit):
                    parse(command, "--kind", "static")

    def test_validate_has_the_defaults_a_static_asset_needs(self):
        args = parse("validate", "--kind", "static", "--roots", "Rock")
        self.assertEqual((args.command, args.stage, args.clips, args.body, args.pair, args.clip_library, args.dyes),
                         ("validate", "model", "", [], [], [], 0))
        self.assertIsNone(args.triangle_budget)
        self.assertFalse(args.covers_nothing)

    def test_the_triangle_budget_and_the_dyes_are_whole_numbers(self):
        args = parse("validate", "--kind", "garment", "--roots", "Male", "--triangle-budget", "3000", "--dyes", "2")
        self.assertEqual((args.triangle_budget, args.dyes), (3000, 2))
        with self.assertRaises(SystemExit):
            parse("validate", "--kind", "garment", "--roots", "Male", "--triangle-budget", "many")

    def test_a_list_option_collects_every_time_it_is_given(self):
        args = parse("validate", "--kind", "garment", "--roots", "Male,Female", "--body", "a.blend=Male", "--body",
                     "b.blend=Female", "--pair", "c.blend=Shirt:tight")
        self.assertEqual(args.body, ["a.blend=Male", "b.blend=Female"])
        self.assertEqual(args.pair, ["c.blend=Shirt:tight"])

    def test_only_export_writes_somewhere(self):
        export = parse("export", "--kind", "static", "--roots", "Rock", "--target", "out.fbx", "--textures", "tex")
        self.assertEqual((export.target, export.textures), ("out.fbx", "tex"))
        self.assertIsNone(export.opaque_only)
        with self.assertRaises(SystemExit):
            parse("validate", "--kind", "static", "--roots", "Rock", "--target", "out.fbx")

    def test_a_preview_needs_a_file_to_write_and_draws_its_cells_420_texels_wide(self):
        with self.assertRaises(SystemExit):
            parse("preview")
        args = parse("preview", "--output", "sheet.png", "--layer", "210")
        self.assertEqual((args.output, args.cell_size, args.kind, args.layer), ("sheet.png", 420, "static", 210))

    def test_the_commands_that_run_without_an_open_file_name_their_file(self):
        for command, required in (("new-static", ["--file", "--roots"]), ("garment-create", ["--file", "--garment", "--manifest", "--cage-manifest"]),
                                  ("garment-sync", ["--file", "--garment", "--manifest", "--cage-manifest"]),
                                  ("garment-review-file", ["--file", "--garment", "--output"]),
                                  ("texture-create", ["--file", "--texture", "--garment-file", "--garment", "--label"]),
                                  ("texture-sync", ["--file", "--texture", "--garment-file", "--garment", "--label"]),
                                  ("texture-dyes", ["--file", "--texture"]), ("cage-create", ["--file", "--manifest"]),
                                  ("cage-sync", ["--file", "--manifest"]), ("paint-sync", ["--file", "--paint", "--part"]),
                                  ("paint-projection", ["--file", "--paint", "--part", "--roots", "--cage"]),
                                  ("library-sync", ["--library", "--armature", "--rig-source", "--rig-root"])):
            parser = subcommands()[command]
            with self.subTest(command=command):
                self.assertEqual(sorted(o for a in parser._actions if a.required for o in a.option_strings), sorted(required))

    def test_a_texture_is_made_1024_texels_square_unless_told_and_dyed_at_the_code_s_level(self):
        args = parse("texture-create", "--file", "f", "--texture", "t", "--garment-file", "g", "--garment", "G", "--label", "Male")
        self.assertEqual(args.size, 1024)
        self.assertEqual(parse("texture-dyes", "--file", "f", "--texture", "t").level, texture.DYE_LEVEL)

    def test_a_garment_sync_may_transfer_weights_and_rebuild_and_a_review_file_may_not(self):
        args = parse("garment-sync", "--file", "f", "--garment", "G", "--manifest", "m", "--cage-manifest", "c", "--weights",
                     "--rebuild")
        self.assertTrue(args.weights and args.rebuild)
        with self.assertRaises(SystemExit):
            parse("garment-review-file", "--file", "f", "--garment", "G", "--output", "o", "--weights")

    def test_a_library_sync_keeps_its_clips_unless_forced(self):
        args = parse("library-sync", "--library", "l", "--armature", "a", "--rig-source", "s", "--rig-root", "r")
        self.assertFalse(args.force)
        self.assertEqual(args.preview, [])

    def test_a_reference_comparison_names_its_output_folder_and_its_root(self):
        parser = subcommands()["reference"]
        self.assertEqual(sorted(o for a in parser._actions if a.required for o in a.option_strings), ["--output", "--root"])
        args = parse("reference", "--output", "out", "--root", "Wolf")
        self.assertEqual((args.output, args.root), ("out", "Wolf"))

    def test_clips_render_on_a_still_ground_without_a_skeleton_unless_told(self):
        with self.assertRaises(SystemExit):
            parse("clips", "--output", "out")
        args = parse("clips", "--output", "out", "--armature", "Humanoid")
        self.assertEqual((args.clip, args.speed, args.skeleton, args.held_attachment), ([], 0.0, None, []))
        args = parse("clips", "--output", "out", "--armature", "Wolf", "--clip", "Walk", "--clip", "Run", "--speed", "1.4")
        self.assertEqual((args.clip, args.speed), (["Walk", "Run"], 1.4))
        with self.assertRaises(SystemExit):
            parse("clips", "--output", "out", "--armature", "Wolf", "--speed", "fast")


class ListArgumentTests(unittest.TestCase):
    def test_a_body_is_a_path_and_an_armature_split_at_the_last_equals_sign(self):
        args = parse("validate", "--kind", "garment", "--roots", "Male,Female", "--body", "art/a=b.blend=Male", "--body",
                     "other.blend=Female")
        self.assertEqual(cli._bodies(args), [(os.path.abspath("art/a=b.blend"), "Male"), (os.path.abspath("other.blend"), "Female")])

    def test_the_path_of_a_body_is_made_absolute(self):
        args = parse("validate", "--kind", "x", "--roots", "y", "--body", "rel.blend=Male")
        self.assertTrue(os.path.isabs(cli._bodies(args)[0][0]))

    def test_a_lower_template_is_a_path_a_name_and_a_band(self):
        args = parse("validate", "--kind", "garment", "--roots", "Male", "--pair", "s.blend=Shirt:tight", "--pair",
                     "t.blend=Trousers:mid")
        self.assertEqual(cli._pairs(args), [(os.path.abspath("s.blend"), "Shirt", "tight"),
                                            (os.path.abspath("t.blend"), "Trousers", "mid")])

    def test_a_band_comes_after_the_first_colon_of_the_template_part(self):
        args = parse("validate", "--kind", "garment", "--roots", "Male", "--pair", "s.blend=Name:a:b")
        self.assertEqual(cli._pairs(args), [(os.path.abspath("s.blend"), "Name", "a:b")])

    def test_a_clip_library_is_a_path_and_its_clips_joined_by_plus(self):
        args = parse("validate", "--kind", "garment", "--roots", "Male", "--clip-library", "h.blend=Idle+Walk+Jog")
        self.assertEqual(cli._clip_libraries(args), [(os.path.abspath("h.blend"), ["Idle", "Walk", "Jog"])])

    def test_a_held_attachment_is_a_path_a_clip_and_a_socket_bone(self):
        args = parse("clips", "--output", "o", "--armature", "Humanoid", "--held-attachment", "art/a=b.blend=Mine:Socket_MainHand")
        self.assertEqual(cli._held_attachments(args), [(os.path.abspath("art/a=b.blend"), "Mine", "Socket_MainHand")])
        args = parse("preview", "--output", "o", "--held-attachment", "s.blend=SwordSlash:Socket_MainHand")
        self.assertEqual(cli._held_attachments(args), [(os.path.abspath("s.blend"), "SwordSlash", "Socket_MainHand")])

    def test_no_lists_give_no_entries(self):
        args = parse("validate", "--kind", "static", "--roots", "Rock")
        self.assertEqual((cli._bodies(args), cli._pairs(args), cli._clip_libraries(args)), ([], [], []))

    def test_an_entry_without_an_equals_sign_is_an_error_and_not_a_guess(self):
        args = parse("validate", "--kind", "x", "--roots", "y", "--body", "no_armature.blend")
        with self.assertRaises(ValueError):
            cli._bodies(args)


class ResultLineTests(unittest.TestCase):
    PRINTED = re.compile(r'print\(f?"((?:ART|HUMAN_BASE)_[A-Z_]+)[ "]')

    def _printed(self, *paths: str) -> set[str]:
        return {token for path in paths for token in self.PRINTED.findall(source_text(path))}

    @staticmethod
    def _sources(*folders: str) -> list[str]:
        return [f"{folder}/{path.name}" if folder else path.name for folder in folders
                for path in sorted((BLENDER_ROOT / folder).glob("*.py"))]

    def test_every_art_line_the_scripts_read_is_printed_by_the_pipeline(self):
        python = self._printed(*self._sources("", "art_pipeline", "human_base"))
        read = set(re.findall(r'"(ART_[A-Z_]+) \*"', all_powershell()))
        self.assertTrue(read)
        self.assertEqual(sorted(read - python), [])

    def test_the_lines_the_human_base_scripts_read_are_printed_by_its_command_line_file(self):
        python = self._printed("human_base_cli.py")
        read = set(re.findall(r'"(HUMAN_BASE_[A-Z_]+)(?: \*)?"',
                              powershell("Sync-HumanBase.ps1", "Sync-AnimationLibrary.ps1", "Import-ArtClip.ps1")))
        self.assertIn("HUMAN_BASE_MOTION", read)
        self.assertEqual(sorted(read - python), [])


class HumanBaseDispatchTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.calls: list[tuple] = []
        record = cls.calls

        def runner(name, result=None):
            def run(*args):
                record.append((name, args))
                return result
            return run

        cls.reports = {"check-mesh": (["mesh ok"], []), "check-seams": (["seams"], ["seam 1", "seam 2"])}
        cls.compared = (["same content: a and b"], [])
        modules = {
            "animate": types.SimpleNamespace(generate=runner("generate")),
            "checks": types.SimpleNamespace(mesh=runner("mesh", cls.reports["check-mesh"]),
                                            seams=runner("seams", cls.reports["check-seams"]),
                                            parts=runner("parts", ([], [])), rig=runner("rig-check", ([], [])),
                                            clips=runner("clips", ([], [])), compare=lambda *args: cls.compared),
            "review": types.SimpleNamespace(review_file=runner("review-file"), render=runner("render", ["a.png", "b.png"])),
            "rig": types.SimpleNamespace(sync=runner("sync"), weights=runner("weights"), rig=runner("rig")),
            "rig_review": types.SimpleNamespace(render=runner("rig-render", ["r.png"]), review_file=runner("rig-review-file", "rr.blend")),
            "retarget": types.SimpleNamespace(import_clip=lambda *args: record.append(("import", args)) or cls.imported),
        }
        cls.cli = load("human_base_cli.py", "main", "_count", "STEPS", "CHECKS", "RENDERS", "IMPORTS", **modules)
        cls.modules = modules

    def setUp(self):
        self.calls.clear()
        type(self).compared = (["same content: a and b"], [])
        type(self).imported = (["Block: 37 frames"], [], {"clip": "Block", "frames": 37})

    def _run(self, *argv: str) -> tuple[int, list[str]]:
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = self.cli.main(list(argv))
        return code, out.getvalue().splitlines()

    def test_no_step_prints_the_steps_and_fails(self):
        code, lines = self._run()
        self.assertEqual(code, 2)
        self.assertIn("Steps:", "\n".join(lines))

    def test_an_unknown_step_is_reported_with_the_marker_the_scripts_read(self):
        self.assertEqual(self._run("polish", "a"), (2, ["HUMAN_BASE_FAILED unknown step polish"]))

    def test_a_step_with_the_wrong_number_of_arguments_is_refused_and_not_run(self):
        self.assertEqual(self._run("sync", "a", "Male"), (2, ["HUMAN_BASE_FAILED sync takes 4 arguments"]))
        self.assertEqual(self._run("check-mesh", "a"), (2, ["HUMAN_BASE_FAILED check-mesh takes 2 arguments"]))
        self.assertEqual(self._run("check-mesh", "a", "b", "c"), (2, ["HUMAN_BASE_FAILED check-mesh takes 2 arguments"]))
        self.assertEqual(self.calls, [])

    def test_a_step_runs_with_its_arguments_and_succeeds(self):
        self.assertEqual(self._run("sync", "a.blend", "Male", "s.json", "out.blend"), (0, []))
        self.assertEqual(self.calls, [("sync", ("a.blend", "Male", "s.json", "out.blend"))])

    def test_clips_are_generated_from_a_comma_separated_list(self):
        self.assertEqual(self._run("generate-clips", "lib.blend", "Humanoid", "Idle,Walk", "s.json")[0], 0)
        self.assertEqual(self.calls, [("generate", ("lib.blend", "Humanoid", ["Idle", "Walk"], "s.json"))])

    def test_a_check_prints_its_lines_and_succeeds_when_nothing_failed(self):
        self.assertEqual(self._run("check-mesh", "m.blend", "f.blend"), (0, ["HUMAN_BASE_REPORT mesh ok"]))

    def test_a_check_that_found_problems_prints_each_and_fails(self):
        code, lines = self._run("check-seams", "m.blend", "f.blend")
        self.assertEqual(code, 1)
        self.assertEqual(lines, ["HUMAN_BASE_REPORT seams", "HUMAN_BASE_FAILED seam 1", "HUMAN_BASE_FAILED seam 2"])

    def test_a_compare_says_whether_the_files_are_the_same_and_does_not_fail_either_way(self):
        self.assertEqual(self._run("compare", "a", "b"), (0, ["HUMAN_BASE_REPORT same content: a and b", "HUMAN_BASE_SAME yes"]))
        type(self).compared = (["different"], ["x"])
        self.assertEqual(self._run("compare", "a", "b"), (0, ["HUMAN_BASE_REPORT different", "HUMAN_BASE_SAME no"]))

    def test_a_review_names_every_sheet_it_wrote(self):
        self.assertEqual(self._run("review", "r.blend", "out", "Prefix"),
                         (0, ["HUMAN_BASE_REPORT review sheet a.png", "HUMAN_BASE_REPORT review sheet b.png"]))
        self.assertEqual(self._run("review-rig", "a.blend", "Male", "out", "Prefix"), (0, ["HUMAN_BASE_REPORT review sheet r.png"]))

    def test_the_rig_review_file_is_named(self):
        self.assertEqual(self._run("rig-review-file", "m", "f", "out.blend"), (0, ["HUMAN_BASE_REPORT review file rr.blend"]))
        self.assertEqual(self._run("rig-review-file", "m"), (2, ["HUMAN_BASE_FAILED rig-review-file takes 3 arguments"]))

    def test_an_imported_clip_prints_its_report_and_its_motion_numbers(self):
        arguments = ("lib.blend", "Humanoid", "src.glb", "map.json", "A+B", "Block", "R", "s.json")
        self.assertEqual(self._run("import-clip", *arguments),
                         (0, ["HUMAN_BASE_REPORT Block: 37 frames", 'HUMAN_BASE_MOTION {"clip":"Block","frames":37}']))
        self.assertEqual(self.calls, [("import", arguments)])

    def test_an_import_that_breaks_a_rule_prints_it_and_fails_without_motion_numbers(self):
        type(self).imported = ([], ["Block: the library has the clip already"], None)
        self.assertEqual(self._run("import-clip", *"abcdefgh"), (1, ["HUMAN_BASE_FAILED Block: the library has the clip already"]))
        self.assertEqual(self._run("import-clip", "a"), (2, ["HUMAN_BASE_FAILED import-clip takes 8 arguments"]))

    def test_the_argument_counts_are_the_ones_the_help_text_lists(self):
        doc = re.search(r'"""(.*?)"""', source_text("human_base_cli.py"), re.DOTALL).group(1)
        steps = doc.split("Steps:")[1].split("\n\n")[0]
        listed = {name: len(re.findall(r"<[^>]+>", rest)) for name, rest in re.findall(r"^    ([a-z-]+) ?(.*)$", steps, re.MULTILINE)}
        tables = {name: count for table in (self.cli.STEPS, self.cli.CHECKS, self.cli.RENDERS, self.cli.IMPORTS)
                  for name, (count, _run) in table.items()}
        tables["rig-review-file"] = 3
        self.assertEqual(listed, tables)

    def test_every_step_the_scripts_start_exists_with_the_arguments_it_takes(self):
        counts = {name: count for table in (self.cli.STEPS, self.cli.CHECKS, self.cli.RENDERS, self.cli.IMPORTS)
                  for name, (count, _run) in table.items()}
        counts["rig-review-file"] = 3
        text = powershell("Sync-HumanBase.ps1")
        started = {}
        for name, arguments in re.findall(r'Invoke-HumanBaseStep (?:"([a-z-]+)"|\$step) @\(([^)]*)\)', text):
            if name:
                started[name] = len(_split_arguments(arguments))
        self.assertTrue(started)
        for name, count in started.items():
            with self.subTest(step=name):
                self.assertEqual(count, counts[name])
        library = powershell("Sync-AnimationLibrary.ps1")
        for name in re.findall(r'@\("(generate-clips|check-clips)"', library):
            self.assertIn(name, counts)
        imported = re.findall(r'@\("(import-clip)",([^)]*\([^)]*\)[^)]*)\)', powershell("Import-ArtClip.ps1"))
        self.assertEqual([(name, len(_split_arguments(arguments))) for name, arguments in imported], [("import-clip", counts["import-clip"])])


def _split_arguments(text: str) -> list[str]:
    """The PowerShell array items at the top level of the text, split at commas outside parentheses."""
    items, depth, current = [], 0, ""
    for char in text:
        depth += char == "("
        depth -= char == ")"
        if char == "," and depth == 0:
            items.append(current.strip())
            current = ""
        else:
            current += char
    if current.strip():
        items.append(current.strip())
    return items


if __name__ == "__main__":
    unittest.main()
