using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace JustShowMe
{
    /// Persists the allowed-face list to one file per face under a folder, so the
    /// list survives restarts. Save() wipes the folder first, so faces the user
    /// removed don't linger as stale files.
    /// ponytail: hand-rolled BinaryWriter — no serializer dependency for ~four fields.
    internal static class FaceStore
    {
        private const int Magic = 0x4A534D46; // "JSMF"

        public static void Save(string dir, IEnumerable<DetectedFace> faces)
        {
            Directory.CreateDirectory(dir);
            foreach (var old in Directory.GetFiles(dir, "*.face")) File.Delete(old);

            int i = 0;
            foreach (var face in faces)
            {
                using (var w = new BinaryWriter(File.Create(Path.Combine(dir, $"face_{i:D3}.face"))))
                {
                    w.Write(Magic);
                    w.Write(face.Name ?? "");
                    w.Write(face.Notes ?? "");
                    w.Write(face.DateAdded.Ticks);
                    w.Write(face.Snapshots.Count);
                    foreach (var s in face.Snapshots)
                    {
                        byte[] png = Encode(s.Image);
                        w.Write(png.Length); w.Write(png);
                        var emb = s.Embedding ?? new float[0];
                        w.Write(emb.Length);
                        foreach (var v in emb) w.Write(v);
                    }
                }
                i++;
            }
        }

        public static List<DetectedFace> Load(string dir)
        {
            var result = new List<DetectedFace>();
            if (!Directory.Exists(dir)) return result;

            foreach (var path in Directory.GetFiles(dir, "*.face"))
            {
                try
                {
                    using (var r = new BinaryReader(File.OpenRead(path)))
                    {
                        if (r.ReadInt32() != Magic) continue;
                        var face = new DetectedFace
                        {
                            Name = r.ReadString(),
                            Notes = r.ReadString(),
                            DateAdded = new DateTime(r.ReadInt64()),
                            LastSeen = DateTime.Now,
                            IsExistingFace = true,
                        };
                        int n = r.ReadInt32();
                        for (int k = 0; k < n; k++)
                        {
                            byte[] png = r.ReadBytes(r.ReadInt32());
                            int embLen = r.ReadInt32();
                            var emb = new float[embLen];
                            for (int j = 0; j < embLen; j++) emb[j] = r.ReadSingle();
                            face.Snapshots.Add(new FaceSnapshot
                            {
                                Image = Decode(png),
                                Embedding = embLen > 0 ? emb : null,
                            });
                        }
                        if (face.Snapshots.Count > 0)
                            face.FaceImage = face.DisplayImage = face.Snapshots[0].Image; // newest first
                        result.Add(face);
                    }
                }
                catch { /* skip a corrupt/partial file rather than fail startup */ }
            }
            return result;
        }

        private static byte[] Encode(ImageSource img)
        {
            if (!(img is BitmapSource bs)) return new byte[0];
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bs));
            using (var ms = new MemoryStream()) { enc.Save(ms); return ms.ToArray(); }
        }

        private static ImageSource Decode(byte[] png)
        {
            if (png == null || png.Length == 0) return null;
            using (var ms = new MemoryStream(png))
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad; // fully decode now so the stream can close
                bi.StreamSource = ms;
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
        }
    }
}
