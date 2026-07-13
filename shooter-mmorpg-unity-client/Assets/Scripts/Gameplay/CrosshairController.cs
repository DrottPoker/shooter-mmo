using System;
using UnityEngine;

namespace ShooterMmo.Gameplay
{
    [DisallowMultipleComponent]
    public sealed class CrosshairController : MonoBehaviour
    {
        private CrosshairDefinition definition = CrosshairDefinition.Unarmed;
        private float dynamicSpread;

        public event Action Changed;

        public CrosshairDefinition CurrentDefinition
        {
            get { return definition; }
        }

        public float DynamicSpread
        {
            get { return dynamicSpread; }
        }

        private void Awake()
        {
            EnsureDefinition();
        }

        private void OnEnable()
        {
            EnsureDefinition();
        }

        public void SetCrosshair(CrosshairDefinition nextDefinition)
        {
            definition = nextDefinition;
            dynamicSpread = 0f;
            Changed?.Invoke();
        }

        public void SetDynamicSpread(float spreadPixels)
        {
            var nextSpread = Mathf.Max(0f, spreadPixels);
            if (Mathf.Approximately(dynamicSpread, nextSpread))
            {
                return;
            }

            dynamicSpread = nextSpread;
            Changed?.Invoke();
        }

        public void ResetToUnarmed()
        {
            SetCrosshair(CrosshairDefinition.Unarmed);
        }

        private void EnsureDefinition()
        {
            if (definition.Size < 1f)
            {
                ResetToUnarmed();
            }
        }
    }
}
