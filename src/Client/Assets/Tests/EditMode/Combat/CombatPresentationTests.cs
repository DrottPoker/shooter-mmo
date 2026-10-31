using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Combat;
using Mmorpg.Client.Items;
using Mmorpg.Client.Ui;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Items;
using NUnit.Framework;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // The pure rules behind the combat presentation: weapon range, the live arrow count,
    // the server clock and respawn countdown, projectile timing, and status labels.
    public sealed class CombatPresentationTests
    {
        private ClientItemCatalog catalog;

        [OneTimeSetUp]
        public void LoadCatalog()
        {
            ItemPresentationCatalogLoader.ResetCache();
            catalog = FixtureItemCatalog.Load();
        }

        [OneTimeTearDown]
        public void ResetCatalog()
        {
            ItemPresentationCatalogLoader.ResetCache();
        }

        [Test]
        public void WeaponRangeComesFromTheMainHandItemInTheBundledCatalog()
        {
            Assert.That(
                CombatLoadoutRules.TryGetWeaponRange(catalog, Snapshot(Item("fixture.bow", 1)), out var range),
                Is.True);
            Assert.That(range, Is.EqualTo(10f));
            Assert.That(
                CombatLoadoutRules.TryGetWeaponRange(catalog, Snapshot(Item("fixture.sword", 1)), out range),
                Is.True);
            Assert.That(range, Is.EqualTo(1.5f));

            // No weapon, a non-weapon item, or nothing loaded yet means no range.
            Assert.That(CombatLoadoutRules.TryGetWeaponRange(catalog, Snapshot(null), out range), Is.False);
            Assert.That(range, Is.Zero);
            Assert.That(
                CombatLoadoutRules.TryGetWeaponRange(catalog, Snapshot(Item("fixture.arrow", 1)), out _),
                Is.False);
            Assert.That(
                CombatLoadoutRules.TryGetWeaponRange(catalog, Snapshot(Item("fixture.unknown", 1)), out _),
                Is.False);
            Assert.That(
                CombatLoadoutRules.TryGetWeaponRange(null, Snapshot(Item("fixture.bow", 1)), out _),
                Is.False);
            Assert.That(CombatLoadoutRules.TryGetWeaponRange(catalog, null, out _), Is.False);
            Assert.That(CombatLoadoutRules.TryGetWeaponRange((ItemDefinition)null, out _), Is.False);
        }

        [Test]
        public void ArrowSlotShowsTheLiveCountOnlyForTheStackTheWorkerNames()
        {
            var equipped = Guid.NewGuid();

            Assert.That(
                CombatLoadoutRules.DisplayedAmmunitionQuantity(equipped, 500, Owner(equipped, 482)),
                Is.EqualTo(482));
            Assert.That(
                CombatLoadoutRules.DisplayedAmmunitionQuantity(equipped, 500, Owner(Guid.NewGuid(), 482)),
                Is.EqualTo(500));
            Assert.That(
                CombatLoadoutRules.DisplayedAmmunitionQuantity(equipped, 500, Owner(Guid.Empty, 0)),
                Is.EqualTo(500));
            Assert.That(
                CombatLoadoutRules.DisplayedAmmunitionQuantity(equipped, 500, null),
                Is.EqualTo(500));
            Assert.That(
                CombatLoadoutRules.DisplayedAmmunitionQuantity(equipped, 3, Owner(equipped, 0)),
                Is.Zero);

            // A live count that differs from the committed stack always shows, even 0 or 1.
            Assert.That(InventorySlotPresentation.LiveQuantityText(500, 482), Is.EqualTo("482"));
            Assert.That(InventorySlotPresentation.LiveQuantityText(3, 1), Is.EqualTo("1"));
            Assert.That(InventorySlotPresentation.LiveQuantityText(3, 0), Is.EqualTo("0"));
            Assert.That(InventorySlotPresentation.LiveQuantityText(500, 500), Is.EqualTo("500"));
            Assert.That(InventorySlotPresentation.LiveQuantityText(1, 1), Is.Empty);
            Assert.That(
                InventorySlotPresentation.LiveQuantityDescription("Equipped in Ammunition", 500, 482),
                Does.StartWith("Equipped in Ammunition\n482 left"));
            Assert.That(
                InventorySlotPresentation.LiveQuantityDescription("Equipped in Ammunition", 500, 500),
                Is.EqualTo("Equipped in Ammunition"));
        }

        [Test]
        public void ServerClockAddsTheTimeSinceTheNewestOwnerSnapshot()
        {
            Assert.That(default(CombatServerClock).IsKnown, Is.False);
            Assert.That(default(CombatServerClock).SecondsUntil(500), Is.Zero);
            Assert.That(new CombatServerClock(0, 1f, 30).IsKnown, Is.False);
            Assert.That(new CombatServerClock(100, 1f, 0).IsKnown, Is.False);

            var clock = new CombatServerClock(1_000, 0.5f, 30);
            Assert.That(clock.IsKnown, Is.True);
            Assert.That(clock.SecondsUntil(1_150), Is.EqualTo(4.5f).Within(0.0001f));
            Assert.That(clock.SecondsUntil(1_000), Is.EqualTo(-0.5f).Within(0.0001f));
            Assert.That(clock.SecondsUntil(970), Is.EqualTo(-1.5f).Within(0.0001f));
            Assert.That(new CombatServerClock(1_000, -3f, 30).SecondsSinceLatestTick, Is.Zero);

            // Ticks wrap, so a respawn tick past the wrap is still ahead.
            var wrapping = new CombatServerClock(uint.MaxValue - 9, 0f, 30);
            Assert.That(wrapping.SecondsUntil(20), Is.EqualTo(1f).Within(0.0001f));
        }

        [Test]
        public void RespawnCountdownRoundsUpToWholeSeconds()
        {
            Assert.That(CombatHudPresentation.RespawnCountdownText(false, 3f), Is.EqualTo("Respawning soon"));
            Assert.That(CombatHudPresentation.RespawnCountdownText(true, 4.2f), Is.EqualTo("Respawning in 5 s"));
            Assert.That(CombatHudPresentation.RespawnCountdownText(true, 5f), Is.EqualTo("Respawning in 5 s"));
            Assert.That(CombatHudPresentation.RespawnCountdownText(true, 0.01f), Is.EqualTo("Respawning in 1 s"));
            Assert.That(CombatHudPresentation.RespawnCountdownText(true, 0f), Is.EqualTo("Respawning..."));
            Assert.That(CombatHudPresentation.RespawnCountdownText(true, -2f), Is.EqualTo("Respawning..."));

            var clock = new CombatServerClock(1_000, 0.25f, 30);
            Assert.That(
                CombatHudPresentation.RespawnCountdownText(clock.IsKnown, clock.SecondsUntil(1_150)),
                Is.EqualTo("Respawning in 5 s"));
        }

        [Test]
        public void EveryAttackStatusHasItsOwnShortLabel()
        {
            var labels = new Dictionary<RealtimeCombatAttackStatus, string>();
            foreach (RealtimeCombatAttackStatus status in Enum.GetValues(typeof(RealtimeCombatAttackStatus)))
            {
                var label = CombatHudPresentation.AttackStatusLabel(status);
                Assert.That(label, Is.Not.Empty, status.ToString());
                Assert.That(label, Is.Not.EqualTo(CombatHudPresentation.UnknownStatusLabel), status.ToString());
                Assert.That(label.Length, Is.LessThanOrEqualTo(24), status.ToString());
                labels.Add(status, label);
            }

            Assert.That(labels.Values.Distinct().Count(), Is.EqualTo(labels.Count));
            Assert.That(labels[RealtimeCombatAttackStatus.OutOfRange], Is.EqualTo("Out of range"));
            Assert.That(labels[RealtimeCombatAttackStatus.WeaponMissing], Is.EqualTo("No weapon equipped"));
            Assert.That(labels[RealtimeCombatAttackStatus.NotEnoughMana], Is.EqualTo("Not enough mana"));
            Assert.That(
                CombatHudPresentation.AttackStatusLabel((RealtimeCombatAttackStatus)200),
                Is.EqualTo(CombatHudPresentation.UnknownStatusLabel));
        }

        [Test]
        public void HealthBarsShowOnlyOverDamagedLivingCombatants()
        {
            Assert.That(CombatHudPresentation.ShowsHealthBar(40, 50, RealtimeCombatLifeState.Alive), Is.True);
            Assert.That(CombatHudPresentation.ShowsHealthBar(40, 50, RealtimeCombatLifeState.Evading), Is.True);
            Assert.That(CombatHudPresentation.ShowsHealthBar(50, 50, RealtimeCombatLifeState.Alive), Is.False);
            Assert.That(CombatHudPresentation.ShowsHealthBar(0, 50, RealtimeCombatLifeState.Dead), Is.False);
            Assert.That(CombatHudPresentation.ShowsHealthBar(0, 0, RealtimeCombatLifeState.Alive), Is.False);

            Assert.That(CombatHudPresentation.HealthFraction(25, 50), Is.EqualTo(0.5f));
            Assert.That(CombatHudPresentation.HealthFraction(80, 50), Is.EqualTo(1f));
            Assert.That(CombatHudPresentation.HealthFraction(10, 0), Is.Zero);
            Assert.That(CombatHudPresentation.HealthText(87, 100), Is.EqualTo("87 / 100"));
            Assert.That(CombatHudPresentation.ArrowCountText(true, 482), Is.EqualTo("Arrows 482"));
            Assert.That(CombatHudPresentation.ArrowCountText(false, 0), Is.EqualTo("No arrows equipped"));
            Assert.That(CombatHudPresentation.ManaText(95, 100), Is.EqualTo("Mana 95 / 100"));
            Assert.That(CombatHudPresentation.ManaText(0, 0), Is.EqualTo("No mana"));
            Assert.That(
                CombatHudPresentation.DamageColor(RealtimeCombatDamageType.Normal),
                Is.EqualTo(Color.white));
        }

        [Test]
        public void OnlyReleasedAttacksThatLandLaterShowAProjectile()
        {
            Assert.That(CombatProjectileRules.IsProjectile(Released(110), 100), Is.True);
            Assert.That(CombatProjectileRules.IsProjectile(Released(100), 100), Is.False);
            Assert.That(CombatProjectileRules.IsProjectile(Released(5), uint.MaxValue - 2), Is.True);
            Assert.That(
                CombatProjectileRules.IsProjectile(
                    new RealtimeCombatEvent(RealtimeCombatEventKind.Hit, 5, 7, 0, 12, RealtimeCombatDamageType.Normal, 38, RealtimeCombatAttackStyle.Melee),
                    100),
                Is.False);
            Assert.That(CombatProjectileRules.IsProjectile(null, 100), Is.False);

            // An arrow lands at its impact tick by the server clock, or by the batch tick
            // before the first owner snapshot.
            Assert.That(
                CombatProjectileRules.RemainingFlightSeconds(new CombatServerClock(100, 0.1f, 30), 109, 100, 30),
                Is.EqualTo(0.2f).Within(0.0001f));
            Assert.That(
                CombatProjectileRules.RemainingFlightSeconds(default(CombatServerClock), 109, 100, 30),
                Is.EqualTo(0.3f).Within(0.0001f));
            Assert.That(
                CombatProjectileRules.RemainingFlightSeconds(default(CombatServerClock), 109, 100, 0),
                Is.Zero);

        }

        // A started attack releases at its release tick, counted from its own batch, so its clip
        // strikes or looses as the batch of the release arrives.
        [Test]
        public void AStartedAttackReleasesAfterItsWindupFromItsBatch()
        {
            Assert.That(CombatProjectileRules.SecondsUntilRelease(124, 100, 30), Is.EqualTo(0.8f).Within(0.0001f));
            Assert.That(CombatProjectileRules.SecondsUntilRelease(100, 100, 30), Is.Zero);
            Assert.That(CombatProjectileRules.SecondsUntilRelease(9, uint.MaxValue - 20, 30), Is.EqualTo(1f).Within(0.0001f));
            Assert.That(CombatProjectileRules.SecondsUntilRelease(124, 100, 0), Is.Zero);
        }

        private static RealtimeCombatEvent Released(uint impactTick)
        {
            return new RealtimeCombatEvent(
                RealtimeCombatEventKind.AttackReleased,
                5,
                7,
                impactTick,
                0,
                RealtimeCombatDamageType.Normal,
                0,
                RealtimeCombatAttackStyle.Ranged);
        }

        private static RealtimeOwnerCombatState Owner(Guid ammunition, int quantity)
        {
            return new RealtimeOwnerCombatState(
                1,
                100,
                100,
                100,
                100,
                100,
                RealtimeCombatLifeState.Alive,
                0,
                0,
                false,
                0,
                RealtimeCombatAttackStatus.Idle,
                0,
                0,
                ammunition,
                quantity);
        }

        private CharacterInventorySnapshot Snapshot(InventoryItem mainHand)
        {
            return new CharacterInventorySnapshot(
                Guid.NewGuid(),
                catalog.Revision,
                5,
                Container("permanent_inventory"),
                new[]
                {
                    new InventoryEquipmentSlot(ItemEquipmentSlotIds.MainHand, 2, mainHand),
                    new InventoryEquipmentSlot(ItemEquipmentSlotIds.Ammunition, 4, null)
                },
                null,
                Container("bank"),
                new SecureContainerInventory("secure_container.base", 1, Container("secure_container")),
                new RecoveryStorageInventory(Guid.NewGuid(), 1, Array.Empty<RecoveryDelivery>()),
                10,
                200,
                500,
                10_000);
        }

        private static InventoryContainer Container(string type)
        {
            return new InventoryContainer(
                Guid.NewGuid(),
                type,
                1,
                4,
                new[] { new InventorySlot(0, "general", Array.Empty<string>(), null) });
        }

        private static InventoryItem Item(string definitionId, int quantity)
        {
            return new InventoryItem(
                Guid.NewGuid(),
                definitionId,
                quantity,
                1,
                Array.Empty<InventoryPolicy>());
        }
    }
}
