using System.Runtime.InteropServices;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.Plugins.Frei0r;

/// <summary>
/// A hosted frei0r filter on the CPU-effect seam (PLAN.md step 59): pairs the catalog descriptor with a factory
/// for per-size <see cref="Frei0rInstance"/>s. The render pipeline's CPU stage owns one instance per pipeline
/// (preview, export, …) and recreates it when the frame size changes — exactly frei0r's
/// <c>f0r_construct(width, height)</c> model.
/// </summary>
internal sealed class Frei0rCpuEffect : ICpuVideoEffect
{
    private readonly Frei0rFunctions _functions;

    public Frei0rCpuEffect(in Frei0rFunctions functions, Frei0rPluginInfo info)
    {
        _functions = functions;
        Info = info;
        Descriptor = Frei0rParameterMapping.ToEffectDescriptor(info);
        PixelFormat = info.ColorModel == Frei0rAbi.ColorBgra8888 ? CpuPixelFormat.Bgra8888 : CpuPixelFormat.Rgba8888;
    }

    /// <inheritdoc />
    public EffectDescriptor Descriptor { get; }

    /// <inheritdoc />
    public CpuPixelFormat PixelFormat { get; }

    /// <summary>frei0r frames must be multiples of 8 in both dimensions (frei0r.h).</summary>
    public int SizeGranularity => 8;

    /// <summary>The plugin's info + parameter table.</summary>
    public Frei0rPluginInfo Info { get; }

    /// <inheritdoc />
    public ICpuVideoEffectInstance CreateInstance(int width, int height) => new Frei0rInstance(_functions, Info, width, height);
}

/// <summary>
/// One constructed frei0r filter instance for a fixed frame size. Each frame it pushes the changed parameter
/// values through <c>f0r_set_param_value</c> (only on change — some plugins rebuild tables on every set) and
/// runs <c>f0r_update(time, in, out)</c>. RAII with a finalizer (<c>f0r_destruct</c>), like the audio instance sets.
/// </summary>
internal sealed unsafe class Frei0rInstance : ICpuVideoEffectInstance
{
    private readonly Frei0rFunctions _functions;
    private readonly Frei0rPluginInfo _info;
    private readonly string[][] _paramNames;   // per parameter: its component keys
    private readonly double[][] _paramDefaults;
    private readonly double[][] _lastSet;       // last values pushed to the plugin (NaN = never)
    private readonly nint _scratch;             // native scratch for one f0r_param_t value (≥ sizeof(F0rPosition))
    private nint _instance;
    private bool _disposed;

    public Frei0rInstance(in Frei0rFunctions functions, Frei0rPluginInfo info, int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "frei0r frames need a positive size.");
        _functions = functions;
        _info = info;
        Width = width;
        Height = height;

        int n = info.Params.Count;
        _paramNames = new string[n][];
        _paramDefaults = new double[n][];
        _lastSet = new double[n][];
        for (int i = 0; i < n; i++)
        {
            EffectParameterDescriptor[] descriptors = Frei0rParameterMapping.ToParameters(info.Params[i]).ToArray();
            _paramNames[i] = descriptors.Select(d => d.Name).ToArray();
            _paramDefaults[i] = descriptors.Select(d => d.Default).ToArray();
            _lastSet[i] = Enumerable.Repeat(double.NaN, descriptors.Length).ToArray();
        }

        _scratch = (nint)NativeMemory.AllocZeroed(16);
        _instance = ((delegate* unmanaged<uint, uint, nint>)functions.Construct)((uint)width, (uint)height);
        if (_instance == nint.Zero)
        {
            NativeMemory.Free((void*)_scratch);
            throw new InvalidOperationException("f0r_construct() returned null.");
        }
    }

    ~Frei0rInstance() => FreeNative();

    public int Width { get; }
    public int Height { get; }

    /// <inheritdoc />
    public void Process(nint input, nint output, double timeSeconds, ResolvedEffect parameters)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        PushParameters(parameters);
        ((delegate* unmanaged<nint, double, uint*, uint*, void>)_functions.Update)(_instance, timeSeconds, (uint*)input, (uint*)output);
    }

    private void PushParameters(ResolvedEffect parameters)
    {
        var set = (delegate* unmanaged<nint, void*, int, void>)_functions.SetParamValue;
        for (int i = 0; i < _paramNames.Length; i++)
        {
            string[] names = _paramNames[i];
            if (names.Length == 0)
                continue; // STRING parameter: left at the plugin default

            double[] last = _lastSet[i];
            bool changed = false;
            for (int c = 0; c < names.Length; c++)
            {
                double v = parameters.Get(names[c], _paramDefaults[i][c]);
                if (v != last[c]) { last[c] = v; changed = true; }
            }
            if (!changed)
                continue;

            switch (_info.Params[i].Type)
            {
                case Frei0rAbi.ParamBool:
                    *(double*)_scratch = last[0] >= 0.5 ? 1.0 : 0.0;
                    break;
                case Frei0rAbi.ParamDouble:
                    *(double*)_scratch = Math.Clamp(last[0], 0.0, 1.0);
                    break;
                case Frei0rAbi.ParamColor:
                    *(F0rColor*)_scratch = new F0rColor
                    {
                        R = (float)Math.Clamp(last[0], 0.0, 1.0),
                        G = (float)Math.Clamp(last[1], 0.0, 1.0),
                        B = (float)Math.Clamp(last[2], 0.0, 1.0),
                    };
                    break;
                case Frei0rAbi.ParamPosition:
                    *(F0rPosition*)_scratch = new F0rPosition { X = Math.Clamp(last[0], 0.0, 1.0), Y = Math.Clamp(last[1], 0.0, 1.0) };
                    break;
                default:
                    continue;
            }
            set(_instance, (void*)_scratch, i);
        }
    }

    public void Dispose()
    {
        FreeNative();
        GC.SuppressFinalize(this);
    }

    private void FreeNative()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_instance != nint.Zero)
        {
            ((delegate* unmanaged<nint, void>)_functions.Destruct)(_instance);
            _instance = nint.Zero;
        }
        if (_scratch != nint.Zero)
            NativeMemory.Free((void*)_scratch);
    }
}
