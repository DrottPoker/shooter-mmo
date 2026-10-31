using Mmorpg.Client.WorldActors;
using NUnit.Framework;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // Holding the left button after a left click that walked steers the walk: a new route at
    // most every RouteInterval, only for a point that moved, only once the previous route
    // applied, and never after another feature took over the navigation.
    public sealed class WalkSteeringTests
    {
        private const int WalkIntent = 7;

        [Test]
        public void AWalkThatNeverStartedSteersNothing()
        {
            var steering = new WalkSteering();
            Assert.That(steering.IsSteering, Is.False);
            Assert.That(steering.ShouldSteerTo(new Vector3(5f, 0f, 5f), 0, 0, 10f), Is.False);

            steering.Follow(0, Vector3.zero, 0f);
            Assert.That(steering.IsSteering, Is.False);
            Assert.That(steering.ShouldSteerTo(new Vector3(5f, 0f, 5f), 0, 0, 10f), Is.False);
        }

        [Test]
        public void ANewRouteWaitsForTheIntervalAndAMovedPoint()
        {
            var steering = new WalkSteering();
            var destination = new Vector3(2f, 0f, 3f);
            steering.Follow(WalkIntent, destination, 1f);
            var moved = destination + new Vector3(1f, 0f, 0f);
            var interval = WalkSteering.RouteInterval;

            Assert.That(steering.IsSteering, Is.True);
            Assert.That(steering.ShouldSteerTo(moved, WalkIntent, WalkIntent, 1f + (interval * 0.5f)), Is.False);
            Assert.That(steering.ShouldSteerTo(destination, WalkIntent, WalkIntent, 1f + interval), Is.False);
            var nearby = destination + new Vector3(0f, 0f, WalkSteering.MinimumShift * 0.9f);
            Assert.That(steering.ShouldSteerTo(nearby, WalkIntent, WalkIntent, 1f + interval), Is.False);
            var climbed = destination + new Vector3(0f, WalkSteering.MinimumShift * 1.1f, 0f);
            Assert.That(steering.ShouldSteerTo(climbed, WalkIntent, WalkIntent, 1f + interval), Is.True);
            Assert.That(steering.ShouldSteerTo(moved, WalkIntent, WalkIntent, 1f + interval), Is.True);

            // The route to the moved point restarts the interval from there.
            var next = WalkIntent + 1;
            steering.Follow(next, moved, 1f + interval);
            var further = moved + new Vector3(1f, 0f, 0f);
            Assert.That(steering.ShouldSteerTo(further, next, next, 1f + (interval * 1.5f)), Is.False);
            Assert.That(steering.ShouldSteerTo(further, next, next, 1f + (interval * 2.5f)), Is.True);
        }

        // The navigator discards a finished plan whose intent a newer request replaced, so a
        // plan that takes longer than the interval must apply before the next route starts.
        [Test]
        public void ANewRouteWaitsUntilThePreviousPlanApplied()
        {
            var steering = new WalkSteering();
            steering.Follow(WalkIntent, Vector3.zero, 0f);
            var moved = new Vector3(4f, 0f, 0f);

            Assert.That(steering.ShouldSteerTo(moved, WalkIntent, WalkIntent - 1, 5f), Is.False);
            Assert.That(steering.IsSteering, Is.True);
            Assert.That(steering.ShouldSteerTo(moved, WalkIntent, WalkIntent, 5f), Is.True);
        }

        // An attack, an interaction, or a death plans or stops with a navigation intent of its
        // own, and the steering must not take the character back from it.
        [Test]
        public void AnotherNavigationIntentEndsTheSteering()
        {
            var steering = new WalkSteering();
            steering.Follow(WalkIntent, Vector3.zero, 0f);
            var moved = new Vector3(4f, 0f, 0f);

            Assert.That(steering.ShouldSteerTo(moved, WalkIntent + 1, WalkIntent + 1, 1f), Is.False);
            Assert.That(steering.IsSteering, Is.False);
            Assert.That(steering.ShouldSteerTo(moved, WalkIntent, WalkIntent, 2f), Is.False);
        }

        [Test]
        public void EndStopsTheSteering()
        {
            var steering = new WalkSteering();
            steering.Follow(WalkIntent, Vector3.zero, 0f);

            steering.End();

            Assert.That(steering.IsSteering, Is.False);
            Assert.That(steering.ShouldSteerTo(new Vector3(4f, 0f, 0f), WalkIntent, WalkIntent, 1f), Is.False);
        }
    }
}
