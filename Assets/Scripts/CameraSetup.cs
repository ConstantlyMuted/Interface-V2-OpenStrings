using Meta.XR;
using UnityEngine;

public class CameraSetup : MonoBehaviour
{
    [SerializeField] private PassthroughCameraAccess cameraAccess;

    void Start()
    {
        if (cameraAccess == null)
        {
            Debug.LogError("[CameraSetup] PassthroughCameraAccess not assigned!");
            return;
        }

        cameraAccess.CameraPosition = PassthroughCameraAccess.CameraPositionType.Left;
        cameraAccess.RequestedResolution = new Vector2Int(1280, 960);
        cameraAccess.enabled = true;
    }
}