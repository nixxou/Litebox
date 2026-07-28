// Minimal glTF 2.0 binary (GLB) writer/loader — the on-disk format of the 3D-model cache. No dependency;
// ported from the speedtest3D bench (validated -62/-87/-79 % load time vs the obj set, ~2× smaller).
//
// Layout is the standard GLB container (header 12 B → JSON chunk → BIN chunk) with two LiteBox-specific
// conventions, both designed so the file is USEFUL BEFORE IT IS FULLY READ:
//   • bufferView 0 = a transparent PNG SNAPSHOT of the whole scene at the detail block's default pose,
//     placed FIRST in the BIN chunk and referenced ONLY from `extras` (no material/texture touches it, so
//     standard loaders — three.js GLTFLoader included — ignore it). Reading header + JSON + the first
//     thumbLen bytes of BIN yields a displayable image while the meshes/textures stream in behind.
//   • root `extras.litebox` = the cache identity (key, game id/platform/title, baker version, the full
//     manifest the key hashed) — the GC and the debug UI read a file's identity from the file itself.
//
// Loaded materials are DiffuseMaterial only (WPF look — glTF PBR fields are written for external viewers).
// Everything the loader returns is FROZEN, so loads can run on any background thread and the result is
// handed to the UI thread directly.

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Color = System.Windows.Media.Color;

namespace LbApiHost.Host.Model3d;

/// <summary>A baked mesh: world-space (transform-flattened) vertices + its material index.</summary>
internal sealed record BakedMesh(Point3D[] Pos, Vector3D[] Nrm, System.Windows.Point[] Uv, int[] Tri, int MaterialIndex);

/// <summary>A flattened material: solid colour w/ alpha, or a pre-rasterized PNG texture.
/// <c>DoubleSided</c> mirrors the source GeometryModel3D's BackMaterial != null — single-sided faces
/// (the jewel spine cap, the split back-insert walls) MUST stay single-sided through the GLB round
/// trip, or the reloaded cap occludes the scan strips it is supposed to be culled in front of.</summary>
internal sealed record BakedMaterial(Color Color, double Opacity, byte[]? TexturePng, bool DoubleSided = true);   // TexturePng = encoded bytes (PNG, or JPEG for opaque faces since baker v4)

/// <summary>The cache identity stored in a GLB's <c>extras.litebox</c> block.</summary>
internal sealed record GlbInfo(string Key, string GameId, string Platform, string Title, int BakerVersion, string Manifest);

internal static class GlbFile
{
    // ── writer ───────────────────────────────────────────────────────────────
    public static void Write(string path, IReadOnlyList<BakedMesh> meshes, IReadOnlyList<BakedMaterial> mats,
                             byte[] thumbPng, GlbInfo info)
    {
        var bin = new MemoryStream();
        var bufferViews = new List<string>();
        var accessors = new List<string>();
        var images = new List<string>();
        var textures = new List<string>();
        var materialsJson = new List<string>();
        var meshesJson = new List<string>();
        var nodes = new List<string>();

        int AddView(byte[] data, int? target = null)
        {
            while (bin.Length % 4 != 0) bin.WriteByte(0);
            long off = bin.Length;
            bin.Write(data, 0, data.Length);
            bufferViews.Add($"{{\"buffer\":0,\"byteOffset\":{off},\"byteLength\":{data.Length}{(target != null ? $",\"target\":{target}" : "")}}}");
            return bufferViews.Count - 1;
        }
        int AddAccessor(int view, int compType, int count, string type, double[]? min = null, double[]? max = null)
        {
            string mm = min != null && max != null
                ? $",\"min\":[{string.Join(",", Array.ConvertAll(min, v => v.ToString("R", CultureInfo.InvariantCulture)))}],\"max\":[{string.Join(",", Array.ConvertAll(max, v => v.ToString("R", CultureInfo.InvariantCulture)))}]"
                : "";
            accessors.Add($"{{\"bufferView\":{view},\"componentType\":{compType},\"count\":{count},\"type\":\"{type}\"{mm}}}");
            return accessors.Count - 1;
        }

        // The thumb MUST be bufferView 0 at offset 0 — ReadThumb relies on it to bound its read.
        AddView(thumbPng);

        for (int i = 0; i < mats.Count; i++)
        {
            var m = mats[i];
            string pbr;
            if (m.TexturePng != null)
            {
                int iv = AddView(m.TexturePng);
                string mime = mats[i].TexturePng is { Length: > 2 } tb && tb[0] == 0xFF && tb[1] == 0xD8
                    ? "image/jpeg" : "image/png";   // sniffed — opaque faces are JPEG since baker v4
                images.Add($"{{\"bufferView\":{iv},\"mimeType\":\"{mime}\"}}");
                textures.Add($"{{\"source\":{images.Count - 1}}}");
                pbr = $"\"pbrMetallicRoughness\":{{\"baseColorTexture\":{{\"index\":{textures.Count - 1}}},\"metallicFactor\":0,\"roughnessFactor\":1}}";
            }
            else
            {
                string c = string.Join(",", Array.ConvertAll(new[] { m.Color.R / 255.0, m.Color.G / 255.0, m.Color.B / 255.0, m.Opacity },
                    v => v.ToString("0.####", CultureInfo.InvariantCulture)));
                pbr = $"\"pbrMetallicRoughness\":{{\"baseColorFactor\":[{c}],\"metallicFactor\":0,\"roughnessFactor\":1}}";
            }
            string alphaMode = m.Opacity < 0.999 ? ",\"alphaMode\":\"BLEND\"" : "";
            materialsJson.Add($"{{{pbr}{alphaMode},\"doubleSided\":{(m.DoubleSided ? "true" : "false")}}}");
        }

        for (int s = 0; s < meshes.Count; s++)
        {
            var me = meshes[s];
            var posBytes = new byte[me.Pos.Length * 12];
            double[] mn = { double.MaxValue, double.MaxValue, double.MaxValue }, mx = { double.MinValue, double.MinValue, double.MinValue };
            for (int i = 0; i < me.Pos.Length; i++)
            {
                var p = me.Pos[i];
                BitConverter.GetBytes((float)p.X).CopyTo(posBytes, i * 12);
                BitConverter.GetBytes((float)p.Y).CopyTo(posBytes, i * 12 + 4);
                BitConverter.GetBytes((float)p.Z).CopyTo(posBytes, i * 12 + 8);
                mn[0] = Math.Min(mn[0], (float)p.X); mn[1] = Math.Min(mn[1], (float)p.Y); mn[2] = Math.Min(mn[2], (float)p.Z);
                mx[0] = Math.Max(mx[0], (float)p.X); mx[1] = Math.Max(mx[1], (float)p.Y); mx[2] = Math.Max(mx[2], (float)p.Z);
            }
            int posAcc = AddAccessor(AddView(posBytes, 34962), 5126, me.Pos.Length, "VEC3", mn, mx);
            string attrs = $"\"POSITION\":{posAcc}";
            if (me.Nrm.Length > 0)
            {
                var nb = new byte[me.Nrm.Length * 12];
                for (int i = 0; i < me.Nrm.Length; i++)
                {
                    BitConverter.GetBytes((float)me.Nrm[i].X).CopyTo(nb, i * 12);
                    BitConverter.GetBytes((float)me.Nrm[i].Y).CopyTo(nb, i * 12 + 4);
                    BitConverter.GetBytes((float)me.Nrm[i].Z).CopyTo(nb, i * 12 + 8);
                }
                attrs += $",\"NORMAL\":{AddAccessor(AddView(nb, 34962), 5126, me.Nrm.Length, "VEC3")}";
            }
            if (me.Uv.Length > 0)
            {
                var ub = new byte[me.Uv.Length * 8];
                for (int i = 0; i < me.Uv.Length; i++)
                {
                    BitConverter.GetBytes((float)me.Uv[i].X).CopyTo(ub, i * 8);
                    BitConverter.GetBytes((float)me.Uv[i].Y).CopyTo(ub, i * 8 + 4);
                }
                attrs += $",\"TEXCOORD_0\":{AddAccessor(AddView(ub, 34962), 5126, me.Uv.Length, "VEC2")}";
            }
            var ib = new byte[me.Tri.Length * 4];
            Buffer.BlockCopy(me.Tri, 0, ib, 0, ib.Length);
            int idxAcc = AddAccessor(AddView(ib, 34963), 5125, me.Tri.Length, "SCALAR");
            meshesJson.Add($"{{\"primitives\":[{{\"attributes\":{{{attrs}}},\"indices\":{idxAcc},\"material\":{me.MaterialIndex}}}]}}");
            nodes.Add($"{{\"mesh\":{s}}}");
        }

        string extras = "\"extras\":{\"thumb\":0,\"litebox\":" + JsonSerializer.Serialize(new
        {
            key = info.Key, gameId = info.GameId, platform = info.Platform, title = info.Title,
            baker = info.BakerVersion, manifest = info.Manifest,
        }) + "}";

        string json = "{\"asset\":{\"version\":\"2.0\",\"generator\":\"LiteBox\"}," + extras + "," +
            $"\"scene\":0,\"scenes\":[{{\"nodes\":[{string.Join(",", NodeIndices(nodes.Count))}]}}]," +
            $"\"nodes\":[{string.Join(",", nodes)}]," +
            $"\"meshes\":[{string.Join(",", meshesJson)}]," +
            $"\"materials\":[{string.Join(",", materialsJson)}]," +
            (textures.Count > 0 ? $"\"textures\":[{string.Join(",", textures)}],\"images\":[{string.Join(",", images)}]," : "") +
            $"\"accessors\":[{string.Join(",", accessors)}]," +
            $"\"bufferViews\":[{string.Join(",", bufferViews)}]," +
            $"\"buffers\":[{{\"byteLength\":{bin.Length}}}]}}";

        var jsonBytes = Encoding.UTF8.GetBytes(json);
        int jsonPad = (4 - jsonBytes.Length % 4) % 4;
        var binBytes = bin.ToArray();
        int binPad = (4 - binBytes.Length % 4) % 4;
        int total = 12 + 8 + jsonBytes.Length + jsonPad + 8 + binBytes.Length + binPad;
        // Write to a temp sibling then move: a reader never sees a half-written cache file.
        string tmp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";   // unique: parallel bakes may race the same key
        using (var f = new BinaryWriter(File.Create(tmp)))
        {
            f.Write(0x46546C67u); f.Write(2u); f.Write((uint)total);
            f.Write((uint)(jsonBytes.Length + jsonPad)); f.Write(0x4E4F534Au);
            f.Write(jsonBytes); for (int i = 0; i < jsonPad; i++) f.Write((byte)' ');
            f.Write((uint)(binBytes.Length + binPad)); f.Write(0x004E4942u);
            f.Write(binBytes); for (int i = 0; i < binPad; i++) f.Write((byte)0);
        }
        File.Move(tmp, path, overwrite: true);
    }

    private static IEnumerable<string> NodeIndices(int n) { for (int i = 0; i < n; i++) yield return i.ToString(); }

    // ── partial readers (the thumb-first payoff: neither reads the whole file) ──

    /// <summary>Read ONLY the embedded scene snapshot: header + JSON chunk + the first thumbLen bytes of
    /// BIN. Null when the file is missing/corrupt or has no thumb.</summary>
    public static byte[]? ReadThumb(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs);
            if (br.ReadUInt32() != 0x46546C67u) return null;
            br.ReadUInt32(); br.ReadUInt32();                       // version, total length
            uint jsonLen = br.ReadUInt32();
            if (br.ReadUInt32() != 0x4E4F534Au) return null;        // JSON chunk tag
            var jsonBytes = br.ReadBytes((int)jsonLen);
            using var doc = JsonDocument.Parse(jsonBytes);
            var root = doc.RootElement;
            if (!root.TryGetProperty("extras", out var ex) || !ex.TryGetProperty("thumb", out var th)) return null;
            var view = root.GetProperty("bufferViews")[th.GetInt32()];
            int off = view.TryGetProperty("byteOffset", out var bo) ? bo.GetInt32() : 0;
            int len = view.GetProperty("byteLength").GetInt32();
            br.ReadUInt32();                                        // BIN chunk length
            if (br.ReadUInt32() != 0x004E4942u) return null;        // BIN chunk tag
            fs.Seek(off, SeekOrigin.Current);
            var png = br.ReadBytes(len);
            return png.Length == len ? png : null;
        }
        catch { return null; }
    }

    /// <summary>Read a cache file's identity (extras.litebox) — header + JSON only.</summary>
    public static GlbInfo? ReadInfo(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs);
            if (br.ReadUInt32() != 0x46546C67u) return null;
            br.ReadUInt32(); br.ReadUInt32();
            uint jsonLen = br.ReadUInt32();
            if (br.ReadUInt32() != 0x4E4F534Au) return null;
            using var doc = JsonDocument.Parse(br.ReadBytes((int)jsonLen));
            if (!doc.RootElement.TryGetProperty("extras", out var ex) || !ex.TryGetProperty("litebox", out var lb)) return null;
            string S(string k) => lb.TryGetProperty(k, out var v) ? (v.GetString() ?? "") : "";
            int baker = lb.TryGetProperty("baker", out var b) ? b.GetInt32() : 0;
            return new GlbInfo(S("key"), S("gameId"), S("platform"), S("title"), baker, S("manifest"));
        }
        catch { return null; }
    }

    // ── full loader → FROZEN Model3DGroup (background-thread safe) ───────────
    public static Model3DGroup? LoadModel(string path)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            uint jsonLen = BitConverter.ToUInt32(data, 12);
            string json = Encoding.UTF8.GetString(data, 20, (int)jsonLen);
            int binOff = 20 + (int)jsonLen + 8;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var views = root.GetProperty("bufferViews");
            var accs = root.GetProperty("accessors");

            (int off, int len) View(int ix)
            {
                var v = views[ix];
                return (binOff + (v.TryGetProperty("byteOffset", out var bo) ? bo.GetInt32() : 0), v.GetProperty("byteLength").GetInt32());
            }

            var matList = new List<Material>();
            var matDouble = new List<bool>();   // per-material doubleSided (default true for pre-v10 files)
            int[] texSources = Array.Empty<int>(), imgViews = Array.Empty<int>();
            if (root.TryGetProperty("textures", out var texEl))
            {
                var tl = new List<int>(); foreach (var t in texEl.EnumerateArray()) tl.Add(t.GetProperty("source").GetInt32());
                texSources = tl.ToArray();
            }
            if (root.TryGetProperty("images", out var imgEl))
            {
                var il = new List<int>(); foreach (var im in imgEl.EnumerateArray()) il.Add(im.GetProperty("bufferView").GetInt32());
                imgViews = il.ToArray();
            }
            foreach (var m in root.GetProperty("materials").EnumerateArray())
            {
                var pbr = m.GetProperty("pbrMetallicRoughness");
                Material mat;
                if (pbr.TryGetProperty("baseColorTexture", out var bct))
                {
                    var (off, len) = View(imgViews[texSources[bct.GetProperty("index").GetInt32()]]);
                    using var ms = new MemoryStream(data, off, len, false);
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.StreamSource = ms;
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.EndInit();
                    bi.Freeze();
                    var brush = new ImageBrush(bi) { Stretch = Stretch.Fill };
                    brush.Freeze();
                    mat = new DiffuseMaterial(brush);
                }
                else
                {
                    var fc = pbr.GetProperty("baseColorFactor");
                    var brush = new SolidColorBrush(Color.FromArgb(
                        (byte)(fc[3].GetDouble() * 255), (byte)(fc[0].GetDouble() * 255),
                        (byte)(fc[1].GetDouble() * 255), (byte)(fc[2].GetDouble() * 255)));
                    brush.Freeze();
                    mat = new DiffuseMaterial(brush);
                }
                mat.Freeze();
                matList.Add(mat);
                matDouble.Add(!m.TryGetProperty("doubleSided", out var dsEl) || dsEl.GetBoolean());
            }

            float[] Floats(JsonElement acc)
            {
                var (off, len) = View(acc.GetProperty("bufferView").GetInt32());
                var res = new float[len / 4];
                Buffer.BlockCopy(data, off, res, 0, len);
                return res;
            }

            var grp = new Model3DGroup();
            foreach (var me in root.GetProperty("meshes").EnumerateArray())
            {
                var prim = me.GetProperty("primitives")[0];
                var attrs = prim.GetProperty("attributes");
                var mesh = new MeshGeometry3D();
                var pf = Floats(accs[attrs.GetProperty("POSITION").GetInt32()]);
                for (int i = 0; i + 2 < pf.Length; i += 3) mesh.Positions.Add(new Point3D(pf[i], pf[i + 1], pf[i + 2]));
                if (attrs.TryGetProperty("NORMAL", out var nEl))
                {
                    var nf = Floats(accs[nEl.GetInt32()]);
                    for (int i = 0; i + 2 < nf.Length; i += 3) mesh.Normals.Add(new Vector3D(nf[i], nf[i + 1], nf[i + 2]));
                }
                if (attrs.TryGetProperty("TEXCOORD_0", out var tEl))
                {
                    var tf = Floats(accs[tEl.GetInt32()]);
                    for (int i = 0; i + 1 < tf.Length; i += 2) mesh.TextureCoordinates.Add(new System.Windows.Point(tf[i], tf[i + 1]));
                }
                var (ioff, ilen) = View(accs[prim.GetProperty("indices").GetInt32()].GetProperty("bufferView").GetInt32());
                var idx = new int[ilen / 4];
                Buffer.BlockCopy(data, ioff, idx, 0, ilen);
                foreach (var ix in idx) mesh.TriangleIndices.Add(ix);
                mesh.Freeze();
                int matIx = prim.GetProperty("material").GetInt32();
                var mat = matList[matIx];
                // Single-sided faces stay single-sided (see BakedMaterial.DoubleSided) — a double-sided
                // reloaded spine cap occluded the scan strips 0.001 behind it (unreadable GLB spine).
                var gm = new GeometryModel3D { Geometry = mesh, Material = mat, BackMaterial = matDouble[matIx] ? mat : null };
                gm.Freeze();
                grp.Children.Add(gm);
            }
            grp.Freeze();
            return grp;
        }
        catch (Exception ex) { Console.WriteLine("[model3d] glb load failed (" + path + "): " + ex.Message); return null; }
    }
}
