using UnityEngine;

public class GrabStateSender : MonoBehaviour
{
    [SerializeField] private UdpSubscriptionClient client;
    [SerializeField] private SnapGrabbable trackedGrabbable;
    [SerializeField] private StringFrame3D sphereSource;

    private bool wasHeld = false;

    private void Update()
    {
        bool held = trackedGrabbable.IsHeld;

        if (held)
        {
            Vector3 positionToSend = Vector3.zero;

            int highlightedSphere = trackedGrabbable.CurrentHighlightedSphere;
            if (highlightedSphere >= 0)
            {
                sphereSource.GetSphereID(highlightedSphere, out int s, out int p);
                positionToSend = sphereSource.GetSphereWorldPosition(s, p);
            }

            client.SendState(true, positionToSend);

            // Live pin pose while grabbed (not yet snapped -> not covered by RegisterSnap's Set).
            // Sent frame-local, not world: every device tracks its own copy of sphereSource's
            // transform via marker tracking, so a raw world position from this device would be
            // meaningless on another.
            Vector3 localPos = sphereSource.transform.InverseTransformPoint(trackedGrabbable.transform.position);
            Quaternion localRot = Quaternion.Inverse(sphereSource.transform.rotation) * trackedGrabbable.transform.rotation;

            client.SendPinState(
                trackedGrabbable.botIndex,
                UdpSubscriptionClient.PinStatus.Held,
                -1, -1,
                localPos,
                localRot
            );
        }
        else if (wasHeld && !held)
        {
            client.SendState(false, Vector3.zero);
        }

        wasHeld = held;
    }

}