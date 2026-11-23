using System.IO;
using System.Reflection;
using Mmorpg.Client.Editor;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    // The follow-up of an export, which tools/art/Update-UnityArt.ps1 runs in batch mode: its entry, and
    // the cloth fixtures it writes only when the bodies and garments they capture changed, so a run with
    // nothing to do writes nothing. See docs/ART_PIPELINE.md.
    public sealed class ArtExportFollowUpTests
    {
        [Test]
        public void TheBatchEntryIsAPublicStaticMethodWithoutArguments()
        {
            var method = typeof(ArtExportFollowUp).GetMethod(nameof(ArtExportFollowUp.ApplyFromCommandLine), BindingFlags.Public | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            Assert.That(method.GetParameters(), Is.Empty);
            Assert.That(method.ReturnType, Is.EqualTo(typeof(void)));
        }

        // The follow-up keeps the hash of the Addressables settings empty while the saved asset has it
        // empty, so it reads the saved hash from the asset's text.
        [Test]
        public void TheSavedHashOfTheAddressablesSettingsIsReadFromTheirAsset()
        {
            var path = Path.Combine(Path.GetTempPath(), "ArtExportFollowUpTests-" + System.Guid.NewGuid().ToString("N") + ".asset");
            try
            {
                const string Head = "  m_DefaultGroup: b9d8df47ca882f349878b22d8ad29670\n  m_currentHash:\n    serializedVersion: 2\n    Hash: ";
                File.WriteAllText(path, Head + "00000000000000000000000000000000\n  m_ExtractTypeTreeData: 0\n");
                Assert.That(ArtExportFollowUp.SavedHashIsEmpty(path), Is.True);
                File.WriteAllText(path, Head + "0123456789abcdef0123456789abcdef\n  m_ExtractTypeTreeData: 0\n");
                Assert.That(ArtExportFollowUp.SavedHashIsEmpty(path), Is.False);
                File.WriteAllText(path, "  m_ExtractTypeTreeData: 0\n");
                Assert.That(ArtExportFollowUp.SavedHashIsEmpty(path), Is.False);
            }
            finally
            {
                File.Delete(path);
            }

            Assert.That(ArtExportFollowUp.SavedHashIsEmpty(path), Is.False);
        }

        [Test]
        public void FixturesAreTheSameWhenEveryNumberLiesWithinTheTolerance()
        {
            const string Written = "{\"version\":2,\"cases\":[\n{\"name\":\"male/robe/rest\",\"x\":0.5,\"y\":-1.25e-7,\"known\":true}\n]}\n";
            Assert.That(ClothKinematicFixtures.SameWithin(Written, Written, 0.0), Is.True);
            Assert.That(ClothKinematicFixtures.SameWithin(Written, Written.Replace("0.5", "0.500004"), 1e-5), Is.True);
            Assert.That(ClothKinematicFixtures.SameWithin(Written, Written.Replace("-1.25e-7", "3.0e-6"), 1e-5), Is.True);
            Assert.That(ClothKinematicFixtures.SameWithin(Written, Written.Replace("{\"version\":2,", "{ \"version\": 2,"), 0.0), Is.True);
        }

        [Test]
        public void FixturesDifferWithAFartherNumberAnotherNameOrAnotherCase()
        {
            const string Written = "{\"version\":2,\"cases\":[\n{\"name\":\"male/robe/rest\",\"x\":0.5,\"known\":true}\n]}\n";
            Assert.That(ClothKinematicFixtures.SameWithin(Written, Written.Replace("0.5", "0.50002"), 1e-5), Is.False);
            Assert.That(ClothKinematicFixtures.SameWithin(Written, Written.Replace("male/robe/rest", "male/robe/sit"), 1e-5), Is.False);
            Assert.That(ClothKinematicFixtures.SameWithin(Written, Written.Replace("true", "false"), 1e-5), Is.False);
            Assert.That(ClothKinematicFixtures.SameWithin(Written, Written.Replace("]}", ",{\"name\":\"female/robe/rest\"}]}"), 1e-5), Is.False);
            Assert.That(ClothKinematicFixtures.SameWithin(Written, Written.Replace("\"x\":0.5,", string.Empty), 1e-5), Is.False);
            Assert.That(ClothKinematicFixtures.SameWithin(Written, Written.Replace("0.5", "\"0.5\""), 1e-5), Is.False);
        }

        // The follow-up compares each number, ClothPoseTests the distance of each point, so a point it
        // keeps is never one the tests fail: 8e-6 on each axis lies 1.39e-5 away.
        [Test]
        public void APointTheFollowUpKeepsLiesWithinTheDistanceTheClothTestsAllow()
        {
            const string Written = "{\"a\":{\"x\":0.5,\"y\":0.25,\"z\":-0.125}}";
            var far = Written.Replace("0.5", "0.500008").Replace("0.25", "0.250008").Replace("-0.125", "-0.124992");
            Assert.That(UnityEngine.Mathf.Sqrt(3f) * 8e-6f, Is.GreaterThan(1e-5f));
            Assert.That(ClothKinematicFixtures.SameWithin(Written, far, ClothKinematicFixtures.Tolerance), Is.False);
            var near = Written.Replace("0.5", "0.500004").Replace("0.25", "0.250004").Replace("-0.125", "-0.124996");
            Assert.That(ClothKinematicFixtures.SameWithin(Written, near, ClothKinematicFixtures.Tolerance), Is.True);
            Assert.That(System.Math.Sqrt(3.0) * ClothKinematicFixtures.Tolerance, Is.LessThanOrEqualTo(1e-5));
        }

        // The committed fixtures are what the follow-up keeps, so it writes them only after a change.
        [Test]
        public void TheCommittedFixturesAreWhatTheFollowUpKeeps()
        {
            var written = File.ReadAllText(ClothKinematicFixtures.FilePath);
            var captured = ClothKinematicFixtures.ToJson(ClothKinematicFixtures.Capture());
            Assert.That(ClothKinematicFixtures.SameWithin(written, captured, ClothKinematicFixtures.Tolerance), Is.True,
                "the cloth fixtures differ from the bodies and garments of today; run " + ClothKinematicFixtures.MenuPath + " and commit them");
        }
    }
}
