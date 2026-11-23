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
import tempfile
import types
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

from blender_source import BLENDER_ROOT, REPOSITORY_ROOT, load, source_text  # noqa: E402

ART_SCRIPTS = REPOSITORY_ROOT / "tools" / "art"
# What Blender itself takes before the "--" that hands the rest to the script.
BLENDER_OPTIONS = {"--background", "--factory-startup", "--python-use-system-env", "--python-exit-code", "--python",
                   "--python-expr", "--disable-autoexec", "--version"}

scene = load("art_pipeline/scene.py", "ArtPipelineError", "parse_args")
texture = load("art_pipeline/texture.py", "DYE_LEVEL")
content = load("art_pipeline/content.py", "PROOF_EDITS")
cli = load("art_pipeline_cli.py", "_parser", "_bodies", "_pairs", "_clip_libraries", "_held_attachments", texture=texture,
           content=content)


def subcommands() -> dict[str, argparse.ArgumentParser]:
    action = next(a for a in cli._parser()._actions if isinstance(a, argparse._SubParsersAction))
    return dict(action.choices)


def parse(*arguments: str) -> argparse.Namespace:
    with contextlib.redirect_stderr(io.StringIO()):
        return cli._parser().parse_args(list(arguments))


def powershell(*names: str) -> str:
    return "\n".join((ART_SCRIPTS / name).read_text(encoding="utf-8") for name in names)


def all_powershell(*leave_out: str) -> str:
    return "\n".join(path.read_text(encoding="utf-8") for path in sorted(ART_SCRIPTS.glob("*.ps1")) if path.name not in leave_out)


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
        started |= set(re.findall(r'@\("(validate|export|content|content-proof|stage|preview|reference|clips)"', text))
        self.assertEqual(started, set(subcommands()))

    def test_every_option_the_scripts_pass_is_an_option_of_some_command(self):
        known = {option for parser in subcommands().values() for action in parser._actions for option in action.option_strings}
        # Update-UnityArt.ps1 starts Unity and its CLI, not Blender.
        used = set(re.findall(r'"(--[a-z][a-z-]*)"', all_powershell("Update-UnityArt.ps1"))) - BLENDER_OPTIONS
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

    def test_a_stage_takes_the_arguments_of_an_export_and_writes_only_data_to_its_folder(self):
        cache = ["--body-fit", "Male=" + "1" * 64, "--pair-content", "Trousers=" + "3" * 64, "--cache-root", "c"]
        args = parse("stage", "--kind", "garment", "--roots", "Male", "--stage", "rig", "--target", "staging",
                     "--pair", "t.blend=Trousers:tight", *cache)
        self.assertEqual((args.command, args.target, args.stage, args.cache_root), ("stage", "staging", "rig", "c"))
        self.assertEqual(cli._pairs(args), [(os.path.abspath("t.blend"), "Trousers", "tight")])
        with self.assertRaises(SystemExit):
            parse("stage", "--kind", "garment", "--roots", "Male")
        for option in ("--textures", "--opaque-only"):
            with self.subTest(option=option), self.assertRaises(SystemExit):
                parse("stage", "--kind", "texture", "--roots", "Male", "--target", "staging", option, "x")

    def test_a_preview_needs_a_file_to_write_and_draws_its_cells_420_texels_wide(self):
        with self.assertRaises(SystemExit):
            parse("preview")
        args = parse("preview", "--output", "sheet.png", "--layer", "210")
        self.assertEqual((args.output, args.cell_size, args.kind, args.layer), ("sheet.png", 420, "static", 210))

    def test_the_commands_that_run_without_an_open_file_name_their_file(self):
        for command, required in (("new-static", ["--file", "--roots"]),
                                  ("garment-create", ["--file", "--garment", "--manifest", "--cage-manifest", "--cage-content"]),
                                  ("garment-sync", ["--file", "--garment", "--manifest", "--cage-manifest", "--cage-content"]),
                                  ("garment-review-file", ["--file", "--garment", "--output"]),
                                  ("texture-create", ["--file", "--texture", "--garment-file", "--garment", "--label"]),
                                  ("texture-sync", ["--file", "--texture", "--garment-file", "--garment", "--label", "--manifest"]),
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
        args = parse("garment-sync", "--file", "f", "--garment", "G", "--manifest", "m", "--cage-manifest", "c",
                     "--cage-content", "0" * 64, "--weights", "--rebuild")
        self.assertTrue(args.weights and args.rebuild)
        with self.assertRaises(SystemExit):
            parse("garment-review-file", "--file", "f", "--garment", "G", "--output", "o", "--weights")

    def test_a_garment_is_made_and_synced_over_the_templates_of_lower_layers_it_is_worn_over(self):
        for command in ("garment-create", "garment-sync"):
            args = parse(command, "--file", "f", "--garment", "G", "--manifest", "m", "--cage-manifest", "c",
                         "--cage-content", "0" * 64, "--pair", "t.blend=Trousers:tight", "--pair", "b.blend=Boots:mid")
            with self.subTest(command=command):
                self.assertEqual(cli._pairs(args), [(os.path.abspath("t.blend"), "Trousers", "tight"),
                                                    (os.path.abspath("b.blend"), "Boots", "mid")])
                self.assertEqual(cli._pairs(parse(command, "--file", "f", "--garment", "G", "--manifest", "m",
                                                  "--cage-manifest", "c", "--cage-content", "0" * 64)), [])

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


class PairSourceTests(unittest.TestCase):
    """A texture's masks name faces of the templates of lower layers worn under its template, so its
    export records their content, as a template's does, and the checks hold it to them."""

    @staticmethod
    def function(name: str) -> str:
        text = powershell("ArtPipelineCommon.ps1", "Export-ArtAsset.ps1")
        match = re.search(rf"^function {re.escape(name)}\b.*?(?=^function |\Z)", text, re.S | re.M)
        assert match is not None, name
        return match.group(0)

    def test_a_texture_with_masks_records_the_content_of_the_templates_under_its_template_and_stops_when_one_is_not_approved(self):
        plan, record = self.function("New-ArtTextureJob"), self.function("Complete-ArtTextureJob")
        self.assertIn("Get-ArtGarmentPairs $template", plan)
        self.assertIn("pairSources = $(if ($masks.Count -gt 0) { $Job.Context.PairSources } else { @() })", record)
        self.assertIn("$problem = Get-ArtExportGateProblem $current", plan)
        self.assertIn("contentSha256 = Get-ArtExportedContent $current", plan)
        self.assertIn('"--opaque-only", $opaqueReason', plan)
        self.assertIn("templates of lower layers worn under its template: {args.opaque_only}.", source_text("art_pipeline_cli.py"))

    def test_the_records_name_the_content_of_the_other_assets_never_their_exports(self):
        """An export needs the other assets' approvals for their content, not their exports, so the
        assets of one wave may export in any order and write the same records."""
        text = powershell("Export-ArtAsset.ps1")
        self.assertNotIn("Get-ArtExportChangeProblem", text)
        self.assertNotIn(".Export.contentSha256 }", text)
        self.assertIn("garmentContentSha256 = Get-ArtExportedContent $template", self.function("New-ArtTextureJob"))
        self.assertIn("$rigContentSha256 = Get-ArtExportedContent $rig", self.function("New-ArtModelJob"))
        self.assertIn("contentSha256 = [string]$_.ContentSha256", self.function("Get-ArtClipSourceRecords"))
        self.assertIn("Get-ArtExportedContent $_", self.function("Get-ArtPairSourcesProblem"))
        self.assertIn("[string]$_.ContentSha256", self.function("Get-ArtClipSourcesProblem"))
        self.assertIn("(Get-ArtExportedContent $rig) -cne $manifest.Export.rigContentSha256", powershell("Verify-ArtAssets.ps1"))
        self.assertIn("(Get-ArtExportedContent $manifest.Garment) -cne $manifest.Export.garmentContentSha256",
                      powershell("Verify-ArtAssets.ps1"))
        self.assertIn("@($script:ArtExportingTemplates) -ccontains $other.RelativeDirectory", self.function("Get-ArtGarmentPairs"))

    def test_the_manifest_reads_a_texture_s_pair_sources_only_with_masks_and_writes_them(self):
        self.assertIn("export.pairSources must list, for a texture with masks", self.function("Read-ArtTextureManifest"))
        self.assertIn("($maskRecords.Count -gt 0 -or $pairSources.Count -eq 0)", self.function("Read-ArtTextureManifest"))
        self.assertIn('"pairSources": [', self.function("Write-ArtTextureManifest"))

    def test_the_verification_holds_a_texture_with_masks_to_the_current_exports_of_its_template_s_pairs(self):
        self.assertIn('Get-ArtPairSourcesProblem $manifest $manifest.Garment "tools/art/Sync-TextureAsset.ps1"',
                      powershell("Verify-ArtAssets.ps1"))
        self.assertIn("Get-ArtGarmentPairs $Template", self.function("Get-ArtPairSourcesProblem"))

    def test_a_wrong_split_names_its_entry_and_its_key_and_that_toward_and_offset_come_together(self):
        body = self.function("Get-ArtGarmentSplitProblem")
        self.assertIn("recipe.splits entry $($i + 1) (line $([string]$split.line))", body)
        self.assertIn('""toward"" and ""offset"" come together', body)
        self.assertIn("Get-ArtGarmentSplitProblem $Json.recipe $cage", powershell("ArtPipelineCommon.ps1"))


class ContentRecordRaceTests(unittest.TestCase):
    """A content record pairs the content Blender read with the hash of the file it read, so every
    script takes the hash before the run whose content it records, and an export checks the content
    it exported before it writes its record."""

    def test_every_record_taken_from_a_run_comes_with_the_hash_before_that_run(self):
        calls = re.findall(r"Update-ArtContentRecord (\$\S+) (\$\S+)(?: (\$\S+))?(?: (\$\S+))?", all_powershell())
        with_results = [call for call in calls if call[2]]
        self.assertTrue(with_results)
        for call in with_results:
            with self.subTest(call=" ".join(call)):
                self.assertTrue(call[3], "a run's output needs the file's hash before the run")

    def test_the_record_refuses_a_file_saved_while_it_was_read(self):
        body = PairSourceTests.function("Update-ArtContentRecord")
        self.assertIn('Assert-ArtFileUnchanged $Manifest $SourceSha256Before "was saved while it was read"', body)
        self.assertIn('Assert-ArtFileUnchanged $Manifest $sourceSha256 "was saved while it was read"', body)

    def test_every_export_run_checks_the_content_it_exported_before_its_record(self):
        text = powershell("Export-ArtAsset.ps1")
        runs = re.findall(r'New-ArtExportJob \$(\w+) \(\s*@\("export"\)', text)
        checks = re.findall(r"Assert-ArtExportedContent \$(\w+) \$results \$Job\.SourceSha256", text)
        self.assertEqual(len(runs), 4)
        self.assertEqual(runs, checks)
        self.assertIn("SourceSha256 = Get-ArtFileSha256 $Manifest.SourcePath", PairSourceTests.function("New-ArtExportJob"))
        self.assertIn("changed while it was exported", PairSourceTests.function("Assert-ArtExportedContent"))

    def test_an_approval_binds_to_the_content_its_own_validation_reported(self):
        text = powershell("Approve-ArtAsset.ps1")
        self.assertIn("contentSha256 = [string]$reported.stages.$Stage", text)
        self.assertIn("changed while it was approved", text)

    def test_the_cage_record_is_made_current_before_its_gate(self):
        for name in ("Approve-ArtAsset.ps1", "Export-ArtAsset.ps1", "Sync-GarmentAsset.ps1"):
            text = powershell(name)
            with self.subTest(script=name):
                update = text.index("Update-ArtContentRecord $" + ("Blender" if name == "Export-ArtAsset.ps1" else "blender")
                                    + " $" + ("Manifest" if name == "Export-ArtAsset.ps1" else "manifest") + ".Cage")
                self.assertLess(update, text.index("Get-ArtCageGateProblem"))


class CageContentTests(unittest.TestCase):
    """Garment templates and paint layers record the content fingerprint of the garment cage, which the
    PowerShell scripts pass from the cage's record made current, so a cage file saved again with the
    same content leaves them current. Python never hashes the cage's file."""

    def test_every_command_given_the_cage_of_a_template_gets_its_content_from_the_cage_s_record(self):
        text = all_powershell()
        manifests = re.findall(r'"--cage-manifest", \$\w+\.Cage\.ManifestPath(, "--cage-content", \(Get-ArtCageContent \$\w+\.Cage\))?',
                               text)
        self.assertEqual(len(manifests), 3)
        self.assertTrue(all(manifests), "every --cage-manifest comes with --cage-content")
        self.assertIn('"--cage", $cage.SourcePath, "--cage-content", (Get-ArtCageContent $cage)', powershell("Sync-PaintAsset.ps1"))

    def test_the_cage_s_record_is_made_current_before_its_content_is_passed(self):
        for name, update in (("Sync-GarmentAsset.ps1", "Update-ArtContentRecord $blender $manifest.Cage"),
                             ("New-ArtAsset.ps1", "Update-ArtContentRecord $blender $manifest.Cage"),
                             ("Sync-PaintAsset.ps1", "Update-ArtContentRecord $blender $cage")):
            text = powershell(name)
            with self.subTest(script=name):
                self.assertLess(text.index(update), text.index('"--cage-content"'))
        self.assertIn("Update-ArtContentRecord $Blender $Manifest.Cage", PairSourceTests.function("Update-ArtContentRecord"))
        body = PairSourceTests.function("Get-ArtCageContent")
        self.assertIn("Get-ArtContentProblem $Cage", body)
        self.assertIn('$Cage.Content.stages["model"]', body)

    def test_the_records_name_the_cage_s_content_and_an_old_record_of_its_file_is_rejected(self):
        text = all_powershell()
        self.assertIn('"cageSourceSha256"', PairSourceTests.function("Get-ArtOldExportProblem"))
        self.assertEqual(text.count("cageSourceSha256"), 1)
        self.assertIn("cageContentSha256 = Get-ArtCageContent $Manifest.Cage", powershell("Export-ArtAsset.ps1"))
        self.assertIn("Get-ArtGarmentFitProblems $Manifest", PairSourceTests.function("Get-ArtGarmentDependencyProblems"))
        self.assertIn("was fitted to other content of its cage", PairSourceTests.function("Get-ArtGarmentFitProblems"))
        self.assertIn("was moved through other content of the garment cage", PairSourceTests.function("Get-ArtPaintDependencyProblems"))

    def test_the_files_keep_the_cage_s_content_and_never_its_file_s_hash(self):
        for path in ("art_pipeline/garment.py", "art_pipeline/paint.py"):
            source = source_text(path)
            with self.subTest(path=path):
                self.assertIn('CAGE_CONTENT = "cage_content"', source)
                self.assertNotIn("cage_sha256", source)
        self.assertIn("cageContentSha256=paint.cage_record()", source_text("art_pipeline_cli.py"))

    def test_a_paint_sync_takes_the_cage_with_its_content(self):
        args = parse("paint-sync", "--file", "f", "--paint", "Tattoo", "--part", "Body", "--cage", "c.blend",
                     "--cage-content", "0" * 64)
        self.assertEqual((args.cage, args.cage_content), ("c.blend", "0" * 64))
        args = parse("validate", "--kind", "garment", "--roots", "Male", "--cage-content", "0" * 64)
        self.assertEqual(args.cage_content, "0" * 64)


class CheckCacheTests(unittest.TestCase):
    """The checks of a garment template or a masked texture go through the cache of the checks, keyed by
    the fingerprints the PowerShell scripts pass, which -NoCache turns off, and every script that runs
    them says how long it took and how many units came from the cache."""

    SCRIPTS = ("Export-ArtAsset.ps1", "Approve-ArtAsset.ps1", "Sync-GarmentAsset.ps1", "Sync-TextureAsset.ps1")

    def test_validate_export_and_preview_take_the_fingerprints_and_the_switch(self):
        cache = ["--no-cache", "--body-fit", "Male=" + "1" * 64, "--clip-content", "h.blend=" + "2" * 64, "--pair-content",
                 "Trousers=" + "3" * 64, "--texture-content", "4" * 64]
        for command in ("validate", "export", "preview"):
            required = ["--output", "o"] if command == "preview" else ["--kind", "garment", "--roots", "Male"]
            args = parse(command, *required, *cache, "--cage-content", "5" * 64)
            with self.subTest(command=command):
                self.assertTrue(args.no_cache)
                self.assertEqual((args.body_fit, args.clip_content, args.pair_content), ([cache[2]], [cache[4]], [cache[6]]))
                self.assertEqual((args.texture_content, args.cage_content), ("4" * 64, "5" * 64))
        args = parse("validate", "--kind", "garment", "--roots", "Male")
        self.assertEqual((args.no_cache, args.body_fit, args.clip_content, args.pair_content), (False, [], [], []))

    def test_every_script_that_runs_the_checks_passes_the_cache_arguments_and_has_the_switch(self):
        for name in self.SCRIPTS + ("Render-ArtPreview.ps1",):
            text = powershell(name)
            with self.subTest(script=name):
                self.assertIn("[switch]$NoCache", text)
                self.assertIn("-NoCache:$NoCache", text)
                self.assertIn("Write-Host (Format-ArtElapsed $", text)
                if name != "Render-ArtPreview.ps1":
                    self.assertIn("Get-ArtCacheArguments $", text)

    def test_every_review_sheet_of_a_template_or_a_texture_is_rendered_by_one_function_with_the_cache(self):
        for function in ("Invoke-ArtGarmentSheet", "Invoke-ArtTextureSheet"):
            self.assertIn("(Get-ArtCacheArguments $Blender $Manifest -NoCache:$NoCache -Preview))", PairSourceTests.function(function))
        for name, functions in (("Render-ArtPreview.ps1", ("Invoke-ArtGarmentSheet", "Invoke-ArtTextureSheet")),
                                ("Sync-GarmentAsset.ps1", ("Invoke-ArtGarmentSheet",)),
                                ("Sync-TextureAsset.ps1", ("Invoke-ArtTextureSheet",))):
            for function in functions:
                with self.subTest(script=name, function=function):
                    self.assertIn(f"@({function} $blender $manifest ", powershell(name))
        self.assertEqual(all_powershell().count('"--texture-file"'), 1)
        self.assertEqual(all_powershell().count('"--kind", "garment", "--garment"'), 1)

    def test_the_fingerprints_come_from_content_records_made_current_first(self):
        body = PairSourceTests.function("Get-ArtCacheArguments")
        for update in ("Update-ArtContentRecord $Blender $bodyManifest", "Update-ArtContentRecord $Blender $current",
                       "Update-ArtContentRecord $Blender $template.Cage", "Update-ArtContentRecord $Blender $Manifest"):
            self.assertIn(update, body)
        self.assertIn("Get-ArtExportChangeProblem $bodyManifest", body)
        self.assertIn('"--no-cache"', body)

    def test_the_cache_never_stops_a_command_and_says_what_it_leaves_out(self):
        body = PairSourceTests.function("Get-ArtCacheArguments")
        tries, catches = len(re.findall(r"\btry \{", body)), re.findall(r"catch \{\s*(?:\$Pairs = @\(\)\s*)?(\S+)", body)
        self.assertGreaterEqual(tries, 6)
        self.assertEqual((len(catches), set(catches)), (tries, {"Write-ArtUncached"}))
        for line in body.splitlines():
            if "Update-ArtContentRecord" in line or "Read-ArtAssetManifest" in line or "Get-ArtCageContent" in line:
                with self.subTest(line=line.strip()):
                    self.assertTrue(line.startswith(" " * 12), "each asset's part runs inside its own try")

    def test_the_timing_line_counts_the_units_of_every_run_and_says_each_note_once(self):
        body, counts = PairSourceTests.function("Format-ArtElapsed"), PairSourceTests.function("Get-ArtCacheCounts")
        self.assertIn("Get-ArtCacheCounts $Results", body)
        self.assertIn('"ART_CACHE *"', counts)
        self.assertIn('ToString("0.0", [System.Globalization.CultureInfo]::InvariantCulture)', body)
        self.assertIn("if (-not $notes.Contains([string]$note))", counts)


class BlenderJobTests(unittest.TestCase):
    """Several art_pipeline_cli.py runs at once: each Blender gets the environment of Invoke-ArtBlender
    and the same handling of its result lines, its output never blocks it, the export plans a wave
    before it starts, writes each asset.json itself, and prints each asset's lines together."""

    def test_every_run_gets_the_environment_and_the_result_handling_of_invoke_art_blender(self):
        start, done = PairSourceTests.function("Start-ArtBlenderJob"), PairSourceTests.function("Complete-ArtBlenderJob")
        self.assertIn("$script:ArtBlenderEnvironment", start)
        self.assertIn("$script:ArtBlenderEnvironment", PairSourceTests.function("Invoke-ArtBlender"))
        self.assertIn("Get-ArtBlenderArguments $Job.Manifest $Job.Arguments", start)
        self.assertIn("ReadToEndAsync()", start)
        self.assertIn("Get-ArtBlenderResults $Job.Manifest", done)
        self.assertIn("Get-ArtBlenderResults $Manifest $output", PairSourceTests.function("Invoke-ArtBlender"))
        self.assertIn('PYTHONHASHSEED = "0"', powershell("ArtPipelineCommon.ps1"))

    def test_the_export_plans_a_wave_before_it_runs_and_writes_each_record_when_its_run_ended(self):
        text = powershell("Export-ArtAsset.ps1")
        self.assertIn("$script:ArtExportingTemplates = @($manifests", text)
        self.assertLess(text.index("New-ArtKindJob $blender $manifest"), text.index("Invoke-ArtBlenderJobs $blender $ordered $Jobs $onDone"))
        self.assertIn("Complete-ArtKindJob $Job", text)
        self.assertIn("$script:ArtLineSink = $Job.Lines", text)
        self.assertIn('Write-ArtLine "The checks that read', PairSourceTests.function("Write-ArtUncached"))
        self.assertIn("[System.IO.File]::Replace($temporary, $Path, [NullString]::Value)", PairSourceTests.function("Write-ArtManifestText"))
        self.assertEqual(powershell("ArtPipelineCommon.ps1").count("WriteAllText($Manifest.ManifestPath"), 0)

    def test_the_build_never_records_an_approval_and_runs_only_the_pipeline_s_own_scripts(self):
        text = powershell("Build-Art.ps1")
        self.assertNotIn('"Approve-ArtAsset.ps1")', text)
        self.assertNotIn(".Approvals[", text)
        self.assertIn(".\\tools\\art\\Approve-ArtAsset.ps1 -Asset $($manifest.RelativeDirectory) -Stage $stage", text)
        started = set(re.findall(r'"([A-Za-z-]+\.ps1)"', text))
        self.assertTrue({"Sync-CageAsset.ps1", "Sync-GarmentAsset.ps1", "Sync-TextureAsset.ps1", "Sync-AnimationLibrary.ps1",
                         "Sync-PaintAsset.ps1", "Export-ArtAsset.ps1", "Update-UnityArt.ps1", "Verify-ArtAssets.ps1"} <= started)
        for name in started:
            with self.subTest(script=name):
                self.assertTrue((ART_SCRIPTS / name).is_file())

    def test_the_build_verifies_with_the_waiting_assets_lists_the_approvals_last_and_follows_the_unity_marker(self):
        build, verify = powershell("Build-Art.ps1"), powershell("Verify-ArtAssets.ps1")
        self.assertIn('"Verify-ArtAssets.ps1") -Waiting @($entries | Where-Object { $_.Need -ne "nothing" }', build)
        self.assertLess(build.index("-Waiting @($entries"), build.index("$approvals = if ($entries.Count -gt 0) { Write-ArtApprovals $entries }"))
        self.assertEqual(build.count("Write-ArtApprovals $entries"), 2)
        self.assertIn("[string[]]$Waiting = @()", verify)
        for duplicate in ("is also the target of", "is also a material of", "is also a texture of", "every model comes from", "every texture comes from"):
            line = next(line for line in verify.splitlines() if duplicate in line)
            with self.subTest(problem=duplicate):
                self.assertIn("$problems.Add(", line)
        marker = 'artifacts/art-build/unity/pending'
        self.assertIn(marker, build)
        self.assertIn("Test-Path -LiteralPath $unityPendingPath", build)
        self.assertIn('Join-Path (Get-ArtBuildFolder "unity") "pending"', powershell("Export-ArtAsset.ps1"))
        self.assertIn("Remove-Item -LiteralPath $pending", powershell("Update-UnityArt.ps1"))
        self.assertIn("Test-ArtSyncRecordHolds $manifest $manifests $syncRecords", build)
        self.assertIn("Save-ArtSyncRecords $records", build)
        self.assertIn("Synced by this run", build)

    def test_the_build_syncs_a_template_whose_template_of_a_lower_layer_has_other_content(self):
        """A template's sync keeps its cloth clear of the templates of lower layers, which the sync
        script passes it, so other content of one of them is a reason to sync, not only to export."""
        build = powershell("Build-Art.ps1")
        reasons = build[build.index("function Get-ArtSyncReasons"):build.index("function Get-ArtExportReasons")]
        self.assertIn("$pairProblem = Get-ArtPairSourcesProblem $Manifest", reasons)
        self.assertIn("(Get-ArtGarmentPairArguments $manifest)", powershell("Sync-GarmentAsset.ps1"))

    def test_the_build_exports_a_new_template_of_a_lower_layer_before_it_syncs_the_templates_over_it(self):
        """The sync script is given only the exported templates of lower layers, so the build exports
        one that has no export yet first, and a sync that ran without it is not taken as having
        followed it."""
        build = powershell("Build-Art.ps1")
        self.assertIn("foreach ($pair in @(Get-ArtGarmentPairs $Manifest)) { Add-Followed $pair.RelativeDirectory $false $true }", build)
        self.assertIn('($_.SeenWhenExported -and $needs[$_.Asset] -eq "export" -and $null -eq $manifests[$_.Asset].Export)', build)
        fingerprints = build[build.index("function Get-ArtFollowedFingerprints"):build.index("function Test-ArtSyncRecordHolds")]
        self.assertIn("elseif ($followed.SeenWhenExported -and $null -eq $other.Export) {", fingerprints)
        self.assertIn("$null -ne $other.Export -or @($script:ArtExportingTemplates) -ccontains $other.RelativeDirectory",
                      PairSourceTests.function("Get-ArtGarmentPairs"))

    def test_the_unity_follow_up_runs_one_method_in_the_editor_or_headless(self):
        text = powershell("Update-UnityArt.ps1")
        self.assertIn('$method = "Mmorpg.Client.Editor.ArtExportFollowUp.ApplyFromCommandLine"', text)
        self.assertIn("Mmorpg.Client.Editor.ArtExportFollowUp.Apply().Lines()", text)
        self.assertIn('"-executeMethod", $method', text)
        self.assertNotIn("unity status", text.replace("'unity status' would", ""))

    def test_a_process_argument_is_quoted_as_the_c_runtime_reads_it_back(self):
        body = PairSourceTests.function("ConvertTo-ArtProcessArgument")
        self.assertIn("2 * $slashes + 1", body)
        self.assertIn("2 * $slashes)", body)
        self.assertIn("ConvertTo-ArtProcessArgument $_", PairSourceTests.function("Start-ArtBlenderJob"))


class StageTests(unittest.TestCase):
    """The stage command writes the data an export of the same content writes, with the export's own record
    and name, by a rename, and nothing else: no model, no image, no asset.json."""

    @staticmethod
    def _function(name: str) -> str:
        text = source_text("art_pipeline_cli.py")
        start = text.index(f"\ndef {name}(")
        end = text.find("\ndef ", start + 1)
        return text[start:end if end >= 0 else len(text)]

    def test_a_staged_file_has_the_bytes_the_export_writes_and_comes_by_a_rename(self):
        records = load("art_pipeline/coverage.py", "write_record")
        staged = load("art_pipeline_cli.py", "_write_by_rename", coverage=types.SimpleNamespace(write_record=records.write_record))
        data = {"schemaVersion": 3, "garment": "Shirt", "body": "Male", "covered": list(range(20)), "pullInDistances": [0.0015, 0.002]}
        with tempfile.TemporaryDirectory() as folder:
            exported, target = os.path.join(folder, "exported.json"), os.path.join(folder, "staging", "Shirt_Male.json")
            records.write_record(exported, data)
            staged._write_by_rename(target, {"old": 1})
            staged._write_by_rename(target, data)
            self.assertEqual(pathlib.Path(target).read_bytes(), pathlib.Path(exported).read_bytes())
            self.assertEqual(sorted(os.listdir(os.path.dirname(target))), ["Shirt_Male.json"])
        self.assertIn("os.replace(temporary, path)", self._function("_write_by_rename"))

    def test_a_template_s_stage_writes_its_data_before_any_model_and_a_texture_s_only_its_masks(self):
        garment = self._function("_garment")
        stage = garment[garment.index('if args.command == "stage":'):garment.index("continue", garment.index('if args.command == "stage":'))]
        self.assertIn("_write_by_rename(data, coverage.record(args.garment, own[label].coverage))", stage)
        self.assertNotIn("export_fbx", stage)
        self.assertLess(garment.index("_leave_bones("), garment.index('if args.command == "stage":'))
        self.assertIn('if args.command == "export":\n        for record in material_records', garment)
        texture_run = self._function("_texture")
        self.assertIn('texture.exported_image(args.texture) if args.command == "export" else None', texture_run)
        self.assertIn("_write_by_rename(data, texture.mask_record(args.texture, args.garment, result))", texture_run)
        self.assertIn('print(f"ART_STAGE_OPAQUE {args.texture}")', texture_run)


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
            "retarget": types.SimpleNamespace(import_clip=lambda *args: record.append(("import", args)) or cls.imported,
                                              refit=lambda *args: record.append(("refit", args)) or cls.refitted),
        }
        cls.cli = load("human_base_cli.py", "main", "_count", "STEPS", "CHECKS", "RENDERS", "IMPORTS", **modules)
        cls.modules = modules

    def setUp(self):
        self.calls.clear()
        type(self).compared = (["same content: a and b"], [])
        type(self).imported = (["Block: 37 frames"], [], {"clip": "Block", "frames": 37})
        type(self).refitted = (["Humanoid: 2 clips moved to the new rig"], [], None)

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
        self.assertEqual(self._run("review-rig", "a.blend", "Male", "s.json", "out", "Prefix"), (0, ["HUMAN_BASE_REPORT review sheet r.png"]))

    def test_the_rig_review_file_is_named(self):
        self.assertEqual(self._run("rig-review-file", "m", "f", "s.json", "out.blend"), (0, ["HUMAN_BASE_REPORT review file rr.blend"]))
        self.assertEqual(self._run("rig-review-file", "m"), (2, ["HUMAN_BASE_FAILED rig-review-file takes 4 arguments"]))

    def test_refitted_clips_print_their_report_and_no_motion_numbers(self):
        arguments = ("lib.blend", "Humanoid", "before.blend", "s.json", "Idle,Walk")
        self.assertEqual(self._run("refit-clips", *arguments), (0, ["HUMAN_BASE_REPORT Humanoid: 2 clips moved to the new rig"]))
        self.assertEqual(self.calls, [("refit", arguments)])
        type(self).refitted = ([], ["Grip: the clip has no slot to play on the armature."], None)
        self.assertEqual(self._run("refit-clips", *arguments),
                         (1, ["HUMAN_BASE_FAILED Grip: the clip has no slot to play on the armature."]))
        self.assertEqual(self._run("refit-clips", "lib.blend"), (2, ["HUMAN_BASE_FAILED refit-clips takes 5 arguments"]))

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
        tables["rig-review-file"] = 4
        self.assertEqual(listed, tables)

    def test_every_step_the_scripts_start_exists_with_the_arguments_it_takes(self):
        counts = {name: count for table in (self.cli.STEPS, self.cli.CHECKS, self.cli.RENDERS, self.cli.IMPORTS)
                  for name, (count, _run) in table.items()}
        counts["rig-review-file"] = 4
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
