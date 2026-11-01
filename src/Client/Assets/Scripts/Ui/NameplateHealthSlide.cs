using UnityEngine;

namespace Mmorpg.Client.Ui
{
    // The health a nameplate shows. Every new value slides in over a fixed time, fast at first
    // and easing out, so a hit runs the bar and its numbers down instead of jumping. A value that
    // arrives mid-slide slides on from where the bar is. The first value and a new maximum show
    // at once.
    public sealed class NameplateHealthSlide
    {
        private readonly float slideSeconds;
        private float from;
        private float startedAt;
        private bool hasValue;

        public NameplateHealthSlide(float slideSeconds)
        {
            this.slideSeconds = Mathf.Max(0f, slideSeconds);
        }

        // The newest authoritative health.
        public int Target { get; private set; }

        public int Maximum { get; private set; }

        public void Set(int health, int maximumHealth, float now)
        {
            if (!hasValue || maximumHealth != Maximum)
            {
                hasValue = true;
                Target = health;
                Maximum = maximumHealth;
                from = health;
                startedAt = float.NegativeInfinity;
                return;
            }

            if (health == Target)
            {
                return;
            }

            from = Displayed(now);
            Target = health;
            startedAt = now;
        }

        // Forgets the shown health, so the next value shows at once.
        public void Reset()
        {
            hasValue = false;
            Target = 0;
            Maximum = 0;
            from = 0f;
            startedAt = float.NegativeInfinity;
        }

        public bool IsSliding(float now)
        {
            return now - startedAt < slideSeconds;
        }

        // The health between the previous and the newest value at this time.
        public float Displayed(float now)
        {
            if (!IsSliding(now))
            {
                return Target;
            }

            var progress = Mathf.Clamp01((now - startedAt) / slideSeconds);
            var eased = 1f - ((1f - progress) * (1f - progress));
            return Mathf.Lerp(from, Target, eased);
        }

        // The whole number the bar shows: rounded while sliding, the newest value once there.
        public int DisplayedHealth(float now)
        {
            return IsSliding(now) ? Mathf.RoundToInt(Displayed(now)) : Target;
        }

        public float Fraction(float now)
        {
            return Maximum <= 0 ? 0f : Mathf.Clamp01(Displayed(now) / Maximum);
        }
    }
}
