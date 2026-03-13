using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using NesEmu.Core;
using Silk.NET.OpenGL;

namespace NesEmu.App;

public sealed class NesOpenGlControl : OpenGlControlBase
{
    private const uint PositionAttributeIndex = 0;
    private const uint TextureCoordinateAttributeIndex = 1;
    private const int VertexCount = 6;

    private static readonly float[] QuadVertices =
    [
        -1f, -1f, 0f, 1f,
         1f, -1f, 1f, 1f,
         1f,  1f, 1f, 0f,
        -1f, -1f, 0f, 1f,
         1f,  1f, 1f, 0f,
        -1f,  1f, 0f, 0f
    ];

    private readonly object _sync = new();
    private readonly uint[] _pendingFrame = new uint[NesVideoConstants.PixelsPerFrame];
    private readonly uint[] _rgbaUploadFrame = new uint[NesVideoConstants.PixelsPerFrame];

    private GL? _gl;
    private bool _frameDirty = true;
    private bool _initialized;
    private bool _textureAllocated;
    private bool _isGlesContext;
    private uint _program;
    private uint _vertexBuffer;
    private uint _vertexArrayObject;
    private uint _texture;

    public event Action<string>? RendererFailed;

    public void SubmitFrame(ReadOnlySpan<uint> frame)
    {
        lock (_sync)
        {
            frame.CopyTo(_pendingFrame);
            _frameDirty = true;
        }

        RequestRender();
    }

    public void ClearFrame()
    {
        lock (_sync)
        {
            Array.Clear(_pendingFrame);
            _frameDirty = true;
        }

        RequestRender();
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            _gl = GL.GetApi(name => gl.GetProcAddress(name));
            _isGlesContext = GlVersion.Type.ToString().Contains("ES", StringComparison.OrdinalIgnoreCase);
            _vertexBuffer = _gl.GenBuffer();
            _vertexArrayObject = TryCreateVertexArray(_gl);
            _texture = _gl.GenTexture();
            _program = CreateProgram(_gl);

            UploadVertexData(_gl);
            InitializeTexture(_gl);
            _initialized = true;
            RequestRender();
        }
        catch (Exception ex)
        {
            DisposeResources();
            NotifyRendererFailed($"OpenGL initialization failed: {ex.Message}");
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        DisposeResources();
    }

    protected override void OnOpenGlLost()
    {
        _initialized = false;
        NotifyRendererFailed("OpenGL context was lost.");
    }

    protected override unsafe void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_gl is null)
        {
            return;
        }

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
        _gl.Viewport(0, 0, (uint)GetViewportWidth(), (uint)GetViewportHeight());
        _gl.ClearColor(0f, 0f, 0f, 1f);
        _gl.Clear(ClearBufferMask.ColorBufferBit);

        if (!_initialized)
        {
            return;
        }

        UploadPendingTexture(_gl);

        _gl.UseProgram(_program);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _texture);
        if (_vertexArrayObject != 0)
        {
            _gl.BindVertexArray(_vertexArrayObject);
        }

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vertexBuffer);
        _gl.EnableVertexAttribArray(PositionAttributeIndex);
        _gl.EnableVertexAttribArray(TextureCoordinateAttributeIndex);

        var stride = 4 * sizeof(float);
        _gl.VertexAttribPointer(PositionAttributeIndex, 2, VertexAttribPointerType.Float, false, (uint)stride, null);
        _gl.VertexAttribPointer(TextureCoordinateAttributeIndex, 2, VertexAttribPointerType.Float, false, (uint)stride, (void*)(2 * sizeof(float)));
        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)VertexCount);

        if (_vertexArrayObject != 0)
        {
            _gl.BindVertexArray(0);
        }

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        _gl.UseProgram(0);
    }

    private void RequestRender()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            RequestNextFrameRendering();
            return;
        }

        Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Render);
    }

    private unsafe void UploadVertexData(GL gl)
    {
        if (_vertexArrayObject != 0)
        {
            gl.BindVertexArray(_vertexArrayObject);
        }

        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vertexBuffer);

        fixed (float* vertices = QuadVertices)
        {
            gl.BufferData(
                BufferTargetARB.ArrayBuffer,
                (nuint)(QuadVertices.Length * sizeof(float)),
                vertices,
                BufferUsageARB.StaticDraw);
        }

        var stride = 4 * sizeof(float);
        gl.EnableVertexAttribArray(PositionAttributeIndex);
        gl.EnableVertexAttribArray(TextureCoordinateAttributeIndex);
        gl.VertexAttribPointer(PositionAttributeIndex, 2, VertexAttribPointerType.Float, false, (uint)stride, null);
        gl.VertexAttribPointer(TextureCoordinateAttributeIndex, 2, VertexAttribPointerType.Float, false, (uint)stride, (void*)(2 * sizeof(float)));

        gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
        if (_vertexArrayObject != 0)
        {
            gl.BindVertexArray(0);
        }
    }

    private void InitializeTexture(GL gl)
    {
        gl.BindTexture(TextureTarget.Texture2D, _texture);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    private unsafe void UploadPendingTexture(GL gl)
    {
        bool shouldUpload;

        lock (_sync)
        {
            shouldUpload = _frameDirty;
            _frameDirty = false;
        }

        if (!shouldUpload)
        {
            return;
        }

        gl.BindTexture(TextureTarget.Texture2D, _texture);

        ConvertPendingFrameToRgba();

        fixed (uint* pixels = _rgbaUploadFrame)
        {
            if (!_textureAllocated)
            {
                gl.TexImage2D(
                    TextureTarget.Texture2D,
                    0,
                    (int)InternalFormat.Rgba,
                    (uint)NesVideoConstants.Width,
                    (uint)NesVideoConstants.Height,
                    0,
                    PixelFormat.Rgba,
                    PixelType.UnsignedByte,
                    pixels);
                _textureAllocated = true;
            }
            else
            {
                gl.TexSubImage2D(
                    TextureTarget.Texture2D,
                    0,
                    0,
                    0,
                    (uint)NesVideoConstants.Width,
                    (uint)NesVideoConstants.Height,
                    PixelFormat.Rgba,
                    PixelType.UnsignedByte,
                    pixels);
            }
        }

        gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    private uint CreateProgram(GL gl)
    {
        var vertexShaderSource = _isGlesContext
            ? """
              attribute vec2 aPosition;
              attribute vec2 aTexCoord;
              varying vec2 vTexCoord;

              void main()
              {
                  vTexCoord = aTexCoord;
                  gl_Position = vec4(aPosition, 0.0, 1.0);
              }
              """
            : """
              attribute vec2 aPosition;
              attribute vec2 aTexCoord;
              varying vec2 vTexCoord;

              void main()
              {
                  vTexCoord = aTexCoord;
                  gl_Position = vec4(aPosition, 0.0, 1.0);
              }
              """;

        var fragmentShaderSource = _isGlesContext
            ? """
              precision mediump float;
              uniform sampler2D uTexture;
              varying vec2 vTexCoord;

              void main()
              {
                  gl_FragColor = texture2D(uTexture, vTexCoord);
              }
              """
            : """
              uniform sampler2D uTexture;
              varying vec2 vTexCoord;

              void main()
              {
                  gl_FragColor = texture2D(uTexture, vTexCoord);
              }
              """;

        var vertexShader = CompileShader(gl, ShaderType.VertexShader, vertexShaderSource);
        var fragmentShader = CompileShader(gl, ShaderType.FragmentShader, fragmentShaderSource);
        var program = gl.CreateProgram();

        gl.AttachShader(program, vertexShader);
        gl.AttachShader(program, fragmentShader);
        gl.BindAttribLocation(program, PositionAttributeIndex, "aPosition");
        gl.BindAttribLocation(program, TextureCoordinateAttributeIndex, "aTexCoord");
        gl.LinkProgram(program);

        gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out var linkStatus);
        if (linkStatus == 0)
        {
            var error = gl.GetProgramInfoLog(program);
            gl.DeleteProgram(program);
            gl.DeleteShader(vertexShader);
            gl.DeleteShader(fragmentShader);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Could not link the OpenGL program." : error);
        }

        gl.DeleteShader(vertexShader);
        gl.DeleteShader(fragmentShader);

        gl.UseProgram(program);
        var samplerLocation = gl.GetUniformLocation(program, "uTexture");
        if (samplerLocation >= 0)
        {
            gl.Uniform1(samplerLocation, 0);
        }

        gl.UseProgram(0);
        return program;
    }

    private static uint CompileShader(GL gl, ShaderType shaderType, string source)
    {
        var shader = gl.CreateShader(shaderType);
        gl.ShaderSource(shader, source);
        gl.CompileShader(shader);
        gl.GetShader(shader, ShaderParameterName.CompileStatus, out var compileStatus);

        if (compileStatus != 0)
        {
            return shader;
        }

        var error = gl.GetShaderInfoLog(shader);
        gl.DeleteShader(shader);
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Could not compile an OpenGL shader." : error);
    }

    private void DisposeResources()
    {
        if (_gl is not null)
        {
            if (_texture != 0)
            {
                _gl.DeleteTexture(_texture);
                _texture = 0;
            }

            if (_vertexArrayObject != 0)
            {
                _gl.DeleteVertexArray(_vertexArrayObject);
                _vertexArrayObject = 0;
            }

            if (_vertexBuffer != 0)
            {
                _gl.DeleteBuffer(_vertexBuffer);
                _vertexBuffer = 0;
            }

            if (_program != 0)
            {
                _gl.DeleteProgram(_program);
                _program = 0;
            }

            _gl.Dispose();
            _gl = null;
        }

        _initialized = false;
        _textureAllocated = false;
    }

    private void NotifyRendererFailed(string message)
    {
        Dispatcher.UIThread.Post(() => RendererFailed?.Invoke(message), DispatcherPriority.Normal);
    }

    private int GetViewportWidth()
    {
        var renderScaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        return Math.Max(1, (int)Math.Round(Bounds.Width * renderScaling));
    }

    private int GetViewportHeight()
    {
        var renderScaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        return Math.Max(1, (int)Math.Round(Bounds.Height * renderScaling));
    }

    private void ConvertPendingFrameToRgba()
    {
        lock (_sync)
        {
            for (var i = 0; i < _pendingFrame.Length; i++)
            {
                var bgra = _pendingFrame[i];
                _rgbaUploadFrame[i] =
                    (bgra & 0xFF00FF00u)
                    | ((bgra & 0x00FF0000u) >> 16)
                    | ((bgra & 0x000000FFu) << 16);
            }
        }
    }

    private static uint TryCreateVertexArray(GL gl)
    {
        try
        {
            return gl.GenVertexArray();
        }
        catch
        {
            return 0;
        }
    }
}
