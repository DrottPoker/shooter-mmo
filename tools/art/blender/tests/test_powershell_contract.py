"""The tables that tools/art/ArtPipelineCommon.ps1 keeps and the Blender code keeps again must agree:
the PowerShell scripts check an asset.json, the Blender scripts then work with it. A table that one
side changes alone makes an asset pass one check and fail the other. Runs without Blender: see
blender_source.py."""

import json
import pathlib
import re
import sys
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

from blender_source import REPOSITORY_ROOT, load, powershell_list, powershell_value, repository_text  # noqa: E402

COMMON = "ArtPipelineCommon.ps1"
scene = load("art_pipeline/scene.py", "ArtPipelineError")
bodies = load("art_pipeline/bodies.py", "ZONES")
cage = load("art_pipeline/cage.py", "REGIONS", "SIDED", "read_manifest", ArtPipelineError=scene.ArtPipelineError)
hang = load("art_pipeline/hang.py", "PREFIXES", "SIDES")
garment = load("art_pipeline/garment.py", "BANDS", "SLOTS", "image_name", "material_name", cage=cage)
validate = load("art_pipeline/validate.py", "STAGES", "NAME_PATTERN", "OBJECT_TYPES")
export = load("art_pipeline/export.py", "ANIMATED_KINDS", "OBJECT_TYPES")
paint = load("art_pipeline/paint.py", "CROP_BLOCK", bodies=bodies, cage=cage)
texture = load("art_pipeline/texture.py", "SIZES", "MAX_DYES", garment=garment)
content = load("art_pipeline/content_core.py", "VERSION", "STAGES", "PARTS", "PART_PATTERN")


def powershell_table(name: str) -> dict[str, list[str]]:
    """The lists of `$script:<name> = @{ kind = @("a", "b") }` in ArtPipelineCommon.ps1 by kind."""
    block = re.search(r"\$script:" + name + r"\s*=\s*@\{(.*?)\n\}", repository_text("tools/art/" + COMMON), re.DOTALL)
    return {kind: re.findall(r'"([^"]+)"', items) for kind, items in re.findall(r"(\w+)\s*=\s*@\(([^)]*)\)", block.group(1))}


def review_stages() -> dict[str, list[str]]:
    return powershell_table("ArtReviewStages")


class PowerShellTablesAgreeTests(unittest.TestCase):
    def test_the_review_stages_of_a_kind_that_blender_checks(self):
        stages = review_stages()
        for kind, expected in validate.STAGES.items():
            with self.subTest(kind=kind):
                self.assertEqual(stages[kind], list(expected))

    def test_kinds_with_an_armature_and_clips(self):
        self.assertEqual(set(powershell_list(COMMON, "ArtAnimatedKinds")), set(export.ANIMATED_KINDS))

    def test_the_name_every_object_and_asset_must_have(self):
        self.assertEqual(powershell_value(COMMON, "ArtNamePattern"), validate.NAME_PATTERN.pattern)

    def test_the_regions_of_the_cage_and_the_sided_ones(self):
        self.assertEqual(powershell_list(COMMON, "ArtCageRegions"), list(cage.REGIONS))
        self.assertEqual(powershell_list(COMMON, "ArtCageSidedRegions"), list(cage.SIDED))

    def test_the_figure_scale_of_the_real_cage_is_inside_the_range_the_script_allows(self):
        low, high = (float(v) for v in powershell_list(COMMON, "ArtCageFigureRange"))
        figure, _lines = cage.read_manifest(str(REPOSITORY_ROOT / "art" / "Cages" / "Humanoid" / "asset.json"))
        self.assertTrue(low <= figure <= high)

    def test_the_slots_and_bands_of_a_garment(self):
        self.assertEqual(powershell_list(COMMON, "ArtGarmentSlots"), list(garment.SLOTS))
        self.assertEqual(powershell_list(COMMON, "ArtGarmentBands"), list(garment.BANDS))

    def test_the_loose_parts_a_recipe_may_hang(self):
        self.assertEqual(powershell_list(COMMON, "ArtGarmentHangSides"), list(hang.SIDES))
        self.assertEqual(powershell_list(COMMON, "ArtGarmentHangChains"), list(hang.PREFIXES))

    def test_the_cloth_profile_of_a_hung_chain_is_a_cloth_profile(self):
        text = repository_text("tools/art/" + COMMON)
        profiles = dict(re.findall(r"(\w+)\s*=\s*\"(\w+)\"", re.search(r"\$script:ArtGarmentHangCloth\s*=\s*@\{([^}]*)\}", text).group(1)))
        self.assertEqual(set(profiles), set(hang.PREFIXES))
        self.assertTrue(set(profiles.values()) <= set(powershell_list(COMMON, "ArtGarmentCloth")))

    def test_the_paint_crop_block(self):
        self.assertEqual(int(powershell_value(COMMON, "ArtPaintCropBlock")), paint.CROP_BLOCK)

    def test_the_texture_sizes(self):
        self.assertEqual([int(v) for v in powershell_list(COMMON, "ArtTextureSizes")], list(texture.SIZES))

    def test_the_dye_channels_a_template_may_ask_for(self):
        self.assertRegex(repository_text("tools/art/" + COMMON), rf"\$Json\.dyes -gt {texture.MAX_DYES}\b")

    def test_every_asset_is_of_a_kind_with_review_stages(self):
        stages = review_stages()
        for manifest in sorted((REPOSITORY_ROOT / "art").glob("*/*/asset.json")):
            data = json.loads(manifest.read_text(encoding="utf-8"))
            with self.subTest(asset=manifest.parent.name):
                self.assertIn(data["kind"], stages)
                self.assertTrue(set(data["approvals"]) <= set(stages[data["kind"]]))

    def test_the_version_of_the_content_fingerprints(self):
        self.assertEqual(int(powershell_value(COMMON, "ArtContentVersion")), content.VERSION)

    def test_the_review_stages_of_every_kind_that_has_content(self):
        self.assertEqual(review_stages(), {kind: list(stages) for kind, stages in content.STAGES.items()})

    def test_the_parts_of_every_stage(self):
        expected = {kind: sorted(f"{stage}/{part}" for stage, parts in stages.items() for part in parts)
                    for kind, stages in content.PARTS.items()}
        self.assertEqual({kind: sorted(parts) for kind, parts in powershell_table("ArtContentParts").items()}, expected)

    def test_the_name_of_a_part(self):
        self.assertEqual(powershell_value(COMMON, "ArtPartPattern"), content.PART_PATTERN.pattern)

    def test_every_content_record_has_the_parts_and_stages_of_its_kind(self):
        for manifest in sorted((REPOSITORY_ROOT / "art").glob("*/*/asset.json")):
            data = json.loads(manifest.read_text(encoding="utf-8"))
            record = data.get("content")
            if record is None:
                continue
            with self.subTest(asset=manifest.parent.name):
                stages = list(record["stages"])
                self.assertEqual(stages, list(content.STAGES[data["kind"]][:len(stages)]))
                for part in record["parts"]:
                    names = [name for listed in content.PARTS[data["kind"]].values() for name in listed]
                    self.assertTrue(part in names or any(n.endswith(":") and part.startswith(n) for n in names), part)
                for approval in data["approvals"].values():
                    self.assertEqual(set(approval), {"date", "contentSha256"})


if __name__ == "__main__":
    unittest.main()
