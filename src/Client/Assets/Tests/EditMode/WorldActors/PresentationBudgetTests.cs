using Mmorpg.Client.Gameplay;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    // The frame's time for putting things into view: every part takes from one budget a frame, and
    // what a frame spends beyond it, at most one budget, the next frame has less of. See
    // docs/UNITY_CLIENT_ARCHITECTURE.md, Crowd Performance.
    public sealed class PresentationBudgetTests
    {
        [Test]
        public void TheFrameSharesOneBudgetAndTheNextOnePaysWhatItOverran()
        {
            var frame = 1;
            var budget = new PresentationBudget(3.0, () => frame);
            Assert.That(budget.Available, Is.EqualTo(3.0));
            budget.Charge(2.0);
            Assert.That(budget.IsSpent, Is.False);
            budget.Charge(1.5);
            Assert.That(budget.IsSpent, Is.True, "two parts share the budget of the frame");

            frame = 2;
            Assert.That(budget.Spent, Is.EqualTo(0.0));
            Assert.That(budget.Available, Is.EqualTo(2.5).Within(1e-9), "the frame before overran by half a millisecond");
            budget.Charge(2.5);
            Assert.That(budget.IsSpent, Is.True);

            frame = 3;
            Assert.That(budget.Available, Is.EqualTo(3.0), "a frame within its budget leaves nothing to pay");
            budget.Charge(100.0);

            frame = 4;
            Assert.That(budget.Available, Is.EqualTo(0.0), "a frame pays at most one budget");
            Assert.That(budget.IsSpent, Is.True);

            frame = 6;
            Assert.That(budget.Available, Is.EqualTo(3.0), "a frame without work in between pays nothing");
        }

        [Test]
        public void TimedWorkCountsWhileItRunsAndOnceItEnds()
        {
            var frame = 1;
            var budget = new PresentationBudget(1000.0, () => frame);
            budget.Begin();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (watch.Elapsed.TotalMilliseconds < 2.0)
            {
            }

            Assert.That(budget.Spent, Is.GreaterThan(1.0));
            budget.End();
            var spent = budget.Spent;
            Assert.That(spent, Is.GreaterThan(1.0));
            Assert.That(budget.Spent, Is.EqualTo(spent), "ended work counts no more time");
            budget.End();
            Assert.That(budget.Spent, Is.EqualTo(spent), "a second end counts nothing");
        }
    }
}
