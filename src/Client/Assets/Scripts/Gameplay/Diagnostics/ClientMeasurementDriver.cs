using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // Hands every frame to the client measurements after the presentation moved, so they read
    // the local player where it is drawn. The composition root adds it next to the persistent
    // services.
    [DefaultExecutionOrder(PresentationExecutionOrder.Measurements)]
    public sealed class ClientMeasurementDriver : MonoBehaviour
    {
        private ClientMeasurements measurements;

        public void Initialize(ClientMeasurements clientMeasurements)
        {
            measurements = clientMeasurements;
        }

        private void LateUpdate()
        {
            measurements?.Frame(Time.realtimeSinceStartup, Time.unscaledDeltaTime, Time.deltaTime);
        }
    }
}
