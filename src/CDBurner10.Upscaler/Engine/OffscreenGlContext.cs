using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CDBurner10.Upscaler.Engine;

/// <summary>
/// Manages an offscreen Win32 WGL OpenGL 4.3+ Core context and RGBA8 FBO on the calling thread
/// for deterministic GPU shader rendering via libmpv's OpenGL render API.
/// </summary>
internal sealed unsafe class OffscreenGlContext : IDisposable
{
    private const uint WS_POPUP = 0x80000000;
    private const uint PFD_DRAW_TO_WINDOW = 0x00000004;
    private const uint PFD_SUPPORT_OPENGL = 0x00000020;
    private const uint PFD_DOUBLEBUFFER = 0x00000001;
    private const byte PFD_TYPE_RGBA = 0;
    private const byte PFD_MAIN_PLANE = 0;

    private const int WGL_CONTEXT_MAJOR_VERSION_ARB = 0x2091;
    private const int WGL_CONTEXT_MINOR_VERSION_ARB = 0x2092;
    private const int WGL_CONTEXT_PROFILE_MASK_ARB = 0x9126;
    private const int WGL_CONTEXT_CORE_PROFILE_BIT_ARB = 0x00000001;

    private const uint GL_TEXTURE_2D = 0x0DE1;
    private const uint GL_RGBA = 0x1908;
    private const uint GL_RGBA8 = 0x8058;
    private const uint GL_UNSIGNED_BYTE = 0x1401;
    private const uint GL_LINEAR = 0x2601;
    private const uint GL_CLAMP_TO_EDGE = 0x812F;
    private const uint GL_TEXTURE_MIN_FILTER = 0x2801;
    private const uint GL_TEXTURE_MAG_FILTER = 0x2800;
    private const uint GL_TEXTURE_WRAP_S = 0x2802;
    private const uint GL_TEXTURE_WRAP_T = 0x2803;
    private const uint GL_FRAMEBUFFER = 0x8D40;
    private const uint GL_COLOR_ATTACHMENT0 = 0x8CE0;
    private const uint GL_FRAMEBUFFER_COMPLETE = 0x8CD5;
    private const uint GL_VERSION = 0x1F02;
    private const uint GL_RENDERER = 0x1F01;
    private const uint GL_PACK_ALIGNMENT = 0x0D05;

    private readonly nint _hInstance;
    private readonly string _className;
    private nint _hwnd;
    private nint _hdc;
    private nint _hglrc;
    private nint _opengl32Module;

    private uint _fbo;
    private uint _texture;
    private bool _disposed;

    public int Width { get; }
    public int Height { get; }
    public int FboId => (int)_fbo;
    public string GlRendererInfo { get; private set; } = "OpenGL";

    private readonly LibMpvNative.GetProcAddressFn _mpvProcCallback;
    private nint _mpvProcCallbackPtr;

    private delegate nint WglCreateContextAttribsArbDelegate(nint hdc, nint hShareContext, int* attribList);

    private delegate void GlGenFramebuffersDelegate(int n, uint* framebuffers);

    private delegate void GlDeleteFramebuffersDelegate(int n, uint* framebuffers);

    private delegate void GlBindFramebufferDelegate(uint target, uint framebuffer);

    private delegate void GlFramebufferTexture2DDelegate(uint target, uint attachment, uint textarget, uint texture,
        int level);

    private delegate uint GlCheckFramebufferStatusDelegate(uint target);

    private GlGenFramebuffersDelegate? _glGenFramebuffers;
    private GlDeleteFramebuffersDelegate? _glDeleteFramebuffers;
    private GlBindFramebufferDelegate? _glBindFramebuffer;
    private GlFramebufferTexture2DDelegate? _glFramebufferTexture2D;
    private GlCheckFramebufferStatusDelegate? _glCheckFramebufferStatus;

    public nint MpvGetProcAddressPointer => _mpvProcCallbackPtr;

    public OffscreenGlContext(int width, int height)
    {
        Width = width;
        Height = height;

        _hInstance = GetModuleHandleW(null);
        _className = $"CDBurner10_GL_{Guid.NewGuid():N}";
        _opengl32Module = NativeLibrary.Load("opengl32.dll");

        _mpvProcCallback = GetProcAddressForMpv;
        _mpvProcCallbackPtr = Marshal.GetFunctionPointerForDelegate(_mpvProcCallback);

        InitializeWindowAndContext();
        InitializeFramebuffer();
    }

    private void InitializeWindowAndContext()
    {
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            style = 0x0020, // CS_OWNDC
            lpfnWndProc = DefWindowProcWPtr(),
            hInstance = _hInstance,
            lpszClassName = Marshal.StringToHGlobalUni(_className)
        };

        try
        {
            if (RegisterClassExW(ref wc) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterClassExW failed.");
        }
        finally
        {
            Marshal.FreeHGlobal(wc.lpszClassName);
        }

        _hwnd = CreateWindowExW(
            0, _className, "CDBurner10.Upscaler Offscreen GL",
            WS_POPUP, -32000, -32000, 64, 64,
            0, 0, _hInstance, 0);

        if (_hwnd == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowExW failed.");

        _hdc = GetDC(_hwnd);
        if (_hdc == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetDC failed.");

        var pfd = new PIXELFORMATDESCRIPTOR
        {
            nSize = (ushort)Marshal.SizeOf<PIXELFORMATDESCRIPTOR>(),
            nVersion = 1,
            dwFlags = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER,
            iPixelType = PFD_TYPE_RGBA,
            cColorBits = 32,
            cAlphaBits = 8,
            cDepthBits = 24,
            cStencilBits = 8,
            iLayerType = PFD_MAIN_PLANE
        };

        int pf = ChoosePixelFormat(_hdc, ref pfd);
        if (pf == 0 || !SetPixelFormat(_hdc, pf, ref pfd))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetPixelFormat failed.");

        nint tempCtx = wglCreateContext(_hdc);
        if (tempCtx == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "wglCreateContext failed.");

        if (!wglMakeCurrent(_hdc, tempCtx))
        {
            wglDeleteContext(tempCtx);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "wglMakeCurrent failed.");
        }

        nint createAttribsPtr = wglGetProcAddress("wglCreateContextAttribsARB");
        if (createAttribsPtr != 0)
        {
            var createAttribs =
                Marshal.GetDelegateForFunctionPointer<WglCreateContextAttribsArbDelegate>(createAttribsPtr);
            int* attribs = stackalloc int[7];
            attribs[0] = WGL_CONTEXT_MAJOR_VERSION_ARB;
            attribs[2] = WGL_CONTEXT_MINOR_VERSION_ARB;
            attribs[4] = WGL_CONTEXT_PROFILE_MASK_ARB;
            attribs[5] = WGL_CONTEXT_CORE_PROFILE_BIT_ARB;
            attribs[6] = 0;
            foreach (var (maj, min) in new[] { (4, 6), (4, 5), (4, 3), (3, 3) })
            {
                attribs[1] = maj;
                attribs[3] = min;
                nint coreCtx = createAttribs(_hdc, 0, attribs);
                if (coreCtx != 0)
                {
                    wglMakeCurrent(_hdc, coreCtx);
                    wglDeleteContext(tempCtx);
                    _hglrc = coreCtx;
                    break;
                }
            }
        }

        if (_hglrc == 0)
            _hglrc = tempCtx;

        nint verPtr = glGetString(GL_VERSION);
        nint renPtr = glGetString(GL_RENDERER);
        string ver = verPtr != 0 ? Marshal.PtrToStringUTF8(verPtr) ?? "?" : "?";
        string ren = renPtr != 0 ? Marshal.PtrToStringUTF8(renPtr) ?? "?" : "?";
        GlRendererInfo = $"{ren} (GL {ver})";
    }

    private void InitializeFramebuffer()
    {
        _glGenFramebuffers = GetProc<GlGenFramebuffersDelegate>("glGenFramebuffers");
        _glDeleteFramebuffers = GetProc<GlDeleteFramebuffersDelegate>("glDeleteFramebuffers");
        _glBindFramebuffer = GetProc<GlBindFramebufferDelegate>("glBindFramebuffer");
        _glFramebufferTexture2D = GetProc<GlFramebufferTexture2DDelegate>("glFramebufferTexture2D");
        _glCheckFramebufferStatus = GetProc<GlCheckFramebufferStatusDelegate>("glCheckFramebufferStatus");

        uint tex = 0;
        glGenTextures(1, &tex);
        _texture = tex;
        glBindTexture(GL_TEXTURE_2D, _texture);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, (int)GL_LINEAR);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, (int)GL_LINEAR);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, (int)GL_CLAMP_TO_EDGE);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, (int)GL_CLAMP_TO_EDGE);
        glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_RGBA8, Width, Height, 0, GL_RGBA, GL_UNSIGNED_BYTE, 0);
        glBindTexture(GL_TEXTURE_2D, 0);

        uint fbo = 0;
        _glGenFramebuffers(1, &fbo);
        _fbo = fbo;
        _glBindFramebuffer(GL_FRAMEBUFFER, _fbo);
        _glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, _texture, 0);

        uint status = _glCheckFramebufferStatus(GL_FRAMEBUFFER);
        if (status != GL_FRAMEBUFFER_COMPLETE)
            throw new InvalidOperationException($"OpenGL FBO incomplete (status 0x{status:X}) for {Width}x{Height}.");

        _glBindFramebuffer(GL_FRAMEBUFFER, 0);
    }

    public void MakeCurrent()
    {
        if (!wglMakeCurrent(_hdc, _hglrc))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "wglMakeCurrent failed.");
    }

    public void ReadFboRgba(byte[] destination)
    {
        if (destination.Length < Width * Height * 4)
            throw new ArgumentException("Destination buffer is smaller than RGBA frame.", nameof(destination));

        _glBindFramebuffer!(GL_FRAMEBUFFER, _fbo);
        glFinish();
        glPixelStorei(GL_PACK_ALIGNMENT, 1);
        fixed (byte* ptr = destination)
        {
            glReadPixels(0, 0, Width, Height, GL_RGBA, GL_UNSIGNED_BYTE, (nint)ptr);
        }

        _glBindFramebuffer(GL_FRAMEBUFFER, 0);
    }

    private nint GetProcAddressForMpv(nint ctx, nint namePtr)
    {
        if (namePtr == 0) return 0;
        string? name = Marshal.PtrToStringUTF8(namePtr);
        if (string.IsNullOrEmpty(name)) return 0;
        return LookupGlProc(name);
    }

    private nint LookupGlProc(string name)
    {
        nint p = wglGetProcAddress(name);
        long v = p.ToInt64();
        if (v is 0 or -1 or 1 or 2 or 3)
        {
            if (_opengl32Module != 0 && NativeLibrary.TryGetExport(_opengl32Module, name, out nint sys))
                return sys;
            return 0;
        }

        return p;
    }

    private T GetProc<T>(string name) where T : Delegate
    {
        nint p = LookupGlProc(name);
        if (p == 0)
            throw new EntryPointNotFoundException($"Required OpenGL entry point not found: {name}");
        return Marshal.GetDelegateForFunctionPointer<T>(p);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_hdc != 0 && _hglrc != 0)
            {
                wglMakeCurrent(_hdc, _hglrc);
                if (_fbo != 0 && _glDeleteFramebuffers is not null)
                {
                    uint fbo = _fbo;
                    _glDeleteFramebuffers(1, &fbo);
                    _fbo = 0;
                }

                if (_texture != 0)
                {
                    uint tex = _texture;
                    glDeleteTextures(1, &tex);
                    _texture = 0;
                }

                wglMakeCurrent(0, 0);
            }
        }
        catch
        {
        }

        if (_hglrc != 0)
        {
            try
            {
                wglDeleteContext(_hglrc);
            }
            catch
            {
            }

            _hglrc = 0;
        }

        if (_hdc != 0 && _hwnd != 0)
        {
            try
            {
                ReleaseDC(_hwnd, _hdc);
            }
            catch
            {
            }

            _hdc = 0;
        }

        if (_hwnd != 0)
        {
            try
            {
                DestroyWindow(_hwnd);
            }
            catch
            {
            }

            _hwnd = 0;
        }

        if (!string.IsNullOrEmpty(_className))
        {
            try
            {
                UnregisterClassW(_className, _hInstance);
            }
            catch
            {
            }
        }

        if (_opengl32Module != 0)
        {
            try
            {
                NativeLibrary.Free(_opengl32Module);
            }
            catch
            {
            }

            _opengl32Module = 0;
        }
    }

    private static nint DefWindowProcWPtr()
    {
        nint user32 = NativeLibrary.Load("user32.dll");
        return NativeLibrary.GetExport(user32, "DefWindowProcW");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public nint lpszMenuName;
        public nint lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PIXELFORMATDESCRIPTOR
    {
        public ushort nSize;
        public ushort nVersion;
        public uint dwFlags;
        public byte iPixelType;
        public byte cColorBits;
        public byte cRedBits;
        public byte cRedShift;
        public byte cGreenBits;
        public byte cGreenShift;
        public byte cBlueBits;
        public byte cBlueShift;
        public byte cAlphaBits;
        public byte cAlphaShift;
        public byte cAccumBits;
        public byte cAccumRedBits;
        public byte cAccumGreenBits;
        public byte cAccumBlueBits;
        public byte cAccumAlphaBits;
        public byte cDepthBits;
        public byte cStencilBits;
        public byte cAuxBuffers;
        public byte iLayerType;
        public byte bReserved;
        public uint dwLayerMask;
        public uint dwVisibleMask;
        public uint dwDamageMask;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClassW(string lpClassName, nint hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int ChoosePixelFormat(nint hdc, ref PIXELFORMATDESCRIPTOR ppfd);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool SetPixelFormat(nint hdc, int format, ref PIXELFORMATDESCRIPTOR ppfd);

    [DllImport("opengl32.dll", SetLastError = true)]
    private static extern nint wglCreateContext(nint hdc);

    [DllImport("opengl32.dll", SetLastError = true)]
    private static extern bool wglMakeCurrent(nint hdc, nint hglrc);

    [DllImport("opengl32.dll", SetLastError = true)]
    private static extern bool wglDeleteContext(nint hglrc);

    [DllImport("opengl32.dll", CharSet = CharSet.Ansi)]
    private static extern nint wglGetProcAddress(string lpszProc);

    [DllImport("opengl32.dll")]
    private static extern nint glGetString(uint name);

    [DllImport("opengl32.dll")]
    private static extern void glGenTextures(int n, uint* textures);

    [DllImport("opengl32.dll")]
    private static extern void glDeleteTextures(int n, uint* textures);

    [DllImport("opengl32.dll")]
    private static extern void glBindTexture(uint target, uint texture);

    [DllImport("opengl32.dll")]
    private static extern void glTexParameteri(uint target, uint pname, int param);

    [DllImport("opengl32.dll")]
    private static extern void glTexImage2D(uint target, int level, int internalformat, int width, int height,
        int border, uint format, uint type, nint data);

    [DllImport("opengl32.dll")]
    private static extern void glPixelStorei(uint pname, int param);

    [DllImport("opengl32.dll")]
    private static extern void glReadPixels(int x, int y, int width, int height, uint format, uint type, nint data);

    [DllImport("opengl32.dll")]
    private static extern void glFinish();
}