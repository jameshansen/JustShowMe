using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace JustShowMe
{
    /// Lists DirectShow video input devices by friendly name, in the same order
    /// OpenCV's DSHOW backend uses, so position i lines up with
    /// VideoCapture(i, VideoCaptureAPIs.DSHOW).
    internal static class CameraEnumerator
    {
        private static readonly Guid CLSID_SystemDeviceEnum = new Guid("62BE5D10-60EB-11d0-BD3B-00A0C911CE86");
        private static readonly Guid CLSID_VideoInputDeviceCategory = new Guid("860BB310-5D01-11d0-BD3B-00A0C911CE86");
        private static readonly Guid IID_IPropertyBag = new Guid("55272A00-42CB-11CE-8135-00AA004BB851");

        [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ICreateDevEnum
        {
            [PreserveSig]
            int CreateClassEnumerator(ref Guid pType, out IEnumMoniker ppEnumMoniker, int dwFlags);
        }

        [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyBag
        {
            [PreserveSig]
            int Read([MarshalAs(UnmanagedType.LPWStr)] string pszPropName, ref object pVar, IntPtr pErrorLog);
            [PreserveSig]
            int Write([MarshalAs(UnmanagedType.LPWStr)] string pszPropName, ref object pVar);
        }

        /// One entry per device, in index order. Entries may be null if a name
        /// couldn't be read (caller falls back to "Camera N").
        public static List<string> GetDeviceNames()
        {
            var names = new List<string>();
            var t = Type.GetTypeFromCLSID(CLSID_SystemDeviceEnum);
            if (t == null) return names;

            var devEnum = (ICreateDevEnum)Activator.CreateInstance(t);
            try
            {
                var cat = CLSID_VideoInputDeviceCategory;
                int hr = devEnum.CreateClassEnumerator(ref cat, out IEnumMoniker moniker, 0);
                if (hr != 0 || moniker == null) return names; // S_FALSE: no devices
                try
                {
                    var one = new IMoniker[1];
                    while (moniker.Next(1, one, IntPtr.Zero) == 0)
                    {
                        names.Add(ReadFriendlyName(one[0]));
                        Marshal.ReleaseComObject(one[0]);
                    }
                }
                finally { Marshal.ReleaseComObject(moniker); }
            }
            finally { Marshal.ReleaseComObject(devEnum); }
            return names;
        }

        private static string ReadFriendlyName(IMoniker m)
        {
            try
            {
                Guid iid = IID_IPropertyBag;
                m.BindToStorage(null, null, ref iid, out object bagObj);
                var bag = (IPropertyBag)bagObj;
                object val = null;
                if (bag.Read("FriendlyName", ref val, IntPtr.Zero) == 0 && val != null)
                    return val.ToString();
            }
            catch { /* fall through to null */ }
            return null;
        }
    }
}
