// CNN embeddings via ONNX Runtime (MobileNetV3-Small features+avgpool, 1024-D, imagededup parity).
// Output is flattened then L2-normalized so cosine similarity reduces to a dot product.
//
// Native resolution: the managed Microsoft.ML.OnnxRuntime.dll P/Invokes "onnxruntime", and onnxruntime
// itself LoadLibrary's "DirectML.dll" when the DML EP is requested. Neither ships next to the exe —
// NativeInstaller deploys both (plus the model) to <LB>\ThirdParty\ImageDedup\. We preload them by full
// path (NativeLibrary.TryLoad) BEFORE the first session: an already-loaded module wins name resolution,
// so no SetDllDirectory slot is consumed (ThumbCache owns that one for Magick.Native).

#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace LbApiHost.Host.Media.Dedup;

internal sealed class CnnEmbedder : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;

    /// <summary>True when this session actually runs on the GPU (DirectML EP accepted).</summary>
    public bool GpuActive { get; }

    /// <summary>&lt;LB&gt;\ThirdParty\ImageDedup — natives + model home (deployed by NativeInstaller).</summary>
    public static string HomeDir
        => Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..")), "ThirdParty", "ImageDedup");

    public static string ModelPath => Path.Combine(HomeDir, "mobilenetv3s_embed.onnx");

    /// <summary>The CNN engine can run: model + onnxruntime native are deployed.</summary>
    public static bool IsAvailable()
        => File.Exists(ModelPath) && File.Exists(Path.Combine(HomeDir, "onnxruntime.dll"));

    private static bool _preloaded;
    private static void PreloadNatives()
    {
        if (_preloaded) return;
        _preloaded = true;
        // Full-path preload; failures are fine (IsAvailable gates callers, ctor throws → caller catches).
        NativeLibrary.TryLoad(Path.Combine(HomeDir, "DirectML.dll"), out _);
        NativeLibrary.TryLoad(Path.Combine(HomeDir, "onnxruntime.dll"), out _);
    }

    public CnnEmbedder(bool preferGpu)
    {
        PreloadNatives();
        var options = new SessionOptions();
        bool gpuOk = false;
        if (preferGpu)
        {
            try
            {
                options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL; // required by DML
                options.EnableMemoryPattern = false;                   // required by DML
                options.AppendExecutionProvider_DML(0);
                gpuOk = true;
                Console.WriteLine("[dedup] CNN: DirectML GPU acceleration active");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[dedup] CNN: GPU unavailable ({ex.Message}), CPU fallback");
                options.Dispose();
                options = new SessionOptions();
            }
        }
        GpuActive = gpuOk;
        _session = new InferenceSession(ModelPath, options);
        _inputName = _session.InputMetadata.Keys.First();
        options.Dispose();
    }

    /// <summary>L2-normalized embedding of a preprocessed CHW [3*224*224] input (DedupPreprocess.LoadCnnInput).</summary>
    public float[] Embed(float[] chw)
    {
        var tensor = new DenseTensor<float>(chw, new[] { 1, 3, 224, 224 });
        var inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) };

        using var results = _session.Run(inputs);
        float[] v = results.First().AsTensor<float>().ToArray();

        double norm = 0;
        for (int i = 0; i < v.Length; i++) norm += (double)v[i] * v[i];
        norm = Math.Sqrt(norm) + 1e-12;

        var outv = new float[v.Length];
        for (int i = 0; i < v.Length; i++) outv[i] = (float)(v[i] / norm);
        return outv;
    }

    /// <summary>Cosine similarity of two already-L2-normalized vectors = dot product.</summary>
    public static float Cosine(float[] a, float[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        double dot = 0;
        for (int i = 0; i < n; i++) dot += (double)a[i] * b[i];
        return (float)dot;
    }

    public void Dispose() => _session.Dispose();
}
