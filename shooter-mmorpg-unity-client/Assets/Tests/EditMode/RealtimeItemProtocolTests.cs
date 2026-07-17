using System;
using NUnit.Framework;
using ShooterMmo.GameProtocol;

namespace ShooterMmo.Tests.EditMode
{
    public sealed class RealtimeItemProtocolTests
    {
        [Test]
        public void SecureContainerRelocateIntentRoundTripsWithoutClientAuthorityFields()
        {
            var operationId = Guid.NewGuid();
            var itemId = Guid.NewGuid();
            var secureContainerId = Guid.NewGuid();
            var expected = RealtimeItemOperationIntent.CreateRelocate(
                operationId,
                7,
                itemId,
                3,
                secureContainerId,
                1);

            var decoded = RealtimeProtocol.TryDecodeItemOperationIntent(
                RealtimeProtocol.EncodeItemOperationIntent(expected),
                out var actual,
                out var error);

            Assert.That(decoded, Is.True, error);
            Assert.That(actual.OperationId, Is.EqualTo(operationId));
            Assert.That(actual.OperationKind, Is.EqualTo(RealtimeItemOperationKind.Relocate));
            Assert.That(actual.ExpectedCharacterRevision, Is.EqualTo(7));
            Assert.That(actual.ItemInstanceId, Is.EqualTo(itemId));
            Assert.That(actual.ExpectedItemRevision, Is.EqualTo(3));
            Assert.That(actual.DestinationContainerId, Is.EqualTo(secureContainerId));
            Assert.That(actual.DestinationSlotIndex, Is.EqualTo(1));
        }

        [Test]
        public void CommittedItemResultCarriesAuthoritativeRevisionAndRefreshSignal()
        {
            var expected = new RealtimeItemOperationResult(
                Guid.NewGuid(),
                RealtimeItemOperationKind.Destroy,
                true,
                false,
                null,
                new RealtimeCarryState(8, 0, 200),
                Array.Empty<RealtimeItemRevision>(),
                new[] { new RealtimeContainerRevision(Guid.NewGuid(), 4) },
                Array.Empty<Guid>());

            var decoded = RealtimeProtocol.TryDecodeItemOperationResult(
                RealtimeProtocol.EncodeItemOperationResult(expected),
                out var actual,
                out var error);

            Assert.That(decoded, Is.True, error);
            Assert.That(actual.Succeeded, Is.True);
            Assert.That(actual.RequiresInventoryRefresh, Is.False);
            Assert.That(actual.CarryState.ItemStateRevision, Is.EqualTo(8));
            Assert.That(actual.CarryState.CarriedWeight, Is.EqualTo(0));
            Assert.That(actual.CarryState.CarryCapacity, Is.EqualTo(200));
            Assert.That(actual.ContainerRevisions, Has.Length.EqualTo(1));
        }

        [Test]
        public void ContainerItemSwapIntentRoundTripsBothItemExpectations()
        {
            var operationId = Guid.NewGuid();
            var firstItemId = Guid.NewGuid();
            var secondItemId = Guid.NewGuid();
            var expected = RealtimeItemOperationIntent.CreateSwapContainerItems(
                operationId,
                11,
                firstItemId,
                4,
                secondItemId,
                9);

            var decoded = RealtimeProtocol.TryDecodeItemOperationIntent(
                RealtimeProtocol.EncodeItemOperationIntent(expected),
                out var actual,
                out var error);

            Assert.That(decoded, Is.True, error);
            Assert.That(actual.OperationKind, Is.EqualTo(RealtimeItemOperationKind.SwapContainerItems));
            Assert.That(actual.OperationId, Is.EqualTo(operationId));
            Assert.That(actual.ExpectedCharacterRevision, Is.EqualTo(11));
            Assert.That(actual.ItemInstanceId, Is.EqualTo(firstItemId));
            Assert.That(actual.ExpectedItemRevision, Is.EqualTo(4));
            Assert.That(actual.TargetItemInstanceId, Is.EqualTo(secondItemId));
            Assert.That(actual.ExpectedTargetItemRevision, Is.EqualTo(9));
        }
    }
}
