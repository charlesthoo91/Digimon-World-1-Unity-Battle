using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Unity.InferenceEngine;
using UnityEngine;

namespace DW1Remake.Formats
{
    /// <summary>
    /// Ours (mod AI UPSCALE MAPS): runs an image upscaling network on the player's own graphics card. The network is read from an ncnn
    /// model pair (.param = the layers, .bin = the weights), the format Upscayl installs (resources/models, e.g. remacri-4x), and rebuilt
    /// with Unity's Inference Engine. Only the layer kinds of the ESRGAN family are known: Input, Split, Convolution, ReLU (leaky),
    /// Concat, BinaryOp (add / sub / mul), Eltwise (weighted sum), activations fused into a convolution and Interp (nearest). The picture goes through in tiles with a margin, so the GPU never gets
    /// one huge job and the tiles meet without seams.
    /// </summary>
    public sealed class NcnnUpscaler : IDisposable
    {
        public const int Tile = 128, Margin = 10;
        const int N = Tile + 2 * Margin;            // the network's input side
        public int Scale { get; private set; }      // 2 per nearest x2 resize in the model (4 for the 4x models)
        public string Name { get; private set; }
        Model _model;
        Worker _worker;

        public void Dispose()
        {
            if (_worker != null) _worker.Dispose();
            _worker = null; _model = null;
        }

        // an array parameter of the .param file (key -23300 - id = "count,v0,v1,..."): element i, or def when it is not there
        static float Arr(Dictionary<int, string> p, int key, int i, float def)
        {
            string v;
            if (!p.TryGetValue(key, out v)) return def;
            var parts = v.Split(',');
            return i + 1 < parts.Length ? float.Parse(parts[i + 1], System.Globalization.CultureInfo.InvariantCulture) : def;
        }

        /// <summary>Reads &lt;path&gt;.param and &lt;path&gt;.bin; null and an error text when the model cannot be used.</summary>
        public static NcnnUpscaler Load(string path, out string error)
        {
            error = null;
            try
            {
                string[] lines = File.ReadAllLines(path + ".param");
                byte[] bin = File.ReadAllBytes(path + ".bin");
                int pos = 0, scale = 1;
                var g = new FunctionalGraph();
                var input = g.AddInput<float>(new TensorShape(1, 3, N, N), "input");
                var blobs = new Dictionary<string, FunctionalTensor>();
                FunctionalTensor last = null;
                // line 0 = magic 7767517, line 1 = layer and blob counts, then one layer a line:
                // type name inputs outputs <input names> <output names> <key=value parameters>
                for (int li = 2; li < lines.Length; li++)
                {
                    string[] t = lines[li].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (t.Length < 4) continue;
                    string type = t[0];
                    int ni = int.Parse(t[2]), no = int.Parse(t[3]);
                    var ins = new string[ni]; var outs = new string[no];
                    Array.Copy(t, 4, ins, 0, ni); Array.Copy(t, 4 + ni, outs, 0, no);
                    var p = new Dictionary<int, string>();
                    for (int k = 4 + ni + no; k < t.Length; k++)
                    {
                        int eq = t[k].IndexOf('=');
                        if (eq > 0) p[int.Parse(t[k].Substring(0, eq))] = t[k].Substring(eq + 1);
                    }
                    Func<int, int, int> pi = (key, def) => p.ContainsKey(key) ? int.Parse(p[key]) : def;
                    Func<int, float, float> pf = (key, def) => p.ContainsKey(key) ? float.Parse(p[key], System.Globalization.CultureInfo.InvariantCulture) : def;
                    FunctionalTensor o;
                    switch (type)
                    {
                        case "Input": o = input; break;
                        case "Split": foreach (var n in outs) blobs[n] = blobs[ins[0]]; continue;
                        case "Convolution":
                        {
                            int oc = pi(0, 0), kw = pi(1, 0), kh = pi(11, kw), dil = pi(2, 1), stride = pi(3, 1), pad = pi(4, 0), bias = pi(5, 0), count = pi(6, 0);
                            if (kw != kh || dil != 1 || stride != 1 || pad < 0) throw new Exception("convolution " + t[1] + " is not a plain square one");
                            int ic = count / (oc * kw * kh);
                            float[] w = ReadWeights(bin, ref pos, count);
                            FunctionalTensor b = null;
                            if (bias != 0) { var bf = new float[oc]; Buffer.BlockCopy(bin, pos, bf, 0, oc * 4); pos += oc * 4; b = Functional.Constant(new TensorShape(oc), bf); }
                            o = Functional.Conv2D(blobs[ins[0]], Functional.Constant(new TensorShape(oc, ic, kh, kw), w), b, 1, pad);
                            // an activation fused into the convolution (9 = activation_type: 1 ReLU, 2 leaky ReLU with -23310 = count, slope;
                            // Real-ESRGAN's ncnn files)
                            int act = pi(9, 0);
                            if (act == 1) o = Functional.Relu(o);
                            else if (act == 2) o = Functional.LeakyRelu(o, Arr(p, -23310, 0, 0f));
                            else if (act != 0) throw new Exception("convolution " + t[1] + ": activation " + act + " not known");
                            break;
                        }
                        case "ReLU": o = Functional.LeakyRelu(blobs[ins[0]], pf(0, 0f)); break;
                        case "Eltwise":
                        {
                            // 0 = operation (1 sum), -23301 = the inputs' coefficients (Real-ESRGAN: 0.2 x the block + 1.0 x its input)
                            if (pi(0, 1) != 1) throw new Exception("eltwise " + t[1] + " is not a sum");
                            FunctionalTensor sum = null;
                            for (int k = 0; k < ins.Length; k++)
                            {
                                float c = Arr(p, -23301, k, 1f);
                                var term = c == 1f ? blobs[ins[k]] : blobs[ins[k]] * c;
                                sum = sum == null ? term : sum + term;
                            }
                            o = sum;
                            break;
                        }
                        case "Concat":
                        {
                            if (pi(0, 0) != 0) throw new Exception("concat on another axis than the channels");
                            var parts = new FunctionalTensor[ins.Length];
                            for (int k = 0; k < ins.Length; k++) parts[k] = blobs[ins[k]];
                            o = Functional.Concat(parts, 1);
                            break;
                        }
                        case "BinaryOp":
                        {
                            int op = pi(0, 0);
                            bool withScalar = pi(1, 0) != 0;
                            var a = blobs[ins[0]];
                            if (withScalar)
                            {
                                float s = pf(2, 0f);
                                o = op == 2 ? a * s : op == 0 ? a + Functional.Constant(s) : op == 1 ? a - Functional.Constant(s) : null;
                            }
                            else
                            {
                                var bt = blobs[ins[1]];
                                o = op == 0 ? a + bt : op == 1 ? a - bt : op == 2 ? a * bt : null;
                            }
                            if (o == null) throw new Exception("binary operation " + op + " not known");
                            break;
                        }
                        case "Interp":
                        {
                            if (pi(0, 1) != 1) throw new Exception("resize " + t[1] + " is not nearest");
                            float sy = pf(1, 1f), sx = pf(2, 1f);
                            o = Functional.Interpolate(blobs[ins[0]], scaleFactor: new[] { sy, sx }, mode: "nearest");
                            scale *= Mathf.RoundToInt(sy);
                            break;
                        }
                        default: throw new Exception("layer kind " + type + " not known");
                    }
                    foreach (var n in outs) blobs[n] = o;
                    last = o;
                }
                if (pos != bin.Length) throw new Exception("the weights do not fit the layers (" + pos + " of " + bin.Length + " bytes read)");
                var up = new NcnnUpscaler { Scale = scale, Name = Path.GetFileName(path) };
                up._model = g.Compile(last);
                up._worker = new Worker(up._model, BackendType.GPUCompute);
                return up;
            }
            catch (Exception e) { error = e.Message; return null; }
        }

        // a weight block: a 4-byte tag (0 = 32-bit floats, 0x01306B47 = 16-bit floats, padded to 4 bytes), then the values
        static float[] ReadWeights(byte[] bin, ref int pos, int count)
        {
            uint tag = BitConverter.ToUInt32(bin, pos); pos += 4;
            var w = new float[count];
            if (tag == 0) { Buffer.BlockCopy(bin, pos, w, 0, count * 4); pos += count * 4; return w; }
            if (tag == 0x01306B47)
            {
                for (int i = 0; i < count; i++) w[i] = Mathf.HalfToFloat(BitConverter.ToUInt16(bin, pos + 2 * i));
                pos += (count * 2 + 3) / 4 * 4;
                return w;
            }
            throw new Exception("weights stored as 0x" + tag.ToString("X") + " (only 32 and 16-bit floats are read)");
        }

        /// <summary>Upscales a picture (w x h, rows from the top) by Scale. Spread over frames: each frame hands the graphics card network
        /// steps for up to budgetMs of the frame's time, so a fast card gets more each frame and a slow one keeps the game moving; the
        /// result of a tile is fetched without waiting (ReadbackRequest). done gets the result (w x Scale by h x Scale, rows from the
        /// top), or null when stop() said so.</summary>
        public IEnumerator Run(Color32[] src, int w, int h, float budgetMs, Func<bool> stop, Action<float> progress, Action<Color32[]> done)
        {
            int s = Scale, ow = w * s, oh = h * s, ns = N * s;
            var dst = new Color32[ow * oh];
            int tilesX = (w + Tile - 1) / Tile, tilesY = (h + Tile - 1) / Tile, made = 0;
            var data = new float[3 * N * N];
            for (int ty = 0; ty < tilesY; ty++)
                for (int tx = 0; tx < tilesX; tx++)
                {
                    // the tile and its margin; outside the picture the edge pixel repeats
                    int x0 = tx * Tile - Margin, y0 = ty * Tile - Margin;
                    for (int y = 0; y < N; y++)
                        for (int x = 0; x < N; x++)
                        {
                            Color32 c = src[Mathf.Clamp(y0 + y, 0, h - 1) * w + Mathf.Clamp(x0 + x, 0, w - 1)];
                            int i = y * N + x;
                            data[i] = c.r / 255f; data[N * N + i] = c.g / 255f; data[2 * N * N + i] = c.b / 255f;
                        }
                    using (var t = new Tensor<float>(new TensorShape(1, 3, N, N), data))
                    {
                        var it = _worker.ScheduleIterable(t);
                        bool stopping = false;
                        float start = Time.realtimeSinceStartup;
                        // a run must always go to its end: the Inference Engine (2.6.1) cannot start the next run after one left half way
                        // (its last output is then missing and DisposeOnExecute throws), so a stop only skips the waiting
                        while (it.MoveNext())
                        {
                            if (stopping || (Time.realtimeSinceStartup - start) * 1000f < budgetMs) continue;
                            yield return null;
                            stopping = stop();
                            start = Time.realtimeSinceStartup;
                        }
                        if (stopping) { done(null); yield break; }
                        var outT = _worker.PeekOutput() as Tensor<float>;
                        outT.ReadbackRequest();
                        while (!outT.IsReadbackRequestDone()) yield return null;
                        float[] r = outT.DownloadToArray();
                        // only the tile's own middle goes into the picture
                        int cx = tx * Tile * s, cy = ty * Tile * s, cw = Mathf.Min(Tile, w - tx * Tile) * s, ch = Mathf.Min(Tile, h - ty * Tile) * s;
                        for (int y = 0; y < ch; y++)
                            for (int x = 0; x < cw; x++)
                            {
                                int i = (Margin * s + y) * ns + Margin * s + x;
                                dst[(cy + y) * ow + cx + x] = new Color32(ToByte(r[i]), ToByte(r[ns * ns + i]), ToByte(r[2 * ns * ns + i]), 255);
                            }
                    }
                    made++;
                    if (progress != null) progress(made / (float)(tilesX * tilesY));
                    if (stop()) { done(null); yield break; }
                }
            done(dst);
        }

        static byte ToByte(float v) { return (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255); }
    }
}
