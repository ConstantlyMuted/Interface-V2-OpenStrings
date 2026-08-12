using UnityEngine;

/// <summary>
/// All pins are ONE shared set of objects, identical across every device — there's no
/// local/remote split. Each device just needs its own copies to mirror the same
/// status as everyone else's.
///
/// IMPORTANT: for a Set pin, this does NOT drive the pin's transform/color itself.
/// Instead it calls StringFrame3D.MirrorRemoteSnap(), which plugs the pin into the exact
/// same snappedObjects/occupied-flag bookkeeping a LOCAL snap would — so
/// StringFrame3D.UpdateSnappedObjects() (which already runs every frame) becomes the single
/// source of truth for position AND harmonicity color on every device, not just the one
/// that physically did the snapping. Two independent coloring paths racing each other frame
/// to frame was the previous cause of pin/sphere color mismatch.
///
/// For Held (no sphere assigned) this script IS the position/rotation source, converting the
/// sender's frame-local pose to this device's own world space.
///
/// The only pin this script never touches is whichever one THIS device currently has
/// grabbed — that one is driven live by hand tracking (and is the thing being sent out via
/// PIN_STATE from GrabStateSender).
/// </summary>
public class PinStateSync : MonoBehaviour
{
    [SerializeField] private UdpSubscriptionClient client;
    [SerializeField] private StringFrame3D frame;

    [Tooltip("All pins that exist on this device — the full shared set (e.g. all 5), not a subset.")]
    [SerializeField] private SnapGrabbable[] pins;

    [Header("Status Colors (only for pins with no assigned sphere — Set pins use harmonicity color)")]
    [SerializeField] private Color heldColor = new Color(1f, 0.65f, 0.1f);
    [SerializeField] private Color unavailableColor = new Color(0.35f, 0.35f, 0.35f);
    [SerializeField] private Color playingColor = new Color(0.2f, 1f, 0.4f);
    [SerializeField] private Color unsetColor = Color.white;

    [Header("Debug")]
    [SerializeField] private bool showDebugLogs = false;

    private Rigidbody[] pinRigidbodies;

    // Tracks which sphere (if any) each pin was last mirrored onto, so a transition away
    // from Set (to Held/Unset/somewhere else) or Set is properly released before applying
    // the new state — the network only tells us the CURRENT sphere, not the previous one.
    private int[] lastMirroredStringID;
    private int[] lastMirroredPartialIndex;

    private void Awake()
    {
        int n = pins != null ? pins.Length : 0;
        pinRigidbodies = new Rigidbody[n];
        lastMirroredStringID = new int[n];
        lastMirroredPartialIndex = new int[n];

        for (int i = 0; i < n; i++)
        {
            if (pins[i] != null)
                pinRigidbodies[i] = pins[i].GetComponent<Rigidbody>();
            lastMirroredStringID[i] = -1;
            lastMirroredPartialIndex[i] = -1;
        }
    }

    private void Update()
    {
        if (client == null || pins == null || frame == null)
            return;

        for (int i = 0; i < pins.Length; i++)
        {
            SnapGrabbable pin = pins[i];
            if (pin == null)
                continue;

            // Being grabbed right here, right now -> hand tracking is authoritative locally.
            if (pin.IsHeld)
                continue;

            if (!client.TryGetPinState(pin.botIndex, out UdpSubscriptionClient.PinState state))
                continue; // no network data for this bot yet

            bool treatAsSnapped = state.status == UdpSubscriptionClient.PinStatus.Set && state.HasSphere;

            if (treatAsSnapped)
            {
                ReleaseIfSphereChanged(i, state.stringID, state.partialIndex);

                frame.MirrorRemoteSnap(state.stringID, state.partialIndex, pin);
                lastMirroredStringID[i] = state.stringID;
                lastMirroredPartialIndex[i] = state.partialIndex;

                // Position/rotation/color for this frame are now handled by
                // StringFrame3D.UpdateSnappedObjects() every frame — nothing more to do here.
            }
            else
            {
                ReleaseIfSphereChanged(i, -1, -1);

                Vector3 worldPos = frame.transform.TransformPoint(state.localPosition);
                Quaternion worldRot = frame.transform.rotation * state.localRotation;

                pin.transform.SetPositionAndRotation(worldPos, worldRot);

                Rigidbody rb = pinRigidbodies[i];
                if (rb != null)
                    rb.isKinematic = true; // externally driven here, don't let physics fight the write

                ApplyNonSphereStatusColor(pin, state.status);
            }

            if (showDebugLogs)
                Debug.Log($"[PinStateSync] pin bot={pin.botIndex} status={state.status} sphere=({state.stringID},{state.partialIndex})");
        }
    }

    private void ReleaseIfSphereChanged(int pinArrayIndex, int newStringID, int newPartialIndex)
    {
        int prevStringID = lastMirroredStringID[pinArrayIndex];
        int prevPartialIndex = lastMirroredPartialIndex[pinArrayIndex];

        if (prevStringID < 0 || prevPartialIndex < 0)
            return; // wasn't mirrored onto a sphere before, nothing to release

        if (prevStringID == newStringID && prevPartialIndex == newPartialIndex)
            return; // unchanged

        frame.MirrorRemoteRelease(prevStringID, prevPartialIndex);
        lastMirroredStringID[pinArrayIndex] = -1;
        lastMirroredPartialIndex[pinArrayIndex] = -1;
    }

    private void ApplyNonSphereStatusColor(SnapGrabbable pin, UdpSubscriptionClient.PinStatus status)
    {
        Color color;
        switch (status)
        {
            case UdpSubscriptionClient.PinStatus.Held:
                color = heldColor;
                break;
            case UdpSubscriptionClient.PinStatus.Unavailable:
                color = unavailableColor;
                break;
            case UdpSubscriptionClient.PinStatus.Playing:
                color = playingColor;
                break;
            default:
                color = unsetColor;
                break;
        }

        pin.SetSnapColor(color);
    }
}