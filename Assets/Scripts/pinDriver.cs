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
        }
        else if (wasHeld && !held)
        {
            client.SendState(false, Vector3.zero);
        }

        wasHeld = held;
    }

}
