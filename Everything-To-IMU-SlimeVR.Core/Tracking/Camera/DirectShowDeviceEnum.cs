using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Everything_To_IMU_SlimeVR.Tracking.Camera {
    /// <summary>
    /// Enumerates DirectShow video capture filters via the
    /// <c>ICreateDevEnum::CreateClassEnumerator(CLSID_VideoInputDeviceCategory)</c> path.
    /// Unlike WMI (PnP entities only) and Windows.Devices.Enumeration (Media Foundation),
    /// this sees DirectShow-only virtual cameras like OBS Virtual Camera, NDI Tools, ManyCam,
    /// XSplit Vcam, etc. The enumeration order matches what
    /// <c>VideoCapture(idx, VideoCaptureAPIs.DSHOW)</c> uses, so the index returned here is
    /// directly usable with OpenCvSharp's DSHOW backend.
    /// </summary>
    internal static class DirectShowDeviceEnum {
        // CLSIDs / IIDs from devguid.h / strmif.h (DirectShow SDK).
        private static readonly Guid CLSID_SystemDeviceEnum = new("62BE5D10-60EB-11D0-BD3B-00A0C911CE86");
        private static readonly Guid CLSID_VideoInputDeviceCategory = new("860BB310-5D01-11D0-BD3B-00A0C911CE86");
        private static readonly Guid IID_IPropertyBag = new("55272A00-42CB-11CE-8135-00AA004BB851");

        [ComImport]
        [Guid("29840822-5B84-11D0-BD3B-00A0C911CE86")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ICreateDevEnum {
            [PreserveSig]
            int CreateClassEnumerator(
                [In] ref Guid pType,
                [Out] out IEnumMoniker ppEnumMoniker,
                [In] int dwFlags);
        }

        [ComImport]
        [Guid("55272A00-42CB-11CE-8135-00AA004BB851")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyBag {
            [PreserveSig]
            int Read(
                [In, MarshalAs(UnmanagedType.LPWStr)] string pszPropName,
                [In, Out, MarshalAs(UnmanagedType.Struct)] ref object pVar,
                [In] IntPtr pErrorLog);

            [PreserveSig]
            int Write(
                [In, MarshalAs(UnmanagedType.LPWStr)] string pszPropName,
                [In] ref object pVar);
        }

        /// <summary>
        /// Returns the friendly names of all DirectShow video input devices in the order
        /// they appear in the filter graph (i.e. the order DSHOW indexes them). Returns an
        /// empty list on any COM failure.
        /// </summary>
        public static List<string> GetVideoInputNames() {
            var names = new List<string>();
            Type devEnumType = Type.GetTypeFromCLSID(CLSID_SystemDeviceEnum, throwOnError: false);
            if (devEnumType == null) return names;

            object devEnumObj = null;
            ICreateDevEnum devEnum = null;
            IEnumMoniker enumMoniker = null;
            try {
                devEnumObj = Activator.CreateInstance(devEnumType);
                devEnum = devEnumObj as ICreateDevEnum;
                if (devEnum == null) return names;

                Guid cat = CLSID_VideoInputDeviceCategory;
                int hr = devEnum.CreateClassEnumerator(ref cat, out enumMoniker, 0);
                // hr == 1 (S_FALSE) when the category exists but has no devices — treat as empty.
                if (hr != 0 || enumMoniker == null) return names;

                IMoniker[] monikers = new IMoniker[1];
                IntPtr fetchedPtr = Marshal.AllocCoTaskMem(sizeof(int));
                try {
                    while (enumMoniker.Next(1, monikers, fetchedPtr) == 0) {
                        IMoniker mk = monikers[0];
                        if (mk == null) continue;
                        try {
                            Guid bagId = IID_IPropertyBag;
                            mk.BindToStorage(null, null, ref bagId, out object bagObj);
                            if (bagObj is IPropertyBag bag) {
                                object val = null;
                                int hrRead = bag.Read("FriendlyName", ref val, IntPtr.Zero);
                                string friendly = hrRead == 0 && val is string s && !string.IsNullOrEmpty(s)
                                    ? s
                                    : $"Camera {names.Count}";
                                if (IsLikelyIrOrDepthOnly(friendly)) {
                                    // Surface / Dell / ThinkPad ship a colour cam + an IR cam
                                    // for Windows Hello. The IR one only emits NV12 grayscale
                                    // at 340x340 — useless for pose tracking and confusing in
                                    // the picker. Skip it.
                                    Marshal.ReleaseComObject(bag);
                                    continue;
                                }
                                names.Add(friendly);
                                Marshal.ReleaseComObject(bag);
                            } else {
                                names.Add($"Camera {names.Count}");
                            }
                        } catch {
                            names.Add($"Camera {names.Count}");
                        } finally {
                            try { Marshal.ReleaseComObject(mk); } catch { }
                            monikers[0] = null;
                        }
                    }
                } finally {
                    Marshal.FreeCoTaskMem(fetchedPtr);
                }
            } catch {
                // COM failure — caller falls back to WinRT / WMI.
            } finally {
                if (enumMoniker != null) {
                    try { Marshal.ReleaseComObject(enumMoniker); } catch { }
                }
                if (devEnum != null) {
                    try { Marshal.ReleaseComObject(devEnum); } catch { }
                } else if (devEnumObj != null) {
                    try { Marshal.ReleaseComObject(devEnumObj); } catch { }
                }
            }
            return names;
        }

        private static bool IsLikelyIrOrDepthOnly(string friendlyName) {
            if (string.IsNullOrEmpty(friendlyName)) return false;
            string n = friendlyName.ToLowerInvariant();
            // Heuristic — works for Surface / Dell / Lenovo / HP Hello cams seen in the wild.
            // We accept some false negatives (vendor that doesn't tag the name) over false
            // positives (skipping a real cam) — anything ambiguous stays in the list.
            return n.Contains("infrared")
                || n.EndsWith(" ir")
                || n.Contains(" ir ")
                || n.Contains("(ir)")
                || n.Contains("hello")
                || n.Contains("depth");
        }
    }
}
