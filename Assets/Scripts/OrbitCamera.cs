// =============================================================================
// OrbitCamera.cs
// Einfache Orbit-Steuerung um ein Target — wie der Three.js OrbitControls.
// 
// Anwendung:
//   1. An die Hauptkamera in Unity hängen
//   2. Im Inspector "Target" auf das StringFrame3D-GameObject setzen 
//      (oder leer lassen — startet bei (0, 1.2, 0))
//   3. Im Play-Mode:
//        - Linke Maustaste + Drag: drehen
//        - Mausrad: zoomen
//        - Rechte Maustaste + Drag: panen (verschieben)
//        - R-Taste: View zurücksetzen
// =============================================================================

using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Camera))]
public class OrbitCamera : MonoBehaviour
{
    [Header("Ziel & Ausgangsposition")]
    public Transform target;
    public Vector3 defaultTarget = new Vector3(0f, 1.2f, 0f);
    public Vector3 defaultPosition = new Vector3(3.5f, 2.5f, -3.5f);

    [Header("Steuerung")]
    [Range(0.1f, 5f)] public float rotateSpeed = 1.2f;
    [Range(0.1f, 5f)] public float panSpeed = 0.8f;
    [Range(0.1f, 5f)] public float zoomSpeed = 1.5f;
    public float minDistance = 0.3f;
    public float maxDistance = 30f;

    private Vector3 currentTarget;
    private float distance;
    private float yawDeg;
    private float pitchDeg;

    void Start()
    {
        ResetView();
    }

    void Update()
    {
        // Reset
        if (Keyboard.current.rKey.wasPressedThisFrame)
        {
            ResetView();
            return;
        }

        // Linke Maustaste: Rotation
        if (Mouse.current.leftButton.isPressed)
        {
            Vector2 mouseDelta = Mouse.current.delta.ReadValue();
            float dx = mouseDelta.x;
            float dy = -mouseDelta.y;
            yawDeg += dx * rotateSpeed * 90f * Time.deltaTime * 30f;
            pitchDeg += dy * rotateSpeed * 90f * Time.deltaTime * 30f;
            pitchDeg = Mathf.Clamp(pitchDeg, -89f, 89f);
        }

        // Rechte Maustaste: Panning
        if (Mouse.current.rightButton.isPressed)
        {
            Vector2 mouseDelta = Mouse.current.delta.ReadValue();
            float dx = mouseDelta.x;
            float dy = -mouseDelta.y;
            Vector3 right = transform.right;
            Vector3 up = transform.up;
            currentTarget -= right * dx * panSpeed * distance * 0.05f
                           + up * dy * panSpeed * distance * 0.05f;
        }

        // Mausrad: Zoom
        float wheel = Mouse.current.scroll.y.ReadValue() / 120f; // Normalize scroll wheel
        if (Mathf.Abs(wheel) > 0.001f)
        {
            distance -= wheel * zoomSpeed * distance;
            distance = Mathf.Clamp(distance, minDistance, maxDistance);
        }

        // Pose anwenden
        Quaternion rotation = Quaternion.Euler(pitchDeg, yawDeg, 0f);
        Vector3 offset = rotation * new Vector3(0f, 0f, -distance);
        transform.position = currentTarget + offset;
        transform.LookAt(currentTarget);
    }

    public void ResetView()
    {
        currentTarget = (target != null) ? target.position : defaultTarget;
        Vector3 offset = defaultPosition - currentTarget;
        distance = offset.magnitude;
        Vector3 dir = offset.normalized;
        yawDeg = Mathf.Atan2(dir.x, -dir.z) * Mathf.Rad2Deg;
        pitchDeg = Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) * Mathf.Rad2Deg;
    }
}
