using System;
using Mmorpg.SharedTests;
using Mmorpg.WorldData.Items.Presentation;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    // Every serialized field of the presentation catalog changes its revision, as Changing A
    // Content Format in content/README.md describes. The .NET twins are ContentHashCoverageTests.
    public sealed class ItemPresentationHashCoverageTests
    {
        [Test]
        public void ThePresentationCatalogStampCoversEveryField()
        {
            var findings = HashCoverage.Check(
                HashCoverage.Sample<ItemPresentationCatalogDocument>,
                catalog =>
                {
                    ItemPresentationCatalogCompiler.Stamp(catalog);
                    return catalog.presentationRevision;
                },
                "ItemPresentationCatalogCompiler.Stamp",
                null,
                null);

            Assert.That(findings, Is.Empty, string.Join(Environment.NewLine, findings));
        }
    }
}
