using UnityEngine;
using Oculus.Interaction;

public class SnapGrabbable : MonoBehaviour
{
    public float snapRadius = 0.20f;
    public float highlightRadius = 1f;
    public int botIndex = -1;

    public StringFrame3D sphereSource;

    private int currentSphere = -1;
    public bool IsHeld => isHeld;
    private bool isHeld = false;
    private Grabbable grabbable;
    private Rigidbody rb;

    private static SnapGrabbable activeGrab;

    private Renderer[] renderers;

    private int currentHighlightedSphere = -1;
    public int CurrentHighlightedSphere => currentHighlightedSphere;

    private void Awake()
    {
        grabbable = GetComponent<Grabbable>();
        rb = GetComponent<Rigidbody>();
        if (rb != null)
            rb.useGravity = false; // position owned by snap/grab logic, not physics

        renderers = GetComponentsInChildren<Renderer>();

        grabbable.WhenPointerEventRaised += OnPointerEvent;
    }


    private void Update()
    {
        if (currentSphere >= 0 && sphereSource != null)
        {
            sphereSource.GetSphereID(
                currentSphere,
                out int stringID,
                out int partialIndex
            );

            transform.position =
                sphereSource.GetSphereWorldPosition(
                    stringID,
                    partialIndex
                );

            sphereSource.UpdateSnappedColor(
                currentSphere,
                this
            );
        }


        if (activeGrab == this && isHeld)
            UpdateHighlights();
    }


    private void OnDestroy()
    {
        if (grabbable != null)
            grabbable.WhenPointerEventRaised -= OnPointerEvent;

        ReleaseCurrentSphere();
    }


    private void OnPointerEvent(PointerEvent evt)
    {
        if (evt.Type == PointerEventType.Select)
            GrabBegin();

        if (evt.Type == PointerEventType.Unselect)
            GrabEnd();
    }


    private void GrabBegin()
    {
        ReleaseCurrentSphere();

        activeGrab = this;
        isHeld = true;

        if (rb)
            rb.isKinematic = false;
    }


    private void GrabEnd()
    {
        isHeld = false;

        ClearHighlights();

        TrySnap();

        if (activeGrab == this)
            activeGrab = null;
    }


    private void UpdateHighlights()
    {
        ClearHighlights();

        int count = sphereSource.SphereCount();
        int closest = -1;
        float closestDistance = highlightRadius;

        for (int i = 0; i < count; i++)
        {
            sphereSource.GetSphereID(i, out int s, out int p);

            float d = Vector3.Distance(
                transform.position,
                sphereSource.GetSphereWorldPosition(s, p)
            );

            if (d < closestDistance)
            {
                closestDistance = d;
                closest = i;
            }
        }

        if (closest >= 0)
        {
            sphereSource.HighlightSphere(closest, true);
            currentHighlightedSphere = closest;
        }
    }


    private void ClearHighlights()
    {
        if (sphereSource == null)
            return;

        sphereSource.ClearSphereHighlights();
    }


    private void TrySnap()
    {
        if (sphereSource == null)
            return;

        int best = -1;
        float distance = snapRadius;

        for (int i = 0; i < sphereSource.SphereCount(); i++)
        {
            if (sphereSource.IsSphereOccupied(i))
                continue;

            sphereSource.GetSphereID(
                i,
                out int s,
                out int p
            );

            float d = Vector3.Distance(
                transform.position,
                sphereSource.GetSphereWorldPosition(s, p)
            );

            if (d < distance)
            {
                distance = d;
                best = i;
            }
        }

        if (best < 0)
            return;

        currentSphere = best;

        sphereSource.RegisterSnap(best, this);
        // Set the SphereTriggerRelay cooldown on successful snap
        sphereSource.SetSphereTriggerCooldown(best);

        if (rb)
            rb.isKinematic = true;
    }



    public void UpdateSnapPosition(Vector3 pos)
    {
        transform.position = pos;
    }

    public void SetSnapColor(Color color) { foreach (Renderer r in renderers) { if (r.material != null) r.material.color = color; } }
    public void SetTransparent(float alpha) { foreach (Renderer r in renderers) { if (r.material == null) continue; Material mat = r.material; Color c = mat.color; c.a = alpha; mat.color = c; mat.SetFloat("_Mode", 3); mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha); mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha); mat.SetInt("_ZWrite", 0); mat.renderQueue = 3000; } }

    private void ReleaseCurrentSphere()
    {
        if (currentSphere < 0)
            return;


        if (sphereSource != null)
            sphereSource.ReleaseSnap(currentSphere);


        currentSphere = -1;
    }
}