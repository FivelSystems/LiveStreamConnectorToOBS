using System;
using System.Collections.Generic;
using System.Text;
using MeshVR;
using UnityEngine;

namespace FivelSystems.LiveStreamConnectorToOBS
{
    public static class CameraScanner
    {
        public static List<CameraInfo> Scan(Func<Camera, bool> extraFilter)
        {
            var result = new List<CameraInfo>();
            Camera[] all;
            try
            {
                all = UnityEngine.Object.FindObjectsOfType<Camera>();
            }
            catch
            {
                return result;
            }

            foreach (var cam in all)
            {
                if (cam == null) continue;
                if (!cam.gameObject.activeInHierarchy) continue;
                if (extraFilter != null && !extraFilter(cam)) continue;

                result.Add(new CameraInfo
                {
                    Camera = cam,
                    DisplayName = Describe(cam)
                });
            }
            return result;
        }

        public static Camera FindMainCamera()
        {
            var cams = Scan(c => c.name != "__SpoutHelper");
            if (cams.Count == 0) return null;

            var main = Camera.main;
            if (main != null && main.gameObject.activeInHierarchy) return main;

            Camera best = cams[0].Camera;
            int bestDepth = Mathf.RoundToInt(best.depth);
            for (int i = 1; i < cams.Count; i++)
            {
                if (cams[i].Camera.depth > best.depth)
                {
                    best = cams[i].Camera;
                    bestDepth = Mathf.RoundToInt(best.depth);
                }
            }
            return best;
        }

        private static string Describe(Camera cam)
        {
            var sb = new StringBuilder();
            sb.Append(cam.name);
            if (cam.targetTexture != null)
            {
                sb.Append(" [RT ");
                sb.Append(cam.targetTexture.width);
                sb.Append('x');
                sb.Append(cam.targetTexture.height);
                sb.Append(']');
            }
            var root = cam.transform.root;
            if (root != null)
            {
                var atom = root.GetComponent<Atom>();
                if (atom != null)
                {
                    sb.Append(" (");
                    sb.Append(atom.uid);
                    sb.Append(')');
                }
            }
            return sb.ToString();
        }
    }

    public class CameraInfo
    {
        public Camera Camera;
        public string DisplayName;
    }
}
