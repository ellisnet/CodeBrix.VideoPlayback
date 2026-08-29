using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using SkiaSharp;

namespace CodeBrix.VideoPlayback.Skia.Tests;

/// <summary>
/// Gets a real graphics context on a machine with no display, so that the graphics render path is tested
/// rather than merely compiled.
/// </summary>
/// <remarks>
/// <para>
/// Mesa offers a "surfaceless" EGL platform - a display with no window and no screen behind it - which is
/// exactly what a test runner needs. Nothing is installed to use it: the EGL library is already on any
/// machine with a graphics driver, and this class calls it through four entry points.
/// </para>
/// <para>
/// A graphics context belongs to ONE thread, and a test runner does not promise which thread a test runs on,
/// so every piece of work is marshalled onto this class's own thread through <see cref="Run" />. That is
/// slower than calling directly and it is the only arrangement that cannot go wrong.
/// </para>
/// <para>
/// When no such context can be had - no EGL, no driver, a machine that simply cannot - <see cref="IsAvailable"/>
/// is false and the tests that need one skip themselves rather than fail. What could not be arranged is said
/// out loud in <see cref="UnavailableReason" />.
/// </para>
/// </remarks>
public static class HeadlessGraphicsContext
{
    private const string Library = "libEGL.so.1";

    private const uint PlatformSurfacelessMesa = 0x31DD;
    private const uint OpenGlApi = 0x30A2;
    private const int AttributeNone = 0x3038;

    private static readonly BlockingCollection<Action> Work = new BlockingCollection<Action>();
    private static readonly object Gate = new object();

    private static bool started;
    private static GRContext context;
    private static string unavailableReason;

    /// <summary>True when a graphics context could be created on this machine.</summary>
    public static bool IsAvailable
    {
        get
        {
            Start();
            return context != null;
        }
    }

    /// <summary>Why no graphics context could be created, or null when one could.</summary>
    public static string UnavailableReason
    {
        get
        {
            Start();
            return unavailableReason;
        }
    }

    /// <summary>Runs a piece of work on the thread that owns the graphics context.</summary>
    /// <param name="action">What to do with the context.</param>
    /// <exception cref="InvalidOperationException">There is no graphics context on this machine.</exception>
    public static void Run(Action<GRContext> action)
    {
        Start();

        if (context == null)
        {
            throw new InvalidOperationException(
                "There is no headless graphics context on this machine: " + unavailableReason);
        }

        Post(() => action(context));
    }

    private static void Post(Action action)
    {
        using ManualResetEventSlim finished = new ManualResetEventSlim(false);
        Exception failure = null;

        Work.Add(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                finished.Set();
            }
        });

        finished.Wait();
        if (failure != null) throw new GraphicsWorkFailedException(failure);
    }

    private static void Start()
    {
        lock (Gate)
        {
            if (started) return;
            started = true;

            Thread thread = new Thread(Pump)
            {
                IsBackground = true,
                Name = "headless-graphics",
            };

            thread.Start();

            using ManualResetEventSlim ready = new ManualResetEventSlim(false);
            Work.Add(() =>
            {
                try
                {
                    context = CreateContext(out unavailableReason);
                }
                catch (Exception exception)
                {
                    unavailableReason = exception.GetType().Name + ": " + exception.Message;
                    context = null;
                }
                finally
                {
                    ready.Set();
                }
            });

            ready.Wait();
        }
    }

    private static void Pump()
    {
        foreach (Action action in Work.GetConsumingEnumerable()) action();
    }

    private static GRContext CreateContext(out string reason)
    {
        reason = null;

        IntPtr display = eglGetPlatformDisplay(PlatformSurfacelessMesa, IntPtr.Zero, IntPtr.Zero);
        if (display == IntPtr.Zero)
        {
            reason = "eglGetPlatformDisplay refused the surfaceless platform "
                + $"(error 0x{eglGetError():x}); this build of Mesa may not offer EGL_MESA_platform_surfaceless.";
            return null;
        }

        if (!eglInitialize(display, out int major, out int minor))
        {
            reason = $"eglInitialize failed with error 0x{eglGetError():x}.";
            return null;
        }

        if (!eglBindAPI(OpenGlApi))
        {
            reason = $"eglBindAPI(EGL_OPENGL_API) failed with error 0x{eglGetError():x}.";
            return null;
        }

        int[] configAttributes =
        {
            0x3033, 0x0001,   // EGL_SURFACE_TYPE, EGL_PBUFFER_BIT
            0x3040, 0x0008,   // EGL_RENDERABLE_TYPE, EGL_OPENGL_BIT
            0x3024, 8,        // EGL_RED_SIZE
            0x3023, 8,        // EGL_GREEN_SIZE
            0x3022, 8,        // EGL_BLUE_SIZE
            0x3021, 8,        // EGL_ALPHA_SIZE
            AttributeNone,
        };

        IntPtr[] configs = new IntPtr[1];
        if (!eglChooseConfig(display, configAttributes, configs, 1, out int found) || found < 1)
        {
            reason = $"eglChooseConfig found no usable configuration (error 0x{eglGetError():x}).";
            return null;
        }

        IntPtr glContext = eglCreateContext(
            display,
            configs[0],
            IntPtr.Zero,
            new[] { 0x3098, 3, 0x30FB, 3, AttributeNone });

        if (glContext == IntPtr.Zero)
        {
            glContext = eglCreateContext(display, configs[0], IntPtr.Zero, new[] { AttributeNone });
        }

        if (glContext == IntPtr.Zero)
        {
            reason = $"eglCreateContext failed with error 0x{eglGetError():x}.";
            return null;
        }

        if (!eglMakeCurrent(display, IntPtr.Zero, IntPtr.Zero, glContext))
        {
            reason = $"eglMakeCurrent failed with error 0x{eglGetError():x}.";
            return null;
        }

        GRGlInterface graphicsInterface = GRGlInterface.CreateOpenGl(eglGetProcAddress);
        if (graphicsInterface == null)
        {
            reason = "SkiaSharp would not assemble a GL interface from eglGetProcAddress.";
            return null;
        }

        GRContext created = GRContext.CreateGl(graphicsInterface);
        if (created == null)
        {
            reason = "SkiaSharp would not create a GRContext from the assembled GL interface.";
            return null;
        }

        _ = major;
        _ = minor;
        return created;
    }

    [DllImport(Library)]
    private static extern IntPtr eglGetProcAddress(string name);

    [DllImport(Library)]
    private static extern IntPtr eglGetPlatformDisplay(uint platform, IntPtr nativeDisplay, IntPtr attributes);

    [DllImport(Library)]
    private static extern bool eglInitialize(IntPtr display, out int major, out int minor);

    [DllImport(Library)]
    private static extern bool eglBindAPI(uint api);

    [DllImport(Library)]
    private static extern bool eglChooseConfig(
        IntPtr display,
        int[] attributes,
        IntPtr[] configs,
        int configSize,
        out int configCount);

    [DllImport(Library)]
    private static extern IntPtr eglCreateContext(IntPtr display, IntPtr config, IntPtr share, int[] attributes);

    [DllImport(Library)]
    private static extern bool eglMakeCurrent(IntPtr display, IntPtr draw, IntPtr read, IntPtr context);

    [DllImport(Library)]
    private static extern int eglGetError();
}

/// <summary>Carries a failure that happened on the graphics thread back to the test that caused it.</summary>
public sealed class GraphicsWorkFailedException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="inner">What actually went wrong, on the graphics thread.</param>
    public GraphicsWorkFailedException(Exception inner)
        : base("Work on the headless graphics thread failed: " + inner.Message, inner)
    {
    }
}
