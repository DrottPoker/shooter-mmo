"""The cache of the garment checks and the checks that need no pose: the keys of the units, which
change with everything a unit reads and with no clip, the entries, which read back as exactly what was
written, the count of poses a check with nothing to follow reports without stepping, and the order and
the shortcuts of coverage.checks, run here with stand-ins for what needs Blender. Runs without Blender:
see blender_source.py. It needs numpy."""

import json
import pathlib
import sys
import tempfile
import threading
import types
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

from blender_source import REPOSITORY_ROOT, load  # noqa: E402

manifest_io = load("art_pipeline/manifest_io.py", "read_json")
cache = load("art_pipeline/check_cache.py", "SCHEMA", "FIELDS", "Store", "Units", "key", "own_inputs", "pair_inputs",
             "base_inputs", "code_files", "code_fingerprint", "manifest_fields", "is_sha", "source_problem", numpy=True,
             manifest_io=manifest_io)
np = __import__("numpy")
garment = load("art_pipeline/garment.py", "version_name")
poses = load("art_pipeline/poses.py", "POSES")
REAL = ("Coverage", "Motion", "Pair", "OwnCheck", "UnderCheck", "own_entry", "own_from", "under_entry", "under_from",
        "skipped_poses", "pair_summary", "motion_summary", "REST", "TEST_POSES")
coverage = load("art_pipeline/coverage.py", *REAL, garment=garment, poses=poses, numpy=True)


def sha(text: str) -> str:
    return __import__("hashlib").sha256(text.encode()).hexdigest()


def base(**changed) -> dict:
    """The inputs every unit of a run reads, with any of them changed."""
    values = {"code": sha("code"), "garment": {"name": "Shirt", "content": sha("shirt"), "band": "tight", "covers": True,
                                               "fields": {"band": "tight", "recipe": {"ease": 0.004}, "layer": 300}},
              "cage": sha("cage"), "texture": None}
    values.update(changed)
    return cache.base_inputs(values["code"], values["garment"], values["cage"], values["texture"])


INNER = {"name": "Trousers", "content": sha("trousers"), "band": "tight", "fields": {"band": "tight", "layer": 200}}
FITS = [("Male", sha("male")), ("Female", sha("female"))]


def own_key(inputs_base=None, fit=sha("male"), stage="rig") -> str:
    return cache.key(cache.own_inputs(inputs_base if inputs_base is not None else base(), "Male", fit, stage))


def pair_key(inputs_base=None, fits=FITS, inner=INNER, stage="rig") -> str:
    return cache.key(cache.pair_inputs(inputs_base if inputs_base is not None else base(), fits, inner, stage))


class KeyTests(unittest.TestCase):
    """A key names everything its unit reads, so a change of any of it misses the cache."""

    def test_the_same_inputs_give_the_same_key(self):
        self.assertEqual(own_key(), own_key())
        self.assertEqual(pair_key(), pair_key())
        self.assertNotEqual(own_key(), pair_key())

    def test_a_change_of_the_outer_template_s_content_misses(self):
        changed = base(garment={**base()["garment"], "content": sha("shirt edited")})
        self.assertNotEqual(own_key(changed), own_key())
        self.assertNotEqual(pair_key(changed), pair_key())

    def test_a_change_of_the_inner_template_s_content_misses(self):
        self.assertNotEqual(pair_key(inner={**INNER, "content": sha("trousers edited")}), pair_key())

    def test_a_change_of_a_body_s_fit_misses(self):
        self.assertNotEqual(own_key(fit=sha("male refitted")), own_key())
        self.assertNotEqual(pair_key(fits=[FITS[0], ("Female", sha("female refitted"))]), pair_key())

    def test_no_unit_reads_a_clip_so_a_changed_clip_misses_no_entry(self):
        """The checks pose the rest pose and the test poses only, so neither a unit's inputs nor what
        every unit of a run reads names an animation library or its clips."""
        self.assertEqual(sorted(base()), ["cage", "code", "garment", "texture"])
        for stage in ("model", "rig"):
            with self.subTest(stage=stage):
                self.assertNotIn("clips", cache.own_inputs(base(), "Male", sha("male"), stage))
                self.assertNotIn("clips", cache.pair_inputs(base(), FITS, INNER, stage))

    def test_a_change_of_band_or_recipe_misses(self):
        template = base()["garment"]
        for changed in ({**template, "band": "mid"}, {**template, "fields": {**template["fields"], "recipe": {"ease": 0.005}}},
                        {**template, "covers": False}):
            self.assertNotEqual(own_key(base(garment=changed)), own_key())
            self.assertNotEqual(pair_key(base(garment=changed)), pair_key())
        self.assertNotEqual(pair_key(inner={**INNER, "band": "mid"}), pair_key())
        self.assertNotEqual(pair_key(inner={**INNER, "fields": {**INNER["fields"], "recipe": {"ease": 0.002}}}), pair_key())

    def test_a_change_of_the_cage_the_code_the_stage_or_a_texture_misses(self):
        for changed in (base(cage=sha("cage edited")), base(code=sha("code edited")),
                        base(texture={"name": "LinenShirt", "image": sha("linen")})):
            self.assertNotEqual(own_key(changed), own_key())
            self.assertNotEqual(pair_key(changed), pair_key())
        self.assertNotEqual(own_key(stage="model"), own_key())
        self.assertNotEqual(own_key(base(texture={"name": "A", "image": sha("a")})), own_key(base(texture={"name": "A", "image": sha("b")})))

    def test_a_missing_fingerprint_leaves_the_unit_without_a_key(self):
        self.assertIsNone(base(code=None))
        self.assertIsNone(base(cage="not a fingerprint"))
        self.assertIsNone(base(garment={**base()["garment"], "content": None}))
        self.assertIsNone(base(texture={"name": "LinenShirt", "image": None}))
        self.assertIsNone(cache.own_inputs(base(), "Male", None, "rig"))
        self.assertIsNone(cache.pair_inputs(base(), [FITS[0], ("Female", None)], INNER, "rig"))
        self.assertIsNone(cache.pair_inputs(base(), FITS, {**INNER, "content": None}, "rig"))
        self.assertIsNone(cache.pair_inputs(base(), FITS, None, "rig"))

    def test_the_rig_stage_has_a_key_with_the_fingerprints_of_the_model_stage(self):
        for stage in ("model", "rig"):
            with self.subTest(stage=stage):
                self.assertIsNotNone(cache.own_inputs(base(), "Male", sha("male"), stage))
                self.assertIsNotNone(cache.pair_inputs(base(), FITS, INNER, stage))

    def test_the_inputs_name_what_the_unit_reads(self):
        inputs = cache.pair_inputs(base(), FITS, INNER, "rig")
        self.assertEqual(sorted(inputs), ["bodies", "cage", "code", "garment", "inner", "stage", "texture", "unit"])
        self.assertEqual(inputs["bodies"], [{"label": "Male", "fit": sha("male")}, {"label": "Female", "fit": sha("female")}])


class CodeFingerprintTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.root = pathlib.Path(self.folder.name)
        (self.root / "art_pipeline" / "__pycache__").mkdir(parents=True)
        (self.root / "art_pipeline" / "coverage.py").write_text("REACH = 1\n", encoding="utf-8")
        (self.root / "art_pipeline" / "__pycache__" / "coverage.pyc").write_bytes(b"cached")
        (self.root / "cli.py").write_text("main()\n", encoding="utf-8")

    def tearDown(self):
        self.folder.cleanup()

    def fingerprint(self, blender: str = "5.2.2 abc") -> str:
        return cache.code_fingerprint(cache.code_files(self.root, [self.root / "art_pipeline"], [self.root / "cli.py"]), blender)

    def test_a_change_of_a_pipeline_source_file_misses_every_entry(self):
        before = self.fingerprint()
        (self.root / "art_pipeline" / "coverage.py").write_text("REACH = 2\n", encoding="utf-8")
        self.assertNotEqual(self.fingerprint(), before)

    def test_a_new_file_and_another_blender_change_it_and_python_s_caches_do_not(self):
        before = self.fingerprint()
        self.assertNotEqual(self.fingerprint("5.2.3 abc"), before)
        (self.root / "art_pipeline" / "__pycache__" / "coverage.pyc").write_bytes(b"other")
        self.assertEqual(self.fingerprint(), before)
        (self.root / "art_pipeline" / "check_cache.py").write_text("", encoding="utf-8")
        self.assertNotEqual(self.fingerprint(), before)

    def test_the_files_are_named_relative_to_the_repository_so_every_checkout_shares_the_fingerprint(self):
        names = [name for name, _data in cache.code_files(self.root, [self.root / "art_pipeline"], [self.root / "cli.py"])]
        self.assertEqual(sorted(names), ["art_pipeline/coverage.py", "cli.py"])


class StoreTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.store = cache.Store(self.folder.name)
        self.inputs = cache.own_inputs(base(), "Male", sha("male"), "rig")
        self.key = cache.key(self.inputs)

    def tearDown(self):
        self.folder.cleanup()

    def test_an_entry_reads_back_as_exactly_what_was_written(self):
        arrays = {"covered": np.array([True, False, True]), "underlay": np.array([3, 1, 2], np.int64),
                  "distances": np.array([0.1 + 0.2, 1e-300, -0.0]), "empty": np.empty(0, np.int64)}
        data = {"depth": 0.1 + 0.2, "worst": [["the rest pose", 0.0, "the rest pose"]], "count": 11}
        self.assertTrue(self.store.save(self.key, self.inputs, data, arrays))
        found_data, found_arrays = self.store.load(self.key)
        self.assertEqual(found_data, data)
        self.assertEqual(sorted(found_arrays), sorted(arrays))
        for name, array in arrays.items():
            self.assertEqual(found_arrays[name].dtype, array.dtype, name)
            self.assertEqual(found_arrays[name].tobytes(), array.tobytes(), name)

    def test_the_entry_names_its_inputs_and_leaves_no_temporary_file(self):
        self.store.save(self.key, self.inputs, {}, {"a": np.zeros(2)})
        entry_path, arrays_path = self.store.paths(self.key)
        entry = json.loads(entry_path.read_text(encoding="utf-8"))
        self.assertEqual((entry["schema"], entry["key"], entry["inputs"]), (cache.SCHEMA, self.key, self.inputs))
        self.assertEqual(sorted(p.name for p in entry_path.parent.iterdir()), sorted([entry_path.name, arrays_path.name]))

    def test_a_missing_broken_or_other_entry_reads_as_none(self):
        self.assertIsNone(self.store.load(self.key))
        self.store.save(self.key, self.inputs, {}, {"a": np.zeros(2)})
        entry_path, arrays_path = self.store.paths(self.key)
        arrays_path.write_bytes(arrays_path.read_bytes()[:-3])
        self.assertIsNone(self.store.load(self.key))
        self.store.save(self.key, self.inputs, {}, {"a": np.zeros(2)})
        entry = json.loads(entry_path.read_text(encoding="utf-8"))
        entry_path.write_text(json.dumps(dict(entry, schema=cache.SCHEMA + 1)), encoding="utf-8")
        self.assertIsNone(self.store.load(self.key))
        entry_path.write_text("{", encoding="utf-8")
        self.assertIsNone(self.store.load(self.key))

    def test_an_entry_holds_no_objects_so_nothing_is_ever_unpickled(self):
        with self.assertRaises(TypeError):
            self.store.save(self.key, self.inputs, {}, {"a": np.array([{"x": 1}], dtype=object)})

    def test_a_disabled_store_neither_reads_nor_writes(self):
        self.store.save(self.key, self.inputs, {}, {"a": np.zeros(2)})
        off = cache.Store(self.folder.name, enabled=False)
        self.assertIsNone(off.load(self.key))
        self.assertFalse(off.save(cache.key(dict(self.inputs, stage="model")), self.inputs, {}, {}))
        self.assertFalse(off.paths(cache.key(dict(self.inputs, stage="model")))[0].exists())

    def test_runs_that_write_and_read_one_entry_at_once_see_a_whole_entry_or_none(self):
        """Several Blender runs share the folder: each file goes to a temporary name first and the
        entry's hash ties its arrays to its data, so a reader never takes the data of one writer with
        the arrays of another, and a write that another run holds open is left out, never half done.
        Writers of one key may leave the data of one with the arrays of another, which reads as no
        entry until the next write; an export never has two runs of one asset at once."""
        stop, mixed = threading.Event(), []

        def write(n: int) -> None:
            for _round in range(40):
                self.store.save(self.key, self.inputs, {"n": n}, {"a": np.full(n + 1, n, np.int64)})

        def read() -> None:
            while not stop.is_set():
                entry = self.store.load(self.key)
                if entry is not None and entry[1]["a"].tolist() != [entry[0]["n"]] * (entry[0]["n"] + 1):
                    mixed.append(entry)

        readers = [threading.Thread(target=read) for _ in range(2)]
        writers = [threading.Thread(target=write, args=(n,)) for n in range(4)]
        for thread in readers + writers:
            thread.start()
        for thread in writers:
            thread.join()
        stop.set()
        for thread in readers:
            thread.join()
        self.assertEqual(mixed, [])
        leftovers = [p.name for p in self.store.paths(self.key)[0].parent.iterdir() if p.suffix == ".tmp"]
        self.assertEqual(leftovers, [])
        self.assertTrue(self.store.save(self.key, self.inputs, {"n": 1}, {"a": np.full(2, 1, np.int64)}))
        self.assertEqual(self.store.load(self.key)[0], {"n": 1})

    def test_the_units_count_what_came_from_the_store_and_what_was_written(self):
        units = cache.Units(self.store, base(), dict(FITS), {"Trousers": INNER})
        self.assertIsNone(units.load(units.own("Male", "rig")))
        units.save(units.own("Male", "rig"), {"x": 1}, {})
        units.save(units.own("Nobody", "rig"), {"x": 1}, {})
        self.assertEqual(units.load(units.own("Male", "rig"))[0], {"x": 1})
        self.assertEqual(units.report(), {"units": 2, "cached": 1, "stored": 1, "notes": []})


class SourceTests(unittest.TestCase):
    """Every file a run reads from another asset must still be what its units' keys say when an entry is
    written: its bytes those of its content record, and the record's fingerprint the one the run was
    given. Once one is not, the run writes nothing more and says why."""

    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        root = pathlib.Path(self.folder.name)
        self.template = root / "Trousers" / "Trousers.blend"
        self.template.parent.mkdir()
        self.template.write_bytes(b"trousers as exported")
        self.record({"sourceSha256": self.hash(b"trousers as exported"), "stages": {"rig": sha("trousers")}})
        self.store = cache.Store(root / "cache")
        self.source = {"name": "Trousers", "path": str(self.template), "record": ["stages", "rig"], "content": sha("trousers")}

    def tearDown(self):
        self.folder.cleanup()

    @staticmethod
    def hash(data: bytes) -> str:
        return __import__("hashlib").sha256(data).hexdigest()

    def record(self, content) -> None:
        record = {"kind": "garment"} if content is None else {"kind": "garment", "content": content}
        (self.template.parent / "asset.json").write_text(json.dumps(record), encoding="utf-8")

    def save(self, sources=None, store=None) -> object:
        units = cache.Units(store or self.store, base(), dict(FITS), {}, sources=[self.source] if sources is None else sources)
        units.save(units.own("Male", "rig"), {"x": 1}, {})
        units.save(units.own("Female", "rig"), {"x": 2}, {})
        return units

    def test_sources_that_hold_let_every_entry_be_written(self):
        self.assertIsNone(cache.source_problem(self.source))
        self.assertEqual(self.save().report(), {"units": 0, "cached": 0, "stored": 2, "notes": []})

    def test_a_source_saved_with_other_bytes_than_its_record_stops_every_write(self):
        self.template.write_bytes(b"trousers saved again")
        units = self.save()
        self.assertEqual((units.stored, units.notes), (0, ["Trousers changed during the run"]))

    def test_a_record_with_another_fingerprint_than_the_one_given_stops_every_write(self):
        self.record({"sourceSha256": self.hash(b"trousers as exported"), "stages": {"rig": sha("other trousers")}})
        units = self.save()
        self.assertEqual((units.stored, units.notes), (0, ["Trousers changed during the run"]))

    def test_a_source_without_its_asset_json_or_content_record_stops_every_write(self):
        self.record(None)
        self.assertEqual(self.save().notes, ["Trousers has no content record to compare"])
        (self.template.parent / "asset.json").unlink()
        self.assertEqual(self.save().notes, ["Trousers has no asset.json to compare"])
        self.template.unlink()
        self.assertEqual(self.save().notes, ["Trousers could not be read"])

    def test_a_file_of_the_run_s_own_asset_is_held_to_its_bytes_when_the_run_began(self):
        own = {"name": "NetShirt", "path": str(self.template), "sha256": self.hash(b"trousers as exported")}
        self.assertEqual(self.save([own]).stored, 2)
        self.template.write_bytes(b"painted again")
        self.assertEqual(self.save([own]).notes, ["NetShirt changed during the run"])

    def test_nothing_is_read_from_a_source_when_nothing_is_written(self):
        self.template.write_bytes(b"trousers saved again")
        units = self.save(store=cache.Store(self.folder.name, enabled=False))
        self.assertEqual((units.stored, units.notes), (0, []))


class PoseCountTests(unittest.TestCase):
    """A check with nothing to follow steps through no test pose but reports the same count and the same
    phases as one that stepped."""

    def test_the_reports_name_the_rest_pose_and_the_ten_test_poses(self):
        self.assertEqual(1 + len(poses.POSES), 11)

    def test_skipped_poses_add_the_test_poses_at_nothing_after_the_rest_pose(self):
        worst = {coverage.REST: (0.0, coverage.REST)}
        count = coverage.skipped_poses(worst)
        self.assertEqual(count, len(poses.POSES))
        self.assertEqual(list(worst.items()), [(coverage.REST, (0.0, coverage.REST)), (coverage.TEST_POSES, (0.0, ""))])

    def test_a_pair_with_nothing_to_follow_reports_as_one_that_stepped(self):
        pair = coverage.Pair("Male", 0, 1, {coverage.REST: (0.0, coverage.REST)})
        pair.checked += coverage.skipped_poses(pair.worst)
        self.assertEqual(coverage.pair_summary("HoodUp", "Boots", pair),
                         f"Boots under HoodUp_Male: none of its drawn triangles lies under HoodUp_Male in {len(poses.POSES) + 1} "
                         "poses")


class Stand:
    """Stand-ins for what coverage.checks calls in Blender, which record their calls."""

    def __init__(self, hides=(), meets=()):
        self.calls = []
        self.hides = set(hides)
        self.meeting = set(meets)

    def functions(self) -> dict:
        def note(*call):
            self.calls.append(call)

        def body_margins(name, labels, objects, recipe):
            note("margins", objects[0].name)
            return [(label, obj, types.SimpleNamespace(over=lambda: np.array([0.0105, 0.012]))) for label, obj in zip(labels, objects)]

        def hidden(name, label, band, inner, recipe, inner_recipe, covers, margins):
            note("hidden", inner.name)
            found = np.zeros(4, bool)
            found[[0, 1]] = inner.name in self.hides and covers
            return found

        def compute(name, label, band, covers=True):
            note("compute", name, label)
            return coverage.Coverage(label, band, np.zeros(6, bool), np.zeros(3, np.int64), np.empty(0, np.int64), np.empty(0),
                                     6, 8, 3, np.zeros(6, bool))

        def meets(name, label, band, inner, recipe):
            note("meets", inner.name, label)
            return (inner.name, label) in self.meeting

        def check_motion(name, cover):
            note("motion", cover.label)
            cover.through = np.zeros(6, bool)
            return coverage.Motion(cover.label, 2, 0, 0.0, "", "", 11, faces=3, worst={coverage.REST: (0.0, coverage.REST)})

        def check_pair(name, cover, inner, underlay, hides, recipe, inner_recipe, inner_covered):
            note("pair", inner.name, cover.label)
            return coverage.Pair(cover.label, 2, 11, {coverage.REST: (0.0, coverage.REST)}, faces=np.zeros(4, bool),
                                 hidden=int(hides.sum()))

        def apart_pair(name, label, inner):
            note("apart", inner.name, label)
            return coverage.Pair(label, 0, 11, {coverage.REST: (0.0, coverage.REST)}, faces=np.zeros(4, bool))

        return {"body_margins": body_margins, "hidden": hidden, "compute": compute, "meets": meets,
                "check_motion": check_motion, "check_pair": check_pair, "apart_pair": apart_pair}


def versions(name: str) -> list:
    polygons = [types.SimpleNamespace(vertices=(0, 1, 2)) for _ in range(4)]
    return [types.SimpleNamespace(name=name, data=types.SimpleNamespace(polygons=polygons)) for _label in ("Male", "Female")]


class ChecksTests(unittest.TestCase):
    """coverage.checks: the order the checks run in, the pairs that cannot meet, and the units that come
    from the cache."""

    LABELS = ["Male", "Female"]

    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()

    def tearDown(self):
        self.folder.cleanup()

    def run_checks(self, stand: Stand, inners=("Boots", "Trousers"), covers=True, pair_checks=True, motion_first=True,
                   rig=True, inner_content=None):
        checks = load("art_pipeline/coverage.py", "checks", garment=garment, poses=poses, numpy=True, **stand.functions()).checks
        units = cache.Units(cache.Store(self.folder.name), base(garment={**base()["garment"], "covers": covers}), dict(FITS),
                            {inner: dict(INNER, name=inner, content=inner_content or sha(inner)) for inner in inners})
        given = [(inner, "mid", None, (lambda found=versions(inner): found)) for inner in inners]
        own, under = checks("Shirt", self.LABELS, "tight", covers, None, given, rig, units, pair_checks, motion_first)
        return own, under, units

    def test_a_pair_that_cannot_meet_on_any_body_computes_no_cover_of_it_and_follows_nothing(self):
        stand = Stand(hides=["Trousers"], meets=[("Boots", "Female")])
        _own, under, _units = self.run_checks(stand, inners=("Boots", "Gloves", "Trousers"))
        self.assertTrue(under["Gloves"].apart)
        self.assertFalse(under["Boots"].apart)
        self.assertFalse(under["Trousers"].apart)
        self.assertNotIn(("compute", "Gloves", "Male"), stand.calls)
        self.assertNotIn(("pair", "Gloves", "Male"), stand.calls)
        self.assertEqual([c for c in stand.calls if c[0] == "apart"], [("apart", "Gloves", "Male"), ("apart", "Gloves", "Female")])
        # The rest rule hides faces of the trousers, so they meet without the test.
        self.assertNotIn(("meets", "Trousers", "Male"), stand.calls)
        self.assertEqual([c for c in stand.calls if c[0] == "meets"],
                         [("meets", "Boots", "Male"), ("meets", "Boots", "Female"), ("meets", "Gloves", "Male"),
                          ("meets", "Gloves", "Female")])
        self.assertIn(("compute", "Boots", "Female"), stand.calls)

    def test_the_checks_run_in_their_order_with_the_motion_before_the_pairs_for_a_template(self):
        stand = Stand(hides=["Trousers"], meets=[("Boots", "Male")])
        _own, under, _units = self.run_checks(stand)
        kinds = [call[0] for call in stand.calls]
        self.assertEqual(kinds, ["margins", "margins", "hidden", "hidden", "compute", "compute", "meets", "compute", "compute",
                                 "compute", "compute", "motion", "motion", "pair", "pair", "pair", "pair"])
        self.assertEqual([c[1:] for c in stand.calls if c[0] == "pair"],
                         [("Boots", "Male"), ("Boots", "Female"), ("Trousers", "Male"), ("Trousers", "Female")])
        # The pair check on every body follows the faces the rest rule hides on the first body, all of them.
        self.assertEqual([pair.hidden for pair in under["Trousers"].pairs], [2, 2])

    def test_a_texture_checks_each_body_s_pairs_before_its_motion_and_a_template_that_covers_nothing_has_no_pair_check(self):
        stand = Stand(meets=[("Boots", "Male")])
        self.run_checks(stand, motion_first=False)
        self.assertEqual([c[:2] + c[-1:] for c in stand.calls if c[0] in ("pair", "motion", "apart")],
                         [("pair", "Boots", "Male"), ("apart", "Trousers", "Male"), ("motion", "Male", "Male"),
                          ("pair", "Boots", "Female"), ("apart", "Trousers", "Female"), ("motion", "Female", "Female")])
        stand = Stand(hides=["Trousers"])
        _own, under, _units = self.run_checks(stand, covers=False, pair_checks=False, motion_first=False)
        self.assertEqual([c[0] for c in stand.calls], ["hidden", "hidden", "compute", "compute", "motion", "motion"])
        self.assertIsNone(under["Trousers"].over)
        self.assertFalse(under["Trousers"].hides.any())

    def test_the_model_stage_runs_the_margins_the_rest_rule_and_the_covers_only(self):
        stand = Stand(hides=["Trousers"])
        own, under, _units = self.run_checks(stand, rig=False)
        self.assertEqual([c[0] for c in stand.calls], ["margins", "margins", "hidden", "hidden", "compute", "compute"])
        self.assertIsNone(own["Male"].motion)
        self.assertEqual(own["Male"].coverage.hidden["Trousers"].tolist(), [True, True, False, False])

    def test_the_rig_stage_hides_what_the_rest_rule_hides(self):
        stand = Stand(hides=["Trousers"], meets=[("Boots", "Male")])
        own, under, _units = self.run_checks(stand)
        for label in self.LABELS:
            self.assertEqual(own[label].coverage.hidden["Trousers"].tolist(), [True, True, False, False])
        self.assertEqual(under["Trousers"].hides.tolist(), [True, True, False, False])

    def test_a_second_run_takes_every_unit_from_the_cache_and_gives_the_same_results(self):
        stand = Stand(hides=["Trousers"], meets=[("Boots", "Male")])
        own, under, units = self.run_checks(stand)
        self.assertEqual(units.report(), {"units": 4, "cached": 0, "stored": 4, "notes": []})
        again = Stand(hides=["Trousers"], meets=[("Boots", "Male")])
        cached_own, cached_under, cached_units = self.run_checks(again)
        self.assertEqual(again.calls, [])
        self.assertEqual(cached_units.report(), {"units": 4, "cached": 4, "stored": 0, "notes": []})
        for label in self.LABELS:
            self.assertEqual(coverage.motion_summary("Shirt", cached_own[label].motion), coverage.motion_summary("Shirt", own[label].motion))
            self.assertEqual(cached_own[label].coverage.through.tolist(), own[label].coverage.through.tolist())
            for inner in ("Boots", "Trousers"):
                self.assertEqual(cached_own[label].coverage.hidden[inner].tolist(), own[label].coverage.hidden[inner].tolist())
        for inner in ("Boots", "Trousers"):
            self.assertEqual([coverage.pair_summary("Shirt", inner, p) for p in cached_under[inner].pairs],
                             [coverage.pair_summary("Shirt", inner, p) for p in under[inner].pairs])
            self.assertEqual(cached_under[inner].apart, under[inner].apart)
        self.assertEqual(cached_under["Trousers"].hides.tolist(), [True, True, False, False])

    def test_a_changed_inner_template_misses_only_its_pair(self):
        self.run_checks(Stand(hides=["Trousers"], meets=[("Boots", "Male")]))
        stand = Stand(hides=["Trousers"], meets=[("Boots", "Male")])
        _own, _under, units = self.run_checks(stand, inners=("Boots",), inner_content=sha("boots edited"))
        self.assertEqual(units.report()["cached"], 2)
        self.assertEqual([c[0] for c in stand.calls], ["margins", "hidden", "meets", "compute", "compute", "pair", "pair"])


class EntryTests(unittest.TestCase):
    """The own check and the pair as entries: what reads back gives the same reports and data."""

    def test_an_own_check_reads_back_with_its_arrays_and_its_motion(self):
        cover = coverage.Coverage("Male", "tight", np.array([True, False]), np.array([1, 0], np.int64), np.array([3], np.int64),
                                  np.array([0.0019]), 2, 4, 2, np.array([False, True]), np.array([False, False]))
        motion = coverage.Motion("Male", 12, 1, 0.00493, "sit", "Pelvis", 11, faces=146, rest=0,
                                 worst={coverage.REST: (0.0, coverage.REST), coverage.TEST_POSES: (0.00493, "sit")})
        data, arrays = coverage.own_entry(coverage.OwnCheck(cover, motion))
        back = coverage.own_from(json.loads(json.dumps(data)), arrays)
        self.assertEqual(coverage.motion_summary("Trousers", back.motion), coverage.motion_summary("Trousers", motion))
        self.assertEqual(back.motion, motion)
        for name in ("covered", "underlay", "pull_vertices", "pull_distances", "edge", "through"):
            self.assertEqual(getattr(back.coverage, name).tolist(), getattr(cover, name).tolist(), name)
        self.assertEqual((back.coverage.body_faces, back.coverage.body_vertices, back.coverage.garment_faces), (2, 4, 2))

    def test_a_pair_reads_back_with_every_phase_in_its_order(self):
        pair = coverage.Pair("Female", 162, 11, {coverage.REST: (0.0, coverage.REST), coverage.TEST_POSES: (0.02102, "arms_up")},
                             through=33, depth=0.02102, where="arms_up", zone="Neck", rest=0, faces=np.array([False, True]),
                             places={coverage.TEST_POSES: (0.1, -0.2, 1.5)}, hidden=782,
                             shown={coverage.REST: (0, coverage.REST, (0.0, 0.0, 0.0)), coverage.TEST_POSES: (3, "sit", (0.1, 0.2, 0.3))})
        check = coverage.UnderCheck([np.array([0.0146, 0.2771]), np.empty(0)], np.array([True, False]), [pair, pair], False)
        data, arrays = coverage.under_entry(check)
        back = coverage.under_from(json.loads(json.dumps(data)), arrays)
        self.assertEqual(coverage.pair_summary("HoodUp", "Cloak", back.pairs[0]), coverage.pair_summary("HoodUp", "Cloak", pair))
        self.assertEqual(list(back.pairs[1].worst), list(pair.worst))
        self.assertEqual((back.pairs[0].places, back.pairs[0].shown), (pair.places, pair.shown))
        self.assertEqual([o.tolist() for o in back.over], [o.tolist() for o in check.over])
        self.assertEqual(back.hides.tolist(), check.hides.tolist())
        self.assertEqual(sorted(data), ["apart", "over", "pairs"])
        self.assertIsNone(coverage.under_from(*coverage.under_entry(coverage.UnderCheck(None, check.hides))).over)


class ManifestFieldTests(unittest.TestCase):
    def test_the_fields_are_those_of_every_template_s_asset_json_that_the_checks_read(self):
        for path in sorted((REPOSITORY_ROOT / "art" / "Garments").glob("*/asset.json")):
            fields = cache.manifest_fields(str(path))
            data = json.loads(path.read_text(encoding="utf-8"))
            with self.subTest(template=path.parent.name):
                self.assertEqual(fields, {name: data.get(name) for name in cache.FIELDS})
                self.assertTrue(all(name in data for name in ("band", "recipe", "covers", "slot", "hides", "layer")))


if __name__ == "__main__":
    unittest.main()
