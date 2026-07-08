using UnityEngine;

namespace Capture
{
    /// <summary>
    /// Requests the Android runtime permission Passthrough Camera Access needs
    /// ("horizonos.permission.HEADSET_CAMERA"), once, at startup.
    ///
    /// This is very likely the actual reason PassthroughCameraAccess.GetTexture() never
    /// returns real camera data: PassthroughCameraAccess.OnEnable() only CHECKS
    /// Permission.HasUserAuthorizedPermission() -- confirmed by reading the installed
    /// package source (Core/Scripts/PassthroughCameraAccess.cs). It polls forever
    /// waiting for the permission to already be granted; it never shows the Android
    /// permission dialog itself. Declaring the permission in AndroidManifest.xml (which
    /// Project Setup Tool does for you) is necessary but not sufficient -- something has
    /// to call OVRPermissionsRequester.Request(...) at runtime too.
    ///
    /// Put this on any always-loaded GameObject (e.g. alongside the Camera Rig). Per
    /// Meta's own guidance, permission requests should happen from exactly one place in
    /// the app -- if OVRManager's "Permission Requests On Startup" is already checked
    /// for something else, don't also duplicate a request for this permission there.
    ///
    /// The actual grant is inherently async (Android shows a dialog, the user taps
    /// Allow/Deny) -- this script fires the request and returns immediately.
    /// PassthroughCameraAccess's own polling loop is what picks up the grant once the
    /// user responds, so nothing here needs to wait for that.
    /// </summary>
    public class PassthroughPermissionRequester : MonoBehaviour
    {
        private void Awake()
        {
            OVRPermissionsRequester.Request(new[]
            {
                OVRPermissionsRequester.Permission.PassthroughCameraAccess
            });
        }
    }
}
